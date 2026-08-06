// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAdmin.V2.Core.CommandChannel;

internal sealed record CommandChannelClientOptions(
    ushort Port,
    string Command,
    string BaseName,
    string? PipeNameOverride,
    int ConnectTimeoutMs,
    int QuietMs,
    int TimeoutMs);

/// <summary>
/// Native, cross-platform client for LocalAdmin's same-user command channel.
/// </summary>
internal static class CommandChannelClient
{
    internal const int SuccessExitCode = 0;
    internal const int ConnectionFailureExitCode = 2;
    internal const int UnconfirmedDeliveryExitCode = 3;

    internal static async Task<int> SendAsync(
        CommandChannelClientOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var pipeName = string.IsNullOrWhiteSpace(options.PipeNameOverride)
            ? CommandChannelServer.BuildPipeName(options.BaseName, options.Port)
            : options.PipeNameOverride;

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(options.ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            error.WriteLine(
                $"Could not connect to LocalAdmin pipe \"{pipeName}\" within {options.ConnectTimeoutMs} ms. " +
                $"Is LocalAdmin running on port {options.Port} as this user with command_channel_enabled: true? ({ex.Message})");
            return ConnectionFailureExitCode;
        }

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync(options.Command).ConfigureAwait(false);

        var echoMarker = ">>> " + options.Command.Trim();
        var overall = Stopwatch.StartNew();
        var lastActivity = Stopwatch.StartNew();
        var anchored = false;
        var gotReply = false;
        var preAnchor = new List<string>();

        while (overall.ElapsedMilliseconds < options.TimeoutMs)
        {
            var remainingOverall = options.TimeoutMs - (int)overall.ElapsedMilliseconds;
            var readBudget = anchored
                ? Math.Min(remainingOverall, Math.Max(1, options.QuietMs - (int)lastActivity.ElapsedMilliseconds))
                : remainingOverall;

            if (readBudget <= 0)
                break;

            string? line;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readCts.CancelAfter(readBudget);
                try
                {
                    line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (line == null)
                break;

            lastActivity.Restart();
            if (!anchored)
            {
                if (string.Equals(line.Trim(), echoMarker, StringComparison.Ordinal))
                {
                    anchored = true;
                }
                else
                {
                    preAnchor.Add(line);
                }

                continue;
            }

            output.WriteLine(line);
            gotReply = true;
        }

        if (!anchored)
        {
            foreach (var line in preAnchor)
                output.WriteLine(line);

            error.WriteLine(
                $"Connected to \"{pipeName}\", but LocalAdmin did not echo this command within {options.TimeoutMs} ms; delivery is unconfirmed.");
            return UnconfirmedDeliveryExitCode;
        }

        if (!gotReply)
        {
            error.WriteLine(
                $"Command delivery was confirmed, but it produced no output within the {options.QuietMs} ms quiet window.");
        }

        return SuccessExitCode;
    }
}
