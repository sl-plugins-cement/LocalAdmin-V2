// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LocalAdmin.V2.JSON;

namespace LocalAdmin.V2.Core.Activation;

internal sealed class ActivationProtocolException : Exception
{
    internal ActivationProtocolException(string message) : base(message) { }
    internal ActivationProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The sole activation-record writer. Callers provide an expected revision; every committed write
/// reloads under the store lock, checks that revision, writes a complete temporary file, flushes it,
/// and atomically replaces the record.
/// </summary>
internal sealed partial class ActivationRecordStore
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _processLock = new(1, 1);

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex LaneRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex("^[A-Za-z0-9_.:@-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex HolderRegex();

    internal ActivationRecordStore(string stateRoot, string lane)
    {
        if (!Path.IsPathFullyQualified(stateRoot))
            throw new ArgumentException("The activation state root must be an absolute path.", nameof(stateRoot));
        if (!LaneRegex().IsMatch(lane))
            throw new ArgumentException("The activation lane is invalid.", nameof(lane));

        Lane = lane;
        var lanePath = Path.Combine(new[] { stateRoot, "lanes" }.Concat(lane.Split('/')).ToArray());
        RecordPath = Path.Combine(lanePath, "activation.json");
        LockPath = Path.Combine(lanePath, "activation.lock");
    }

    internal string Lane { get; }
    internal string RecordPath { get; }
    internal string LockPath { get; }

    internal async Task<ActivationRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processLock.Release();
        }
    }

    /// <summary>
    /// Commits one transition. Use expected revision -1 only to create the first verified record.
    /// </summary>
    internal async Task<ActivationRecord> UpdateAsync(
        long expectedRevision,
        Func<ActivationRecord?, ActivationRecord> update,
        CancellationToken cancellationToken = default)
    {
        await _processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var actualRevision = current?.Revision ?? -1;
            if (actualRevision != expectedRevision)
                throw new ActivationProtocolException(
                    $"Activation record revision changed: expected {expectedRevision}, found {actualRevision}.");

            var candidate = update(current) with
            {
                ProtocolVersion = ProtocolVersion,
                Revision = checked(actualRevision + 1),
                Lane = Lane,
                UpdatedAt = FormatTimestamp(DateTime.UtcNow)
            };

            Validate(candidate, Lane);
            if (current != null)
                ValidateTransition(current, candidate);

            await WriteAtomicAsync(candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        finally
        {
            _processLock.Release();
        }
    }

    internal static string FormatTimestamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static void Validate(ActivationRecord record, string expectedLane)
    {
        if (record.ProtocolVersion != ProtocolVersion)
            Fail($"Unsupported protocol_version {record.ProtocolVersion}.");
        if (record.Revision < 0)
            Fail("revision must be nonnegative.");
        if (!string.Equals(record.Lane, expectedLane, StringComparison.Ordinal) || !LaneRegex().IsMatch(record.Lane))
            Fail("lane is invalid or differs from the configured lane.");
        if (!ActivationStates.IsKnown(record.State))
            Fail($"Unknown activation state '{record.State}'.");
        ValidateUuid(record.ActivationId, "activation_id");
        ValidateCheckpoint(record.CurrentCheckpoint, "current_checkpoint");
        ValidateCheckpoint(record.PendingCheckpoint, "pending_checkpoint");
        ValidateCheckpoint(record.PreviousCheckpoint, "previous_checkpoint");
        if (record.VerificationTarget is not (null or "pending" or "previous"))
            Fail("verification_target must be pending, previous, or null.");
        if (record.Readiness == null || record.Readiness.TimeoutSeconds <= 0)
            Fail("readiness.timeout_seconds must be positive.");
        if (record.Checks == null)
            Fail("checks is required.");
        ValidateTimestamp(record.PreparedAt, "prepared_at");
        ValidateTimestamp(record.UpdatedAt, "updated_at");

        if (record.Lock != null)
        {
            ValidateUuid(record.Lock.Id, "lock.id");
            if (record.Lock.Id != record.ActivationId)
                Fail("lock.id must equal activation_id.");
            if (!HolderRegex().IsMatch(record.Lock.Holder))
                Fail("lock.holder must match [A-Za-z0-9_.:@-]{1,128}.");
            ValidateTimestamp(record.Lock.AcquiredAt, "lock.acquired_at");
        }

        if (record.Restart != null)
        {
            if (string.IsNullOrWhiteSpace(record.Restart.Preset))
                Fail("restart.preset must not be empty.");
            if (record.Restart.Edge is not ("immediate" or "round-end" or "empty"))
                Fail("restart.edge is invalid.");
            if (record.Restart.MaxPlayers < 0)
                Fail("restart.max_players cannot be negative.");
            if (record.Restart.Rounds <= 0)
                Fail("restart.rounds must be positive when present.");
        }

        foreach (var check in record.Checks)
        {
            if (check == null || string.IsNullOrWhiteSpace(check.Name))
                Fail("checks[].name must not be empty.");
            ValidateTimestamp(check.ObservedAt, "checks[].observed_at");
            if (check.Detail == null)
                Fail("checks[].detail is required.");
        }

        var openFieldsPresent = record.CurrentCheckpoint != null && record.PendingCheckpoint != null &&
                                record.PreviousCheckpoint != null && record.Lock != null && record.Restart != null;

        switch (record.State)
        {
            case ActivationStates.Verified:
                if (record.CurrentCheckpoint == null || record.PendingCheckpoint != null ||
                    record.PreviousCheckpoint != null || record.VerificationTarget != null ||
                    record.Lock != null || record.Restart != null)
                    Fail("verified state has inconsistent open-activation fields.");
                break;

            case ActivationStates.Prepared:
            case ActivationStates.Pending:
            case ActivationStates.Activating:
            case ActivationStates.Cancelled:
                if (!openFieldsPresent || record.VerificationTarget != null || record.RollbackAttempted)
                    Fail($"{record.State} state has inconsistent fields.");
                if (record.CurrentCheckpoint != record.PreviousCheckpoint)
                    Fail("previous_checkpoint must equal the checkpoint current at preparation.");
                break;

            case ActivationStates.Verifying:
                if (!openFieldsPresent || record.VerificationTarget != "pending" || record.RollbackAttempted)
                    Fail("verifying state has inconsistent fields.");
                if (record.CurrentCheckpoint != record.PendingCheckpoint)
                    Fail("verifying state must identify the installed pending checkpoint as current.");
                break;

            case ActivationStates.RollingBack:
                if (!openFieldsPresent || record.VerificationTarget != "previous" || !record.RollbackAttempted)
                    Fail("rolling-back state has inconsistent fields.");
                if (record.CurrentCheckpoint != record.PendingCheckpoint &&
                    record.CurrentCheckpoint != record.PreviousCheckpoint)
                    Fail("rolling-back current checkpoint must be the candidate or restored previous checkpoint.");
                break;

            case ActivationStates.RolledBack:
                if (!openFieldsPresent || record.CurrentCheckpoint != record.PreviousCheckpoint ||
                    record.VerificationTarget != "previous" || !record.RollbackAttempted)
                    Fail("rolled-back state has inconsistent fields.");
                break;

            case ActivationStates.RecoveryRequired:
                if (!openFieldsPresent || record.VerificationTarget != "previous" ||
                    !record.RollbackAttempted || string.IsNullOrWhiteSpace(record.LastError))
                    Fail("recovery-required state has inconsistent fields.");
                if (record.CurrentCheckpoint != record.PendingCheckpoint &&
                    record.CurrentCheckpoint != record.PreviousCheckpoint)
                    Fail("recovery-required current checkpoint must retain the last durably known installed target.");
                break;
        }
    }

    private static void ValidateTransition(ActivationRecord before, ActivationRecord after)
    {
        if (!string.Equals(before.ActivationId, after.ActivationId, StringComparison.Ordinal) &&
            before.State != ActivationStates.Verified)
            Fail("activation_id cannot change while an activation is open.");

        // Checks are immutable evidence WITHIN one activation (protocol: "Existing check entries
        // are immutable evidence within an activation"). verified -> prepared begins a NEW
        // activation attempt, which starts with empty checks; the previous activation's evidence
        // lives on in its committed revisions.
        var startsNewActivation = before.State == ActivationStates.Verified &&
                                  after.State == ActivationStates.Prepared;
        if (startsNewActivation)
        {
            if (after.Checks.Count != 0)
                Fail("A new activation attempt must start with empty readiness checks.");
        }
        else if (after.Checks.Count < before.Checks.Count ||
            !before.Checks.SequenceEqual(after.Checks.Take(before.Checks.Count)))
        {
            Fail("Existing readiness checks are immutable and may only be appended.");
        }

        if (before.State == after.State)
        {
            var finalizesPending = before.State == ActivationStates.Verifying &&
                                   before.VerificationTarget == "pending" &&
                                   after.CurrentCheckpoint == after.PendingCheckpoint;
            var recordsRestoredRollback = before.State == ActivationStates.RollingBack &&
                                          before.CurrentCheckpoint == before.PendingCheckpoint &&
                                          after.CurrentCheckpoint == before.PreviousCheckpoint;
            var normalizedAfter = after with
            {
                Revision = before.Revision,
                UpdatedAt = before.UpdatedAt,
                Checks = before.Checks,
                PendingCheckpoint = finalizesPending
                    ? before.PendingCheckpoint
                    : after.PendingCheckpoint,
                CurrentCheckpoint = finalizesPending || recordsRestoredRollback
                    ? before.CurrentCheckpoint
                    : after.CurrentCheckpoint
            };
            if (normalizedAfter != before)
                Fail("A same-state revision may only append checks, finalize the pending manifest, or record an applied rollback.");
            return;
        }

        var allowed = before.State switch
        {
            ActivationStates.Verified => after.State == ActivationStates.Prepared,
            ActivationStates.Prepared => after.State is ActivationStates.Pending or ActivationStates.Cancelled,
            ActivationStates.Pending => after.State is ActivationStates.Activating or ActivationStates.Cancelled,
            ActivationStates.Activating => after.State is ActivationStates.Verifying or ActivationStates.RollingBack,
            ActivationStates.Verifying => after.State is ActivationStates.Verified or ActivationStates.RollingBack,
            ActivationStates.RollingBack => after.State is ActivationStates.RolledBack or ActivationStates.RecoveryRequired,
            ActivationStates.RolledBack => after.State == ActivationStates.Verified,
            ActivationStates.Cancelled => after.State == ActivationStates.Verified,
            ActivationStates.RecoveryRequired => after.State == ActivationStates.Verified,
            _ => false
        };

        if (!allowed)
            Fail($"Illegal activation transition {before.State} -> {after.State}.");

        if (before.State != ActivationStates.Verified &&
            (before.PreparedAt != after.PreparedAt || before.Readiness != after.Readiness))
            Fail("Captured activation identity and policy are immutable while an activation is open.");
        if (before.State != ActivationStates.Verified && after.State != ActivationStates.Verified &&
            (before.Lock != after.Lock || before.Restart != after.Restart ||
             before.PreviousCheckpoint != after.PreviousCheckpoint))
            Fail("The lane lock, restart policy, and rollback target are immutable while an activation is open.");

        if (after.State == ActivationStates.Verifying && after.CurrentCheckpoint != after.PendingCheckpoint)
            Fail("activating -> verifying must record the installed pending checkpoint as current.");
        if (after.State == ActivationStates.RolledBack && after.CurrentCheckpoint != after.PreviousCheckpoint)
            Fail("rolling-back -> rolled-back must retain the restored previous checkpoint as current.");
    }

    private async Task<ActivationRecord?> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RecordPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(RecordPath, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize(json, OpsJsonContext.Default.ActivationRecord)
                         ?? throw new ActivationProtocolException("Activation record is JSON null.");
            Validate(record, Lane);
            return record;
        }
        catch (ActivationProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ActivationProtocolException($"Cannot read activation record '{RecordPath}': {ex.Message}", ex);
        }
    }

    private async Task WriteAtomicAsync(ActivationRecord record, CancellationToken cancellationToken)
    {
        var tempPath = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, OpsJsonContext.Default.ActivationRecord);
            await using (var stream = new FileStream(
                             tempPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                             }))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, RecordPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch { /* stale temp is never authoritative */ }
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateCheckpoint(string? value, string name)
    {
        if (value != null && !CheckpointRegex().IsMatch(value))
            Fail($"{name} must be a lowercase SHA-256 string or null.");
    }

    private static void ValidateUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed.ToString("D") != value)
            Fail($"{name} must be a canonical lowercase UUID string.");
    }

    private static void ValidateTimestamp(string value, string name)
    {
        if (!TimestampRegex().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            Fail($"{name} must be an RFC 3339 UTC timestamp ending in Z.");
    }

    [DoesNotReturn]
    private static void Fail(string message) => throw new ActivationProtocolException(message);
}
