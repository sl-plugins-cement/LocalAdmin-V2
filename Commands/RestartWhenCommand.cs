using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.Core.Policy;
using LocalAdmin.V2.IO;
using LocalAdmin.V2.JSON;

namespace LocalAdmin.V2.Commands;

internal sealed record RestartWhenTypedResponse
{
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("exists")]
    public required bool Exists { get; init; }

    [JsonPropertyName("value")]
    public required string? Value { get; init; }

    [JsonPropertyName("error")]
    public required string? Error { get; init; }
}

/// <summary>
/// Preset-driven restart requests: <c>restartwhen &lt;preset&gt;</c> either forwards a native SCP:SL
/// command or arms a named LocalAdmin condition - a built-in from <see cref="RestartPresets"/>, or
/// a custom JSON preset authored on demand in the <c>restart_presets/</c> config directory (see
/// <see cref="CustomRestartPresets"/>). One LocalAdmin-managed intent can be armed at a time.
/// </summary>
internal sealed class RestartWhenCommand : CommandBase
{
    internal const string TypedResponsePrefix = "@@SCPSL_OPS_TYPED_V1@@";

    public RestartWhenCommand()
        : base("RestartWhen", "Requests a predefined restart action (restartwhen list).") { }

    private static string CustomPresetDirectory =>
        System.IO.Path.Combine(IO.PathManager.ConfigPath, CustomRestartPresets.DirectoryName);

    internal override void Execute(string[] arguments)
    {
        var localAdmin = Core.LocalAdmin.Singleton!;

        if (arguments.Length == 0)
        {
            PrintUsage();
            return;
        }

        switch (arguments[0].ToLowerInvariant())
        {
            case "list":
            case "presets":
                ConsoleUtil.WriteLine("Available restart presets:", ConsoleColor.DarkGreen);
                foreach (var line in RestartPresets.DescribeAll(CustomPresetDirectory))
                    ConsoleUtil.WriteLine("  " + line, ConsoleColor.Gray);
                ConsoleUtil.WriteLine(
                    $"Custom presets are JSON files in {CustomPresetDirectory} (name = filename).",
                    ConsoleColor.Gray);
                break;

            case "status":
                ConsoleUtil.WriteLine(Core.LocalAdmin.RestartIntents.GetStatus(localAdmin.PlayerTracker), ConsoleColor.DarkGreen);
                WriteTyped("restartwhen.status", ok: true, error: null);
                break;

            case "cancel":
                var cancelled = Core.LocalAdmin.RestartIntents.TryCancel(localAdmin, out var cancelMessage);
                Report(cancelled, cancelMessage);
                WriteTyped("restartwhen.cancel", cancelled, cancelled ? null : cancelMessage);
                break;

            default:
                Arm(localAdmin, arguments[0]);
                break;
        }
    }

    private static void Arm(Core.LocalAdmin localAdmin, string presetName)
    {
        if (localAdmin.ManagedActivationBlocksRestartIntent(out var activationState))
        {
            var message = $"Cannot arm a separate restart while managed activation is {activationState}. Use scpsl-ops status or cancel the pending activation.";
            ConsoleUtil.WriteLine(message, ConsoleColor.Yellow);
            WriteTyped("restartwhen.arm", ok: false, message);
            return;
        }

        if (!RestartPresets.TryResolve(presetName, CustomPresetDirectory, out var preset, out var resolveError))
        {
            var message = $"Cannot arm \"{presetName}\": {resolveError}";
            ConsoleUtil.WriteLine(message, ConsoleColor.Yellow);
            ConsoleUtil.WriteLine("Run \"restartwhen list\" to see the available presets.", ConsoleColor.Yellow);
            WriteTyped("restartwhen.arm", ok: false, message);
            return;
        }

        var armed = Core.LocalAdmin.RestartIntents.TryArm(
            localAdmin,
            localAdmin.PlayerTracker,
            preset.Name,
            preset.Condition,
            out var armMessage);
        Report(armed, armMessage);
        WriteTyped("restartwhen.arm", armed, armed ? null : armMessage);
    }

    private static void Report(bool ok, string message) =>
        ConsoleUtil.WriteLine(message, ok ? ConsoleColor.DarkGreen : ConsoleColor.Yellow);

    internal static RestartWhenTypedResponse CreateTypedResponse(
        string operation,
        string subject,
        bool ok,
        RestartIntentSnapshot snapshot,
        string? error) => new()
    {
        Operation = operation,
        Subject = subject,
        Ok = ok,
        Exists = snapshot.Exists,
        Value = snapshot.Preset,
        Error = error
    };

    private static void WriteTyped(string operation, bool ok, string? error)
    {
        var response = CreateTypedResponse(
            operation,
            Core.LocalAdmin.Configuration?.OpsLane ?? string.Empty,
            ok,
            Core.LocalAdmin.RestartIntents.GetSnapshot(),
            error);
        var json = JsonSerializer.Serialize(response, OpsJsonContext.Default.RestartWhenTypedResponse);
        ConsoleUtil.WriteLine(TypedResponsePrefix + json, ok ? ConsoleColor.DarkGreen : ConsoleColor.Yellow);
    }

    private static void PrintUsage()
    {
        ConsoleUtil.WriteLine("Usage: restartwhen <preset> | list | status | cancel", ConsoleColor.Yellow);
        ConsoleUtil.WriteLine("Run \"restartwhen list\" to see built-in and custom presets.", ConsoleColor.Yellow);
    }
}
