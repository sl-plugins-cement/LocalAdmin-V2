using System.Collections.Generic;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class NativeRestartCommandTests
{
    [Fact]
    public void Sends_Rnr_Then_Restart_Without_Exit()
    {
        var commands = new List<string>();

        var accepted = LocalAdmin.V2.Core.LocalAdmin.TrySendNativeRestartCommands(command =>
        {
            commands.Add(command);
            return true;
        });

        Assert.True(accepted);
        Assert.Equal(new[] { "rnr", "restart" }, commands);
        Assert.DoesNotContain("exit", commands);
    }

    [Fact]
    public void Does_Not_Send_Restart_When_Rnr_Cannot_Be_Sent()
    {
        var commands = new List<string>();

        var accepted = LocalAdmin.V2.Core.LocalAdmin.TrySendNativeRestartCommands(command =>
        {
            commands.Add(command);
            return false;
        });

        Assert.False(accepted);
        Assert.Equal(new[] { "rnr" }, commands);
    }

    [Fact]
    public void Reports_Failure_When_Restart_Cannot_Be_Sent()
    {
        var commands = new List<string>();

        var accepted = LocalAdmin.V2.Core.LocalAdmin.TrySendNativeRestartCommands(command =>
        {
            commands.Add(command);
            return command == "rnr";
        });

        Assert.False(accepted);
        Assert.Equal(new[] { "rnr", "restart" }, commands);
    }
}
