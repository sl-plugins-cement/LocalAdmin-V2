using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LocalAdmin.V2.Core.Activation;
using LocalAdmin.V2.JSON;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class ActivationRecordStoreTests : IDisposable
{
    private const string Current = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Pending = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "la-activation-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Creates_Reloads_And_Increments_Revision()
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        var created = await store.UpdateAsync(-1, _ => Verified());
        var loaded = await store.LoadAsync();

        Assert.Equal(0, created.Revision);
        Assert.Equal(
            JsonSerializer.Serialize(created, OpsJsonContext.Default.ActivationRecord),
            JsonSerializer.Serialize(loaded, OpsJsonContext.Default.ActivationRecord));
        Assert.DoesNotContain('\r', await File.ReadAllTextAsync(store.RecordPath));
    }

    [Fact]
    public async Task Rejects_Stale_Expected_Revision()
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        await store.UpdateAsync(-1, _ => Verified());

        await Assert.ThrowsAsync<ActivationProtocolException>(
            () => store.UpdateAsync(9, record => record!));
    }

    [Fact]
    public async Task Strictly_Rejects_Unknown_Json_Field()
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        Directory.CreateDirectory(Path.GetDirectoryName(store.RecordPath)!);
        var json = JsonSerializer.Serialize(Verified(), OpsJsonContext.Default.ActivationRecord);
        json = json[..^1] + ",\"surprise\":true}";
        await File.WriteAllTextAsync(store.RecordPath, json);

        await Assert.ThrowsAsync<ActivationProtocolException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task Strictly_Rejects_Missing_Nullable_Field()
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        Directory.CreateDirectory(Path.GetDirectoryName(store.RecordPath)!);
        var json = JsonSerializer.Serialize(Verified(), OpsJsonContext.Default.ActivationRecord)
            .Replace(",\"last_error\":null", string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(store.RecordPath, json);

        await Assert.ThrowsAsync<ActivationProtocolException>(() => store.LoadAsync());
    }

    [Fact]
    public void Rejects_Lock_Identity_Or_Unsafe_Holder()
    {
        var open = Open(Verified(), ActivationStates.Pending);
        Assert.Throws<ActivationProtocolException>(() => ActivationRecordStore.Validate(
            open with { Lock = open.Lock! with { Id = "8fd4c689-3df4-4d6c-b797-888fb35d4b51" } },
            open.Lane));
        Assert.Throws<ActivationProtocolException>(() => ActivationRecordStore.Validate(
            open with { Lock = open.Lock! with { Holder = "spaces are not allowed" } },
            open.Lane));
    }

    [Fact]
    public async Task Finalized_Pending_Manifest_Is_The_Only_Allowed_Same_State_Field_Change()
    {
        var store = new ActivationRecordStore(_root, "example/prod");
        var verified = await store.UpdateAsync(-1, _ => Verified());
        var prepared = await store.UpdateAsync(verified.Revision, _ => Open(verified, ActivationStates.Prepared));
        var pending = await store.UpdateAsync(prepared.Revision, record => record! with { State = ActivationStates.Pending });
        var activating = await store.UpdateAsync(pending.Revision, record => record! with { State = ActivationStates.Activating });
        var verifying = await store.UpdateAsync(activating.Revision, record => record! with
        {
            State = ActivationStates.Verifying,
            CurrentCheckpoint = record.PendingCheckpoint,
            VerificationTarget = "pending"
        });

        var finalized = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var updated = await store.UpdateAsync(verifying.Revision, record => record! with
        {
            PendingCheckpoint = finalized,
            CurrentCheckpoint = finalized,
            Checks = new List<ActivationCheck>(record.Checks)
            {
                Check("pending.generated-config", true)
            }
        });
        Assert.Equal(finalized, updated.PendingCheckpoint);

        await Assert.ThrowsAsync<ActivationProtocolException>(() =>
            store.UpdateAsync(updated.Revision, record => record! with { PreparedAt = "2026-08-26T03:00:00Z" }));
    }

    [Fact]
    public async Task New_Activation_Resets_Checks_Retained_By_The_Verified_Record()
    {
        // A verified record keeps the previous activation's readiness evidence. Preparing the
        // NEXT activation starts a fresh attempt whose checks are empty; append-only immutability
        // applies within one activation, not across activations (proven by the scpsl-ops E2E:
        // the second staged deploy on a lane was refused before this fix).
        var store = new ActivationRecordStore(_root, "example/prod");
        var verified = await store.UpdateAsync(-1, _ => Verified() with
        {
            Checks = new List<ActivationCheck> { Check("pending.players", true) }
        });

        var prepared = await store.UpdateAsync(
            verified.Revision,
            record => Open(record!, ActivationStates.Prepared));

        Assert.Equal(ActivationStates.Prepared, prepared.State);
        Assert.Empty(prepared.Checks);

        // Within the open activation the append-only rule still holds.
        var pending = await store.UpdateAsync(
            prepared.Revision,
            record => record! with
            {
                State = ActivationStates.Pending,
                Checks = new List<ActivationCheck> { Check("pending.udp", true) }
            });
        await Assert.ThrowsAsync<ActivationProtocolException>(() => store.UpdateAsync(
            pending.Revision,
            record => record! with
            {
                State = ActivationStates.Activating,
                Checks = new List<ActivationCheck> { Check("rewritten", false) }
            }));
    }

    [Fact]
    public void Golden_Shapes_Validate()
    {
        var verified = Verified();
        ActivationRecordStore.Validate(verified, verified.Lane);
        ActivationRecordStore.Validate(Open(verified, ActivationStates.Pending), verified.Lane);
    }

    internal static ActivationRecord Verified(string lane = "example/prod") => new()
    {
        ProtocolVersion = 1,
        Revision = 0,
        Lane = lane,
        State = ActivationStates.Verified,
        ActivationId = "58f83dc3-bf19-4a45-89f6-f7138142b4db",
        CurrentCheckpoint = Current,
        PendingCheckpoint = null,
        PreviousCheckpoint = null,
        VerificationTarget = null,
        RollbackAttempted = false,
        Lock = null,
        Restart = null,
        Readiness = Readiness(),
        Checks = new(),
        PreparedAt = "2026-08-26T01:00:00Z",
        UpdatedAt = "2026-08-26T01:02:12Z",
        LastError = null
    };

    internal static ActivationRecord Open(ActivationRecord verified, string state) => verified with
    {
        State = state,
        ActivationId = "b4ed4b95-9ce4-42a3-8388-f372e24b30dd",
        PendingCheckpoint = Pending,
        PreviousCheckpoint = Current,
        Lock = new ActivationLock
        {
            Id = "b4ed4b95-9ce4-42a3-8388-f372e24b30dd",
            Holder = "ssh-operations",
            AcquiredAt = "2026-08-26T02:00:00Z"
        },
        Restart = new ActivationRestart
        {
            Preset = "immediate",
            Edge = "immediate",
            MaxPlayers = null,
            Rounds = null
        },
        Checks = new(),
        PreparedAt = "2026-08-26T02:00:00Z",
        UpdatedAt = "2026-08-26T02:00:04Z"
    };

    internal static ActivationReadiness Readiness() => new()
    {
        TimeoutSeconds = 30,
        RequireHeartbeat = true,
        RequireUdp = true,
        RequireVerification = true
    };

    internal static ActivationCheck Check(string name, bool ok) => new()
    {
        Name = name,
        Ok = ok,
        ObservedAt = "2026-08-26T02:00:05Z",
        Detail = ok ? "ok" : "failed"
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup */ }
    }
}
