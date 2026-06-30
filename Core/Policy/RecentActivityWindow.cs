// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// A rolling-activity guard ported from codex-restart-when-empty-7777.ps1. It keeps the
/// player-count samples observed over the last <c>windowMinutes</c> and reports whether
/// the window peaked above a threshold, so a server that was recently busy is not
/// restarted the moment it briefly empties.
/// </summary>
internal sealed class RecentActivityWindow
{
    private readonly object _lock = new();
    private readonly Queue<(DateTime AtUtc, int Players)> _samples = new();

    private DateTime? _firstObservedUtc;

    /// <summary>Records a sample and prunes anything older than the window.</summary>
    internal void AddSample(int players, DateTime nowUtc, int windowMinutes)
    {
        // windowMinutes <= 0 disables the recent-activity guard entirely; don't accumulate.
        if (windowMinutes <= 0)
            return;

        lock (_lock)
        {
            _firstObservedUtc ??= nowUtc;

            _samples.Enqueue((nowUtc, players < 0 ? 0 : players));
            Prune(nowUtc, windowMinutes);
        }
    }

    /// <summary>
    /// Evaluates the guard for the given moment. <paramref name="requireFullWindow"/>
    /// mirrors the script's RequireFullRecentWindow: when true the guard blocks until we
    /// have actually observed the server for the full window duration (cold-start safety).
    /// </summary>
    internal ActivityDecision Evaluate(DateTime nowUtc, int windowMinutes, int overPlayers, bool requireFullWindow)
    {
        // Guard disabled: always covered, never high-activity, so it never blocks a restart.
        if (windowMinutes <= 0)
            return new ActivityDecision(true, 0, 0, false);

        lock (_lock)
        {
            Prune(nowUtc, windowMinutes);

            var maxPlayers = 0;
            foreach (var s in _samples)
            {
                if (s.Players > maxPlayers)
                    maxPlayers = s.Players;
            }

            var cutoffUtc = windowMinutes > 0 ? nowUtc.AddMinutes(-windowMinutes) : nowUtc;

            var windowCovered = true;
            if (requireFullWindow && windowMinutes > 0)
                windowCovered = _firstObservedUtc.HasValue && _firstObservedUtc.Value <= cutoffUtc;

            return new ActivityDecision(
                windowCovered,
                _samples.Count,
                maxPlayers,
                maxPlayers > overPlayers);
        }
    }

    internal void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
            _firstObservedUtc = null;
        }
    }

    private void Prune(DateTime nowUtc, int windowMinutes)
    {
        if (windowMinutes <= 0)
            return;

        var cutoffUtc = nowUtc.AddMinutes(-windowMinutes);
        while (_samples.Count > 0 && _samples.Peek().AtUtc < cutoffUtc)
            _samples.Dequeue();
    }

    internal readonly struct ActivityDecision
    {
        internal ActivityDecision(bool windowCovered, int recentSampleCount, int recentMaxPlayers, bool recentHighActivity)
        {
            WindowCovered = windowCovered;
            RecentSampleCount = recentSampleCount;
            RecentMaxPlayers = recentMaxPlayers;
            RecentHighActivity = recentHighActivity;
        }

        internal bool WindowCovered { get; }
        internal int RecentSampleCount { get; }
        internal int RecentMaxPlayers { get; }
        internal bool RecentHighActivity { get; }
    }
}
