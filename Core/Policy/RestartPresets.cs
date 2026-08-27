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
        new("next-round",
            "Restart after the current round through the cancellable LocalAdmin intent.",
            new RestartCondition { Edge = RestartEdge.RoundEnd }),

        new("next-round-lowpop",
            "Restart on the next round restart that ends with fewer than 5 players; otherwise wait for a later round.",
            new RestartCondition { Edge = RestartEdge.RoundEnd, MaxPlayers = 4 }),

        new("empty",
            "Restart once the server has been empty for 30 sustained minutes.",
            new RestartCondition
            {
                Edge = RestartEdge.LowActivityTick,
                MaxPlayers = 0,
                SustainedSeconds = 1800,
                Confirmations = 2,
                TickSeconds = 30
            }),

        new("low-30m",
            "Restart once fewer than 2 players have been on for 30 sustained minutes.",
            new RestartCondition
            {
                Edge = RestartEdge.LowActivityTick,
                MaxPlayers = 1,
                SustainedSeconds = 1800,
                Confirmations = 2,
                TickSeconds = 30
            }),

        new("quiet-hour",
            "Restart once fewer than 2 players have been on for 30 sustained minutes AND the last hour never peaked above 4 players.",
            new RestartCondition
            {
                Edge = RestartEdge.LowActivityTick,
                MaxPlayers = 1,
                SustainedSeconds = 1800,
                Confirmations = 2,
                TickSeconds = 30,
                ActivityWindowMinutes = 60,
                ActivityOverPlayers = 4
            }),
    };

    internal static IReadOnlyList<Preset> List => All;

    internal static bool TryGet(string name, out Preset preset)
    {
        preset = All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))!;
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
