// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LocalAdmin.V2.Core.CommandChannel;

/// <summary>
/// Parses and runs the explicit "ctl" process mode. Keeping this behind an unambiguous first
/// argument preserves every existing server-start command line (whose first argument is a port).
/// </summary>
internal static class CommandChannelClientCli
{
    internal const int UsageExitCode = 64;

    internal static bool IsInvocation(IReadOnlyList<string> args)
    {
        return args.Count > 0 && string.Equals(args[0], "ctl", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParse(args.Skip(1).ToArray(), out var options, out var parseError, out var showHelp))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
                error.WriteLine(parseError);
            WriteUsage(error);
            return UsageExitCode;
        }

        if (showHelp)
        {
            WriteUsage(output);
            return 0;
        }

        return await CommandChannelClient.SendAsync(options!, output, error).ConfigureAwait(false);
    }

    internal static bool TryParse(
        IReadOnlyList<string> args,
        out CommandChannelClientOptions? options,
        out string? error,
        out bool showHelp)
    {
        options = null;
        error = null;
        showHelp = false;

        ushort? port = null;
        var baseName = "LocalAdmin-Console";
        string? pipeNameOverride = null;
        var connectTimeoutMs = 5000;
        var quietMs = 1500;
        var timeoutMs = 12000;
        var useRa = false;
        var commandStart = -1;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h")
            {
                showHelp = true;
                return true;
            }

            if (TryReadValue(args, ref i, "--port", "-p", out var value))
            {
                if (!ushort.TryParse(value, out var parsedPort) || parsedPort == 0)
                    return Fail($"Invalid port: \"{value}\".", out options, out error);
                port = parsedPort;
                continue;
            }

            if (TryReadValue(args, ref i, "--base-name", null, out value))
            {
                if (string.IsNullOrWhiteSpace(value))
                    return Fail("--base-name requires a non-empty value.", out options, out error);
                baseName = value;
                continue;
            }

            if (TryReadValue(args, ref i, "--pipe-name", null, out value))
            {
                pipeNameOverride = value;
                continue;
            }

            if (TryReadPositiveInt(args, ref i, "--connect-timeout-ms", out var parsedInt, out error))
            {
                if (error != null)
                    return false;
                connectTimeoutMs = parsedInt;
                continue;
            }

            if (TryReadPositiveInt(args, ref i, "--quiet-ms", out parsedInt, out error))
            {
                if (error != null)
                    return false;
                quietMs = parsedInt;
                continue;
            }

            if (TryReadPositiveInt(args, ref i, "--timeout-ms", out parsedInt, out error))
            {
                if (error != null)
                    return false;
                timeoutMs = parsedInt;
                continue;
            }

            if (port == null && ushort.TryParse(arg, out var positionalPort) && positionalPort != 0)
            {
                port = positionalPort;
                continue;
            }

            if (IsRawMode(arg))
            {
                commandStart = i + 1;
                break;
            }

            if (string.Equals(arg, "ra", StringComparison.OrdinalIgnoreCase))
            {
                useRa = true;
                commandStart = i + 1;
                break;
            }

            if (arg.Length > 0 && arg[0] == '-')
                return Fail($"Unknown client option: \"{arg}\".", out options, out error);

            // With a port already known, omitting the mode means raw mode. This keeps the
            // shortest useful invocation simple: LocalAdmin ctl 7777 players.
            if (port != null)
            {
                commandStart = i;
                break;
            }

            return Fail($"Unexpected argument before the port: \"{arg}\".", out options, out error);
        }

        if (port == null)
            return Fail("A game port is required.", out options, out error);

        if (commandStart < 0 || commandStart >= args.Count)
            return Fail("A command is required.", out options, out error);

        var command = string.Join(' ', args.Skip(commandStart)).Trim();
        if (command.Length == 0)
            return Fail("A command is required.", out options, out error);

        if (useRa && command[0] != '/')
            command = "/" + command;

        options = new CommandChannelClientOptions(
            port.Value,
            command,
            baseName,
            pipeNameOverride,
            connectTimeoutMs,
            quietMs,
            timeoutMs);
        return true;
    }

    private static bool IsRawMode(string value)
    {
        return value.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("console", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("local", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("localadmin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string longName,
        string? shortName,
        out string value)
    {
        var arg = args[index];
        if (!string.Equals(arg, longName, StringComparison.OrdinalIgnoreCase) &&
            (shortName == null || !string.Equals(arg, shortName, StringComparison.OrdinalIgnoreCase)))
        {
            value = string.Empty;
            return false;
        }

        if (++index >= args.Count)
        {
            value = string.Empty;
            return true;
        }

        value = args[index];
        return true;
    }

    private static bool TryReadPositiveInt(
        IReadOnlyList<string> args,
        ref int index,
        string name,
        out int value,
        out string? error)
    {
        value = 0;
        error = null;
        var originalIndex = index;
        if (!TryReadValue(args, ref index, name, null, out var raw))
            return false;

        if (index == originalIndex || !int.TryParse(raw, out value) || value <= 0)
            error = $"{name} requires a positive integer value.";
        return true;
    }

    private static bool Fail(
        string message,
        out CommandChannelClientOptions? options,
        out string? error)
    {
        options = null;
        error = message;
        return false;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  LocalAdmin ctl <port> [raw|console|local|localadmin] <command...>");
        writer.WriteLine("  LocalAdmin ctl <port> ra <RA command...>");
        writer.WriteLine("  LocalAdmin ctl --port <port> [options] [raw|ra] <command...>");
        writer.WriteLine();
        writer.WriteLine("Raw/console/local modes send text exactly as if it were typed in LocalAdmin.");
        writer.WriteLine("RA mode prepends '/' and uses the game's native Remote Admin command path.");
        writer.WriteLine();
        writer.WriteLine("Options: --base-name <name>, --pipe-name <name>, --connect-timeout-ms <ms>,");
        writer.WriteLine("         --quiet-ms <ms>, --timeout-ms <ms>");
    }
}
