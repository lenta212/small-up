#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMDeepCryoPersistenceTest
{
    [Test]
    public async Task SqliteMigrationDownAndReapplyAvoidsPartialProfileRebuild()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new SqliteServerDbContext(options);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        Assert.That(await CountSqliteSchemaObjectsAsync(connection, "table", "luam_deep_cryo_snapshot"), Is.EqualTo(1));
        Assert.That(await CountProfileColumnsAsync(connection, "lifecycle_revision"), Is.EqualTo(1));

        await migrator.MigrateAsync("20260714142229_LuaMAtomicMonoCoinsTransfers");
        Assert.That(await CountSqliteSchemaObjectsAsync(connection, "table", "luam_deep_cryo_snapshot"), Is.Zero);
        Assert.That(await CountProfileColumnsAsync(connection, "lifecycle_revision"), Is.Zero);

        await migrator.MigrateAsync();
        Assert.That(await CountSqliteSchemaObjectsAsync(connection, "table", "luam_deep_cryo_snapshot"), Is.EqualTo(1));
        Assert.That(await CountProfileColumnsAsync(connection, "lifecycle_revision"), Is.EqualTo(1));
        Assert.That(await CountSqliteSchemaObjectsAsync(
            connection,
            "trigger",
            "TR_luam_cryo_operation_no_update"), Is.EqualTo(1));
    }

    [Test]
    public async Task LifecycleReplayQuarantineAndImmutableEvidenceAreFailClosed()
    {
        await using var connection = await OpenSqliteAsync();
        var db = NewServerDb(() => new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Lifecycle"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;

        var storeRequest = NewStoreRequest(userId, profileId, DateTime.UtcNow);
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(storeRequest);
        var storeReplay = await db.StoreLuaMDeepCryoSnapshotAsync(storeRequest);
        var storeIdentityConflict = await db.StoreLuaMDeepCryoSnapshotAsync(storeRequest with
        {
            SourceBuildVersion = "test-build-conflict",
        });
        var loaded = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(stored.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(stored.Revision, Is.Zero);
            Assert.That(storeReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(storeReplay.SnapshotId, Is.EqualTo(stored.SnapshotId));
            Assert.That(storeIdentityConflict.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.IdentityConflict));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Payload, Is.EqualTo(storeRequest.Payload));
        });
        Assert.That(await db.ValidateLuaMDeepCryoProfileAsync(userId, profileId, 0), Is.True);
        Assert.That(await db.ValidateLuaMDeepCryoProfileAsync(new NetUserId(Guid.NewGuid()), profileId, 0), Is.False);
        Assert.That(await db.ValidateLuaMDeepCryoProfileAsync(userId, profileId, 1), Is.False);

        var firstClaim = NewClaimRequest(userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(firstClaim);
        var claimReplayBeforeTransition = await db.ClaimLuaMDeepCryoRestoreAsync(firstClaim);
        var abortedAt = DateTime.UtcNow;
        var abortRequest = new LuaMDeepCryoAbortRequest(
            Guid.NewGuid(), userId, profileId, 0, stored.SnapshotId.Value, 1,
            firstClaim.LeaseId, "runtime restore rolled back", abortedAt);
        var aborted = await db.AbortLuaMDeepCryoRestoreAsync(abortRequest);
        var abortReplay = await db.AbortLuaMDeepCryoRestoreAsync(abortRequest);
        var oldClaimReplay = await db.ClaimLuaMDeepCryoRestoreAsync(firstClaim);

        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(claimed.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Restoring));
            Assert.That(claimed.Revision, Is.EqualTo(1));
            Assert.That(claimReplayBeforeTransition.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(claimReplayBeforeTransition.Snapshot, Is.Not.Null);
            Assert.That(aborted.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(aborted.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(aborted.Revision, Is.EqualTo(2));
            Assert.That(abortReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(abortReplay.Snapshot, Is.Not.Null,
                "An exact transition replay should include the still-current result snapshot.");
            Assert.That(abortReplay.Snapshot!.LeaseId, Is.Null);
            Assert.That(oldClaimReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(oldClaimReplay.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Restoring));
            Assert.That(oldClaimReplay.Revision, Is.EqualTo(1));
            Assert.That(oldClaimReplay.LeaseId, Is.EqualTo(firstClaim.LeaseId));
            Assert.That(oldClaimReplay.Snapshot, Is.Null,
                "A historical replay must not present a later current snapshot as the old claim result.");
        });

        var secondClaim = NewClaimRequest(userId, profileId, stored.SnapshotId.Value, 2);
        var claimedAgain = await db.ClaimLuaMDeepCryoRestoreAsync(secondClaim);
        var completed = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            Guid.NewGuid(), userId, profileId, 0, stored.SnapshotId.Value, 3,
            secondClaim.LeaseId, DateTime.UtcNow));

        Assert.Multiple(() =>
        {
            Assert.That(claimedAgain.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(claimedAgain.Revision, Is.EqualTo(3));
            Assert.That(completed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(completed.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(completed.Revision, Is.EqualTo(4));
        });
        Assert.That(await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0), Is.Null);

        var discardedEpisode = await db.StoreLuaMDeepCryoSnapshotAsync(
            NewStoreRequest(userId, profileId, DateTime.UtcNow));
        var discarded = await db.DiscardLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoDiscardRequest(
            Guid.NewGuid(), userId, profileId, 0, discardedEpisode.SnapshotId!.Value, 0,
            null, DateTime.UtcNow));
        Assert.Multiple(() =>
        {
            Assert.That(discardedEpisode.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(discarded.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(discarded.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
        });

        var quarantinedEpisode = await db.StoreLuaMDeepCryoSnapshotAsync(
            NewStoreRequest(userId, profileId, DateTime.UtcNow));
        var quarantined = await db.QuarantineLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoQuarantineRequest(
            Guid.NewGuid(), userId, profileId, 0, quarantinedEpisode.SnapshotId!.Value, 0,
            null, "prototype manifest mismatch", DateTime.UtcNow));
        var quarantineDiscard = await db.DiscardLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoDiscardRequest(
            Guid.NewGuid(), userId, profileId, 0, quarantinedEpisode.SnapshotId.Value, 1,
            null, DateTime.UtcNow));
        var storePastQuarantine = await db.StoreLuaMDeepCryoSnapshotAsync(
            NewStoreRequest(userId, profileId, DateTime.UtcNow));

        Assert.Multiple(() =>
        {
            Assert.That(quarantined.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(quarantined.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(quarantineDiscard.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Quarantined));
            Assert.That(storePastQuarantine.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Quarantined));
        });
        Assert.That(
            async () => await db.SaveCharacterSlotAsync(userId, null, 0),
            Throws.TypeOf<InvalidOperationException>(),
            "Archiving cannot bypass a quarantined durable identity.");

        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new SqliteServerDbContext(options);
        await context.Database.MigrateAsync();
        var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
        Assert.That(career.Status, Is.EqualTo(DbLuaMCharacterCareerStatus.Quarantined));
        Assert.That(await context.LuaMCharacterPresenceLeases.AnyAsync(value => value.ProfileId == profileId), Is.False);

        var journalMutation = Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE luam_deep_cryo_operation SET reason = 'tamper' WHERE operation_id = {0}",
                storeRequest.OperationId));
        var payloadMutation = Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE luam_deep_cryo_snapshot SET payload = X'00' WHERE lua_m_deep_cryo_snapshots_id = {0}",
                stored.SnapshotId.Value));
        var snapshotDeletion = Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM luam_deep_cryo_snapshot WHERE lua_m_deep_cryo_snapshots_id = {0}",
                stored.SnapshotId.Value));
        var negativeProfileRevision = Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE profile SET lifecycle_revision = -1 WHERE profile_id = {0}",
                profileId));

        Assert.Multiple(() =>
        {
            Assert.That(journalMutation!.Message, Does.Contain("append-only"));
            Assert.That(payloadMutation!.Message, Does.Contain("immutable"));
            Assert.That(snapshotDeletion!.Message, Does.Contain("cannot be deleted"));
            Assert.That(negativeProfileRevision!.Message, Does.Contain("cannot be negative"));
        });
    }

    [Test]
    public async Task ExpiredLeaseRejectsCompletionAndRecoversExactlyOnce()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Lease"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            NewStoreRequest(userId, profileId, DateTime.UtcNow));
        var claim = NewClaimRequest(userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        var afterExpiry = claim.LeaseExpiresAtUtc.AddSeconds(1);

        var lateComplete = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            Guid.NewGuid(), userId, profileId, 0, stored.SnapshotId.Value, 1,
            claim.LeaseId, afterExpiry));
        var recovered = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(afterExpiry);
        var recoveredAgain = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(afterExpiry);
        var loaded = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        var historicalClaim = await db.ClaimLuaMDeepCryoRestoreAsync(claim);

        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(lateComplete.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LeaseConflict));
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(recoveredAgain, Is.Zero, "Recovery replay is not a newly recovered lease.");
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(loaded.Revision, Is.EqualTo(2));
            Assert.That(loaded.LeaseId, Is.Null);
            Assert.That(historicalClaim.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(historicalClaim.Snapshot, Is.Null);
        });

        await using var context = new SqliteServerDbContext(options);
        var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
        Assert.That(career.Status, Is.EqualTo(DbLuaMCharacterCareerStatus.CryoStored));
        Assert.That(await context.LuaMDeepCryoOperations.CountAsync(value =>
            value.Kind == DbLuaMDeepCryoOperationKind.RecoverExpiredLease), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentClaimsProduceOneDurablePresenceLease()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"luam-cryo-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var db = NewServerDb(() => options, inMemory: false);
            var userId = new NetUserId(Guid.NewGuid());
            await db.InitPrefsAsync(userId, NewProfile("Cryo Claim Race"));
            var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
            var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
                NewStoreRequest(userId, profileId, DateTime.UtcNow));

            var first = NewClaimRequest(userId, profileId, stored.SnapshotId!.Value, 0);
            var second = NewClaimRequest(userId, profileId, stored.SnapshotId.Value, 0);
            var results = await Task.WhenAll(
                db.ClaimLuaMDeepCryoRestoreAsync(first),
                db.ClaimLuaMDeepCryoRestoreAsync(second));

            Assert.That(results.Count(value => value.Status == LuaMDeepCryoWriteStatus.Success), Is.EqualTo(1));
            Assert.That(results.Single(value => value.Status != LuaMDeepCryoWriteStatus.Success).Status,
                Is.AnyOf(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    LuaMDeepCryoWriteStatus.InvalidState,
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    LuaMDeepCryoWriteStatus.UnknownOutcome));

            await using var context = new SqliteServerDbContext(options);
            var lease = await context.LuaMCharacterPresenceLeases.SingleAsync();
            Assert.That(lease.LeaseId, Is.EqualTo(results.Single(value =>
                value.Status == LuaMDeepCryoWriteStatus.Success).LeaseId));
            Assert.That(await context.LuaMDeepCryoOperations.CountAsync(value =>
                value.Kind == DbLuaMDeepCryoOperationKind.ClaimRestore), Is.EqualTo(1));
            Assert.That((await context.LuaMDeepCryoSnapshots.SingleAsync()).Status,
                Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Restoring));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    private static LuaMDeepCryoStoreRequest NewStoreRequest(
        NetUserId userId,
        int profileId,
        DateTime storedAtUtc)
    {
        var payload = new byte[] { 0x4C, 0x75, 0x61, 0x4D, 0x01, 0x02 };
        return new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(),
            userId,
            profileId,
            0,
            100,
            1,
            payload,
            Convert.ToHexString(SHA256.HashData(payload)),
            payload.Length,
            2,
            "test-build",
            new string('A', 64),
            storedAtUtc);
    }

    private static LuaMDeepCryoClaimRequest NewClaimRequest(
        NetUserId userId,
        int profileId,
        long snapshotId,
        long expectedRevision)
    {
        var claimedAt = DateTime.UtcNow;
        return new LuaMDeepCryoClaimRequest(
            Guid.NewGuid(),
            userId,
            profileId,
            0,
            snapshotId,
            expectedRevision,
            101,
            Guid.NewGuid(),
            "integration-test",
            claimedAt,
            claimedAt.AddMinutes(5));
    }

    private static async Task<SqliteConnection> OpenSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> CountSqliteSchemaObjectsAsync(
        SqliteConnection connection,
        string type,
        string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = $type AND name = $name";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return (long) (await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountProfileColumnsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('profile') WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long) (await command.ExecuteScalarAsync())!;
    }

    private static ServerDbSqlite NewServerDb(
        Func<DbContextOptions<SqliteServerDbContext>> options,
        bool inMemory)
    {
        var logManager = new LogManager();
        IConfigurationManager configuration = MockInterfaces.MakeConfigurationManager(
            new Mock<IGameTiming>().Object,
            logManager,
            loadCvarsFromTypes: [typeof(CCVars)]);
        return new ServerDbSqlite(options, inMemory, configuration, false, new DummySawmill());
    }

    private static HumanoidCharacterProfile NewProfile(string name)
        => new()
        {
            Name = name,
            FlavorText = string.Empty,
            Species = "Human",
            Age = 30,
            Appearance = new(
                "Afro",
                Color.Aqua,
                "Shaved",
                Color.Aquamarine,
                Color.Azure,
                Color.Beige,
                new()),
        };
}
