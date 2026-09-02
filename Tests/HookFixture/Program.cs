using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

var pidFile = Environment.GetEnvironmentVariable("LOCALADMIN_HOOK_FIXTURE_PID_FILE");
if (!string.IsNullOrWhiteSpace(pidFile))
    await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

var phase = args.Length > 2 ? args[2] : string.Empty;
if (phase == "apply-target")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
else if (phase == "apply-rollback")
{
    while (true)
    {
        Console.Write(new string('x', 4096));
        await Console.Out.FlushAsync();
    }
}

return 0;
