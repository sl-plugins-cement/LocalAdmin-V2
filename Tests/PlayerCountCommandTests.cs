using System.Linq;
using System.Text.Json;
using LocalAdmin.V2.Commands;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class PlayerCountCommandTests
{
    private static JsonElement Parse(string line)
    {
        Assert.StartsWith(RestartWhenCommand.TypedResponsePrefix, line);
        return JsonDocument.Parse(line[RestartWhenCommand.TypedResponsePrefix.Length..]).RootElement;
    }

    [Fact]
    public void Success_reply_carries_the_integer_count_for_the_lane()
    {
        var line = PlayerCountCommand.Serialize(PlayerCountCommand.CreateTypedResponse("sr3/carl", 7, error: null));
        var root = Parse(line);

        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal("players.count", root.GetProperty("operation").GetString());
        Assert.Equal("sr3/carl", root.GetProperty("subject").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("value").ValueKind);
        Assert.Equal(7, root.GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Failure_reply_has_a_null_value_and_a_reason()
    {
        var line = PlayerCountCommand.Serialize(
            PlayerCountCommand.CreateTypedResponse(string.Empty, players: null, "The game console is not connected."));
        var root = Parse(line);

        Assert.Equal(string.Empty, root.GetProperty("subject").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("value").ValueKind);
        Assert.Equal("The game console is not connected.", root.GetProperty("error").GetString());
    }

    [Fact]
    public void Reply_is_a_single_line()
    {
        var line = PlayerCountCommand.Serialize(PlayerCountCommand.CreateTypedResponse("oa/test", 0, error: null));

        Assert.DoesNotContain('\n', line);
        Assert.Equal(0, Parse(line).GetProperty("value").GetInt32());
    }
}
