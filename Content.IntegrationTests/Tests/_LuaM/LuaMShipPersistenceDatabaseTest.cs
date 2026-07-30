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
public sealed class LuaMShipPersistenceDatabaseTest
{
    [Test]
    public async Task SqliteMigrationCreatesAndRevertsShipPersistenceSchema()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new SqliteServerDbContext(options);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        var snapshotTable = await CountSqliteSchemaObjectsAsync(connection, "table", "luam_ship_snapshot");
        var leaseTable = await CountSqliteSchemaObjectsAsync(connection, "table", "luam_ship_presence_lease");
        var leaseIndex = await CountSqliteSchemaObjectsAsync(connection, "index", "UX_luam_ship_lease_token");
        var retirementIndex = await CountSqliteSchemaObjectsAsync(
            connection,
            "index",
            "UX_luam_ship_retirement_operation");
        var payloadRevisionColumn = await CountSqliteTableColumnsAsync(
            connection,
            "luam_ship_snapshot",
            "payload_revision");
        var legacySingleOwnerIndex = await CountSqliteSchemaObjectsAsync(
            connection,
            "index",
            "UX_luam_ship_snapshot_active_owner");
        Assert.Multiple(() =>
        {
            Assert.That(snapshotTable, Is.EqualTo(1));
            Assert.That(leaseTable, Is.EqualTo(1));
            Assert.That(leaseIndex, Is.EqualTo(1));
            Assert.That(retirementIndex, Is.EqualTo(1));
            Assert.That(payloadRevisionColumn, Is.EqualTo(1));
            Assert.That(legacySingleOwnerIndex, Is.Zero,
                "Fleet persistence must allow more than one non-retired ship per owner.");
        });

        await migrator.MigrateAsync("20260719112950_LuaMFullShipPersistence");
        snapshotTable = await CountSqliteSchemaObjectsAsync(connection, "table", "luam_ship_snapshot");
        payloadRevisionColumn = await CountSqliteTableColumnsAsync(
            connection,
            "luam_ship_snapshot",
            "payload_revision");
        legacySingleOwnerIndex = await CountSqliteSchemaObjectsAsync(
            connection,
            "index",
            "UX_luam_ship_snapshot_active_owner");
        Assert.Multiple(() =>
        {
            Assert.That(snapshotTable, Is.EqualTo(1));
            Assert.That(payloadRevisionColumn, Is.Zero);
            Assert.That(legacySingleOwnerIndex, Is.Zero,
                "Downgrading to the real previous schema must not create a fleet-breaking index.");
        });

        await migrator.MigrateAsync("20260716101408_LuaMDeepCryoPersistence");
        snapshotTable = await CountSqliteSchemaObjectsAsync(connection, "table", "luam_ship_snapshot");
        leaseTable = await CountSqliteSchemaObjectsAsync(connection, "table", "luam_ship_presence_lease");
        Assert.Multiple(() =>
        {
            Assert.That(snapshotTable, Is.Zero);
            Assert.That(leaseTable, Is.Zero);
        });

        await migrator.MigrateAsync();
        snapshotTable = await CountSqliteSchemaObjectsAsync(connection, "table", "luam_ship_snapshot");
        payloadRevisionColumn = await CountSqliteTableColumnsAsync(
            connection,
            "luam_ship_snapshot",
            "payload_revision");
        legacySingleOwnerIndex = await CountSqliteSchemaObjectsAsync(
            connection,
            "index",
            "UX_luam_ship_snapshot_active_owner");
        Assert.Multiple(() =>
        {
            Assert.That(snapshotTable, Is.EqualTo(1));
            Assert.That(payloadRevisionColumn, Is.EqualTo(1));
            Assert.That(legacySingleOwnerIndex, Is.Zero);
        });
    }

    [Test]
    public async Task OwnerCanRegisterMultipleStoredShips()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var owner = new NetUserId(Guid.NewGuid());
        var now = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("Fleet Owner"));

        var first = await db.StoreLuaMShipSnapshotAsync(
            NewStoreRequest(Guid.NewGuid(), owner, now, "first hull"));
        var second = await db.StoreLuaMShipSnapshotAsync(
            NewStoreRequest(Guid.NewGuid(), owner, now.AddSeconds(1), "second hull"));
        var fleet = await db.GetLuaMShipSnapshotsByOwnerAsync(owner);

        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.True);
            Assert.That(second.Success, Is.True);
            Assert.That(fleet, Has.Count.EqualTo(2));
            Assert.That(fleet.All(ship => ship.Status == DbLuaMShipSnapshotStatus.Stored), Is.True);
        });
    }

    [Test]
    public async Task FleetMigrationDowngradePreservesCanonicalRowsAndAllowsUpgradeAgain()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new SqliteServerDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        var db = NewServerDb(() => options, inMemory: true);
        var owner = new NetUserId(Guid.NewGuid());
        var now = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("Fleet Migration Owner"));
        Assert.That(
            (await db.StoreLuaMShipSnapshotAsync(
                NewStoreRequest(Guid.NewGuid(), owner, now, "fleet hull one"))).Success,
            Is.True);
        Assert.That(
            (await db.StoreLuaMShipSnapshotAsync(
                NewStoreRequest(Guid.NewGuid(), owner, now.AddSeconds(1), "fleet hull two"))).Success,
            Is.True);

        await migrator.MigrateAsync("20260720164259_LuaMShipPayloadRevision");
        var downgradedIndex = await CountSqliteSchemaObjectsAsync(
            connection,
            "index",
            "UX_luam_ship_snapshot_active_owner");
        var downgradedRows = await CountSqliteRowsAsync(connection, "luam_ship_snapshot");
        Assert.Multiple(() =>
        {
            Assert.That(downgradedIndex, Is.Zero,
                "The migration Down path must match the prior canonical schema.");
            Assert.That(downgradedRows, Is.EqualTo(2),
                "Downgrade must preserve an already-valid fleet.");
        });

        await migrator.MigrateAsync();
        var upgradedIndex = await CountSqliteSchemaObjectsAsync(
            connection,
            "index",
            "UX_luam_ship_snapshot_active_owner");
        var upgradedRows = await CountSqliteRowsAsync(connection, "luam_ship_snapshot");
        Assert.Multiple(() =>
        {
            Assert.That(upgradedIndex, Is.Zero);
            Assert.That(upgradedRows, Is.EqualTo(2));
        });
    }

    [Test]
    public void PostgresFleetMigrationDownDoesNotInventTheDivergentUniqueIndex()
    {
        var options = new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=luam_migration_script;Username=unused;Password=unused")
            .Options;
        using var context = new PostgresServerDbContext(options);
        var migrator = context.GetService<IMigrator>();

        var downScript = migrator.GenerateScript(
            "20260727090000_LuaMShipFleetOwnerIndex",
            "20260720164309_LuaMShipPayloadRevision");
        var upScript = migrator.GenerateScript(
            "20260720164309_LuaMShipPayloadRevision",
            "20260727090000_LuaMShipFleetOwnerIndex");

        Assert.Multiple(() =>
        {
            Assert.That(downScript, Does.Not.Contain("UX_luam_ship_snapshot_active_owner"),
                "Downgrading canonical PostgreSQL must not create a one-ship-per-owner index.");
            Assert.That(upScript, Does.Contain("DROP INDEX IF EXISTS"));
            Assert.That(upScript, Does.Contain("UX_luam_ship_snapshot_active_owner"));
        });
    }

    [Test]
    public async Task FullSnapshotLifecycleUsesCasLeaseMetadataAndRetirementFence()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var owner = new NetUserId(Guid.NewGuid());
        var stranger = new NetUserId(Guid.NewGuid());
        var shipId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("Ship Owner"));

        var initialRequest = NewStoreRequest(shipId, owner, startedAt, "Original full YAML payload");
        var stored = await db.StoreLuaMShipSnapshotAsync(initialRequest);
        var loaded = await db.GetLuaMShipSnapshotAsync(shipId, owner);
        var ownerRegistry = await db.GetLuaMShipSnapshotsByOwnerAsync(owner);
        var strangerRegistry = await db.GetLuaMShipSnapshotsByOwnerAsync(stranger);
        var hiddenFromStranger = await db.GetLuaMShipSnapshotAsync(shipId, stranger);
        var invalidChecksum = await db.StoreLuaMShipSnapshotAsync(
            NewStoreRequest(Guid.NewGuid(), owner, startedAt, "invalid checksum") with
            {
                PayloadHash = new string('0', 64),
            });
        var optionalSuffixShipId = Guid.NewGuid();
        var optionalSuffix = await db.StoreLuaMShipSnapshotAsync(
            NewStoreRequest(optionalSuffixShipId, owner, startedAt, "ship without suffix") with
            {
                ShipNameSuffix = null,
            });

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(stored.Revision, Is.Zero);
            Assert.That(loaded!.PayloadRevision, Is.EqualTo(1));
            Assert.That(stored.SnapshotStatus, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Payload, Is.EqualTo(initialRequest.Payload));
            Assert.That(loaded.VesselPrototypeId, Is.EqualTo("VesselTestPersistence"));
            Assert.That(loaded.ShipName, Is.EqualTo("Persistent Test Ship"));
            Assert.That(loaded.ShipNameSuffix, Is.EqualTo("PT-01"));
            Assert.That(loaded.PurchasePrice, Is.EqualTo(125_000));
            Assert.That(loaded.PurchasedWithVoucher, Is.False);
            Assert.That(ownerRegistry, Has.Count.EqualTo(1));
            Assert.That(ownerRegistry[0].ShipId, Is.EqualTo(shipId));
            Assert.That(ownerRegistry[0].PayloadHash, Is.EqualTo(initialRequest.PayloadHash));
            Assert.That(strangerRegistry, Is.Empty);
            Assert.That(hiddenFromStranger, Is.Null);
            Assert.That(invalidChecksum.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidRequest));
            Assert.That(optionalSuffix.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
        });

        var claim = NewClaimRequest(shipId, owner, 0, startedAt.AddSeconds(1), startedAt.AddMinutes(5));
        var claimed = await db.ClaimLuaMShipRestoreAsync(claim);
        var staleClaim = await db.ClaimLuaMShipRestoreAsync(claim with { LeaseId = Guid.NewGuid() });
        var wrongOwnerClaim = await db.ClaimLuaMShipRestoreAsync(claim with
        {
            OwnerUserId = stranger,
            ExpectedRevision = 1,
            LeaseId = Guid.NewGuid(),
        });
        var completed = await db.CompleteLuaMShipRestoreAsync(new LuaMShipRestoreCompleteRequest(
            shipId,
            owner,
            1,
            claim.LeaseId,
            startedAt.AddSeconds(2)));
        var renewed = await db.RenewLuaMShipLeaseAsync(new LuaMShipLeaseRenewRequest(
            shipId,
            owner,
            2,
            claim.LeaseId,
            0,
            startedAt.AddSeconds(3),
            startedAt.AddMinutes(10)));

        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(claimed.Revision, Is.EqualTo(1));
            Assert.That(claimed.SnapshotStatus, Is.EqualTo(DbLuaMShipSnapshotStatus.Restoring));
            Assert.That(staleClaim.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.RevisionConflict));
            Assert.That(wrongOwnerClaim.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.OwnerMismatch));
            Assert.That(completed.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(completed.Revision, Is.EqualTo(2));
            Assert.That(completed.SnapshotStatus, Is.EqualTo(DbLuaMShipSnapshotStatus.Active));
            Assert.That(completed.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(renewed.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(renewed.LeaseRevision, Is.EqualTo(1));
        });

        var replacementPayload = "Modified ship: tiles, atmos, machines, inventories, mobs";
        var replacement = NewStoreRequest(shipId, owner, startedAt.AddSeconds(4), replacementPayload) with
        {
            ExpectedRevision = 2,
            PayloadRevision = 2,
            LeaseId = claim.LeaseId,
            ShipName = "Renamed Persistent Ship",
        };
        var stalePayloadReplacement = await db.StoreLuaMShipSnapshotAsync(replacement with
        {
            PayloadRevision = 1,
        });
        var replaced = await db.StoreLuaMShipSnapshotAsync(replacement);
        var afterReplace = await db.GetLuaMShipSnapshotAsync(shipId, owner);

        Assert.Multiple(() =>
        {
            Assert.That(stalePayloadReplacement.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidRequest));
            Assert.That(replaced.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(replaced.Revision, Is.EqualTo(3));
            Assert.That(afterReplace!.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(afterReplace.PayloadRevision, Is.EqualTo(2));
            Assert.That(afterReplace.LeaseId, Is.Null);
            Assert.That(afterReplace.Payload, Is.EqualTo(replacement.Payload));
            Assert.That(afterReplace.ShipName, Is.EqualTo("Renamed Persistent Ship"));
        });

        var abortClaim = NewClaimRequest(shipId, owner, 3, startedAt.AddSeconds(5), startedAt.AddMinutes(5));
        var claimedForAbort = await db.ClaimLuaMShipRestoreAsync(abortClaim);
        var aborted = await db.AbortLuaMShipRestoreAsync(new LuaMShipRestoreAbortRequest(
            shipId,
            owner,
            4,
            abortClaim.LeaseId,
            "spawn transaction rolled back",
            startedAt.AddSeconds(6)));
        Assert.Multiple(() =>
        {
            Assert.That(claimedForAbort.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(aborted.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(aborted.Revision, Is.EqualTo(5));
            Assert.That(aborted.SnapshotStatus, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(aborted.Snapshot!.PayloadRevision, Is.EqualTo(2));
            Assert.That(aborted.LeaseId, Is.Null);
        });

        var finalClaim = NewClaimRequest(shipId, owner, 5, startedAt.AddSeconds(7), startedAt.AddMinutes(5));
        var claimedAfterAbort = await db.ClaimLuaMShipRestoreAsync(finalClaim);
        Assert.Multiple(() =>
        {
            Assert.That(claimedAfterAbort.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(claimedAfterAbort.Revision, Is.EqualTo(6));
            Assert.That(claimedAfterAbort.Snapshot!.PayloadRevision, Is.EqualTo(2));
            Assert.That(claimedAfterAbort.Snapshot.Payload, Is.EqualTo(replacement.Payload));
        });
        await db.CompleteLuaMShipRestoreAsync(new LuaMShipRestoreCompleteRequest(
            shipId,
            owner,
            6,
            finalClaim.LeaseId,
            startedAt.AddSeconds(8)));

        var retirementOperation = Guid.NewGuid();
        var retirement = new LuaMShipSnapshotRetireRequest(
            retirementOperation,
            shipId,
            owner,
            7,
            finalClaim.LeaseId,
            "sold through shipyard",
            startedAt.AddSeconds(9));
        var retired = await db.RetireLuaMShipSnapshotAsync(retirement);
        var retiredReplay = await db.RetireLuaMShipSnapshotAsync(retirement);
        var restoreAfterSale = await db.ClaimLuaMShipRestoreAsync(
            NewClaimRequest(shipId, owner, 8, startedAt.AddSeconds(10), startedAt.AddMinutes(6)));
        var retiredRecord = await db.GetLuaMShipSnapshotAsync(shipId, owner);
        var activeRegistryAfterRetirement = await db.GetLuaMShipSnapshotsByOwnerAsync(owner);
        var auditRegistryAfterRetirement = await db.GetLuaMShipSnapshotsByOwnerAsync(owner, includeRetired: true);

        Assert.Multiple(() =>
        {
            Assert.That(retired.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(retired.Revision, Is.EqualTo(8));
            Assert.That(retired.SnapshotStatus, Is.EqualTo(DbLuaMShipSnapshotStatus.Retired));
            Assert.That(retiredReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(retiredReplay.Success, Is.True);
            Assert.That(restoreAfterSale.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Retired));
            Assert.That(retiredRecord!.LeaseId, Is.Null);
            Assert.That(retiredRecord.RetirementOperationId, Is.EqualTo(retirementOperation));
            Assert.That(retiredRecord.Payload, Is.EqualTo(replacement.Payload),
                "Retirement keeps the final evidence payload but makes it non-restorable.");
            Assert.That(activeRegistryAfterRetirement, Has.Count.EqualTo(1));
            Assert.That(activeRegistryAfterRetirement[0].ShipId, Is.EqualTo(optionalSuffixShipId));
            Assert.That(auditRegistryAfterRetirement, Has.Count.EqualTo(2));
            Assert.That(auditRegistryAfterRetirement.Count(record => record.Status == DbLuaMShipSnapshotStatus.Retired),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ExactWriteReplaysAreIdempotentAndPresenceReleaseRequiresActive()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var owner = new NetUserId(Guid.NewGuid());
        var shipId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("Idempotent Ship Owner"));

        var storeRequest = NewStoreRequest(shipId, owner, startedAt, "durable payload");
        var stored = await db.StoreLuaMShipSnapshotAsync(storeRequest);
        var storedReplay = await db.StoreLuaMShipSnapshotAsync(storeRequest);

        var firstClaimRequest = NewClaimRequest(
            shipId,
            owner,
            0,
            startedAt.AddSeconds(1),
            startedAt.AddMinutes(5));
        var firstClaim = await db.ClaimLuaMShipRestoreAsync(firstClaimRequest);
        var firstClaimReplay = await db.ClaimLuaMShipRestoreAsync(firstClaimRequest);
        var releaseWhileRestoring = await db.ReleaseLuaMShipPresenceAsync(new(
            shipId,
            owner,
            1,
            firstClaimRequest.LeaseId,
            "must not release a restore transaction",
            startedAt.AddSeconds(2)));
        var afterInvalidRelease = await db.GetLuaMShipSnapshotAsync(shipId, owner);

        var abortRequest = new LuaMShipRestoreAbortRequest(
            shipId,
            owner,
            1,
            firstClaimRequest.LeaseId,
            "placement rolled back",
            startedAt.AddSeconds(3));
        var aborted = await db.AbortLuaMShipRestoreAsync(abortRequest);
        var abortReplay = await db.AbortLuaMShipRestoreAsync(abortRequest);

        var secondClaimRequest = NewClaimRequest(
            shipId,
            owner,
            2,
            startedAt.AddSeconds(4),
            startedAt.AddMinutes(6));
        var secondClaim = await db.ClaimLuaMShipRestoreAsync(secondClaimRequest);
        var completeRequest = new LuaMShipRestoreCompleteRequest(
            shipId,
            owner,
            3,
            secondClaimRequest.LeaseId,
            startedAt.AddSeconds(5));
        var completed = await db.CompleteLuaMShipRestoreAsync(completeRequest);
        var completeReplay = await db.CompleteLuaMShipRestoreAsync(completeRequest);

        var renewRequest = new LuaMShipLeaseRenewRequest(
            shipId,
            owner,
            4,
            secondClaimRequest.LeaseId,
            0,
            startedAt.AddSeconds(6),
            startedAt.AddMinutes(10));
        var renewed = await db.RenewLuaMShipLeaseAsync(renewRequest);
        var renewReplay = await db.RenewLuaMShipLeaseAsync(renewRequest);

        var releaseRequest = new LuaMShipPresenceReleaseRequest(
            shipId,
            owner,
            4,
            secondClaimRequest.LeaseId,
            "live grid disappeared",
            startedAt.AddSeconds(7));
        var released = await db.ReleaseLuaMShipPresenceAsync(releaseRequest);
        var releaseReplay = await db.ReleaseLuaMShipPresenceAsync(releaseRequest);
        var final = await db.GetLuaMShipSnapshotAsync(shipId, owner);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(storedReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(firstClaim.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(firstClaimReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(releaseWhileRestoring.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidState));
            Assert.That(afterInvalidRelease!.Revision, Is.EqualTo(1));
            Assert.That(afterInvalidRelease.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Restoring));
            Assert.That(afterInvalidRelease.LeaseId, Is.EqualTo(firstClaimRequest.LeaseId));
            Assert.That(aborted.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(abortReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(secondClaim.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(completed.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(completeReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(renewed.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(renewed.LeaseRevision, Is.EqualTo(1));
            Assert.That(renewReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(released.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(releaseReplay.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
            Assert.That(final!.Revision, Is.EqualTo(5));
            Assert.That(final.PayloadRevision, Is.EqualTo(1));
            Assert.That(final.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(final.LeaseId, Is.Null);
            Assert.That(final.Payload, Is.EqualTo(storeRequest.Payload));
            Assert.That(final.PayloadHash, Is.EqualTo(storeRequest.PayloadHash).IgnoreCase);
        });
    }

    [Test]
    public async Task LegacyNullPayloadRevisionBackfillsOnNextSnapshotReplacement()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var owner = new NetUserId(Guid.NewGuid());
        var shipId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("Legacy Ship Owner"));
        var original = NewStoreRequest(shipId, owner, startedAt, "legacy payload");
        Assert.That((await db.StoreLuaMShipSnapshotAsync(original)).Success, Is.True);

        await using (var legacyContext = new SqliteServerDbContext(options))
        {
            var row = await legacyContext.LuaMShipSnapshots.SingleAsync();
            row.PayloadRevision = null;
            await legacyContext.SaveChangesAsync();
        }

        var legacy = await db.GetLuaMShipSnapshotAsync(shipId, owner);
        Assert.That(legacy!.PayloadRevision, Is.Null);
        var claim = NewClaimRequest(shipId, owner, 0, startedAt.AddSeconds(1), startedAt.AddMinutes(5));
        var claimed = await db.ClaimLuaMShipRestoreAsync(claim);
        var completed = await db.CompleteLuaMShipRestoreAsync(new(
            shipId,
            owner,
            claimed.Revision!.Value,
            claim.LeaseId,
            startedAt.AddSeconds(2)));
        var replacement = NewStoreRequest(shipId, owner, startedAt.AddSeconds(3), "replacement payload") with
        {
            ExpectedRevision = completed.Revision,
            PayloadRevision = 2,
            LeaseId = claim.LeaseId,
        };
        var stored = await db.StoreLuaMShipSnapshotAsync(replacement);
        var backfilled = await db.GetLuaMShipSnapshotAsync(shipId, owner);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.Success));
            Assert.That(stored.Revision, Is.EqualTo(3));
            Assert.That(backfilled!.PayloadRevision, Is.EqualTo(2));
            Assert.That(backfilled.Payload, Is.EqualTo(replacement.Payload));
            Assert.That(backfilled.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(backfilled.LeaseId, Is.Null);
        });
    }

    [Test]
    public async Task ExpiredRestoringAndActiveLeasesReturnTheWholeSnapshotToStored()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var owner = new NetUserId(Guid.NewGuid());
        var shipId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("Lease Owner"));
        await db.StoreLuaMShipSnapshotAsync(NewStoreRequest(shipId, owner, startedAt, "lease payload"));

        var restoringClaim = NewClaimRequest(
            shipId,
            owner,
            0,
            startedAt.AddSeconds(1),
            startedAt.AddSeconds(2));
        await db.ClaimLuaMShipRestoreAsync(restoringClaim);
        var restoredFromExpiredClaim = await db.RecoverExpiredLuaMShipLeasesAsync(startedAt.AddSeconds(3));
        var noDuplicateRecovery = await db.RecoverExpiredLuaMShipLeasesAsync(startedAt.AddSeconds(3));
        var afterClaimRecovery = await db.GetLuaMShipSnapshotAsync(shipId, owner);

        Assert.Multiple(() =>
        {
            Assert.That(restoredFromExpiredClaim, Is.EqualTo(1));
            Assert.That(noDuplicateRecovery, Is.Zero);
            Assert.That(afterClaimRecovery!.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(afterClaimRecovery.Revision, Is.EqualTo(2));
            Assert.That(afterClaimRecovery.PayloadRevision, Is.EqualTo(1));
            Assert.That(afterClaimRecovery.LeaseId, Is.Null);
        });

        var activeClaim = NewClaimRequest(
            shipId,
            owner,
            2,
            startedAt.AddSeconds(4),
            startedAt.AddSeconds(6));
        await db.ClaimLuaMShipRestoreAsync(activeClaim);
        await db.CompleteLuaMShipRestoreAsync(new LuaMShipRestoreCompleteRequest(
            shipId,
            owner,
            3,
            activeClaim.LeaseId,
            startedAt.AddSeconds(5)));
        var restoredFromExpiredActive = await db.RecoverExpiredLuaMShipLeasesAsync(startedAt.AddSeconds(7));
        var afterActiveRecovery = await db.GetLuaMShipSnapshotAsync(shipId, owner);

        Assert.Multiple(() =>
        {
            Assert.That(restoredFromExpiredActive, Is.EqualTo(1));
            Assert.That(afterActiveRecovery!.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            Assert.That(afterActiveRecovery.Revision, Is.EqualTo(5));
            Assert.That(afterActiveRecovery.PayloadRevision, Is.EqualTo(1));
            Assert.That(afterActiveRecovery.LeaseId, Is.Null);
            Assert.That(afterActiveRecovery.Payload, Is.EqualTo(
                NewStoreRequest(shipId, owner, startedAt, "lease payload").Payload));
        });
    }

    [Test]
    public async Task ConcurrentIdenticalInitialStoresResolveAsOneSuccessAndOneReplay()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"luam-ship-store-{Guid.NewGuid():N}.db");
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
            var owner = new NetUserId(Guid.NewGuid());
            var startedAt = DateTime.UtcNow;
            await db.InitPrefsAsync(owner, NewProfile("Concurrent Initial Store Owner"));
            var request = NewStoreRequest(Guid.NewGuid(), owner, startedAt, "identical initial payload");

            var results = await Task.WhenAll(
                db.StoreLuaMShipSnapshotAsync(request),
                db.StoreLuaMShipSnapshotAsync(request));

            Assert.Multiple(() =>
            {
                Assert.That(
                    results.Count(value => value.Status == LuaMShipPersistenceWriteStatus.Success),
                    Is.EqualTo(1));
                Assert.That(
                    results.Count(value => value.Status == LuaMShipPersistenceWriteStatus.AlreadyProcessed),
                    Is.EqualTo(1),
                    "The loser of the initial insert race must re-read and recognize the exact committed request.");
            });

            await using var context = new SqliteServerDbContext(options);
            Assert.That(await context.LuaMShipSnapshots.CountAsync(), Is.EqualTo(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Test]
    public async Task PostgresConcurrentIdenticalInitialStoresResolveAsOneSuccessAndOneReplay()
    {
        var connectionString = Environment.GetEnvironmentVariable("LUAM_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore(
                "Set LUAM_TEST_POSTGRES_CONNECTION to a disposable PostgreSQL database to run the live provider race.");
        }

        var options = new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql(connectionString!)
            .Options;
        await using (var context = new PostgresServerDbContext(options))
            await context.Database.MigrateAsync();

        var db = NewPostgresDb(options, connectionString!);
        var owner = new NetUserId(Guid.NewGuid());
        var startedAt = DateTime.UtcNow;
        await db.InitPrefsAsync(owner, NewProfile("PostgreSQL Concurrent Initial Store Owner"));
        var request = NewStoreRequest(Guid.NewGuid(), owner, startedAt, "postgres identical initial payload");

        var results = await Task.WhenAll(
            db.StoreLuaMShipSnapshotAsync(request),
            db.StoreLuaMShipSnapshotAsync(request));

        Assert.Multiple(() =>
        {
            Assert.That(
                results.Count(value => value.Status == LuaMShipPersistenceWriteStatus.Success),
                Is.EqualTo(1));
            Assert.That(
                results.Count(value => value.Status == LuaMShipPersistenceWriteStatus.AlreadyProcessed),
                Is.EqualTo(1),
                "The PostgreSQL insert-race loser must re-read the exact committed request.");
        });

        await using var verify = new PostgresServerDbContext(options);
        Assert.That(
            await verify.LuaMShipSnapshots.CountAsync(snapshot => snapshot.ShipId == request.ShipId),
            Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentClaimsCreateAtMostOneDurableShipLease()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"luam-ship-{Guid.NewGuid():N}.db");
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
            var owner = new NetUserId(Guid.NewGuid());
            var shipId = Guid.NewGuid();
            var startedAt = DateTime.UtcNow;
            await db.InitPrefsAsync(owner, NewProfile("Concurrent Ship Owner"));
            await db.StoreLuaMShipSnapshotAsync(NewStoreRequest(shipId, owner, startedAt, "race payload"));

            var first = NewClaimRequest(shipId, owner, 0, startedAt.AddSeconds(1), startedAt.AddMinutes(5));
            var second = first with { LeaseId = Guid.NewGuid() };
            var results = await Task.WhenAll(
                db.ClaimLuaMShipRestoreAsync(first),
                db.ClaimLuaMShipRestoreAsync(second));

            Assert.That(results.Count(value => value.Status == LuaMShipPersistenceWriteStatus.Success), Is.EqualTo(1));
            Assert.That(results.Single(value => value.Status != LuaMShipPersistenceWriteStatus.Success).Status,
                Is.AnyOf(
                    LuaMShipPersistenceWriteStatus.RevisionConflict,
                    LuaMShipPersistenceWriteStatus.InvalidState,
                    LuaMShipPersistenceWriteStatus.LeaseConflict,
                    LuaMShipPersistenceWriteStatus.UnknownOutcome));

            await using var context = new SqliteServerDbContext(options);
            Assert.That(await context.LuaMShipPresenceLeases.CountAsync(), Is.EqualTo(1));
            Assert.That((await context.LuaMShipSnapshots.SingleAsync()).Status,
                Is.EqualTo(DbLuaMShipSnapshotStatus.Restoring));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    private static LuaMShipSnapshotStoreRequest NewStoreRequest(
        Guid shipId,
        NetUserId owner,
        DateTime storedAtUtc,
        string payloadText)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(payloadText);
        return new LuaMShipSnapshotStoreRequest(
            shipId,
            owner,
            null,
            1,
            null,
            "VesselTestPersistence",
            "Persistent Test Ship",
            "PT-01",
            125_000,
            false,
            200,
            1,
            1,
            payload,
            Convert.ToHexString(SHA256.HashData(payload)),
            payload.Length,
            64,
            "integration-test-build",
            new string('A', 64),
            storedAtUtc);
    }

    private static LuaMShipRestoreClaimRequest NewClaimRequest(
        Guid shipId,
        NetUserId owner,
        long expectedRevision,
        DateTime claimedAtUtc,
        DateTime expiresAtUtc)
        => new(
            shipId,
            owner,
            expectedRevision,
            201,
            Guid.NewGuid(),
            "ship-persistence-integration-test",
            claimedAtUtc,
            expiresAtUtc);

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
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountSqliteTableColumnsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static async Task<long> CountSqliteRowsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
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

    private static ServerDbPostgres NewPostgresDb(
        DbContextOptions<PostgresServerDbContext> options,
        string connectionString)
    {
        var logManager = new LogManager();
        IConfigurationManager configuration = MockInterfaces.MakeConfigurationManager(
            new Mock<IGameTiming>().Object,
            logManager,
            loadCvarsFromTypes: [typeof(CCVars)]);
        return new ServerDbPostgres(
            options,
            connectionString,
            configuration,
            new DummySawmill(),
            new DummySawmill());
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
