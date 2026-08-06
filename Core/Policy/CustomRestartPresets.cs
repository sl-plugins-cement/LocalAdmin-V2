// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// File-backed restart presets, so an operator or agent can author a condition on demand by
/// dropping a small JSON file into <c>restart_presets/</c> under the LocalAdmin config directory
/// (one file per preset; the preset name is the filename without extension). Files are read fresh
/// on each lookup/list - no reload command or restart is needed. Built-in presets always win a
/// name collision so a file can never silently shadow a vetted preset.
///
/// Example <c>restart_presets/late-night.json</c>:
/// <code>
/// {
///   "summary": "Restart at the next round end with fewer than 3 players.",
///   "edge": "round-end",
///   "maxPlayers": 2
/// }
/// </code>
/// </summary>
internal static class CustomRestartPresets
{
    internal const string DirectoryName = "restart_presets";

    /// <summary>Flat, agent-friendly JSON shape. Unknown fields are rejected (typo safety).</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PresetFile
    {
        [JsonPropertyName("summary")] public string? Summary { get; set; }

        /// <summary>"round-end" or "low-activity".</summary>
        [JsonPropertyName("edge")] public string? Edge { get; set; }

        [JsonPropertyName("rounds")] public int Rounds { get; set; } = 1;
        [JsonPropertyName("maxPlayers")] public int? MaxPlayers { get; set; }
        [JsonPropertyName("sustainedSeconds")] public int SustainedSeconds { get; set; }
        [JsonPropertyName("confirmations")] public int Confirmations { get; set; } = 2;
        [JsonPropertyName("tickSeconds")] public int TickSeconds { get; set; } = 30;
        [JsonPropertyName("activityWindowMinutes")] public int ActivityWindowMinutes { get; set; }
        [JsonPropertyName("activityOverPlayers")] public int ActivityOverPlayers { get; set; }
    }

    /// <summary>
    /// Parses one preset JSON document into a validated condition. Exposed for tests and reused
    /// by the file loader.
    /// </summary>
    internal static bool TryParse(string json, out RestartCondition condition, out string summary, out string error)
    {
        condition = null!;
        summary = string.Empty;

        PresetFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, RestartPresetJsonContext.Default.PresetFile);
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }

        if (file == null)
        {
            error = "invalid JSON: document is null.";
            return false;
        }

        RestartEdge edge;
        switch (file.Edge?.Trim().ToLowerInvariant())
        {
            case "round-end":
            case "roundend":
            case "round":
                edge = RestartEdge.RoundEnd;
                break;

            case "low-activity":
            case "lowactivity":
            case "tick":
                edge = RestartEdge.LowActivityTick;
                break;

            default:
                error = $"\"edge\" must be \"round-end\" or \"low-activity\" (got \"{file.Edge}\").";
                return false;
        }

        condition = new RestartCondition
        {
            Edge = edge,
            Rounds = file.Rounds,
            MaxPlayers = file.MaxPlayers,
            SustainedSeconds = file.SustainedSeconds,
            Confirmations = file.Confirmations,
            TickSeconds = file.TickSeconds,
            ActivityWindowMinutes = file.ActivityWindowMinutes,
            ActivityOverPlayers = file.ActivityOverPlayers
        };

        if (!condition.Validate(out error))
            return false;

        summary = string.IsNullOrWhiteSpace(file.Summary) ? condition.Describe() : file.Summary.Trim();
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Loads a single custom preset by name from <paramref name="directory"/>. Returns false with
    /// a reason when the file is missing or invalid.
    /// </summary>
    internal static bool TryLoad(string directory, string name, out RestartPresets.Preset preset, out string error)
    {
        preset = null!;

        if (!IsValidName(name))
        {
            error = "preset names may contain only letters, digits, and dashes.";
            return false;
        }

        var path = Path.Combine(directory, name + ".json");
        if (!File.Exists(path))
        {
            error = $"no preset file \"{name}.json\" in {DirectoryName}/.";
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            error = $"could not read \"{name}.json\": {ex.Message}";
            return false;
        }

        if (!TryParse(json, out var condition, out var summary, out var parseError))
        {
            error = $"\"{name}.json\": {parseError}";
            return false;
        }

        preset = new RestartPresets.Preset(name.ToLowerInvariant(), summary, condition);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Enumerates all custom presets in <paramref name="directory"/>. Invalid files are reported
    /// as one-line problems instead of being silently skipped.
    /// </summary>
    internal static (List<RestartPresets.Preset> Presets, List<string> Problems) LoadAll(string directory)
    {
        var presets = new List<RestartPresets.Preset>();
        var problems = new List<string>();

        if (!Directory.Exists(directory))
            return (presets, problems);

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (TryLoad(directory, name, out var preset, out var error))
                presets.Add(preset);
            else
                problems.Add(error);
        }

        presets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return (presets, problems);
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            return false;

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }

        return true;
    }
}

/// <summary>AOT-safe source-generated serializer context (the project publishes with PublishAot).</summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(CustomRestartPresets.PresetFile))]
internal sealed partial class RestartPresetJsonContext : JsonSerializerContext
{
}
