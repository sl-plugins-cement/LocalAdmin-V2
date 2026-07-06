using System;
using LocalAdmin.V2.Core.Policy;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class PlayerCountTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NotReady_Before_AnySignal()
    {
        var tracker = new PlayerCountTracker();
        Assert.False(tracker.IsReady);
        Assert.False(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out _));
    }

    [Theory]
    [InlineData("List of players (0):", 0)]
    [InlineData("List of players (3):", 3)]
    [InlineData("   List of players (12):", 12)]
    public void ParsesAuthoritativePlayersList(string line, int expected)
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);
        tracker.ProcessConsoleLine(line, T0);

        Assert.True(tracker.IsReady);
        Assert.True(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out var count));
        Assert.Equal(expected, count);
    }

    [Fact]
    public void UnsolicitedListLine_IsIgnored()
    {
        var tracker = new PlayerCountTracker();

        // No "players" poll was issued, so a list-shaped line must not be trusted: console lines
        // can embed player-controlled text.
        tracker.ProcessConsoleLine("List of players (0):", T0);

        Assert.False(tracker.IsReady);
        Assert.False(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void ListReply_IsIgnored_LongAfterPoll()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);

        // A reply landing well past the acceptance window is no longer correlated with our poll.
        tracker.ProcessConsoleLine("List of players (0):", T0.AddSeconds(30));

        Assert.False(tracker.TryGetFreshCount(T0.AddSeconds(30), TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void SpoofedNickname_AuthLine_DoesNotSetCount()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);

        // A player whose NICKNAME is "List of players (0):" produces an auth line that merely
        // starts with the list-reply shape. It must be treated as a join (non-empty, count
        // unknown), never as an authoritative count of 0.
        tracker.ProcessConsoleLine(
            "List of players (0): authenticated from endpoint 1.2.3.4:5678. Player ID assigned: 7.", T0);

        Assert.True(tracker.IsReady); // it IS a real join
        Assert.False(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void FreshCount_Expires_WithAge()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);
        tracker.ProcessConsoleLine("List of players (0):", T0);

        // Within maxAge -> fresh.
        Assert.True(tracker.TryGetFreshCount(T0.AddSeconds(30), TimeSpan.FromSeconds(60), out _));
        // Beyond maxAge -> stale, must not be acted on.
        Assert.False(tracker.TryGetFreshCount(T0.AddSeconds(120), TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void IdleEnter_Is_Authoritative_Zero()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotifyIdleEnter(T0);

        Assert.True(tracker.IsReady);
        Assert.True(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out var count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void IdleExit_Invalidates_StaleZero()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotifyIdleEnter(T0);          // authoritative 0
        tracker.NotifyIdleExit(T0);           // now non-empty but unknown

        // The stale "0" must NOT be usable - this is what prevents a mid-round false restart.
        Assert.True(tracker.IsReady);
        Assert.False(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(600), out _));

        // Once the suppression window has passed, a polled reply restores an authoritative count.
        tracker.NotePlayersPollIssued(T0.AddSeconds(6));
        tracker.ProcessConsoleLine("List of players (2):", T0.AddSeconds(6));
        Assert.True(tracker.TryGetFreshCount(T0.AddSeconds(6), TimeSpan.FromSeconds(60), out var count));
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("List of players (0):")]
    [InlineData("List of players (1):")]
    public void IdleExit_Suppresses_StaleLowReply_Within_Window(string staleReply)
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);
        tracker.NotifyIdleExit(T0);

        // A reply landing 2s after IdleExit may be the stale in-flight answer to a pre-join poll.
        // Not just "0": with a restart threshold of 1, a stale "1" racing a second join would
        // drive the same mid-round false restart - so ANY reply in the window is ignored.
        tracker.ProcessConsoleLine(staleReply, T0.AddSeconds(2));
        Assert.False(tracker.TryGetFreshCount(T0.AddSeconds(2), TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void IdleExit_Accepts_Zero_Reply_After_Window()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);
        tracker.NotifyIdleExit(T0);

        // Past the suppression window a genuine empty reading is accepted (so restart-when-empty
        // still works even when idle mode is disabled / no IdleEnter follows).
        tracker.ProcessConsoleLine("List of players (0):", T0.AddSeconds(6));
        Assert.True(tracker.TryGetFreshCount(T0.AddSeconds(6), TimeSpan.FromSeconds(60), out var count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void JoinLine_Marks_Ready_But_Is_Not_Authoritative()
    {
        var tracker = new PlayerCountTracker();
        tracker.ProcessConsoleLine(
            "Peanut authenticated from endpoint 1.2.3.4:5678. Player ID assigned: 7.", T0);

        Assert.True(tracker.IsReady);
        // Supplementary only: no authoritative reading yet, so the policy must not act.
        Assert.False(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void JoinLine_Invalidates_Stale_Fresh_Zero()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);
        tracker.ProcessConsoleLine("List of players (0):", T0);         // fresh authoritative 0
        Assert.True(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out _));

        // A player joins (no idle byte, e.g. idle mode disabled): the stale 0 must stop being fresh
        // so the watchdog cannot restart a server that just gained a player.
        tracker.ProcessConsoleLine(
            "Peanut authenticated from endpoint 1.2.3.4:5678. Player ID assigned: 5.", T0.AddSeconds(1));
        Assert.False(tracker.TryGetFreshCount(T0.AddSeconds(1), TimeSpan.FromSeconds(60), out _));

        // And a stale reply from the pre-join poll is rejected within the suppression window.
        tracker.NotePlayersPollIssued(T0.AddSeconds(2));
        tracker.ProcessConsoleLine("List of players (0):", T0.AddSeconds(2));
        Assert.False(tracker.TryGetFreshCount(T0.AddSeconds(2), TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void Reset_Clears_State()
    {
        var tracker = new PlayerCountTracker();
        tracker.NotePlayersPollIssued(T0);
        tracker.ProcessConsoleLine("List of players (5):", T0);
        tracker.Reset();

        Assert.False(tracker.IsReady);
        Assert.False(tracker.TryGetFreshCount(T0, TimeSpan.FromSeconds(60), out _));

        // The pre-reset poll must not validate a post-reset (next session's) list line.
        tracker.ProcessConsoleLine("List of players (5):", T0.AddSeconds(1));
        Assert.False(tracker.TryGetFreshCount(T0.AddSeconds(1), TimeSpan.FromSeconds(60), out _));
    }
}
