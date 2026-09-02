using System.Text.Json;
using System.Linq;
using LocalAdmin.V2.Core.Activation;
using LocalAdmin.V2.JSON;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class OpsCommandWireTests
{
    [Fact]
    public void Response_serializes_the_exact_version_one_envelope()
    {
        var response = new OpsCommandResponse
        {
            ProtocolVersion = 1,
            RequestId = "11111111-2222-3333-4444-555555555555",
            Operation = "probe",
            Ok = true,
            Message = "correlated players response: 0",
            Record = null,
            Probe = new OpsPlayerProbe
            {
                ProbeId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                Players = 0,
                IssuedAt = "2026-08-26T01:02:03.0000000Z",
                RespondedAt = "2026-08-26T01:02:04.0000000Z"
            }
        };

        var json = JsonSerializer.Serialize(response, OpsJsonContext.Default.OpsCommandResponse);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("probe", root.GetProperty("operation").GetString());
        Assert.Equal(0, root.GetProperty("probe").GetProperty("players").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("record").ValueKind);
    }
}
