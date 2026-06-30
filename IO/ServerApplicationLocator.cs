using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace LocalAdmin.V2.IO;

internal sealed class ServerApplicationLocation
{
    internal ServerApplicationLocation(string executablePath, string workingDirectory, string source)
    {
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        Source = source;
    }

    internal string ExecutablePath { get; }
    internal string WorkingDirectory { get; }
    internal string Source { get; }
}

internal static class ServerApplicationLocator
{
    private const string DedicatedServerSteamAppId = "996560";
    private const string DedicatedServerSteamFolder = "SCP Secret Laboratory Dedicated Server";

    internal static bool TryLocate(
        string executableName,
        string configuredPath,
        string? configFilePath,
        out ServerApplicationLocation? location,
        out IReadOnlyList<string> attemptedPaths)
    {
        var candidates = new List<ServerApplicationCandidate>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var hasConfiguredPath = HasConfiguredPath(configuredPath);

        AddConfiguredPathCandidates(candidates, seen, executableName, configuredPath, configFilePath);

        if (!hasConfiguredPath)
        {
            AddCandidate(candidates, seen, "current working directory", Path.Combine(Environment.CurrentDirectory, executableName));
            AddCandidate(candidates, seen, "LocalAdmin executable directory", Path.Combine(AppContext.BaseDirectory, executableName));
            AddSteamCandidates(candidates, seen, executableName);
        }

        var attempts = new List<string>();

        foreach (var candidate in candidates)
        {
            var exists = File.Exists(candidate.ExecutablePath);
            attempts.Add($"{candidate.Source}: {candidate.ExecutablePath}" + (exists ? " (found)" : string.Empty));

            if (!exists)
                continue;

            var workingDirectory = Path.GetDirectoryName(candidate.ExecutablePath);
            if (string.IsNullOrWhiteSpace(workingDirectory))
                workingDirectory = Environment.CurrentDirectory;

            location = new ServerApplicationLocation(candidate.ExecutablePath, workingDirectory, candidate.Source);
            attemptedPaths = attempts;
            return true;
        }

        location = null;
        attemptedPaths = attempts;
        return false;
    }

    private static bool HasConfiguredPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return false;

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        return !string.IsNullOrWhiteSpace(expanded);
    }

    private static void AddConfiguredPathCandidates(
        List<ServerApplicationCandidate> candidates,
        HashSet<string> seen,
        string executableName,
        string configuredPath,
        string? configFilePath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return;

        var configDirectory = string.IsNullOrWhiteSpace(configFilePath) ? null : Path.GetDirectoryName(configFilePath);

        foreach (var resolved in ResolveConfiguredPaths(configuredPath, configDirectory))
        {
            var executablePath = LooksLikeExecutablePath(resolved, executableName)
                ? resolved
                : Path.Combine(resolved, executableName);

            AddCandidate(candidates, seen, "configured server_application_path", executablePath);
        }
    }

    private static IEnumerable<string> ResolveConfiguredPaths(string configuredPath, string? configDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(expanded))
            yield break;

        if (Path.IsPathFullyQualified(expanded))
        {
            yield return expanded;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(configDirectory))
            yield return Path.GetFullPath(expanded, configDirectory);

        yield return Path.GetFullPath(expanded, Environment.CurrentDirectory);
    }

    private static bool LooksLikeExecutablePath(string path, string executableName)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(trimmed);

        if (fileName.Equals(executableName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return true;

        return OperatingSystem.IsWindows() && Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSteamCandidates(
        List<ServerApplicationCandidate> candidates,
        HashSet<string> seen,
        string executableName)
    {
        foreach (var libraryRoot in GetSteamLibraryRoots())
        {
            var installDir = TryReadSteamInstallDir(libraryRoot) ?? DedicatedServerSteamFolder;
            AddCandidate(
                candidates,
                seen,
                "Steam library",
                Path.Combine(libraryRoot, "steamapps", "common", installDir, executableName));
        }
    }

    private static IEnumerable<string> GetSteamLibraryRoots()
    {
        var steamRoots = new List<string>();
        var seenRoots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var root in GetSteamInstallRoots())
        {
            AddDirectory(steamRoots, seenRoots, root);

            var libraryFoldersPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (var libraryRoot in ReadSteamLibraryFolders(libraryFoldersPath))
                AddDirectory(steamRoots, seenRoots, libraryRoot);
        }

        foreach (var root in steamRoots)
            yield return root;
    }

    private static IEnumerable<string> GetSteamInstallRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var registryPath in new[]
                     {
                         @"HKEY_CURRENT_USER\Software\Valve\Steam",
                         @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                         @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
                     })
            {
                foreach (var valueName in new[] { "InstallPath", "SteamPath" })
                {
                    string? value = null;

                    try
                    {
                        value = Registry.GetValue(registryPath, valueName, null) as string;
                    }
                    catch
                    {
                        // Registry probing is best-effort; later candidates still cover common installs.
                    }

                    if (!string.IsNullOrWhiteSpace(value))
                        yield return value;
                }
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
                yield return Path.Combine(programFilesX86, "Steam");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "Steam");

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            yield break;

        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
    }

    private static IEnumerable<string> ReadSteamLibraryFolders(string libraryFoldersPath)
    {
        if (!File.Exists(libraryFoldersPath))
            yield break;

        IEnumerable<string> lines;

        try
        {
            lines = File.ReadLines(libraryFoldersPath);
        }
        catch
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var fields = ReadVdfQuotedFields(line);
            if (fields.Count >= 2 && fields[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                yield return fields[1];
        }
    }

    private static string? TryReadSteamInstallDir(string libraryRoot)
    {
        var manifestPath = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{DedicatedServerSteamAppId}.acf");
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            foreach (var line in File.ReadLines(manifestPath))
            {
                var fields = ReadVdfQuotedFields(line);
                if (fields.Count >= 2 && fields[0].Equals("installdir", StringComparison.OrdinalIgnoreCase))
                    return fields[1];
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void AddDirectory(List<string> roots, HashSet<string> seenRoots, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var fullPath = ToFullPath(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')));
        if (seenRoots.Add(NormalizeKey(fullPath)))
            roots.Add(fullPath);
    }

    private static void AddCandidate(
        List<ServerApplicationCandidate> candidates,
        HashSet<string> seen,
        string source,
        string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        var fullPath = ToFullPath(Environment.ExpandEnvironmentVariables(executablePath.Trim().Trim('"')));
        if (seen.Add(NormalizeKey(fullPath)))
            candidates.Add(new ServerApplicationCandidate(source, fullPath));
    }

    private static string ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string NormalizeKey(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static List<string> ReadVdfQuotedFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (!inQuote)
            {
                if (c == '"')
                {
                    inQuote = true;
                    current.Clear();
                }

                continue;
            }

            if (c == '\\' && i + 1 < line.Length)
            {
                current.Append(line[++i]);
                continue;
            }

            if (c == '"')
            {
                inQuote = false;
                fields.Add(current.ToString());
                continue;
            }

            current.Append(c);
        }

        return fields;
    }

    private sealed class ServerApplicationCandidate
    {
        internal ServerApplicationCandidate(string source, string executablePath)
        {
            Source = source;
            ExecutablePath = executablePath;
        }

        internal string Source { get; }
        internal string ExecutablePath { get; }
    }
}
