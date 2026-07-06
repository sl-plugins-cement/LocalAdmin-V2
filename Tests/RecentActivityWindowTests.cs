using System;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class RecentActivityWindowTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Disabled_Window_Always_Covered_NeverBlocks()
    {
        var window = new RecentActivityWindow();
        window.AddSample(50, T0, windowMinutes: 0);
        var decision = window.Evaluate(T0, windowMinutes: 0, overPlayers: 5, requireFullWindow: true);

        Assert.True(decision.WindowCovered);
        Assert.False(decision.RecentHighActivity);
    }

    [Fact]
    public void RequireFullWindow_Blocks_Until_Observed_Long_Enough()
    {
        var window = new RecentActivityWindow();
        window.AddSample(0, T0, windowMinutes: 30);

        // Only 10 minutes observed of a 30-minute window -> not covered.
        var early = window.Evaluate(T0.AddMinutes(10), 30, overPlayers: 5, requireFullWindow: true);
        Assert.False(early.WindowCovered);

        // 30+ minutes observed -> covered.
        var later = window.Evaluate(T0.AddMinutes(31), 30, overPlayers: 5, requireFullWindow: true);
        Assert.True(later.WindowCovered);
    }

    [Fact]
    public void HighActivity_Detected_By_Peak()
    {
        var window = new RecentActivityWindow();
        window.AddSample(8, T0, windowMinutes: 30);
        window.AddSample(1, T0.AddMinutes(1), windowMinutes: 30);

        // Evaluate while the peak-8 sample is still inside the 30-minute window.
        var decision = window.Evaluate(T0.AddMinutes(5), 30, overPlayers: 5, requireFullWindow: false);
        Assert.True(decision.RecentHighActivity);
        Assert.Equal(8, decision.RecentMaxPlayers);
    }

    [Fact]
    public void Old_Samples_Are_Pruned_Out_Of_Window()
    {
        var window = new RecentActivityWindow();
        window.AddSample(20, T0, windowMinutes: 30);               // busy long ago
        window.AddSample(0, T0.AddMinutes(45), windowMinutes: 30); // empty now

        // At T0+45 the 30-min window no longer includes the old busy sample.
        var decision = window.Evaluate(T0.AddMinutes(45), 30, overPlayers: 5, requireFullWindow: false);
        Assert.False(decision.RecentHighActivity);
        Assert.Equal(0, decision.RecentMaxPlayers);
    }
}
