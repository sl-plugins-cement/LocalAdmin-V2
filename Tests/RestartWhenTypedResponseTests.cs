using System.Linq;
using System.Text.Json;
using LocalAdmin.V2.Commands;
using LocalAdmin.V2.Core.Policy;
using LocalAdmin.V2.JSON;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class RestartWhenTypedResponseTests
{
    [Fact]
    public void Intent_snapshot_tracks_the_exact_armed_preset_and_cancel()
    {
        var manager = new RestartIntentManager();
        var host = new FakeHost();
        var tracker = new PlayerCountTracker();

        Assert.False(manager.GetSnapshot().Exists);
        Assert.True(manager.TryArm(
            host,
            tracker,
            "next-round-lowpop",
            new RestartCondition { Edge = RestartEdge.RoundEnd, MaxPlayers = 4 },
            out _));

        var armed = manager.GetSnapshot();
        Assert.True(armed.Exists);
        Assert.Equal("next-round-lowpop", armed.Preset);
        Assert.False(armed.Firing);

        Assert.True(manager.TryCancel(host, out _));
        Assert.False(manager.GetSnapshot().Exists);
    }

    [Fact]
    public void Armed_receipt_serializes_the_exact_typed_contract()
    {
        Assert.Equal("@@SCPSL_OPS_TYPED_V1@@", RestartWhenCommand.TypedResponsePrefix);
        var response = RestartWhenCommand.CreateTypedResponse(
            "restartwhen.arm",
            "oa/test",
            ok: true,
            new RestartIntentSnapshot("empty", Firing: false),
            error: null);

        var json = JsonSerializer.Serialize(response, OpsJsonContext.Default.RestartWhenTypedResponse);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal("restartwhen.arm", root.GetProperty("operation").GetString());
        Assert.Equal("oa/test", root.GetProperty("subject").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("exists").GetBoolean());
        Assert.Equal("empty", root.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Cancel_status_receipt_reports_that_the_intent_does_not_exist()
    {
        var response = RestartWhenCommand.CreateTypedResponse(
            "restartwhen.cancel",
            "oa/test",
            ok: true,
            new RestartIntentSnapshot(Preset: null, Firing: false),
            error: null);

        var json = JsonSerializer.Serialize(response, OpsJsonContext.Default.RestartWhenTypedResponse);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Failed_mutation_receipt_is_explicit_and_keeps_the_observed_intent()
    {
        var response = RestartWhenCommand.CreateTypedResponse(
            "restartwhen.arm",
            "oa/test",
            ok: false,
            new RestartIntentSnapshot("quiet-hour", Firing: false),
            "A restart intent is already armed.");

        var json = JsonSerializer.Serialize(response, OpsJsonContext.Default.RestartWhenTypedResponse);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("exists").GetBoolean());
        Assert.Equal("quiet-hour", root.GetProperty("value").GetString());
        Assert.Equal("A restart intent is already armed.", root.GetProperty("error").GetString());
    }

    private sealed class FakeHost : IRestartHost
    {
        public bool TryScheduleGracefulRestart(string reason) => true;

        public void RequestPlayerCountRefresh()
        {
        }

        public bool IsShuttingDown => false;

        public bool RestartInProgress => false;

        public bool ConfirmNotShuttingDown() => true;
    }
}
