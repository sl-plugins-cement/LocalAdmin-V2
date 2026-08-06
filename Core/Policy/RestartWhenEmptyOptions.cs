// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using LocalAdmin.V2.IO;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>Validated, immutable settings for a <see cref="RestartWhenEmptyPolicy"/> run.</summary>
internal sealed class RestartWhenEmptyOptions
{
    private const bool DefaultRequireFullRecentWindow = true;

    internal RestartWhenEmptyOptions(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes,
        string label,
        string restartReason,
        int durationSeconds = 0,
        bool requireFullRecentWindow = DefaultRequireFullRecentWindow)
    {
        RestartAtOrBelowPlayers = restartAtOrBelowPlayers;
        EmptyConfirmations = Math.Max(1, emptyConfirmations);
        TickSeconds = Math.Max(1, tickSeconds);
        DurationSeconds = Math.Max(0, durationSeconds);
        RecentActivityOverPlayers = recentActivityOverPlayers;
        RecentActivityWindowMinutes = recentActivityWindowMinutes;
        Label = label;
        RestartReason = restartReason;
        RequireFullRecentWindow = requireFullRecentWindow;
    }

    internal int RestartAtOrBelowPlayers { get; }
    internal int EmptyConfirmations { get; }
    internal int TickSeconds { get; }
    internal int DurationSeconds { get; }
    internal int RecentActivityOverPlayers { get; }
    internal int RecentActivityWindowMinutes { get; }
    internal string Label { get; }
    internal string RestartReason { get; }
    internal bool RequireFullRecentWindow { get; }

    internal string Summary =>
        FormatSummary(RestartAtOrBelowPlayers, EmptyConfirmations, TickSeconds, DurationSeconds, RecentActivityOverPlayers, RecentActivityWindowMinutes);

    /// <summary>The permanent config-enabled watchdog.</summary>
    internal static RestartWhenEmptyOptions FromConfig(Config config) =>
        new(
            config.RestartAtOrBelowPlayers,
            config.EmptyConfirmations,
            config.PolicyTickSeconds,
            config.RecentActivityOverPlayers,
            config.RecentActivityWindowMinutes,
            "Restart-when-empty watchdog",
            "restart-when-empty watchdog",
            config.EmptyDurationSeconds);

    /// <summary>A one-shot low-activity restart intent (preset or legacy command).</summary>
    internal static RestartWhenEmptyOptions FromCondition(RestartCondition condition) =>
        new(
            condition.MaxPlayers ?? 0,
            condition.Confirmations,
            condition.TickSeconds,
            condition.ActivityOverPlayers,
            condition.ActivityWindowMinutes,
            "Restart intent",
            "restart intent (low activity)",
            condition.SustainedSeconds);

    internal static string FormatSummary(
        int restartAtOrBelowPlayers,
        int emptyConfirmations,
        int tickSeconds,
        int durationSeconds,
        int recentActivityOverPlayers,
        int recentActivityWindowMinutes)
    {
        var summary =
            $"Restart at or below {restartAtOrBelowPlayers} players after {emptyConfirmations} confirmation(s)";

        if (durationSeconds > 0)
            summary += $" and at least {durationSeconds}s sustained";

        summary += $", polling every {tickSeconds}s";

        if (recentActivityWindowMinutes <= 0)
            return summary + "; recent-activity guard disabled.";

        return summary + $"; blocked if the last {recentActivityWindowMinutes} min peaked above {recentActivityOverPlayers} players.";
    }
}
