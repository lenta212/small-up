#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
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
        Assert.That(await CountSqliteSchemaObjectsAsync(
            connection,
            "table",
            "luam_character_presence_operation"), Is.EqualTo(1));
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
        Assert.That(await CountSqliteSchemaObjectsAsync(
            connection,
            "trigger",
            "TR_luam_presence_operation_no_update"), Is.EqualTo(1));
    }

    [Test]
    public async Task PresenceMigrationBackfillsFinalEpochForClaimPrepareAndAuthorization()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var users = new[]
        {
            new NetUserId(Guid.NewGuid()),
            new NetUserId(Guid.NewGuid()),
            new NetUserId(Guid.NewGuid()),
        };
        for (var i = 0; i < users.Length; i++)
            await db.InitPrefsAsync(users[i], NewProfile($"Migration Epoch {i}"));

        int[] profileIds;
        int[] preferenceIds;
        await using (var context = new SqliteServerDbContext(options))
        {
            profileIds = new int[users.Length];
            preferenceIds = new int[users.Length];
            for (var i = 0; i < users.Length; i++)
            {
                var profile = await context.Profile
                    .SingleAsync(value => value.Preference.UserId == users[i].UserId);
                profileIds[i] = profile.Id;
                preferenceIds[i] = profile.PreferenceId;
            }

            await context.GetService<IMigrator>()
                .MigrateAsync("20260720164259_LuaMShipPayloadRevision");
        }

        var leaseIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await SeedLegacyRestoreLeaseAsync(
            connection,
            users[0],
            preferenceIds[0],
            profileIds[0],
            9101,
            leaseIds[0],
            DbLuaMDeepCryoSnapshotStatus.Restoring,
            snapshotRevision: 1,
            lifecycleRevision: 10,
            completionReason: null,
            includeCompletion: false);
        await SeedLegacyRestoreLeaseAsync(
            connection,
            users[1],
            preferenceIds[1],
            profileIds[1],
            9102,
            leaseIds[1],
            DbLuaMDeepCryoSnapshotStatus.Consumed,
            snapshotRevision: 2,
            lifecycleRevision: 20,
            completionReason: null,
            includeCompletion: true);
        await SeedLegacyRestoreLeaseAsync(
            connection,
            users[2],
            preferenceIds[2],
            profileIds[2],
            9103,
            leaseIds[2],
            DbLuaMDeepCryoSnapshotStatus.Consumed,
            snapshotRevision: 3,
            lifecycleRevision: 30,
            completionReason: LuaMDeepCryoPublication.AuthorizationReason,
            includeCompletion: true);

        await using (var context = new SqliteServerDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync();
            var leases = await context.LuaMCharacterPresenceLeases.AsNoTracking()
                .OrderBy(value => value.ProfileId)
                .ToArrayAsync();
            var profileEpochs = await context.Profile.AsNoTracking()
                .Where(value => profileIds.Contains(value.Id))
                .OrderBy(value => value.Id)
                .Select(value => value.LifecycleRevision)
                .ToArrayAsync();
            Assert.Multiple(() =>
            {
                Assert.That(leases, Has.Length.EqualTo(3));
                Assert.That(leases.All(value => value.Phase == DbLuaMCharacterPresencePhase.RestoreClaim), Is.True);
                Assert.That(leases[0].AuthorityLifecycleRevision, Is.EqualTo(13));
                Assert.That(leases[1].AuthorityLifecycleRevision, Is.EqualTo(22));
                Assert.That(leases[2].AuthorityLifecycleRevision, Is.EqualTo(31));
                Assert.That(leases.Select(value => value.RoundId), Is.All.EqualTo(501));
                Assert.That(leases.Select(value => value.SnapshotId), Is.All.Not.Null);
                Assert.That(profileEpochs, Is.EqualTo(new long[] { 13, 22, 31 }),
                    "Backfilled authority epochs must also advance the profile CAS fence.");
            });
        }
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

        var storeRequest = await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow);
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

        var firstClaim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
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

        var secondClaim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId.Value, 2);
        var claimedAgain = await db.ClaimLuaMDeepCryoRestoreAsync(secondClaim);
        var completionOperationId = Guid.NewGuid();
        var completed = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            completionOperationId, userId, profileId, 0, stored.SnapshotId.Value, 3,
            secondClaim.LeaseId, DateTime.UtcNow));
        var prepared = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        var authorizationOperationId = Guid.NewGuid();
        var authorizedAt = DateTime.UtcNow;
        var authorized = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                authorizationOperationId, completionOperationId, userId, profileId, 0,
                stored.SnapshotId.Value, 4, secondClaim.LeaseId, authorizedAt));
        var acknowledgeRequest = new LuaMDeepCryoAcknowledgePublicationRequest(
            Guid.NewGuid(), authorizationOperationId, userId, profileId, 0,
            stored.SnapshotId.Value, 5, secondClaim.LeaseId,
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5));
        var acknowledged = await db.AcknowledgeLuaMDeepCryoPublicationAsync(acknowledgeRequest);
        var acknowledgeReplay = await db.AcknowledgeLuaMDeepCryoPublicationAsync(acknowledgeRequest);

        Assert.Multiple(() =>
        {
            Assert.That(claimedAgain.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(claimedAgain.Revision, Is.EqualTo(3));
            Assert.That(completed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(completed.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(completed.Revision, Is.EqualTo(4));
            Assert.That(completed.LeaseId, Is.EqualTo(secondClaim.LeaseId));
            Assert.That(completed.Snapshot?.LeaseId, Is.EqualTo(secondClaim.LeaseId));
            Assert.That(prepared, Is.Not.Null,
                "A prepared publication must remain visible as busy while its exact lease is retained.");
            Assert.That(prepared!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(prepared.LeaseId, Is.EqualTo(secondClaim.LeaseId));
            Assert.That(authorized.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(authorized.Revision, Is.EqualTo(5));
            Assert.That(authorized.Snapshot?.LeaseId, Is.EqualTo(secondClaim.LeaseId));
            Assert.That(acknowledged.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(acknowledged.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(acknowledged.Revision, Is.EqualTo(6));
            Assert.That(acknowledged.Snapshot?.LeaseId, Is.EqualTo(secondClaim.LeaseId));
            Assert.That(acknowledged.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
            Assert.That(acknowledgeReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(acknowledgeReplay.Revision, Is.EqualTo(6));
            Assert.That(acknowledgeReplay.Authority, Is.EqualTo(acknowledged.Authority));
        });
        Assert.That(await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0), Is.Null);

        var discardedEpisode = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
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
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var quarantined = await db.QuarantineLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoQuarantineRequest(
            Guid.NewGuid(), userId, profileId, 0, quarantinedEpisode.SnapshotId!.Value, 0,
            null, "prototype manifest mismatch", DateTime.UtcNow));
        var quarantineDiscard = await db.DiscardLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoDiscardRequest(
            Guid.NewGuid(), userId, profileId, 0, quarantinedEpisode.SnapshotId.Value, 1,
            null, DateTime.UtcNow));
        var quarantinePrecondition = await db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
        var storePastQuarantine = await db.StoreLuaMDeepCryoSnapshotAsync(
            NewStoreRequest(
                userId,
                profileId,
                DateTime.UtcNow,
                quarantinePrecondition!.LifecycleRevision,
                Guid.NewGuid()));

        Assert.Multiple(() =>
        {
            Assert.That(quarantined.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(quarantined.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(quarantineDiscard.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Quarantined));
            Assert.That(quarantinePrecondition.ActiveSnapshot?.Status,
                Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(storePastQuarantine.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LeaseConflict));
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
        var presenceJournalMutation = Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE luam_character_presence_operation SET reason = 'tamper' WHERE lua_m_character_presence_operations_id = (SELECT MIN(lua_m_character_presence_operations_id) FROM luam_character_presence_operation)"));
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
        var profileEpoch = (await context.Profile.AsNoTracking()
            .SingleAsync(value => value.Id == profileId)).LifecycleRevision;
        var constraintNow = DateTime.UtcNow;
        var malformedRenewalProof = Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO luam_character_presence_lease (
                    profile_id, snapshot_id, lease_id, phase, server_instance_id, round_id,
                    acquired_at_utc, renewed_at_utc, expires_at_utc, revision,
                    authority_lifecycle_revision, last_renewal_operation_id,
                    last_renewal_operation_identity_key)
                VALUES ({0}, NULL, {1}, 1, 'constraint-test', 1, {2}, {2}, {3}, 0, {4}, {5}, NULL)
                """,
                profileId,
                Guid.NewGuid(),
                constraintNow,
                constraintNow.AddMinutes(1),
                profileEpoch,
                Guid.NewGuid()));

        Assert.Multiple(() =>
        {
            Assert.That(journalMutation!.Message, Does.Contain("append-only"));
            Assert.That(presenceJournalMutation!.Message, Does.Contain("append-only"));
            Assert.That(payloadMutation!.Message, Does.Contain("immutable"));
            Assert.That(snapshotDeletion!.Message, Does.Contain("cannot be deleted"));
            Assert.That(negativeProfileRevision!.Message, Does.Contain("cannot be negative"));
            Assert.That(malformedRenewalProof!.Message, Does.Contain("CK_luam_cryo_lease_last_renewal"));
        });
    }

    [Test]
    public async Task UnpublishedCompletionRollbackIsBoundIdempotentAndRetryable()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Publication Rollback"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;

        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        var completionOperationId = Guid.NewGuid();
        var completed = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            claimed.Revision!.Value,
            claim.LeaseId,
            DateTime.UtcNow));
        var prepared = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);

        var wrongProof = await db.RollbackLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoRollbackPublicationRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                completed.Revision!.Value,
                claim.LeaseId,
                "unpublished test restore",
                DateTime.UtcNow));

        var rollbackRequest = new LuaMDeepCryoRollbackPublicationRequest(
            Guid.NewGuid(),
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            completed.Revision.Value,
            claim.LeaseId,
            "unpublished test restore",
            DateTime.UtcNow);
        var rolledBack = await db.RollbackLuaMDeepCryoPublicationAsync(rollbackRequest);
        var rollbackReplay = await db.RollbackLuaMDeepCryoPublicationAsync(rollbackRequest);
        var reopened = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);

        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(completed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(completed.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(completed.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(completed.Snapshot?.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(prepared, Is.Not.Null);
            Assert.That(prepared!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(prepared.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(wrongProof.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.InvalidState),
                "A different completion operation must not reopen a consumed snapshot.");
            Assert.That(rolledBack.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(rolledBack.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(rolledBack.Revision, Is.EqualTo(completed.Revision + 1));
            Assert.That(rollbackReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(rollbackReplay.Revision, Is.EqualTo(rolledBack.Revision));
            Assert.That(rollbackReplay.Snapshot, Is.Not.Null,
                "An accepted rollback replay must prove that its result is still the coherent current state.");
            Assert.That(reopened, Is.Not.Null);
            Assert.That(reopened!.Id, Is.EqualTo(stored.SnapshotId));
            Assert.That(reopened.Revision, Is.EqualTo(rolledBack.Revision));
            Assert.That(reopened.LeaseId, Is.Null);
        });

        var reclaimed = await db.ClaimLuaMDeepCryoRestoreAsync(
            await NewClaimRequestAsync(
                db,
                userId,
                profileId,
                stored.SnapshotId.Value,
                rolledBack.Revision!.Value));
        var retryCompletionOperationId = Guid.NewGuid();
        var completedRetry = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            retryCompletionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            reclaimed.Revision!.Value,
            reclaimed.LeaseId!.Value,
            DateTime.UtcNow));
        var retryAuthorizationOperationId = Guid.NewGuid();
        var authorizedRetry = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                retryAuthorizationOperationId,
                retryCompletionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                completedRetry.Revision!.Value,
                reclaimed.LeaseId.Value,
                DateTime.UtcNow));
        var acknowledgeRequest = new LuaMDeepCryoAcknowledgePublicationRequest(
            Guid.NewGuid(),
            retryAuthorizationOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            authorizedRetry.Revision!.Value,
            reclaimed.LeaseId.Value,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5));
        var acknowledged = await db.AcknowledgeLuaMDeepCryoPublicationAsync(acknowledgeRequest);
        var acknowledgeReplay = await db.AcknowledgeLuaMDeepCryoPublicationAsync(acknowledgeRequest);

        await using var context = new SqliteServerDbContext(options);
        var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
        Assert.Multiple(() =>
        {
            Assert.That(reclaimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(completedRetry.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(completedRetry.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(completedRetry.Snapshot?.LeaseId, Is.EqualTo(reclaimed.LeaseId));
            Assert.That(authorizedRetry.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(authorizedRetry.Revision, Is.EqualTo(completedRetry.Revision + 1));
            Assert.That(authorizedRetry.Snapshot?.LeaseId, Is.EqualTo(reclaimed.LeaseId));
            Assert.That(acknowledged.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(acknowledged.Revision, Is.EqualTo(authorizedRetry.Revision + 1));
            Assert.That(acknowledged.Snapshot?.LeaseId, Is.EqualTo(reclaimed.LeaseId));
            Assert.That(acknowledged.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
            Assert.That(acknowledgeReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(career.Status, Is.EqualTo(DbLuaMCharacterCareerStatus.Playable));
            Assert.That(context.LuaMDeepCryoOperations.Count(value =>
                    value.Kind == DbLuaMDeepCryoOperationKind.AbortRestore &&
                    value.Reason == "unpublished test restore"),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AuthorizationProofStrictlyGatesAckRollbackAndAuthorizedQuarantine()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Publication Authorization Proof"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        var completionOperationId = Guid.NewGuid();
        var prepared = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            claimed.Revision!.Value,
            claim.LeaseId,
            DateTime.UtcNow));

        var ackWithPrepareProof = await db.AcknowledgeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAcknowledgePublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision!.Value,
                claim.LeaseId,
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(5)));
        var quarantineWithPrepareProof = await db.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoQuarantineAuthorizedPublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision.Value,
                claim.LeaseId,
                "wrong predecessor proof",
                DateTime.UtcNow));

        var authorizationOperationId = Guid.NewGuid();
        var authorizedAt = DateTime.UtcNow;
        var authorizationRequest = new LuaMDeepCryoAuthorizePublicationRequest(
            authorizationOperationId,
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            prepared.Revision.Value,
            claim.LeaseId,
            authorizedAt);
        var authorized = await db.AuthorizeLuaMDeepCryoPublicationAsync(authorizationRequest);
        var rollbackAfterAuthorization = await db.RollbackLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoRollbackPublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision.Value,
                claim.LeaseId,
                "AUTH already won",
                DateTime.UtcNow));
        var ackWithHistoricalPrepareProof = await db.AcknowledgeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAcknowledgePublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                authorized.Revision!.Value,
                claim.LeaseId,
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(5)));

        var quarantineRequest = new LuaMDeepCryoQuarantineAuthorizedPublicationRequest(
            Guid.NewGuid(),
            authorizationOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            authorized.Revision.Value,
            claim.LeaseId,
            "authorized body attachment unconfirmed",
            DateTime.UtcNow);
        var quarantined = await db.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(quarantineRequest);
        var quarantineReplay = await db.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(quarantineRequest);
        var historicalAuthorizationReplay = await db.AuthorizeLuaMDeepCryoPublicationAsync(authorizationRequest);
        var loaded = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);

        Assert.Multiple(() =>
        {
            Assert.That(ackWithPrepareProof.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.InvalidState));
            Assert.That(quarantineWithPrepareProof.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.InvalidState));
            Assert.That(authorized.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(authorized.Snapshot?.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(rollbackAfterAuthorization.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.RevisionConflict),
                "Once AUTH advances the revision, PREPARE rollback must lose the CAS race.");
            Assert.That(ackWithHistoricalPrepareProof.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.InvalidState));
            Assert.That(quarantined.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(quarantined.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(quarantined.Snapshot?.LeaseId, Is.Null);
            Assert.That(quarantineReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(quarantineReplay.Snapshot, Is.Not.Null);
            Assert.That(historicalAuthorizationReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(historicalAuthorizationReplay.Snapshot, Is.Null,
                "A later revision must not be accepted as coherent proof of the historical AUTH result.");
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(loaded.LeaseId, Is.Null);
        });

        await using var context = new SqliteServerDbContext(options);
        var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
        Assert.That(career.Status, Is.EqualTo(DbLuaMCharacterCareerStatus.Quarantined));
    }

    [Test]
    public async Task AcknowledgedPublicationQuarantineConsumesPlayableTokenAndPreservesPayload()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Acknowledged Compensation"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var storeRequest = await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow);
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(storeRequest);
        var claimRequest = await NewClaimRequestAsync(
            db,
            userId,
            profileId,
            stored.SnapshotId!.Value,
            stored.Revision!.Value);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claimRequest);
        var prepareOperationId = Guid.NewGuid();
        var prepared = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            prepareOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            claimed.Revision!.Value,
            claimRequest.LeaseId,
            DateTime.UtcNow));
        var authorizationOperationId = Guid.NewGuid();
        var authorized = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                authorizationOperationId,
                prepareOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision!.Value,
                claimRequest.LeaseId,
                DateTime.UtcNow));
        var acknowledgementOperationId = Guid.NewGuid();
        var acknowledged = await db.AcknowledgeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAcknowledgePublicationRequest(
                acknowledgementOperationId,
                authorizationOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                authorized.Revision!.Value,
                claimRequest.LeaseId,
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(5)));
        var hiddenAfterAcknowledgement = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        Assert.Multiple(() =>
        {
            Assert.That(acknowledged.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(acknowledged.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
            Assert.That(hiddenAfterAcknowledgement, Is.Null);
        });

        await using (var context = new SqliteServerDbContext(options))
        {
            var lease = await context.LuaMCharacterPresenceLeases.SingleAsync();
            var expiredAt = DateTime.UtcNow.AddMinutes(-1);
            lease.AcquiredAtUtc = expiredAt.AddMinutes(-2);
            lease.RenewedAtUtc = expiredAt.AddMinutes(-1);
            lease.ExpiresAtUtc = expiredAt;
            await context.SaveChangesAsync();
        }

        Assert.That(
            await db.RecoverExpiredLuaMDeepCryoLeasesAsync(DateTime.UtcNow.AddYears(10)),
            Is.Zero,
            "Background restore recovery must never consume an expired Playable authority linked to ACK evidence.");
        var hiddenAfterExpiryRecovery = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        var authorityAfterExpiryRecovery = await db.GetLuaMCharacterPresenceAuthorityAsync(userId, profileId, 0);
        Assert.Multiple(() =>
        {
            Assert.That(hiddenAfterExpiryRecovery, Is.Null);
            Assert.That(authorityAfterExpiryRecovery?.LeaseId, Is.EqualTo(acknowledged.Authority!.LeaseId));
            Assert.That(authorityAfterExpiryRecovery?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
            Assert.That(authorityAfterExpiryRecovery?.SnapshotId, Is.EqualTo(stored.SnapshotId));
        });

        var quarantineRequest = new LuaMDeepCryoQuarantineAcknowledgedPublicationRequest(
            Guid.NewGuid(),
            acknowledgementOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            acknowledged.Revision!.Value,
            acknowledged.Authority!.LeaseId,
            acknowledged.Authority.Revision,
            acknowledged.Authority.AuthorityLifecycleRevision,
            "post-acknowledgement live-body bind failed",
            DateTime.UtcNow);
        var quarantined = await db.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(quarantineRequest);
        var quarantineReplay = await db.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(quarantineRequest);
        var staleAcknowledgement = await db.AcknowledgeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAcknowledgePublicationRequest(
                acknowledgementOperationId,
                authorizationOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                authorized.Revision.Value,
                claimRequest.LeaseId,
                acknowledged.Snapshot!.UpdatedAtUtc,
                acknowledged.Authority.ExpiresAtUtc));
        var active = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        Assert.Multiple(() =>
        {
            Assert.That(quarantined.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(quarantined.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(quarantined.Snapshot?.Payload, Is.EqualTo(storeRequest.Payload));
            Assert.That(quarantined.Authority, Is.Null);
            Assert.That(quarantineReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(staleAcknowledgement.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict));
            Assert.That(staleAcknowledgement.Authority, Is.Null);
            Assert.That(active?.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(active?.Payload, Is.EqualTo(storeRequest.Payload));
            Assert.That(active?.Authority, Is.Null);
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
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        await ExpirePresenceLeaseAsync(options, claim.LeaseId);
        var afterExpiry = DateTime.UtcNow;

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
    public async Task ExpiredPreparedPublicationIsSkippedWhileProtectedAndReopenedAfterUnprotect()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Prepared Lease Recovery"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        var completionOperationId = Guid.NewGuid();
        var prepared = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            claimed.Revision!.Value,
            claim.LeaseId,
            DateTime.UtcNow));
        await ExpirePresenceLeaseAsync(options, claim.LeaseId);
        var afterExpiry = DateTime.UtcNow;

        var ownerRecovery = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(
            afterExpiry,
            [claim.LeaseId]);
        var stillPrepared = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        var replacementRecovery = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(
            afterExpiry,
            Array.Empty<Guid>());
        var replacementReplay = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(
            afterExpiry,
            Array.Empty<Guid>());
        var authorizationAfterRecovery = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision!.Value,
                claim.LeaseId,
                afterExpiry));
        var reopened = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);

        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(prepared.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(prepared.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(prepared.Snapshot?.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(ownerRecovery, Is.Zero,
                "Recovery must not reap an exact in-memory owner lease.");
            Assert.That(stillPrepared, Is.Not.Null);
            Assert.That(stillPrepared!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
            Assert.That(stillPrepared.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(replacementRecovery, Is.EqualTo(1));
            Assert.That(replacementReplay, Is.Zero);
            Assert.That(authorizationAfterRecovery.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.RevisionConflict),
                "If lease recovery wins the CAS, a late AUTH must not resurrect the prepared publication.");
            Assert.That(reopened, Is.Not.Null);
            Assert.That(reopened!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(reopened.Revision, Is.EqualTo(prepared.Revision + 1));
            Assert.That(reopened.LeaseId, Is.Null);
        });

        await using var context = new SqliteServerDbContext(options);
        var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
        Assert.Multiple(() =>
        {
            Assert.That(career.Status, Is.EqualTo(DbLuaMCharacterCareerStatus.CryoStored));
            Assert.That(context.LuaMDeepCryoOperations.Count(value =>
                    value.Kind == DbLuaMDeepCryoOperationKind.RecoverExpiredLease &&
                    value.Reason == "prepared publication lease expired"),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ExpiredAuthorizedPublicationIsQuarantinedAfterUnprotect()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Authorized Lease Recovery"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        var completionOperationId = Guid.NewGuid();
        var prepared = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            claimed.Revision!.Value,
            claim.LeaseId,
            DateTime.UtcNow));
        var authorizationOperationId = Guid.NewGuid();
        var authorizedAt = DateTime.UtcNow;
        var authorized = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                authorizationOperationId,
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision!.Value,
                claim.LeaseId,
                authorizedAt));
        var authorizationReplay = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                authorizationOperationId,
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision.Value,
                claim.LeaseId,
                authorizedAt));
        await ExpirePresenceLeaseAsync(options, claim.LeaseId);
        var afterExpiry = DateTime.UtcNow;

        var protectedRecovery = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(
            afterExpiry,
            [claim.LeaseId]);
        var unprotectedRecovery = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(
            afterExpiry,
            Array.Empty<Guid>());
        var lateAuthorizedQuarantine = await db.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoQuarantineAuthorizedPublicationRequest(
                Guid.NewGuid(),
                authorizationOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                authorized.Revision!.Value,
                claim.LeaseId,
                "local continuation observed remote AUTH recovery",
                DateTime.UtcNow));
        var authorizationReplayAfterRecovery = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                authorizationOperationId,
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision.Value,
                claim.LeaseId,
                authorizedAt));
        var quarantined = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);

        Assert.Multiple(() =>
        {
            Assert.That(authorized.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(authorized.Revision, Is.EqualTo(prepared.Revision + 1));
            Assert.That(authorizationReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(authorizationReplay.Snapshot?.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(protectedRecovery, Is.Zero);
            Assert.That(unprotectedRecovery, Is.EqualTo(1));
            Assert.That(lateAuthorizedQuarantine.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.RevisionConflict));
            Assert.That(lateAuthorizedQuarantine.SnapshotStatus,
                Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(lateAuthorizedQuarantine.Revision, Is.EqualTo(authorized.Revision + 1));
            Assert.That(lateAuthorizedQuarantine.Snapshot?.Status,
                Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(lateAuthorizedQuarantine.Authority, Is.Null);
            Assert.That(authorizationReplayAfterRecovery.Status,
                Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            Assert.That(authorizationReplayAfterRecovery.Snapshot, Is.Null);
            Assert.That(quarantined, Is.Not.Null);
            Assert.That(quarantined!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
            Assert.That(quarantined.Revision, Is.EqualTo(authorized.Revision + 1));
            Assert.That(quarantined.LeaseId, Is.Null);
            Assert.That(quarantined.QuarantineReason,
                Is.EqualTo("authorized publication lease expired; manual recovery required"));
        });

        await using var context = new SqliteServerDbContext(options);
        var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
        Assert.That(career.Status, Is.EqualTo(DbLuaMCharacterCareerStatus.Quarantined));
    }

    [Test]
    public async Task AuthorizationRejectsExpiredLeaseButExactRollbackStillReopensPreparedSnapshot()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Cryo Authorization Expiry"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claim = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claim);
        var completionOperationId = Guid.NewGuid();
        var prepared = await db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
            completionOperationId,
            userId,
            profileId,
            0,
            stored.SnapshotId.Value,
            claimed.Revision!.Value,
            claim.LeaseId,
            DateTime.UtcNow));
        await ExpirePresenceLeaseAsync(options, claim.LeaseId);
        var afterExpiry = DateTime.UtcNow;
        var authorization = await db.AuthorizeLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoAuthorizePublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision!.Value,
                claim.LeaseId,
                afterExpiry));
        var rollback = await db.RollbackLuaMDeepCryoPublicationAsync(
            new LuaMDeepCryoRollbackPublicationRequest(
                Guid.NewGuid(),
                completionOperationId,
                userId,
                profileId,
                0,
                stored.SnapshotId.Value,
                prepared.Revision.Value,
                claim.LeaseId,
                "authorization lease expired before exposure",
                afterExpiry.AddSeconds(1)));

        Assert.Multiple(() =>
        {
            Assert.That(authorization.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LeaseConflict));
            Assert.That(authorization.Revision, Is.EqualTo(prepared.Revision));
            Assert.That(authorization.LeaseId, Is.EqualTo(claim.LeaseId));
            Assert.That(rollback.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(rollback.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(rollback.Revision, Is.EqualTo(prepared.Revision + 1));
            Assert.That(rollback.Snapshot?.LeaseId, Is.Null);
        });
    }

    [Test]
    public async Task StaleStoreRequestCannotRebaseAfterWinnerAcknowledgement()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"luam-cryo-store-race-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var winnerDb = NewServerDb(() => options, inMemory: false);
            var userId = new NetUserId(Guid.NewGuid());
            await winnerDb.InitPrefsAsync(userId, NewProfile("Cryo Store CAS Winner"));
            var profileId = (await winnerDb.GetCharacterIdAsync(userId, 0))!.Value;

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL";
                Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("wal").IgnoreCase);
            }

            // A separate ServerDbBase instance has its own context/semaphore and
            // exercises the same database-only CAS boundary as another process.
            var loserDb = NewServerDb(() => options, inMemory: false);
            var playableAuthority = await EnsurePlayableAuthorityAsync(
                winnerDb,
                userId,
                profileId,
                DateTime.UtcNow);
            var precondition = await winnerDb.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
            Assert.Multiple(() =>
            {
                Assert.That(precondition, Is.Not.Null);
                Assert.That(precondition!.ActiveSnapshot, Is.Null);
            });

            var storedAt = DateTime.UtcNow;
            var winnerRequest = NewStoreRequest(
                userId,
                profileId,
                storedAt,
                precondition!.LifecycleRevision,
                playableAuthority.LeaseId);
            var loserRequest = NewStoreRequest(
                userId,
                profileId,
                storedAt,
                precondition.LifecycleRevision,
                playableAuthority.LeaseId);

            var winner = await winnerDb.StoreLuaMDeepCryoSnapshotAsync(winnerRequest);
            Assert.That(winner.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            var claimPrecondition = await winnerDb.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
            Assert.Multiple(() =>
            {
                Assert.That(claimPrecondition, Is.Not.Null);
                Assert.That(claimPrecondition!.ActiveSnapshot?.Id, Is.EqualTo(winner.SnapshotId));
            });
            var claimRequest = NewClaimRequest(
                userId,
                profileId,
                winner.SnapshotId!.Value,
                winner.Revision!.Value,
                claimPrecondition!.LifecycleRevision);
            var staleClaimRequest = NewClaimRequest(
                userId,
                profileId,
                winner.SnapshotId.Value,
                winner.Revision.Value,
                claimPrecondition.LifecycleRevision);
            var claimed = await winnerDb.ClaimLuaMDeepCryoRestoreAsync(claimRequest);
            var completionOperationId = Guid.NewGuid();
            var prepared = await winnerDb.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
                completionOperationId,
                userId,
                profileId,
                0,
                winner.SnapshotId.Value,
                claimed.Revision!.Value,
                claimRequest.LeaseId,
                DateTime.UtcNow));
            var authorizationOperationId = Guid.NewGuid();
            var authorized = await winnerDb.AuthorizeLuaMDeepCryoPublicationAsync(
                new LuaMDeepCryoAuthorizePublicationRequest(
                    authorizationOperationId,
                    completionOperationId,
                    userId,
                    profileId,
                    0,
                    winner.SnapshotId.Value,
                    prepared.Revision!.Value,
                    claimRequest.LeaseId,
                    DateTime.UtcNow));
            var acknowledged = await winnerDb.AcknowledgeLuaMDeepCryoPublicationAsync(
                new LuaMDeepCryoAcknowledgePublicationRequest(
                    Guid.NewGuid(),
                    authorizationOperationId,
                    userId,
                    profileId,
                    0,
                    winner.SnapshotId.Value,
                    authorized.Revision!.Value,
                    claimRequest.LeaseId,
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddMinutes(5)));

            Assert.Multiple(() =>
            {
                Assert.That(claimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(prepared.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(authorized.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(acknowledged.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(acknowledged.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(acknowledged.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(acknowledged.Snapshot?.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(acknowledged.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
            });
            Assert.That(
                await winnerDb.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0),
                Is.Null,
                "Terminal ACK must hide the winner from the semantic active-snapshot query before the loser resumes.");

            // This request has the lifecycle and authority token observed before
            // the winning Store. It must not be allowed to adopt the later
            // terminal lifecycle merely because no semantic active snapshot remains.
            var loser = await loserDb.StoreLuaMDeepCryoSnapshotAsync(loserRequest);
            var loserExactRetry = await loserDb.StoreLuaMDeepCryoSnapshotAsync(loserRequest);
            var staleClaim = await loserDb.ClaimLuaMDeepCryoRestoreAsync(staleClaimRequest);
            var staleClaimExactRetry = await loserDb.ClaimLuaMDeepCryoRestoreAsync(staleClaimRequest);
            Assert.Multiple(() =>
            {
                Assert.That(loser.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict),
                    "A stale Store must never rebase after another lifecycle advanced and became terminal.");
                Assert.That(loser.SnapshotId, Is.EqualTo(winner.SnapshotId));
                Assert.That(loser.Revision, Is.EqualTo(acknowledged.Revision));
                Assert.That(loser.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(loser.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(loser.Snapshot, Is.Not.Null,
                    "Historical terminal evidence must remain attached to the authoritative conflict result.");
                Assert.That(loser.Snapshot!.Id, Is.EqualTo(winner.SnapshotId));
                Assert.That(loser.Snapshot.Revision, Is.EqualTo(acknowledged.Revision));
                Assert.That(loser.Snapshot.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(loser.Snapshot.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(loser.Authority, Is.EqualTo(acknowledged.Authority));
                Assert.That(loserExactRetry.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict),
                    "The immutable request token must reject every later exact retry instead of adopting the new revision.");
                Assert.That(loserExactRetry.SnapshotId, Is.EqualTo(winner.SnapshotId));
                Assert.That(loserExactRetry.Revision, Is.EqualTo(acknowledged.Revision));
                Assert.That(loserExactRetry.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(loserExactRetry.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(staleClaim.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict),
                    "A Claim token acquired before the winning restore must not rebase after terminal ACK.");
                Assert.That(staleClaim.SnapshotId, Is.EqualTo(winner.SnapshotId));
                Assert.That(staleClaim.Revision, Is.EqualTo(acknowledged.Revision));
                Assert.That(staleClaim.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(staleClaim.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(staleClaim.Snapshot?.LeaseId, Is.EqualTo(claimRequest.LeaseId));
                Assert.That(staleClaimExactRetry.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict));
                Assert.That(staleClaimExactRetry.SnapshotId, Is.EqualTo(winner.SnapshotId));
                Assert.That(staleClaimExactRetry.Revision, Is.EqualTo(acknowledged.Revision));
            });

            await using var context = new SqliteServerDbContext(options);
            Assert.Multiple(() =>
            {
                Assert.That(context.LuaMDeepCryoSnapshots.Count(), Is.EqualTo(1));
                Assert.That(context.LuaMDeepCryoOperations.Count(value =>
                    value.Kind == DbLuaMDeepCryoOperationKind.Store), Is.EqualTo(1));
                Assert.That(context.LuaMCharacterPresenceLeases.Count(), Is.EqualTo(1));
                Assert.That(context.Profile.Single(value => value.Id == profileId).LifecycleRevision,
                    Is.EqualTo(claimPrecondition.LifecycleRevision + 4),
                    "Claim, PREPARE, AUTH, and ACK must advance the immutable claim epoch exactly four times.");
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Test]
    public async Task ExpiredFreshTombstoneArchivesExactlyAndFutureRecoveryCannotStealRestoreClaim()
    {
        await using var connection = await OpenSqliteAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = NewServerDb(() => options, inMemory: true);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Presence Expiry"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var initial = await db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
        var reservedAt = DateTime.UtcNow;
        var reserved = await db.ReserveLuaMCharacterPresenceAsync(new LuaMCharacterPresenceReserveRequest(
            Guid.NewGuid(),
            userId,
            profileId,
            0,
            Guid.NewGuid(),
            "expiry-owner",
            401,
            reservedAt,
            reservedAt.AddMinutes(5),
            initial!.LifecycleRevision));
        Assert.That(reserved.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

        await using (var context = new SqliteServerDbContext(options))
        {
            var lease = await context.LuaMCharacterPresenceLeases.SingleAsync();
            var expiredAt = DateTime.UtcNow.AddMinutes(-1);
            lease.AcquiredAtUtc = expiredAt.AddMinutes(-2);
            lease.RenewedAtUtc = expiredAt.AddMinutes(-1);
            lease.ExpiresAtUtc = expiredAt;
            await context.SaveChangesAsync();
        }

        Assert.That(
            await db.RecoverExpiredLuaMDeepCryoLeasesAsync(DateTime.UtcNow.AddYears(10)),
            Is.Zero,
            "Background recovery must leave expired FreshReserved tombstones for exact reclaim/archive.");
        await db.SaveCharacterSlotAsync(userId, null, 0);

        await using (var context = new SqliteServerDbContext(options))
        {
            var archived = await context.Profile.SingleAsync(value => value.Id == profileId);
            Assert.Multiple(() =>
            {
                Assert.That(archived.IsArchived, Is.True);
                Assert.That(archived.LifecycleRevision,
                    Is.EqualTo(reserved.Authority!.AuthorityLifecycleRevision + 1));
                Assert.That(context.LuaMCharacterPresenceLeases.Any(), Is.False);
            });
        }

        Assert.That(await db.RestoreArchivedCharacterAsync(userId, profileId, 0), Is.True);
        var stored = await db.StoreLuaMDeepCryoSnapshotAsync(
            await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));
        var claimRequest = await NewClaimRequestAsync(
            db,
            userId,
            profileId,
            stored.SnapshotId!.Value,
            stored.Revision!.Value);
        var claimed = await db.ClaimLuaMDeepCryoRestoreAsync(claimRequest);
        Assert.That(claimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

        var recovered = await db.RecoverExpiredLuaMDeepCryoLeasesAsync(DateTime.UtcNow.AddYears(10));
        var stillRestoring = await db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, 0);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero,
                "Caller-supplied future time must be capped by trusted server time.");
            Assert.That(stillRestoring?.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Restoring));
            Assert.That(stillRestoring?.Authority?.Phase,
                Is.EqualTo(DbLuaMCharacterPresencePhase.RestoreClaim));
        });
    }

    [Test]
    public async Task ConcurrentRenewAndReclaimCommitExactlyOneCurrentAuthority()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"luam-presence-renew-reclaim-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var firstManager = NewServerDb(() => options, inMemory: false);
            var secondManager = NewServerDb(() => options, inMemory: false);
            var userId = new NetUserId(Guid.NewGuid());
            await firstManager.InitPrefsAsync(userId, NewProfile("Presence Renew Reclaim CAS"));
            var profileId = (await firstManager.GetCharacterIdAsync(userId, 0))!.Value;
            var published = await EnsurePlayableAuthorityAsync(
                firstManager,
                userId,
                profileId,
                DateTime.UtcNow);
            await ExpirePresenceLeaseAsync(options, published.LeaseId);
            var expired = await firstManager.GetLuaMCharacterPresenceAuthorityAsync(userId, profileId, 0);
            Assert.That(expired, Is.Not.Null);

            var renewedAt = DateTime.UtcNow;
            var renewRequest = new LuaMCharacterPresenceRenewRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                expired!.LeaseId,
                expired.Revision,
                expired.AuthorityLifecycleRevision,
                renewedAt,
                renewedAt.AddMinutes(5));
            var newLeaseId = Guid.NewGuid();
            var reclaimRequest = new LuaMCharacterPresenceReclaimRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                expired.LeaseId,
                newLeaseId,
                expired.Phase,
                expired.SnapshotId,
                expired.Revision,
                expired.AuthorityLifecycleRevision,
                "presence-race-reclaimer",
                777,
                renewedAt,
                renewedAt.AddMinutes(5));

            var results = await Task.WhenAll(
                firstManager.RenewLuaMCharacterPresenceAsync(renewRequest),
                secondManager.ReclaimLuaMCharacterPresenceAsync(reclaimRequest));
            var renewResult = results[0];
            var reclaimResult = results[1];
            var current = await firstManager.GetLuaMCharacterPresenceAuthorityAsync(userId, profileId, 0);
            Assert.That(results.Count(value => value.Status == LuaMDeepCryoWriteStatus.Success), Is.EqualTo(1));
            var winnerReplay = renewResult.Status == LuaMDeepCryoWriteStatus.Success
                ? await secondManager.RenewLuaMCharacterPresenceAsync(renewRequest)
                : await firstManager.ReclaimLuaMCharacterPresenceAsync(reclaimRequest);

            Assert.Multiple(() =>
            {
                Assert.That(results.Single(value => value.Status != LuaMDeepCryoWriteStatus.Success).Status,
                    Is.AnyOf(
                        LuaMDeepCryoWriteStatus.LeaseConflict,
                        LuaMDeepCryoWriteStatus.RevisionConflict,
                        LuaMDeepCryoWriteStatus.LifecycleConflict));
                Assert.That(winnerReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
                Assert.That(current, Is.Not.Null);
                Assert.That(current!.Revision, Is.EqualTo(expired.Revision + 1));
                Assert.That(current.AuthorityLifecycleRevision,
                    Is.EqualTo(renewResult.Status == LuaMDeepCryoWriteStatus.Success
                        ? expired.AuthorityLifecycleRevision
                        : expired.AuthorityLifecycleRevision + 1));
                Assert.That(current.LeaseId,
                    Is.EqualTo(renewResult.Status == LuaMDeepCryoWriteStatus.Success
                        ? expired.LeaseId
                        : newLeaseId));
                Assert.That(current.Phase,
                    Is.EqualTo(renewResult.Status == LuaMDeepCryoWriteStatus.Success
                        ? DbLuaMCharacterPresencePhase.Playable
                        : DbLuaMCharacterPresencePhase.FreshReserved));
            });

            await using var context = new SqliteServerDbContext(options);
            var profile = await context.Profile.SingleAsync(value => value.Id == profileId);
            var leaseCount = await context.LuaMCharacterPresenceLeases.CountAsync();
            var operationCount = await context.LuaMCharacterPresenceOperations.CountAsync();
            Assert.Multiple(() =>
            {
                Assert.That(leaseCount, Is.EqualTo(1));
                Assert.That(profile.LifecycleRevision, Is.EqualTo(current!.AuthorityLifecycleRevision));
                Assert.That(operationCount,
                    Is.EqualTo(reclaimResult.Status == LuaMDeepCryoWriteStatus.Success ? 3 : 2));
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Test]
    public async Task DurablePresenceReplayReclaimAndStoreConsumeOnlyCurrentAuthority()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"luam-presence-cas-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var firstManager = NewServerDb(() => options, inMemory: false);
            var secondManager = NewServerDb(() => options, inMemory: false);
            var userId = new NetUserId(Guid.NewGuid());
            await firstManager.InitPrefsAsync(userId, NewProfile("Presence CAS"));
            var profileId = (await firstManager.GetCharacterIdAsync(userId, 0))!.Value;
            var initial = await firstManager.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
            Assert.That(initial, Is.Not.Null);

            var reservedAt = DateTime.UtcNow;
            var reserveRequest = new LuaMCharacterPresenceReserveRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                Guid.NewGuid(),
                "presence-manager-a",
                301,
                reservedAt,
                reservedAt.AddMinutes(5),
                initial!.LifecycleRevision);
            var reserved = await firstManager.ReserveLuaMCharacterPresenceAsync(reserveRequest);
            var reserveReplay = await secondManager.ReserveLuaMCharacterPresenceAsync(reserveRequest);
            Assert.Multiple(() =>
            {
                Assert.That(reserved.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(reserved.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.FreshReserved));
                Assert.That(reserveReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
                Assert.That(reserveReplay.Authority, Is.EqualTo(reserved.Authority));
            });

            var publishRequest = new LuaMCharacterPresencePublishRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                reserved.Authority!.LeaseId,
                reserved.Authority.Revision,
                reserved.Authority.AuthorityLifecycleRevision,
                reservedAt.AddSeconds(1),
                reservedAt.AddMinutes(6));
            var published = await firstManager.PublishLuaMCharacterPresenceAsync(publishRequest);
            var publishReplay = await secondManager.PublishLuaMCharacterPresenceAsync(publishRequest);
            var supersededReserveReplay = await secondManager.ReserveLuaMCharacterPresenceAsync(reserveRequest);
            Assert.Multiple(() =>
            {
                Assert.That(published.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(published.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
                Assert.That(publishReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
                Assert.That(publishReplay.Authority, Is.EqualTo(published.Authority));
                Assert.That(supersededReserveReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict));
                Assert.That(supersededReserveReplay.Authority, Is.EqualTo(published.Authority),
                    "Historical reserve proof must return the current authority, never its stale ticket.");
            });

            var renewRequest = new LuaMCharacterPresenceRenewRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                published.Authority!.LeaseId,
                published.Authority.Revision,
                published.Authority.AuthorityLifecycleRevision,
                reservedAt.AddSeconds(2),
                reservedAt.AddMinutes(7));
            var renewed = await firstManager.RenewLuaMCharacterPresenceAsync(renewRequest);
            var renewReplay = await secondManager.RenewLuaMCharacterPresenceAsync(renewRequest);
            var renewIdentityConflict = await secondManager.RenewLuaMCharacterPresenceAsync(renewRequest with
            {
                LeaseExpiresAtUtc = renewRequest.LeaseExpiresAtUtc.AddMinutes(1),
            });
            Assert.Multiple(() =>
            {
                Assert.That(renewed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(renewReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
                Assert.That(renewReplay.Authority, Is.EqualTo(renewed.Authority));
                Assert.That(renewIdentityConflict.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.IdentityConflict));
            });

            await using (var context = new SqliteServerDbContext(options))
            {
                var lease = await context.LuaMCharacterPresenceLeases.SingleAsync();
                var expiredAt = DateTime.UtcNow.AddMinutes(-1);
                lease.AcquiredAtUtc = expiredAt.AddMinutes(-2);
                lease.RenewedAtUtc = expiredAt.AddMinutes(-1);
                lease.ExpiresAtUtc = expiredAt;
                await context.SaveChangesAsync();
                Assert.That(await context.LuaMCharacterPresenceOperations.CountAsync(), Is.EqualTo(2),
                    "Renewal replay evidence must remain bounded on the lease row.");
            }

            Assert.That(
                await firstManager.RecoverExpiredLuaMDeepCryoLeasesAsync(DateTime.UtcNow.AddYears(10)),
                Is.Zero,
                "Future caller time must not recover Playable/Fresh authority, and server time caps restore recovery.");
            var expiredAuthority = await firstManager.GetLuaMCharacterPresenceAuthorityAsync(userId, profileId, 0);
            Assert.That(expiredAuthority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));

            var reclaimRequest = new LuaMCharacterPresenceReclaimRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                expiredAuthority!.LeaseId,
                Guid.NewGuid(),
                expiredAuthority.Phase,
                expiredAuthority.SnapshotId,
                expiredAuthority.Revision,
                expiredAuthority.AuthorityLifecycleRevision,
                "presence-manager-b",
                302,
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(5));
            var reclaimed = await secondManager.ReclaimLuaMCharacterPresenceAsync(reclaimRequest);
            var reclaimReplay = await firstManager.ReclaimLuaMCharacterPresenceAsync(reclaimRequest);
            var lateRenew = await firstManager.RenewLuaMCharacterPresenceAsync(renewRequest);
            var stalePublishReplay = await firstManager.PublishLuaMCharacterPresenceAsync(publishRequest);
            Assert.Multiple(() =>
            {
                Assert.That(reclaimed.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(reclaimed.Authority?.LeaseId, Is.EqualTo(reclaimRequest.NewLeaseId));
                Assert.That(reclaimed.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.FreshReserved));
                Assert.That(reclaimed.Authority?.SnapshotId, Is.Null);
                Assert.That(reclaimReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
                Assert.That(lateRenew.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LeaseConflict));
                Assert.That(lateRenew.Authority, Is.EqualTo(reclaimed.Authority));
                Assert.That(stalePublishReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict));
                Assert.That(stalePublishReplay.Authority, Is.EqualTo(reclaimed.Authority));
            });

            var secondPublish = await secondManager.PublishLuaMCharacterPresenceAsync(
                new LuaMCharacterPresencePublishRequest(
                    Guid.NewGuid(),
                    userId,
                    profileId,
                    0,
                    reclaimed.Authority!.LeaseId,
                    reclaimed.Authority.Revision,
                    reclaimed.Authority.AuthorityLifecycleRevision,
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddMinutes(5)));
            Assert.That(secondPublish.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

            var storeAt = DateTime.UtcNow;
            var wrongTokenStore = NewStoreRequest(
                userId,
                profileId,
                storeAt,
                secondPublish.Authority!.AuthorityLifecycleRevision,
                Guid.NewGuid());
            var rejectedStore = await firstManager.StoreLuaMDeepCryoSnapshotAsync(wrongTokenStore);
            Assert.Multiple(() =>
            {
                Assert.That(rejectedStore.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LeaseConflict));
                Assert.That(rejectedStore.Authority, Is.EqualTo(secondPublish.Authority));
            });

            await using (var context = new SqliteServerDbContext(options))
            {
                Assert.That(await context.LuaMDeepCryoSnapshots.AnyAsync(), Is.False);
                Assert.That(await context.LuaMCharacterPresenceLeases.CountAsync(), Is.EqualTo(1));
            }

            var exactStore = wrongTokenStore with
            {
                OperationId = Guid.NewGuid(),
                ExpectedPresenceLeaseId = secondPublish.Authority.LeaseId,
            };
            var stored = await secondManager.StoreLuaMDeepCryoSnapshotAsync(exactStore);
            var storeReplay = await firstManager.StoreLuaMDeepCryoSnapshotAsync(exactStore);
            Assert.Multiple(() =>
            {
                Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(storeReplay.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.AlreadyProcessed));
            });

            await using (var context = new SqliteServerDbContext(options))
            {
                Assert.That(await context.LuaMDeepCryoSnapshots.CountAsync(), Is.EqualTo(1));
                Assert.That(await context.LuaMCharacterPresenceLeases.AnyAsync(), Is.False,
                    "Playable token consumption and Stored authority must commit atomically.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
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
                await NewStoreRequestAsync(db, userId, profileId, DateTime.UtcNow));

            var first = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId!.Value, 0);
            var second = await NewClaimRequestAsync(db, userId, profileId, stored.SnapshotId.Value, 0);
            var results = await Task.WhenAll(
                db.ClaimLuaMDeepCryoRestoreAsync(first),
                db.ClaimLuaMDeepCryoRestoreAsync(second));

            Assert.That(results.Count(value => value.Status == LuaMDeepCryoWriteStatus.Success), Is.EqualTo(1));
            Assert.That(results.Single(value => value.Status != LuaMDeepCryoWriteStatus.Success).Status,
                Is.AnyOf(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
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

    private static async Task<LuaMDeepCryoStoreRequest> NewStoreRequestAsync(
        ServerDbBase db,
        NetUserId userId,
        int profileId,
        DateTime storedAtUtc)
    {
        var authority = await EnsurePlayableAuthorityAsync(db, userId, profileId, storedAtUtc);
        var precondition = await db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
        Assert.That(precondition, Is.Not.Null);
        Assert.That(precondition!.Authority, Is.EqualTo(authority));
        return NewStoreRequest(
            userId,
            profileId,
            storedAtUtc,
            precondition.LifecycleRevision,
            authority.LeaseId);
    }

    private static LuaMDeepCryoStoreRequest NewStoreRequest(
        NetUserId userId,
        int profileId,
        DateTime storedAtUtc,
        long expectedLifecycleRevision,
        Guid expectedPresenceLeaseId)
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
            storedAtUtc,
            expectedLifecycleRevision,
            expectedPresenceLeaseId);
    }

    private static async Task<LuaMCharacterPresenceAuthorityRecord> EnsurePlayableAuthorityAsync(
        ServerDbBase db,
        NetUserId userId,
        int profileId,
        DateTime nowUtc)
    {
        var precondition = await db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
        Assert.That(precondition, Is.Not.Null);
        if (precondition!.Authority is { Phase: DbLuaMCharacterPresencePhase.Playable } playable)
            return playable;

        LuaMCharacterPresenceAuthorityRecord reserved;
        if (precondition.Authority is { Phase: DbLuaMCharacterPresencePhase.FreshReserved } existingReserved)
        {
            reserved = existingReserved;
        }
        else
        {
            Assert.That(precondition.Authority, Is.Null);
            Assert.That(precondition.ActiveSnapshot, Is.Null);
            var reserve = await db.ReserveLuaMCharacterPresenceAsync(new LuaMCharacterPresenceReserveRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                0,
                Guid.NewGuid(),
                "integration-test",
                100,
                nowUtc,
                nowUtc.AddMinutes(5),
                precondition.LifecycleRevision));
            Assert.That(reserve.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            reserved = reserve.Authority!;
        }

        var publishedAt = nowUtc < reserved.RenewedAtUtc ? reserved.RenewedAtUtc : nowUtc;
        var publish = await db.PublishLuaMCharacterPresenceAsync(new LuaMCharacterPresencePublishRequest(
            Guid.NewGuid(),
            userId,
            profileId,
            0,
            reserved.LeaseId,
            reserved.Revision,
            reserved.AuthorityLifecycleRevision,
            publishedAt,
            publishedAt.AddMinutes(5)));
        Assert.That(publish.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
        return publish.Authority!;
    }

    private static async Task<LuaMDeepCryoClaimRequest> NewClaimRequestAsync(
        ServerDbBase db,
        NetUserId userId,
        int profileId,
        long snapshotId,
        long expectedRevision)
    {
        var precondition = await db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, 0);
        Assert.That(precondition, Is.Not.Null);
        return NewClaimRequest(
            userId,
            profileId,
            snapshotId,
            expectedRevision,
            precondition!.LifecycleRevision);
    }

    private static LuaMDeepCryoClaimRequest NewClaimRequest(
        NetUserId userId,
        int profileId,
        long snapshotId,
        long expectedRevision,
        long expectedLifecycleRevision)
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
            claimedAt.AddMinutes(5),
            expectedLifecycleRevision);
    }

    private sealed class StoreReadRace
    {
        private readonly TaskCompletionSource<bool> _bothObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseLoser =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public Task BothObserved => _bothObserved.Task;

        public Task WinnerObservedAsync(CancellationToken cancel)
            => ArriveAsync(waitForLoserRelease: false, cancel);

        public Task LoserObservedAsync(CancellationToken cancel)
            => ArriveAsync(waitForLoserRelease: true, cancel);

        public void ReleaseLoser()
            => _releaseLoser.TrySetResult(true);

        public void ReleaseAll()
        {
            _bothObserved.TrySetResult(true);
            _releaseLoser.TrySetResult(true);
        }

        private async Task ArriveAsync(bool waitForLoserRelease, CancellationToken cancel)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _bothObserved.TrySetResult(true);

            await _bothObserved.Task.WaitAsync(cancel);
            if (waitForLoserRelease)
                await _releaseLoser.Task.WaitAsync(cancel);
        }
    }

    private static async Task SeedLegacyRestoreLeaseAsync(
        SqliteConnection connection,
        NetUserId userId,
        int preferenceId,
        int profileId,
        long snapshotId,
        Guid leaseId,
        DbLuaMDeepCryoSnapshotStatus status,
        long snapshotRevision,
        long lifecycleRevision,
        string? completionReason,
        bool includeCompletion)
    {
        var nowUtc = DateTime.UtcNow;
        var payload = new byte[] { 0x4C, 0x75, 0x61, 0x4D };

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE profile SET lifecycle_revision = $revision WHERE profile_id = $profile";
            command.Parameters.AddWithValue("$revision", lifecycleRevision);
            command.Parameters.AddWithValue("$profile", profileId);
            Assert.That(await command.ExecuteNonQueryAsync(), Is.EqualTo(1));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO luam_deep_cryo_snapshot (
                    lua_m_deep_cryo_snapshots_id,
                    player_user_id,
                    preference_id,
                    profile_id,
                    slot,
                    source_round_id,
                    last_restore_round_id,
                    status,
                    revision,
                    format_version,
                    payload,
                    payload_hash,
                    payload_size_bytes,
                    entity_count,
                    source_build_version,
                    prototype_manifest_hash,
                    stored_at_utc,
                    updated_at_utc,
                    consumed_at_utc,
                    quarantined_at_utc,
                    quarantine_reason)
                VALUES (
                    $snapshot,
                    $user,
                    $preference,
                    $profile,
                    0,
                    500,
                    501,
                    $status,
                    $revision,
                    1,
                    $payload,
                    $payloadHash,
                    $payloadSize,
                    1,
                    'migration-test',
                    $manifestHash,
                    $storedAt,
                    $updatedAt,
                    $consumedAt,
                    NULL,
                    NULL)
                """;
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$user", userId.UserId);
            command.Parameters.AddWithValue("$preference", preferenceId);
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$status", (int) status);
            command.Parameters.AddWithValue("$revision", snapshotRevision);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$payloadHash", Convert.ToHexString(SHA256.HashData(payload)));
            command.Parameters.AddWithValue("$payloadSize", payload.Length);
            command.Parameters.AddWithValue("$manifestHash", new string('B', 64));
            command.Parameters.AddWithValue("$storedAt", nowUtc.AddMinutes(-2));
            command.Parameters.AddWithValue("$updatedAt", nowUtc.AddMinutes(-1));
            command.Parameters.AddWithValue(
                "$consumedAt",
                status == DbLuaMDeepCryoSnapshotStatus.Consumed
                    ? (object) nowUtc.AddMinutes(-1)
                    : DBNull.Value);
            Assert.That(await command.ExecuteNonQueryAsync(), Is.EqualTo(1));
        }

        if (includeCompletion)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO luam_deep_cryo_operation (
                    operation_id,
                    operation_identity_key,
                    snapshot_id,
                    profile_id,
                    kind,
                    result_status,
                    result_revision,
                    lease_id,
                    round_id,
                    reason,
                    created_at_utc)
                VALUES (
                    $operation,
                    $identity,
                    $snapshot,
                    $profile,
                    2,
                    2,
                    $revision,
                    $lease,
                    501,
                    $reason,
                    $createdAt)
                """;
            command.Parameters.AddWithValue("$operation", Guid.NewGuid());
            command.Parameters.AddWithValue("$identity", new string('C', 64));
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$revision", snapshotRevision);
            command.Parameters.AddWithValue("$lease", leaseId);
            command.Parameters.AddWithValue("$reason", (object?) completionReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", nowUtc.AddSeconds(-30));
            Assert.That(await command.ExecuteNonQueryAsync(), Is.EqualTo(1));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO luam_character_presence_lease (
                    profile_id,
                    snapshot_id,
                    lease_id,
                    server_instance_id,
                    restore_round_id,
                    acquired_at_utc,
                    renewed_at_utc,
                    expires_at_utc,
                    revision)
                VALUES (
                    $profile,
                    $snapshot,
                    $lease,
                    'migration-test',
                    501,
                    $acquiredAt,
                    $renewedAt,
                    $expiresAt,
                    0)
                """;
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$lease", leaseId);
            command.Parameters.AddWithValue("$acquiredAt", nowUtc.AddMinutes(-1));
            command.Parameters.AddWithValue("$renewedAt", nowUtc);
            command.Parameters.AddWithValue("$expiresAt", nowUtc.AddMinutes(5));
            Assert.That(await command.ExecuteNonQueryAsync(), Is.EqualTo(1));
        }
    }

    private static async Task ExpirePresenceLeaseAsync(
        DbContextOptions<SqliteServerDbContext> options,
        Guid leaseId)
    {
        await using var context = new SqliteServerDbContext(options);
        var lease = await context.LuaMCharacterPresenceLeases
            .SingleAsync(value => value.LeaseId == leaseId);
        var expiredAt = DateTime.UtcNow.AddSeconds(-1);
        lease.AcquiredAtUtc = expiredAt.AddMinutes(-2);
        lease.RenewedAtUtc = expiredAt.AddMinutes(-1);
        lease.ExpiresAtUtc = expiredAt;
        await context.SaveChangesAsync();
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
