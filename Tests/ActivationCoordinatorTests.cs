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

        Assert.False(resume.NeedsRepair);
        Assert.Equal(ActivationStates.Verifying, resume.Record!.State);
        Assert.Equal(Pending, resume.Record.CurrentCheckpoint);
        Assert.Equal(new[] { "preflight", "apply-target" }, setup.Hooks.Phases);
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
        Assert.False(resume.NeedsRepair);
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
    public async Task Failed_Rollback_Readiness_Keeps_The_Game_Running_In_Recovery()
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
        Assert.Contains("keeps running", recovery.LastError);
        // Only the candidate failure restarts (into the rollback); the rollback target is left running.
        Assert.Equal(1, setup.Host.ForcedRestarts);
        Assert.Single(setup.Hooks.Phases.FindAll(p => p == "apply-rollback"));

        var afterRestart = new ActivationCoordinator(setup.Store, setup.Hooks, setup.Host, setup.Tracker);
        var resume = await afterRestart.ResumeBeforeChildAsync();
        Assert.True(resume.NeedsRepair);
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

        Assert.True(resume.NeedsRepair);
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
        ActivationReadiness? readiness = null,
        bool stopAtPrepared = false, ActivationRestart? restart = null, string? root = null)
    {
        var store = new ActivationRecordStore(root ?? _root, "example/prod");
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
            restart ?? new ActivationRestart
            {
                Preset = "immediate",
                Edge = "immediate",
                MaxPlayers = null,
                Rounds = null
            },
            readiness ?? ActivationRecordStoreTests.Readiness());
        if (stopAtPrepared)
            return new(store, coordinator, hooks, host, tracker);
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

    [Fact]
    public async Task Failed_Preflight_Leaves_Game_Running_And_Reservation_Cancellable()
    {
        var setup = await SetupActivatingAsync(stopAtPrepared: true);
        setup.Hooks.FailPreflight = true;
        var before = (await setup.Store.LoadAsync())!;
        var error = await Assert.ThrowsAsync<ActivationProtocolException>(() =>
            setup.Coordinator.ArmAsync(before.ActivationId));
        Assert.Contains("preflight failed before restart", error.Message);
        var after = (await setup.Store.LoadAsync())!;
        Assert.Equal(ActivationStates.Prepared, after.State);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(0, setup.Host.ForcedRestarts);
        Assert.False(setup.Host.ManagedRestartCommitInvoked);
        Assert.Equal(ActivationStates.Verified,
            (await setup.Coordinator.CancelAsync(before.ActivationId)).State);
    }

    [Fact]
    public async Task Next_Round_Arm_Ensures_Native_Rnr_Without_Toggling_An_Operator_Arm()
    {
        var setup = await SetupNextRoundAsync();
        await setup.Coordinator.OnChildStartedAsync();
        Assert.Equal(new[] { "rnr" }, setup.Host.Commands);
        Assert.True(setup.Host.NativeRnr.OwnedByLocalAdmin);
        setup.Coordinator.StopForSessionEnd();

        var operatorArmed = await SetupNextRoundAsync(SecondRoot);
        operatorArmed.Host.NativeRnr.ObserveExitAction(LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Restart);
        await operatorArmed.Coordinator.OnChildStartedAsync();
        Assert.Empty(operatorArmed.Host.Commands);
        operatorArmed.Coordinator.StopForSessionEnd();
    }

    [Fact]
    public async Task Game_Restart_Exit_Commits_A_Pending_Next_Round_Activation()
    {
        var setup = await SetupNextRoundAsync();
        await setup.Coordinator.OnChildStartedAsync();

        Assert.True(await setup.Coordinator.CommitPendingForRestartExitAsync());
        Assert.Equal(ActivationStates.Activating, (await setup.Store.LoadAsync())!.State);
        Assert.False(await setup.Coordinator.CommitPendingForRestartExitAsync());
        setup.Coordinator.StopForSessionEnd();

        var resumed = new ActivationCoordinator(setup.Store, setup.Hooks, setup.Host, setup.Tracker);
        var resume = await resumed.ResumeBeforeChildAsync();
        Assert.False(resume.NeedsRepair);
        Assert.Equal(ActivationStates.Verifying, resume.Record!.State);
        Assert.Contains("apply-target", setup.Hooks.Phases);
    }

    [Fact]
    public async Task Empty_Edge_Pending_Activation_Does_Not_Commit_On_An_Unrelated_Restart()
    {
        var setup = await SetupActivatingAsync(stopAtPending: true,
            restart: new ActivationRestart { Preset = "empty", Edge = "empty", MaxPlayers = 0, Rounds = null });

        Assert.False(await setup.Coordinator.CommitPendingForRestartExitAsync());
        Assert.Equal(ActivationStates.Pending, (await setup.Store.LoadAsync())!.State);
    }

    [Fact]
    public async Task Reschedule_To_Immediate_Restarts_Now_With_Rnr_Already_Armed()
    {
        var setup = await SetupNextRoundAsync();
        await setup.Coordinator.OnChildStartedAsync();
        var before = (await setup.Store.LoadAsync())!;

        var after = await setup.Coordinator.RescheduleAsync(before.ActivationId, before.Revision, "immediate");

        Assert.Equal(ActivationStates.Activating, after.State);
        Assert.Equal("immediate", after.Restart!.Edge);
        Assert.Equal(before.ActivationId, after.ActivationId);
        Assert.Equal(Pending, after.PendingCheckpoint);
        Assert.True(setup.Host.ManagedRestartCommitInvoked);
        // rnr was armed by the next-round edge; the immediate edge only adds the round restart.
        Assert.Equal(new[] { "rnr", "restart" }, setup.Host.Commands);
        setup.Coordinator.StopForSessionEnd();
    }

    [Fact]
    public async Task Reschedule_Rejects_Stale_Revision_Same_Edge_And_Unknown_Edge()
    {
        var setup = await SetupNextRoundAsync();
        var before = (await setup.Store.LoadAsync())!;

        await Assert.ThrowsAsync<ActivationProtocolException>(() =>
            setup.Coordinator.RescheduleAsync(before.ActivationId, before.Revision - 1, "immediate"));
        await Assert.ThrowsAsync<ActivationProtocolException>(() =>
            setup.Coordinator.RescheduleAsync(before.ActivationId, before.Revision, "round-end"));
        await Assert.ThrowsAsync<ActivationProtocolException>(() =>
            setup.Coordinator.RescheduleAsync(before.ActivationId, before.Revision, "empty"));
        Assert.Equal(before.Revision, (await setup.Store.LoadAsync())!.Revision);
        Assert.Empty(setup.Host.Commands);
    }

    [Fact]
    public async Task Cancel_Disarms_Only_The_Rnr_LocalAdmin_Armed()
    {
        var setup = await SetupNextRoundAsync();
        await setup.Coordinator.OnChildStartedAsync();
        var record = (await setup.Store.LoadAsync())!;

        Assert.Equal(ActivationStates.Verified, (await setup.Coordinator.CancelAsync(record.ActivationId)).State);
        Assert.Equal(new[] { "rnr", "rnr" }, setup.Host.Commands);
        Assert.False(setup.Host.NativeRnr.Armed);

        var operatorArmed = await SetupNextRoundAsync(SecondRoot);
        operatorArmed.Host.NativeRnr.ObserveExitAction(LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Restart);
        await operatorArmed.Coordinator.OnChildStartedAsync();
        var pending = (await operatorArmed.Store.LoadAsync())!;
        await operatorArmed.Coordinator.CancelAsync(pending.ActivationId);
        Assert.Empty(operatorArmed.Host.Commands);
        Assert.True(operatorArmed.Host.NativeRnr.Armed);
    }

    [Fact]
    public async Task Cancel_Retries_A_Failed_Cleanup_Without_Waiting_For_A_New_Session()
    {
        var setup = await SetupNextRoundAsync();
        await setup.Coordinator.OnChildStartedAsync();
        var pending = (await setup.Store.LoadAsync())!;
        setup.Hooks.FailCleanup = true;

        var stuck = await setup.Coordinator.CancelAsync(pending.ActivationId);
        Assert.Equal(ActivationStates.Cancelled, stuck.State);
        Assert.NotNull(stuck.Lock);
        await Assert.ThrowsAsync<ActivationProtocolException>(() => setup.Coordinator.CancelAsync(Guid.NewGuid().ToString("D")));

        setup.Hooks.FailCleanup = false;
        var released = await setup.Coordinator.CancelAsync(pending.ActivationId);
        Assert.Equal(ActivationStates.Verified, released.State);
        Assert.Null(released.Lock);
        Assert.Equal(Current, released.CurrentCheckpoint);
        Assert.Equal(2, setup.Hooks.Phases.FindAll(phase => phase == "cleanup").Count);
        setup.Coordinator.StopForSessionEnd();
    }

    [Fact]
    public async Task Pending_Replacement_Preserves_Intent_And_Next_Round_Activates_New_Batch()
    {
        var setup = await SetupNextRoundAsync();
        var before = (await setup.Store.LoadAsync())!;
        await setup.Coordinator.OnChildStartedAsync();
        var nextId = Guid.NewGuid().ToString("D");
        var nextCheckpoint = new string('c', 64);
        var after = await setup.Coordinator.ReplacePendingAsync(before.ActivationId, Pending,
            nextId, nextCheckpoint, before.Revision);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(ActivationStates.Pending, after.State);
        Assert.Equal(before.Restart, after.Restart);
        Assert.Equal(before.PreparedAt, after.PreparedAt);
        Assert.Equal(before.Lock!.AcquiredAt, after.Lock!.AcquiredAt);
        Assert.Equal(Current, after.PreviousCheckpoint);
        Assert.Equal(Current, after.CurrentCheckpoint);
        Assert.False(setup.Host.ManagedRestartCommitInvoked);
        setup.Coordinator.NotifyRoundRestart();
        var activated = (await setup.Store.LoadAsync())!;
        Assert.Equal(ActivationStates.Activating, activated.State);
        Assert.Equal(nextId, activated.ActivationId);
        Assert.Equal(nextCheckpoint, activated.PendingCheckpoint);
        setup.Coordinator.StopForSessionEnd();
    }

    [Fact]
    public async Task Replacement_Preflight_Failure_Leaves_Original_Queue_Armed()
    {
        var setup = await SetupNextRoundAsync();
        var before = (await setup.Store.LoadAsync())!;
        setup.Hooks.FailPreflight = true;
        await Assert.ThrowsAsync<ActivationProtocolException>(() => setup.Coordinator.ReplacePendingAsync(
            before.ActivationId, Pending, Guid.NewGuid().ToString("D"), new string('c', 64), before.Revision));
        var after = (await setup.Store.LoadAsync())!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.ActivationId, after.ActivationId);
        Assert.Equal(ActivationStates.Pending, after.State);
        Assert.Equal(Pending, after.PendingCheckpoint);
        Assert.Equal(before.Restart, after.Restart);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("checkpoint")]
    [InlineData("activation")]
    [InlineData("edge")]
    public async Task Replacement_Rejects_Stale_Identity_And_Unsupported_Edge(string mismatch)
    {
        var setup = mismatch == "edge" ? await SetupActivatingAsync(stopAtPending: true) : await SetupNextRoundAsync();
        var before = (await setup.Store.LoadAsync())!;
        await Assert.ThrowsAsync<ActivationProtocolException>(() => setup.Coordinator.ReplacePendingAsync(
            mismatch == "activation" ? Guid.NewGuid().ToString("D") : before.ActivationId,
            mismatch == "checkpoint" ? Current : Pending,
            Guid.NewGuid().ToString("D"), new string('c', 64),
            mismatch == "revision" ? before.Revision - 1 : before.Revision));
        Assert.Equal(before.Revision, (await setup.Store.LoadAsync())!.Revision);
    }

    [Fact]
    public async Task Round_Edge_During_Replacement_Preflight_Wins_Without_Losing_Old_Plan()
    {
        var setup = await SetupNextRoundAsync();
        var before = (await setup.Store.LoadAsync())!;
        setup.Hooks.BeforeReplacement = async () =>
        {
            await setup.Store.UpdateAsync(before.Revision, r => r! with { State = ActivationStates.Activating });
        };
        await Assert.ThrowsAsync<ActivationProtocolException>(() => setup.Coordinator.ReplacePendingAsync(
            before.ActivationId, Pending, Guid.NewGuid().ToString("D"), new string('c', 64), before.Revision));
        var after = (await setup.Store.LoadAsync())!;
        Assert.Equal(ActivationStates.Activating, after.State);
        Assert.Equal(before.ActivationId, after.ActivationId);
        Assert.Equal(Pending, after.PendingCheckpoint);
    }

    private Task<Setup> SetupNextRoundAsync(string? root = null) => SetupActivatingAsync(stopAtPending: true,
        restart: new ActivationRestart { Preset = "next-round", Edge = "round-end", Rounds = 1, MaxPlayers = null }, root: root);

    private string SecondRoot => Path.Combine(_root, "second");

    private sealed record Setup(
        ActivationRecordStore Store,
        ActivationCoordinator Coordinator,
        FakeHooks Hooks,
        FakeHost Host,
        PlayerCountTracker Tracker);

    private sealed class FakeHooks : IActivationHookRunner
    {
        internal readonly List<string> Phases = new();
        internal Func<Task>? BeforeReplacement;
        internal bool VerifyPendingOk = true;
        internal bool VerifyPreviousOk = true;
        internal bool NullCheckpointOnFailure;
        internal bool IncludeVerificationProof = true;
        internal bool FailRollbackApply;
        internal bool FailCleanup;
        internal bool FailPreflight;
        internal TimeSpan VerificationDelay;

        public async Task<ActivationHookResult> RunAsync(string phase, ActivationRecord record, CancellationToken cancellationToken)
        {
            lock (Phases)
                Phases.Add(phase);
            if (phase == "verify-runtime" && VerificationDelay > TimeSpan.Zero)
                await Task.Delay(VerificationDelay, cancellationToken);
            if (phase == "preflight-replacement" && BeforeReplacement is not null) await BeforeReplacement();
            var isPrevious = record.VerificationTarget == "previous" || phase == "apply-rollback";
            var ok = phase switch
            {
                "preflight" or "preflight-replacement" => !FailPreflight,
                "apply-rollback" => !FailRollbackApply,
                "cleanup" => !FailCleanup,
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
        internal readonly List<string> Commands = new();
        internal readonly NativeRestartNextRoundTracker NativeRnr = new();
        internal bool ConsoleConnected = true;
        public bool EnsureNativeRestartNextRoundArmed() => ConsoleConnected && NativeRnr.EnsureArmed(Send);
        public bool DisarmNativeRestartNextRoundIfOwned() => ConsoleConnected && NativeRnr.DisarmIfOwned(Send);
        public string DescribeNativeRestartNextRound() => NativeRnr.Describe();
        private bool Send(string command)
        {
            lock (Commands)
                Commands.Add(command);
            return true;
        }
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
            if (!commitActivation())
                return false;
            // Mirrors LocalAdmin.RequestScheduledRestart: the tracked native sequence.
            return NativeRnr.TrySendRestartSequence(Send);
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
