using LocalAdmin.V2.Core.CommandChannel;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class CommandChannelClientIntegrationTests
{
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
}
