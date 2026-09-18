// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.JSON;

namespace LocalAdmin.V2.Core.Activation;

internal interface IActivationHookRunner
{
    Task<ActivationHookResult> RunAsync(string phase, ActivationRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Executes the one canonical checkpoint/file implementation. Arguments never pass through a
/// shell, and the configured executable must be an absolute path.
/// </summary>
internal sealed class ActivationHookRunner : IActivationHookRunner
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private readonly string _cliPath;

    internal ActivationHookRunner(string cliPath)
    {
        if (!Path.IsPathFullyQualified(cliPath))
            throw new ArgumentException("ops_cli_path must be an absolute path.", nameof(cliPath));
        _cliPath = cliPath;
    }

    public async Task<ActivationHookResult> RunAsync(
        string phase,
        ActivationRecord record,
        CancellationToken cancellationToken)
    {
        if (phase is not ("preflight" or "preflight-replacement" or "apply-target" or "apply-rollback" or "verify-runtime" or "cleanup"))
            throw new ArgumentOutOfRangeException(nameof(phase));

        var startInfo = new ProcessStartInfo
        {
            FileName = _cliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("internal");
        startInfo.ArgumentList.Add("activation");
        startInfo.ArgumentList.Add(phase);
        startInfo.ArgumentList.Add("--lane");
        startInfo.ArgumentList.Add(record.Lane);
        startInfo.ArgumentList.Add("--activation-id");
        startInfo.ArgumentList.Add(record.ActivationId);
        startInfo.ArgumentList.Add("--expected-revision");
        startInfo.ArgumentList.Add(record.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--json");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new ActivationProtocolException($"Failed to start scpsl-ops hook '{phase}'.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, record.Readiness.TimeoutSeconds)));

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, process, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, process, timeout.Token);
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(timeout.Token),
                stdoutTask,
                stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' timed out.");
        }
        catch
        {
            await KillAndWaitAsync(process).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        ActivationHookResult result;
        try
        {
            result = JsonSerializer.Deserialize(stdout, OpsJsonContext.Default.ActivationHookResult)
                     ?? throw new JsonException("JSON null is not a hook result.");
        }
        catch (Exception ex)
        {
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' exited {process.ExitCode} with invalid JSON: {ex.Message} {Bound(stderr)}", ex);
        }

        if (process.ExitCode != 0 && result.Ok)
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' claimed success but exited {process.ExitCode}.");

        if (result.ProtocolVersion != 1 || result.ActivationId != record.ActivationId || result.Phase != phase)
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' response identity does not match the request.");
        if (result.Checks == null)
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' omitted checks.");

        var expectedCheckpoint = phase == "apply-rollback" ? record.PreviousCheckpoint : record.PendingCheckpoint;
        if (result.Ok && phase != "cleanup" && phase != "verify-runtime" && result.Checkpoint != expectedCheckpoint)
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' returned the wrong checkpoint.");
        if (result.Ok && phase == "verify-runtime" && result.Checkpoint is null)
            throw new ActivationProtocolException("verify-runtime must return the finalized checkpoint.");
        if (result.Ok && phase == "verify-runtime" && record.VerificationTarget == "previous" &&
            result.Checkpoint != record.PreviousCheckpoint)
            throw new ActivationProtocolException("verify-runtime returned a different rollback checkpoint.");
        if (!result.Ok && string.IsNullOrWhiteSpace(result.Error))
            throw new ActivationProtocolException($"scpsl-ops hook '{phase}' failed without an error.");

        return result;
    }

    private static string Bound(string text)
    {
        text = text.Trim();
        return text.Length <= 512 ? text : text[..512];
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        Process process,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(4096, MaximumOutputCharacters));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return result.ToString();
            if (result.Length > MaximumOutputCharacters - read)
            {
                TryKill(process);
                throw new ActivationProtocolException("scpsl-ops hook exceeded the output limit.");
            }
            result.Append(buffer, 0, read);
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        TryKill(process);
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // The process may race its own exit or become unavailable after Kill. The kill request
            // has still been issued; never mask the original hook failure with teardown noise.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during cancellation/failure; KillAndWaitAsync preserves the root cause.
        }
    }
}
