// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Background watchdog that gracefully restarts the server once it has been at or below a player
/// threshold for a configurable number of consecutive ticks (and, optionally, for a sustained
/// duration), guarded by a rolling recent-activity window. Modeled on LocalAdmin's
/// StartHeartbeatMonitoring().
///
/// Restart decisions act ONLY on a FRESH authoritative player count (the server's reply to the
/// <c>players</c> command, which this watchdog polls each tick, or the game's IdleEnter byte); a
/// stale or unknown count never triggers a restart. The restart itself is delegated to
/// <see cref="LocalAdmin.RequestScheduledRestart"/> which marks the next restart as expected so the
/// restart-storm guard is not tripped; an independent cross-session cooldown (<see
/// cref="s_lastAutoRestartUtc"/>) caps how often the watchdog can restart, so an always-empty
/// server cannot restart-loop tightly.
/// </summary>
internal sealed class RestartWhenEmptyPolicy
{
    private const int GracefulRestartRetrySeconds = 10;
    private const int GracefulRestartFallbackSeconds = 60;

    // Cross-session floor between watchdog-initiated restarts. Readiness gating already forces a
    // full boot between restarts; this is the belt-and-suspenders cap that also covers very
    // aggressive configs (e.g. window=0, confirmations=1, tick=1). Manual restarts are unaffected.
    private const int MinAutoRestartIntervalSeconds = 60;
    private static DateTime? s_lastAutoRestartUtc;

    private readonly LocalAdmin _localAdmin;
    private readonly PlayerCountTracker _tracker;
    private readonly RecentActivityWindow _activity;
    private readonly Action? _onRestartCommitted;

    private readonly int _restartAtOrBelow;
    private readonly int _emptyConfirmations;
    private readonly int _tickSeconds;
    private readonly int _durationSeconds;
    private readonly int _overPlayers;
    private readonly int _windowMinutes;
    private readonly bool _requireFullRecentWindow;
    private readonly string _label;
    private readonly string _restartReason;

    private int _lowPopStreak;
    private DateTime? _lowSinceUtc;
    private DateTime? _restartRequestedAtUtc;
    private int _restartRequestAttempts;
    private bool _restartFallbackAttempted;
    private bool _restartAbandoned;

    internal RestartWhenEmptyPolicy(LocalAdmin localAdmin, PlayerCountTracker tracker, Config config)
        : this(localAdmin, tracker, RestartWhenEmptyOptions.FromConfig(config))
    {
    }

    internal RestartWhenEmptyPolicy(
        LocalAdmin localAdmin,
        PlayerCountTracker tracker,
        RestartWhenEmptyOptions options,
        Action? onRestartCommitted = null)
    {
        _localAdmin = localAdmin;
        _tracker = tracker;
        _activity = new RecentActivityWindow();
        _onRestartCommitted = onRestartCommitted;

        _restartAtOrBelow = options.RestartAtOrBelowPlayers;
        _emptyConfirmations = options.EmptyConfirmations;
        _tickSeconds = options.TickSeconds;
        _durationSeconds = options.DurationSeconds;
        _overPlayers = options.RecentActivityOverPlayers;
        _windowMinutes = options.RecentActivityWindowMinutes;
        _requireFullRecentWindow = options.RequireFullRecentWindow;
        _label = options.Label;
        _restartReason = options.RestartReason;
    }

    /// <summary>
    /// Starts the watchdog loop bound to <paramref name="token"/>. The loop self-terminates when
    /// the token is cancelled (the session ends) so a stale watchdog from a previous session can
    /// never act on a new one.
    /// </summary>
    internal void Start(CancellationToken token)
    {
        ConsoleUtil.WriteLine(
            $"{_label} started. {RestartWhenEmptyOptions.FormatSummary(_restartAtOrBelow, _emptyConfirmations, _tickSeconds, _durationSeconds, _overPlayers, _windowMinutes)}",
            ConsoleColor.Gray);

        async Task LoopAsync()
        {
            try
            {
                // Small initial settle so the game has a chance to come up before we sample.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_tickSeconds, 5)), token).ConfigureAwait(false);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        Tick(token);
                    }
                    catch (Exception ex)
                    {
                        // Never let the watchdog kill the process; just log and reset the streak.
                        ConsoleUtil.WriteLine($"{_label} tick failed: {ex.Message}", ConsoleColor.Yellow);
                        ResetStreak();
                    }

                    var delaySeconds = _restartRequestedAtUtc.HasValue && !_restartFallbackAttempted
                        ? Math.Min(_tickSeconds, GracefulRestartRetrySeconds)
                        : _tickSeconds;

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // session ended - normal shutdown of the watchdog
            }
        }

        // Task.Run gives us a real Task whose faults are observed (no fire-and-forget async void).
        _ = Task.Run(LoopAsync, token);
    }

    private void Tick(CancellationToken token)
    {
        var nowUtc = DateTime.UtcNow;

        if (_restartRequestedAtUtc.HasValue)
        {
            ContinueScheduledRestart(nowUtc);
            return;
        }

        if (_restartAbandoned)
            return;

        // Only act on a FRESH authoritative reading (recent players-list reply or idle byte).
        var maxAge = TimeSpan.FromSeconds((_tickSeconds * 2) + 15);
        var haveFresh = _tracker.TryGetFreshCount(nowUtc, maxAge, out var players);

        // Ask the server for a refreshed count (its "List of players (N):" reply arrives on a later
        // console line and is consumed by PlayerCountTracker), UNLESS we already have a fresh reading
        // showing the server is populated - that avoids printing a full player list every tick on a
        // busy server. Near-empty or stale/unknown -> poll each tick so decisions stay current.
        // No-op if the console connection isn't up yet.
        if (!haveFresh || players <= _restartAtOrBelow)
            _localAdmin.RequestPlayerCountRefresh();

        // Don't act until the server has produced an authoritative readiness signal for this
        // session. Before that the count is "unknown" and restarting would loop forever.
        if (!_tracker.IsReady)
        {
            ResetStreak();
            return;
        }

        if (!haveFresh)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset (authoritative player count is not fresh yet).", ConsoleColor.Gray);
            ResetStreak();
            return;
        }

        _activity.AddSample(players, nowUtc, _windowMinutes);
        var activity = _activity.Evaluate(nowUtc, _windowMinutes, _overPlayers, _requireFullRecentWindow);

        if (players > _restartAtOrBelow)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset (players={players}).", ConsoleColor.Gray);
            ResetStreak();
            return;
        }

        if (!activity.WindowCovered)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset because recent activity window is not fully observed yet.", ConsoleColor.Gray);
            ResetStreak();
            ConsoleUtil.WriteLine($"{_label} blocked: recent activity window is not fully observed yet.", ConsoleColor.Gray);
            return;
        }

        if (activity.RecentHighActivity)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset because recent activity peaked at {activity.RecentMaxPlayers}.", ConsoleColor.Gray);
            ResetStreak();
            ConsoleUtil.WriteLine(
                $"{_label} blocked: recent activity peaked at {activity.RecentMaxPlayers}, above threshold {_overPlayers}.",
                ConsoleColor.Gray);
            return;
        }

        _lowSinceUtc ??= nowUtc;
        _lowPopStreak++;

        var sustainedSeconds = (int)(nowUtc - _lowSinceUtc.Value).TotalSeconds;
        ConsoleUtil.WriteLine(
            $"{_label}: low-pop confirmation {_lowPopStreak}/{_emptyConfirmations} (players={players}, threshold<={_restartAtOrBelow}, sustained={sustainedSeconds}s/{_durationSeconds}s, recentMax={activity.RecentMaxPlayers}, windowCovered={activity.WindowCovered}).",
            ConsoleColor.Gray);

        var confirmationsMet = _lowPopStreak >= _emptyConfirmations;
        var durationMet = _durationSeconds <= 0 || sustainedSeconds >= _durationSeconds;
        if (!confirmationsMet || !durationMet)
            return;

        // Cross-session cooldown floor: never restart-loop faster than the floor.
        if (s_lastAutoRestartUtc.HasValue &&
            nowUtc - s_lastAutoRestartUtc.Value < TimeSpan.FromSeconds(MinAutoRestartIntervalSeconds))
        {
            var waitSeconds = MinAutoRestartIntervalSeconds - (int)(nowUtc - s_lastAutoRestartUtc.Value).TotalSeconds;
            ConsoleUtil.WriteLine(
                $"{_label}: restart condition met but suppressed by cooldown (~{Math.Max(0, waitSeconds)}s remaining).",
                ConsoleColor.Gray);
            return;
        }

        BeginScheduledRestart(nowUtc, token);
    }

    private void ResetStreak()
    {
        _lowPopStreak = 0;
        _lowSinceUtc = null;
    }

    private void BeginScheduledRestart(DateTime nowUtc, CancellationToken token)
    {
        _restartRequestedAtUtc = nowUtc;
        _restartRequestAttempts = 1;

        ConsoleUtil.WriteLine(
            $"{_label} threshold reached; scheduling a graceful server restart. Will force restart if graceful exit has not completed within {GracefulRestartFallbackSeconds}s.",
            ConsoleColor.Cyan);

        if (_localAdmin.RequestScheduledRestart(_restartReason))
        {
            // Exit() cancels this watchdog's token under the lifecycle lock before
            // RequestScheduledRestart's early "already terminal" path can return true, so a
            // cancelled token here means an in-flight tick raced session teardown and the restart
            // belongs to someone else: do NOT stamp the cooldown or consume a one-shot intent for
            // an unrelated restart.
            if (token.IsCancellationRequested)
                return;

            s_lastAutoRestartUtc = nowUtc;

            // Only consume the one-shot intent once a Restart is actually the committed action. If a
            // non-restart shutdown/crash won the race, leave the intent so a crash-restart re-arms it.
            if (_localAdmin.ExitAction == LocalAdmin.ShutdownAction.Restart)
                _onRestartCommitted?.Invoke();
        }
        else
        {
            AbandonScheduledRestart($"{_label}: scheduled restart was not accepted because another shutdown action is already in progress.");
        }
    }

    private void ContinueScheduledRestart(DateTime nowUtc)
    {
        if (_restartFallbackAttempted || _restartAbandoned)
            return;

        var elapsed = nowUtc - _restartRequestedAtUtc!.Value;

        if (elapsed >= TimeSpan.FromSeconds(GracefulRestartFallbackSeconds))
        {
            _restartFallbackAttempted = true;
            if (!_localAdmin.ForceScheduledRestart(
                    $"{_restartReason}; graceful exit stalled for {(int)elapsed.TotalSeconds}s"))
                AbandonScheduledRestart($"{_label}: force fallback was skipped because another shutdown action is already in progress.");

            return;
        }

        _restartRequestAttempts++;
        var remainingSeconds = Math.Max(0, GracefulRestartFallbackSeconds - (int)elapsed.TotalSeconds);

        ConsoleUtil.WriteLine(
            $"{_label}: graceful restart still pending; retrying exit command (attempt {_restartRequestAttempts}, force fallback in ~{remainingSeconds}s).",
            ConsoleColor.Gray);

        if (!_localAdmin.RequestScheduledRestart($"{_restartReason} retry"))
            AbandonScheduledRestart($"{_label}: retry was not accepted because another shutdown action is already in progress.");
    }

    private void AbandonScheduledRestart(string message)
    {
        _restartAbandoned = true;
        _restartFallbackAttempted = true;
        ConsoleUtil.WriteLine(message, ConsoleColor.Yellow);
    }
}

internal sealed class RestartWhenEmptyOptions
{
    private const bool DefaultRequireFullRecentWindow = true;

    internal RestartWhenEmptyOptions(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes,
        string label,
        string restartReason,
        int durationSeconds = 0,
        bool requireFullRecentWindow = DefaultRequireFullRecentWindow)
    {
        RestartAtOrBelowPlayers = restartAtOrBelowPlayers;
        EmptyConfirmations = Math.Max(1, emptyConfirmations);
        TickSeconds = Math.Max(1, tickSeconds);
        DurationSeconds = Math.Max(0, durationSeconds);
        RecentActivityOverPlayers = recentActivityOverPlayers;
        RecentActivityWindowMinutes = recentActivityWindowMinutes;
        Label = label;
        RestartReason = restartReason;
        RequireFullRecentWindow = requireFullRecentWindow;
    }

    internal int RestartAtOrBelowPlayers { get; }
    internal int EmptyConfirmations { get; }
    internal int TickSeconds { get; }
    internal int DurationSeconds { get; }
    internal int RecentActivityOverPlayers { get; }
    internal int RecentActivityWindowMinutes { get; }
    internal string Label { get; }
    internal string RestartReason { get; }
    internal bool RequireFullRecentWindow { get; }

    internal string Summary =>
        FormatSummary(RestartAtOrBelowPlayers, EmptyConfirmations, TickSeconds, DurationSeconds, RecentActivityOverPlayers, RecentActivityWindowMinutes);

    internal static RestartWhenEmptyOptions FromConfig(Config config) =>
        new(
            config.RestartAtOrBelowPlayers,
            config.EmptyConfirmations,
            config.PolicyTickSeconds,
            config.RecentActivityOverPlayers,
            config.RecentActivityWindowMinutes,
            "Restart-when-empty watchdog",
            "restart-when-empty watchdog",
            config.EmptyDurationSeconds);

    internal static RestartWhenEmptyOptions OneShot(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes,
        int durationSeconds) =>
        new(
            restartAtOrBelowPlayers,
            emptyConfirmations,
            tickSeconds,
            recentActivityOverPlayers,
            recentActivityWindowMinutes,
            "One-shot restart-when-empty",
            "one-shot restart-when-empty command",
            durationSeconds);

    internal static string FormatSummary(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int durationSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes)
    {
        var summary =
            $"Restart at or below {restartAtOrBelowPlayers} players after {emptyConfirmations} confirmation(s)";

        if (durationSeconds > 0)
            summary += $" and at least {durationSeconds}s sustained";

        summary += $", polling every {tickSeconds}s";

        if (recentActivityWindowMinutes <= 0)
            return summary + "; recent-activity guard disabled.";

        return summary + $"; blocked if the last {recentActivityWindowMinutes} min peaked above {recentActivityOverPlayers} players.";
    }
}
