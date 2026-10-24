using LocalAdmin.V2.Core.CommandChannel;
using System;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class CommandChannelClientCliTests
{
    [Fact]
    public void Only_Explicit_Ctl_First_Argument_Selects_Client_Mode()
    {
        Assert.True(CommandChannelClientCli.IsInvocation(new[] { "ctl", "7777", "players" }));
        Assert.True(CommandChannelClientCli.IsInvocation(new[] { "CTL", "7777", "players" }));
        Assert.False(CommandChannelClientCli.IsInvocation(new[] { "7777", "ctl" }));
        Assert.False(CommandChannelClientCli.IsInvocation(new[] { "--acceptEULA" }));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("console")]
    [InlineData("local")]
    [InlineData("localadmin")]
    public void Raw_Mode_Aliases_Preserve_Command(string mode)
    {
        var success = CommandChannelClientCli.TryParse(
            new[] { "7777", mode, "restartwhen", "status" },
            out var options,
            out var error,
            out var showHelp);

        Assert.True(success, error);
        Assert.False(showHelp);
        Assert.NotNull(options);
        Assert.Equal((ushort)7777, options.Port);
        Assert.Equal("restartwhen status", options.Command);
    }

    [Fact]
    public void Raw_Mode_Is_Default_After_Positional_Port()
    {
        Assert.True(CommandChannelClientCli.TryParse(
            new[] { "7777", "players" },
            out var options,
            out var error,
            out _), error);

        Assert.Equal("players", options!.Command);
        Assert.Equal(0, options.QuietMs);
    }

    [Theory]
    [InlineData("pm", "reload", "/pm reload")]
    [InlineData("/pm", "reload", "/pm reload")]
    public void Ra_Mode_Ensures_Exactly_One_Leading_Slash(string first, string second, string expected)
    {
        Assert.True(CommandChannelClientCli.TryParse(
            new[] { "--port", "8888", "ra", first, second },
            out var options,
            out var error,
            out _), error);

        Assert.Equal((ushort)8888, options!.Port);
        Assert.Equal(expected, options.Command);
    }

    [Fact]
    public void Parses_Client_Timing_And_Pipe_Options()
    {
        Assert.True(CommandChannelClientCli.TryParse(
            new[]
            {
                "--port", "7777",
                "--base-name", "Custom",
                "--pipe-name", "ExactPipe",
                "--connect-timeout-ms", "100",
                "--quiet-ms", "200",
                "--timeout-ms", "300",
                "console", "players"
            },
            out var options,
            out var error,
            out _), error);

        Assert.Equal("Custom", options!.BaseName);
        Assert.Equal("ExactPipe", options.PipeNameOverride);
        Assert.Equal(100, options.ConnectTimeoutMs);
        Assert.Equal(200, options.QuietMs);
        Assert.Equal(300, options.TimeoutMs);
    }

    [Theory]
    [InlineData("7777")]
    [InlineData("ra", "pm", "reload")]
    [InlineData("7777", "ra")]
    [InlineData("0", "players")]
    [InlineData("--port", "not-a-port", "players")]
    [InlineData("7777", "--quiet-ms", "-1", "players")]
    [InlineData("7777", "--quite-ms", "100", "players")]
    public void Rejects_Incomplete_Or_Invalid_Invocations(params string[] args)
    {
        Assert.False(CommandChannelClientCli.TryParse(args, out _, out var error, out _));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Commands_File_Preserves_Consecutive_Order()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "/god 2 enable\n\n/forcerole 3 Tutorial\n");
            Assert.True(CommandChannelClientCli.TryParse(new[] { "7777", "--commands-file", path },
                out var options, out var error, out _), error);
            Assert.Equal("/god 2 enable\n/forcerole 3 Tutorial", options!.Command);
            Assert.Equal(0, options.QuietMs);
        }
        finally { System.IO.File.Delete(path); }
    }
}
