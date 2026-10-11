using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using LocalAdmin.V2.IO;
using LocalAdmin.V2.IO.Logging;

namespace LocalAdmin.V2.Core;

public class TcpServer
{
    private enum OutputCodes : byte
    {
        //0x00 - 0x0F - reserved for colors

        RoundRestart = 0x10,
        IdleEnter = 0x11,
        IdleExit = 0x12,
        ExitActionReset = 0x13,
        ExitActionShutdown = 0x14,
        ExitActionSilentShutdown = 0x15,
        ExitActionRestart = 0x16,
        Heartbeat = 0x17
    }

    public event EventHandler<string>? Received;

    private readonly TcpListener _listener;
    private TcpClient? _client;
    private NetworkStream? _networkStream;

    internal ushort ConsolePort;
    internal bool Connected;
    private bool _exit = true;
    private int _txBuffer;
    private readonly object _lck = new();
    private readonly UTF8Encoding _encoding = new(false, true);

    public TcpServer() => _listener = new TcpListener(IPAddress.Loopback, 0);

    public void Start()
    {
        lock (_lck)
        {
            _exit = false;

            _listener.Start();
            ConsolePort = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            _listener.BeginAcceptTcpClient(result =>
            {
                lock (_lck)
                {
                    if (_exit)
                        return;
                }

                _client = _listener.EndAcceptTcpClient(result);
                _client.NoDelay = true;

                _client.ReceiveBufferSize = LocalAdmin.Configuration!.SlToLaBufferSize;
                _client.SendBufferSize = LocalAdmin.Configuration.LaToSlBufferSize;

                _txBuffer = LocalAdmin.Configuration.LaToSlBufferSize;
                var maxMessageLength = LocalAdmin.Configuration.SlToLaBufferSize;
                var bigEndianLengths = LocalAdmin.Configuration.SlToLaBigEndianLength;

                _networkStream = _client.GetStream();
                Connected = true;

                var stream = _networkStream;

                Task.Run(async () =>
                {
                    var codeBuffer = new byte[1];
                    var lengthBuffer = new byte[sizeof(int)];
                    var restartReceived = false;

                    while (true)
                    {
                        lock (_lck)
                        {
                            if (_exit)
                                break;
                        }

                        try
                        {
                            if (await stream.ReadAsync(codeBuffer.AsMemory(0, 1)) == 0)
                            {
                                // Normal on every game exit; the process watcher handles the restart.
                                Received?.Invoke(this, "7[LocalAdmin] The game closed the console connection.");
                                break;
                            }

                            if (codeBuffer[0] < 16)
                            {
                                await stream.ReadExactlyAsync(lengthBuffer);

                                var length = bigEndianLengths
                                    ? BinaryPrimitives.ReadInt32BigEndian(lengthBuffer)
                                    : BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

                                // Every later byte would be parsed out of step, so there is no safe way to resume.
                                if (length < 0 || length > maxMessageLength)
                                {
                                    ReportReadStopped(
                                        $"received a data message length of {length}, outside 0-{maxMessageLength}. If the game frames lengths with the other byte order, toggle sl_to_la_big_endian_length.");
                                    break;
                                }

                                var buffer = ArrayPool<byte>.Shared.Rent(length);
                                string text;

                                try
                                {
                                    await stream.ReadExactlyAsync(buffer.AsMemory(0, length));
                                    text = _encoding.GetString(buffer, 0, length);
                                }
                                catch (DecoderFallbackException)
                                {
                                    Received?.Invoke(this,
                                        $"4[LocalAdmin] Received a data message that is not valid UTF-8 ({length} bytes).");
                                    continue;
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                }

                                Received?.Invoke(this, $"{codeBuffer[0]:X}{text}");
                                continue;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                        {
                            lock (_lck)
                            {
                                if (_exit)
                                    break;
                            }

                            ReportReadStopped("the console connection failed: " + ex.Message);
                            break;
                        }

                        // Ignore control bytes once this TcpServer is no longer the active
                        // session's server (an in-process restart created a new session/server).
                        // Otherwise a late byte from the old connection could seed the new
                        // session's player tracker or round-restart counter.
                        if (!ReferenceEquals(LocalAdmin.Singleton?.Server, this))
                            continue;

                        if (LocalAdmin.PrintControlMessages)
                            Received?.Invoke(this, "7[LocalAdmin] Received control message: " + codeBuffer[0]);

                        switch ((OutputCodes)codeBuffer[0])
                        {
                            case OutputCodes.RoundRestart:
                                if (restartReceived)
                                    Logger.Initialize();
                                else restartReceived = true;
                                LocalAdmin.Singleton?.NotifyRoundRestart();
                                break;

                            case OutputCodes.IdleEnter:
                                LocalAdmin.Singleton?.SetIdleModeState(true);
                                LocalAdmin.Singleton?.NotifyIdleEnter();
                                break;

                            case OutputCodes.IdleExit:
                                LocalAdmin.Singleton?.SetIdleModeState(false);
                                LocalAdmin.Singleton?.NotifyIdleExit();
                                break;

                            case OutputCodes.ExitActionReset:
                                LocalAdmin.Singleton!.TryApplyGameExitAction(LocalAdmin.ShutdownAction.Crash);
                                break;

                            case OutputCodes.ExitActionShutdown:
                                LocalAdmin.Singleton!.TryApplyGameExitAction(LocalAdmin.ShutdownAction.Shutdown);
                                break;

                            case OutputCodes.ExitActionSilentShutdown:
                                LocalAdmin.Singleton!.TryApplyGameExitAction(LocalAdmin.ShutdownAction.SilentShutdown);
                                break;

                            case OutputCodes.ExitActionRestart:
                                LocalAdmin.Singleton!.TryApplyGameExitAction(LocalAdmin.ShutdownAction.Restart);
                                break;

                            case OutputCodes.Heartbeat:
                                LocalAdmin.Singleton!.HandleHeartbeat();
                                break;

                            default:
                                Received?.Invoke(this,
                                    "4[LocalAdmin] Received **INVALID** control message: " + codeBuffer[0]);
                                break;
                        }
                    }
                });
            }, _listener);
        }
    }

    private void ReportReadStopped(string reason) =>
        Received?.Invoke(this, "4[LocalAdmin] Stopped reading game output: " + reason);

    public void Stop()
    {
        lock (_lck)
        {
            if (_exit)
                return;
            _exit = true;
            Connected = false;

            _listener.Stop();
            _client?.Close();
        }
    }

    public bool WriteLine(string input)
    {
        lock (_lck)
        {
            if (_exit || !Connected || _networkStream == null)
                return false;

            const int offset = sizeof(int);

            var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(input.Length) + offset);

            try
            {
                var length = _encoding.GetBytes(input, 0, input.Length, buffer, offset);

                if (length + offset > _txBuffer)
                {
                    ConsoleUtil.WriteLine("Failed to send command - configured LA to SL buffer size is too small. Please increase it in the LocalAdmin config file to run this command!", ConsoleColor.Red);
                    return false;
                }

                MemoryMarshal.Write(buffer, length);

                _networkStream.Write(buffer, 0, length + offset);
                return true;
            }
            catch (Exception ex)
            {
                Connected = false;
                ConsoleUtil.WriteLine($"Failed to send command - TCP write failed: {ex.Message}", ConsoleColor.Yellow);
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
