using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.Core.Activation;
using Xunit;

namespace LocalAdmin.V2.Tests;

[Collection("Activation hook process")]
public sealed class ActivationHookRunnerTests : IDisposable
{
    private readonly string _pidFile = Path.Combine(Path.GetTempPath(), "la-hook-pid-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cancellation_Kills_The_Hook_Process()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var runner = new ActivationHookRunner(FixturePath());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("apply-target", ActivationRecordStoreTests.Open(
                ActivationRecordStoreTests.Verified(), ActivationStates.Activating), cancellation.Token));

        Assert.False(IsFixtureRunning());
    }

    [Fact]
    public async Task Excess_Output_Is_Stopped_At_The_Bound_And_Kills_The_Hook_Process()
    {
        var runner = new ActivationHookRunner(FixturePath());

        var exception = await Assert.ThrowsAsync<ActivationProtocolException>(() =>
            runner.RunAsync("apply-rollback", ActivationRecordStoreTests.Open(
                ActivationRecordStoreTests.Verified(), ActivationStates.RollingBack) with
                {
                    VerificationTarget = "previous",
                    RollbackAttempted = true
                }, CancellationToken.None));

        Assert.Contains("output limit", exception.Message);
        Assert.False(IsFixtureRunning());
    }

    private bool IsFixtureRunning()
    {
        if (!File.Exists(_pidFile) || !int.TryParse(File.ReadAllText(_pidFile), out var pid))
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string FixturePath()
    {
        Environment.SetEnvironmentVariable("LOCALADMIN_HOOK_FIXTURE_PID_FILE", _pidFile);
        return Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "LocalAdmin.HookFixture.exe" : "LocalAdmin.HookFixture");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALADMIN_HOOK_FIXTURE_PID_FILE", null);
        try { File.Delete(_pidFile); }
        catch { /* test cleanup */ }
    }
}

[CollectionDefinition("Activation hook process", DisableParallelization = true)]
public sealed class ActivationHookProcessCollection;
