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
}
