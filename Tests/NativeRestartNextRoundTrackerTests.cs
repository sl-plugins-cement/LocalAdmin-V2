using System.Collections.Generic;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class NativeRestartNextRoundTrackerTests
{
    private static (NativeRestartNextRoundTracker Tracker, List<string> Sent) Create(bool accept = true)
    {
        var sent = new List<string>();
        var tracker = new NativeRestartNextRoundTracker();
        return (tracker, sent);
    }

    private static System.Func<string, bool> Sender(List<string> sent, bool accept = true) => command =>
    {
        sent.Add(command);
        return accept;
    };

    [Fact]
    public void Fresh_session_sends_rnr_then_restart()
    {
        var (tracker, sent) = Create();

        Assert.True(tracker.TrySendRestartSequence(Sender(sent)));
        Assert.Equal(new[] { "rnr", "restart" }, sent);
        Assert.True(tracker.Armed);
        Assert.True(tracker.OwnedByLocalAdmin);
    }

    [Fact]
    public void Operator_armed_rnr_is_not_toggled_off_by_the_restart_sequence()
    {
        var (tracker, sent) = Create();
        tracker.ObserveExitAction(LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Restart);

        Assert.True(tracker.TrySendRestartSequence(Sender(sent)));
        Assert.Equal(new[] { "restart" }, sent);
        Assert.False(tracker.OwnedByLocalAdmin);
    }

    [Fact]
    public void Ensure_armed_sends_rnr_once_and_is_idempotent()
    {
        var (tracker, sent) = Create();

        Assert.True(tracker.EnsureArmed(Sender(sent)));
        Assert.True(tracker.EnsureArmed(Sender(sent)));
        Assert.Equal(new[] { "rnr" }, sent);
    }

    [Fact]
    public void Restart_is_withheld_when_rnr_cannot_be_sent()
    {
        var (tracker, sent) = Create();

        Assert.False(tracker.TrySendRestartSequence(Sender(sent, accept: false)));
        Assert.Equal(new[] { "rnr" }, sent);
        Assert.False(tracker.Armed);
    }

    [Fact]
    public void Reset_control_byte_clears_arming_and_ownership()
    {
        var (tracker, sent) = Create();
        tracker.EnsureArmed(Sender(sent));

        tracker.ObserveExitAction(LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Crash);

        Assert.False(tracker.Armed);
        Assert.False(tracker.OwnedByLocalAdmin);
        Assert.True(tracker.TrySendRestartSequence(Sender(sent)));
        Assert.Equal(new[] { "rnr", "rnr", "restart" }, sent);
    }

    [Fact]
    public void Round_restart_without_exit_proves_the_flag_was_not_armed()
    {
        var (tracker, sent) = Create();
        tracker.EnsureArmed(Sender(sent));

        tracker.ObserveRoundRestart();

        Assert.False(tracker.Armed);
    }

    [Theory]
    [InlineData("Server WILL restart after next round.", true)]
    [InlineData("[2026-09-19 00:00:00] Server WON'T restart after next round.", false)]
    public void Rnr_reply_text_updates_the_belief(string line, bool armed)
    {
        var tracker = new NativeRestartNextRoundTracker();
        tracker.ObserveExitAction(armed
            ? LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Crash
            : LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Restart);

        Assert.True(tracker.ObserveConsoleLine(line));
        Assert.Equal(armed, tracker.Armed);
        Assert.False(tracker.ObserveConsoleLine("unrelated output"));
    }

    [Fact]
    public void Disarm_only_toggles_when_LocalAdmin_armed_it()
    {
        var (tracker, sent) = Create();
        tracker.ObserveExitAction(LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Restart);
        Assert.False(tracker.DisarmIfOwned(Sender(sent)));
        Assert.Empty(sent);

        tracker.ObserveExitAction(LocalAdmin.V2.Core.LocalAdmin.ShutdownAction.Crash);
        tracker.EnsureArmed(Sender(sent));
        Assert.True(tracker.DisarmIfOwned(Sender(sent)));
        Assert.Equal(new[] { "rnr", "rnr" }, sent);
        Assert.False(tracker.Armed);
    }
}
