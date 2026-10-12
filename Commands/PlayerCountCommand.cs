// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.Core.Policy;
using LocalAdmin.V2.IO;
using LocalAdmin.V2.JSON;

namespace LocalAdmin.V2.Commands;

internal sealed record PlayerCountTypedResponse
{
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("value")]
    public required int? Value { get; init; }

    [JsonPropertyName("error")]
    public required string? Error { get; init; }
}

/// <summary>
/// <c>playercount</c>: a quiet player count for a remote status poller. It asks the game for the
/// player list with a quiet probe and answers with one typed <c>players.count</c> line that goes
/// to command-channel clients only. Neither the answer, the game's list reply nor the command's
/// own input echo reaches the console window or the LocalAdmin log.
/// </summary>
internal sealed class PlayerCountCommand : CommandBase
{
    internal const string OperationName = "players.count";

    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public PlayerCountCommand()
        : base("PlayerCount", "Replies with the player count to command-channel clients only (ctl <port> localadmin playercount).") { }

    internal override bool EchoToChannelOnly => true;

    internal override void Execute(string[] arguments)
    {
        if (!ConsoleUtil.HasChannelSink)
        {
            ConsoleUtil.WriteLine(
                "playercount answers command-channel clients only. Enable command_channel_enabled and run: LocalAdmin ctl <port> localadmin playercount",
                ConsoleColor.Yellow);
            return;
        }

        Task<PlayerProbeResult> probe;
        try
        {
            probe = Core.LocalAdmin.Singleton!.ProbePlayersQuietAsync(ProbeTimeout);
        }
        catch (Exception ex)
        {
            probe = Task.FromException<PlayerProbeResult>(ex);
        }

        _ = ReplyWhenDoneAsync(probe);
    }

    internal static PlayerCountTypedResponse CreateTypedResponse(string subject, int? players, string? error) => new()
    {
        Operation = OperationName,
        Subject = subject,
        Ok = error == null && players.HasValue,
        Value = error == null ? players : null,
        Error = error
    };

    internal static string Serialize(PlayerCountTypedResponse response) =>
        RestartWhenCommand.TypedResponsePrefix +
        JsonSerializer.Serialize(response, OpsJsonContext.Default.PlayerCountTypedResponse);

    private static async Task ReplyWhenDoneAsync(Task<PlayerProbeResult> probe)
    {
        PlayerCountTypedResponse response;
        var subject = Core.LocalAdmin.Configuration?.OpsLane ?? string.Empty;
        try
        {
            var result = await probe.ConfigureAwait(false);
            response = CreateTypedResponse(subject, result.Players, error: null);
        }
        catch (TimeoutException)
        {
            response = CreateTypedResponse(subject, players: null, "Timed out waiting for the players reply.");
        }
        catch (Exception ex)
        {
            response = CreateTypedResponse(subject, players: null, ex.Message);
        }

        ConsoleUtil.WriteChannelOnly(Serialize(response));
    }
}
