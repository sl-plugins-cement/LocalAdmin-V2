using System;
using System.Globalization;
using System.Linq;
using LocalAdmin.V2.Commands.Meta;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Commands;

/// <summary>
/// Arms/reports/cancels a one-shot that gracefully restarts the server the next time it performs a
/// round restart (the game's initial round at boot is not counted). The intent persists across
/// unrelated restarts until it fires or is canceled.
/// </summary>
internal sealed class RestartNextRoundCommand : CommandBase
{
    public RestartNextRoundCommand()
        : base("RestartNextRound", "Arms a one-shot restart on the next round restart.") { }

    internal override void Execute(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Arm(Array.Empty<string>());
            return;
        }

        switch (arguments[0].ToLowerInvariant())
        {
            case "once":
            case "arm":
            case "start":
            case "on":
                Arm(arguments.Skip(1).ToArray());
                break;

            case "cancel":
            case "stop":
            case "clear":
            case "off":
                if (Core.LocalAdmin.Singleton!.TryCancelRestartOnNextRound(out var cancelMessage))
                    ConsoleUtil.WriteLine(cancelMessage, ConsoleColor.DarkGreen);
                else
                    ConsoleUtil.WriteLine(cancelMessage, ConsoleColor.Yellow);
                break;

            case "status":
            case "st":
            case "s":
                ConsoleUtil.WriteLine(Core.LocalAdmin.Singleton!.GetRestartOnNextRoundStatus(), ConsoleColor.DarkGreen);
                break;

            default:
                // Treat a bare number or after=<n> with no subcommand as an arm request.
                Arm(arguments);
                break;
        }
    }

    private static void Arm(string[] arguments)
    {
        if (!TryParseRounds(arguments, out var rounds, out var error))
        {
            ConsoleUtil.WriteLine(error, ConsoleColor.Yellow);
            PrintUsage();
            return;
        }

        if (Core.LocalAdmin.Singleton!.TryArmRestartOnNextRound(rounds, out var message))
            ConsoleUtil.WriteLine(message, ConsoleColor.DarkGreen);
        else
            ConsoleUtil.WriteLine(message, ConsoleColor.Yellow);
    }

    private static bool TryParseRounds(string[] arguments, out int rounds, out string error)
    {
        rounds = 1;

        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
                continue;

            var value = argument;
            var separatorIndex = argument.IndexOf('=');
            if (separatorIndex > 0)
            {
                var key = argument[..separatorIndex]
                    .Trim()
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace("_", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant();

                if (key is not ("after" or "rounds" or "count" or "n"))
                {
                    error = $"Unknown option \"{key}\".";
                    return false;
                }

                value = argument[(separatorIndex + 1)..];
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rounds))
            {
                error = $"Expected an integer value, got \"{value}\".";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static void PrintUsage()
    {
        ConsoleUtil.WriteLine(
            "Usage: restartnextround [once] [after=<rounds>] | status | cancel",
            ConsoleColor.Yellow);
        ConsoleUtil.WriteLine(
            "Restarts the server after the next round restart (or after <rounds> round restarts). Persists across unrelated restarts until it fires or is canceled.",
            ConsoleColor.Yellow);
    }
}
