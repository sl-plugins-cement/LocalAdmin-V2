// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Predefined restart actions, so a runtime operator (human or agent) selects a vetted preset by
/// name instead of authoring raw condition parameters. Every action arms a LocalAdmin-managed,
/// readable, cancellable intent.
/// </summary>
internal static class RestartPresets
{
    /// <summary>
    /// A preset is implemented by the LocalAdmin intent manager so status and cancellation have
    /// one deterministic owner.
    /// </summary>
    internal sealed record Preset(
        string Name,
        string Summary,
        RestartCondition Condition);

    // Ordered so "restartwhen list" prints a stable, curated catalog.
    private static readonly Preset[] All =
    {
        new("safe", "Restart as soon as confirmed empty; otherwise at the next round end.",
            new RestartCondition { Edge = RestartEdge.RoundEnd, RestartIfEmpty = true }),
        new("low-pop", "Restart as soon as confirmed empty; otherwise at a round end with fewer than 5 players.",
            new RestartCondition { Edge = RestartEdge.RoundEnd, MaxPlayers = 4, RestartIfEmpty = true }),
        new("now", "Restart immediately, including when players are connected.",
            new RestartCondition { Edge = RestartEdge.Immediate }),
    };

    internal static IReadOnlyList<Preset> List => All;

    internal static bool TryGet(string name, out Preset preset)
    {
        // Compatibility for existing CLI deployment records; not separate menu choices.
        var requestedName = name.ToLowerInvariant();
        name = requestedName switch { "next-round" => "safe", "next-round-lowpop" => "low-pop", _ => name };
        preset = All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))!;
        if (preset != null && (requestedName == "next-round" || requestedName == "next-round-lowpop"))
            preset = preset with { Name = requestedName };
        return preset != null;
    }

    /// <summary>
    /// Resolves a preset by name: built-ins first (a file can never shadow a vetted preset),
    /// then JSON files in <paramref name="customDirectory"/>.
    /// </summary>
    internal static bool TryResolve(string name, string customDirectory, out Preset preset, out string error)
    {
        if (TryGet(name, out preset))
        {
            error = string.Empty;
            return true;
        }

        return CustomRestartPresets.TryLoad(customDirectory, name, out preset, out error);
    }

    /// <summary>
    /// One line per preset (built-in plus custom), for <c>restartwhen list</c> and error
    /// messages. Invalid custom files are reported as problems rather than silently skipped.
    /// </summary>
    internal static IEnumerable<string> DescribeAll(string customDirectory)
    {
        foreach (var p in All)
            yield return $"{p.Name,-18} {p.Summary}";

        var (custom, problems) = CustomRestartPresets.LoadAll(customDirectory);
        foreach (var p in custom.Where(c => !TryGet(c.Name, out _)))
            yield return $"{p.Name,-18} {p.Summary} [custom]";

        foreach (var problem in problems)
            yield return $"[invalid] {problem}";
    }
}
