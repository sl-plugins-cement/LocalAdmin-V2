using System;
using System.Collections.Generic;
using System.IO;

namespace LocalAdmin.V2.Core;

internal static class StartupEnvironment
{
    internal static string[] Apply(IReadOnlyList<string> args)
    {
        var forwarded = new List<string>(args.Count);

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--")
            {
                for (; i < args.Count; i++)
                    forwarded.Add(args[i]);
                break;
            }

            if (args[i] is not ("--game-state-root" or "--prepend-path"))
            {
                forwarded.Add(args[i]);
                continue;
            }

            var option = args[i];
            if (++i >= args.Count || string.IsNullOrWhiteSpace(args[i]))
                throw new ArgumentException($"{option} requires a path.");

            var path = args[i];
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException($"{option} requires an absolute path: {path}");

            if (option == "--game-state-root")
            {
                Environment.SetEnvironmentVariable("SCPSL_OPS_STATE_ROOT", path);
            }
            else
            {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException($"PATH entry does not exist: {path}");

                var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var prefix = path + Path.PathSeparator;
                if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("PATH", prefix + current);
            }
        }

        return forwarded.ToArray();
    }
}
