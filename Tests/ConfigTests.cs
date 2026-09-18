using LocalAdmin.V2.IO;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Dev_Keeps_Typed_Lane_Identity_Without_Managed_Activation()
    {
        var cfg = Config.DeserializeConfig(new[] { "ops_lane: oa/dev", "ops_managed_activation_enabled: false" });
        Assert.False(cfg.UsesManagedActivation);
        var roundtrip = Config.DeserializeConfig(cfg.SerializeConfig().Split('\n'));
        Assert.False(roundtrip.UsesManagedActivation);
        Assert.Equal("oa/dev", roundtrip.OpsLane);
        Assert.True(Config.DeserializeConfig(new[] { "ops_lane: oa/prod" }).UsesManagedActivation);
        Assert.True(Config.DeserializeConfig(new[] { "ops_lane: oa/prod", "ops_managed_activation_enabled: invalid" }).UsesManagedActivation);
    }

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
            OpsLane = "oa/prod",
            OpsStateRoot = "/var/lib/scpsl-ops-test",
            OpsCliPath = "/usr/local/bin/scpsl-ops",
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
        Assert.Equal("oa/prod", roundTripped.OpsLane);
        Assert.Equal("/var/lib/scpsl-ops-test", roundTripped.OpsStateRoot);
        Assert.Equal("/usr/local/bin/scpsl-ops", roundTripped.OpsCliPath);
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
