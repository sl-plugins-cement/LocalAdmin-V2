// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Core.CommandChannel;

/// <summary>
/// Hosts a per-port named pipe that lets an external process (e.g. an SSH-side script)
/// drive a DETACHED/headless LocalAdmin. Each line a client writes is enqueued onto
/// LocalAdmin's normal input queue (so the local console, the exit/quit/stop special-case
/// and the command service all keep working). Displayed console output is sent only to
/// clients that recently submitted a command, giving callers a practical read-back window
/// without streaming the global console log to idle clients.
///
/// Wire format (both directions): UTF-8, newline-delimited (one frame per line).
/// </summary>
internal sealed class CommandChannelServer
{
    // Per-client outbound queue bound: a slow/dead reader can fall this far behind before
    // it is dropped, so a misbehaving client can never stall the console output path.
    private const int MaxQueuedLinesPerClient = 4096;

    // Bound the blast radius of a hostile/buggy client (particularly on Linux, where the pipe is
    // reachable by the current user only but not further ACL-restricted): cap simultaneous clients,
    // cap inbound line length (so a newline-less stream can't grow memory unbounded), and reap a
    // connection that sends nothing for a long time.
    private const int MaxConcurrentClients = 32;
    private const int MaxInboundLineLength = 8 * 1024;
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan OutputCaptureWindow = TimeSpan.FromSeconds(15);

    private readonly string _pipeName;
    private readonly Action<string> _enqueue;

    private readonly object _clientsLock = new();
    private readonly List<ClientConnection> _clients = new();

    private CancellationTokenSource? _cts;
    private volatile bool _running;

    internal CommandChannelServer(string baseName, ushort gamePort, Action<string> enqueue)
    {
        _pipeName = BuildPipeName(baseName, gamePort);
        _enqueue = enqueue;
    }

    internal string PipeName => _pipeName;

    /// <summary>
    /// Builds a per-port pipe name that also embeds the current user's SID. This primarily
    /// avoids cross-account name collisions (two users each running LocalAdmin on the same
    /// port). NOTE: a SID is locally discoverable, so this is NOT a strong anti-squatting
    /// defense - the real access control is the DACL applied to each instance we create (see
    /// <see cref="BuildPipeSecurity"/>), which limits connections to the current user +
    /// Administrators. A determined same-machine attacker could still pre-create the name;
    /// for hardened deployments, run LocalAdmin under a dedicated service account. The
    /// friendly "&lt;base&gt;-&lt;port&gt;" prefix is kept so the client only needs the base
    /// name and port (it reconstructs the SID from its own identity).
    /// </summary>
    internal static string BuildPipeName(string baseName, ushort gamePort)
    {
        var prefix = $"{baseName}-{gamePort}";

        if (!OperatingSystem.IsWindows())
            return prefix;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (!string.IsNullOrEmpty(sid))
                return $"{prefix}-{sid}";
        }
        catch
        {
            // fall back to the predictable name (still ACL-protected)
        }

        return prefix;
    }

    internal void Start()
    {
        if (_running)
            return;

        _running = true;
        _cts = new CancellationTokenSource();

        Task.Run(() => AcceptLoopAsync(_cts.Token));

        ConsoleUtil.WriteLine($"Command channel listening on named pipe \"{_pipeName}\".", ConsoleColor.Gray);
    }

    internal void Stop()
    {
        if (!_running)
            return;

        _running = false;

        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        ClientConnection[] snapshot;
        lock (_clientsLock)
        {
            snapshot = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var c in snapshot)
            c.Dispose();

        try { _cts?.Dispose(); }
        catch { /* ignore */ }

        _cts = null;
    }

    /// <summary>
    /// Enqueues a single console line to connected pipe clients with an active capture
    /// window. This NEVER performs blocking I/O on the caller's thread (it runs on the
    /// console-output path): each client has its own bounded queue drained by a dedicated
    /// writer task. A client whose queue overflows is dropped instead of blocking the
    /// producer.
    /// </summary>
    internal void Broadcast(string line)
    {
        if (!_running)
            return;

        ClientConnection[] snapshot;
        lock (_clientsLock)
        {
            if (_clients.Count == 0)
                return;

            snapshot = _clients.ToArray();
        }

        List<ClientConnection>? overflowed = null;
        var nowUtcTicks = DateTime.UtcNow.Ticks;
        foreach (var c in snapshot)
        {
            if (!c.CanCaptureOutput(nowUtcTicks))
                continue;

            if (!c.TryEnqueue(line))
                (overflowed ??= new List<ClientConnection>()).Add(c);
        }

        if (overflowed == null)
            return;

        // Drop clients that fell too far behind (off the producer's thread).
        lock (_clientsLock)
        {
            foreach (var c in overflowed)
                _clients.Remove(c);
        }

        foreach (var c in overflowed)
            c.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (_running && !token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipeInstance();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                // Hand the connected stream off to its own connection object; immediately
                // loop to create a fresh instance so multiple clients can connect concurrently.
                var handed = server;
                server = null;
                RegisterClient(handed, token);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                if (!_running || token.IsCancellationRequested)
                    break;

                ConsoleUtil.WriteLine($"Command channel accept error: {ex.Message}", ConsoleColor.Yellow);
                // Avoid a tight failure spin (e.g. transient ACL/handle exhaustion).
                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private NamedPipeServerStream CreatePipeInstance()
    {
        const int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances;

        if (OperatingSystem.IsWindows())
        {
            var security = BuildPipeSecurity();
            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: security);
        }

        // Non-Windows: .NET maps a named pipe to a Unix-domain socket at $TMPDIR/CoreFxPipe_<name>,
        // which is NOT per-user by default. PipeOptions.CurrentUserOnly makes the runtime restrict
        // the socket to the current user (and rejects clients running as a different user), which is
        // the closest equivalent to the Windows DACL. The client sets the same flag.
        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        // Limit access to the current user + the local Administrators group. No
        // Everyone/Authenticated-Users entry, so other local users cannot drive the server.
        var security = new PipeSecurity();

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != null)
        {
            security.AddAccessRule(new PipeAccessRule(
                identity.User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(
            admins,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return security;
    }

    private void RegisterClient(NamedPipeServerStream server, CancellationToken token)
    {
        var client = new ClientConnection(server, MaxQueuedLinesPerClient);
        var rejected = false;

        lock (_clientsLock)
        {
            if (!_running || token.IsCancellationRequested)
            {
                client.Dispose();
                return;
            }

            if (_clients.Count >= MaxConcurrentClients)
            {
                rejected = true;
            }
            else
            {
                _clients.Add(client);

                // Start under the lock so a concurrent Stop() (which snapshots+disposes _clients
                // under the same lock) cannot dispose this client's CTS before Start() reads it.
                client.Start(token, OnClientLine, RemoveClient);
            }
        }

        if (rejected)
        {
            client.Dispose();
            ConsoleUtil.WriteLine(
                $"Command channel: rejecting connection, already at the {MaxConcurrentClients}-client limit.",
                ConsoleColor.Yellow);
        }
    }

    private void OnClientLine(ClientConnection client, string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;

        client.OpenCaptureWindow(OutputCaptureWindow);
        _enqueue(line);
    }

    private void RemoveClient(ClientConnection client)
    {
        lock (_clientsLock)
            _clients.Remove(client);

        client.Dispose();
    }

    /// <summary>
    /// One connected pipe client: owns the stream, a reader pump (client -&gt; LocalAdmin),
    /// and a writer pump draining a bounded outbound queue (LocalAdmin -&gt; client).
    /// </summary>
    private sealed class ClientConnection
    {
        private readonly NamedPipeServerStream _stream;
        private readonly int _maxQueued;
        private readonly BlockingCollection<string> _outbound;
        private readonly CancellationTokenSource _cts = new();

        private long _captureUntilUtcTicks;
        private int _disposed;

        internal ClientConnection(NamedPipeServerStream stream, int maxQueued)
        {
            _stream = stream;
            _maxQueued = maxQueued;
            _outbound = new BlockingCollection<string>(new ConcurrentQueue<string>(), maxQueued);
        }

        internal void OpenCaptureWindow(TimeSpan duration)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            Volatile.Write(ref _captureUntilUtcTicks, DateTime.UtcNow.Add(duration).Ticks);
        }

        internal bool CanCaptureOutput(long nowUtcTicks)
        {
            return Volatile.Read(ref _disposed) == 0 &&
                   Volatile.Read(ref _captureUntilUtcTicks) > nowUtcTicks;
        }

        internal bool TryEnqueue(string line)
        {
            if (Volatile.Read(ref _disposed) != 0 || _outbound.IsAddingCompleted)
                return true; // already gone; treat as "no overflow" (will be reaped elsewhere)

            // Non-blocking add: false means the bounded queue is full -> caller drops us.
            try
            {
                return _outbound.TryAdd(line);
            }
            catch (InvalidOperationException)
            {
                // Adding completed / collection disposed concurrently. Note ObjectDisposedException
                // derives from InvalidOperationException, so a concurrent Dispose() is covered here.
                return true;
            }
        }

        internal void Start(CancellationToken parentToken, Action<ClientConnection, string> onLine, Action<ClientConnection> onClosed)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken, _cts.Token);
            var token = linked.Token;

            // Reader pump: client -> LocalAdmin input queue. Reads in bounded chunks (never
            // ReadLineAsync, which would buffer a newline-less stream without limit) and enforces a
            // per-line cap and an idle timeout.
            var readerTask = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                    var lineBuffer = new StringBuilder();
                    var chunk = new char[1024];

                    while (!token.IsCancellationRequested && _stream.IsConnected)
                    {
                        var read = 0;
                        using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            idleCts.CancelAfter(IdleConnectionTimeout);
                            try
                            {
                                read = await reader.ReadAsync(chunk.AsMemory(), idleCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !token.IsCancellationRequested)
                            {
                                break; // idle timeout - reap the connection
                            }
                        }

                        if (read == 0)
                            break; // client closed its write side

                        var aborted = false;
                        for (var i = 0; i < read; i++)
                        {
                            var c = chunk[i];
                            if (c == '\n')
                            {
                                var line = lineBuffer.ToString().TrimEnd('\r');
                                lineBuffer.Clear();
                                onLine(this, line);
                            }
                            else if (lineBuffer.Length >= MaxInboundLineLength)
                            {
                                ConsoleUtil.WriteLine(
                                    "Command channel: inbound line exceeded the maximum length; dropping client.",
                                    ConsoleColor.Yellow);
                                aborted = true;
                                break;
                            }
                            else
                            {
                                lineBuffer.Append(c);
                            }
                        }

                        if (aborted)
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    ConsoleUtil.WriteLine($"Command channel client read error: {ex.Message}", ConsoleColor.Yellow);
                }
                finally
                {
                    onClosed(this);
                }
            }, token);

            // Writer pump: drain the bounded outbound queue to the client.
            var writerTask = Task.Run(async () =>
            {
                try
                {
                    var writer = new StreamWriter(_stream, new UTF8Encoding(false), 1024, leaveOpen: true)
                    {
                        AutoFlush = false,
                        NewLine = "\n"
                    };

                    foreach (var line in _outbound.GetConsumingEnumerable(token))
                    {
                        if (!_stream.IsConnected)
                            break;

                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                        // Flush only when the queue has drained, to coalesce bursts.
                        if (_outbound.Count == 0)
                            await writer.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { } // GetConsumingEnumerable after CompleteAdding
                catch (Exception ex)
                {
                    ConsoleUtil.WriteLine($"Command channel client write error: {ex.Message}", ConsoleColor.Yellow);
                }
                finally
                {
                    onClosed(this);
                }
            }, token);

            _ = Task.WhenAll(readerTask, writerTask).ContinueWith(
                _ => linked.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _outbound.CompleteAdding(); } catch { /* ignore */ }
            try { _cts.Cancel(); } catch { /* ignore */ }

            try
            {
                if (_stream.IsConnected)
                    _stream.Disconnect();
            }
            catch { /* ignore */ }

            try { _stream.Dispose(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
            try { _outbound.Dispose(); } catch { /* ignore */ }
        }
    }
}
