// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Globalization;

namespace LocalAdmin.V2.Core.Policy;

/// <summary>
/// Recognizes the game's reply to the <c>players</c> command so a quiet probe can keep it off the
/// console and log. The shape is strict (header, then only <c>- name: userid [id]</c> entries) so
/// unrelated output or player-controlled text can never be mistaken for it.
/// </summary>
internal static class PlayersReplyShape
{
    private const string EntryPrefix = "- ";

    /// <summary>
    /// A whole reply in one message: the <c>List of players (N):</c> header, optionally followed
    /// by newline-separated entry lines. <paramref name="header"/> is the header line alone, the
    /// only part the player tracker needs.
    /// </summary>
    internal static bool TryParseReply(string? message, out string header, out int count, out bool hasEntries)
    {
        header = string.Empty;
        count = 0;
        hasEntries = false;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lines = message.Trim().Split('\n');
        var headerLine = lines[0].TrimEnd('\r');
        var match = PlayerCountTracker.PlayersListRegex.Match(headerLine);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
        {
            count = 0;
            return false;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var entry = lines[i].Trim();
            if (entry.Length == 0)
                continue;

            if (!entry.StartsWith(EntryPrefix, StringComparison.Ordinal))
            {
                count = 0;
                return false;
            }

            hasEntries = true;
        }

        header = headerLine.Trim();
        return true;
    }

    /// <summary>
    /// A message made only of entry lines: how the game sends the player list when it splits the
    /// reply into a header message and a following entries message. A blank message counts as
    /// entries-only with <paramref name="blank"/> set.
    /// </summary>
    internal static bool IsEntriesOnly(string? message, out bool blank)
    {
        blank = string.IsNullOrWhiteSpace(message);
        if (blank)
            return true;

        foreach (var raw in message!.Split('\n'))
        {
            var entry = raw.Trim();
            if (entry.Length != 0 && !entry.StartsWith(EntryPrefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
