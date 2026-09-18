// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Tracks the game's native "restart next round" flag (<c>ServerStatic.StopNextRound</c>). The
/// native <c>rnr</c> command is a TOGGLE: sending it while a staff member already armed it would
/// silently disarm their restart, and a following <c>restart</c> would then only restart the round.
///
/// The game reports every flip through the console control stream (<c>ExitActionRestart</c> when
/// the flag becomes Restart, <c>ExitActionReset</c>/<c>Shutdown</c> otherwise) and through the
/// <c>rnr</c> reply text, so LocalAdmin can know the flag without guessing. A new game process
/// always starts with the flag cleared, so one tracker per session starts from "not armed".
///
/// <see cref="OwnedByLocalAdmin"/> records whether LocalAdmin's own <c>rnr</c> flipped the flag on;
/// only that case may be disarmed again by LocalAdmin, so an operator-armed restart is never
/// cancelled behind their back.
/// </summary>
internal sealed class NativeRestartNextRoundTracker
{
    internal const string RnrCommand = "rnr";
    internal const string RoundRestartCommand = "restart";
    internal const string ArmedReply = "Server WILL restart after next round.";
    internal const string DisarmedReply = "Server WON'T restart after next round.";

    private readonly object _lock = new();
    private bool _armed;
    private bool _owned;

    /// <summary>Last known state of the native flag for the current game session.</summary>
    internal bool Armed
    {
        get
        {
            lock (_lock)
                return _armed;
        }
    }

    /// <summary>True when LocalAdmin's own <c>rnr</c> is what armed the flag.</summary>
    internal bool OwnedByLocalAdmin
    {
        get
        {
            lock (_lock)
                return _armed && _owned;
        }
    }

    /// <summary>Feed from the game's exit-action control bytes (authoritative for the flag).</summary>
    internal void ObserveExitAction(LocalAdmin.ShutdownAction action)
    {
        lock (_lock)
        {
            _armed = action == LocalAdmin.ShutdownAction.Restart;
            if (!_armed)
                _owned = false;
        }
    }

    /// <summary>
    /// A round restarted without the game exiting: the flag was not armed at that edge, whatever
    /// LocalAdmin believed. Clears the belief so the next sequence sends <c>rnr</c> again.
    /// </summary>
    internal void ObserveRoundRestart()
    {
        lock (_lock)
        {
            _armed = false;
            _owned = false;
        }
    }

    /// <summary>Feed from console output: the <c>rnr</c> reply names the resulting state.</summary>
    internal bool ObserveConsoleLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        if (line.Contains(ArmedReply, StringComparison.Ordinal))
        {
            lock (_lock)
                _armed = true;
            return true;
        }
        if (line.Contains(DisarmedReply, StringComparison.Ordinal))
        {
            lock (_lock)
            {
                _armed = false;
                _owned = false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sends <c>rnr</c> only when the flag is not known to be armed. Returns true when the flag is
    /// (or is now believed to be) armed. Ownership is claimed only when LocalAdmin sent the toggle.
    /// </summary>
    internal bool EnsureArmed(Func<string, bool> sendCommand)
    {
        ArgumentNullException.ThrowIfNull(sendCommand);
        lock (_lock)
        {
            if (_armed)
                return true;
            if (!sendCommand(RnrCommand))
                return false;
            _armed = true;
            _owned = true;
            return true;
        }
    }

    /// <summary>
    /// The native full-restart sequence: arm <c>rnr</c> (if not already armed) then <c>restart</c>
    /// the round, which makes the game exit for LocalAdmin to relaunch. If <c>rnr</c> cannot be
    /// sent, <c>restart</c> is withheld because it would only restart the current round.
    /// </summary>
    internal bool TrySendRestartSequence(Func<string, bool> sendCommand)
    {
        ArgumentNullException.ThrowIfNull(sendCommand);
        lock (_lock)
        {
            if (!_armed)
            {
                if (!sendCommand(RnrCommand))
                    return false;
                _armed = true;
                _owned = true;
            }
            return sendCommand(RoundRestartCommand);
        }
    }

    /// <summary>
    /// Toggles the flag off, but only when LocalAdmin armed it. An operator-armed restart stays.
    /// Returns true when a disarm was sent.
    /// </summary>
    internal bool DisarmIfOwned(Func<string, bool> sendCommand)
    {
        ArgumentNullException.ThrowIfNull(sendCommand);
        lock (_lock)
        {
            if (!_armed || !_owned)
                return false;
            if (!sendCommand(RnrCommand))
                return false;
            _armed = false;
            _owned = false;
            return true;
        }
    }

    internal string Describe()
    {
        lock (_lock)
        {
            if (!_armed)
                return "native rnr is not armed";
            return _owned ? "native rnr is armed by LocalAdmin" : "native rnr is armed by an operator";
        }
    }
}
