// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Tracks the live player count for the restart-when-empty watchdog.
///
/// The <b>authoritative</b> source is the server's own <c>List of players (N):</c> console line
/// (emitted in response to the <c>players</c> command, which the watchdog polls) and the game's
/// IdleEnter control byte (an authoritative "0 players" signal). These are timestamped, so the
/// policy can require a <i>fresh</i> reading before it acts - it never restarts on a stale count.
/// A list reply is only accepted when a <c>players</c> poll was recently issued (see
/// <see cref="NotePlayersPollIssued()"/>) and the line matches the reply shape exactly, so
/// player-controlled text (e.g. a nickname crafted to look like a list header) can never spoof
/// the count.
///
/// The <c>authenticated ... Player ID assigned</c> / <c>disconnected from IP address</c> console
/// regexes are kept only as a <b>best-effort supplementary</b> signal for the live status display
/// before the first authoritative reading; they are intentionally NOT used to make restart
/// decisions, because their exact wording is version- and plugin-dependent and a disconnect line
/// does not reliably carry the numeric player id. All mutations are guarded by a single lock so
/// the policy watchdog, the console-line handler and the TCP receive loop can touch it safely.
/// </summary>
internal sealed class PlayerCountTracker
{
    // Authoritative: the server's reply to the "players" command, e.g. "List of players (0):"
    // (player entries follow on separate lines). End-anchored: console lines can embed
    // player-controlled text (a nickname leads the authentication line), so a line that merely
    // STARTS with this shape (e.g. a player named "List of players (0):" joining) must not match.
    internal static readonly Regex PlayersListRegex = new(
        @"^\s*List of players \((\d+)\):?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Best-effort supplementary only (see class summary) - not used for restart decisions.
    private static readonly Regex JoinRegex = new(
        @"authenticated from endpoint .+ Player ID assigned: (\d+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LeaveRegex = new(
        @"(?:Player\s+)?(\d+)?\s*disconnected from IP address .+ Last class:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // After the server is known to have left the empty state (an IdleExit byte OR a parsed join),
    // briefly reject ANY players reply: it may be the in-flight answer to a poll sent while the
    // server was still empty, and accepting a stale low count as fresh (not just 0 - anything at
    // or below a restart threshold) would drive a mid-round false restart. Bounded so it never
    // suppresses a genuine empty, and the watchdog re-polls every tick so a genuine reading lands
    // right after the window.
    private static readonly TimeSpan StaleReplySuppressWindow = TimeSpan.FromSeconds(5);

    // A players-list reply is only trusted if WE recently asked for one (the watchdog's poll or a
    // user-typed "players" command). Belt-and-suspenders on top of the end-anchored regex: an
    // unsolicited line that happens to look like a list reply is never an authoritative count.
    private static readonly TimeSpan PollReplyAcceptWindow = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();

    // Best-effort set of player ids seen via console parsing (supplementary display only).
    private readonly HashSet<int> _players = new();

    // Authoritative count + when it was observed. Null => currently unknown (must be (re)polled).
    private int? _authoritativeCount;
    private DateTime _authoritativeAtUtc;

    // Set when the server is known to have become non-empty (IdleExit or a parsed join); gates the
    // brief stale-reply suppression above.
    private DateTime? _leftEmptyAtUtc;

    // When a "players" poll was last sent to the game; gates list-reply acceptance above.
    private DateTime? _lastPollIssuedAtUtc;

    // Becomes true once we have observed ANY authoritative signal for THIS session (a players-list
    // reply, an idle byte, or a real join). Until then the count is "unknown" and the policy must
    // not act - this prevents restarting merely because the game is still booting.
    private bool _ready;

    /// <summary>
    /// True once an authoritative signal has established that the server is up for this session.
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
    /// Best-effort current player count for status display: the last authoritative reading if we
    /// have one, otherwise the size of the parsed set. Not safe for restart decisions - use
    /// <see cref="TryGetFreshCount"/> for those.
    /// </summary>
    internal int CurrentCount
    {
        get
        {
            lock (_lock)
                return _authoritativeCount ?? _players.Count;
        }
    }

    /// <summary>
    /// Returns the last authoritative count regardless of age (for status display).
    /// </summary>
    internal bool TryGetKnownCount(out int count)
    {
        lock (_lock)
        {
            if (_authoritativeCount.HasValue)
            {
                count = _authoritativeCount.Value;
                return true;
            }

            count = 0;
            return false;
        }
    }

    /// <summary>
    /// Returns the authoritative count only if it was observed within <paramref name="maxAge"/> of
    /// <paramref name="nowUtc"/>. This is the ONLY count the restart-when-empty policy acts on, so a
    /// stale reading can never drive a restart.
    /// </summary>
    internal bool TryGetFreshCount(DateTime nowUtc, TimeSpan maxAge, out int count)
    {
        lock (_lock)
        {
            if (_authoritativeCount.HasValue && nowUtc - _authoritativeAtUtc <= maxAge)
            {
                count = _authoritativeCount.Value;
                return true;
            }

            count = 0;
            return false;
        }
    }

    /// <summary>
    /// Records that a <c>players</c> command was just sent to the game. List replies are only
    /// accepted within <see cref="PollReplyAcceptWindow"/> of a noted poll, so console lines that
    /// merely look like a list reply (player-controlled text) can never set the count.
    /// </summary>
    internal void NotePlayersPollIssued() => NotePlayersPollIssued(DateTime.UtcNow);

    internal void NotePlayersPollIssued(DateTime nowUtc)
    {
        lock (_lock)
            _lastPollIssuedAtUtc = nowUtc;
    }

    /// <summary>
    /// Feeds a single console line (already stripped of its color prefix). Returns true if the
    /// authoritative count changed as a result.
    /// </summary>
    internal bool ProcessConsoleLine(string line) => ProcessConsoleLine(line, DateTime.UtcNow, out _);

    internal bool ProcessConsoleLine(string line, DateTime nowUtc) => ProcessConsoleLine(line, nowUtc, out _);

    /// <summary>
    /// As above, with the accepted response count exposed separately from the "count changed"
    /// return value. A correlated probe must complete even when two consecutive replies contain
    /// the same count.
    /// </summary>
    internal bool ProcessConsoleLine(string line, DateTime nowUtc, out int? acceptedPlayersReply)
    {
        acceptedPlayersReply = null;

        if (string.IsNullOrEmpty(line))
            return false;

        // Authoritative: the "players" command reply.
        var listMatch = PlayersListRegex.Match(line);
        if (listMatch.Success &&
            int.TryParse(listMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listCount))
        {
            lock (_lock)
            {
                // Only trust a reply we actually solicited (see PollReplyAcceptWindow above). An
                // unsolicited list-shaped line must not mark the session ready or set a count.
                if (!_lastPollIssuedAtUtc.HasValue ||
                    nowUtc - _lastPollIssuedAtUtc.Value > PollReplyAcceptWindow)
                    return false;

                _ready = true;

                // Reject any reply that lands within the suppression window after the server was
                // known to leave empty - it may be the stale answer to a poll issued before a join.
                if (_leftEmptyAtUtc.HasValue &&
                    nowUtc - _leftEmptyAtUtc.Value < StaleReplySuppressWindow)
                    return false;

                var changed = _authoritativeCount != listCount;
                _authoritativeCount = listCount;
                _authoritativeAtUtc = nowUtc;
                _leftEmptyAtUtc = null; // an accepted reading resolves the ambiguity

                // Keep the supplementary set from drifting unboundedly once we have ground truth.
                if (listCount == 0)
                    _players.Clear();

                acceptedPlayersReply = listCount;
                return changed;
            }
        }

        // A real join proves the server is up and non-empty. Beyond the supplementary set, this
        // INVALIDATES any (possibly stale, low) authoritative reading and opens the zero-reply
        // suppression window, so a fresh "List of players (0)" cannot drive a restart the moment a
        // player joins - the policy will re-poll for the true count. This is the only empty->nonempty
        // signal when the game's idle mode is disabled (no IdleExit byte).
        var joinMatch = JoinRegex.Match(line);
        if (joinMatch.Success &&
            int.TryParse(joinMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var joinId))
        {
            lock (_lock)
            {
                _ready = true;
                _players.Add(joinId);
                _authoritativeCount = null;
                _leftEmptyAtUtc = nowUtc;
                return false;
            }
        }

        var leaveMatch = LeaveRegex.Match(line);
        if (leaveMatch.Success &&
            leaveMatch.Groups[1].Success &&
            int.TryParse(leaveMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leaveId))
        {
            lock (_lock)
                _players.Remove(leaveId);
        }

        return false;
    }

    /// <summary>
    /// Authoritative empty signal from the game's IdleEnter control byte (the game only emits this
    /// once it is up and idle), so it also marks the session ready and the count as a fresh 0.
    /// </summary>
    internal void NotifyIdleEnter() => NotifyIdleEnter(DateTime.UtcNow);

    internal void NotifyIdleEnter(DateTime nowUtc)
    {
        lock (_lock)
        {
            _ready = true;
            _authoritativeCount = 0;
            _authoritativeAtUtc = nowUtc;
            _leftEmptyAtUtc = null; // authoritative empty - no ambiguity to suppress
            _players.Clear();
        }
    }

    /// <summary>
    /// The game left idle mode (a player connected). The count is now non-zero but unknown, so we
    /// INVALIDATE the authoritative reading (forcing a fresh <c>players</c> poll) - this prevents a
    /// stale "0" from the previous IdleEnter driving a mid-round restart.
    /// </summary>
    internal void NotifyIdleExit() => NotifyIdleExit(DateTime.UtcNow);

    internal void NotifyIdleExit(DateTime nowUtc)
    {
        lock (_lock)
        {
            _ready = true;
            _authoritativeCount = null;
            _leftEmptyAtUtc = nowUtc;
        }
    }

    /// <summary>Resets all tracked state. Called on a fresh session/restart.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _players.Clear();
            _authoritativeCount = null;
            _authoritativeAtUtc = default;
            _leftEmptyAtUtc = null;
            _lastPollIssuedAtUtc = null;
            _ready = false;
        }
    }
}
