// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LocalAdmin.V2.Core.Activation;

internal static class ActivationStates
{
    internal const string Verified = "verified";
    internal const string Prepared = "prepared";
    internal const string Pending = "pending";
    internal const string Activating = "activating";
    internal const string Verifying = "verifying";
    internal const string RollingBack = "rolling-back";
    internal const string RolledBack = "rolled-back";
    internal const string Cancelled = "cancelled";
    internal const string RecoveryRequired = "recovery-required";

    internal static bool IsKnown(string value) => value is
        Verified or Prepared or Pending or Activating or Verifying or RollingBack or RolledBack or
        Cancelled or RecoveryRequired;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivationRecord
{
    [JsonRequired, JsonPropertyName("protocol_version")]
    public required int ProtocolVersion { get; init; }

    [JsonRequired, JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonRequired, JsonPropertyName("lane")]
    public required string Lane { get; init; }

    [JsonRequired, JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonRequired, JsonPropertyName("activation_id")]
    public required string ActivationId { get; init; }

    [JsonRequired, JsonPropertyName("current_checkpoint")]
    public required string? CurrentCheckpoint { get; init; }

    [JsonRequired, JsonPropertyName("pending_checkpoint")]
    public required string? PendingCheckpoint { get; init; }

    [JsonRequired, JsonPropertyName("previous_checkpoint")]
    public required string? PreviousCheckpoint { get; init; }

    [JsonRequired, JsonPropertyName("verification_target")]
    public required string? VerificationTarget { get; init; }

    [JsonRequired, JsonPropertyName("rollback_attempted")]
    public required bool RollbackAttempted { get; init; }

    [JsonRequired, JsonPropertyName("lock")]
    public required ActivationLock? Lock { get; init; }

    [JsonRequired, JsonPropertyName("restart")]
    public required ActivationRestart? Restart { get; init; }

    [JsonRequired, JsonPropertyName("readiness")]
    public required ActivationReadiness Readiness { get; init; }

    [JsonRequired, JsonPropertyName("checks")]
    public required List<ActivationCheck> Checks { get; init; }

    [JsonRequired, JsonPropertyName("prepared_at")]
    public required string PreparedAt { get; init; }

    [JsonRequired, JsonPropertyName("updated_at")]
    public required string UpdatedAt { get; init; }

    [JsonRequired, JsonPropertyName("last_error")]
    public required string? LastError { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivationLock
{
    [JsonRequired, JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonRequired, JsonPropertyName("holder")]
    public required string Holder { get; init; }

    [JsonRequired, JsonPropertyName("acquired_at")]
    public required string AcquiredAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivationRestart
{
    [JsonRequired, JsonPropertyName("preset")]
    public required string Preset { get; init; }

    [JsonRequired, JsonPropertyName("edge")]
    public required string Edge { get; init; }

    [JsonRequired, JsonPropertyName("max_players")]
    public required int? MaxPlayers { get; init; }

    [JsonRequired, JsonPropertyName("rounds")]
    public required int? Rounds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivationReadiness
{
    [JsonRequired, JsonPropertyName("timeout_seconds")]
    public required int TimeoutSeconds { get; init; }

    [JsonRequired, JsonPropertyName("require_heartbeat")]
    public required bool RequireHeartbeat { get; init; }

    [JsonRequired, JsonPropertyName("require_udp")]
    public required bool RequireUdp { get; init; }

    [JsonRequired, JsonPropertyName("require_verification")]
    public required bool RequireVerification { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivationCheck
{
    [JsonRequired, JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonRequired, JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonRequired, JsonPropertyName("observed_at")]
    public required string ObservedAt { get; init; }

    [JsonRequired, JsonPropertyName("detail")]
    public required string Detail { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivationHookResult
{
    [JsonRequired, JsonPropertyName("protocol_version")]
    public required int ProtocolVersion { get; init; }

    [JsonRequired, JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonRequired, JsonPropertyName("activation_id")]
    public required string ActivationId { get; init; }

    [JsonRequired, JsonPropertyName("phase")]
    public required string Phase { get; init; }

    [JsonRequired, JsonPropertyName("checkpoint")]
    public required string? Checkpoint { get; init; }

    [JsonRequired, JsonPropertyName("checks")]
    public required List<ActivationCheck> Checks { get; init; }

    [JsonRequired, JsonPropertyName("error")]
    public required string? Error { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OpsCommandResponse
{
    [JsonRequired, JsonPropertyName("protocol_version")]
    public required int ProtocolVersion { get; init; }

    [JsonRequired, JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonRequired, JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonRequired, JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonRequired, JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonRequired, JsonPropertyName("record")]
    public required ActivationRecord? Record { get; init; }

    [JsonRequired, JsonPropertyName("probe")]
    public required OpsPlayerProbe? Probe { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OpsPlayerProbe
{
    [JsonRequired, JsonPropertyName("probe_id")]
    public required string ProbeId { get; init; }

    [JsonRequired, JsonPropertyName("players")]
    public required int Players { get; init; }

    [JsonRequired, JsonPropertyName("issued_at")]
    public required string IssuedAt { get; init; }

    [JsonRequired, JsonPropertyName("responded_at")]
    public required string RespondedAt { get; init; }
}
