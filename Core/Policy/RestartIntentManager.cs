// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// The single registry for the pending one-shot restart intent. Exactly one intent can be armed
/// at a time; it persists across in-process child-game sessions (the manager outlives the
/// per-session LocalAdmin object) and is consumed only when a graceful restart is actually
/// committed - a lost race (crash/shutdown winning the exit) leaves the intent armed so the next
/// session retries instead of silently dropping it.
///
/// Firing protocol (ported from the original round-restart intent, the simpler of the two prior
/// dialects): a <see cref="_firing"/> flag blocks cancel between deciding to fire and consuming
/// the intent, and the intent is consumed in a try/finally only when the restart was claimed.
/// </summary>
internal sealed class RestartIntentManager
{
    private sealed class PendingIntent
    {
        internal PendingIntent(string presetName, RestartCondition condition)
        {
            PresetName = presetName;
            Condition = condition;
            RoundsRemaining = condition.Rounds;
        }

        internal string PresetName { get; }
        internal RestartCondition Condition { get; }
        internal int RoundsRemaining;
    }

    // How stale a player count may be and still gate a round-edge decision. Matches the
    // low-activity policy's freshness rule for its default 30s tick.
    private static readonly TimeSpan RoundGateMaxCountAge = TimeSpan.FromSeconds(75);

    // While a player-gated round intent is armed, poll the count on this cadence so a fresh
    // reading exists when the round-restart edge arrives.
    private static readonly TimeSpan GatePollInterval = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private PendingIntent? _pending;
    private bool _firing;

    // Live per-session machinery (watchdog or gate poller). Recreated by RearmForNewSession.
    private CancellationTokenSource? _liveCts;

    internal bool TryArm(
        IRestartHost host,
        PlayerCountTracker tracker,
        string presetName,
        RestartCondition condition,
        out string message)
    {
        if (!condition.Validate(out var error))
        {
            message = error;
            return false;
        }

        PendingIntent intent;
        lock (_lock)
        {
            if (host.IsShuttingDown)
            {
                message = "Cannot arm a restart intent while LocalAdmin is shutting down.";
                return false;
            }

            if (host.RestartInProgress)
            {
                message = "A server restart is already in progress; arm again after the server restarts.";
                return false;
            }

            if (_pending != null)
            {
                message = $"A restart intent (\"{_pending.PresetName}\") is already armed. Run \"restartwhen cancel\" first.";
                return false;
            }

            intent = new PendingIntent(presetName, condition);
            _pending = intent;
        }

        if (!TryStartLiveMachinery(host, tracker, intent, out error))
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pending, intent))
                    _pending = null;
            }

            message = error;
            return false;
        }

        // The pre-arm shutdown check runs under this manager's lock, but the host flips its
        // shutdown flag under its own lifecycle lock - an arm can pass the check and publish
        // live machinery after teardown already ran. Re-check under the lifecycle lock; if a
        // shutdown won the race, tear back down instead of leaking an orphan watchdog.
        if (!host.ConfirmNotShuttingDown())
        {
            StopLiveMachinery();
            lock (_lock)
            {
                if (ReferenceEquals(_pending, intent))
                    _pending = null;
            }

            message = "Cannot arm a restart intent while LocalAdmin is shutting down.";
            return false;
        }

        message = $"Restart intent \"{presetName}\" armed: restarts {condition.Describe()}. " +
                  "It persists across unrelated restarts until it fires or is canceled.";
        return true;
    }

    internal bool TryCancel(IRestartHost host, out string message)
    {
        lock (_lock)
        {
            if (_firing)
            {
                message = "The restart intent is firing right now; it can no longer be canceled.";
                return false;
            }

            if (_pending == null)
            {
                message = host.RestartInProgress
                    ? "The restart intent has already committed a restart; it can no longer be canceled."
                    : "No restart intent is armed.";
                return false;
            }

            _pending = null;
        }

        // Armed but not yet fired -> tear down any live watchdog/poller.
        StopLiveMachinery();
        message = "Restart intent canceled.";
        return true;
    }

    internal string GetStatus(PlayerCountTracker tracker)
    {
        PendingIntent? pending;
        bool firing;
        lock (_lock)
        {
            pending = _pending;
            firing = _firing;
        }

        if (pending == null)
            return "No restart intent is armed.";

        if (firing)
            return $"Restart intent \"{pending.PresetName}\" is firing now.";

        var players = tracker.IsReady && tracker.TryGetKnownCount(out var count)
            ? count.ToString(CultureInfo.InvariantCulture)
            : "not ready";

        var rounds = pending.Condition.Edge == RestartEdge.RoundEnd && pending.RoundsRemaining > 1
            ? $" {pending.RoundsRemaining} round restarts remain."
            : string.Empty;

        return $"Restart intent \"{pending.PresetName}\" is armed: restarts {pending.Condition.Describe()}.{rounds} " +
               $"Current player count: {players}.";
    }

    /// <summary>
    /// Round-restart edge (the host has already skipped the session's boot-time initial round).
    /// Counts the round, checks the player gate against a fresh count, and fires when satisfied.
    /// </summary>
    internal void NotifyRoundRestart(IRestartHost host, PlayerCountTracker tracker)
        => NotifyRoundRestart(host, tracker, DateTime.UtcNow);

    internal void NotifyRoundRestart(IRestartHost host, PlayerCountTracker tracker, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_pending is not { Condition.Edge: RestartEdge.RoundEnd } || _firing)
                return;

            if (_pending.RoundsRemaining > 1)
            {
                _pending.RoundsRemaining--;
                ConsoleUtil.WriteLine(
                    $"Round restart observed; {_pending.RoundsRemaining} more until the restart intent fires.",
                    ConsoleColor.Gray);
                return;
            }

            // Player gate: only fire when a FRESH count is at or below the threshold. No fresh
            // count -> skip this round (never act on stale data); too many players -> wait for a
            // later round. Either way the intent stays armed at 1 remaining round.
            var gate = _pending.Condition.MaxPlayers;
            if (gate.HasValue)
            {
                if (!tracker.TryGetFreshCount(nowUtc, RoundGateMaxCountAge, out var players))
                {
                    ConsoleUtil.WriteLine(
                        "Restart intent skipped this round: no fresh player count was available. Waiting for the next round.",
                        ConsoleColor.Yellow);
                    host.RequestPlayerCountRefresh();
                    return;
                }

                if (players > gate.Value)
                {
                    ConsoleUtil.WriteLine(
                        $"Restart intent skipped this round: {players} players online, gate is <= {gate.Value}. Waiting for the next round.",
                        ConsoleColor.Gray);
                    return;
                }
            }

            // Fire. Block cancel while committing; consume only if the restart is claimed.
            _firing = true;
        }

        ConsoleUtil.WriteLine("Round restart observed; performing the scheduled LocalAdmin restart now.", ConsoleColor.Cyan);

        var scheduled = false;
        try
        {
            scheduled = host.TryScheduleGracefulRestart("restart intent (round edge)");
        }
        finally
        {
            lock (_lock)
            {
                _firing = false;
                if (scheduled)
                    _pending = null; // claimed - consume the intent (host fallback forces if it stalls)
            }
        }

        if (!scheduled)
        {
            // Rejected, or a non-restart shutdown/crash won: leave it armed so the next round (or
            // a crash-restart's next session) retries instead of silently dropping the request.
            ConsoleUtil.WriteLine(
                "Restart intent could not be scheduled now (another shutdown action is in progress); leaving it armed.",
                ConsoleColor.Yellow);
        }
    }

    /// <summary>
    /// Restores live machinery for an intent carried over from a previous in-process session.
    /// </summary>
    internal void RearmForNewSession(IRestartHost host, PlayerCountTracker tracker)
    {
        PendingIntent? pending;
        lock (_lock)
            pending = _pending;

        if (pending == null)
            return;

        if (TryStartLiveMachinery(host, tracker, pending, out var error))
            ConsoleUtil.WriteLine(
                $"Re-armed carried-over restart intent \"{pending.PresetName}\" from a previous session.",
                ConsoleColor.Gray);
        else
            ConsoleUtil.WriteLine(
                $"Failed to re-arm carried-over restart intent: {error} The intent is still saved; it will retry " +
                "after the next restart, or run \"restartwhen cancel\" to clear it.",
                ConsoleColor.Yellow);
    }

    /// <summary>
    /// Tears down only the LIVE per-session machinery; the pending intent is preserved so
    /// <see cref="RearmForNewSession"/> can restore it in the next session.
    /// </summary>
    internal void StopForSessionEnd() => StopLiveMachinery();

    private bool TryStartLiveMachinery(IRestartHost host, PlayerCountTracker tracker, PendingIntent intent, out string error)
    {
        StopLiveMachinery();

        var cts = new CancellationTokenSource();
        var token = cts.Token; // capture before a concurrent cancel could dispose the source

        try
        {
            switch (intent.Condition.Edge)
            {
                case RestartEdge.LowActivityTick:
                    var policy = new RestartWhenEmptyPolicy(
                        host,
                        tracker,
                        RestartWhenEmptyOptions.FromCondition(intent.Condition),
                        onRestartCommitted: () => Consume(intent));
                    policy.Start(token);
                    break;

                case RestartEdge.RoundEnd when intent.Condition.MaxPlayers.HasValue:
                    StartGatePoller(host, token);
                    break;

                // Ungated round intent: the round-restart byte is the only input; nothing to run.
            }
        }
        catch (Exception ex)
        {
            cts.Dispose();
            error = $"Failed to start the restart intent's watchdog: {ex.Message}.";
            return false;
        }

        // Publish the live CTS only if this exact intent is still the pending one. If a cancel
        // ran while we were starting, tear the machinery back down so no orphan can restart the
        // server after "cancel" reported success.
        lock (_lock)
        {
            if (ReferenceEquals(_pending, intent))
            {
                _liveCts = cts;
                error = string.Empty;
                return true;
            }
        }

        try { cts.Cancel(); }
        catch { /* ignore */ }
        cts.Dispose();
        error = "The restart intent was canceled before it finished arming.";
        return false;
    }

    // Invoked by the low-activity watchdog the moment it commits a restart: drop the persistent
    // intent so the relaunched session does not re-arm it (the one-shot has done its job).
    private void Consume(PendingIntent intent)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pending, intent))
                _pending = null;
        }
    }

    private void StopLiveMachinery()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _liveCts;
            _liveCts = null;
        }

        if (cts == null)
            return;

        try { cts.Cancel(); }
        catch { /* ignore */ }
        cts.Dispose();
    }

    /// <summary>
    /// Keeps a fresh authoritative player count available while a player-gated round intent is
    /// armed, so the gate can be evaluated the moment the round-restart edge arrives.
    /// </summary>
    private static void StartGatePoller(IRestartHost host, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        host.RequestPlayerCountRefresh();
                    }
                    catch
                    {
                        // best-effort; never let the poller die on a transient console error
                    }

                    await Task.Delay(GatePollInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // session ended - normal shutdown of the poller
            }
        }, token);
    }
}
