// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// The small set of host primitives the restart-intent machinery needs from LocalAdmin.
/// Keeping policies behind this interface decouples them from the session/lifecycle god
/// object and makes the manager fully unit-testable with a fake host.
/// </summary>
internal interface IRestartHost
{
    /// <summary>
    /// Claims a graceful restart with a background force fallback (a stalled game-side exit is
    /// forced after ~60s). Returns true iff a Restart was actually claimed as the shutdown
    /// action; false means another shutdown action already owns the exit.
    /// </summary>
    bool TryScheduleGracefulRestart(string reason);

    /// <summary>
    /// Best-effort request for a fresh authoritative player count (sends <c>players</c> to the
    /// game console; the reply is parsed by <see cref="PlayerCountTracker"/>). No-op when the
    /// console connection is not up.
    /// </summary>
    void RequestPlayerCountRefresh();

    /// <summary>Quick (lock-free) check: the session/process is tearing down.</summary>
    bool IsShuttingDown { get; }

    /// <summary>A graceful restart has already been committed and owns the shutdown action.</summary>
    bool RestartInProgress { get; }

    /// <summary>
    /// Re-checks shutdown state under the host's lifecycle lock. Used after publishing an armed
    /// intent, because <see cref="IsShuttingDown"/> can flip between the pre-arm check and the
    /// publish (the arm/teardown race).
    /// </summary>
    bool ConfirmNotShuttingDown();
}
