using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class RestartPresetsTests
{
    [Fact]
    public void All_Presets_Are_Valid()
    {
        foreach (var preset in RestartPresets.List)
        {
            Assert.True(preset.Condition.Validate(out var error), $"{preset.Name}: {error}");
        }
    }

    [Fact]
    public void Preset_Names_Are_Unique_And_Resolvable_CaseInsensitive()
    {
        foreach (var preset in RestartPresets.List)
        {
            Assert.True(RestartPresets.TryGet(preset.Name.ToUpperInvariant(), out var resolved));
            Assert.Equal(preset.Name, resolved.Name);
        }
    }

    [Fact]
    public void Unknown_Preset_Is_Rejected()
    {
        Assert.False(RestartPresets.TryGet("no-such-preset", out _));
    }

    [Fact]
    public void NextRoundLowpop_Gates_Below_Five_Players()
    {
        Assert.True(RestartPresets.TryGet("next-round-lowpop", out var preset));
        var condition = Assert.IsType<RestartCondition>(preset.Condition);
        Assert.Equal(RestartEdge.RoundEnd, condition.Edge);
        Assert.Equal(4, condition.MaxPlayers);
    }

    [Fact]
    public void NextRound_Is_A_Tracked_Cancellable_Intent()
    {
        Assert.True(RestartPresets.TryGet("next-round", out var preset));
        var condition = Assert.IsType<RestartCondition>(preset.Condition);
        Assert.Equal(RestartEdge.RoundEnd, condition.Edge);
        Assert.Equal(1, condition.Rounds);
    }

    [Fact]
    public void Catalog_Contains_Only_Three_Clear_Choices()
    {
        Assert.Equal(new[] { "safe", "low-pop", "now" }, System.Linq.Enumerable.Select(RestartPresets.List, p => p.Name));
        foreach (var name in new[] { "empty", "low-30m", "quiet-hour" }) Assert.False(RestartPresets.TryGet(name, out _));
    }

    [Fact]
    public void Condition_Validation_Rejects_Bad_Values()
    {
        Assert.False(new RestartCondition { Edge = RestartEdge.RoundEnd, Rounds = 0 }.Validate(out _));
        Assert.False(new RestartCondition { Edge = RestartEdge.RoundEnd, MaxPlayers = -1 }.Validate(out _));
        Assert.False(new RestartCondition { Edge = RestartEdge.LowActivityTick }.Validate(out _)); // needs MaxPlayers
        Assert.False(new RestartCondition { Edge = RestartEdge.LowActivityTick, MaxPlayers = 0, TickSeconds = 0 }.Validate(out _));
    }
}
