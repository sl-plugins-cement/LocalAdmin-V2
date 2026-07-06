using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class RestartWhenEmptyOptionsTests
{
    [Fact]
    public void Clamps_Invalid_Values()
    {
        var options = RestartWhenEmptyOptions.OneShot(
            restartAtOrBelowPlayers: 1,
            emptyConfirmations: 0,   // -> clamped to 1
            tickSeconds: 0,          // -> clamped to 1
            recentActivityOverPlayers: 5,
            recentActivityWindowMinutes: 30,
            durationSeconds: -5);    // -> clamped to 0

        Assert.Equal(1, options.EmptyConfirmations);
        Assert.Equal(1, options.TickSeconds);
        Assert.Equal(0, options.DurationSeconds);
    }

    [Fact]
    public void Summary_Mentions_Duration_When_Set()
    {
        var withDuration = RestartWhenEmptyOptions.OneShot(1, 2, 30, 5, 30, durationSeconds: 120);
        Assert.Contains("120s sustained", withDuration.Summary);

        var withoutDuration = RestartWhenEmptyOptions.OneShot(1, 2, 30, 5, 30, durationSeconds: 0);
        Assert.DoesNotContain("sustained", withoutDuration.Summary);
    }

    [Fact]
    public void Summary_Notes_Disabled_Activity_Guard()
    {
        var options = RestartWhenEmptyOptions.OneShot(1, 2, 30, 5, recentActivityWindowMinutes: 0, durationSeconds: 0);
        Assert.Contains("recent-activity guard disabled", options.Summary);
    }
}
