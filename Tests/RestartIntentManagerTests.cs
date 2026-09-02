using System;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class RestartIntentManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeHost : IRestartHost
    {
        internal int ScheduledRestarts;
        internal int RefreshRequests;
        internal bool AcceptRestart = true;
        internal bool ShuttingDown;
        internal bool RestartOwned;

        public bool TryScheduleGracefulRestart(string reason)
        {
            if (!AcceptRestart)
                return false;
            ScheduledRestarts++;
            return true;
        }

        public void RequestPlayerCountRefresh() => RefreshRequests++;
        public bool IsShuttingDown => ShuttingDown;
        public bool RestartInProgress => RestartOwned;
        public bool ConfirmNotShuttingDown() => !ShuttingDown;
    }

    private static PlayerCountTracker TrackerWithFreshCount(int players, DateTime nowUtc)
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(nowUtc);
        tracker.ProcessConsoleLine($"List of players ({players}):", nowUtc);
        return tracker;
    }

    private static RestartCondition RoundCondition(int? maxPlayers = null, int rounds = 1) =>
        new() { Edge = RestartEdge.RoundEnd, MaxPlayers = maxPlayers, Rounds = rounds };

    [Fact]
    public void Arm_Status_Cancel_Roundtrip()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = new PlayerCountTracker();

        Assert.True(manager.TryArm(host, tracker, "custom-round", RoundCondition(), out var armMessage));
        Assert.Contains("custom-round", armMessage);
        Assert.Contains("custom-round", manager.GetStatus(tracker));

        Assert.True(manager.TryCancel(host, out _));
        Assert.Contains("No restart intent", manager.GetStatus(tracker));
    }

    [Fact]
    public void Refuses_Second_Intent_While_One_Is_Armed()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = new PlayerCountTracker();

        Assert.True(manager.TryArm(host, tracker, "custom-round", RoundCondition(), out _));
        Assert.False(manager.TryArm(host, tracker, "round-empty-gated", RoundCondition(0), out var message));
        Assert.Contains("already armed", message);
    }

    [Fact]
    public void Refuses_Arm_While_ShuttingDown_Or_RestartInProgress()
    {
        var manager = new RestartIntentManager();
        var tracker = new PlayerCountTracker();

        Assert.False(manager.TryArm(new FakeHost { ShuttingDown = true }, tracker, "custom-round", RoundCondition(), out _));
        Assert.False(manager.TryArm(new FakeHost { RestartOwned = true }, tracker, "custom-round", RoundCondition(), out _));
    }

    [Fact]
    public void Ungated_Round_Intent_Fires_And_Consumes()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = new PlayerCountTracker();

        Assert.True(manager.TryArm(host, tracker, "custom-round", RoundCondition(), out _));
        manager.NotifyRoundRestart(host, tracker, T0);

        Assert.Equal(1, host.ScheduledRestarts);
        Assert.Contains("No restart intent", manager.GetStatus(tracker));
    }

    [Fact]
    public void Round_Intent_Counts_Down_Multiple_Rounds()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = new PlayerCountTracker();

        Assert.True(manager.TryArm(host, tracker, "custom-three-rounds", RoundCondition(rounds: 3), out _));

        manager.NotifyRoundRestart(host, tracker, T0);
        manager.NotifyRoundRestart(host, tracker, T0.AddMinutes(10));
        Assert.Equal(0, host.ScheduledRestarts);

        manager.NotifyRoundRestart(host, tracker, T0.AddMinutes(20));
        Assert.Equal(1, host.ScheduledRestarts);
    }

    [Fact]
    public void Gated_Round_Intent_Skips_When_Count_Above_Gate()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = TrackerWithFreshCount(7, T0);

        Assert.True(manager.TryArm(host, tracker, "next-round-lowpop", RoundCondition(maxPlayers: 4), out _));
        manager.NotifyRoundRestart(host, tracker, T0.AddSeconds(5));

        Assert.Equal(0, host.ScheduledRestarts);
        Assert.Contains("next-round-lowpop", manager.GetStatus(tracker)); // still armed
    }

    [Fact]
    public void Gated_Round_Intent_Fires_When_Count_At_Or_Below_Gate()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = TrackerWithFreshCount(3, T0);

        Assert.True(manager.TryArm(host, tracker, "next-round-lowpop", RoundCondition(maxPlayers: 4), out _));
        manager.NotifyRoundRestart(host, tracker, T0.AddSeconds(5));

        Assert.Equal(1, host.ScheduledRestarts);
        Assert.Contains("No restart intent", manager.GetStatus(tracker));
    }

    [Fact]
    public void Gated_Round_Intent_Skips_On_Stale_Count_And_Requests_Refresh()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = TrackerWithFreshCount(0, T0);

        Assert.True(manager.TryArm(host, tracker, "round-empty-gated", RoundCondition(maxPlayers: 0), out _));

        // Count is 10 minutes old at the edge: stale -> must skip and stay armed, never fire.
        var refreshesBefore = host.RefreshRequests;
        manager.NotifyRoundRestart(host, tracker, T0.AddMinutes(10));

        Assert.Equal(0, host.ScheduledRestarts);
        Assert.True(host.RefreshRequests > refreshesBefore);
        Assert.Contains("round-empty-gated", manager.GetStatus(tracker));
    }

    [Fact]
    public void Round_Intent_Stays_Armed_When_Restart_Not_Accepted()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost { AcceptRestart = false };
        var tracker = new PlayerCountTracker();

        Assert.True(manager.TryArm(host, tracker, "custom-round", RoundCondition(), out _));
        manager.NotifyRoundRestart(host, tracker, T0);

        Assert.Equal(0, host.ScheduledRestarts);
        Assert.Contains("custom-round", manager.GetStatus(tracker)); // retried on the next round
    }

    [Fact]
    public void RoundRestart_Ignored_When_LowActivity_Intent_Armed()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = new PlayerCountTracker();

        RestartPresets.TryGet("empty", out var preset);
        Assert.True(manager.TryArm(host, tracker, preset.Name, Assert.IsType<RestartCondition>(preset.Condition), out _));

        manager.NotifyRoundRestart(host, tracker, T0);
        Assert.Equal(0, host.ScheduledRestarts);

        Assert.True(manager.TryCancel(host, out _)); // cleanup: stop the live watchdog
    }
}
