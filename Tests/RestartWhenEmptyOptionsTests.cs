using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class RestartWhenEmptyOptionsTests
{
    private static RestartWhenEmptyOptions FromCondition(RestartCondition condition) =>
        RestartWhenEmptyOptions.FromCondition(condition);

    [Fact]
    public void Clamps_Invalid_Values()
    {
        var options = FromCondition(new RestartCondition
        {
            Edge = RestartEdge.LowActivityTick,
            MaxPlayers = 1,
            Confirmations = 0,    // -> clamped to 1
            TickSeconds = 0,      // -> clamped to 1
            SustainedSeconds = -5 // -> clamped to 0
        });

        Assert.Equal(1, options.EmptyConfirmations);
        Assert.Equal(1, options.TickSeconds);
        Assert.Equal(0, options.DurationSeconds);
    }

    [Fact]
    public void Summary_Mentions_Duration_When_Set()
    {
        var baseCondition = new RestartCondition
        {
            Edge = RestartEdge.LowActivityTick,
            MaxPlayers = 1,
            Confirmations = 2,
            TickSeconds = 30,
            ActivityOverPlayers = 5,
            ActivityWindowMinutes = 30
        };

        Assert.Contains("120s sustained", FromCondition(baseCondition with { SustainedSeconds = 120 }).Summary);
        Assert.DoesNotContain("sustained", FromCondition(baseCondition).Summary);
    }

    [Fact]
    public void Summary_Notes_Disabled_Activity_Guard()
    {
        var options = FromCondition(new RestartCondition
        {
            Edge = RestartEdge.LowActivityTick,
            MaxPlayers = 1,
            ActivityWindowMinutes = 0
        });

        Assert.Contains("recent-activity guard disabled", options.Summary);
    }
}
