// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.Core.Activation;
using LocalAdmin.V2.IO;
using LocalAdmin.V2.JSON;

namespace LocalAdmin.V2.Commands;

/// <summary>Machine-facing managed-activation control over the existing same-user command pipe.</summary>
internal sealed partial class OpsCommand : CommandBase
{
    internal const string ResponsePrefix = "@@SCPSL_OPS_V1@@";
    private readonly ActivationCoordinator _coordinator;

    internal OpsCommand(ActivationCoordinator coordinator)
        : base("Ops", "Managed activation protocol. Use scpsl-ops instead of calling this directly.")
    {
        _coordinator = coordinator;
    }

    internal override void Execute(string[] arguments)
    {
        var requestId = arguments.Length > 0 ? arguments[0] : string.Empty;
        try
        {
            if (!CanonicalUuid(requestId))
                throw new ActivationProtocolException("The first argument must be a canonical request UUID.");
            if (arguments.Length < 2)
                throw new ActivationProtocolException("Missing ops verb.");

            var response = ExecuteCore(requestId, arguments[1..]);
            Write(response);
        }
        catch (Exception ex)
        {
            Write(new OpsCommandResponse
            {
                ProtocolVersion = 1,
                RequestId = requestId,
                Operation = arguments.Length > 1 ? arguments[1].ToLowerInvariant() : "unknown",
                Ok = false,
                Message = ex.Message,
                Record = TryStatus(),
                Probe = null
            });
        }
    }

    private OpsCommandResponse ExecuteCore(string requestId, string[] args)
    {
        ActivationRecord? record;
        string message;
        OpsPlayerProbe? playerProbe = null;
        var operation = args[0].ToLowerInvariant();

        switch (operation)
        {
            case "status" when args.Length == 1:
                record = _coordinator.GetStatusAsync().GetAwaiter().GetResult();
                message = record == null ? "activation.json does not exist" : $"activation state is {record.State}";
                break;

            case "bootstrap" when args.Length == 7:
                record = _coordinator.BootstrapAsync(
                    args[1],
                    args[2],
                    Readiness(args[3], args[4], args[5], args[6])).GetAwaiter().GetResult();
                message = "verified activation record created";
                Core.LocalAdmin.Singleton!.ForceScheduledRestart("managed activation bootstrap");
                break;

            case "prepare" when args.Length == 13:
                record = _coordinator.PrepareAsync(
                    args[1],
                    RequiredLong(args[2], "expected revision"),
                    args[3],
                    HolderToken(args[4]),
                    new ActivationRestart
                    {
                        Preset = args[5],
                        Edge = args[6],
                        MaxPlayers = NullableInt(args[7]),
                        Rounds = NullableInt(args[8])
                    },
                    Readiness(args[9], args[10], args[11], args[12])).GetAwaiter().GetResult();
                message = "activation prepared";
                break;

            case "prepare-legacy" when args.Length == 13:
                // Reserved so an older client fails with a typed response instead of shifting
                // positional arguments into a different meaning.
                throw new ActivationProtocolException("prepare-legacy is not supported; update scpsl-ops.");

            case "arm" when args.Length == 2:
                if (Core.LocalAdmin.RestartIntents.HasPending)
                    throw new ActivationProtocolException("Cancel the existing restartwhen intent before arming activation.");
                record = _coordinator.ArmAsync(args[1]).GetAwaiter().GetResult();
                _coordinator.OnChildStartedAsync().GetAwaiter().GetResult();
                message = "activation pending";
                break;

            case "cancel" when args.Length == 2:
                record = _coordinator.CancelAsync(args[1]).GetAwaiter().GetResult();
                message = record.State == ActivationStates.Verified
                    ? "pending activation cancelled and staging cleaned"
                    : "activation cancelled; staging cleanup must be retried";
                break;

            case "recovery-adopt" or "adopt" when args.Length == 4:
                record = _coordinator.AdoptRecoveryAsync(
                    args[1],
                    args[2],
                    RequiredLong(args[3], "expected revision")).GetAwaiter().GetResult();
                message = "repaired checkpoint adopted; starting a fresh game session";
                Core.LocalAdmin.Singleton!.ForceScheduledRestart("managed activation recovery adoption");
                break;

            case "probe" when args.Length == 1:
                var probe = Core.LocalAdmin.Singleton!.ProbePlayersAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                record = _coordinator.GetStatusAsync().GetAwaiter().GetResult();
                message = $"correlated players response: {probe.Players}";
                playerProbe = new OpsPlayerProbe
                {
                    ProbeId = probe.ProbeId.ToString("D"),
                    Players = probe.Players,
                    IssuedAt = ActivationRecordStore.FormatTimestamp(probe.IssuedAtUtc),
                    RespondedAt = ActivationRecordStore.FormatTimestamp(probe.RespondedAtUtc)
                };
                break;

            default:
                throw new ActivationProtocolException(
                    "Usage: ops <request-id> status|bootstrap|prepare|arm|cancel|recovery-adopt|probe");
        }

        return new OpsCommandResponse
        {
            ProtocolVersion = 1,
            RequestId = requestId,
            Operation = operation,
            Ok = true,
            Message = message,
            Record = record,
            Probe = playerProbe
        };
    }

    private ActivationRecord? TryStatus()
    {
        try { return _coordinator.GetStatusAsync().GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private static ActivationReadiness Readiness(string timeout, string heartbeat, string udp, string verification)
    {
        if (!int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            throw new ActivationProtocolException("Readiness timeout must be a positive integer.");

        return new ActivationReadiness
        {
            TimeoutSeconds = seconds,
            RequireHeartbeat = Flag(heartbeat),
            RequireUdp = Flag(udp),
            RequireVerification = Flag(verification)
        };
    }

    private static bool Flag(string value) => value switch
    {
        "0" => false,
        "1" => true,
        _ => throw new ActivationProtocolException("Boolean flags must be 0 or 1.")
    };

    private static int? NullableInt(string value)
    {
        if (value == "-")
            return null;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new ActivationProtocolException("Optional integers must be decimal integers or '-'.");
    }

    private static long RequiredLong(string value, string name)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            return parsed;
        throw new ActivationProtocolException($"{name} must be a nonnegative integer.");
    }

    [GeneratedRegex("^[A-Za-z0-9_.:@-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex HolderRegex();

    private static string HolderToken(string value)
    {
        if (HolderRegex().IsMatch(value))
            return value;
        throw new ActivationProtocolException("Holder token must match [A-Za-z0-9_.:@-]{1,128}.");
    }

    private static bool CanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;

    private static void Write(OpsCommandResponse response)
    {
        var json = JsonSerializer.Serialize(response, OpsJsonContext.Default.OpsCommandResponse);
        ConsoleUtil.WriteLine(ResponsePrefix + json, response.Ok ? ConsoleColor.DarkGreen : ConsoleColor.Yellow);
    }
}
