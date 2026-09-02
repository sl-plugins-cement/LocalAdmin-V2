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
        internal TaskCompletionSource<PlayerProbeResult> Completion { get; }
    }

    private readonly object _lock = new();
    private readonly PlayerCountTracker _tracker;
    private readonly Func<bool> _sendPlayers;
    private PendingProbe? _pending;

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
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        PendingProbe probe;
        var send = false;
        lock (_lock)
        {
            if (_pending == null)
            {
                probe = new PendingProbe(nowUtc);
                _pending = probe;
                send = true;
            }
            else
            {
                probe = _pending;
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
