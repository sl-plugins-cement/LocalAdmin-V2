using System.Text.Json.Serialization;
using LocalAdmin.V2.Commands;
using LocalAdmin.V2.Core.Activation;

namespace LocalAdmin.V2.JSON;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ActivationRecord))]
[JsonSerializable(typeof(ActivationHookResult))]
[JsonSerializable(typeof(OpsCommandResponse))]
[JsonSerializable(typeof(RestartWhenTypedResponse))]
internal sealed partial class OpsJsonContext : JsonSerializerContext;
