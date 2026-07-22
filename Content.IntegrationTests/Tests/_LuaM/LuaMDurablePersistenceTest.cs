#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using Content.Client.PDA;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
public sealed class LuaMDurablePersistenceTest
{
    [Test]
    public async Task ExpeditionCreateMutationReplayCasAndArchiveSurviveReload()
    {
        await using var connection = await OpenSqliteAsync();
        var db = NewServerDb(connection);
        var now = DateTime.UtcNow;
        var create = NewExpedition(now);
        var duplicateCreate = await db.CreateOrGetLuaMExpeditionAsync(create with
        {
            CampaignId = "invalid-duplicate-regions",
            Regions = [create.Regions[0], create.Regions[0]],
        });
        var nonUtcCreate = await db.CreateOrGetLuaMExpeditionAsync(create with
        {
            CampaignId = "invalid-local-time",
            CreatedAtUtc = DateTime.SpecifyKind(now, DateTimeKind.Local),
        });

        var first = await db.CreateOrGetLuaMExpeditionAsync(create);
        var replayCreate = await db.CreateOrGetLuaMExpeditionAsync(create);
        var loaded = await db.LoadLuaMExpeditionAsync(create.CampaignId, create.ExpeditionId);

        Assert.Multiple(() =>
        {
            Assert.That(duplicateCreate.Status, Is.EqualTo(LuaMExpeditionWriteStatus.InvalidRequest));
            Assert.That(nonUtcCreate.Status, Is.EqualTo(LuaMExpeditionWriteStatus.InvalidRequest));
            Assert.That(first.Status, Is.EqualTo(LuaMExpeditionWriteStatus.Success));
            Assert.That(replayCreate.Status, Is.EqualTo(LuaMExpeditionWriteStatus.AlreadyProcessed));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Manifest.Seed, Is.EqualTo(ulong.MaxValue));
            Assert.That(loaded.Regions, Has.Count.EqualTo(1));
            Assert.That(loaded.Sites, Has.Count.EqualTo(1));
        });

        var region = loaded!.Regions.Single();
        var operationId = Guid.NewGuid();
        var mutation = NewMutation(first.ManifestId!.Value, region.Id, 0, operationId, now.AddMinutes(1));
        var append = await db.AppendLuaMExpeditionMutationAsync(mutation);
        var replay = await db.AppendLuaMExpeditionMutationAsync(mutation);
        var replayIdentityConflict = await db.AppendLuaMExpeditionMutationAsync(mutation with
        {
            SourceInstanceId = "same-checkpoint-different-delta",
        });
        var duplicateSnapshot = await db.AppendLuaMExpeditionMutationAsync(mutation with
        {
            CheckpointOperationId = Guid.NewGuid(),
            ExpectedRegionRevision = 1,
            EntitySnapshots = [mutation.EntitySnapshots[0], mutation.EntitySnapshots[0]],
        });
        var duplicateTombstone = await db.AppendLuaMExpeditionMutationAsync(mutation with
        {
            CheckpointOperationId = Guid.NewGuid(),
            ExpectedRegionRevision = 1,
            Tombstones = [mutation.Tombstones[0], mutation.Tombstones[0]],
        });
        var stale = await db.AppendLuaMExpeditionMutationAsync(mutation with
        {
            CheckpointOperationId = Guid.NewGuid(),
            SourceInstanceId = "container-opened-stale",
        });
        var after = await db.LoadLuaMExpeditionAsync(create.CampaignId, create.ExpeditionId);

        Assert.Multiple(() =>
        {
            Assert.That(append.Status, Is.EqualTo(LuaMExpeditionWriteStatus.Success));
            Assert.That(append.RegionRevision, Is.EqualTo(1));
            Assert.That(replay.Status, Is.EqualTo(LuaMExpeditionWriteStatus.AlreadyProcessed));
            Assert.That(replay.CheckpointId, Is.EqualTo(append.CheckpointId));
            Assert.That(replayIdentityConflict.Status, Is.EqualTo(LuaMExpeditionWriteStatus.IdentityConflict));
            Assert.That(duplicateSnapshot.Status, Is.EqualTo(LuaMExpeditionWriteStatus.InvalidRequest));
            Assert.That(duplicateTombstone.Status, Is.EqualTo(LuaMExpeditionWriteStatus.InvalidRequest));
            Assert.That(stale.Status, Is.EqualTo(LuaMExpeditionWriteStatus.RevisionConflict));
            Assert.That(after!.Deltas, Has.Count.EqualTo(1));
            Assert.That(after.EntitySnapshots, Has.Count.EqualTo(1));
            Assert.That(after.Tombstones, Has.Count.EqualTo(1));
            Assert.That(after.LatestCheckpoint!.OperationId, Is.EqualTo(operationId));
            Assert.That(after.LatestCheckpoint.OperationIdentityKey, Has.Length.EqualTo(64));
        });

        var archived = await db.ArchiveLuaMExpeditionAsync(
            first.ManifestId.Value,
            expectedRevision: 1,
            now.AddMinutes(2));
        var appendAfterArchive = await db.AppendLuaMExpeditionMutationAsync(mutation with
        {
            ExpectedRegionRevision = 1,
            CheckpointOperationId = Guid.NewGuid(),
            SourceInstanceId = "after-archive",
        });

        Assert.Multiple(() =>
        {
            Assert.That(archived.Status, Is.EqualTo(LuaMExpeditionWriteStatus.Success));
            Assert.That(appendAfterArchive.Status, Is.EqualTo(LuaMExpeditionWriteStatus.IdentityConflict));
        });

    }

    [Test]
    public async Task CareerAwardReplaySealAndProfileArchiveKeepOldIdentity()
    {
        await using var connection = await OpenSqliteAsync();
        var db = NewServerDb(connection);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Career Original"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var starts = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);

        var shift = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            42,
            starts,
            starts.AddDays(7),
            null,
            starts));
        var awardRequest = new LuaMCareerAwardRequest(
            42,
            profileId,
            null,
            DbLuaMProgressionCurrency.Career,
            null,
            100,
            "contract",
            "contract-42",
            "completed",
            null,
            1,
            "{}",
            true,
            300,
            20,
            2,
            true,
            starts.AddHours(2));
        var award = await db.RecordLuaMCareerAwardAsync(awardRequest);
        var awardReplay = await db.RecordLuaMCareerAwardAsync(awardRequest);
        var awardReplayConflict = await db.RecordLuaMCareerAwardAsync(awardRequest with
        {
            ActiveMinutesDelta = awardRequest.ActiveMinutesDelta + 1,
        });
        var staleSeal = await db.SealLuaMCampaignShiftAsync(42, 0, starts.AddDays(7));
        var sealedShift = await db.SealLuaMCampaignShiftAsync(
            42,
            award.CurrentRevision!.Value,
            starts.AddDays(7));
        var career = await db.GetLuaMCareerStateAsync(profileId);

        Assert.Multiple(() =>
        {
            Assert.That(shift.Success, Is.True);
            Assert.That(award.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(award.CurrentRevision, Is.EqualTo(1), "An award must fence the shift row.");
            Assert.That(awardReplay.Status, Is.EqualTo(LuaMProgressionWriteStatus.AlreadyProcessed));
            Assert.That(awardReplay.LedgerId, Is.EqualTo(award.LedgerId));
            Assert.That(awardReplayConflict.Status, Is.EqualTo(LuaMProgressionWriteStatus.IdentityConflict));
            Assert.That(staleSeal.Status, Is.EqualTo(LuaMProgressionWriteStatus.RevisionConflict));
            Assert.That(sealedShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(career!.Career.TotalCareerXp, Is.EqualTo(100));
            Assert.That(career.Career.CreditedShiftCount, Is.EqualTo(1));
            Assert.That(career.Participations.Single().FinalCareerXp, Is.EqualTo(100));
            Assert.That(career.Ledger, Has.Count.EqualTo(1));
            Assert.That(career.Ledger[0].OperationIdentityKey, Has.Length.EqualTo(64));
        });

        await db.SaveCharacterSlotAsync(userId, null, 0);
        var replayAfterArchive = await db.RecordLuaMCareerAwardAsync(awardRequest);
        Assert.That(replayAfterArchive.Status, Is.EqualTo(LuaMProgressionWriteStatus.AlreadyProcessed),
            "Committed evidence must resolve before active-profile validation.");

        await db.SaveCharacterSlotAsync(userId, NewProfile("Career Replacement"), 0);
        var replacementId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var archivedCareer = await db.GetLuaMCareerStateAsync(profileId);
        var replacementCareer = await db.GetLuaMCareerStateAsync(replacementId);
        Assert.Multiple(() =>
        {
            Assert.That(replacementId, Is.Not.EqualTo(profileId));
            Assert.That(archivedCareer, Is.Not.Null);
            Assert.That(replacementCareer, Is.Null);
        });

    }

    [Test]
    public async Task AwardValidationRequiresExactReversalAndPlayableCareer()
    {
        await using var connection = await OpenSqliteAsync();
        var db = NewServerDb(connection);
        var userId = new NetUserId(Guid.NewGuid());
        await db.InitPrefsAsync(userId, NewProfile("Career Validation"));
        var profileId = (await db.GetCharacterIdAsync(userId, 0))!.Value;
        var starts = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        var zeroShift = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            0, starts.AddDays(-7), starts, null, starts.AddDays(-7)));
        var sealedZeroShift = await db.SealLuaMCampaignShiftAsync(0, 0, starts);
        var closedShiftNewRun = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            0, starts.AddDays(-7), starts, 100, starts));
        var negativeShift = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            -1, starts.AddDays(-14), starts.AddDays(-7), 99, starts.AddDays(-14)));
        var sealedNegativeShift = await db.SealLuaMCampaignShiftAsync(-1, 0, starts.AddDays(-7));
        var closedShiftRunReplay = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            -1, starts.AddDays(-14), starts.AddDays(-7), 99, starts.AddDays(-7)));
        var attachFenceShift = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            2, starts.AddDays(7), starts.AddDays(14), null, starts.AddDays(7)));
        var attachedRun = await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            2, starts.AddDays(7), starts.AddDays(14), 101, starts.AddDays(7)));
        var staleAttachSeal = await db.SealLuaMCampaignShiftAsync(2, 0, starts.AddDays(14));
        var sealedAttachShift = await db.SealLuaMCampaignShiftAsync(
            2, attachedRun.CurrentRevision!.Value, starts.AddDays(14));
        await db.CreateOrGetLuaMCampaignShiftAsync(new LuaMCampaignShiftCreateRequest(
            41, starts, starts.AddDays(7), null, starts));

        var request = new LuaMCareerAwardRequest(
            41, profileId, null, DbLuaMProgressionCurrency.Career, null, 10,
            "validation", "award-original", "award", null, 1, "{}",
            false, 0, 0, 0, false, starts.AddHours(1));
        var unsafeNegative = await db.RecordLuaMCareerAwardAsync(request with
        {
            Amount = -10,
            SourceInstanceId = "unsafe-negative",
        });
        var invalidCurrency = await db.RecordLuaMCareerAwardAsync(request with
        {
            Currency = (DbLuaMProgressionCurrency) 99,
            SourceInstanceId = "invalid-currency",
        });
        var original = await db.RecordLuaMCareerAwardAsync(request);
        var reversal = await db.RecordLuaMCareerAwardAsync(request with
        {
            Amount = -10,
            SourceInstanceId = "award-reversal",
            AwardCode = "reversal",
            ReversesLedgerId = original.LedgerId,
        });
        var duplicateReversal = await db.RecordLuaMCareerAwardAsync(request with
        {
            Amount = -10,
            SourceInstanceId = "award-reversal-duplicate",
            AwardCode = "reversal-duplicate",
            ReversesLedgerId = original.LedgerId,
        });

        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new SqliteServerDbContext(options))
        {
            var career = await context.LuaMCharacterCareers.SingleAsync(value => value.ProfileId == profileId);
            career.Status = DbLuaMCharacterCareerStatus.Quarantined;
            await context.SaveChangesAsync();
        }

        var quarantined = await db.RecordLuaMCareerAwardAsync(request with
        {
            SourceInstanceId = "quarantined-career",
            AwardCode = "quarantined",
        });

        Assert.Multiple(() =>
        {
            Assert.That(zeroShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(sealedZeroShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(closedShiftNewRun.Status, Is.EqualTo(LuaMProgressionWriteStatus.ShiftNotOpen));
            Assert.That(negativeShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(sealedNegativeShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(closedShiftRunReplay.Status, Is.EqualTo(LuaMProgressionWriteStatus.AlreadyProcessed));
            Assert.That(attachFenceShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(attachedRun.CurrentRevision, Is.EqualTo(1), "Run attachment must fence the shift row.");
            Assert.That(staleAttachSeal.Status, Is.EqualTo(LuaMProgressionWriteStatus.RevisionConflict));
            Assert.That(sealedAttachShift.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(unsafeNegative.Status, Is.EqualTo(LuaMProgressionWriteStatus.InvalidRequest));
            Assert.That(invalidCurrency.Status, Is.EqualTo(LuaMProgressionWriteStatus.InvalidRequest));
            Assert.That(original.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(reversal.Status, Is.EqualTo(LuaMProgressionWriteStatus.Success));
            Assert.That(duplicateReversal.Status, Is.EqualTo(LuaMProgressionWriteStatus.IdentityConflict));
            Assert.That(quarantined.Status, Is.EqualTo(LuaMProgressionWriteStatus.ProfileNotActive));
        });
    }

    [Test]
    public async Task SqliteEnforcesLedgerAppendOnlyRestrictAndOptimisticRevision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        await using var setup = new SqliteServerDbContext(options);
        await setup.Database.MigrateAsync();

        var preference = new Preference
        {
            UserId = Guid.NewGuid(),
            SelectedCharacterSlot = 0,
            AdminOOCColor = "#FFFFFFFF",
        };
        var profile = NewDbProfile(preference);
        setup.AddRange(preference, profile);
        await setup.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var manifest = new LuaMExpeditionManifest
        {
            CampaignId = "constraint-campaign",
            ExpeditionId = "constraint-expedition",
            SeedBits = -1,
            GeneratorVersion = 1,
            PlanHash = "0123456789ABCDEF",
            MinX = 0,
            MinY = 0,
            MaxX = 63,
            MaxY = 63,
            Status = DbLuaMExpeditionStatus.Active,
            PreservationPolicy = DbLuaMExpeditionPreservationPolicy.CampaignPermanent,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var region = new LuaMExpeditionRegion
        {
            ManifestId = 0,
            RegionX = 0,
            RegionY = 0,
            MinX = 0,
            MinY = 0,
            MaxX = 63,
            MaxY = 63,
            Biome = "test",
            EdgeFormatVersion = 1,
            EdgePayload = [],
            EdgePayloadHash = "edge",
        };
        setup.LuaMExpeditionManifests.Add(manifest);
        await setup.SaveChangesAsync();
        region.ManifestId = manifest.Id;
        setup.LuaMExpeditionRegions.Add(region);

        setup.LuaMCampaignShifts.Add(new LuaMCampaignShift
        {
            ShiftPeriodId = 1,
            StartsAtUtc = now,
            EndsAtUtc = now.AddDays(7),
            Status = DbLuaMCampaignShiftStatus.Open,
            CreatedAtUtc = now,
        });
        setup.LuaMCampaignShifts.Add(new LuaMCampaignShift
        {
            ShiftPeriodId = 2,
            StartsAtUtc = now.AddDays(7),
            EndsAtUtc = now.AddDays(14),
            Status = DbLuaMCampaignShiftStatus.Open,
            CreatedAtUtc = now,
        });
        setup.LuaMCharacterCareers.Add(new LuaMCharacterCareer
        {
            ProfileId = profile.Id,
            Status = DbLuaMCharacterCareerStatus.Playable,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        setup.LuaMCareerShiftParticipations.Add(new LuaMCareerShiftParticipation
        {
            ShiftPeriodId = 1,
            ProfileId = profile.Id,
            PreferenceId = preference.Id,
            IsCareerFocus = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        var ledger = new LuaMCareerXpLedger
        {
            ShiftPeriodId = 1,
            ProfileId = profile.Id,
            Currency = DbLuaMProgressionCurrency.Career,
            Amount = 5,
            SourceType = "test",
            SourceInstanceId = "test-1",
            AwardCode = "award",
            IdempotencyKey = new string('A', 64),
            OperationIdentityKey = new string('B', 64),
            RulesetVersion = 1,
            PayloadJson = "{}",
            CreatedAtUtc = now,
        };
        setup.LuaMCareerXpLedger.Add(ledger);
        await setup.SaveChangesAsync();
        setup.ChangeTracker.Clear();

        Assert.That(
            async () => await setup.Database.ExecuteSqlRawAsync(
                "UPDATE luam_career_xp_ledger SET amount = 6 WHERE lua_m_career_xp_ledger_id = {0}", ledger.Id),
            Throws.TypeOf<SqliteException>());
        Assert.That(
            async () => await setup.Database.ExecuteSqlRawAsync(
                "DELETE FROM profile WHERE profile_id = {0}", profile.Id),
            Throws.TypeOf<SqliteException>(),
            "Career foreign keys must make account erasure fail closed.");

        await using var first = new SqliteServerDbContext(options);
        await using var second = new SqliteServerDbContext(options);
        var firstRegion = await first.LuaMExpeditionRegions.SingleAsync(value => value.Id == region.Id);
        var secondRegion = await second.LuaMExpeditionRegions.SingleAsync(value => value.Id == region.Id);
        firstRegion.Revision = 1;
        await first.SaveChangesAsync();
        secondRegion.Revision = 1;
        Assert.That(async () => await second.SaveChangesAsync(), Throws.TypeOf<DbUpdateConcurrencyException>());

        await using var lateAward = new SqliteServerDbContext(options);
        await using var closing = new SqliteServerDbContext(options);
        var lateAwardShift = await lateAward.LuaMCampaignShifts.SingleAsync(value => value.ShiftPeriodId == 1);
        var closingShift = await closing.LuaMCampaignShifts.SingleAsync(value => value.ShiftPeriodId == 1);
        closingShift.Status = DbLuaMCampaignShiftStatus.Closed;
        closingShift.Revision++;
        closingShift.SealedAtUtc = now.AddDays(7);
        await closing.SaveChangesAsync();

        lateAward.LuaMCareerXpLedger.Add(new LuaMCareerXpLedger
        {
            ShiftPeriodId = 1,
            ProfileId = profile.Id,
            Currency = DbLuaMProgressionCurrency.Career,
            Amount = 7,
            SourceType = "test",
            SourceInstanceId = "late-award",
            AwardCode = "late",
            IdempotencyKey = new string('C', 64),
            OperationIdentityKey = new string('D', 64),
            RulesetVersion = 1,
            PayloadJson = "{}",
            CreatedAtUtc = now,
        });
        lateAwardShift.Revision++;
        Assert.That(async () => await lateAward.SaveChangesAsync(), Throws.TypeOf<DbUpdateConcurrencyException>(),
            "The shift CAS fence must roll back a ledger insert that raced with sealing.");

        await using var verification = new SqliteServerDbContext(options);
        Assert.That(await verification.LuaMCareerXpLedger
            .AnyAsync(value => value.IdempotencyKey == new string('C', 64)), Is.False);

        await using var lateRunAttach = new SqliteServerDbContext(options);
        await using var runClosing = new SqliteServerDbContext(options);
        var attachShift = await lateRunAttach.LuaMCampaignShifts.SingleAsync(value => value.ShiftPeriodId == 2);
        var runClosingShift = await runClosing.LuaMCampaignShifts.SingleAsync(value => value.ShiftPeriodId == 2);
        runClosingShift.Status = DbLuaMCampaignShiftStatus.Closed;
        runClosingShift.Revision++;
        runClosingShift.SealedAtUtc = now.AddDays(14);
        await runClosing.SaveChangesAsync();

        lateRunAttach.LuaMCampaignShiftRuns.Add(new LuaMCampaignShiftRun
        {
            ShiftPeriodId = 2,
            RoundId = 77,
            AttachedAtUtc = now.AddDays(8),
        });
        attachShift.Revision++;
        Assert.That(async () => await lateRunAttach.SaveChangesAsync(), Throws.TypeOf<DbUpdateConcurrencyException>(),
            "The shift CAS fence must roll back a run attachment that raced with sealing.");

        await using var runVerification = new SqliteServerDbContext(options);
        Assert.That(await runVerification.LuaMCampaignShiftRuns.AnyAsync(value => value.RoundId == 77), Is.False);
    }

    [Test]
    public void ProviderModelsMatchAndClientAssembliesDoNotExposeServerPersistence()
    {
        var sqliteOptions = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var postgresOptions = new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .Options;
        using var sqlite = new SqliteServerDbContext(sqliteOptions);
        using var postgres = new PostgresServerDbContext(postgresOptions);
        Assert.Multiple(() =>
        {
            Assert.That(sqlite.Database.HasPendingModelChanges(), Is.False, "SQLite migration snapshot");
            Assert.That(postgres.Database.HasPendingModelChanges(), Is.False, "PostgreSQL migration snapshot");
        });
        var serverTypes = new[]
        {
            typeof(LuaMExpeditionManifest), typeof(LuaMExpeditionRegion), typeof(LuaMExpeditionSite),
            typeof(LuaMExpeditionDelta), typeof(LuaMExpeditionEntitySnapshot), typeof(LuaMExpeditionTombstone),
            typeof(LuaMExpeditionCheckpoint), typeof(LuaMCampaignShift), typeof(LuaMCampaignShiftRun),
            typeof(LuaMCharacterCareer), typeof(LuaMCareerShiftParticipation), typeof(LuaMCareerXpLedger),
            typeof(LuaMDeepCryoSnapshot), typeof(LuaMCharacterPresenceLease), typeof(LuaMDeepCryoOperation),
        };

        foreach (var type in serverTypes)
        {
            var sqliteEntity = sqlite.Model.FindEntityType(type);
            var postgresEntity = postgres.Model.FindEntityType(type);
            Assert.Multiple(() =>
            {
                Assert.That(sqliteEntity, Is.Not.Null, type.Name);
                Assert.That(postgresEntity, Is.Not.Null, type.Name);
                Assert.That(postgresEntity!.GetTableName(), Is.EqualTo(sqliteEntity!.GetTableName()), type.Name);
            });

            foreach (var foreignKey in sqliteEntity!.GetForeignKeys())
                Assert.That(foreignKey.DeleteBehavior, Is.EqualTo(DeleteBehavior.Restrict), type.Name);
            foreach (var foreignKey in postgresEntity!.GetForeignKeys())
                Assert.That(foreignKey.DeleteBehavior, Is.EqualTo(DeleteBehavior.Restrict), type.Name);
        }

        var clientSurfaceAssemblies = new[]
        {
            typeof(PdaMenu).Assembly,
            typeof(HumanoidCharacterProfile).Assembly,
            typeof(NoteSeverity).Assembly,
        };
        var forbidden = new[]
        {
            nameof(LuaMExpeditionManifest), nameof(LuaMExpeditionRegion), nameof(LuaMExpeditionEntitySnapshot),
            nameof(LuaMCampaignShift), nameof(LuaMCharacterCareer), nameof(LuaMCareerXpLedger),
            nameof(LuaMDeepCryoSnapshot), nameof(LuaMCharacterPresenceLease), nameof(LuaMDeepCryoOperation),
        };
        foreach (var assembly in clientSurfaceAssemblies.Distinct())
        {
            Assert.That(assembly.GetReferencedAssemblies().Select(value => value.Name),
                Does.Not.Contain("Content.Server.Database"), assembly.GetName().Name);
            var names = assembly.GetTypes().Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
            Assert.That(names.Intersect(forbidden), Is.Empty, assembly.GetName().Name);
        }
    }

    private static async Task<SqliteConnection> OpenSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static ServerDbSqlite NewServerDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        var logManager = new LogManager();
        IConfigurationManager configuration = MockInterfaces.MakeConfigurationManager(
            new Mock<IGameTiming>().Object,
            logManager,
            loadCvarsFromTypes: [typeof(CCVars)]);
        return new ServerDbSqlite(() => options, true, configuration, false, new DummySawmill());
    }

    private static LuaMExpeditionCreateRequest NewExpedition(DateTime now)
        => new(
            "campaign-persistence",
            "expedition-persistence",
            ulong.MaxValue,
            1,
            "0123456789ABCDEF",
            0,
            0,
            63,
            63,
            DbLuaMExpeditionStatus.Active,
            DbLuaMExpeditionPreservationPolicy.CampaignPermanent,
            [new LuaMExpeditionRegionWrite(0, 0, 0, 0, 63, 63, "test-biome", null, 10, 1, [], "edge-hash")],
            [new LuaMExpeditionSiteWrite("site-1", 1, "TestSite", "campaign", 16, 16,
                12, 12, 20, 20, 1, [], "site-hash")],
            now);

    private static LuaMExpeditionMutationRequest NewMutation(
        long manifestId,
        long regionId,
        long expectedRevision,
        Guid operationId,
        DateTime now)
        => new(
            manifestId,
            regionId,
            expectedRevision,
            "container-opened",
            1,
            1,
            [1, 2, 3],
            "delta-hash",
            null,
            [new LuaMExpeditionSnapshotWrite("container-1", "container-opened", new string('B', 64),
                1, [4, 5], "snapshot-hash")],
            [new LuaMExpeditionTombstoneWrite("loot-1", "container-opened", 1)],
            operationId,
            1,
            [6, 7],
            "checkpoint-hash",
            now);

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

    private static Profile NewDbProfile(Preference preference)
        => new()
        {
            Slot = 0,
            CharacterName = "Constraint Character",
            FlavorText = string.Empty,
            Age = 30,
            BankBalance = 0,
            Sex = "Male",
            Gender = "Male",
            Species = "Human",
            Voice = string.Empty,
            Markings = JsonDocument.Parse("[]"),
            HairName = "Afro",
            HairColor = "#FFFFFFFF",
            FacialHairName = "Shaved",
            FacialHairColor = "#FFFFFFFF",
            EyeColor = "#FFFFFFFF",
            SkinColor = "#FFFFFFFF",
            PreferenceUnavailable = 0,
            Company = "None",
            Preference = preference,
        };
}
