using LocalAdmin.V2.Commands;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.IO;
using LocalAdmin.V2.IO.ExitHandlers;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.Commands.PluginManager;
using LocalAdmin.V2.Core.CommandChannel;
using LocalAdmin.V2.Core.Policy;
using LocalAdmin.V2.IO.Logging;
using LocalAdmin.V2.PluginsManager;

namespace LocalAdmin.V2.Core;
/*
    * Console colors:
    * Gray - LocalAdmin log
    * Red - critical error
    * DarkGray - insignificant info
    * Cyan - Header or important tip
    * Yellow - warning
    * DarkGreen - success
    * Blue - normal SCPSL log
*/

public sealed class LocalAdmin : IDisposable
{
    public const string VersionString = "2.5.17";
    private const ushort DefaultPort = 7777;

    private static readonly ConcurrentQueue<string> InputQueue = new();
    private static readonly Stopwatch RestartsStopwatch = new();
    private static string? _previousPat;
    private static bool _firstRun = true;
    private static string _gameArguments = string.Empty;
    private static bool _exit, _processRefreshFail;
    private static bool _noTrueColor;
    private static bool _stdPrint;
    private static bool _ignoreNextRestart;
    private static bool _noTerminalTitle;
    private static int _restarts = -1, _restartsLimit = 4, _restartsTimeWindow = 480; //480 seconds = 8 minutes

    internal readonly CommandService CommandService = new();
    private readonly string _scpslExecutable;
    private volatile bool _processClosing;
    private bool _idleMode;
    private uint _heartbeatSpanMaxThreshold;
    private uint _heartbeatRestartInSeconds;
    private Process? _gameProcess;
    private Task? _readerTask, _heartbeatMonitoringTask;
    private static int? _processId;

    // Enhanced fork: headless command channel + restart-when-empty watchdog.
    private static CommandChannelServer? _commandChannel;
    private readonly PlayerCountTracker _playerTracker = new();
    private CancellationTokenSource? _policyCts;
    private CancellationTokenSource? _oneShotRestartWhenEmptyCts;

    // One-shot restart intents that must survive an in-process session restart (Program.cs creates
    // a fresh LocalAdmin per session), so they are re-armed each session by RearmPendingOneShots().
    // Cleared when the intent fires or is explicitly cancelled - NOT on an unrelated restart.
    private static readonly object OneShotIntentLock = new();
    private static OneShotLowActivityIntent? _pendingOneShotLowActivity;
    private static RoundRestartIntent? _pendingRoundRestart;

    // True while NotifyRoundRestart is committing the round-restart intent (between deciding to
    // fire and consuming the intent). Cancel must fail during this window - otherwise it clears
    // the intent, reports success, and the restart happens anyway.
    private static bool _roundRestartFiring;

    // Per-session: the game emits a RoundRestart byte for the initial round too; skip that one so
    // "restart on next round restart" fires on a real round transition, not on server boot.
    private bool _sawInitialRoundRestart;

    private sealed record OneShotLowActivityIntent(
        int RestartAtOrBelowPlayers,
        int EmptyConfirmations,
        int TickSeconds,
        int RecentActivityOverPlayers,
        int RecentActivityWindowMinutes,
        int DurationSeconds);

    private sealed class RoundRestartIntent
    {
        internal RoundRestartIntent(int roundsRemaining) => RoundsRemaining = roundsRemaining;
        internal int RoundsRemaining;
    }

    internal static readonly Stopwatch HeartbeatStopwatch = new();
    internal static LocalAdmin? Singleton;
    internal static ushort GamePort;
    internal static string? ConfigPath, CurrentConfigPath, LaLogsPath, GameLogsPath;
    internal static ulong LogLengthLimit = 25000000000, LogEntriesLimit = 10000000000;
    internal static Config? Configuration;
    internal static DataJson? DataJson;
    private string BaseWindowTitle =>
        (_idleMode ? "[IDLE] " : string.Empty) +
        $"LocalAdmin v. {VersionString}" +
        (GamePort != 0 ? $" | Port: {GamePort}" : string.Empty) +
        (_processId.HasValue ? $" | PID: {_processId}" : string.Empty);
    internal static bool NoSetCursor, PrintControlMessages, AutoFlush = true, EnableLogging = true, NoPadding, DismissPluginsSecurityWarning;

    // volatile: these two form a cross-thread exit-decision protocol mutated from the reader,
    // heartbeat monitor, TCP receive loop, OS exit handlers and the restart-when-empty
    // watchdog. Volatile reads/writes give the watchdog reliable visibility of an exit claim
    // (DisableExitActionSignals) set by another thread without a shared lock.
    internal volatile ShutdownAction ExitAction = ShutdownAction.Crash;
    internal volatile bool DisableExitActionSignals;
    internal HeartbeatStatus CurrentHeartbeatStatus = HeartbeatStatus.Disabled;
    internal uint HeartbeatWarningStage;

    internal TcpServer? Server { get; private set; }
    internal bool EnableGameHeartbeat { get; private set; }

    internal enum ShutdownAction : byte
    {
        Crash,
        Shutdown,
        SilentShutdown,
        Restart
    }

    private enum CaptureArgs : byte
    {
        None,
        ArgsPassthrough,
        ConfigPath,
        LaLogsPath,
        GameLogsPath,
        RestartsLimit,
        RestartsTimeWindow,
        LogLengthLimit,
        LogEntriesLimit
    }

    internal enum HeartbeatStatus : byte
    {
        Disabled,
        AwaitingFirstHeartbeat,
        Active
    }

    internal LocalAdmin()
    {
        if (OperatingSystem.IsWindows())
            _scpslExecutable = "SCPSL.exe";
        else if (OperatingSystem.IsLinux())
            _scpslExecutable = "SCPSL.x86_64";
        else
        {
            ConsoleUtil.WriteLine("Failed - Unsupported platform! Please switch to the Windows, or Linux platform to continue.", ConsoleColor.Red);
            // shut up dotnet
            _scpslExecutable = string.Empty;
            Exit(1);
        }
    }

    internal async Task Start(string[] args)
    {
        Singleton = this;

        if (args.Length == 0)
            SetTerminalTitle();

        HeartbeatStopwatch.Reset();

        if (!PathManager.CorrectPathFound && !args.Contains("--skipHomeCheck", StringComparer.Ordinal))
        {
            ConsoleUtil.WriteLine($"Welcome to LocalAdmin version {VersionString}!", ConsoleColor.Red);
            ConsoleUtil.WriteLine("Can't obtain a valid user home directory path!", ConsoleColor.Red);
            if (OperatingSystem.IsWindows())
            {
                ConsoleUtil.WriteLine("Such error should never occur on Windows.", ConsoleColor.Red);
                ConsoleUtil.WriteLine("Open issue on the LocalAdmin GitHub repository (https://github.com/northwood-studios/LocalAdmin-V2/issues) or contact our technical support!", ConsoleColor.Red);
            }
            else if (OperatingSystem.IsLinux())
            {
                ConsoleUtil.WriteLine("Make sure to export a valid path, for example using command: export HOME=/home/username-here", ConsoleColor.Red);
                ConsoleUtil.WriteLine("You may want to add that command to the top of ~/.bashrc file and restart the terminal session to avoid having to enter that command every time.", ConsoleColor.Red);
            }
            else
            {
                ConsoleUtil.WriteLine("You are running LocalAdmin on an unsupported platform, please switch to Windows or Linux!", ConsoleColor.Red);
                throw new PlatformNotSupportedException();
            }
            ConsoleUtil.WriteLine("To skip this check, use --skipHomeCheck argument.", ConsoleColor.Red);
            Terminate();
            return;
        }

        if (_restartsLimit > -1)
        {
            if (_restartsTimeWindow > 0)
            {
                if (!RestartsStopwatch.IsRunning)
                    RestartsStopwatch.Start();
                else if (RestartsStopwatch.Elapsed.TotalSeconds > _restartsTimeWindow)
                {
                    RestartsStopwatch.Restart();
                    _restarts = 0;
                }
            }

            if (!_ignoreNextRestart)
                _restarts++;
            else _ignoreNextRestart = false;

            if (_restarts > _restartsLimit)
            {
                ConsoleUtil.WriteLine("Restarts limit exceeded.", ConsoleColor.Red);
                Terminate();
            }
        }

        try
        {
            await LoadJsonOrTerminate();

            var autoEula = false;

            if (DataJson!.EulaAccepted == null)
            {
                if (args.Contains("--acceptEULA", StringComparer.Ordinal) ||
                    Environment.GetEnvironmentVariable("ACCEPT_SCPSL_EULA")?.ToUpperInvariant() is "1" or "TRUE")
                {
                    DataJson!.EulaAccepted = DateTime.UtcNow;
                    autoEula = true;

                    await SaveJsonOrTerminate();
                }
                else
                {
                    ConsoleUtil.WriteLine($"Welcome to LocalAdmin version {VersionString}!", ConsoleColor.Cyan);
                    ConsoleUtil.WriteLine("Before starting please read and accept the SCP:SL EULA.", ConsoleColor.Cyan);
                    ConsoleUtil.WriteLine("You can find it on the following website: https://link.scpslgame.com/eula", ConsoleColor.Cyan);
                    ConsoleUtil.WriteLine("", ConsoleColor.Cyan);
                    ConsoleUtil.WriteLine("Do you accept the EULA? [yes/no]", ConsoleColor.Cyan);

                    ReadInput((input) =>
                        {
                            if (input == null)
                                return false;

                            switch (input.ToLowerInvariant())
                            {
                                case "y":
                                case "yes":
                                case "1":
                                    DataJson.EulaAccepted = DateTime.UtcNow;
                                    return true;

                                case "n":
                                case "no":
                                case "nope":
                                case "0":
                                    ConsoleUtil.WriteLine(
                                        "You have to accept the EULA to use LocalAdmin and SCP: Secret Laboratory Dedicated Server.",
                                        ConsoleColor.Red);
                                    Terminate();
                                    return true;

                                default:
                                    return false;
                            }

                        }, () => { },
                        () => { ConsoleUtil.WriteLine("Do you accept the EULA? [yes/no]", ConsoleColor.Red); });

                    if (!_exit)
                        await SaveJsonOrTerminate();
                }
            }

            var reconfigure = false;
            var useDefault = false;

            if (_firstRun)
            {
                if (args.Length == 0 || !ushort.TryParse(args[0], out GamePort))
                {
                    ConsoleUtil.WriteLine("You can pass port number as first startup argument.",
                        ConsoleColor.Green);
                    Console.WriteLine(string.Empty);
                    ConsoleUtil.Write($"Port number (default: {DefaultPort}): ", ConsoleColor.Green);

                    ReadInput((input) =>
                        {
                            if (!string.IsNullOrEmpty(input))
                                return ushort.TryParse(input, out GamePort);
                            GamePort = DefaultPort;
                            return true;

                        }, () => { },
                        () =>
                        {
                            ConsoleUtil.WriteLine("Port number must be a unsigned short integer.",
                                ConsoleColor.Red);
                        });
                }

                var capture = CaptureArgs.None;

                foreach (var arg in args)
                {
                    switch (capture)
                    {
                        case CaptureArgs.None:
                            if (arg.StartsWith("-", StringComparison.Ordinal) &&
                                !arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 1)
                            {
                                for (var i = 1; i < arg.Length; i++)
                                {
                                    switch (arg[i])
                                    {
                                        case 'c':
                                            NoSetCursor = true;
                                            break;

                                        case 'p':
                                            PrintControlMessages = true;
                                            break;

                                        case 'n':
                                            AutoFlush = false;
                                            break;

                                        case 'l':
                                            EnableLogging = false;
                                            break;

                                        case 'r':
                                            reconfigure = true;
                                            break;

                                        case 's':
                                            _stdPrint = true;
                                            break;

                                        case 'd':
                                            useDefault = true;
                                            break;

                                        case 'a':
                                            NoPadding = true;
                                            break;
                                        case 't':
                                            _noTerminalTitle = true;
                                            break;
                                    }
                                }
                            }
                            else
                                switch (arg)
                                {
                                    case "--noSetCursor":
                                        NoSetCursor = true;
                                        break;

                                    case "--printControl":
                                        PrintControlMessages = true;
                                        break;

                                    case "--noAutoFlush":
                                        AutoFlush = false;
                                        break;

                                    case "--noLogs":
                                        EnableLogging = false;
                                        break;

                                    case "--reconfigure":
                                        reconfigure = true;
                                        break;

                                    case "--printStd":
                                        _stdPrint = true;
                                        break;

                                    case "--useDefault":
                                        useDefault = true;
                                        break;

                                    case "--noAlign":
                                        NoPadding = true;
                                        break;

                                    case "--disableTrueColor":
                                        _noTrueColor = true;
                                        break;

                                    case "--dismissPluginManagerSecurityWarning":
                                        ConsoleUtil.WriteLine("Plugin manager has been enabled. USE AT YOUR OWN RISK!", ConsoleColor.Yellow);
                                        DismissPluginsSecurityWarning = true;
                                        break;

                                    case "--config":
                                        capture = CaptureArgs.ConfigPath;
                                        break;

                                    case "--logs":
                                        capture = CaptureArgs.LaLogsPath;
                                        break;

                                    case "--gameLogs":
                                        capture = CaptureArgs.GameLogsPath;
                                        break;

                                    case "--restartsLimit":
                                        capture = CaptureArgs.RestartsLimit;
                                        break;

                                    case "--restartsTimeWindow":
                                        capture = CaptureArgs.RestartsTimeWindow;
                                        break;

                                    case "--logLengthLimit":
                                        capture = CaptureArgs.LogLengthLimit;
                                        break;

                                    case "--logEntriesLimit":
                                        capture = CaptureArgs.LogEntriesLimit;
                                        break;

                                    case "--noTerminalTitle":
                                        _noTerminalTitle = true;
                                        break;

                                    case "--":
                                        capture = CaptureArgs.ArgsPassthrough;
                                        break;
                                }
                            break;

                        case CaptureArgs.ArgsPassthrough:
                            _gameArguments += $"\"{arg}\" ";
                            break;

                        case CaptureArgs.ConfigPath:
                            ConfigPath = arg;
                            capture = CaptureArgs.None;
                            break;

                        case CaptureArgs.LaLogsPath:
                            LaLogsPath = arg + Path.DirectorySeparatorChar;
                            capture = CaptureArgs.None;
                            break;

                        case CaptureArgs.GameLogsPath:
                            GameLogsPath = arg + Path.DirectorySeparatorChar;
                            capture = CaptureArgs.None;
                            break;

                        case CaptureArgs.RestartsLimit:
                            if (!int.TryParse(arg, out _restartsLimit) || _restartsLimit < -1)
                            {
                                _restartsLimit = 4;
                                ConsoleUtil.WriteLine("restartsLimit argument value must be an integer greater or equal to -1.", ConsoleColor.Red);
                            }
                            capture = CaptureArgs.None;
                            break;

                        case CaptureArgs.RestartsTimeWindow:
                            if (!int.TryParse(arg, out _restartsTimeWindow) || _restartsTimeWindow < 0)
                            {
                                _restartsTimeWindow = 480;
                                ConsoleUtil.WriteLine("restartsTimeWindow argument value must be an integer greater or equal to 0.", ConsoleColor.Red);
                            }
                            capture = CaptureArgs.None;
                            break;

                        case CaptureArgs.LogLengthLimit:
                        {
                            string a = arg.Replace("k", "000", StringComparison.Ordinal)
                                .Replace("M", "000000", StringComparison.Ordinal)
                                .Replace("G", "000000000", StringComparison.Ordinal)
                                .Replace("T", "000000000000", StringComparison.Ordinal);
                            if (!ulong.TryParse(a, out LogLengthLimit))
                            {
                                ConsoleUtil.WriteLine(
                                    "logLengthLimit argument value must be an integer greater or equal to 0.",
                                    ConsoleColor.Red);
                                LogLengthLimit = 25000000000;
                            }

                            capture = CaptureArgs.None;
                        }
                            break;

                        case CaptureArgs.LogEntriesLimit:
                        {
                            string a = arg.Replace("k", "000", StringComparison.Ordinal)
                                .Replace("M", "000000", StringComparison.Ordinal)
                                .Replace("G", "000000000", StringComparison.Ordinal)
                                .Replace("T", "000000000000", StringComparison.Ordinal);
                            if (!ulong.TryParse(a, out LogEntriesLimit))
                            {
                                ConsoleUtil.WriteLine(
                                    "logEntriesLimit argument value must be an integer greater or equal to 0.",
                                    ConsoleColor.Red);
                                LogEntriesLimit = 10000000000;
                            }

                            capture = CaptureArgs.None;
                        }
                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            if (ConfigPath != null)
            {
                CurrentConfigPath = ConfigPath;

                if (File.Exists(ConfigPath))
                    Configuration = Config.DeserializeConfig(await File.ReadAllLinesAsync(ConfigPath, Encoding.UTF8));
                else reconfigure = true;
            }
            else
            {
                CurrentConfigPath = Path.Combine(PathManager.GameUserDataRoot, "config", GamePort.ToString(), "config_localadmin.txt");
                if (File.Exists(CurrentConfigPath))
                    Configuration = Config.DeserializeConfig(await File.ReadAllLinesAsync(CurrentConfigPath, Encoding.UTF8));
                else
                {
                    CurrentConfigPath = Path.Combine(PathManager.GameUserDataRoot, "config", "config_localadmin_global.txt");
                    if (File.Exists(CurrentConfigPath))
                        Configuration = Config.DeserializeConfig(await File.ReadAllLinesAsync(CurrentConfigPath, Encoding.UTF8));
                    else
                        reconfigure = true;
                }
            }

            SetTerminalTitle();

            if (reconfigure)
                ConfigWizard.RunConfigWizard(useDefault);

            NoSetCursor |= Configuration!.LaNoSetCursor;
            DisableCursorManagementIfConsoleUnavailable();
            AutoFlush &= Configuration.LaLogAutoFlush;
            EnableLogging &= Configuration.EnableLaLogs;

            if (Configuration.EnableHeartbeat)
            {
                EnableGameHeartbeat = true;
                _heartbeatSpanMaxThreshold = Configuration.HeartbeatSpanMaxThreshold;
                _heartbeatRestartInSeconds = Configuration.HeartbeatRestartInSeconds;
                CurrentHeartbeatStatus = HeartbeatStatus.AwaitingFirstHeartbeat;
                HeartbeatStopwatch.Start();
                StartHeartbeatMonitoring();
            }

            InputQueue.Clear();

            if (_firstRun)
            {
                try
                {
                    SetupExitHandlers();
                }
                catch (Exception ex)
                {
                    ConsoleUtil.WriteLine(
                        $"Starting exit handlers threw {ex}. SCPSL Server will NOT be terminated when LocalAdmin closes!",
                        ConsoleColor.Yellow);
                }
            }

            if (_firstRun || _exit)
            {
                _exit = false;
                _firstRun = false;
                SetupKeyboardInput();
                StartCommandChannel();
            }

            RegisterCommands();
            SetupReader();

            // Fresh per-session player-count state, then arm the restart-when-empty watchdog.
            _playerTracker.Reset();
            StartRestartWhenEmptyPolicy();

            // Re-arm any one-shot restart intents carried over from a previous session.
            RearmPendingOneShots();

            StartSession();

            _readerTask!.Start();

            if (autoEula)
                ConsoleUtil.WriteLine("SCP: Secret Laboratory EULA (https://link.scpslgame.com/eula) was accepted by providing a startup argument or setting an environment variable.", ConsoleColor.Yellow);

            if (!EnableLogging)
                ConsoleUtil.WriteLine("Logging has been disabled.", ConsoleColor.Red);
            else if (!AutoFlush)
                ConsoleUtil.WriteLine("Logs Auto-Flush has been disabled.", ConsoleColor.Yellow);

            if (PrintControlMessages)
                ConsoleUtil.WriteLine("Printing control messages been enabled using startup argument.", ConsoleColor.Gray);

            if (NoSetCursor)
                ConsoleUtil.WriteLine("Cursor management has been disabled.", ConsoleColor.Gray);

            if (Configuration.LaDeleteOldLogs || Configuration.DeleteOldRoundLogs || Configuration.CompressOldRoundLogs)
                LogCleaner.Initialize();

            while (!_exit)
                Thread.Sleep(250);

            // If the game was terminated intentionally, then wait, otherwise no
            Exit(0, true); // After the readerTask is completed this will happen
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync($"LocalAdmin Crash {DateTime.UtcNow:yyyy-MM-ddTHH-mm-ssZ}.txt", ex.ToString());

            Logger.Log("|===| Exception |===|");
            Logger.Log(ex);
            Logger.Log("|===================|");
            Logger.Log("");
        }
    }

    /// <summary>
    /// Starts a session,
    /// if the session has already begun,
    /// then terminates it.
    /// </summary>
    private void StartSession()
    {
        // Terminate the game, if the game process is exists
        if (_gameProcess is { HasExited: false })
            TerminateGame();

        Menu();
        SetTerminalTitle();
        Logger.Initialize();

        ConsoleUtil.WriteLine($"Started new session on port {GamePort}.", ConsoleColor.DarkGreen);
        ConsoleUtil.WriteLine("Trying to start server...", ConsoleColor.Gray);

        SetupServer();

        while (Server!.ConsolePort == 0)
            Thread.Sleep(200);

        RunScpsl();
    }

    private static void Menu()
    {
        ConsoleUtil.Clear();
        ConsoleUtil.WriteLine($"SCP: Secret Laboratory - LocalAdmin v. {VersionString}", ConsoleColor.Cyan);
        ConsoleUtil.WriteLine(string.Empty, ConsoleColor.Cyan);
        ConsoleUtil.WriteLine("Licensed under The MIT License (use command \"license\" to get license text).", ConsoleColor.Cyan);
        ConsoleUtil.WriteLine("Copyright by Łukasz \"zabszk\" Jurczyk and KernelError, 2019 - 2026", ConsoleColor.Cyan);
        ConsoleUtil.WriteLine(string.Empty, ConsoleColor.Cyan);
        ConsoleUtil.WriteLine("Type 'help' to get list of available commands.", ConsoleColor.Cyan);
        ConsoleUtil.WriteLine(string.Empty, ConsoleColor.Cyan);
    }

    private static void SetupExitHandlers()
    {
        ProcessHandler.Handler.Setup();
        AppDomainHandler.Handler.Setup();

        if (OperatingSystem.IsWindows())
            WindowsHandler.Handler.Setup();
        else if (OperatingSystem.IsLinux())
        {
#if LINUX_SIGNALS
                try
                {
                    UnixHandler.Handler.Setup();
                }
                catch (DllNotFoundException ex)
                {
                    if (!CheckMonoException(ex)) throw;
                }
                catch (EntryPointNotFoundException ex)
                {
                    if (!CheckMonoException(ex)) throw;
                }
                catch (TypeInitializationException ex)
                {
                    switch (ex.InnerException)
                    {
                        case DllNotFoundException dll:
                            if (!CheckMonoException(dll)) throw;
                            break;
                        case EntryPointNotFoundException dll:
                            if (!CheckMonoException(dll)) throw;
                            break;
                        default:
                            throw;
                    }
                }
#else
            ConsoleUtil.WriteLine("Invalid Linux build! Please download LocalAdmin from GitHub here: https://github.com/northwood-studios/LocalAdmin-V2/releases", ConsoleColor.Red);
#endif
        }
    }

#if LINUX_SIGNALS
        private static bool CheckMonoException(Exception ex)
        {
            if (!ex.Message.Contains("MonoPosixHelper")) return false;
            ConsoleUtil.WriteLine("Native exit handling for Linux requires Mono to be installed!", ConsoleColor.Yellow);
            return true;
        }
#endif

    private void SetupServer()
    {
        Server = new TcpServer();
        Server.Received += (_, line) =>
        {
            if (!byte.TryParse(line.AsSpan(0, 1), NumberStyles.HexNumber, null, out var colorValue))
                colorValue = (byte)ConsoleColor.Gray;

            var stripped = line[1..];
            ConsoleUtil.WriteLine(stripped, (ConsoleColor)colorValue);

            // Fan out: feed the restart-when-empty tracker and the command-channel read-back.
            OnConsoleLine(stripped);
        };
        Server.Start();
    }

    private static void SetupKeyboardInput()
    {
        new Task(() =>
        {
            while (!_exit)
            {
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                InputQueue.Enqueue(input);
            }
        }).Start();
    }

    private static void StartCommandChannel()
    {
        if (Configuration is not { CommandChannelEnabled: true })
        {
            StopCommandChannel();
            return;
        }

        if (_commandChannel != null)
        {
            var configuredPipeName = CommandChannelServer.BuildPipeName(Configuration.CommandChannelPipeName, GamePort);
            if (string.Equals(_commandChannel.PipeName, configuredPipeName, StringComparison.Ordinal))
            {
                ConsoleUtil.OutputSink = line => _commandChannel?.Broadcast(line);
                return;
            }

            StopCommandChannel();
        }

        try
        {
            _commandChannel = new CommandChannelServer(
                Configuration.CommandChannelPipeName,
                GamePort,
                line => InputQueue.Enqueue(line));
            _commandChannel.Start();

            // Mirror displayed console output to connected pipe clients for read-back.
            ConsoleUtil.OutputSink = line => _commandChannel?.Broadcast(line);
        }
        catch (Exception ex)
        {
            ConsoleUtil.WriteLine($"Failed to start command channel: {ex.Message}. Continuing without it.", ConsoleColor.Yellow);
            _commandChannel?.Stop();
            _commandChannel = null;
            ConsoleUtil.OutputSink = null;
        }
    }

    private static void StopCommandChannel()
    {
        ConsoleUtil.OutputSink = null;
        _commandChannel?.Stop();
        _commandChannel = null;
    }

    private void StartRestartWhenEmptyPolicy()
    {
        if (Configuration is not { AutoRestartEmptyEnabled: true })
            return;

        // Cancel any watchdog left over from a previous session and start a fresh one bound
        // to this session's token, so a stale watchdog can never act on the new session.
        try { _policyCts?.Cancel(); }
        catch { /* ignore */ }
        _policyCts?.Dispose();
        _policyCts = new CancellationTokenSource();

        try
        {
            var policy = new RestartWhenEmptyPolicy(this, _playerTracker, Configuration);
            policy.Start(_policyCts.Token);
        }
        catch (Exception ex)
        {
            ConsoleUtil.WriteLine($"Failed to start restart-when-empty watchdog: {ex.Message}.", ConsoleColor.Yellow);
        }
    }

    private void SetupReader()
    {
        async void ReaderTaskMethod()
        {
            while (Server == null) await Task.Delay(20);

            while (!_exit)
            {
                if (!InputQueue.TryDequeue(out var input))
                {
                    await Task.Delay(65);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                EchoInput(input);

                if (!_processRefreshFail && _gameProcess != null)
                {
                    try
                    {
                        if (_gameProcess.HasExited)
                        {
                            ConsoleUtil.WriteLine("Failed to send command - the game process was terminated...", ConsoleColor.Red);
                            _exit = true;
                            continue;
                        }
                    }
                    catch
                    {
                        _processRefreshFail = true;
                    }
                }

                var split = input.Split(' ');

                if (split.Length == 0) continue;
                var name = split[0].ToUpperInvariant();

                var command = CommandService.GetCommandByName(name);

                if (command != null)
                {
                    command.Execute(split.Skip(1).ToArray());
                    if (!command.SendToGame) continue;
                }

                var exit = false;

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.StartsWith("exit ", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase) || input.StartsWith("quit ", StringComparison.OrdinalIgnoreCase) || input.Equals("stop", StringComparison.OrdinalIgnoreCase) || input.StartsWith("stop ", StringComparison.OrdinalIgnoreCase))
                {
                    ClaimExitAction(ShutdownAction.SilentShutdown);
                    exit = true;
                }

                if (Server.Connected)
                {
                    // Correlate a manually typed "players" command with its list reply so the
                    // tracker accepts it (unsolicited list-shaped lines are untrusted).
                    if (name == "PLAYERS")
                        _playerTracker.NotePlayersPollIssued();
                    Server.WriteLine(input);
                }
                else
                    ConsoleUtil.WriteLine("Failed to send command - connection to server process hasn't been established yet.", ConsoleColor.Yellow);

                if (!exit)
                    continue;

                await Task.Delay(100);
                _exit = true;
            }
        }

        _readerTask = new Task(ReaderTaskMethod);
    }

    private static void DisableCursorManagementIfConsoleUnavailable()
    {
        if (NoSetCursor)
            return;

        if (Console.IsOutputRedirected || !CanUseConsoleCursor())
            NoSetCursor = true;
    }

    private static bool CanUseConsoleCursor()
    {
        try
        {
            _ = Console.CursorTop;
            _ = Console.WindowWidth;
            return true;
        }
        catch (Exception ex) when (IsConsoleCursorUnavailable(ex))
        {
            return false;
        }
    }

    private static void EchoInput(string input)
    {
        if (TryEchoInputAtCurrentCursor(input))
            return;

        ConsoleUtil.WriteLine($">>> {input}", ConsoleColor.DarkMagenta, -1);
    }

    private static bool TryEchoInputAtCurrentCursor(string input)
    {
        if (NoSetCursor)
            return false;

        var wroteInput = false;

        try
        {
            var currentLineCursor = Console.CursorTop;
            if (currentLineCursor <= 0)
                return false;

            var windowWidth = Console.WindowWidth;
            Console.SetCursorPosition(0, currentLineCursor - 1);
            ConsoleUtil.WriteLine($"{string.Empty.PadLeft(windowWidth)}>>> {input}", ConsoleColor.DarkMagenta, -1);
            wroteInput = true;
            Console.SetCursorPosition(0, currentLineCursor);
            return true;
        }
        catch (Exception ex) when (IsConsoleCursorUnavailable(ex))
        {
            NoSetCursor = true;
            return wroteInput;
        }
    }

    private static bool IsConsoleCursorUnavailable(Exception ex)
    {
        return ex is IOException or PlatformNotSupportedException or InvalidOperationException or ArgumentOutOfRangeException;
    }

    private void RunScpsl()
    {
        if (ServerApplicationLocator.TryLocate(
                _scpslExecutable,
                Configuration!.ServerApplicationPath,
                CurrentConfigPath,
                out var serverApplication,
                out var attemptedPaths))
        {
            ConsoleUtil.WriteLine("Executing: " + serverApplication!.ExecutablePath, ConsoleColor.DarkGreen);
            ConsoleUtil.WriteLine("Server application source: " + serverApplication.Source, ConsoleColor.DarkGreen);
            ConsoleUtil.WriteLine("Server working directory: " + serverApplication.WorkingDirectory, ConsoleColor.DarkGreen);

            var printStd = Configuration.LaShowStdoutStderr || _stdPrint;
            var redirectStreams =
                Configuration.LaLogStdoutStderr || printStd;

            var extraArgs = string.Empty;

            if (_noTrueColor || !Configuration.EnableTrueColor)
                extraArgs = " -disableAnsiColors";

            if (EnableGameHeartbeat)
                extraArgs += " -heartbeat";

            var startInfo = new ProcessStartInfo
            {
                FileName = serverApplication.ExecutablePath,
                Arguments =
                    $"-batchmode -nographics -txbuffer {Configuration.SlToLaBufferSize} -rxbuffer {Configuration.LaToSlBufferSize} -port{GamePort} -console{Server!.ConsolePort} -id{Environment.ProcessId}{extraArgs} {_gameArguments}",
                WorkingDirectory = serverApplication.WorkingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _gameProcess = Process.Start(startInfo);

            _processId = _gameProcess!.Id;
            SetTerminalTitle();

            ConsoleUtil.WriteLine("Game process started with PID: " + _processId, ConsoleColor.DarkGreen);

            _gameProcess!.OutputDataReceived += (_, args) =>
            {
                if (!redirectStreams || string.IsNullOrWhiteSpace(args.Data))
                    return;

                ConsoleUtil.WriteLine("[STDOUT] " + args.Data, ConsoleColor.Gray,
                    log: Configuration.LaLogStdoutStderr,
                    display: printStd);
            };

            _gameProcess!.ErrorDataReceived += (_, args) =>
            {
                if (!redirectStreams || string.IsNullOrWhiteSpace(args.Data))
                    return;

                ConsoleUtil.WriteLine("[STDERR] " + args.Data, ConsoleColor.DarkMagenta,
                    log: Configuration.LaLogStdoutStderr,
                    display: printStd);
            };

            _gameProcess!.BeginOutputReadLine();
            _gameProcess!.BeginErrorReadLine();

            _gameProcess!.Exited += (_, _) =>
            {
                try
                {
                    if (_gameProcess is { HasExited: false })
                    {
                        ConsoleUtil.WriteLine("Game process exited and has been killed.", ConsoleColor.Gray);
                        _gameProcess.Kill();
                    }
                    else ConsoleUtil.WriteLine("Game process exited and has been killed, no need to kill it.", ConsoleColor.Gray);
                }
                catch (Exception e)
                {
                    ConsoleUtil.WriteLine($"Game process exited and has been killed, can't kill it: {e.Message}.", ConsoleColor.Gray);
                }

                if (_processClosing)
                    return;

                switch (ExitAction)
                {
                    case ShutdownAction.Crash:
                        ConsoleUtil.WriteLine("The game process has been terminated...", ConsoleColor.Red);
                        Exit(0, true);
                        break;

                    case ShutdownAction.Shutdown:
                        Exit(0, true);
                        break;

                    case ShutdownAction.SilentShutdown:
                        Exit(0);
                        break;

                    case ShutdownAction.Restart:
                        Exit(0, false, true);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            };

            _gameProcess!.EnableRaisingEvents = true;
        }
        else
        {
            ConsoleUtil.WriteLine("Failed - Server application could not be located!", ConsoleColor.Red);
            ConsoleUtil.WriteLine("Attempted server application paths:", ConsoleColor.Red);

            foreach (var attemptedPath in attemptedPaths)
                ConsoleUtil.WriteLine("- " + attemptedPath, ConsoleColor.Red);

            ClaimExitAction(ShutdownAction.Shutdown);

            if (OperatingSystem.IsWindows())
                Exit((int)WindowsErrorCode.ERROR_FILE_NOT_FOUND, true);
            else if (OperatingSystem.IsLinux())
                Exit((int)UnixErrorCode.ERROR_FILE_NOT_FOUND, true);
            else
                Exit(1);
        }
    }

    private void RegisterCommands()
    {
        CommandService.RegisterCommand(new HeartbeatCancelCommand());
        CommandService.RegisterCommand(new HeartbeatControlCommand());
        CommandService.RegisterCommand(new RestartCommand());
        CommandService.RegisterCommand(new RestartWhenEmptyCommand());
        CommandService.RegisterCommand(new RestartNextRoundCommand());
        CommandService.RegisterCommand(new ForceRestartCommand());
        CommandService.RegisterCommand(new HelpCommand());
        CommandService.RegisterCommand(new LicenseCommand());
        CommandService.RegisterCommand(new ResaveCommand());
        CommandService.RegisterCommand(new PluginManagerCommand());
        CommandService.RegisterCommand(new LaCfgCommand());
        CommandService.RegisterCommand(new ExitCommand());
    }

    private static void ReadInput(Func<string?, bool> checkInput, Action validInputAction, Action invalidInputAction)
    {
        var input = Console.ReadLine();

        while (!checkInput(input))
        {
            invalidInputAction();

            input = Console.ReadLine();
        }

        validInputAction();
    }

    /// <summary>
    ///     Terminates the game.
    /// </summary>
    private void TerminateGame()
    {
        Server?.Stop();
        if (_gameProcess is { HasExited: false })
            _gameProcess.Kill();
    }

    /// <summary>
    ///     Terminates the game and console.
    /// </summary>
    public void Exit(int code = -1, bool waitForKey = false, bool restart = false)
    {
        lock (this)
        {
            if (_processClosing)
                return;

            _exit = true;
            _processClosing = true;

            // Stop this session's restart-when-empty watchdog so it cannot act on the next
            // session (which resets the static _exit flag back to false).
            try { _policyCts?.Cancel(); }
            catch { /* ignore */ }
            StopOneShotWatchdogForSessionEnd();

            LogCleaner.Abort();
            Logger.EndLogging();
            TerminateGame(); // Forcefully terminating the process
            _gameProcess?.Dispose();

            try
            {
                if (_readerTask is { IsCompleted: true })
                    _readerTask?.Dispose();
            }
            catch
            {
                //Ignore
            }

            try
            {
                if (_heartbeatMonitoringTask is { IsCompleted: true })
                    _heartbeatMonitoringTask?.Dispose();
            }
            catch
            {
                //Ignore
            }

            if (restart || ExitAction == ShutdownAction.Restart)
            {
                _ignoreNextRestart = true;
                return;
            }

            if (ExitAction == ShutdownAction.Crash && Configuration is { RestartOnCrash: true })
                return;

            // Real process termination (not a restart): tear down the process-wide channel.
            StopCommandChannel();

            if (waitForKey && ExitAction != ShutdownAction.SilentShutdown)
            {
                ConsoleUtil.WriteLine("Press any key to close...", ConsoleColor.DarkGray);
                Console.ReadKey(true);
            }

            Environment.Exit(code);
        }
    }

    /// <summary>
    ///     Releases resources used by the program
    /// </summary>
    public void Dispose()
    {
        Exit(0);
        GC.SuppressFinalize(this);
    }

    internal async Task LoadJsonOrTerminate()
    {
        try
        {
            if (!Directory.Exists(PathManager.ConfigPath))
                Directory.CreateDirectory(PathManager.ConfigPath);

            if (!File.Exists(PathManager.InternalJsonDataPath))
            {
                DataJson = new DataJson();
                await SaveJsonOrTerminate();
            }
            else
            {
                DataJson = await JsonFile.Load<DataJson>(PathManager.InternalJsonDataPath);
                JsonFile.UnlockFile(PathManager.InternalJsonDataPath);

                if (DataJson == null)
                {
                    ConsoleUtil.WriteLine("Json file is corrupted! Terminating LocalAdmin. If the issue persists, please delete the file and restart LocalAdmin.", ConsoleColor.Red);
                    Terminate();
                }
            }

            if (_previousPat != DataJson!.GitHubPersonalAccessToken)
            {
                _previousPat = DataJson.GitHubPersonalAccessToken;
                PluginInstaller.RefreshPat();
            }
        }
        catch (Exception e)
        {
            ConsoleUtil.WriteLine($"Failed to read JSON config file: {e.Message}", ConsoleColor.Red);
            DataJson = null;
            Terminate();
            throw;
        }
    }

    internal async Task SaveJsonOrTerminate()
    {
        if (!(await DataJson!.TrySave(PathManager.InternalJsonDataPath)))
            Terminate();
    }

    private void Terminate()
    {
        ClaimExitAction(ShutdownAction.SilentShutdown);
        Exit(1);
    }

    internal static void HandleExitSignal()
    {
        Console.WriteLine("exit");
        InputQueue.Enqueue("exit");
    }

    /// <summary>
    /// Best-effort request for a fresh authoritative player count: sends the <c>players</c> command
    /// to the game console (its "List of players (N):" reply is parsed by PlayerCountTracker).
    /// No-op if the console connection is not up yet.
    /// </summary>
    internal void RequestPlayerCountRefresh()
    {
        var server = Server;
        if (server is { Connected: true })
        {
            _playerTracker.NotePlayersPollIssued();
            server.WriteLine("players");
        }
    }

    // A graceful restart has already been committed (someone owns the ShutdownAction.Restart and its
    // retry/force fallback). Arming a new one-shot now must be refused so it cannot cancel the live
    // watchdog that owns that fallback - the intent would re-arm in the next session anyway.
    private bool RestartInProgress => DisableExitActionSignals && ExitAction == ShutdownAction.Restart;

    /// <summary>
    /// Claims a graceful restart and, when committed, arms a background force-fallback so a stalled
    /// game-side exit is still forced after ~60s (the restart-when-empty watchdog has this via its
    /// tick loop; direct callers like restart-on-next-round need it too). The fallback is bound to
    /// THIS session via the instance <see cref="_processClosing"/> flag - a fresh session gets a
    /// fresh instance - so it can never act on a later session. Returns true iff a Restart was
    /// actually claimed.
    /// </summary>
    private bool TryScheduleGracefulRestartWithForceFallback(string reason)
    {
        if (!RequestScheduledRestart(reason) || ExitAction != ShutdownAction.Restart)
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

                // If this session hasn't started tearing down, the graceful exit stalled - force it.
                if (!_processClosing)
                    ForceScheduledRestart($"{reason}; graceful exit stalled");
            }
            catch { /* fallback is best-effort; never surface */ }
        });

        return true;
    }

    internal bool TryStartOneShotRestartWhenEmpty(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes,
        int durationSeconds,
        out string message)
    {
        if (restartAtOrBelowPlayers < 0)
        {
            message = "restart-at-or-below players must be 0 or higher.";
            return false;
        }

        if (emptyConfirmations < 1)
        {
            message = "confirmations must be 1 or higher.";
            return false;
        }

        if (tickSeconds < 1)
        {
            message = "tick seconds must be 1 or higher.";
            return false;
        }

        if (recentActivityOverPlayers < 0)
        {
            message = "recent-activity player threshold must be 0 or higher.";
            return false;
        }

        if (recentActivityWindowMinutes < 0)
        {
            message = "recent-activity window minutes must be 0 or higher.";
            return false;
        }

        if (durationSeconds < 0)
        {
            message = "duration seconds must be 0 or higher.";
            return false;
        }

        OneShotLowActivityIntent intent;
        lock (OneShotIntentLock)
        {
            if (_processClosing || _exit)
            {
                message = "Cannot arm one-shot restart-when-empty while LocalAdmin is shutting down.";
                return false;
            }

            if (RestartInProgress)
            {
                message = "A server restart is already in progress; arm again after the server restarts.";
                return false;
            }

            if (_pendingOneShotLowActivity != null)
            {
                message = "One-shot restart-when-empty is already armed. Run \"restartwhenempty cancel\" before arming a new one.";
                return false;
            }

            intent = new OneShotLowActivityIntent(
                restartAtOrBelowPlayers,
                emptyConfirmations,
                tickSeconds,
                recentActivityOverPlayers,
                recentActivityWindowMinutes,
                durationSeconds);
            _pendingOneShotLowActivity = intent;
        }

        if (!TryArmLiveOneShotLowActivity(intent, out var options, out var error))
        {
            lock (OneShotIntentLock)
                _pendingOneShotLowActivity = null;

            message = error;
            return false;
        }

        // The pre-arm shutdown check above runs under OneShotIntentLock, but Exit() flips
        // _processClosing under the lifecycle lock - an arm can pass that check before Exit and
        // publish its live watchdog after Exit's teardown already ran. Re-check under the
        // lifecycle lock now that the CTS is published: if a shutdown won the race, tear the
        // watchdog back down instead of leaking an orphan bound to this dying session.
        lock (this)
        {
            if (_processClosing || _exit)
            {
                StopLiveOneShotLowActivity();
                lock (OneShotIntentLock)
                {
                    if (ReferenceEquals(_pendingOneShotLowActivity, intent))
                        _pendingOneShotLowActivity = null;
                }

                message = "Cannot arm one-shot restart-when-empty while LocalAdmin is shutting down.";
                return false;
            }
        }

        message = $"One-shot restart-when-empty armed. {options!.Summary} It persists across unrelated restarts until it fires or is canceled.";
        return true;
    }

    internal bool TryCancelOneShotRestartWhenEmpty(out string message)
    {
        bool wasArmed;
        lock (OneShotIntentLock)
        {
            wasArmed = _pendingOneShotLowActivity != null;
            _pendingOneShotLowActivity = null;
        }

        if (!wasArmed)
        {
            // Nothing armed to cancel. Crucially, DON'T stop the live watchdog here: if the one-shot
            // already fired, that live policy is now committing the restart and owns the retry/force
            // fallback - cancelling it would let a stalled graceful exit hang.
            message = RestartInProgress
                ? "A one-shot restart-when-empty has already committed a restart; it can no longer be canceled."
                : "One-shot restart-when-empty is not armed.";
            return false;
        }

        // Armed but not yet fired -> tear down the live watchdog.
        StopLiveOneShotLowActivity();
        message = "One-shot restart-when-empty canceled.";
        return true;
    }

    internal string GetOneShotRestartWhenEmptyStatus()
    {
        OneShotLowActivityIntent? intent;
        lock (OneShotIntentLock)
            intent = _pendingOneShotLowActivity;

        if (intent == null)
            return "One-shot restart-when-empty is not armed.";

        var options = RestartWhenEmptyOptions.OneShot(
            intent.RestartAtOrBelowPlayers,
            intent.EmptyConfirmations,
            intent.TickSeconds,
            intent.RecentActivityOverPlayers,
            intent.RecentActivityWindowMinutes,
            intent.DurationSeconds);

        var playerStatus = _playerTracker.IsReady && _playerTracker.TryGetKnownCount(out var players)
            ? players.ToString(CultureInfo.InvariantCulture)
            : "not ready";

        return $"One-shot restart-when-empty is armed. {options.Summary} Current player count: {playerStatus}.";
    }

    /// <summary>Re-arms one-shot intents carried over from a previous in-process session.</summary>
    private void RearmPendingOneShots()
    {
        OneShotLowActivityIntent? low;
        RoundRestartIntent? round;
        lock (OneShotIntentLock)
        {
            low = _pendingOneShotLowActivity;
            round = _pendingRoundRestart;
        }

        if (low != null)
        {
            if (TryArmLiveOneShotLowActivity(low, out _, out var error))
                ConsoleUtil.WriteLine("Re-armed carried-over one-shot restart-when-empty from a previous session.", ConsoleColor.Gray);
            else
                ConsoleUtil.WriteLine(
                    $"Failed to re-arm carried-over one-shot restart-when-empty: {error} The intent is still saved; it will retry after the next restart, or run \"restartwhenempty cancel\" to clear it.",
                    ConsoleColor.Yellow);
        }

        if (round != null)
            ConsoleUtil.WriteLine(
                round.RoundsRemaining <= 1
                    ? "Armed: LocalAdmin will restart on the next round restart (carried over from a previous session)."
                    : $"Armed: LocalAdmin will restart after {round.RoundsRemaining} more round restarts (carried over from a previous session).",
                ConsoleColor.Gray);
    }

    private bool TryArmLiveOneShotLowActivity(OneShotLowActivityIntent intent, out RestartWhenEmptyOptions? options, out string error)
    {
        // Stop any prior live watchdog before starting a new one.
        StopLiveOneShotLowActivity();

        options = RestartWhenEmptyOptions.OneShot(
            intent.RestartAtOrBelowPlayers,
            intent.EmptyConfirmations,
            intent.TickSeconds,
            intent.RecentActivityOverPlayers,
            intent.RecentActivityWindowMinutes,
            intent.DurationSeconds);

        var cts = new CancellationTokenSource();
        var token = cts.Token; // capture before a concurrent cancel could dispose the source

        try
        {
            var policy = new RestartWhenEmptyPolicy(this, _playerTracker, options, onRestartCommitted: OnOneShotLowActivityFired);
            policy.Start(token);
        }
        catch (Exception ex)
        {
            cts.Dispose();
            error = $"Failed to arm one-shot restart-when-empty: {ex.Message}.";
            return false;
        }

        // Publish the live CTS only if this exact intent is still the pending one. If a cancel (or a
        // replacing arm) ran while we were starting, tear the watchdog we just started back down so
        // no orphan watchdog can restart the server after "cancel" reported success.
        lock (OneShotIntentLock)
        {
            if (ReferenceEquals(_pendingOneShotLowActivity, intent))
            {
                _oneShotRestartWhenEmptyCts = cts;
                error = string.Empty;
                return true;
            }
        }

        try { cts.Cancel(); }
        catch { /* ignore */ }
        cts.Dispose();
        error = "One-shot restart-when-empty was canceled before it finished arming.";
        return false;
    }

    // Invoked by the one-shot watchdog the moment it commits to a restart: drop the persistent
    // intent so the relaunched session does NOT re-arm it (the one-shot has done its job).
    private void OnOneShotLowActivityFired()
    {
        lock (OneShotIntentLock)
            _pendingOneShotLowActivity = null;
    }

    private void StopLiveOneShotLowActivity()
    {
        CancellationTokenSource? cts;
        lock (OneShotIntentLock)
        {
            cts = _oneShotRestartWhenEmptyCts;
            _oneShotRestartWhenEmptyCts = null;
        }

        if (cts == null)
            return;

        try { cts.Cancel(); }
        catch { /* ignore */ }
        cts.Dispose();
    }

    /// <summary>
    /// Arms/replaces the "restart on the next round restart" one-shot. Fires after
    /// <paramref name="rounds"/> real round restarts (server boot's initial round is skipped) and
    /// persists across unrelated restarts until it fires or is canceled.
    /// </summary>
    internal bool TryArmRestartOnNextRound(int rounds, out string message)
    {
        if (rounds < 1)
        {
            message = "rounds must be 1 or higher.";
            return false;
        }

        lock (OneShotIntentLock)
        {
            if (_processClosing || _exit)
            {
                message = "Cannot arm restart-on-next-round while LocalAdmin is shutting down.";
                return false;
            }

            if (RestartInProgress)
            {
                message = "A server restart is already in progress; arm again after the server restarts.";
                return false;
            }

            if (_pendingRoundRestart != null)
            {
                message = "Restart-on-next-round is already armed. Run \"restartnextround cancel\" before arming a new one.";
                return false;
            }

            _pendingRoundRestart = new RoundRestartIntent(rounds);
        }

        message = rounds == 1
            ? "Armed: LocalAdmin will restart on the next round restart."
            : $"Armed: LocalAdmin will restart after {rounds} round restarts.";
        return true;
    }

    internal bool TryCancelRestartOnNextRound(out string message)
    {
        lock (OneShotIntentLock)
        {
            if (_roundRestartFiring)
            {
                message = "Restart-on-next-round is firing right now; it can no longer be canceled.";
                return false;
            }

            if (_pendingRoundRestart == null)
            {
                message = "Restart-on-next-round is not armed.";
                return false;
            }

            _pendingRoundRestart = null;
        }

        message = "Restart-on-next-round canceled.";
        return true;
    }

    internal string GetRestartOnNextRoundStatus()
    {
        lock (OneShotIntentLock)
        {
            if (_pendingRoundRestart == null)
                return "Restart-on-next-round is not armed.";

            if (_roundRestartFiring)
                return "Restart-on-next-round is firing now.";

            var remaining = _pendingRoundRestart.RoundsRemaining;
            return remaining <= 1
                ? "Restart-on-next-round is armed (fires on the next round restart)."
                : $"Restart-on-next-round is armed (fires after {remaining} more round restarts).";
        }
    }

    /// <summary>
    /// Called from the TCP loop on every RoundRestart control byte. Skips the session's initial
    /// round, then counts real round transitions toward an armed "restart on next round" one-shot.
    /// </summary>
    internal void NotifyRoundRestart()
    {
        if (!_sawInitialRoundRestart)
        {
            _sawInitialRoundRestart = true;
            return;
        }

        bool fire;
        int remaining;
        lock (OneShotIntentLock)
        {
            if (_pendingRoundRestart == null || _roundRestartFiring)
                return;

            // Count this round toward the target, but do NOT clear the intent yet - we only consume
            // it once a Restart is actually claimed below (otherwise a lost race would drop it).
            // _roundRestartFiring blocks cancel in the meantime so it cannot report success for a
            // restart that is already committing.
            if (_pendingRoundRestart.RoundsRemaining <= 1)
            {
                fire = true;
                remaining = 0;
                _roundRestartFiring = true;
            }
            else
            {
                _pendingRoundRestart.RoundsRemaining--;
                remaining = _pendingRoundRestart.RoundsRemaining;
                fire = false;
            }
        }

        if (!fire)
        {
            ConsoleUtil.WriteLine($"Round restart observed; {remaining} more until LocalAdmin restarts.", ConsoleColor.Gray);
            return;
        }

        ConsoleUtil.WriteLine("Round restart observed; performing the scheduled LocalAdmin restart now.", ConsoleColor.Cyan);

        var scheduled = false;
        try
        {
            scheduled = TryScheduleGracefulRestartWithForceFallback("restart on next round restart");
        }
        finally
        {
            lock (OneShotIntentLock)
            {
                _roundRestartFiring = false;
                if (scheduled)
                    _pendingRoundRestart = null; // claimed - consume the intent (fallback will force if it stalls)
            }
        }

        if (!scheduled)
        {
            // Rejected, or a non-restart shutdown/crash won: leave it armed so the next round (or a
            // crash-restart's next session) retries instead of silently dropping the request.
            ConsoleUtil.WriteLine("Restart-on-next-round could not be scheduled now (another shutdown action is in progress); leaving it armed.", ConsoleColor.Yellow);
        }
    }

    // Tears down only the LIVE one-shot watchdog for a session teardown; the persistent intent is
    // intentionally preserved so RearmPendingOneShots() can restore it in the next session.
    private void StopOneShotWatchdogForSessionEnd() => StopLiveOneShotLowActivity();

    /// <summary>
    /// Performs or retries a graceful restart (the RestartCommand pattern) for an automated trigger.
    /// The storm-guard bypass (<see cref="_ignoreNextRestart"/>) is NOT armed here: Exit()
    /// already sets it atomically under its lifecycle lock at the moment it commits to a
    /// ShutdownAction.Restart, so arming early would let an interleaving crash/shutdown
    /// consume the bypass for the wrong restart. The Program loop relaunches via Restart.
    /// </summary>
    internal bool RequestScheduledRestart(string reason)
    {
        // Coordinate with Exit() via the same lifecycle lock. If the process is already
        // closing (a real shutdown/crash won the race, or a stale watchdog from a previous
        // session fired late), the restart request is already terminal from the caller's
        // perspective - we must not resurrect or re-target a teardown.
        lock (this)
        {
            if (_processClosing || _exit)
                return true;

            // Defer to any authoritative exit decision already in flight. The heartbeat
            // crash path, the console exit/quit/stop handler, and Terminate() all set
            // DisableExitActionSignals=true the instant they claim the shutdown (heartbeat
            // does so just before setting _exit, leaving a tiny window where _exit is still
            // false). If that flag is already set, something else owns the exit - we must NOT
            // overwrite its ExitAction (which would, e.g., misclassify a crash as a graceful
            // restart and wrongly arm the storm-guard bypass).
            if (DisableExitActionSignals && ExitAction != ShutdownAction.Restart)
                return false;

            var firstRequest = !DisableExitActionSignals || ExitAction != ShutdownAction.Restart;

            DisableExitActionSignals = true;
            ExitAction = ShutdownAction.Restart;

            ConsoleUtil.WriteLine(
                firstRequest
                    ? $"Scheduled restart requested ({reason})."
                    : $"Scheduled restart retry requested ({reason}).",
                ConsoleColor.Cyan);

            var server = Server;
            if (server is { Connected: true })
            {
                if (server.WriteLine("exit"))
                    return true;

                ConsoleUtil.WriteLine("Scheduled restart could not send graceful exit command; falling back to LocalAdmin-controlled restart.", ConsoleColor.Yellow);
            }

            // No live game connection, or the graceful command could not be sent: unblock
            // the main loop; the ExitAction.Restart path will terminate the game and relaunch.
            _exit = true;
            return true;
        }
    }

    /// <summary>
    /// Forces a scheduled restart after the graceful game-side exit command failed to
    /// complete. This keeps the same restart-storm guard bypass semantics as
    /// <see cref="RequestScheduledRestart"/>.
    /// </summary>
    internal bool ForceScheduledRestart(string reason)
    {
        lock (this)
        {
            if (_processClosing || _exit)
                return true;

            if (DisableExitActionSignals && ExitAction != ShutdownAction.Restart)
                return false;

            DisableExitActionSignals = true;
            ExitAction = ShutdownAction.Restart;

            ConsoleUtil.WriteLine($"Scheduled restart fallback forcing game process termination ({reason}).", ConsoleColor.Yellow);
        }

        Exit(0, restart: true);
        return true;
    }

    private void ClaimHeartbeatRestart()
    {
        lock (this)
        {
            if (_processClosing || _exit)
                return;

            if (!DisableExitActionSignals)
            {
                DisableExitActionSignals = true;
                ExitAction = ShutdownAction.Crash;
            }

            _exit = true;
        }
    }

    internal bool TryApplyGameExitAction(ShutdownAction action)
    {
        lock (this)
        {
            if (DisableExitActionSignals)
                return false;

            ExitAction = action;
            return true;
        }
    }

    internal void ClaimExitAction(ShutdownAction action)
    {
        lock (this)
        {
            if (_processClosing)
                return;

            DisableExitActionSignals = true;
            ExitAction = action;
        }
    }

    internal void OnConsoleLine(string strippedLine)
    {
        // Feed the player-count tracker (restart-when-empty). The command-channel read-back
        // is driven separately via ConsoleUtil.OutputSink so it also captures LocalAdmin's
        // own console output (help, errors, the ">>> input" echo), not just game lines.
        try
        {
            _playerTracker.ProcessConsoleLine(strippedLine);
        }
        catch
        {
            // tracker must never break console output
        }
    }

    internal void NotifyIdleEnter() => _playerTracker.NotifyIdleEnter();

    internal void NotifyIdleExit() => _playerTracker.NotifyIdleExit();

    internal void HandleHeartbeat()
    {
        switch (CurrentHeartbeatStatus)
        {
            case HeartbeatStatus.Disabled when EnableGameHeartbeat:
                return;

            case HeartbeatStatus.Disabled:
                ConsoleUtil.WriteLine("Received a heartbeat signal, but the heartbeat is disabled.", ConsoleColor.Yellow);
                return;

            case HeartbeatStatus.AwaitingFirstHeartbeat:
                ConsoleUtil.WriteLine("Received first heartbeat. Silent crash detection is now active.", ConsoleColor.DarkGreen);
                HeartbeatStopwatch.Restart();
                CurrentHeartbeatStatus = HeartbeatStatus.Active;
                break;

            default:
                HeartbeatStopwatch.Restart();
                break;
        }
    }

    private void StartHeartbeatMonitoring()
    {
        async void HeartbeatMonitoringMethod()
        {
            {
                ushort i = 0;

                while (CurrentHeartbeatStatus == HeartbeatStatus.AwaitingFirstHeartbeat)
                {
                    if (_exit)
                        return;

                    switch (i)
                    {
                        case 320: //80 seconds
                            ConsoleUtil.WriteLine(
                                "No heartbeat was received so far. Silent crash detection is NOT active.",
                                ConsoleColor.Yellow);
                            i++;
                            await Task.Delay(1000);
                            break;

                        case < 320:
                            i++;
                            await Task.Delay(250);
                            break;

                        default:
                            await Task.Delay(1000);
                            break;
                    }
                }
            }

            while (!_exit)
            {
                switch (CurrentHeartbeatStatus)
                {
                    case HeartbeatStatus.AwaitingFirstHeartbeat:
                    case HeartbeatStatus.Disabled:
                        await Task.Delay(1000);
                        continue;

                    case HeartbeatStatus.Active:
                        if (HeartbeatStopwatch.ElapsedMilliseconds <= (_heartbeatSpanMaxThreshold * 1000))
                        {
                            if (HeartbeatWarningStage != 0)
                                ConsoleUtil.WriteLine("Heartbeat has been received. Restart procedure aborted.", ConsoleColor.DarkGreen);

                            HeartbeatWarningStage = 0;
                            await Task.Delay(1800);
                            continue;
                        }

                        HeartbeatWarningStage++;

                        if (HeartbeatWarningStage >= _heartbeatRestartInSeconds)
                        {
                            ConsoleUtil.WriteLine("Game server has probably crashed. Restarting the server...", ConsoleColor.Red);
                            HeartbeatStopwatch.Reset();

                            ClaimHeartbeatRestart();
                            return;
                        }

                        ConsoleUtil.WriteLine($"Game server has not sent a heartbeat in {HeartbeatStopwatch.ElapsedMilliseconds / 1000} seconds. Restarting the server in {_heartbeatRestartInSeconds - HeartbeatWarningStage} seconds! Type \"hbc\" command to abort!", ConsoleColor.Red);
                        await Task.Delay(1000);
                        break;
                }
            }
        }
        _heartbeatMonitoringTask = new Task(HeartbeatMonitoringMethod);
        _heartbeatMonitoringTask.Start();
    }

    ~LocalAdmin()
    {
        Exit(0);
    }

    /// <summary>
    /// Sets the terminal title if not disabled in the config or by command line arguments
    /// </summary>
    private void SetTerminalTitle()
    {
        if (_noTerminalTitle || Configuration?.SetTerminalTitle is false)
            return;

        Console.Title = BaseWindowTitle;
    }

    internal void SetIdleModeState(bool state)
    {
        _idleMode = state;
        SetTerminalTitle();
    }
}
