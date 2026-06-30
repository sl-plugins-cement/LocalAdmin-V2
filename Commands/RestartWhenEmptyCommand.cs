using System;
using System.Globalization;
using System.Linq;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Commands;

internal sealed class RestartWhenEmptyCommand : CommandBase
{
    public RestartWhenEmptyCommand()
        : base("RestartWhenEmpty", "Arms a one-shot restart when player count is low.") { }

    internal override void Execute(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            PrintUsage();
            return;
        }

        switch (arguments[0].ToLowerInvariant())
        {
            case "once":
            case "start":
            case "arm":
                Start(arguments.Skip(1).ToArray());
                break;

            case "cancel":
            case "stop":
            case "clear":
                Cancel();
                break;

            case "status":
            case "st":
            case "s":
                ConsoleUtil.WriteLine(Core.LocalAdmin.Singleton!.GetOneShotRestartWhenEmptyStatus(), ConsoleColor.DarkGreen);
                break;

            default:
                ConsoleUtil.WriteLine("Unknown restartwhenempty subcommand.", ConsoleColor.Red);
                PrintUsage();
                break;
        }
    }

    private static void Start(string[] arguments)
    {
        var config = Core.LocalAdmin.Configuration!;
        var settings = new Settings(
            config.RestartAtOrBelowPlayers,
            Math.Max(1, config.EmptyConfirmations),
            Math.Max(1, config.PolicyTickSeconds),
            config.RecentActivityOverPlayers,
            Math.Max(0, config.RecentActivityWindowMinutes));

        if (!TryApplyArguments(arguments, ref settings, out var error))
        {
            ConsoleUtil.WriteLine(error, ConsoleColor.Yellow);
            PrintUsage();
            return;
        }

        if (Core.LocalAdmin.Singleton!.TryStartOneShotRestartWhenEmpty(
                settings.RestartAtOrBelowPlayers,
                settings.EmptyConfirmations,
                settings.TickSeconds,
                settings.RecentActivityOverPlayers,
                settings.RecentActivityWindowMinutes,
                out var message))
            ConsoleUtil.WriteLine(message, ConsoleColor.DarkGreen);
        else
            ConsoleUtil.WriteLine(message, ConsoleColor.Yellow);
    }

    private static void Cancel()
    {
        if (Core.LocalAdmin.Singleton!.TryCancelOneShotRestartWhenEmpty(out var message))
            ConsoleUtil.WriteLine(message, ConsoleColor.DarkGreen);
        else
            ConsoleUtil.WriteLine(message, ConsoleColor.Yellow);
    }

    private static bool TryApplyArguments(string[] arguments, ref Settings settings, out string error)
    {
        var positional = 0;
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
                continue;

            var separatorIndex = argument.IndexOf('=');
            if (separatorIndex > 0)
            {
                var key = NormalizeKey(argument[..separatorIndex]);
                var value = argument[(separatorIndex + 1)..];

                if (!TryParseInt(value, out var parsed, out error))
                    return false;

                if (!ApplyNamedArgument(key, parsed, ref settings, out error))
                    return false;

                continue;
            }

            if (!TryParseInt(argument, out var positionalValue, out error))
                return false;

            if (!ApplyPositionalArgument(positional++, positionalValue, ref settings, out error))
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ApplyNamedArgument(string key, int value, ref Settings settings, out string error)
    {
        switch (key)
        {
            case "threshold":
            case "at":
            case "players":
            case "restartat":
            case "restartatorbelow":
                settings = settings with { RestartAtOrBelowPlayers = value };
                break;

            case "confirmations":
            case "confirms":
            case "confirm":
                settings = settings with { EmptyConfirmations = value };
                break;

            case "tick":
            case "tickseconds":
            case "seconds":
            case "poll":
            case "pollseconds":
                settings = settings with { TickSeconds = value };
                break;

            case "recentover":
            case "recentactivityover":
            case "over":
                settings = settings with { RecentActivityOverPlayers = value };
                break;

            case "window":
            case "recentwindow":
            case "recentactivitywindow":
            case "minutes":
                settings = settings with { RecentActivityWindowMinutes = value };
                break;

            default:
                error = $"Unknown option \"{key}\".";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ApplyPositionalArgument(int position, int value, ref Settings settings, out string error)
    {
        switch (position)
        {
            case 0:
                settings = settings with { RestartAtOrBelowPlayers = value };
                break;

            case 1:
                settings = settings with { EmptyConfirmations = value };
                break;

            case 2:
                settings = settings with { TickSeconds = value };
                break;

            case 3:
                settings = settings with { RecentActivityOverPlayers = value };
                break;

            case 4:
                settings = settings with { RecentActivityWindowMinutes = value };
                break;

            default:
                error = "Too many positional arguments.";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseInt(string value, out int parsed, out string error)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            error = string.Empty;
            return true;
        }

        error = $"Expected an integer value, got \"{value}\".";
        return false;
    }

    private static string NormalizeKey(string key) =>
        key.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static void PrintUsage()
    {
        ConsoleUtil.WriteLine(
            "Usage: restartwhenempty <once|status|cancel> [threshold] [confirmations] [tickSeconds] [recentOverPlayers] [windowMinutes]",
            ConsoleColor.Yellow);
        ConsoleUtil.WriteLine(
            "Named options: threshold=<players> confirmations=<count> tick=<seconds> recentOver=<players> window=<minutes>",
            ConsoleColor.Yellow);
    }

    private readonly record struct Settings(
        int RestartAtOrBelowPlayers,
        int EmptyConfirmations,
        int TickSeconds,
        int RecentActivityOverPlayers,
        int RecentActivityWindowMinutes);
}
