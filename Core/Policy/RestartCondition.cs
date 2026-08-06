// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Text;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>Which event evaluates a pending restart intent.</summary>
internal enum RestartEdge : byte
{
    /// <summary>A real round restart (the boot-time initial round is never counted).</summary>
    RoundEnd,

    /// <summary>The low-activity watchdog's periodic tick.</summary>
    LowActivityTick
}

/// <summary>
/// A declarative restart condition: one <see cref="Edge"/> (the event that evaluates the intent)
/// plus optional gates (state checks at evaluation time). An edge without gates fires
/// unconditionally; an edge with gates fires only when every gate holds, otherwise it waits for
/// the next edge. Gates are always checked against a FRESH authoritative player count - if no
/// fresh count is available at the edge, the intent skips (never fires on stale data).
/// </summary>
internal sealed record RestartCondition
{
    /// <summary>The event that evaluates this intent.</summary>
    internal required RestartEdge Edge { get; init; }

    /// <summary>For <see cref="RestartEdge.RoundEnd"/>: fire on the Nth qualifying round restart.</summary>
    internal int Rounds { get; init; } = 1;

    /// <summary>Gate: fresh player count must be at or below this to fire. Null = no player gate.</summary>
    internal int? MaxPlayers { get; init; }

    /// <summary>
    /// For <see cref="RestartEdge.LowActivityTick"/>: the low state must hold this long before
    /// firing. 0 = a confirmed low reading is enough.
    /// </summary>
    internal int SustainedSeconds { get; init; }

    /// <summary>For <see cref="RestartEdge.LowActivityTick"/>: consecutive confirming ticks required.</summary>
    internal int Confirmations { get; init; } = 2;

    /// <summary>For <see cref="RestartEdge.LowActivityTick"/>: seconds between ticks.</summary>
    internal int TickSeconds { get; init; } = 30;

    /// <summary>
    /// Recent-activity guard: block firing if the last <see cref="ActivityWindowMinutes"/> peaked
    /// above <see cref="ActivityOverPlayers"/>. 0 window = guard disabled.
    /// </summary>
    internal int ActivityWindowMinutes { get; init; }

    internal int ActivityOverPlayers { get; init; }

    /// <summary>Human-readable one-line summary for status output.</summary>
    internal string Describe()
    {
        var sb = new StringBuilder();

        if (Edge == RestartEdge.RoundEnd)
        {
            sb.Append(Rounds <= 1 ? "on the next round restart" : $"after {Rounds} round restarts");
            if (MaxPlayers.HasValue)
                sb.Append($" with at most {MaxPlayers.Value} players (otherwise waits for a later round)");
        }
        else
        {
            sb.Append($"when players <= {MaxPlayers ?? 0} for {Confirmations} tick(s) every {TickSeconds}s");
            if (SustainedSeconds > 0)
                sb.Append($" sustained {SustainedSeconds}s");
            if (ActivityWindowMinutes > 0)
                sb.Append($"; blocked if the last {ActivityWindowMinutes} min peaked above {ActivityOverPlayers} players");
        }

        return sb.ToString();
    }

    /// <summary>Validates the condition; returns false with a reason for agent-friendly errors.</summary>
    internal bool Validate(out string error)
    {
        if (Rounds < 1)
        {
            error = "rounds must be 1 or higher.";
            return false;
        }

        if (MaxPlayers is < 0)
        {
            error = "player threshold must be 0 or higher.";
            return false;
        }

        if (Edge == RestartEdge.LowActivityTick && MaxPlayers is null)
        {
            error = "a low-activity condition requires a player threshold.";
            return false;
        }

        if (SustainedSeconds < 0 || Confirmations < 1 || TickSeconds < 1 ||
            ActivityWindowMinutes < 0 || ActivityOverPlayers < 0)
        {
            error = "durations, confirmations, ticks, and activity values must be non-negative (confirmations/tick at least 1).";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
