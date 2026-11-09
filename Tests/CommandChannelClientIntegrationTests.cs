using LocalAdmin.V2.Core.CommandChannel;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class CommandChannelClientIntegrationTests
{
    [Fact]
    public async Task Consecutive_Batch_Completes_On_Barrier_Without_Waiting_For_Silence()
    {
        var baseName = "LocalAdmin-Test-" + Guid.NewGuid().ToString("N");
        var received = new List<string>();
        CommandChannelServer? server = null;
        server = new CommandChannelServer(baseName, 7777, command =>
        {
            received.Add(command);
            server!.Broadcast(">>> " + command);
            server.Broadcast(command.StartsWith("echo ctl-", StringComparison.Ordinal)
                ? "ECHO#Echo response: " + command.Substring(5)
                : "reply: " + command);
        });
        var output = new StringWriter();
        var error = new StringWriter();
        server.Start();
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await CommandChannelClient.SendAsync(new CommandChannelClientOptions(
                7777, "first\nsecond\nthird", baseName, null, 2000, 0, 3000), output, error);
            Assert.Equal(0, result);
            Assert.Equal(new[] { "first", "second", "third" }, received.GetRange(0, 3));
            Assert.Equal("reply: first" + Environment.NewLine + "reply: second" + Environment.NewLine + "reply: third" + Environment.NewLine, output.ToString());
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(watch.ElapsedMilliseconds < 1500, "Completion unexpectedly waited for silence");
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Native_Client_Connects_Sends_Anchors_And_Reads_Reply()
    {
        var baseName = "LocalAdmin-Test-" + Guid.NewGuid().ToString("N");
        CommandChannelServer? server = null;
        server = new CommandChannelServer(baseName, 7777, command =>
        {
            server!.Broadcast(">>> " + command);
            server.Broadcast("List of players (6):");
        });

        var output = new StringWriter();
        var error = new StringWriter();
        server.Start();

        try
        {
            var result = await CommandChannelClient.SendAsync(
                new CommandChannelClientOptions(
                    7777,
                    "players",
                    baseName,
                    null,
                    ConnectTimeoutMs: 2000,
                    QuietMs: 100,
                    TimeoutMs: 3000),
                output,
                error);

            Assert.Equal(CommandChannelClient.SuccessExitCode, result);
            Assert.Equal("List of players (6):" + Environment.NewLine, output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            server.Stop();
        }
    }
    [Fact]
    public async Task Large_Unicode_Profile_Command_Crosses_The_Named_Pipe_Unchanged()
    {
        var baseName = "LocalAdmin-Long-" + Guid.NewGuid().ToString("N");
        string json = "{\"v\":2,\"label\":\"金色称号\",\"color\":\"yellow\",\"listText\":\"" + new string('金', 4096) + "\",\"hoverText\":\"<b>金色</b>\"}";
        string command = "/pbadge profile setsaved 76561198000000000@steam " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.True(command.Length > 8192);
        var received = new List<string>();
        CommandChannelServer? server = null;
        server = new CommandChannelServer(baseName, 7777, line =>
        {
            received.Add(line);
            server!.Broadcast(">>> " + line);
            server.Broadcast(line.StartsWith("echo ctl-", StringComparison.Ordinal)
                ? "ECHO#Echo response: " + line.Substring(5) : "profile accepted");
        });
        var output = new StringWriter(); var error = new StringWriter();
        server.Start();
        try
        {
            int result = await CommandChannelClient.SendAsync(new CommandChannelClientOptions(
                7777, command, baseName, null, 2000, 0, 5000), output, error);
            Assert.Equal(0, result);
            Assert.Equal(command, received[0]);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("profile accepted", output.ToString());
        }
        finally { server.Stop(); }
    }

}
