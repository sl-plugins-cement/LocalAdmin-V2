// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Background watchdog that gracefully restarts the server once it has been empty
/// (or below a threshold) for a configurable number of consecutive ticks, guarded by a
/// rolling recent-activity window. Modeled on LocalAdmin's StartHeartbeatMonitoring().
///
/// The restart itself is delegated to <see cref="LocalAdmin.RequestScheduledRestart"/>
/// which marks the next restart as expected so the restart-storm guard is not tripped.
/// </summary>
internal sealed class RestartWhenEmptyPolicy
{
    private const int GracefulRestartRetrySeconds = 10;
    private const int GracefulRestartFallbackSeconds = 60;

    private readonly LocalAdmin _localAdmin;
    private readonly PlayerCountTracker _tracker;
    private readonly RecentActivityWindow _activity;

    private readonly int _restartAtOrBelow;
    private readonly int _emptyConfirmations;
    private readonly int _tickSeconds;
    private readonly int _overPlayers;
    private readonly int _windowMinutes;
    private readonly bool _requireFullRecentWindow;
    private readonly string _label;
    private readonly string _restartReason;

    private int _lowPopStreak;
    private DateTime? _restartRequestedAtUtc;
    private int _restartRequestAttempts;
    private bool _restartFallbackAttempted;
    private bool _restartAbandoned;

    internal RestartWhenEmptyPolicy(LocalAdmin localAdmin, PlayerCountTracker tracker, Config config)
        : this(localAdmin, tracker, RestartWhenEmptyOptions.FromConfig(config))
    {
    }

    internal RestartWhenEmptyPolicy(LocalAdmin localAdmin, PlayerCountTracker tracker, RestartWhenEmptyOptions options)
    {
        _localAdmin = localAdmin;
        _tracker = tracker;
        _activity = new RecentActivityWindow();

        _restartAtOrBelow = options.RestartAtOrBelowPlayers;
        _emptyConfirmations = options.EmptyConfirmations;
        _tickSeconds = options.TickSeconds;
        _overPlayers = options.RecentActivityOverPlayers;
        _windowMinutes = options.RecentActivityWindowMinutes;
        _requireFullRecentWindow = options.RequireFullRecentWindow;
        _label = options.Label;
        _restartReason = options.RestartReason;
    }

    /// <summary>
    /// Starts the watchdog loop bound to <paramref name="token"/>. The loop self-terminates
    /// when the token is cancelled (the session ends) so a stale watchdog from a previous
    /// session can never act on a new one.
    /// </summary>
    internal void Start(CancellationToken token)
    {
        ConsoleUtil.WriteLine(
            $"{_label} started. {RestartWhenEmptyOptions.FormatSummary(_restartAtOrBelow, _emptyConfirmations, _tickSeconds, _overPlayers, _windowMinutes)}",
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
                        Tick();
                    }
                    catch (Exception ex)
                    {
                        // Never let the watchdog kill the process; just log and reset the streak.
                        ConsoleUtil.WriteLine($"{_label} tick failed: {ex.Message}", ConsoleColor.Yellow);
                        _lowPopStreak = 0;
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

    private void Tick()
    {
        var nowUtc = DateTime.UtcNow;

        if (_restartRequestedAtUtc.HasValue)
        {
            ContinueScheduledRestart(nowUtc);
            return;
        }

        if (_restartAbandoned)
            return;

        // Don't act until the server has produced an authoritative readiness signal for this
        // session (idle byte or a real player join). Before that the count is "unknown" and a
        // reported 0 just means the game is still booting - restarting then would loop forever.
        if (!_tracker.IsReady)
        {
            if (_lowPopStreak != 0)
                _lowPopStreak = 0;
            return;
        }

        if (!_tracker.TryGetKnownCount(out var players))
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset (player count unknown while server is non-idle).", ConsoleColor.Gray);
            _lowPopStreak = 0;
            return;
        }

        _activity.AddSample(players, nowUtc, _windowMinutes);
        var activity = _activity.Evaluate(nowUtc, _windowMinutes, _overPlayers, _requireFullRecentWindow);

        if (players > _restartAtOrBelow)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset (players={players}).", ConsoleColor.Gray);
            _lowPopStreak = 0;
            return;
        }

        if (!activity.WindowCovered)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset because recent activity window is not fully observed yet.", ConsoleColor.Gray);
            _lowPopStreak = 0;
            ConsoleUtil.WriteLine($"{_label} blocked: recent activity window is not fully observed yet.", ConsoleColor.Gray);
            return;
        }

        if (activity.RecentHighActivity)
        {
            if (_lowPopStreak != 0)
                ConsoleUtil.WriteLine($"{_label}: low-pop streak reset because recent activity peaked at {activity.RecentMaxPlayers}.", ConsoleColor.Gray);
            _lowPopStreak = 0;
            ConsoleUtil.WriteLine(
                $"{_label} blocked: recent activity peaked at {activity.RecentMaxPlayers}, above threshold {_overPlayers}.",
                ConsoleColor.Gray);
            return;
        }

        _lowPopStreak++;
        ConsoleUtil.WriteLine(
            $"{_label}: low-pop confirmation {_lowPopStreak}/{_emptyConfirmations} (players={players}, threshold<={_restartAtOrBelow}, recentMax={activity.RecentMaxPlayers}, windowCovered={activity.WindowCovered}).",
            ConsoleColor.Gray);

        if (_lowPopStreak < _emptyConfirmations)
            return;

        BeginScheduledRestart(nowUtc);
    }

    private void BeginScheduledRestart(DateTime nowUtc)
    {
        _restartRequestedAtUtc = nowUtc;
        _restartRequestAttempts = 1;

        ConsoleUtil.WriteLine(
            $"{_label} threshold reached; scheduling a graceful server restart. Will force restart if graceful exit has not completed within {GracefulRestartFallbackSeconds}s.",
            ConsoleColor.Cyan);

        if (!_localAdmin.RequestScheduledRestart(_restartReason))
            AbandonScheduledRestart($"{_label}: scheduled restart was not accepted because another shutdown action is already in progress.");
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
        bool requireFullRecentWindow = DefaultRequireFullRecentWindow)
    {
        RestartAtOrBelowPlayers = restartAtOrBelowPlayers;
        EmptyConfirmations = Math.Max(1, emptyConfirmations);
        TickSeconds = Math.Max(1, tickSeconds);
        RecentActivityOverPlayers = recentActivityOverPlayers;
        RecentActivityWindowMinutes = recentActivityWindowMinutes;
        Label = label;
        RestartReason = restartReason;
        RequireFullRecentWindow = requireFullRecentWindow;
    }

    internal int RestartAtOrBelowPlayers { get; }
    internal int EmptyConfirmations { get; }
    internal int TickSeconds { get; }
    internal int RecentActivityOverPlayers { get; }
    internal int RecentActivityWindowMinutes { get; }
    internal string Label { get; }
    internal string RestartReason { get; }
    internal bool RequireFullRecentWindow { get; }

    internal string Summary =>
        FormatSummary(RestartAtOrBelowPlayers, EmptyConfirmations, TickSeconds, RecentActivityOverPlayers, RecentActivityWindowMinutes);

    internal static RestartWhenEmptyOptions FromConfig(Config config) =>
        new(
            config.RestartAtOrBelowPlayers,
            config.EmptyConfirmations,
            config.PolicyTickSeconds,
            config.RecentActivityOverPlayers,
            config.RecentActivityWindowMinutes,
            "Restart-when-empty watchdog",
            "restart-when-empty watchdog");

    internal static RestartWhenEmptyOptions OneShot(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes) =>
        new(
            restartAtOrBelowPlayers,
            emptyConfirmations,
            tickSeconds,
            recentActivityOverPlayers,
            recentActivityWindowMinutes,
            "One-shot restart-when-empty",
            "one-shot restart-when-empty command");

    internal static string FormatSummary(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes)
    {
        var summary =
            $"Restart at or below {restartAtOrBelowPlayers} players after {emptyConfirmations} confirmation(s), polling every {tickSeconds}s";

        if (recentActivityWindowMinutes <= 0)
            return summary + "; recent-activity guard disabled.";

        return summary + $"; blocked if the last {recentActivityWindowMinutes} min peaked above {recentActivityOverPlayers} players.";
    }
}
