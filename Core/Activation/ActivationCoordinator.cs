// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.Core.Policy;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Core.Activation;

internal interface IActivationHost : IRestartHost
{
    bool GameProcessRunning { get; }
    bool GameConsoleConnected { get; }

    /// <summary>False when LocalAdmin's heartbeat monitoring is disabled, so no heartbeat can be observed.</summary>
    bool HeartbeatMonitoringAvailable { get; }

    bool StartupHeartbeatReady { get; }
    bool IsGameUdpPortBound();
    Task<PlayerProbeResult> ProbePlayersAsync(TimeSpan timeout, CancellationToken cancellationToken);
    bool TryScheduleManagedActivationRestart(string reason, Func<bool> commitActivation);
    void ForceActivationSessionRestart(string reason);

    /// <summary>
    /// Arms the game's native "restart next round" flag unless it is already armed (rnr is a
    /// toggle). Returns true when the flag is armed after the call.
    /// </summary>
    bool EnsureNativeRestartNextRoundArmed();

    /// <summary>Disarms the native flag only when LocalAdmin armed it. Returns true when a disarm was sent.</summary>
    bool DisarmNativeRestartNextRoundIfOwned();

    string DescribeNativeRestartNextRound();
}

/// <param name="NeedsRepair">The lane needs operator attention. The game still starts.</param>
internal sealed record ActivationResumeResult(bool NeedsRepair, string Message, ActivationRecord? Record);

/// <summary>
/// Durable activation state machine. It supervises the game and invokes fixed scpsl-ops hooks; it
/// never interprets checkpoint manifests or writes managed game files itself.
/// It never keeps the game stopped: LabAPI already skips a plugin that fails to load, so a failed
/// check or an unreadable record is reported for repair while the server keeps serving. A candidate
/// is rolled back only on evidence against it, never because the verification tooling failed.
/// </summary>
internal sealed class ActivationCoordinator
{
    private readonly ActivationRecordStore _store;
    private readonly IActivationHookRunner _hooks;
    private readonly IActivationHost _host;
    private readonly PlayerCountTracker _tracker;
    private readonly RestartIntentManager _restartIntents = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private int _verificationRunning;

    // The target this session's child is under verification for. Cleared when the tooling fails,
    // so a later crash follows ordinary crash handling instead of rolling the candidate back.
    private volatile string? _runningVerificationTarget;

    internal ActivationCoordinator(
        ActivationRecordStore store,
        IActivationHookRunner hooks,
        IActivationHost host,
        PlayerCountTracker tracker)
    {
        _store = store;
        _hooks = hooks;
        _host = host;
        _tracker = tracker;
    }

    internal ActivationRecordStore Store => _store;

    // A candidate the tooling could not verify keeps running and is verified again on this
    // schedule, each attempt with a fresh readiness deadline.
    internal TimeSpan VerificationRetryInterval { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan VerificationRetryWindow { get; init; } = TimeSpan.FromMinutes(30);

    internal async Task<ActivationResumeResult> ResumeBeforeChildAsync(CancellationToken cancellationToken = default)
    {
        ActivationRecord? record;
        try
        {
            record = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ActivationProtocolException ex)
        {
            return new(true, ex.Message, null);
        }

        if (record == null)
            return new(true, "Managed activation is enabled but activation.json does not exist. Bootstrap it through scpsl-ops.", null);

        while (true)
        {
            switch (record.State)
            {
                case ActivationStates.RecoveryRequired:
                    return new(true, record.LastError ?? "Recovery is required.", record);

                case ActivationStates.Cancelled:
                    // Nothing was applied; the staging cleanup runs once the game is up.
                    return new(false, "Cancelled activation cleanup runs after the game starts.", record);

                case ActivationStates.RolledBack:
                    record = await _store.UpdateAsync(record.Revision, current => ClearOpenActivation(current!, current!.PreviousCheckpoint!), cancellationToken).ConfigureAwait(false);
                    continue;

                case ActivationStates.Activating:
                {
                    var result = await RunHookSafeAsync("apply-target", record, cancellationToken).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        record = await EnterRollbackAsync(record, result.Error ?? "target apply failed", result.Checks, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    record = await _store.UpdateAsync(record.Revision, current => current! with
                    {
                        State = ActivationStates.Verifying,
                        CurrentCheckpoint = current.PendingCheckpoint,
                        VerificationTarget = "pending",
                        Checks = Append(current.Checks, result.Checks),
                        LastError = null
                    }, cancellationToken).ConfigureAwait(false);
                    return new(false, "Target files applied; starting readiness verification.", record);
                }

                case ActivationStates.RollingBack:
                {
                    var result = await RunHookSafeAsync("apply-rollback", record, cancellationToken).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        record = await EnterRecoveryAsync(record, result.Error ?? "rollback apply failed", result.Checks, cancellationToken).ConfigureAwait(false);
                        return new(true, record.LastError!, record);
                    }

                    if (result.Checkpoint != record.PreviousCheckpoint)
                    {
                        record = await EnterRecoveryAsync(record, "rollback hook returned the wrong checkpoint", result.Checks, cancellationToken).ConfigureAwait(false);
                        return new(true, record.LastError!, record);
                    }

                    record = await _store.UpdateAsync(record.Revision, current => current! with
                    {
                        CurrentCheckpoint = current.PreviousCheckpoint,
                        Checks = Append(current.Checks, result.Checks)
                    }, cancellationToken).ConfigureAwait(false);
                    return new(false, "Previous checkpoint restored; starting rollback readiness verification.", record);
                }

                default:
                    return new(false, $"Activation state is {record.State}.", record);
            }
        }
    }

    internal async Task OnChildStartedAsync(CancellationToken cancellationToken = default)
    {
        var record = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (record == null)
            return;

        if (record.State == ActivationStates.Pending)
        {
            ArmPending(record);
            return;
        }

        if (record.State == ActivationStates.Cancelled)
        {
            var cleanupToken = _sessionCts.Token;
            _ = Task.Run(async () =>
            {
                try { await CleanupCancelledAsync(record, cleanupToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cleanupToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    ConsoleUtil.WriteLine($"Cancelled activation cleanup failed; the lane stays cancelled: {ex.Message}", ConsoleColor.Yellow);
                }
            }, cleanupToken);
            return;
        }

        if (record.State is ActivationStates.Verifying or ActivationStates.RollingBack &&
            Interlocked.Exchange(ref _verificationRunning, 1) == 0)
        {
            _runningVerificationTarget = record.State == ActivationStates.RollingBack ? "previous" : "pending";
            var verificationToken = _sessionCts.Token;
            _ = Task.Run(async () =>
            {
                try { await SuperviseVerificationAsync(record, verificationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (verificationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    ConsoleUtil.WriteLine($"Managed activation verification failed: {ex.Message}", ConsoleColor.Red);
                }
                finally { Interlocked.Exchange(ref _verificationRunning, 0); }
            }, verificationToken);
        }
    }

    internal async Task<bool> OnChildExitedAsync(CancellationToken cancellationToken = default)
    {
        var target = _runningVerificationTarget;
        if (target == null)
            return false;

        var record = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (record == null)
            return false;

        if (target == "pending" && record.State == ActivationStates.Verifying)
        {
            await EnterRollbackAsync(record, "candidate game child exited during readiness", Array.Empty<ActivationCheck>(), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (target == "previous" && record.State == ActivationStates.RollingBack)
        {
            await EnterRecoveryAsync(record, "rollback target game child exited during readiness", Array.Empty<ActivationCheck>(), cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    internal void NotifyRoundRestart() => _restartIntents.NotifyRoundRestart(new ActivationRestartHost(this, _host), _tracker);

    internal void NotifyIdleEnter() => _restartIntents.NotifyIdleEnter(new ActivationRestartHost(this, _host), _tracker);

    internal void StopForSessionEnd()
    {
        _restartIntents.StopForSessionEnd();
        try { _sessionCts.Cancel(); }
        catch { /* session teardown */ }
    }

    internal async Task<ActivationRecord?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    internal async Task<ActivationRecord> BootstrapAsync(
        string activationId,
        string checkpoint,
        ActivationReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        var now = ActivationRecordStore.FormatTimestamp(DateTime.UtcNow);
        return await _store.UpdateAsync(-1, _ => new ActivationRecord
        {
            ProtocolVersion = 1,
            Revision = 0,
            Lane = _store.Lane,
            State = ActivationStates.Verified,
            ActivationId = activationId,
            CurrentCheckpoint = checkpoint,
            PendingCheckpoint = null,
            PreviousCheckpoint = null,
            VerificationTarget = null,
            RollbackAttempted = false,
            Lock = null,
            Restart = null,
            Readiness = readiness,
            Checks = new(),
            PreparedAt = now,
            UpdatedAt = now,
            LastError = null
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ActivationRecord> PrepareAsync(
        string activationId,
        long expectedRevision,
        string pendingCheckpoint,
        string holder,
        ActivationRestart restart,
        ActivationReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireStateAsync(ActivationStates.Verified, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
            throw new ActivationProtocolException($"Expected revision {expectedRevision}, found {current.Revision}.");
        var now = ActivationRecordStore.FormatTimestamp(DateTime.UtcNow);
        return await _store.UpdateAsync(current.Revision, record => record! with
        {
            State = ActivationStates.Prepared,
            ActivationId = activationId,
            PendingCheckpoint = pendingCheckpoint,
            PreviousCheckpoint = record.CurrentCheckpoint,
            VerificationTarget = null,
            RollbackAttempted = false,
            Lock = new ActivationLock
            {
                Id = activationId,
                Holder = holder,
                AcquiredAt = now
            },
            Restart = restart,
            Readiness = readiness,
            Checks = new(),
            PreparedAt = now,
            LastError = null
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ActivationRecord> ArmAsync(string activationId, CancellationToken cancellationToken = default)
    {
        var prepared = await RequireActivationAsync(activationId, ActivationStates.Prepared, cancellationToken).ConfigureAwait(false);
        // Run with LocalAdmin's identity while the current game is still available. A failed
        // preflight leaves the reservation cancellable and never schedules a shutdown.
        var preflight = await RunHookSafeAsync("preflight", prepared, cancellationToken).ConfigureAwait(false);
        if (!preflight.Ok)
            throw new ActivationProtocolException($"Deployment preflight failed before restart: {preflight.Error}");
        return await _store.UpdateAsync(prepared.Revision, record => record! with { State = ActivationStates.Pending }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ActivationRecord> ReplacePendingAsync(string previousActivationId,
        string previousCheckpoint, string activationId, string pendingCheckpoint, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireActivationAsync(previousActivationId, ActivationStates.Pending, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision || current.PendingCheckpoint != previousCheckpoint)
            throw new ActivationProtocolException("The pending activation changed; prepare a fresh hotfix addition.");
        if (current.Restart is not { Preset: "next-round", Edge: "round-end", Rounds: 1, MaxPlayers: null })
            throw new ActivationProtocolException("Only an armed next-round activation accepts additions.");
        if (!Guid.TryParseExact(activationId, "D", out var id) || id == Guid.Empty || activationId == previousActivationId)
            throw new ActivationProtocolException("Replacement requires a fresh activation UUID.");
        var candidate = current with
        {
            ProtocolVersion = 2,
            ActivationId = activationId,
            PendingCheckpoint = pendingCheckpoint,
            Lock = current.Lock! with { Id = activationId }
        };
        var preflight = await RunHookSafeAsync("preflight-replacement", candidate, cancellationToken).ConfigureAwait(false);
        if (!preflight.Ok)
            throw new ActivationProtocolException($"Pending replacement preflight failed: {preflight.Error}");
        // The store's compare-and-swap competes with the round-edge transition. The existing
        // restart manager keeps its counters and reads the winning record when it fires.
        return await _store.UpdateAsync(current.Revision, _ => candidate, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ActivationRecord> CancelAsync(string activationId, CancellationToken cancellationToken = default)
    {
        var current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new ActivationProtocolException("activation.json does not exist.");
        if (current.ActivationId != activationId ||
            current.State is not (ActivationStates.Prepared or ActivationStates.Pending or ActivationStates.Cancelled))
        {
            throw new ActivationProtocolException("Only the matching prepared, pending, or cancelled activation can be cancelled.");
        }

        // A record left in `cancelled` because staging cleanup failed used to wait for the next
        // LocalAdmin session, holding the lane lock meanwhile. Cancelling it again retries the
        // cleanup in place so the lane can return to verified without a restart.
        var cancelled = current.State == ActivationStates.Cancelled
            ? current
            : await _store.UpdateAsync(current.Revision, record => record! with { State = ActivationStates.Cancelled }, cancellationToken).ConfigureAwait(false);
        ReleaseRestartEdge();
        var result = await RunHookSafeAsync("cleanup", cancelled, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
            return cancelled;
        return await _store.UpdateAsync(cancelled.Revision, record => ClearOpenActivation(record!, record!.CurrentCheckpoint!), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a pending activation to another restart edge without re-staging anything: the staged
    /// checkpoint, lock and provenance stay; only the restart intent changes. <c>immediate</c>
    /// fires the native rnr+restart sequence right away; <c>round-end</c> arms the native
    /// restart-next-round flag. The record is updated before anything fires, so a LocalAdmin
    /// restart in between resumes with the new edge.
    /// </summary>
    internal async Task<ActivationRecord> RescheduleAsync(
        string activationId,
        long expectedRevision,
        string edge,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireActivationAsync(activationId, ActivationStates.Pending, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
            throw new ActivationProtocolException($"Expected revision {expectedRevision}, found {current.Revision}.");
        var restart = edge switch
        {
            "immediate" => new ActivationRestart { Preset = "immediate", Edge = "immediate", MaxPlayers = null, Rounds = null },
            "round-end" => new ActivationRestart { Preset = "next-round", Edge = "round-end", MaxPlayers = null, Rounds = 1 },
            _ => throw new ActivationProtocolException("Reschedule edge must be immediate or round-end.")
        };
        if (current.Restart is { } existing && existing.Edge == restart.Edge && existing.MaxPlayers is null)
            throw new ActivationProtocolException($"The pending activation already waits for the {edge} edge.");

        var updated = await _store.UpdateAsync(current.Revision, record => record! with { Restart = restart }, cancellationToken).ConfigureAwait(false);
        // Both target edges want the native flag armed, so an rnr LocalAdmin already sent stays;
        // only this session's LocalAdmin-side intent is replaced.
        ReleaseRestartEdge(disarmNativeRnr: false);
        ArmPending(updated);
        return await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? updated;
    }

    /// <summary>
    /// The game is exiting for a restart (native rnr at round end, restart_after_rounds, or the
    /// rnr+restart sequence LocalAdmin sent). That restart is the edge a pending next-round or
    /// immediate activation waits for, so commit it as activating before the relaunch applies the
    /// staged files. A crash exit never commits; the next session re-arms the pending record.
    /// </summary>
    internal async Task<bool> CommitPendingForRestartExitAsync(CancellationToken cancellationToken = default)
    {
        var record = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (record is not { State: ActivationStates.Pending, Restart.Edge: "round-end" or "immediate" })
            return false;
        await _store.UpdateAsync(record.Revision, current => current! with { State = ActivationStates.Activating }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Drops this session's restart intent and, when asked, any native rnr LocalAdmin itself armed.</summary>
    private void ReleaseRestartEdge(bool disarmNativeRnr = true)
    {
        _restartIntents.TryCancel(new ActivationRestartHost(this, _host), out _);
        _restartIntents.StopForSessionEnd();
        if (disarmNativeRnr && _host.DisarmNativeRestartNextRoundIfOwned())
            ConsoleUtil.WriteLine("Native restart-next-round armed by LocalAdmin was disarmed.", ConsoleColor.Gray);
    }

    private async Task CleanupCancelledAsync(ActivationRecord record, CancellationToken cancellationToken)
    {
        var result = await RunHookSafeAsync("cleanup", record, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            ConsoleUtil.WriteLine($"Staging cleanup failed; the lane stays cancelled and locked: {result.Error}", ConsoleColor.Yellow);
            return;
        }

        await _store.UpdateAsync(record.Revision, current => ClearOpenActivation(current!, current!.CurrentCheckpoint!), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ActivationRecord> AdoptRecoveryAsync(
        string activationId,
        string checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var recovery = await RequireActivationAsync(activationId, ActivationStates.RecoveryRequired, cancellationToken).ConfigureAwait(false);
        if (recovery.Revision != expectedRevision)
            throw new ActivationProtocolException($"Expected revision {expectedRevision}, found {recovery.Revision}.");
        return await _store.UpdateAsync(recovery.Revision, record => ClearOpenActivation(record!, checkpoint), cancellationToken).ConfigureAwait(false);
    }

    private void ArmPending(ActivationRecord record)
    {
        var restart = record.Restart!;
        var host = new ActivationRestartHost(this, _host);
        if (restart.Edge == "immediate")
        {
            if (!host.TryScheduleGracefulRestart("managed activation (immediate)"))
                ConsoleUtil.WriteLine("Managed activation could not claim the immediate restart.", ConsoleColor.Yellow);
            return;
        }

        var customPresetDirectory = System.IO.Path.Combine(
            PathManager.ConfigPath,
            CustomRestartPresets.DirectoryName);
        RestartCondition condition;
        if (restart.Preset == "empty" && restart.Edge == "empty")
        {
            condition = new RestartCondition { Edge = RestartEdge.LowActivityTick, MaxPlayers = 0,
                Confirmations = 1, TickSeconds = 1 };
        }
        else if (RestartPresets.TryResolve(restart.Preset, customPresetDirectory, out var preset, out var presetError))
        {
            condition = preset.Condition;
        }
        else
        {
            throw new ActivationProtocolException(
                $"Managed activation restart preset '{restart.Preset}' is unavailable: {presetError}");
        }

        if (restart.Edge == "round-end" && restart.Rounds is not null && condition.Rounds != restart.Rounds.Value)
        {
            condition = new RestartCondition
            {
                Edge = condition.Edge,
                RestartIfEmpty = condition.RestartIfEmpty,
                MaxPlayers = condition.MaxPlayers,
                SustainedSeconds = condition.SustainedSeconds,
                Confirmations = condition.Confirmations,
                TickSeconds = condition.TickSeconds,
                ActivityWindowMinutes = condition.ActivityWindowMinutes,
                ActivityOverPlayers = condition.ActivityOverPlayers,
                Rounds = restart.Rounds.Value
            };
        }

        if (restart.Edge is not ("round-end" or "empty"))
        {
            throw new ActivationProtocolException(
                $"Managed activation has unsupported restart edge '{restart.Edge}'.");
        }

        if (restart.MaxPlayers is not null && condition.MaxPlayers != restart.MaxPlayers)
        {
            condition = new RestartCondition
            {
                Edge = condition.Edge,
                RestartIfEmpty = condition.RestartIfEmpty,
                MaxPlayers = restart.MaxPlayers,
                SustainedSeconds = condition.SustainedSeconds,
                Confirmations = condition.Confirmations,
                TickSeconds = condition.TickSeconds,
                ActivityWindowMinutes = condition.ActivityWindowMinutes,
                ActivityOverPlayers = condition.ActivityOverPlayers,
                Rounds = condition.Rounds
            };
        }

        // Next-round: the game itself owns the edge through its native restart-next-round flag
        // (a toggle, so it is armed only when not already armed). The game's restart exit then
        // commits the activation. LocalAdmin's own round-edge intent below stays as the fallback
        // and restarts early when the game reports idle mode (no polling).
        if (restart.Edge == "round-end" && restart.MaxPlayers is null && condition.Rounds == 1)
        {
            if (_host.EnsureNativeRestartNextRoundArmed())
                ConsoleUtil.WriteLine($"Pending activation waits for the next round end ({_host.DescribeNativeRestartNextRound()}).", ConsoleColor.DarkGreen);
            else
                ConsoleUtil.WriteLine("Could not arm the native restart-next-round flag; LocalAdmin's round-edge intent is the only trigger.", ConsoleColor.Yellow);
        }

        if (!_restartIntents.TryArm(host, _tracker, "activation-" + restart.Preset, condition, out var message))
            ConsoleUtil.WriteLine($"Could not re-arm pending activation: {message}", ConsoleColor.Red);
        else
            ConsoleUtil.WriteLine(message, ConsoleColor.DarkGreen);
    }

    /// <summary>
    /// Verifies this session's child. When the verification tooling, not the game, fails for a
    /// candidate, the candidate keeps running in <c>verifying</c> and is verified again on the
    /// retry schedule; a later LocalAdmin session starts over if the retries run out.
    /// </summary>
    private async Task SuperviseVerificationAsync(ActivationRecord initial, CancellationToken cancellationToken)
    {
        if (initial.State == ActivationStates.RollingBack)
        {
            await VerifyRunningChildAsync(initial, attended: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var error = await TryVerifyCandidateAsync(initial, attended: true, cancellationToken).ConfigureAwait(false);
        if (error == null)
            return;

        _runningVerificationTarget = null;
        var schedule = $"every {VerificationRetryInterval.TotalSeconds:0.#} s for up to {VerificationRetryWindow.TotalMinutes:0.#} min";
        ConsoleUtil.WriteLine(
            $"Managed activation could not verify the candidate ({error}). This is a verification tooling failure, not evidence against the deployment: " +
            $"the game keeps running on the candidate, nothing is rolled back, and the record stays verifying. Retrying {schedule}.",
            ConsoleColor.Red);

        var retrying = Stopwatch.StartNew();
        for (var attempt = 1; retrying.Elapsed + VerificationRetryInterval <= VerificationRetryWindow; attempt++)
        {
            await Task.Delay(VerificationRetryInterval, cancellationToken).ConfigureAwait(false);
            error = await TryVerifyCandidateAsync(initial, attended: false, cancellationToken).ConfigureAwait(false);
            if (error == null)
                return;
            ConsoleUtil.WriteLine($"Managed activation verification retry {attempt} could not verify the candidate ({error}).", ConsoleColor.Yellow);
        }

        ConsoleUtil.WriteLine(
            $"Managed activation is still unverified after retrying {schedule} ({error}). The record stays verifying and the game keeps running; " +
            "the next LocalAdmin session verifies it again.",
            ConsoleColor.Red);
    }

    /// <summary>
    /// Runs one candidate attempt and contains every tooling error. Returns null when the attempt
    /// reached a decision or the record moved on, otherwise why the candidate is still unverified.
    /// </summary>
    private async Task<string?> TryVerifyCandidateAsync(ActivationRecord initial, bool attended, CancellationToken cancellationToken)
    {
        string? error;
        try
        {
            error = await VerifyRunningChildAsync(initial, attended, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
        }

        // A decision made meanwhile (a child-exit rollback, an operator repair) or the end of this
        // session wins; the next session verifies again.
        return error != null && !_host.IsShuttingDown &&
               await StillVerifyingAsync(initial.ActivationId, cancellationToken).ConfigureAwait(false)
            ? error
            : null;
    }

    private async Task<bool> StillVerifyingAsync(string activationId, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return latest != null && IsExpectedVerification(latest, activationId, "pending");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An unreadable store is reported and retried rather than silently dropped.
            return true;
        }
    }

    /// <summary>
    /// One readiness and runtime-verification attempt. Only evidence fails the target: an
    /// unplanned child exit, no readiness answer before the deadline while monitoring works, or a
    /// runtime check that failed. Returns null once the attempt reached a decision or the record
    /// moved on, otherwise why the tooling could not verify the candidate.
    /// </summary>
    /// <param name="attended">This session's child is under verification. A retry after a tooling
    /// failure is not: a child exit is left to ordinary crash handling, a readiness timeout is
    /// retried, and only a failed runtime check rolls the candidate back.</param>
    private async Task<string?> VerifyRunningChildAsync(ActivationRecord initial, bool attended, CancellationToken cancellationToken)
    {
        var target = initial.State == ActivationStates.RollingBack ? "previous" : "pending";
        var deadline = DateTime.UtcNow.AddSeconds(initial.Readiness.TimeoutSeconds);
        var checks = new List<ActivationCheck>();

        while (DateTime.UtcNow < deadline && !_host.IsShuttingDown)
        {
            if (!_host.GameProcessRunning)
            {
                if (attended)
                    await FailReadinessAsync(initial, target, checks, "game process exited during readiness", cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!_host.GameConsoleConnected)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Waiting for a heartbeat LocalAdmin cannot observe would fail a healthy game at the deadline.
            var heartbeatObservable = _host.HeartbeatMonitoringAvailable;
            if (initial.Readiness.RequireHeartbeat && heartbeatObservable && !_host.StartupHeartbeatReady)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (initial.Readiness.RequireUdp && !_host.IsGameUdpPortBound())
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var remaining = deadline - DateTime.UtcNow;
                var probe = await _host.ProbePlayersAsync(
                    remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
                checks.Add(Check(target + ".players", true, $"correlated response reported {probe.Players} players"));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            checks.Add(Check(target + ".process", true, "game process is running"));
            checks.Add(Check(target + ".console", true, "game console is connected"));
            if (initial.Readiness.RequireHeartbeat)
            {
                checks.Add(heartbeatObservable
                    ? Check(target + ".startup-heartbeat", true, "startup heartbeat observed")
                    : Check(target + ".startup-heartbeat", true,
                        "advisory: LocalAdmin heartbeat monitoring is disabled, so readiness did not wait for a heartbeat"));
            }
            if (initial.Readiness.RequireUdp)
                checks.Add(Check(target + ".udp", true, "configured UDP listener is bound"));

            var latest = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new ActivationProtocolException("activation.json disappeared during readiness.");
            if (!IsExpectedVerification(latest, initial.ActivationId, target))
                return null;
            latest = await _store.UpdateAsync(latest.Revision, record => record! with
            {
                Checks = Append(record.Checks, checks)
            }, cancellationToken).ConfigureAwait(false);

            // The game answered readiness, so from here on only a failed runtime check is evidence.
            var remainingForHook = deadline - DateTime.UtcNow;
            if (remainingForHook <= TimeSpan.Zero)
                return await VerificationUnconfirmedAsync(latest, target, "no readiness time was left to run verify-runtime", cancellationToken).ConfigureAwait(false);

            using var hookDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hookDeadline.CancelAfter(remainingForHook);
            var hook = await RunHookSafeAsync("verify-runtime", latest, hookDeadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!hook.Ok && hook.Checks.Count == 0 && hookDeadline.IsCancellationRequested)
                hook = hook with { Error = "verify-runtime did not answer before the readiness deadline" };

            if (hook.Ok && initial.Readiness.RequireVerification && !HasSuccessfulServerVerification(hook.Checks))
            {
                hook = hook with
                {
                    Ok = false,
                    Error = "runtime verification did not prove the server verification state"
                };
            }

            // A FAILED verify-runtime returns no finalized checkpoint. The staged candidate must
            // stay in pending_checkpoint so the record remains valid for the single rollback or a
            // retry instead of crashing this verification task.
            latest = await _store.UpdateAsync(latest.Revision, record => record! with
            {
                PendingCheckpoint = target == "pending" && hook.Ok && hook.Checkpoint != null
                    ? hook.Checkpoint
                    : record.PendingCheckpoint,
                CurrentCheckpoint = target == "pending" && hook.Ok && hook.Checkpoint != null
                    ? hook.Checkpoint
                    : record.CurrentCheckpoint,
                Checks = Append(record.Checks, hook.Checks)
            }, cancellationToken).ConfigureAwait(false);

            if (!hook.Ok)
            {
                // A hook that could not run or answer, or that stopped without failing a check
                // (checkpoint finalization on a full disk, config bookkeeping, a missing proof),
                // produced no evidence against the target.
                var error = hook.Error ?? "runtime verification failed";
                if (!hook.Checks.Any(check => !check.Ok))
                    return await VerificationUnconfirmedAsync(latest, target, error, cancellationToken).ConfigureAwait(false);
                await FailReadinessAsync(latest, target, Array.Empty<ActivationCheck>(), error, cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (target == "pending")
            {
                await _store.UpdateAsync(latest.Revision, record => ClearOpenActivation(record!, record!.PendingCheckpoint!), cancellationToken).ConfigureAwait(false);
                ConsoleUtil.WriteLine("Managed activation verified successfully.", ConsoleColor.DarkGreen);
            }
            else
            {
                var rolledBack = await _store.UpdateAsync(latest.Revision, record => record! with
                {
                    State = ActivationStates.RolledBack,
                    CurrentCheckpoint = record.PreviousCheckpoint,
                    LastError = null
                }, cancellationToken).ConfigureAwait(false);
                await _store.UpdateAsync(rolledBack.Revision, record => ClearOpenActivation(record!, record!.PreviousCheckpoint!), cancellationToken).ConfigureAwait(false);
                ConsoleUtil.WriteLine("Previous checkpoint verified after automatic rollback.", ConsoleColor.DarkGreen);
            }
            return null;
        }

        // LocalAdmin is ending the session (an operator exit or restart); the next session verifies again.
        if (_host.IsShuttingDown)
            return null;
        if (!attended)
            return "the game did not answer readiness before the deadline";
        await FailReadinessAsync(initial, target, checks, "readiness deadline expired", cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>The target produced evidence of failure.</summary>
    private async Task FailReadinessAsync(
        ActivationRecord stale,
        string target,
        IReadOnlyCollection<ActivationCheck> checks,
        string error,
        CancellationToken cancellationToken)
    {
        if (target == "previous")
        {
            await RecoverRollbackTargetAsync(stale, checks, "rollback target failed readiness; the game keeps running: " + error, cancellationToken).ConfigureAwait(false);
            return;
        }

        var latest = await LoadOpenAsync(stale, cancellationToken).ConfigureAwait(false);
        if (latest == null)
            return;
        if (latest.State == ActivationStates.Verifying)
            await EnterRollbackAsync(latest, error, checks, cancellationToken).ConfigureAwait(false);
        else if (latest.State is not (ActivationStates.RollingBack or ActivationStates.RecoveryRequired))
            throw new ActivationProtocolException($"Cannot fail pending readiness from state {latest.State}.");

        _host.ForceActivationSessionRestart("managed activation readiness failed; restoring the previous checkpoint");
    }

    /// <summary>
    /// The verification tooling could not decide the target, which is no evidence against it. A
    /// candidate stays installed and verifying and the reason is returned for a retry; the rollback
    /// target keeps its recovery-required handling with a message that names the tooling.
    /// </summary>
    private async Task<string?> VerificationUnconfirmedAsync(
        ActivationRecord stale,
        string target,
        string error,
        CancellationToken cancellationToken)
    {
        if (target == "pending")
            return error;
        await RecoverRollbackTargetAsync(stale, Array.Empty<ActivationCheck>(),
            "rollback target could not be verified because the verification tooling failed; the game keeps running: " + error,
            cancellationToken).ConfigureAwait(false);
        return null;
    }

    // The rollback target is the last known-good build. Restarting it again cannot help, so the
    // running game is left alone: a plugin that failed to load is already skipped by LabAPI, and a
    // dead child is relaunched by LocalAdmin's normal crash handling.
    private async Task RecoverRollbackTargetAsync(
        ActivationRecord stale,
        IReadOnlyCollection<ActivationCheck> checks,
        string error,
        CancellationToken cancellationToken)
    {
        var latest = await LoadOpenAsync(stale, cancellationToken).ConfigureAwait(false);
        if (latest == null)
            return;
        if (latest.State == ActivationStates.RollingBack)
        {
            await EnterRecoveryAsync(latest, error, checks, cancellationToken).ConfigureAwait(false);
            ConsoleUtil.WriteLine("Managed activation needs repair (recovery-required); the game keeps running.", ConsoleColor.Red);
        }
        else if (latest.State != ActivationStates.RecoveryRequired)
        {
            throw new ActivationProtocolException($"Cannot fail rollback readiness from state {latest.State}.");
        }
    }

    /// <summary>The current record of <paramref name="stale"/>'s activation, or null once it was verified or replaced.</summary>
    private async Task<ActivationRecord?> LoadOpenAsync(ActivationRecord stale, CancellationToken cancellationToken)
    {
        var latest = await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? stale;
        return string.Equals(latest.ActivationId, stale.ActivationId, StringComparison.Ordinal) &&
               latest.State != ActivationStates.Verified
            ? latest
            : null;
    }

    private async Task<ActivationRecord> EnterRollbackAsync(
        ActivationRecord record,
        string error,
        IReadOnlyCollection<ActivationCheck> checks,
        CancellationToken cancellationToken)
    {
        if (record.State == ActivationStates.RollingBack && record.RollbackAttempted)
            return record;
        if (record.State != ActivationStates.Verifying && record.State != ActivationStates.Activating)
            throw new ActivationProtocolException($"Cannot enter rollback from state {record.State}.");

        return await _store.UpdateAsync(record.Revision, current => current! with
        {
            State = ActivationStates.RollingBack,
            VerificationTarget = "previous",
            RollbackAttempted = true,
            Checks = Append(current.Checks, checks),
            LastError = error
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<ActivationRecord> EnterRecoveryAsync(
        ActivationRecord record,
        string error,
        IReadOnlyCollection<ActivationCheck> checks,
        CancellationToken cancellationToken) =>
        _store.UpdateAsync(record.Revision, current => current! with
        {
            State = ActivationStates.RecoveryRequired,
            VerificationTarget = "previous",
            RollbackAttempted = true,
            Checks = Append(current.Checks, checks),
            LastError = error
        }, cancellationToken);

    private async Task<ActivationHookResult> RunHookSafeAsync(
        string phase,
        ActivationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _hooks.RunAsync(phase, record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ActivationHookResult
            {
                ProtocolVersion = 1,
                Ok = false,
                ActivationId = record.ActivationId,
                Phase = phase,
                Checkpoint = phase == "apply-rollback" ? record.PreviousCheckpoint : record.PendingCheckpoint,
                Checks = new(),
                Error = ex.Message
            };
        }
    }

    private async Task<ActivationRecord> RequireStateAsync(string state, CancellationToken cancellationToken)
    {
        var record = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new ActivationProtocolException("activation.json does not exist.");
        if (record.State != state)
            throw new ActivationProtocolException($"Expected state {state}, found {record.State}.");
        return record;
    }

    private async Task<ActivationRecord> RequireActivationAsync(string activationId, string state, CancellationToken cancellationToken)
    {
        var record = await RequireStateAsync(state, cancellationToken).ConfigureAwait(false);
        if (record.ActivationId != activationId)
            throw new ActivationProtocolException("activation_id does not match the open activation.");
        return record;
    }

    private static ActivationRecord ClearOpenActivation(ActivationRecord record, string currentCheckpoint) => record with
    {
        State = ActivationStates.Verified,
        CurrentCheckpoint = currentCheckpoint,
        PendingCheckpoint = null,
        PreviousCheckpoint = null,
        VerificationTarget = null,
        Lock = null,
        Restart = null,
        LastError = null
    };

    private static List<ActivationCheck> Append(IEnumerable<ActivationCheck> existing, IEnumerable<ActivationCheck> added) =>
        existing.Concat(added).ToList();

    private static ActivationCheck Check(string name, bool ok, string detail) => new()
    {
        Name = name,
        Ok = ok,
        ObservedAt = ActivationRecordStore.FormatTimestamp(DateTime.UtcNow),
        Detail = detail
    };

    private static bool IsExpectedVerification(ActivationRecord record, string activationId, string target) =>
        string.Equals(record.ActivationId, activationId, StringComparison.Ordinal) &&
        (target == "pending"
            ? record.State == ActivationStates.Verifying && record.VerificationTarget == "pending"
            : record.State == ActivationStates.RollingBack && record.VerificationTarget == "previous");

    private static bool HasSuccessfulServerVerification(IEnumerable<ActivationCheck> checks) =>
        checks.Any(check => check.Ok &&
                            string.Equals(check.Name, "runtime.verification-token", StringComparison.Ordinal));

    private sealed class ActivationRestartHost : IRestartHost
    {
        private readonly ActivationCoordinator _owner;
        private readonly IActivationHost _inner;

        internal ActivationRestartHost(ActivationCoordinator owner, IActivationHost inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public bool TryScheduleGracefulRestart(string reason)
        {
            try
            {
                return _inner.TryScheduleManagedActivationRestart(reason, () =>
                {
                    var current = _owner._store.LoadAsync().GetAwaiter().GetResult();
                    if (current is not { State: ActivationStates.Pending })
                        return false;
                    _owner._store.UpdateAsync(current.Revision, record => record! with
                    {
                        State = ActivationStates.Activating
                    }).GetAwaiter().GetResult();
                    return true;
                });
            }
            catch (Exception ex)
            {
                ConsoleUtil.WriteLine($"Failed to commit pending activation: {ex.Message}", ConsoleColor.Red);
                return false;
            }
        }

        public void RequestPlayerCountRefresh() => _inner.RequestPlayerCountRefresh();
        public bool IsShuttingDown => _inner.IsShuttingDown;
        public bool RestartInProgress => _inner.RestartInProgress;
        public bool ConfirmNotShuttingDown() => _inner.ConfirmNotShuttingDown();
    }
}
