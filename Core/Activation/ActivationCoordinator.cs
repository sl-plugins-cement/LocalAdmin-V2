// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Collections.Generic;
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
    bool StartupHeartbeatReady { get; }
    bool IsGameUdpPortBound();
    Task<PlayerProbeResult> ProbePlayersAsync(TimeSpan timeout, CancellationToken cancellationToken);
    bool TryScheduleManagedActivationRestart(string reason, Func<bool> commitActivation);
    void ForceActivationSessionRestart(string reason);
}

internal sealed record ActivationResumeResult(bool LaunchGame, string Message, ActivationRecord? Record);

/// <summary>
/// Durable activation state machine. It supervises the game and invokes fixed scpsl-ops hooks; it
/// never interprets checkpoint manifests or writes managed game files itself.
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
    private string? _runningVerificationTarget;

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

    internal async Task<ActivationResumeResult> ResumeBeforeChildAsync(CancellationToken cancellationToken = default)
    {
        ActivationRecord? record;
        try
        {
            record = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ActivationProtocolException ex)
        {
            return new(false, ex.Message, null);
        }

        if (record == null)
            return new(false, "Managed activation is enabled but activation.json does not exist. Bootstrap it through scpsl-ops.", null);

        while (true)
        {
            switch (record.State)
            {
                case ActivationStates.RecoveryRequired:
                    return new(false, record.LastError ?? "Recovery is required.", record);

                case ActivationStates.Cancelled:
                {
                    var result = await RunHookSafeAsync("cleanup", record, cancellationToken).ConfigureAwait(false);
                    if (!result.Ok)
                        return new(true, result.Error ?? "Staging cleanup failed; lane remains cancelled and locked.", record);

                    record = await _store.UpdateAsync(record.Revision, current => ClearOpenActivation(current!, current!.CurrentCheckpoint!), cancellationToken).ConfigureAwait(false);
                    continue;
                }

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
                    return new(true, "Target files applied; starting readiness verification.", record);
                }

                case ActivationStates.RollingBack:
                {
                    var result = await RunHookSafeAsync("apply-rollback", record, cancellationToken).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        record = await EnterRecoveryAsync(record, result.Error ?? "rollback apply failed", result.Checks, cancellationToken).ConfigureAwait(false);
                        return new(false, record.LastError!, record);
                    }

                    if (result.Checkpoint != record.PreviousCheckpoint)
                    {
                        record = await EnterRecoveryAsync(record, "rollback hook returned the wrong checkpoint", result.Checks, cancellationToken).ConfigureAwait(false);
                        return new(false, record.LastError!, record);
                    }

                    record = await _store.UpdateAsync(record.Revision, current => current! with
                    {
                        CurrentCheckpoint = current.PreviousCheckpoint,
                        Checks = Append(current.Checks, result.Checks)
                    }, cancellationToken).ConfigureAwait(false);
                    return new(true, "Previous checkpoint restored; starting rollback readiness verification.", record);
                }

                default:
                    return new(true, $"Activation state is {record.State}.", record);
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

        if (record.State is ActivationStates.Verifying or ActivationStates.RollingBack &&
            Interlocked.Exchange(ref _verificationRunning, 1) == 0)
        {
            _runningVerificationTarget = record.State == ActivationStates.RollingBack ? "previous" : "pending";
            var verificationToken = _sessionCts.Token;
            _ = Task.Run(async () =>
            {
                try { await VerifyRunningChildAsync(record, verificationToken).ConfigureAwait(false); }
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
            await EnterRecoveryAsync(record, "rollback target game child exited during readiness; game child stopped", Array.Empty<ActivationCheck>(), cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    internal void NotifyRoundRestart() => _restartIntents.NotifyRoundRestart(new ActivationRestartHost(this, _host), _tracker);

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
        return await _store.UpdateAsync(prepared.Revision, record => record! with { State = ActivationStates.Pending }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ActivationRecord> CancelAsync(string activationId, CancellationToken cancellationToken = default)
    {
        var current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new ActivationProtocolException("activation.json does not exist.");
        if (current.ActivationId != activationId || current.State is not (ActivationStates.Prepared or ActivationStates.Pending))
            throw new ActivationProtocolException("Only the matching prepared or pending activation can be cancelled.");

        var cancelled = await _store.UpdateAsync(current.Revision, record => record! with { State = ActivationStates.Cancelled }, cancellationToken).ConfigureAwait(false);
        _restartIntents.StopForSessionEnd();
        var result = await RunHookSafeAsync("cleanup", cancelled, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
            return cancelled;
        return await _store.UpdateAsync(cancelled.Revision, record => ClearOpenActivation(record!, record!.CurrentCheckpoint!), cancellationToken).ConfigureAwait(false);
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
        if (RestartPresets.TryResolve(restart.Preset, customPresetDirectory, out var preset, out var presetError))
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
                MaxPlayers = restart.MaxPlayers,
                SustainedSeconds = condition.SustainedSeconds,
                Confirmations = condition.Confirmations,
                TickSeconds = condition.TickSeconds,
                ActivityWindowMinutes = condition.ActivityWindowMinutes,
                ActivityOverPlayers = condition.ActivityOverPlayers,
                Rounds = condition.Rounds
            };
        }

        if (!_restartIntents.TryArm(host, _tracker, "activation-" + restart.Preset, condition, out var message))
            ConsoleUtil.WriteLine($"Could not re-arm pending activation: {message}", ConsoleColor.Red);
        else
            ConsoleUtil.WriteLine(message, ConsoleColor.DarkGreen);
    }

    private async Task VerifyRunningChildAsync(ActivationRecord initial, CancellationToken cancellationToken)
    {
        var target = initial.State == ActivationStates.RollingBack ? "previous" : "pending";
        var deadline = DateTime.UtcNow.AddSeconds(initial.Readiness.TimeoutSeconds);
        var checks = new List<ActivationCheck>();

        while (DateTime.UtcNow < deadline && !_host.IsShuttingDown)
        {
            if (!_host.GameProcessRunning)
            {
                await FailReadinessAsync(initial, target, checks, "game process exited during readiness", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_host.GameConsoleConnected)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (initial.Readiness.RequireHeartbeat && !_host.StartupHeartbeatReady)
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
                checks.Add(Check(target + ".startup-heartbeat", true, "startup heartbeat observed"));
            if (initial.Readiness.RequireUdp)
                checks.Add(Check(target + ".udp", true, "configured UDP listener is bound"));

            var latest = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new ActivationProtocolException("activation.json disappeared during readiness.");
            if (!IsExpectedVerification(latest, initial.ActivationId, target))
                return;
            latest = await _store.UpdateAsync(latest.Revision, record => record! with
            {
                Checks = Append(record.Checks, checks)
            }, cancellationToken).ConfigureAwait(false);

            var remainingForHook = deadline - DateTime.UtcNow;
            if (remainingForHook <= TimeSpan.Zero)
            {
                await FailReadinessAsync(latest, target, Array.Empty<ActivationCheck>(), "readiness deadline expired", cancellationToken).ConfigureAwait(false);
                return;
            }

            using var hookDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hookDeadline.CancelAfter(remainingForHook);
            var hook = await RunHookSafeAsync("verify-runtime", latest, hookDeadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (hookDeadline.IsCancellationRequested || DateTime.UtcNow >= deadline)
            {
                await FailReadinessAsync(latest, target, hook.Checks, "readiness deadline expired", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (hook.Ok && initial.Readiness.RequireVerification && !HasSuccessfulServerVerification(hook.Checks))
            {
                hook = hook with
                {
                    Ok = false,
                    Error = "runtime verification did not prove the server verification state"
                };
            }

            // A FAILED verify-runtime returns no finalized checkpoint. The staged candidate must
            // stay in pending_checkpoint so the record remains valid and the failure converts
            // into the single rollback instead of crashing this verification task.
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
                await FailReadinessAsync(latest, target, Array.Empty<ActivationCheck>(), hook.Error ?? "runtime verification failed", cancellationToken).ConfigureAwait(false);
                return;
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
            return;
        }

        await FailReadinessAsync(initial, target, checks, "readiness deadline expired", cancellationToken).ConfigureAwait(false);
    }

    private async Task FailReadinessAsync(
        ActivationRecord stale,
        string target,
        IReadOnlyCollection<ActivationCheck> checks,
        string error,
        CancellationToken cancellationToken)
    {
        var latest = await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? stale;
        if (!string.Equals(latest.ActivationId, stale.ActivationId, StringComparison.Ordinal))
            return;
        if (latest.State == ActivationStates.Verified)
            return;
        if (target == "pending")
        {
            if (latest.State == ActivationStates.Verifying)
                await EnterRollbackAsync(latest, error, checks, cancellationToken).ConfigureAwait(false);
            else if (latest.State is not (ActivationStates.RollingBack or ActivationStates.RecoveryRequired or ActivationStates.Verified))
                throw new ActivationProtocolException($"Cannot fail pending readiness from state {latest.State}.");
        }
        else
        {
            if (latest.State == ActivationStates.RollingBack)
                await EnterRecoveryAsync(latest, "rollback target failed readiness; game child stopped: " + error, checks, cancellationToken).ConfigureAwait(false);
            else if (latest.State is not (ActivationStates.RecoveryRequired or ActivationStates.Verified))
                throw new ActivationProtocolException($"Cannot fail rollback readiness from state {latest.State}.");
        }

        _host.ForceActivationSessionRestart("managed activation readiness failed");
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
