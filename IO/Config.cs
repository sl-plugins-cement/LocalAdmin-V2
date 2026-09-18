using System;
using System.IO;
using System.Text;

namespace LocalAdmin.V2.IO;

public class Config
{
    public bool RestartOnCrash = true;
    public bool EnableHeartbeat = true;
    public bool SetTerminalTitle = true;
    public bool LaLiveViewUseUtc;

    public bool LaShowStdoutStderr;
    public bool LaNoSetCursor = OperatingSystem.IsLinux();
    public bool EnableTrueColor = OperatingSystem.IsLinux();
    public bool EnableLaLogs = true;
    public bool LaLogsUseUtc;
    public bool LaLogsUseZForUtc;
    public bool LaLogAutoFlush = true;
    public bool LaLogStdoutStderr = true;
    public bool LaDeleteOldLogs = true;
    public ushort LaLogsExpirationDays = 90;
    public bool DeleteOldRoundLogs;
    public ushort RoundLogsExpirationDays = 180;
    public bool CompressOldRoundLogs;
    public ushort RoundLogsCompressionThresholdDays = 14;
    public uint HeartbeatSpanMaxThreshold = 30;
    public uint HeartbeatRestartInSeconds = 11;

    public int LaToSlBufferSize = 25000;
    public int SlToLaBufferSize = 200000;

    // Enhanced fork additions: headless command channel + restart-when-empty.
    public bool CommandChannelEnabled;
    public bool AutoRestartEmptyEnabled;
    public int RestartAtOrBelowPlayers = 1;
    public int EmptyConfirmations = 2;
    public int EmptyDurationSeconds;
    public int PolicyTickSeconds = 30;
    public int RecentActivityOverPlayers = 5;
    public int RecentActivityWindowMinutes = 30;

    public string LaLiveViewTimeFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";
    public string CommandChannelPipeName = "LocalAdmin-Console";
    public string ServerApplicationPath = string.Empty;
    // Managed activation is disabled until a lane is assigned. The CLI path must be absolute so
    // LocalAdmin never resolves a deployment hook through a mutable service PATH.
    public string OpsLane = string.Empty;
    // DEV keeps its lane identity for typed restart receipts without enabling checkpoint activation.
    public bool OpsManagedActivationEnabled = true;
    internal bool UsesManagedActivation => OpsManagedActivationEnabled && !string.IsNullOrWhiteSpace(OpsLane);
    public string OpsStateRoot = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scpsl-ops")
        : "/var/lib/scpsl-ops";
    public string OpsCliPath = string.Empty;

    public string SerializeConfig()
    {
        var sb = new StringBuilder();

        sb.Append("restart_on_crash: ");
        sb.AppendLine(RestartOnCrash.ToString().ToLowerInvariant());

        sb.Append("enable_heartbeat: ");
        sb.AppendLine(EnableHeartbeat.ToString().ToLowerInvariant());

        sb.Append("set_terminal_title: ");
        sb.AppendLine(SetTerminalTitle.ToString().ToLowerInvariant());

        sb.Append("la_live_view_use_utc: ");
        sb.AppendLine(LaLiveViewUseUtc.ToString().ToLowerInvariant());

        sb.Append("la_live_view_time_format: ");
        sb.AppendLine(LaLiveViewTimeFormat);

        sb.Append("la_show_stdout_and_stderr: ");
        sb.AppendLine(LaShowStdoutStderr.ToString().ToLowerInvariant());

        sb.Append("la_no_set_cursor: ");
        sb.AppendLine(LaNoSetCursor.ToString().ToLowerInvariant());

        sb.Append("enable_true_color: ");
        sb.AppendLine(EnableTrueColor.ToString().ToLowerInvariant());

        sb.Append("enable_la_logs: ");
        sb.AppendLine(EnableLaLogs.ToString().ToLowerInvariant());

        sb.Append("la_logs_use_utc: ");
        sb.AppendLine(LaLogsUseUtc.ToString().ToLowerInvariant());

        sb.Append("la_logs_use_Z_for_utc: ");
        sb.AppendLine(LaLogsUseZForUtc.ToString().ToLowerInvariant());

        sb.Append("la_log_auto_flush: ");
        sb.AppendLine(LaLogAutoFlush.ToString().ToLowerInvariant());

        sb.Append("la_log_stdout_and_stderr: ");
        sb.AppendLine(LaLogStdoutStderr.ToString().ToLowerInvariant());

        sb.Append("la_delete_old_logs: ");
        sb.AppendLine(LaDeleteOldLogs.ToString().ToLowerInvariant());

        sb.Append("la_logs_expiration_days: ");
        sb.AppendLine(LaLogsExpirationDays.ToString());

        sb.Append("delete_old_round_logs: ");
        sb.AppendLine(DeleteOldRoundLogs.ToString().ToLowerInvariant());

        sb.Append("round_logs_expiration_days: ");
        sb.AppendLine(RoundLogsExpirationDays.ToString());

        sb.Append("compress_old_round_logs: ");
        sb.AppendLine(CompressOldRoundLogs.ToString().ToLowerInvariant());

        sb.Append("round_logs_compression_threshold_days: ");
        sb.AppendLine(RoundLogsCompressionThresholdDays.ToString());

        sb.Append("heartbeat_span_max_threshold: ");
        sb.AppendLine(HeartbeatSpanMaxThreshold.ToString());

        sb.Append("heartbeat_restart_in_seconds: ");
        sb.AppendLine(HeartbeatRestartInSeconds.ToString());

        sb.Append("la_to_sl_buffer_size: ");
        sb.AppendLine(LaToSlBufferSize.ToString());

        sb.Append("sl_to_la_buffer_size: ");
        sb.AppendLine(SlToLaBufferSize.ToString());

        sb.Append("command_channel_enabled: ");
        sb.AppendLine(CommandChannelEnabled.ToString().ToLowerInvariant());

        sb.Append("command_channel_pipe_name: ");
        sb.AppendLine(CommandChannelPipeName);

        sb.Append("server_application_path: ");
        sb.AppendLine(ServerApplicationPath);

        sb.Append("ops_lane: ");
        sb.AppendLine(OpsLane);

        sb.Append("ops_managed_activation_enabled: ");
        sb.AppendLine(OpsManagedActivationEnabled.ToString().ToLowerInvariant());

        sb.Append("ops_state_root: ");
        sb.AppendLine(OpsStateRoot);

        sb.Append("ops_cli_path: ");
        sb.AppendLine(OpsCliPath);

        sb.Append("auto_restart_empty_enabled: ");
        sb.AppendLine(AutoRestartEmptyEnabled.ToString().ToLowerInvariant());

        sb.Append("restart_at_or_below_players: ");
        sb.AppendLine(RestartAtOrBelowPlayers.ToString());

        sb.Append("empty_confirmations: ");
        sb.AppendLine(EmptyConfirmations.ToString());

        sb.Append("empty_duration_seconds: ");
        sb.AppendLine(EmptyDurationSeconds.ToString());

        sb.Append("policy_tick_seconds: ");
        sb.AppendLine(PolicyTickSeconds.ToString());

        sb.Append("recent_activity_over_players: ");
        sb.AppendLine(RecentActivityOverPlayers.ToString());

        sb.Append("recent_activity_window_minutes: ");
        sb.AppendLine(RecentActivityWindowMinutes.ToString());

        return sb.ToString();
    }

    public static Config DeserializeConfig(string[] lines)
    {
        var cfg = new Config();

        foreach (var line in lines)
        {
            // Split on the FIRST ": " only, so values that themselves contain ": " (e.g. a POSIX
            // path like "/home/user/my: server/SCPSL.x86_64") are preserved instead of dropped.
            var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex < 0)
                continue;

            var sp = new[] { line[..separatorIndex], line[(separatorIndex + 2)..] };

            switch (sp[0].ToLowerInvariant())
            {
                case "restart_on_crash" when bool.TryParse(sp[1], out var b):
                    cfg.RestartOnCrash = b;
                    break;

                case "enable_heartbeat" when bool.TryParse(sp[1], out var b):
                    cfg.EnableHeartbeat = b;
                    break;

                case "set_terminal_title" when bool.TryParse(sp[1], out var b):
                    cfg.SetTerminalTitle = b;
                    break;

                case "la_live_view_use_utc" when bool.TryParse(sp[1], out var b):
                    cfg.LaLiveViewUseUtc = b;
                    break;

                case "la_live_view_time_format":
                    cfg.LaLiveViewTimeFormat = sp[1];
                    break;

                case "la_show_stdout_and_stderr" when bool.TryParse(sp[1], out var b):
                    cfg.LaShowStdoutStderr = b;
                    break;

                case "la_no_set_cursor" when bool.TryParse(sp[1], out var b):
                    cfg.LaNoSetCursor = b;
                    break;

                case "enable_true_color" when bool.TryParse(sp[1], out var b):
                    cfg.EnableTrueColor = b;
                    break;

                case "enable_la_logs" when bool.TryParse(sp[1], out var b):
                    cfg.EnableLaLogs = b;
                    break;

                case "la_logs_use_utc" when bool.TryParse(sp[1], out var b):
                    cfg.LaLogsUseUtc = b;
                    break;

                case "la_logs_use_Z_for_utc" when bool.TryParse(sp[1], out var b):
                    cfg.LaLogsUseZForUtc = b;
                    break;

                case "la_log_auto_flush" when bool.TryParse(sp[1], out var b):
                    cfg.LaLogAutoFlush = b;
                    break;

                case "la_log_stdout_and_stderr" when bool.TryParse(sp[1], out var b):
                    cfg.LaLogStdoutStderr = b;
                    break;

                case "la_delete_old_logs" when bool.TryParse(sp[1], out var b):
                    cfg.LaDeleteOldLogs = b;
                    break;

                case "la_logs_expiration_days" when ushort.TryParse(sp[1], out var b):
                    cfg.LaLogsExpirationDays = b;
                    break;

                case "delete_old_round_logs" when bool.TryParse(sp[1], out var b):
                    cfg.DeleteOldRoundLogs = b;
                    break;

                case "round_logs_expiration_days" when ushort.TryParse(sp[1], out var b):
                    cfg.RoundLogsExpirationDays = b;
                    break;

                case "compress_old_round_logs" when bool.TryParse(sp[1], out var b):
                    cfg.CompressOldRoundLogs = b;
                    break;

                case "round_logs_compression_threshold_days" when ushort.TryParse(sp[1], out var b):
                    cfg.RoundLogsCompressionThresholdDays = b;
                    break;

                case "heartbeat_restart_in_seconds" when ushort.TryParse(sp[1], out var b):
                    cfg.HeartbeatRestartInSeconds = b;
                    break;

                case "heartbeat_span_max_threshold" when ushort.TryParse(sp[1], out var b):
                    cfg.HeartbeatSpanMaxThreshold = b;
                    break;

                case "la_to_sl_buffer_size" when int.TryParse(sp[1], out var b):
                    if (b <= 100)
                        b = 101;
                    cfg.LaToSlBufferSize = b;
                    break;

                case "sl_to_la_buffer_size" when int.TryParse(sp[1], out var b):
                    if (b <= 350)
                        b = 351;
                    cfg.SlToLaBufferSize = b;
                    break;

                case "command_channel_enabled" when bool.TryParse(sp[1], out var b):
                    cfg.CommandChannelEnabled = b;
                    break;

                case "command_channel_pipe_name":
                    if (!string.IsNullOrWhiteSpace(sp[1]))
                        cfg.CommandChannelPipeName = sp[1].Trim();
                    break;

                case "server_application_path":
                    cfg.ServerApplicationPath = sp[1].Trim();
                    break;

                case "ops_lane":
                    cfg.OpsLane = sp[1].Trim();
                    break;

                case "ops_managed_activation_enabled" when bool.TryParse(sp[1], out var enabled):
                    cfg.OpsManagedActivationEnabled = enabled;
                    break;

                case "ops_state_root":
                    cfg.OpsStateRoot = sp[1].Trim();
                    break;

                case "ops_cli_path":
                    cfg.OpsCliPath = sp[1].Trim();
                    break;

                case "auto_restart_empty_enabled" when bool.TryParse(sp[1], out var b):
                    cfg.AutoRestartEmptyEnabled = b;
                    break;

                case "restart_at_or_below_players" when int.TryParse(sp[1], out var b):
                    if (b < 0)
                        b = 0;
                    cfg.RestartAtOrBelowPlayers = b;
                    break;

                case "empty_confirmations" when int.TryParse(sp[1], out var b):
                    if (b < 1)
                        b = 1;
                    cfg.EmptyConfirmations = b;
                    break;

                case "empty_duration_seconds" when int.TryParse(sp[1], out var b):
                    if (b < 0)
                        b = 0;
                    cfg.EmptyDurationSeconds = b;
                    break;

                case "policy_tick_seconds" when int.TryParse(sp[1], out var b):
                    if (b < 1)
                        b = 1;
                    cfg.PolicyTickSeconds = b;
                    break;

                case "recent_activity_over_players" when int.TryParse(sp[1], out var b):
                    if (b < 0)
                        b = 0;
                    cfg.RecentActivityOverPlayers = b;
                    break;

                case "recent_activity_window_minutes" when int.TryParse(sp[1], out var b):
                    if (b < 0)
                        b = 0;
                    cfg.RecentActivityWindowMinutes = b;
                    break;
            }

            if (!cfg.EnableLaLogs || cfg.LaLogsExpirationDays == 0)
                cfg.LaDeleteOldLogs = false;

            if (cfg.RoundLogsExpirationDays == 0)
                cfg.DeleteOldRoundLogs = false;

            if (cfg.RoundLogsCompressionThresholdDays == 0)
                cfg.CompressOldRoundLogs = false;

            if (cfg.DeleteOldRoundLogs && cfg.RoundLogsExpirationDays <= cfg.RoundLogsCompressionThresholdDays)
                cfg.CompressOldRoundLogs = false;
        }

        return cfg;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();

        sb.AppendLine(RestartOnCrash ? "- Server will be automatically restarted after a crash." : "- Server will NOT be automatically restarted after a crash.");
        sb.AppendLine(EnableHeartbeat ? "- LocalAdmin will attempt to detect silent server crashes (heartbeat enabled)." : "- LocalAdmin will NOT attempt to detect silent server crashes (heartbeat DISABLED).");
        sb.AppendLine(SetTerminalTitle ? "- LocalAdmin will attempt to show its current status in your terminal title." : "- LocalAdmin will NOT attempt to show its current status in your terminal title.");
        sb.AppendLine(LaLiveViewUseUtc ? "- LocalAdmin live view will use UTC timezone." : "- LocalAdmin live view will use local timezone.");
        sb.AppendLine($"- LocalAdmin live will use the following timestamp format: {LaLiveViewTimeFormat}");
        sb.AppendLine($"- UTC timezone will be displayed as \"{(LaLogsUseZForUtc ? "Z" : "+00:00")}\".");
        sb.AppendLine(LaShowStdoutStderr ? "- Standard outputs (that contain a lot of debug information) will be displayed." : "- Standard outputs (that contain a lot of debug information) will NOT be displayed.");
        sb.AppendLine(LaNoSetCursor ? "- Cursor position management is DISABLED." : "- Cursor position management is ENABLED.");
        sb.AppendLine(EnableTrueColor ? "- True Color output is ENABLED." : "- True Color output is DISABLED.");
        sb.AppendLine(EnableLaLogs ? "- LocalAdmin logs are ENABLED." : "- LocalAdmin logs are DISABLED.");

        if (EnableLaLogs)
        {
            sb.AppendLine(LaLogsUseUtc ? "- LocalAdmin logs will use UTC timezone." : "- LocalAdmin logs will use local timezone.");
            sb.AppendLine(LaLogAutoFlush ? "- LocalAdmin logs auto flushing is ENABLED." : "- LocalAdmin logs auto flushing is DISABLED.");
            sb.AppendLine(LaLogStdoutStderr ? "- Standard outputs will be logged." : "- Standard outputs will NOT be logged.");

            if (LaDeleteOldLogs)
            {
                sb.Append("- Delete LocalAdmin logs older than ");
                sb.Append(LaLogsExpirationDays);
                sb.AppendLine(" days");
            }
            else sb.AppendLine("- Do not delete old LocalAdmin logs.");
        }

        if (DeleteOldRoundLogs)
        {
            sb.Append("- Delete round logs older than ");
            sb.Append(RoundLogsExpirationDays);
            sb.AppendLine(" days");
        }
        else sb.AppendLine("- Do not delete old round logs.");

        if (CompressOldRoundLogs)
        {
            sb.Append("- Compress round logs older than ");
            sb.Append(RoundLogsCompressionThresholdDays);
            sb.AppendLine(" days");
        }
        else sb.AppendLine("- Do not compress old round logs.");

        sb.AppendLine(CommandChannelEnabled
            ? $"- Command channel (named pipe \"{CommandChannelPipeName}-<port>\") is ENABLED."
            : "- Command channel (named pipe) is DISABLED.");

        sb.AppendLine(string.IsNullOrWhiteSpace(ServerApplicationPath)
            ? "- Server application path is not configured; LocalAdmin will auto-discover it."
            : $"- Server application path is configured as: {ServerApplicationPath}");

        sb.AppendLine(!UsesManagedActivation
            ? $"- Managed activation is DISABLED; typed command lane: {OpsLane}."
            : $"- Managed activation lane: {OpsLane}; state root: {OpsStateRoot}; CLI: {OpsCliPath}");

        if (AutoRestartEmptyEnabled)
        {
            sb.Append("- Auto restart-when-empty is ENABLED (restart at or below ");
            sb.Append(RestartAtOrBelowPlayers);
            sb.Append(" players after ");
            sb.Append(EmptyConfirmations);
            sb.Append(" confirmations");
            if (EmptyDurationSeconds > 0)
            {
                sb.Append(" and ");
                sb.Append(EmptyDurationSeconds);
                sb.Append("s sustained");
            }
            sb.Append(", polling every ");
            sb.Append(PolicyTickSeconds);
            sb.Append("s, blocked if the last ");
            sb.Append(RecentActivityWindowMinutes);
            sb.Append(" minutes peaked above ");
            sb.Append(RecentActivityOverPlayers);
            sb.AppendLine(" players).");
        }
        else sb.AppendLine("- Auto restart-when-empty is DISABLED.");

        return sb.ToString();
    }
}
