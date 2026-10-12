using System;
using System.Threading.Tasks;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class PlayerProbeCoordinatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Accepts_Correlated_Zero_Player_Response()
    {
        var tracker = new PlayerCountTracker();
        var sends = 0;
        var coordinator = new PlayerProbeCoordinator(tracker, () => { sends++; return true; });

        var task = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0);
        tracker.ProcessConsoleLine("List of players (0):", T0.AddMilliseconds(10), out var accepted);
        Assert.Equal(0, accepted);
        coordinator.AcceptReply(accepted!.Value, T0.AddMilliseconds(10));

        var result = await task;
        Assert.Equal(0, result.Players);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Concurrent_Callers_Share_One_Poll()
    {
        var tracker = new PlayerCountTracker();
        var sends = 0;
        var coordinator = new PlayerProbeCoordinator(tracker, () => { sends++; return true; });

        var first = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0);
        var second = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0.AddMilliseconds(1));
        tracker.ProcessConsoleLine("List of players (4):", T0.AddMilliseconds(10), out var accepted);
        coordinator.AcceptReply(accepted!.Value, T0.AddMilliseconds(10));

        Assert.Equal((await first).ProbeId, (await second).ProbeId);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Failed_Send_Fails_Probe()
    {
        var coordinator = new PlayerProbeCoordinator(new PlayerCountTracker(), () => false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0));
    }

    [Fact]
    public async Task Session_Reset_Cancels_Outstanding_Probe()
    {
        var coordinator = new PlayerProbeCoordinator(new PlayerCountTracker(), () => true);
        var task = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0);
        coordinator.ResetSession();

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    private static PlayerProbeCoordinator NewCoordinator(out PlayerCountTracker tracker)
    {
        tracker = new PlayerCountTracker();
        return new PlayerProbeCoordinator(tracker, () => true);
    }

    [Fact]
    public async Task Quiet_Probe_Suppresses_Single_Message_Reply_And_Still_Completes()
    {
        var coordinator = NewCoordinator(out var tracker);
        var task = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);

        // One message holding the header and the entries (Carl Mod).
        var message = "List of players (2):\n- alpha: 111@steam [1]\n- beta: 222@steam [2]";
        Assert.True(coordinator.TrySuppressQuietReply(message, T0.AddMilliseconds(5), out var trackerLine));
        Assert.Equal("List of players (2):", trackerLine);

        tracker.ProcessConsoleLine(trackerLine!, T0.AddMilliseconds(5), out var accepted);
        coordinator.AcceptReply(accepted!.Value, T0.AddMilliseconds(5));
        Assert.Equal(2, (await task).Players);
    }

    [Fact]
    public async Task Quiet_Probe_Suppresses_Header_Then_Separate_Entries_Message_Once()
    {
        var coordinator = NewCoordinator(out var tracker);
        var task = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);

        Assert.True(coordinator.TrySuppressQuietReply("List of players (2):", T0.AddMilliseconds(5), out var trackerLine));
        Assert.Equal("List of players (2):", trackerLine);
        tracker.ProcessConsoleLine(trackerLine!, T0.AddMilliseconds(5), out var accepted);
        coordinator.AcceptReply(accepted!.Value, T0.AddMilliseconds(5));
        await task;

        // The entries message follows the header; it is hidden and not fed to the tracker.
        Assert.True(coordinator.TrySuppressQuietReply(
            "- alpha: 111@steam [1]\n- beta: 222@steam [2]", T0.AddMilliseconds(8), out var entriesLine));
        Assert.Null(entriesLine);

        // Only that one message belongs to the reply.
        Assert.False(coordinator.TrySuppressQuietReply("- unrelated bullet", T0.AddMilliseconds(9), out _));
    }

    [Fact]
    public void Quiet_Probe_Empty_Server_Has_No_Entries_Message_To_Hide()
    {
        var coordinator = NewCoordinator(out _);
        _ = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);

        Assert.True(coordinator.TrySuppressQuietReply("List of players (0):", T0.AddMilliseconds(5), out _));
        Assert.False(coordinator.TrySuppressQuietReply("- unrelated bullet", T0.AddMilliseconds(6), out _));
    }

    [Fact]
    public void Quiet_Entries_Message_Is_Shown_After_The_Continuation_Window()
    {
        var coordinator = NewCoordinator(out _);
        _ = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);

        Assert.True(coordinator.TrySuppressQuietReply("List of players (1):", T0.AddMilliseconds(5), out _));
        Assert.False(coordinator.TrySuppressQuietReply("- alpha: 111@steam [1]", T0.AddSeconds(3), out _));
    }

    [Fact]
    public void Non_Quiet_Probe_Reply_Is_Not_Suppressed()
    {
        var coordinator = NewCoordinator(out _);
        _ = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0);

        Assert.False(coordinator.TrySuppressQuietReply("List of players (0):", T0.AddMilliseconds(5), out var trackerLine));
        Assert.Null(trackerLine);
    }

    [Fact]
    public void Reply_Without_A_Pending_Probe_Is_Not_Suppressed()
    {
        var coordinator = NewCoordinator(out _);

        Assert.False(coordinator.TrySuppressQuietReply("List of players (0):", T0, out _));
    }

    [Fact]
    public async Task Reply_After_The_Quiet_Probe_Completed_Is_Not_Suppressed()
    {
        var coordinator = NewCoordinator(out var tracker);
        var task = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);
        Assert.True(coordinator.TrySuppressQuietReply("List of players (0):", T0.AddMilliseconds(5), out var trackerLine));
        tracker.ProcessConsoleLine(trackerLine!, T0.AddMilliseconds(5), out var accepted);
        coordinator.AcceptReply(accepted!.Value, T0.AddMilliseconds(5));
        await task;

        // A manually typed `players` afterwards is a new, non-quiet poll whose reply must print.
        _ = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0.AddSeconds(1));
        Assert.False(coordinator.TrySuppressQuietReply("List of players (0):", T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Non_Quiet_Caller_Joining_A_Quiet_Probe_Makes_The_Shared_Reply_Visible()
    {
        var coordinator = NewCoordinator(out _);
        _ = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);
        _ = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0.AddMilliseconds(1));

        Assert.False(coordinator.TrySuppressQuietReply("List of players (0):", T0.AddMilliseconds(5), out _));
    }

    [Fact]
    public void Quiet_Caller_Joining_A_Non_Quiet_Probe_Does_Not_Hide_The_Reply()
    {
        var coordinator = NewCoordinator(out _);
        _ = coordinator.ProbeAsync(TimeSpan.FromSeconds(1), T0);
        _ = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0.AddMilliseconds(1));

        Assert.False(coordinator.TrySuppressQuietReply("List of players (0):", T0.AddMilliseconds(5), out _));
    }

    [Theory]
    [InlineData("Round started")]
    [InlineData("List of players (0): authenticated from endpoint 1.2.3.4:5678. Player ID assigned: 7.")]
    [InlineData("List of players (2):\nchat: hello")]
    [InlineData("alpha chatted\nList of players (0):")]
    public void Quiet_Probe_Only_Hides_The_Exact_Reply_Shape(string message)
    {
        var coordinator = NewCoordinator(out _);
        _ = coordinator.ProbeQuietAsync(TimeSpan.FromSeconds(1), T0);

        Assert.False(coordinator.TrySuppressQuietReply(message, T0.AddMilliseconds(5), out _));
    }
}
