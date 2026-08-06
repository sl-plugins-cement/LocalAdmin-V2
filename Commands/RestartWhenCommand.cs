using System;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.Core.Policy;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Commands;

/// <summary>
/// Preset-driven one-shot restart intents: <c>restartwhen &lt;preset&gt;</c> arms a named
/// condition - a built-in from <see cref="RestartPresets"/>, or a custom JSON preset authored on
/// demand in the <c>restart_presets/</c> config directory (see
/// <see cref="CustomRestartPresets"/>). One intent can be armed at a time.
/// </summary>
internal sealed class RestartWhenCommand : CommandBase
{
    public RestartWhenCommand()
        : base("RestartWhen", "Arms a predefined one-shot restart condition (restartwhen list).") { }

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
                break;

            case "cancel":
                Report(Core.LocalAdmin.RestartIntents.TryCancel(localAdmin, out var cancelMessage), cancelMessage);
                break;

            default:
                Arm(localAdmin, arguments[0]);
                break;
        }
    }

    private static void Arm(Core.LocalAdmin localAdmin, string presetName)
    {
        if (!RestartPresets.TryResolve(presetName, CustomPresetDirectory, out var preset, out var resolveError))
        {
            ConsoleUtil.WriteLine($"Cannot arm \"{presetName}\": {resolveError}", ConsoleColor.Yellow);
            ConsoleUtil.WriteLine("Run \"restartwhen list\" to see the available presets.", ConsoleColor.Yellow);
            return;
        }

        Report(
            Core.LocalAdmin.RestartIntents.TryArm(localAdmin, localAdmin.PlayerTracker, preset.Name, preset.Condition, out var message),
            message);
    }

    private static void Report(bool ok, string message) =>
        ConsoleUtil.WriteLine(message, ok ? ConsoleColor.DarkGreen : ConsoleColor.Yellow);

    private static void PrintUsage()
    {
        ConsoleUtil.WriteLine("Usage: restartwhen <preset> | list | status | cancel", ConsoleColor.Yellow);
        ConsoleUtil.WriteLine("Run \"restartwhen list\" to see built-in and custom presets.", ConsoleColor.Yellow);
    }
}
