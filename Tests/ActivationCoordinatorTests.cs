using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.Core.Activation;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class ActivationCoordinatorTests : IDisposable
{
    private const string Current = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Pending = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "la-coordinator-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resumes_Activating_By_Applying_Target_Before_Launch()
    {
        var setup = await SetupActivatingAsync();
        var resume = await setup.Coordinator.ResumeBeforeChildAsync();

        Assert.True(resume.LaunchGame);
        Assert.Equal(ActivationStates.Verifying, resume.Record!.State);
        Assert.Equal(Pending, resume.Record.CurrentCheckpoint);
        Assert.Equal(new[] { "apply-target" }, setup.Hooks.Phases);
    }

    [Fact]
    public async Task Child_Exit_And_Readiness_Task_Report_One_Rollback_Without_Recovery()
    {
        var setup = await SetupActivatingAsync();
        setup.Host.BlockProbe = true;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();
        await setup.Host.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await setup.Coordinator.OnChildExitedAsync());
        setup.Host.ProbeRelease.TrySetResult();
        await Task.Delay(100);

        var record = await setup.Store.LoadAsync();
        Assert.Equal(ActivationStates.RollingBack, record!.State);
        Assert.True(record.RollbackAttempted);
    }

    [Fact]
    public async Task Prepare_Rejects_Stale_Expected_Revision_Before_Acquiring_Lock()
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        var host = new FakeHost();
        var coordinator = new ActivationCoordinator(store, new FakeHooks(), host, new PlayerCountTracker());
        await coordinator.BootstrapAsync(
            "58f83dc3-bf19-4a45-89f6-f7138142b4db",
            Current,
            ActivationRecordStoreTests.Readiness());

        await Assert.ThrowsAsync<ActivationProtocolException>(() => coordinator.PrepareAsync(
            "b4ed4b95-9ce4-42a3-8388-f372e24b30dd",
            99,
            Pending,
            "test",
            new ActivationRestart { Preset = "immediate", Edge = "immediate", MaxPlayers = null, Rounds = null },
            ActivationRecordStoreTests.Readiness()));

        Assert.Equal(ActivationStates.Verified, (await store.LoadAsync())!.State);
    }

    [Fact]
    public async Task Successful_Rollback_Records_RolledBack_Then_Returns_Verified()
    {
        var setup = await SetupActivatingAsync();
        setup.Hooks.VerifyPendingOk = false;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();
        await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);

        var rollbackCoordinator = new ActivationCoordinator(setup.Store, setup.Hooks, setup.Host, setup.Tracker);
        var resume = await rollbackCoordinator.ResumeBeforeChildAsync();
        Assert.True(resume.LaunchGame);
        Assert.Equal(ActivationStates.RollingBack, resume.Record!.State);
        Assert.Equal(Current, resume.Record.CurrentCheckpoint);
        await rollbackCoordinator.OnChildStartedAsync();

        var final = await WaitForStateAsync(setup.Store, ActivationStates.Verified);
        Assert.Equal(Current, final.CurrentCheckpoint);
        Assert.True(final.RollbackAttempted);
        Assert.Null(final.Lock);
        Assert.Contains("apply-rollback", setup.Hooks.Phases);
    }

    [Fact]
    public async Task Failed_Rollback_Readiness_Stops_In_Recovery_Without_Second_Rollback()
    {
        var setup = await SetupActivatingAsync();
        setup.Hooks.VerifyPendingOk = false;
        setup.Hooks.VerifyPreviousOk = false;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();
        await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);

        var rollbackCoordinator = new ActivationCoordinator(setup.Store, setup.Hooks, setup.Host, setup.Tracker);
        await rollbackCoordinator.ResumeBeforeChildAsync();
        await rollbackCoordinator.OnChildStartedAsync();
        var recovery = await WaitForStateAsync(setup.Store, ActivationStates.RecoveryRequired);

        Assert.True(recovery.RollbackAttempted);
        Assert.NotNull(recovery.Lock);
        Assert.NotNull(recovery.LastError);
        Assert.Equal(2, setup.Host.ForcedRestarts);
        Assert.Single(setup.Hooks.Phases.FindAll(p => p == "apply-rollback"));

        var afterRestart = new ActivationCoordinator(setup.Store, setup.Hooks, setup.Host, setup.Tracker);
        var held = await afterRestart.ResumeBeforeChildAsync();
        Assert.False(held.LaunchGame);
        Assert.Single(setup.Hooks.Phases.FindAll(p => p == "apply-rollback"));
    }

    [Fact]
    public async Task Failed_Pending_Verification_With_Null_Checkpoint_Enters_Rollback()
    {
        // The real scpsl-ops hook reports a FAILED verify-runtime with checkpoint null (there is
        // no finalized manifest). The staged candidate must stay in pending_checkpoint and the
        // failure must convert into the single rollback; nulling it crashed the verification task
        // and stranded the lane in verifying (found by the local E2E).
        var setup = await SetupActivatingAsync();
        setup.Hooks.VerifyPendingOk = false;
        setup.Hooks.NullCheckpointOnFailure = true;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();

        var rollingBack = await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);
        Assert.Equal(Pending, rollingBack.PendingCheckpoint);
        Assert.True(rollingBack.RollbackAttempted);
    }

    [Fact]
    public async Task Required_Server_Verification_Must_Be_Proved_By_Runtime_Hook()
    {
        var setup = await SetupActivatingAsync();
        setup.Hooks.IncludeVerificationProof = false;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();

        var rollingBack = await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);
        Assert.Contains("did not prove", rollingBack.LastError);
    }

    [Fact]
    public async Task Optional_Server_Verification_Does_Not_Require_The_Runtime_Proof()
    {
        var readiness = ActivationRecordStoreTests.Readiness() with { RequireVerification = false };
        var setup = await SetupActivatingAsync(readiness: readiness);
        setup.Hooks.IncludeVerificationProof = false;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();

        var verified = await WaitForStateAsync(setup.Store, ActivationStates.Verified);
        Assert.Equal(Pending, verified.CurrentCheckpoint);
    }

    [Fact]
    public async Task Required_Heartbeat_Does_Not_Pass_When_Monitoring_Is_Unavailable()
    {
        var readiness = ActivationRecordStoreTests.Readiness() with { TimeoutSeconds = 1 };
        var setup = await SetupActivatingAsync(readiness: readiness);
        setup.Host.StartupHeartbeatReady = false;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();

        var rollingBack = await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);
        Assert.Equal("readiness deadline expired", rollingBack.LastError);
        Assert.DoesNotContain("verify-runtime", setup.Hooks.Phases);
    }

    [Fact]
    public async Task Runtime_Hook_Is_Bounded_By_Total_Readiness_Deadline()
    {
        var readiness = ActivationRecordStoreTests.Readiness() with { TimeoutSeconds = 1 };
        var setup = await SetupActivatingAsync(readiness: readiness);
        setup.Hooks.VerificationDelay = TimeSpan.FromSeconds(10);
        await setup.Coordinator.ResumeBeforeChildAsync();
        var started = DateTime.UtcNow;
        await setup.Coordinator.OnChildStartedAsync();

        var rollingBack = await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Equal("readiness deadline expired", rollingBack.LastError);
    }

    [Fact]
    public async Task Failed_Rollback_Apply_Does_Not_Claim_Previous_Checkpoint_Is_Installed()
    {
        var setup = await SetupActivatingAsync();
        setup.Hooks.VerifyPendingOk = false;
        await setup.Coordinator.ResumeBeforeChildAsync();
        await setup.Coordinator.OnChildStartedAsync();
        await WaitForStateAsync(setup.Store, ActivationStates.RollingBack);

        setup.Hooks.FailRollbackApply = true;
        var rollbackCoordinator = new ActivationCoordinator(setup.Store, setup.Hooks, setup.Host, setup.Tracker);
        var resume = await rollbackCoordinator.ResumeBeforeChildAsync();

        Assert.False(resume.LaunchGame);
        Assert.Equal(ActivationStates.RecoveryRequired, resume.Record!.State);
        Assert.Equal(Pending, resume.Record.CurrentCheckpoint);
    }

    [Fact]
    public async Task Rejected_Restart_Claim_Leaves_Activation_Pending()
    {
        var setup = await SetupActivatingAsync(stopAtPending: true);
        setup.Host.ManagedRestartAccepted = false;

        await setup.Coordinator.OnChildStartedAsync();

        Assert.False(setup.Host.ManagedRestartCommitInvoked);
        Assert.Equal(ActivationStates.Pending, (await setup.Store.LoadAsync())!.State);
    }

    [Fact]
    public async Task Cancelled_Pending_Cleans_Stage_And_Releases_Lock()
    {
        var setup = await SetupActivatingAsync(stopAtPending: true);
        var current = await setup.Store.LoadAsync();
        var final = await setup.Coordinator.CancelAsync(current!.ActivationId);

        Assert.Equal(ActivationStates.Verified, final.State);
        Assert.Null(final.PendingCheckpoint);
        Assert.Null(final.Lock);
        Assert.Contains("cleanup", setup.Hooks.Phases);
    }

    private async Task<Setup> SetupActivatingAsync(
        bool stopAtPending = false,
        ActivationReadiness? readiness = null)
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        var host = new FakeHost();
        var hooks = new FakeHooks();
        var tracker = new PlayerCountTracker();
        var coordinator = new ActivationCoordinator(store, hooks, host, tracker);
        var bootstrap = await coordinator.BootstrapAsync(
            "58f83dc3-bf19-4a45-89f6-f7138142b4db",
            Current,
            readiness ?? ActivationRecordStoreTests.Readiness());
        var prepared = await coordinator.PrepareAsync(
            "b4ed4b95-9ce4-42a3-8388-f372e24b30dd",
            bootstrap.Revision,
            Pending,
            "test",
            new ActivationRestart
            {
                Preset = "immediate",
                Edge = "immediate",
                MaxPlayers = null,
                Rounds = null
            },
            readiness ?? ActivationRecordStoreTests.Readiness());
        var pending = await coordinator.ArmAsync(prepared.ActivationId);
        if (!stopAtPending)
        {
            await store.UpdateAsync(pending.Revision, record => record! with { State = ActivationStates.Activating });
        }
        return new(store, coordinator, hooks, host, tracker);
    }

    private static async Task<ActivationRecord> WaitForStateAsync(ActivationRecordStore store, string state)
    {
        for (var i = 0; i < 200; i++)
        {
            var record = await store.LoadAsync();
            if (record?.State == state)
                return record;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Activation record did not enter {state}.");
    }

    private sealed record Setup(
        ActivationRecordStore Store,
        ActivationCoordinator Coordinator,
        FakeHooks Hooks,
        FakeHost Host,
        PlayerCountTracker Tracker);

    private sealed class FakeHooks : IActivationHookRunner
    {
        internal readonly List<string> Phases = new();
        internal bool VerifyPendingOk = true;
        internal bool VerifyPreviousOk = true;
        internal bool NullCheckpointOnFailure;
        internal bool IncludeVerificationProof = true;
        internal bool FailRollbackApply;
        internal TimeSpan VerificationDelay;

        public async Task<ActivationHookResult> RunAsync(string phase, ActivationRecord record, CancellationToken cancellationToken)
        {
            lock (Phases)
                Phases.Add(phase);
            if (phase == "verify-runtime" && VerificationDelay > TimeSpan.Zero)
                await Task.Delay(VerificationDelay, cancellationToken);
            var isPrevious = record.VerificationTarget == "previous" || phase == "apply-rollback";
            var ok = phase switch
            {
                "apply-rollback" => !FailRollbackApply,
                "verify-runtime" => isPrevious ? VerifyPreviousOk : VerifyPendingOk,
                _ => true
            };
            return new ActivationHookResult
            {
                ProtocolVersion = 1,
                Ok = ok,
                ActivationId = record.ActivationId,
                Phase = phase,
                Checkpoint = !ok && NullCheckpointOnFailure
                    ? null
                    : isPrevious ? record.PreviousCheckpoint : record.PendingCheckpoint,
                Checks = phase == "verify-runtime" && ok && IncludeVerificationProof
                    ? new List<ActivationCheck>
                    {
                        new()
                        {
                            Name = "runtime.verification-token",
                            Ok = true,
                            ObservedAt = ActivationRecordStore.FormatTimestamp(DateTime.UtcNow),
                            Detail = "verification token remains present"
                        }
                    }
                    : new(),
                Error = ok ? null : "simulated readiness failure"
            };
        }
    }

    private sealed class FakeHost : IActivationHost
    {
        internal int ForcedRestarts;
        internal bool BlockProbe;
        internal bool ManagedRestartAccepted = true;
        internal bool ManagedRestartCommitInvoked;
        internal TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ProbeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GameProcessRunning => true;
        public bool GameConsoleConnected => true;
        public bool StartupHeartbeatReady { get; set; } = true;
        public bool IsShuttingDown => false;
        public bool RestartInProgress => false;
        public bool TryScheduleGracefulRestart(string reason) => true;
        public bool TryScheduleManagedActivationRestart(string reason, Func<bool> commitActivation)
        {
            if (!ManagedRestartAccepted)
                return false;
            ManagedRestartCommitInvoked = true;
            return commitActivation();
        }
        public void RequestPlayerCountRefresh() { }
        public bool ConfirmNotShuttingDown() => true;
        public bool IsGameUdpPortBound() => true;
        public async Task<PlayerProbeResult> ProbePlayersAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (BlockProbe)
            {
                ProbeStarted.TrySetResult();
                await ProbeRelease.Task.WaitAsync(cancellationToken);
            }
            return new PlayerProbeResult(Guid.NewGuid(), 0, DateTime.UtcNow, DateTime.UtcNow);
        }
        public void ForceActivationSessionRestart(string reason) => ForcedRestarts++;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup */ }
    }
}
