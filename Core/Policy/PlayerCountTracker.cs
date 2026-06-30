// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Tracks the live player count by parsing the SCP:SL console output that LocalAdmin
/// already receives, plus the authoritative Idle enter/exit control bytes from the
/// game's TCP channel. All mutations are guarded by a single lock so the policy
/// watchdog, the console-line handler and the TCP receive loop can all touch it safely.
/// </summary>
internal sealed class PlayerCountTracker
{
    // "<color>...authenticated from endpoint 1.2.3.4:5678. ... Player ID assigned: 7."
    private static readonly Regex JoinRegex = new(
        @"authenticated from endpoint .+ Player ID assigned: (\d+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "<color>... 7 disconnected from IP address 1.2.3.4. Last class: ..."
    // The captured id (when present) lets us keep the join set in sync.
    private static readonly Regex LeaveRegex = new(
        @"(?:Player\s+)?(\d+)?\s*disconnected from IP address .+ Last class:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _lock = new();

    // Authoritative set of player ids currently authenticated (per console parsing).
    private readonly HashSet<int> _players = new();

    // The game raised IdleEnter (no players) - treated as an authoritative "empty" signal
    // that overrides any drift in the parsed set.
    private bool _idleEmpty;

    // Becomes true once we have observed an authoritative readiness signal for THIS session
    // (the game's IdleEnter/IdleExit control byte, or a real player join). Until then the
    // player count is "unknown" and the restart-when-empty policy must not act on it - this
    // prevents restarting an empty count reported merely because the game is still booting.
    private bool _ready;

    // True when CurrentCount is meaningful for policy decisions. IdleExit proves the server
    // is non-idle, but if the matching join line is not parsed we know only "nonzero", not
    // the actual count, so restart-when-empty must not treat the parsed set size as empty.
    private bool _countKnown;

    /// <summary>
    /// Best-effort current player count. When the game reports IDLE (IdleEnter without a
    /// subsequent IdleExit) we trust that as 0 regardless of any parsing drift.
    /// </summary>
    internal int CurrentCount
    {
        get
        {
            lock (_lock)
                return _idleEmpty ? 0 : _players.Count;
        }
    }

    /// <summary>
    /// True once an authoritative signal (idle byte or a real join) has established that the
    /// server is up for this session. The restart-when-empty policy also requires
    /// <see cref="TryGetKnownCount"/> before it can act on the count.
    /// </summary>
    internal bool IsReady
    {
        get
        {
            lock (_lock)
                return _ready;
        }
    }

    /// <summary>
    /// Attempts to read a count that is safe for restart-when-empty decisions.
    /// </summary>
    internal bool TryGetKnownCount(out int count)
    {
        lock (_lock)
        {
            if (!_ready || !_countKnown)
            {
                count = 0;
                return false;
            }

            count = _idleEmpty ? 0 : _players.Count;
            return true;
        }
    }

    /// <summary>
    /// Feeds a single console line (already stripped of its color prefix). Returns true if
    /// the count changed as a result.
    /// </summary>
    internal bool ProcessConsoleLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        var joinMatch = JoinRegex.Match(line);
        if (joinMatch.Success &&
            int.TryParse(joinMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var joinId))
        {
            lock (_lock)
            {
                // A real authentication means the server is up and no longer idle/empty.
                _ready = true;
                _countKnown = true;
                var changed = _players.Add(joinId);
                if (_idleEmpty)
                {
                    _idleEmpty = false;
                    changed = true;
                }

                return changed;
            }
        }

        var leaveMatch = LeaveRegex.Match(line);
        if (leaveMatch.Success)
        {
            lock (_lock)
            {
                if (leaveMatch.Groups[1].Success &&
                    int.TryParse(leaveMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leaveId))
                    return _players.Remove(leaveId);

                // No id captured: we can't reliably know who left. Don't guess (avoids
                // under-counting). The IdleEnter byte remains the authoritative empty signal.
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Authoritative empty signal from the game's IdleEnter control byte. The game only emits
    /// this once it is up and idle, so it also marks the session as ready.
    /// </summary>
    internal void NotifyIdleEnter()
    {
        lock (_lock)
        {
            _ready = true;
            _countKnown = true;
            _idleEmpty = true;
            _players.Clear();
        }
    }

    /// <summary>
    /// The game left idle mode (a player connected) - clear the empty override and mark ready.
    /// If no player id has been parsed yet, the count is nonzero but unknown.
    /// </summary>
    internal void NotifyIdleExit()
    {
        lock (_lock)
        {
            _ready = true;
            _idleEmpty = false;
            _countKnown = _players.Count > 0;
        }
    }

    /// <summary>Resets all tracked state. Called on a fresh session/restart.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _players.Clear();
            _idleEmpty = false;
            _ready = false;
            _countKnown = false;
        }
    }
}
