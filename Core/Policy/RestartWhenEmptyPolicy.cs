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
/// duration), guarded by a rolling recent-activity window.
///
/// Restart decisions act ONLY on a FRESH authoritative player count (the server's reply to the
/// <c>players</c> command, which this watchdog polls each tick, or the game's IdleEnter byte); a
/// stale or unknown count never triggers a restart. The restart itself is delegated to
/// <see cref="IRestartHost.TryScheduleGracefulRestart"/>, which owns the native-restart force
/// fallback; an independent cross-session cooldown (<see cref="s_lastAutoRestartUtc"/>) caps how
/// often the watchdog can restart, so an always-empty server cannot restart-loop tightly.
/// </summary>
internal sealed class RestartWhenEmptyPolicy
{
    // Cross-session floor between watchdog-initiated restarts. Readiness gating already forces a
    // full boot between restarts; this is the belt-and-suspenders cap that also covers very
    // aggressive configs (e.g. window=0, confirmations=1, tick=1). Manual restarts are unaffected.
    private const int MinAutoRestartIntervalSeconds = 60;
    private static DateTime? s_lastAutoRestartUtc;

    private readonly IRestartHost _host;
    private readonly PlayerCountTracker _tracker;
    private readonly RecentActivityWindow _activity = new();
    private readonly RestartWhenEmptyOptions _options;
    private readonly Action? _onRestartCommitted;

    private int _lowPopStreak;
    private DateTime? _lowSinceUtc;
    private bool _done;

    internal RestartWhenEmptyPolicy(
        IRestartHost host,
        PlayerCountTracker tracker,
        RestartWhenEmptyOptions options,
        Action? onRestartCommitted = null)
    {
        _host = host;
        _tracker = tracker;
        _options = options;
        _onRestartCommitted = onRestartCommitted;
    }

    /// <summary>
    /// Starts the watchdog loop bound to <paramref name="token"/>. The loop self-terminates when
    /// the token is cancelled (the session ends) so a stale watchdog from a previous session can
    /// never act on a new one.
    /// </summary>
    internal void Start(CancellationToken token)
    {
        ConsoleUtil.WriteLine($"{_options.Label} started. {_options.Summary}", ConsoleColor.Gray);

        async Task LoopAsync()
        {
            try
            {
                // Small initial settle so the game has a chance to come up before we sample.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_options.TickSeconds, 5)), token).ConfigureAwait(false);

                while (!token.IsCancellationRequested && !_done)
                {
                    try
                    {
                        Tick(token);
                    }
                    catch (Exception ex)
                    {
                        // Never let the watchdog kill the process; just log and reset the streak.
                        ConsoleUtil.WriteLine($"{_options.Label} tick failed: {ex.Message}", ConsoleColor.Yellow);
                        ResetStreak();
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_options.TickSeconds), token).ConfigureAwait(false);
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

        // Only act on a FRESH authoritative reading (recent players-list reply or idle byte).
        var maxAge = TimeSpan.FromSeconds((_options.TickSeconds * 2) + 15);
        var haveFresh = _tracker.TryGetFreshCount(nowUtc, maxAge, out var players);

        // Ask the server for a refreshed count (its "List of players (N):" reply arrives on a later
        // console line and is consumed by PlayerCountTracker), UNLESS we already have a fresh reading
        // showing the server is populated - that avoids printing a full player list every tick on a
        // busy server. Near-empty or stale/unknown -> poll each tick so decisions stay current.
        // No-op if the console connection isn't up yet.
        if (!haveFresh || players <= _options.RestartAtOrBelowPlayers)
            _host.RequestPlayerCountRefresh();

        // Don't act until the server has produced an authoritative readiness signal for this
        // session. Before that the count is "unknown" and restarting would loop forever.
        if (!_tracker.IsReady)
        {
            ResetStreak();
            return;
        }

        if (!haveFresh)
        {
            ResetStreak("authoritative player count is not fresh yet");
            return;
        }

        _activity.AddSample(players, nowUtc, _options.RecentActivityWindowMinutes);
        var activity = _activity.Evaluate(
            nowUtc, _options.RecentActivityWindowMinutes, _options.RecentActivityOverPlayers, _options.RequireFullRecentWindow);

        if (players > _options.RestartAtOrBelowPlayers)
        {
            ResetStreak($"players={players}");
            return;
        }

        if (!activity.WindowCovered)
        {
            ResetStreak("recent activity window is not fully observed yet");
            ConsoleUtil.WriteLine($"{_options.Label} blocked: recent activity window is not fully observed yet.", ConsoleColor.Gray);
            return;
        }

        if (activity.RecentHighActivity)
        {
            ResetStreak($"recent activity peaked at {activity.RecentMaxPlayers}");
            ConsoleUtil.WriteLine(
                $"{_options.Label} blocked: recent activity peaked at {activity.RecentMaxPlayers}, above threshold {_options.RecentActivityOverPlayers}.",
                ConsoleColor.Gray);
            return;
        }

        _lowSinceUtc ??= nowUtc;
        _lowPopStreak++;

        var sustainedSeconds = (int)(nowUtc - _lowSinceUtc.Value).TotalSeconds;
        ConsoleUtil.WriteLine(
            $"{_options.Label}: low-pop confirmation {_lowPopStreak}/{_options.EmptyConfirmations} (players={players}, threshold<={_options.RestartAtOrBelowPlayers}, sustained={sustainedSeconds}s/{_options.DurationSeconds}s, recentMax={activity.RecentMaxPlayers}, windowCovered={activity.WindowCovered}).",
            ConsoleColor.Gray);

        var confirmationsMet = _lowPopStreak >= _options.EmptyConfirmations;
        var durationMet = _options.DurationSeconds <= 0 || sustainedSeconds >= _options.DurationSeconds;
        if (!confirmationsMet || !durationMet)
            return;

        // Cross-session cooldown floor: never restart-loop faster than the floor.
        if (s_lastAutoRestartUtc.HasValue &&
            nowUtc - s_lastAutoRestartUtc.Value < TimeSpan.FromSeconds(MinAutoRestartIntervalSeconds))
        {
            var waitSeconds = MinAutoRestartIntervalSeconds - (int)(nowUtc - s_lastAutoRestartUtc.Value).TotalSeconds;
            ConsoleUtil.WriteLine(
                $"{_options.Label}: restart condition met but suppressed by cooldown (~{Math.Max(0, waitSeconds)}s remaining).",
                ConsoleColor.Gray);
            return;
        }

        CommitRestart(nowUtc, token);
    }

    private void CommitRestart(DateTime nowUtc, CancellationToken token)
    {
        ConsoleUtil.WriteLine(
            $"{_options.Label} threshold reached; scheduling native rnr then restart (force fallback applies if the native restart stalls).",
            ConsoleColor.Cyan);

        if (_host.TryScheduleGracefulRestart(_options.RestartReason))
        {
            _done = true;

            // Exit() cancels this watchdog's token under the lifecycle lock before the host's
            // early "already terminal" path can return true, so a cancelled token here means an
            // in-flight tick raced session teardown and the restart belongs to someone else: do
            // NOT stamp the cooldown or consume a one-shot intent for an unrelated restart.
            if (token.IsCancellationRequested)
                return;

            s_lastAutoRestartUtc = nowUtc;
            _onRestartCommitted?.Invoke();
        }
        else
        {
            _done = true;
            ConsoleUtil.WriteLine(
                $"{_options.Label}: scheduled restart was not accepted because another shutdown action is already in progress.",
                ConsoleColor.Yellow);
        }
    }

    private void ResetStreak(string? reason = null)
    {
        if (_lowPopStreak != 0 && reason != null)
            ConsoleUtil.WriteLine($"{_options.Label}: low-pop streak reset ({reason}).", ConsoleColor.Gray);

        _lowPopStreak = 0;
        _lowSinceUtc = null;
    }
}
