// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAdmin.V2.Core.Policy;

internal sealed record PlayerProbeResult(
    Guid ProbeId,
    int Players,
    DateTime IssuedAtUtc,
    DateTime RespondedAtUtc);

/// <summary>
/// Serializes LocalAdmin-originated <c>players</c> polls. SCP:SL does not echo a caller token, so
/// allowing two polls in flight would make either response impossible to attribute. Concurrent
/// callers therefore share one poll and one completion task.
///
/// A probe may be <i>quiet</i>: the game's reply to it is kept off the console and log (see
/// <see cref="TrySuppressQuietReply"/>). The quiet flag belongs to the shared poll, so it only
/// applies while every caller waiting on that poll asked for it; a non-quiet caller joining the
/// poll makes the reply print as usual.
/// </summary>
internal sealed class PlayerProbeCoordinator
{
    private sealed class PendingProbe
    {
        internal PendingProbe(DateTime issuedAtUtc)
        {
            Id = Guid.NewGuid();
            IssuedAtUtc = issuedAtUtc;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal Guid Id { get; }
        internal DateTime IssuedAtUtc { get; }
        internal bool Quiet { get; set; }
        internal TaskCompletionSource<PlayerProbeResult> Completion { get; }
    }

    // The game sends a reply with players as a header message followed by a separate entries
    // message, back to back; this bounds how long after the header the entries are still treated
    // as part of the quiet reply.
    private static readonly TimeSpan EntriesContinuationWindow = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly PlayerCountTracker _tracker;
    private readonly Func<bool> _sendPlayers;
    private PendingProbe? _pending;
    private DateTime? _entriesExpectedUntilUtc;

    internal PlayerProbeCoordinator(PlayerCountTracker tracker, Func<bool> sendPlayers)
    {
        _tracker = tracker;
        _sendPlayers = sendPlayers;
    }

    internal Task<PlayerProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => ProbeAsync(timeout, DateTime.UtcNow, cancellationToken);

    internal Task<PlayerProbeResult> ProbeAsync(
        TimeSpan timeout,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
        => ProbeCoreAsync(timeout, nowUtc, quiet: false, cancellationToken);

    /// <summary>
    /// As <see cref="ProbeAsync(TimeSpan, CancellationToken)"/>, but the game's reply to the poll
    /// is suppressed from the console and log (it still reaches the tracker).
    /// </summary>
    internal Task<PlayerProbeResult> ProbeQuietAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => ProbeQuietAsync(timeout, DateTime.UtcNow, cancellationToken);

    internal Task<PlayerProbeResult> ProbeQuietAsync(
        TimeSpan timeout,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
        => ProbeCoreAsync(timeout, nowUtc, quiet: true, cancellationToken);

    private Task<PlayerProbeResult> ProbeCoreAsync(
        TimeSpan timeout,
        DateTime nowUtc,
        bool quiet,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        PendingProbe probe;
        var send = false;
        lock (_lock)
        {
            if (_pending == null)
            {
                probe = new PendingProbe(nowUtc) { Quiet = quiet };
                _pending = probe;
                send = true;
            }
            else
            {
                probe = _pending;
                // A caller that wants to see the reply keeps it visible for the shared poll.
                if (!quiet)
                    probe.Quiet = false;
            }
        }

        if (send)
        {
            _tracker.NotePlayersPollIssued(nowUtc);
            if (!_sendPlayers())
            {
                CompleteException(probe, new InvalidOperationException("The game console is not connected."));
            }
        }

        return AwaitWithTimeoutAsync(probe, timeout, cancellationToken);
    }

    /// <summary>
    /// Decides whether a game console message is the reply to a quiet probe and so must not be
    /// printed or logged. Only the reply to the pending quiet poll qualifies: the whole message
    /// when it holds the header and the entries together, or the header and then its separate
    /// entries message. Anything else, including every reply to a non-quiet poll, returns false.
    /// When it returns true, <paramref name="trackerLine"/> is the line to feed the player
    /// tracker (the header), or null when there is nothing for the tracker.
    /// </summary>
    internal bool TrySuppressQuietReply(string message, DateTime nowUtc, out string? trackerLine)
    {
        trackerLine = null;
        lock (_lock)
        {
            if (_pending is { Quiet: true } &&
                PlayersReplyShape.TryParseReply(message, out var header, out var count, out var hasEntries))
            {
                trackerLine = header;
                _entriesExpectedUntilUtc = !hasEntries && count > 0 ? nowUtc + EntriesContinuationWindow : null;
                return true;
            }

            if (_entriesExpectedUntilUtc is { } until)
            {
                if (nowUtc <= until && PlayersReplyShape.IsEntriesOnly(message, out var blank))
                {
                    if (!blank)
                        _entriesExpectedUntilUtc = null;
                    return true;
                }

                _entriesExpectedUntilUtc = null;
            }

            return false;
        }
    }

    internal void AcceptReply(int players, DateTime respondedAtUtc)
    {
        PendingProbe? probe;
        lock (_lock)
        {
            probe = _pending;
            if (probe != null)
                _pending = null;
        }

        probe?.Completion.TrySetResult(new PlayerProbeResult(
            probe.Id,
            players,
            probe.IssuedAtUtc,
            respondedAtUtc));
    }

    internal void ResetSession()
    {
        PendingProbe? probe;
        lock (_lock)
        {
            probe = _pending;
            _pending = null;
            _entriesExpectedUntilUtc = null;
        }

        probe?.Completion.TrySetException(new InvalidOperationException("The game session ended before the players reply arrived."));
    }

    private async Task<PlayerProbeResult> AwaitWithTimeoutAsync(
        PendingProbe probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await probe.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CompleteException(probe, new TimeoutException("Timed out waiting for a correlated players reply."));
            throw;
        }
    }

    private void CompleteException(PendingProbe probe, Exception error)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pending, probe))
                _pending = null;
        }

        probe.Completion.TrySetException(error);
    }
}
