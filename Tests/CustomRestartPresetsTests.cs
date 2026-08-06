using System;
using System.IO;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class CustomRestartPresetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "la-presets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string Write(string name, string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Parses_RoundEnd_With_Gate()
    {
        var ok = CustomRestartPresets.TryParse(
            """{ "summary": "custom", "edge": "round-end", "maxPlayers": 2 }""",
            out var condition, out var summary, out _);

        Assert.True(ok);
        Assert.Equal(RestartEdge.RoundEnd, condition.Edge);
        Assert.Equal(2, condition.MaxPlayers);
        Assert.Equal("custom", summary);
    }

    [Fact]
    public void Parses_LowActivity_And_Defaults_Summary_To_Describe()
    {
        var ok = CustomRestartPresets.TryParse(
            """{ "edge": "low-activity", "maxPlayers": 1, "sustainedSeconds": 600 }""",
            out var condition, out var summary, out _);

        Assert.True(ok);
        Assert.Equal(RestartEdge.LowActivityTick, condition.Edge);
        Assert.Equal(600, condition.SustainedSeconds);
        Assert.False(string.IsNullOrWhiteSpace(summary));
    }

    [Theory]
    [InlineData("""{ "maxPlayers": 2 }""")]                          // missing edge
    [InlineData("""{ "edge": "sometimes", "maxPlayers": 2 }""")]     // bad edge
    [InlineData("""{ "edge": "low-activity" }""")]                   // low-activity needs maxPlayers
    [InlineData("""{ "edge": "round-end", "rounds": 0 }""")]         // invalid rounds
    [InlineData("""not json""")]
    [InlineData("""{ "edge": "round-end", "maxPlayrs": 2 }""")]      // typo'd field name
    public void Rejects_Invalid_Documents(string json)
    {
        Assert.False(CustomRestartPresets.TryParse(json, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Loads_Preset_From_File_By_Name()
    {
        Write("late-night", """{ "edge": "round-end", "maxPlayers": 2 }""");

        Assert.True(CustomRestartPresets.TryLoad(_dir, "late-night", out var preset, out _));
        Assert.Equal("late-night", preset.Name);
        Assert.Equal(2, preset.Condition.MaxPlayers);
    }

    [Fact]
    public void Missing_File_And_Bad_Names_Are_Rejected()
    {
        Directory.CreateDirectory(_dir);

        Assert.False(CustomRestartPresets.TryLoad(_dir, "does-not-exist", out _, out var missingError));
        Assert.Contains("does-not-exist", missingError);

        // Path traversal / invalid characters must never reach the filesystem.
        Assert.False(CustomRestartPresets.TryLoad(_dir, "../evil", out _, out _));
        Assert.False(CustomRestartPresets.TryLoad(_dir, "a b", out _, out _));
        Assert.False(CustomRestartPresets.TryLoad(_dir, "", out _, out _));
    }

    [Fact]
    public void LoadAll_Reports_Invalid_Files_As_Problems()
    {
        Write("good", """{ "edge": "round-end" }""");
        Write("bad", """{ "edge": "nope" }""");

        var (presets, problems) = CustomRestartPresets.LoadAll(_dir);

        Assert.Single(presets);
        Assert.Equal("good", presets[0].Name);
        Assert.Single(problems);
        Assert.Contains("bad.json", problems[0]);
    }

    [Fact]
    public void LoadAll_Empty_When_Directory_Missing()
    {
        var (presets, problems) = CustomRestartPresets.LoadAll(Path.Combine(_dir, "nope"));
        Assert.Empty(presets);
        Assert.Empty(problems);
    }

    [Fact]
    public void Resolve_Prefers_BuiltIn_Over_Custom_File()
    {
        // A custom file named like a built-in must not shadow the vetted preset.
        Write("next-round", """{ "edge": "round-end", "maxPlayers": 0 }""");

        Assert.True(RestartPresets.TryResolve("next-round", _dir, out var preset, out _));
        Assert.Null(preset.Condition.MaxPlayers); // built-in next-round has no player gate
    }

    [Fact]
    public void Resolve_Falls_Back_To_Custom_File()
    {
        Write("my-preset", """{ "edge": "low-activity", "maxPlayers": 3, "sustainedSeconds": 300 }""");

        Assert.True(RestartPresets.TryResolve("my-preset", _dir, out var preset, out _));
        Assert.Equal(3, preset.Condition.MaxPlayers);
    }
}
