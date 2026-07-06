using LocalAdmin.V2.IO;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Roundtrips_Serialize_Then_Deserialize()
    {
        var cfg = new Config
        {
            CommandChannelEnabled = true,
            AutoRestartEmptyEnabled = true,
            RestartAtOrBelowPlayers = 2,
            EmptyConfirmations = 3,
            EmptyDurationSeconds = 90,
            PolicyTickSeconds = 15,
            RecentActivityOverPlayers = 8,
            RecentActivityWindowMinutes = 45,
            CommandChannelPipeName = "MyPipe",
        };

        var roundTripped = Config.DeserializeConfig(cfg.SerializeConfig().Split('\n'));

        Assert.True(roundTripped.CommandChannelEnabled);
        Assert.True(roundTripped.AutoRestartEmptyEnabled);
        Assert.Equal(2, roundTripped.RestartAtOrBelowPlayers);
        Assert.Equal(3, roundTripped.EmptyConfirmations);
        Assert.Equal(90, roundTripped.EmptyDurationSeconds);
        Assert.Equal(15, roundTripped.PolicyTickSeconds);
        Assert.Equal(8, roundTripped.RecentActivityOverPlayers);
        Assert.Equal(45, roundTripped.RecentActivityWindowMinutes);
        Assert.Equal("MyPipe", roundTripped.CommandChannelPipeName);
    }

    [Fact]
    public void Preserves_Path_Value_Containing_Colon_Space()
    {
        // ": " is legal inside a POSIX path; the parser must split on the FIRST ": " only.
        var path = "/home/user/my: server/SCPSL.x86_64";
        var cfg = Config.DeserializeConfig(new[] { $"server_application_path: {path}" });

        Assert.Equal(path, cfg.ServerApplicationPath);
    }

    [Fact]
    public void Clamps_Empty_Duration_Seconds_Negative_To_Zero()
    {
        var cfg = Config.DeserializeConfig(new[] { "empty_duration_seconds: -10" });
        Assert.Equal(0, cfg.EmptyDurationSeconds);
    }

    [Fact]
    public void Ignores_Lines_Without_Separator()
    {
        var cfg = Config.DeserializeConfig(new[] { "this is not a config line", "# comment" });
        // Defaults preserved.
        Assert.Equal(1, cfg.RestartAtOrBelowPlayers);
    }
}
