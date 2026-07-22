using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public abstract partial class ServerDbBase
{
    public async Task<LuaMExpeditionWriteResult> CreateOrGetLuaMExpeditionAsync(
        LuaMExpeditionCreateRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidExpeditionCreateRequest(request))
            return new(LuaMExpeditionWriteStatus.InvalidRequest);

        var existing = await FindLuaMExpeditionManifestAsync(request.CampaignId, request.ExpeditionId, cancel);
        if (existing != null)
            return MatchImmutableManifest(existing, request)
                ? new(LuaMExpeditionWriteStatus.AlreadyProcessed, existing.Id)
                : new(LuaMExpeditionWriteStatus.IdentityConflict, existing.Id);

        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var now = AsUtc(request.CreatedAtUtc);
            var manifest = new LuaMExpeditionManifest
            {
                CampaignId = request.CampaignId,
                ExpeditionId = request.ExpeditionId,
                SeedBits = unchecked((long) request.Seed),
                GeneratorVersion = request.GeneratorVersion,
                PlanHash = request.PlanHash,
                MinX = request.MinX,
                MinY = request.MinY,
                MaxX = request.MaxX,
                MaxY = request.MaxY,
                Status = request.Status,
                PreservationPolicy = request.PreservationPolicy,
                Revision = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.DbContext.LuaMExpeditionManifests.Add(manifest);
            await db.DbContext.SaveChangesAsync(cancel);

            db.DbContext.LuaMExpeditionRegions.AddRange(request.Regions.Select(region =>
                new LuaMExpeditionRegion
                {
                    ManifestId = manifest.Id,
                    RegionX = region.RegionX,
                    RegionY = region.RegionY,
                    MinX = region.MinX,
                    MinY = region.MinY,
                    MaxX = region.MaxX,
                    MaxY = region.MaxY,
                    Biome = region.Biome,
                    ScenarioTag = region.ScenarioTag,
                    DangerBudget = region.DangerBudget,
                    EdgeFormatVersion = region.EdgeFormatVersion,
                    EdgePayload = region.EdgePayload,
                    EdgePayloadHash = region.EdgePayloadHash,
                    Revision = 0,
                }));
            db.DbContext.LuaMExpeditionSites.AddRange(request.Sites.Select(site =>
                new LuaMExpeditionSite
                {
                    ManifestId = manifest.Id,
                    SiteId = site.SiteId,
                    Kind = site.Kind,
                    PrototypeId = site.PrototypeId,
                    UniqueScope = site.UniqueScope,
                    PositionX = site.PositionX,
                    PositionY = site.PositionY,
                    MinX = site.MinX,
                    MinY = site.MinY,
                    MaxX = site.MaxX,
                    MaxY = site.MaxY,
                    StateFormatVersion = site.StateFormatVersion,
                    StatePayload = site.StatePayload,
                    StatePayloadHash = site.StatePayloadHash,
                    Revision = 0,
                }));
            await db.DbContext.SaveChangesAsync(cancel);
            await transaction.CommitAsync(cancel);
            return new(LuaMExpeditionWriteStatus.Success, manifest.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM expedition create outcome requires re-read: {exception.Message}");
            existing = await FindLuaMExpeditionManifestAsync(request.CampaignId, request.ExpeditionId, CancellationToken.None);
            if (existing == null)
                return new(LuaMExpeditionWriteStatus.UnknownOutcome);

            return MatchImmutableManifest(existing, request)
                ? new(LuaMExpeditionWriteStatus.AlreadyProcessed, existing.Id)
                : new(LuaMExpeditionWriteStatus.IdentityConflict, existing.Id);
        }
    }

    public async Task<LuaMExpeditionStateRecord?> LoadLuaMExpeditionAsync(
        string campaignId,
        string expeditionId,
        CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        var manifest = await db.DbContext.LuaMExpeditionManifests.AsNoTracking()
            .SingleOrDefaultAsync(value => value.CampaignId == campaignId && value.ExpeditionId == expeditionId, cancel);
        if (manifest == null)
            return null;

        var regions = await db.DbContext.LuaMExpeditionRegions.AsNoTracking()
            .Where(value => value.ManifestId == manifest.Id)
            .OrderBy(value => value.RegionY).ThenBy(value => value.RegionX)
            .ToArrayAsync(cancel);
        var regionIds = regions.Select(value => value.Id).ToArray();
        var sites = await db.DbContext.LuaMExpeditionSites.AsNoTracking()
            .Where(value => value.ManifestId == manifest.Id)
            .OrderBy(value => value.SiteId)
            .ToArrayAsync(cancel);
        var deltas = await db.DbContext.LuaMExpeditionDeltas.AsNoTracking()
            .Where(value => regionIds.Contains(value.RegionId))
            .OrderBy(value => value.RegionId).ThenBy(value => value.RegionRevision)
            .ToArrayAsync(cancel);
        var snapshots = await db.DbContext.LuaMExpeditionEntitySnapshots.AsNoTracking()
            .Where(value => regionIds.Contains(value.RegionId))
            .OrderBy(value => value.RegionId).ThenBy(value => value.StableEntityId)
            .ToArrayAsync(cancel);
        var tombstones = await db.DbContext.LuaMExpeditionTombstones.AsNoTracking()
            .Where(value => regionIds.Contains(value.RegionId))
            .OrderBy(value => value.RegionId).ThenBy(value => value.StableObjectId)
            .ToArrayAsync(cancel);
        var checkpoint = await db.DbContext.LuaMExpeditionCheckpoints.AsNoTracking()
            .Where(value => value.ManifestId == manifest.Id &&
                            value.Status == DbLuaMExpeditionCheckpointStatus.Committed)
            .OrderByDescending(value => value.CommittedAtUtc)
            .ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancel);

        return new LuaMExpeditionStateRecord(
            ToManifestRecord(manifest),
            regions.Select(ToRegionRecord).ToArray(),
            sites.Select(value => new LuaMExpeditionSiteRecord(
                value.Id, value.ManifestId, value.SiteId, value.Kind, value.PrototypeId, value.UniqueScope,
                value.Revision, value.StatePayload, value.StateFormatVersion, value.CompletedAtUtc)).ToArray(),
            deltas.Select(value => new LuaMExpeditionDeltaRecord(
                value.Id, value.RegionId, value.RegionRevision, value.SourceInstanceId, value.Kind,
                value.FormatVersion, value.Payload, value.PayloadHash, value.CreatedAtUtc)).ToArray(),
            snapshots.Select(value => new LuaMExpeditionEntitySnapshotRecord(
                value.Id, value.RegionId, value.StableEntityId, value.RegionRevision, value.FormatVersion,
                value.Payload, value.PayloadHash, value.Status, value.QuarantineReason)).ToArray(),
            tombstones.Select(value => new LuaMExpeditionTombstoneRecord(
                value.Id, value.RegionId, value.StableObjectId, value.SourceInstanceId, value.Kind,
                value.RegionRevision, value.CreatedAtUtc)).ToArray(),
            checkpoint == null
                ? null
                : new LuaMExpeditionCheckpointRecord(
                    checkpoint.Id, checkpoint.ManifestId, checkpoint.OperationId, checkpoint.OperationIdentityKey,
                    checkpoint.FormatVersion, checkpoint.StatePayload, checkpoint.PayloadHash,
                    checkpoint.Status, checkpoint.CommittedAtUtc));
    }

    public async Task<LuaMExpeditionWriteResult> AppendLuaMExpeditionMutationAsync(
        LuaMExpeditionMutationRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidMutationRequest(request))
            return new(LuaMExpeditionWriteStatus.InvalidRequest);

        var operationIdentityKey = CreateMutationIdentityKey(request);
        var replay = await FindLuaMCheckpointAsync(request.CheckpointOperationId, cancel);
        if (replay != null)
            return CheckpointMatches(replay, operationIdentityKey)
                ? new(LuaMExpeditionWriteStatus.AlreadyProcessed, replay.ManifestId, CheckpointId: replay.Id)
                : new(LuaMExpeditionWriteStatus.IdentityConflict, replay.ManifestId, CheckpointId: replay.Id);

        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await db.DbContext.LuaMExpeditionCheckpoints.AsNoTracking()
                .SingleOrDefaultAsync(value => value.OperationId == request.CheckpointOperationId, cancel);
            if (replay != null)
                return CheckpointMatches(replay, operationIdentityKey)
                    ? new(LuaMExpeditionWriteStatus.AlreadyProcessed, replay.ManifestId, CheckpointId: replay.Id)
                    : new(LuaMExpeditionWriteStatus.IdentityConflict, replay.ManifestId, CheckpointId: replay.Id);

            var manifest = await db.DbContext.LuaMExpeditionManifests
                .SingleOrDefaultAsync(value => value.Id == request.ManifestId, cancel);
            if (manifest == null)
                return new(LuaMExpeditionWriteStatus.NotFound);
            if (manifest.Status == DbLuaMExpeditionStatus.Quarantined)
                return new(LuaMExpeditionWriteStatus.Quarantined, manifest.Id);
            if (manifest.Status == DbLuaMExpeditionStatus.Archived)
                return new(LuaMExpeditionWriteStatus.IdentityConflict, manifest.Id);

            var region = await db.DbContext.LuaMExpeditionRegions
                .SingleOrDefaultAsync(value => value.Id == request.RegionId && value.ManifestId == request.ManifestId, cancel);
            if (region == null)
                return new(LuaMExpeditionWriteStatus.NotFound, manifest.Id);
            if (region.QuarantinedAtUtc != null)
                return new(LuaMExpeditionWriteStatus.Quarantined, manifest.Id, region.Revision);
            if (region.Revision != request.ExpectedRegionRevision)
                return new(LuaMExpeditionWriteStatus.RevisionConflict, manifest.Id, region.Revision);

            var sourceExists = await db.DbContext.LuaMExpeditionDeltas.AsNoTracking()
                .AnyAsync(value => value.RegionId == region.Id && value.SourceInstanceId == request.SourceInstanceId, cancel);
            if (sourceExists)
                return new(LuaMExpeditionWriteStatus.IdentityConflict, manifest.Id, region.Revision);

            var nextRevision = checked(region.Revision + 1);
            var now = AsUtc(request.CreatedAtUtc);
            region.Revision = nextRevision;
            manifest.Revision = checked(manifest.Revision + 1);
            manifest.UpdatedAtUtc = now;
            db.DbContext.LuaMExpeditionDeltas.Add(new LuaMExpeditionDelta
            {
                RegionId = region.Id,
                RegionRevision = nextRevision,
                SourceInstanceId = request.SourceInstanceId,
                Kind = request.DeltaKind,
                FormatVersion = request.DeltaFormatVersion,
                Payload = request.DeltaPayload,
                PayloadHash = request.DeltaPayloadHash,
                ActorProfileId = request.ActorProfileId,
                CreatedAtUtc = now,
            });

            foreach (var write in request.EntitySnapshots)
            {
                var existing = await db.DbContext.LuaMExpeditionEntitySnapshots
                    .SingleOrDefaultAsync(value => value.RegionId == region.Id &&
                                                   value.StableEntityId == write.StableEntityId, cancel);
                if (existing == null)
                {
                    db.DbContext.LuaMExpeditionEntitySnapshots.Add(new LuaMExpeditionEntitySnapshot
                    {
                        RegionId = region.Id,
                        StableEntityId = write.StableEntityId,
                        SourceInstanceId = write.SourceInstanceId,
                        IdempotencyKey = write.IdempotencyKey,
                        RegionRevision = nextRevision,
                        FormatVersion = write.FormatVersion,
                        Payload = write.Payload,
                        PayloadHash = write.PayloadHash,
                        Status = DbLuaMExpeditionSnapshotStatus.Active,
                        ActorProfileId = write.ActorProfileId,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    });
                }
                else
                {
                    existing.SourceInstanceId = write.SourceInstanceId;
                    existing.IdempotencyKey = write.IdempotencyKey;
                    existing.RegionRevision = nextRevision;
                    existing.FormatVersion = write.FormatVersion;
                    existing.Payload = write.Payload;
                    existing.PayloadHash = write.PayloadHash;
                    existing.Status = DbLuaMExpeditionSnapshotStatus.Active;
                    existing.ActorProfileId = write.ActorProfileId;
                    existing.UpdatedAtUtc = now;
                    existing.QuarantineReason = null;
                }
            }

            foreach (var write in request.Tombstones)
            {
                var exists = await db.DbContext.LuaMExpeditionTombstones.AsNoTracking()
                    .AnyAsync(value => value.RegionId == region.Id && value.StableObjectId == write.StableObjectId, cancel);
                if (exists)
                    continue;

                db.DbContext.LuaMExpeditionTombstones.Add(new LuaMExpeditionTombstone
                {
                    RegionId = region.Id,
                    SiteRowId = write.SiteRowId,
                    StableObjectId = write.StableObjectId,
                    SourceInstanceId = write.SourceInstanceId,
                    Kind = write.Kind,
                    RegionRevision = nextRevision,
                    ActorProfileId = write.ActorProfileId,
                    CreatedAtUtc = now,
                });
            }

            var checkpoint = new LuaMExpeditionCheckpoint
            {
                ManifestId = manifest.Id,
                OperationId = request.CheckpointOperationId,
                OperationIdentityKey = operationIdentityKey,
                FormatVersion = request.CheckpointFormatVersion,
                StatePayload = request.CheckpointPayload,
                PayloadHash = request.CheckpointPayloadHash,
                Status = DbLuaMExpeditionCheckpointStatus.Committed,
                CreatedAtUtc = now,
                CommittedAtUtc = now,
            };
            db.DbContext.LuaMExpeditionCheckpoints.Add(checkpoint);
            await db.DbContext.SaveChangesAsync(cancel);
            await transaction.CommitAsync(cancel);
            return new(LuaMExpeditionWriteStatus.Success, manifest.Id, nextRevision, checkpoint.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            replay = await FindLuaMCheckpointAsync(request.CheckpointOperationId, CancellationToken.None);
            if (replay != null && CheckpointMatches(replay, operationIdentityKey))
                return new(LuaMExpeditionWriteStatus.AlreadyProcessed, replay.ManifestId, CheckpointId: replay.Id);

            var current = await FindLuaMRegionAsync(request.RegionId, CancellationToken.None);
            return new(LuaMExpeditionWriteStatus.RevisionConflict, request.ManifestId, current?.Revision);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM expedition mutation outcome requires re-read: {exception.Message}");
            replay = await FindLuaMCheckpointAsync(request.CheckpointOperationId, CancellationToken.None);
            if (replay == null)
                return new(LuaMExpeditionWriteStatus.UnknownOutcome, request.ManifestId);

            return CheckpointMatches(replay, operationIdentityKey)
                ? new(LuaMExpeditionWriteStatus.AlreadyProcessed, replay.ManifestId, CheckpointId: replay.Id)
                : new(LuaMExpeditionWriteStatus.IdentityConflict, replay.ManifestId, CheckpointId: replay.Id);
        }
    }

    public Task<LuaMExpeditionWriteResult> ArchiveLuaMExpeditionAsync(
        long manifestId,
        long expectedRevision,
        DateTime archivedAtUtc,
        CancellationToken cancel = default)
        => TransitionLuaMExpeditionAsync(
            manifestId,
            expectedRevision,
            DbLuaMExpeditionStatus.Archived,
            AsUtc(archivedAtUtc),
            null,
            cancel);

    public Task<LuaMExpeditionWriteResult> QuarantineLuaMExpeditionAsync(
        long manifestId,
        long expectedRevision,
        string reason,
        DateTime quarantinedAtUtc,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
            return Task.FromResult(new LuaMExpeditionWriteResult(LuaMExpeditionWriteStatus.InvalidRequest));

        return TransitionLuaMExpeditionAsync(
            manifestId,
            expectedRevision,
            DbLuaMExpeditionStatus.Quarantined,
            AsUtc(quarantinedAtUtc),
            reason,
            cancel);
    }

    private async Task<LuaMExpeditionWriteResult> TransitionLuaMExpeditionAsync(
        long manifestId,
        long expectedRevision,
        DbLuaMExpeditionStatus status,
        DateTime timestamp,
        string? quarantineReason,
        CancellationToken cancel)
    {
        if (manifestId <= 0 || expectedRevision < 0)
            return new(LuaMExpeditionWriteStatus.InvalidRequest);

        await using var db = await GetDb(cancel);
        var affected = await db.DbContext.LuaMExpeditionManifests
            .Where(value => value.Id == manifestId && value.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, status)
                .SetProperty(value => value.Revision, expectedRevision + 1)
                .SetProperty(value => value.UpdatedAtUtc, timestamp)
                .SetProperty(value => value.ArchivedAtUtc,
                    status == DbLuaMExpeditionStatus.Archived ? timestamp : (DateTime?) null)
                .SetProperty(value => value.QuarantineReason, quarantineReason), cancel);
        if (affected == 1)
            return new(LuaMExpeditionWriteStatus.Success, manifestId, expectedRevision + 1);

        var current = await db.DbContext.LuaMExpeditionManifests.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == manifestId, cancel);
        if (current == null)
            return new(LuaMExpeditionWriteStatus.NotFound);
        if (current.Status == status &&
            (status != DbLuaMExpeditionStatus.Quarantined || current.QuarantineReason == quarantineReason))
            return new(LuaMExpeditionWriteStatus.AlreadyProcessed, manifestId, current.Revision);
        return new(LuaMExpeditionWriteStatus.RevisionConflict, manifestId, current.Revision);
    }

    private async Task<LuaMExpeditionManifest?> FindLuaMExpeditionManifestAsync(
        string campaignId,
        string expeditionId,
        CancellationToken cancel)
    {
        await using var db = await GetDb(cancel);
        return await db.DbContext.LuaMExpeditionManifests.AsNoTracking()
            .SingleOrDefaultAsync(value => value.CampaignId == campaignId && value.ExpeditionId == expeditionId, cancel);
    }

    private async Task<LuaMExpeditionCheckpoint?> FindLuaMCheckpointAsync(Guid operationId, CancellationToken cancel)
    {
        await using var db = await GetDb(cancel);
        return await db.DbContext.LuaMExpeditionCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(value => value.OperationId == operationId, cancel);
    }

    private async Task<LuaMExpeditionRegion?> FindLuaMRegionAsync(long regionId, CancellationToken cancel)
    {
        await using var db = await GetDb(cancel);
        return await db.DbContext.LuaMExpeditionRegions.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == regionId, cancel);
    }

    private static bool MatchImmutableManifest(LuaMExpeditionManifest manifest, LuaMExpeditionCreateRequest request)
        => manifest.SeedBits == unchecked((long) request.Seed) &&
           manifest.GeneratorVersion == request.GeneratorVersion &&
           manifest.PlanHash == request.PlanHash &&
           manifest.MinX == request.MinX && manifest.MinY == request.MinY &&
           manifest.MaxX == request.MaxX && manifest.MaxY == request.MaxY &&
           manifest.PreservationPolicy == request.PreservationPolicy;

    private static bool CheckpointMatches(LuaMExpeditionCheckpoint checkpoint, string operationIdentityKey)
        => checkpoint.OperationIdentityKey == operationIdentityKey;

    private static bool IsValidExpeditionCreateRequest(LuaMExpeditionCreateRequest request)
        => !string.IsNullOrWhiteSpace(request.CampaignId) && request.CampaignId.Length <= 128 &&
           !string.IsNullOrWhiteSpace(request.ExpeditionId) && request.ExpeditionId.Length <= 128 &&
           request.CreatedAtUtc.Kind == DateTimeKind.Utc &&
           Enum.IsDefined(request.Status) && Enum.IsDefined(request.PreservationPolicy) &&
           request.GeneratorVersion > 0 &&
           !string.IsNullOrWhiteSpace(request.PlanHash) && request.PlanHash.Length <= 64 &&
           request.MinX <= request.MaxX && request.MinY <= request.MaxY &&
           request.Regions.Count > 0 && request.Sites.Count > 0 &&
           request.Regions.All(region => region.MinX <= region.MaxX && region.MinY <= region.MaxY &&
                                         region.DangerBudget >= 0 && region.EdgeFormatVersion > 0 &&
                                         !string.IsNullOrWhiteSpace(region.Biome) && region.Biome.Length <= 128 &&
                                         (region.ScenarioTag == null || region.ScenarioTag.Length <= 128) &&
                                         region.EdgePayload != null &&
                                         !string.IsNullOrWhiteSpace(region.EdgePayloadHash) &&
                                         region.EdgePayloadHash.Length <= 64) &&
           request.Sites.All(site => site.MinX <= site.MaxX && site.MinY <= site.MaxY &&
                                     site.StateFormatVersion > 0 &&
                                     !string.IsNullOrWhiteSpace(site.SiteId) && site.SiteId.Length <= 128 &&
                                     !string.IsNullOrWhiteSpace(site.PrototypeId) && site.PrototypeId.Length <= 128 &&
                                     !string.IsNullOrWhiteSpace(site.UniqueScope) && site.UniqueScope.Length <= 128 &&
                                     site.StatePayload != null && !string.IsNullOrWhiteSpace(site.StatePayloadHash) &&
                                     site.StatePayloadHash.Length <= 64) &&
           request.Regions.Select(region => (region.RegionX, region.RegionY)).Distinct().Count() ==
               request.Regions.Count &&
           request.Sites.Select(site => site.SiteId).Distinct(StringComparer.Ordinal).Count() ==
               request.Sites.Count &&
           request.Sites.GroupBy(site => (site.UniqueScope, site.PrototypeId)).All(group => group.Count() == 1);

    private static bool IsValidMutationRequest(LuaMExpeditionMutationRequest request)
        => request.ManifestId > 0 && request.RegionId > 0 && request.ExpectedRegionRevision >= 0 &&
           request.CheckpointOperationId != Guid.Empty &&
           request.CreatedAtUtc.Kind == DateTimeKind.Utc &&
           !string.IsNullOrWhiteSpace(request.SourceInstanceId) && request.SourceInstanceId.Length <= 256 &&
           request.DeltaFormatVersion > 0 && request.CheckpointFormatVersion > 0 &&
           request.DeltaPayload != null && request.CheckpointPayload != null &&
           request.DeltaPayloadHash.Length is > 0 and <= 64 &&
           request.CheckpointPayloadHash.Length is > 0 and <= 64 &&
           request.ActorProfileId is null or > 0 &&
           request.EntitySnapshots.All(value => value.StableEntityId.Length is > 0 and <= 256 &&
                                                value.SourceInstanceId.Length is > 0 and <= 256 &&
                                                value.IdempotencyKey.Length == 64 && value.FormatVersion > 0 &&
                                                value.Payload != null && value.PayloadHash.Length is > 0 and <= 64 &&
                                                value.ActorProfileId is null or > 0) &&
           request.Tombstones.All(value => value.StableObjectId.Length is > 0 and <= 256 &&
                                           value.SourceInstanceId.Length is > 0 and <= 256 &&
                                           value.SiteRowId is null or > 0 &&
                                           value.ActorProfileId is null or > 0) &&
           request.EntitySnapshots.Select(value => value.StableEntityId)
               .Distinct(StringComparer.Ordinal).Count() == request.EntitySnapshots.Count &&
           request.EntitySnapshots.Select(value => value.IdempotencyKey)
               .Distinct(StringComparer.Ordinal).Count() == request.EntitySnapshots.Count &&
           request.Tombstones.Select(value => value.StableObjectId)
               .Distinct(StringComparer.Ordinal).Count() == request.Tombstones.Count;

    private static string CreateMutationIdentityKey(LuaMExpeditionMutationRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(request.ManifestId);
            writer.Write(request.RegionId);
            writer.Write(request.ExpectedRegionRevision);
            writer.Write(request.SourceInstanceId);
            writer.Write(request.DeltaKind);
            writer.Write(request.DeltaFormatVersion);
            WriteBytes(writer, request.DeltaPayload);
            writer.Write(request.DeltaPayloadHash);
            WriteNullableInt(writer, request.ActorProfileId);
            writer.Write(request.CheckpointOperationId.ToByteArray());
            writer.Write(request.CheckpointFormatVersion);
            WriteBytes(writer, request.CheckpointPayload);
            writer.Write(request.CheckpointPayloadHash);
            writer.Write(request.CreatedAtUtc.Ticks);

            var snapshots = request.EntitySnapshots
                .OrderBy(value => value.StableEntityId, StringComparer.Ordinal)
                .ThenBy(value => value.IdempotencyKey, StringComparer.Ordinal)
                .ToArray();
            writer.Write(snapshots.Length);
            foreach (var snapshot in snapshots)
            {
                writer.Write(snapshot.StableEntityId);
                writer.Write(snapshot.SourceInstanceId);
                writer.Write(snapshot.IdempotencyKey);
                writer.Write(snapshot.FormatVersion);
                WriteBytes(writer, snapshot.Payload);
                writer.Write(snapshot.PayloadHash);
                WriteNullableInt(writer, snapshot.ActorProfileId);
            }

            var tombstones = request.Tombstones
                .OrderBy(value => value.StableObjectId, StringComparer.Ordinal)
                .ToArray();
            writer.Write(tombstones.Length);
            foreach (var tombstone in tombstones)
            {
                writer.Write(tombstone.StableObjectId);
                writer.Write(tombstone.SourceInstanceId);
                writer.Write(tombstone.Kind);
                WriteNullableLong(writer, tombstone.SiteRowId);
                WriteNullableInt(writer, tombstone.ActorProfileId);
            }
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int) buffer.Length))));
    }

    private static void WriteBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static void WriteNullableInt(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    private static void WriteNullableLong(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    private static LuaMExpeditionManifestRecord ToManifestRecord(LuaMExpeditionManifest value)
        => new(value.Id, value.CampaignId, value.ExpeditionId, unchecked((ulong) value.SeedBits),
            value.GeneratorVersion, value.PlanHash, value.Status, value.PreservationPolicy, value.Revision,
            value.CreatedAtUtc, value.UpdatedAtUtc, value.ArchivedAtUtc, value.QuarantineReason);

    private static LuaMExpeditionRegionRecord ToRegionRecord(LuaMExpeditionRegion value)
        => new(value.Id, value.ManifestId, value.RegionX, value.RegionY, value.Revision,
            value.DiscoveredAtUtc, value.IsDepleted, value.QuarantinedAtUtc, value.QuarantineReason);

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
