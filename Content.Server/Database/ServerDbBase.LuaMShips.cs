using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public abstract partial class ServerDbBase
{
    public async Task<LuaMShipSnapshotRecord?> GetLuaMShipSnapshotAsync(
        Guid shipId,
        NetUserId ownerUserId,
        CancellationToken cancel = default)
    {
        if (shipId == Guid.Empty || ownerUserId.UserId == Guid.Empty)
            return null;

        await using var db = await GetDb(cancel);
        var snapshot = await db.DbContext.LuaMShipSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ShipId == shipId &&
                                           value.OwnerUserId == ownerUserId.UserId,
                cancel);
        if (snapshot == null)
            return null;

        var lease = await db.DbContext.LuaMShipPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ShipId == shipId, cancel);
        return ToShipSnapshotRecord(snapshot, lease);
    }

    public async Task<IReadOnlyList<LuaMShipRegistryRecord>> GetLuaMShipSnapshotsByOwnerAsync(
        NetUserId ownerUserId,
        bool includeRetired = false,
        CancellationToken cancel = default)
    {
        if (ownerUserId.UserId == Guid.Empty)
            return Array.Empty<LuaMShipRegistryRecord>();

        await using var db = await GetDb(cancel);
        var rows = await db.DbContext.LuaMShipSnapshots.AsNoTracking()
            .Where(value => value.OwnerUserId == ownerUserId.UserId &&
                            (includeRetired || value.Status != DbLuaMShipSnapshotStatus.Retired))
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ThenBy(value => value.ShipId)
            .Select(value => new
            {
                value.ShipId,
                value.OwnerUserId,
                value.Revision,
                value.PayloadRevision,
                value.Status,
                value.VesselPrototypeId,
                value.ShipName,
                value.ShipNameSuffix,
                value.PurchasePrice,
                value.PurchasedWithVoucher,
                value.SchemaVersion,
                value.FormatVersion,
                value.PayloadHash,
                value.PayloadSizeBytes,
                value.EntityCount,
                value.SourceBuildVersion,
                value.PrototypeManifestHash,
                value.SourceRoundId,
                value.LastRestoreRoundId,
                value.CreatedAtUtc,
                value.StoredAtUtc,
                value.UpdatedAtUtc,
                value.LastRestoredAtUtc,
                value.QuarantinedAtUtc,
                value.QuarantineReason,
                value.RetiredAtUtc,
                value.RetirementReason,
                value.RetirementOperationId,
            })
            .ToArrayAsync(cancel);
        if (rows.Length == 0)
            return Array.Empty<LuaMShipRegistryRecord>();

        var shipIds = rows.Select(value => value.ShipId).ToArray();
        var leases = await db.DbContext.LuaMShipPresenceLeases.AsNoTracking()
            .Where(value => shipIds.Contains(value.ShipId))
            .ToDictionaryAsync(value => value.ShipId, cancel);

        return rows.Select(value =>
        {
            leases.TryGetValue(value.ShipId, out var lease);
            return new LuaMShipRegistryRecord(
                value.ShipId,
                new NetUserId(value.OwnerUserId),
                value.Revision,
                value.PayloadRevision,
                value.Status,
                value.VesselPrototypeId,
                value.ShipName,
                value.ShipNameSuffix,
                value.PurchasePrice,
                value.PurchasedWithVoucher,
                value.SchemaVersion,
                value.FormatVersion,
                value.PayloadHash,
                value.PayloadSizeBytes,
                value.EntityCount,
                value.SourceBuildVersion,
                value.PrototypeManifestHash,
                value.SourceRoundId,
                value.LastRestoreRoundId,
                value.CreatedAtUtc,
                value.StoredAtUtc,
                value.UpdatedAtUtc,
                value.LastRestoredAtUtc,
                value.QuarantinedAtUtc,
                value.QuarantineReason,
                value.RetiredAtUtc,
                value.RetirementReason,
                value.RetirementOperationId,
                lease?.LeaseId,
                lease?.ServerInstanceId,
                lease?.RoundId,
                lease?.Revision,
                lease?.ExpiresAtUtc);
        }).ToArray();
    }

    public async Task<LuaMShipPersistenceWriteResult> StoreLuaMShipSnapshotAsync(
        LuaMShipSnapshotStoreRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidShipStoreRequest(request))
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            var ownerPreferenceId = await db.DbContext.Preference.AsNoTracking()
                .Where(value => value.UserId == request.OwnerUserId.UserId)
                .Select(value => (int?) value.Id)
                .SingleOrDefaultAsync(cancel);
            if (ownerPreferenceId == null)
                return new(LuaMShipPersistenceWriteStatus.OwnerNotFound, request.ShipId);

            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            var storedAt = AsUtc(request.StoredAtUtc);

            if (snapshot == null)
            {
                if (request.ExpectedRevision != null || request.LeaseId != null)
                    return new(LuaMShipPersistenceWriteStatus.NotFound, request.ShipId);
                if (request.PayloadRevision != 1)
                    return new(LuaMShipPersistenceWriteStatus.InvalidRequest, request.ShipId);

                snapshot = new LuaMShipSnapshot
                {
                    ShipId = request.ShipId,
                    OwnerUserId = request.OwnerUserId.UserId,
                    OwnerPreferenceId = ownerPreferenceId.Value,
                    PayloadRevision = request.PayloadRevision,
                    Status = DbLuaMShipSnapshotStatus.Stored,
                    VesselPrototypeId = request.VesselPrototypeId,
                    ShipName = request.ShipName,
                    ShipNameSuffix = request.ShipNameSuffix,
                    PurchasePrice = request.PurchasePrice,
                    PurchasedWithVoucher = request.PurchasedWithVoucher,
                    SchemaVersion = request.SchemaVersion,
                    FormatVersion = request.FormatVersion,
                    Payload = request.Payload.ToArray(),
                    PayloadHash = NormalizeShipSha256(request.PayloadHash),
                    PayloadSizeBytes = request.PayloadSizeBytes,
                    EntityCount = request.EntityCount,
                    SourceBuildVersion = request.SourceBuildVersion,
                    PrototypeManifestHash = NormalizeShipSha256(request.PrototypeManifestHash),
                    SourceRoundId = request.SourceRoundId,
                    CreatedAtUtc = storedAt,
                    StoredAtUtc = storedAt,
                    UpdatedAtUtc = storedAt,
                };
                db.DbContext.LuaMShipSnapshots.Add(snapshot);
            }
            else
            {
                if (snapshot.OwnerUserId != request.OwnerUserId.UserId)
                    return new(LuaMShipPersistenceWriteStatus.OwnerMismatch, request.ShipId);

                var lease = await db.DbContext.LuaMShipPresenceLeases
                    .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
                if (IsExactShipStoreOutcome(ToShipSnapshotRecord(snapshot, lease), request))
                    return ShipAlreadyProcessed(snapshot, null);

                if (snapshot.Status == DbLuaMShipSnapshotStatus.Quarantined)
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.Quarantined,
                        snapshot.ShipId,
                        snapshot.Revision,
                        snapshot.Status);
                }
                if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
                    return ShipRetired(snapshot);
                if (request.ExpectedRevision == null || snapshot.Revision != request.ExpectedRevision.Value)
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.RevisionConflict,
                        snapshot.ShipId,
                        snapshot.Revision,
                        snapshot.Status);
                }
                if (snapshot.Status != DbLuaMShipSnapshotStatus.Active)
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.InvalidState,
                        snapshot.ShipId,
                        snapshot.Revision,
                        snapshot.Status);
                }
                if (snapshot.Revision == long.MaxValue)
                    return new(LuaMShipPersistenceWriteStatus.InvalidRequest, snapshot.ShipId, snapshot.Revision);
                if (snapshot.PayloadRevision is { } currentPayloadRevision &&
                    (currentPayloadRevision == long.MaxValue || request.PayloadRevision != currentPayloadRevision + 1))
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.InvalidRequest,
                        snapshot.ShipId,
                        snapshot.Revision,
                        snapshot.Status);
                }

                if (lease == null || request.LeaseId == null || lease.LeaseId != request.LeaseId.Value ||
                    lease.ExpiresAtUtc <= storedAt)
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.LeaseConflict,
                        snapshot.ShipId,
                        snapshot.Revision,
                        snapshot.Status,
                        lease?.LeaseId,
                        lease?.Revision);
                }

                storedAt = MaxShipDate(storedAt, snapshot.UpdatedAtUtc);
                snapshot.Revision++;
                snapshot.PayloadRevision = request.PayloadRevision;
                snapshot.Status = DbLuaMShipSnapshotStatus.Stored;
                snapshot.VesselPrototypeId = request.VesselPrototypeId;
                snapshot.ShipName = request.ShipName;
                snapshot.ShipNameSuffix = request.ShipNameSuffix;
                snapshot.PurchasePrice = request.PurchasePrice;
                snapshot.PurchasedWithVoucher = request.PurchasedWithVoucher;
                snapshot.SchemaVersion = request.SchemaVersion;
                snapshot.FormatVersion = request.FormatVersion;
                snapshot.Payload = request.Payload.ToArray();
                snapshot.PayloadHash = NormalizeShipSha256(request.PayloadHash);
                snapshot.PayloadSizeBytes = request.PayloadSizeBytes;
                snapshot.EntityCount = request.EntityCount;
                snapshot.SourceBuildVersion = request.SourceBuildVersion;
                snapshot.PrototypeManifestHash = NormalizeShipSha256(request.PrototypeManifestHash);
                snapshot.SourceRoundId = request.SourceRoundId;
                snapshot.StoredAtUtc = storedAt;
                snapshot.UpdatedAtUtc = storedAt;
                snapshot.QuarantinedAtUtc = null;
                snapshot.QuarantineReason = null;
                db.DbContext.LuaMShipPresenceLeases.Remove(lease);
            }

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMShipPersistenceWriteStatus.Success,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                Snapshot: ToShipSnapshotRecord(snapshot, null));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveShipStoreFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveShipStoreFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM ship snapshot store outcome requires re-read: {exception.Message}");
            return await ResolveShipStoreFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMShipPersistenceWriteResult> ClaimLuaMShipRestoreAsync(
        LuaMShipRestoreClaimRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidShipClaimRequest(request))
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if (snapshot == null)
                return new(LuaMShipPersistenceWriteStatus.NotFound, request.ShipId);

            if (snapshot.OwnerUserId != request.OwnerUserId.UserId)
                return new(LuaMShipPersistenceWriteStatus.OwnerMismatch, request.ShipId);

            var existingLease = await db.DbContext.LuaMShipPresenceLeases
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if (IsExactShipClaimOutcome(ToShipSnapshotRecord(snapshot, existingLease), request))
                return ShipAlreadyProcessed(snapshot, existingLease);

            var initial = CheckShipIdentityRevision(snapshot, request.OwnerUserId, request.ExpectedRevision);
            if (initial != null)
                return initial;
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Quarantined)
                return ShipQuarantined(snapshot);
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
                return ShipRetired(snapshot);
            if (snapshot.Status != DbLuaMShipSnapshotStatus.Stored)
                return ShipInvalidState(snapshot);
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMShipPersistenceWriteStatus.InvalidRequest, snapshot.ShipId, snapshot.Revision);

            if (existingLease != null)
                return new(LuaMShipPersistenceWriteStatus.LeaseConflict, snapshot.ShipId, snapshot.Revision, snapshot.Status);

            var claimedAt = AsUtc(request.ClaimedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            var lease = new LuaMShipPresenceLease
            {
                ShipId = request.ShipId,
                LeaseId = request.LeaseId,
                ServerInstanceId = request.ServerInstanceId,
                RoundId = request.RestoreRoundId,
                AcquiredAtUtc = claimedAt,
                RenewedAtUtc = claimedAt,
                ExpiresAtUtc = expiresAt,
            };
            snapshot.Status = DbLuaMShipSnapshotStatus.Restoring;
            snapshot.Revision++;
            snapshot.LastRestoreRoundId = request.RestoreRoundId;
            snapshot.UpdatedAtUtc = MaxShipDate(snapshot.UpdatedAtUtc, claimedAt);
            db.DbContext.LuaMShipPresenceLeases.Add(lease);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMShipPersistenceWriteStatus.Success,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                lease.LeaseId,
                lease.Revision,
                ToShipSnapshotRecord(snapshot, lease));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveShipClaimFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveShipClaimFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM ship restore claim outcome requires re-read: {exception.Message}");
            return await ResolveShipClaimFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
    }

    public Task<LuaMShipPersistenceWriteResult> CompleteLuaMShipRestoreAsync(
        LuaMShipRestoreCompleteRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMShipRestoreAsync(
            request.ShipId,
            request.OwnerUserId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMShipSnapshotStatus.Active,
            request.CompletedAtUtc,
            keepLease: true,
            null,
            DbLuaMShipSnapshotStatus.Restoring,
            cancel);

    public Task<LuaMShipPersistenceWriteResult> AbortLuaMShipRestoreAsync(
        LuaMShipRestoreAbortRequest request,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > LuaMShipPersistenceLimits.MaxReasonLength)
        {
            return Task.FromResult(new LuaMShipPersistenceWriteResult(
                LuaMShipPersistenceWriteStatus.InvalidRequest));
        }

        return TransitionLuaMShipRestoreAsync(
            request.ShipId,
            request.OwnerUserId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMShipSnapshotStatus.Stored,
            request.AbortedAtUtc,
            keepLease: false,
            request.Reason,
            DbLuaMShipSnapshotStatus.Restoring,
            cancel);
    }

    public Task<LuaMShipPersistenceWriteResult> ReleaseLuaMShipPresenceAsync(
        LuaMShipPresenceReleaseRequest request,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > LuaMShipPersistenceLimits.MaxReasonLength)
        {
            return Task.FromResult(new LuaMShipPersistenceWriteResult(
                LuaMShipPersistenceWriteStatus.InvalidRequest));
        }

        return TransitionLuaMShipRestoreAsync(
            request.ShipId,
            request.OwnerUserId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMShipSnapshotStatus.Stored,
            request.ReleasedAtUtc,
            keepLease: false,
            request.Reason,
            DbLuaMShipSnapshotStatus.Active,
            cancel);
    }

    public async Task<LuaMShipPersistenceWriteResult> RenewLuaMShipLeaseAsync(
        LuaMShipLeaseRenewRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidShipLeaseRenewRequest(request))
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if (snapshot == null)
                return new(LuaMShipPersistenceWriteStatus.NotFound, request.ShipId);

            var initial = CheckShipIdentityRevision(snapshot, request.OwnerUserId, request.ExpectedSnapshotRevision);
            if (initial != null)
                return initial;
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Quarantined)
                return ShipQuarantined(snapshot);
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
                return ShipRetired(snapshot);
            if (snapshot.Status is not (DbLuaMShipSnapshotStatus.Restoring or DbLuaMShipSnapshotStatus.Active))
                return ShipInvalidState(snapshot);

            var lease = await db.DbContext.LuaMShipPresenceLeases
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if (IsExactShipRenewOutcome(ToShipSnapshotRecord(snapshot, lease), request))
                return ShipAlreadyProcessed(snapshot, lease);

            var renewedAt = AsUtc(request.RenewedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            if (lease == null || lease.LeaseId != request.LeaseId ||
                lease.Revision != request.ExpectedLeaseRevision ||
                lease.ExpiresAtUtc <= renewedAt || renewedAt < lease.RenewedAtUtc ||
                expiresAt <= renewedAt || lease.Revision == long.MaxValue)
            {
                return new(
                    LuaMShipPersistenceWriteStatus.LeaseConflict,
                    snapshot.ShipId,
                    snapshot.Revision,
                    snapshot.Status,
                    lease?.LeaseId,
                    lease?.Revision);
            }

            lease.RenewedAtUtc = renewedAt;
            lease.ExpiresAtUtc = expiresAt;
            lease.Revision++;
            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMShipPersistenceWriteStatus.Success,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                lease.LeaseId,
                lease.Revision,
                ToShipSnapshotRecord(snapshot, lease));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveShipRenewFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveShipRenewFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.LeaseConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM ship lease renewal outcome requires re-read: {exception.Message}");
            return await ResolveShipRenewFailureAsync(
                request,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMShipPersistenceWriteResult> QuarantineLuaMShipSnapshotAsync(
        LuaMShipSnapshotQuarantineRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidShipQuarantineRequest(request))
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if (snapshot == null)
                return new(LuaMShipPersistenceWriteStatus.NotFound, request.ShipId);

            var initial = CheckShipIdentityRevision(snapshot, request.OwnerUserId, request.ExpectedRevision);
            if (initial != null)
                return initial;
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Quarantined)
                return ShipQuarantined(snapshot);
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
                return ShipRetired(snapshot);
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMShipPersistenceWriteStatus.InvalidRequest, snapshot.ShipId, snapshot.Revision);

            var lease = await db.DbContext.LuaMShipPresenceLeases
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if ((lease == null && request.LeaseId != null) ||
                (lease != null && (request.LeaseId == null || lease.LeaseId != request.LeaseId.Value)))
            {
                return new(
                    LuaMShipPersistenceWriteStatus.LeaseConflict,
                    snapshot.ShipId,
                    snapshot.Revision,
                    snapshot.Status,
                    lease?.LeaseId,
                    lease?.Revision);
            }

            var quarantinedAt = MaxShipDate(snapshot.UpdatedAtUtc, AsUtc(request.QuarantinedAtUtc));
            snapshot.Status = DbLuaMShipSnapshotStatus.Quarantined;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = quarantinedAt;
            snapshot.QuarantinedAtUtc = quarantinedAt;
            snapshot.QuarantineReason = request.Reason;
            if (lease != null)
                db.DbContext.LuaMShipPresenceLeases.Remove(lease);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMShipPersistenceWriteStatus.Success,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                Snapshot: ToShipSnapshotRecord(snapshot, null));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveShipWriteFailureAsync(
                request.ShipId,
                request.OwnerUserId,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveShipWriteFailureAsync(
                request.ShipId,
                request.OwnerUserId,
                LuaMShipPersistenceWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM ship quarantine outcome requires re-read: {exception.Message}");
            return await ResolveShipWriteFailureAsync(
                request.ShipId,
                request.OwnerUserId,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMShipPersistenceWriteResult> RetireLuaMShipSnapshotAsync(
        LuaMShipSnapshotRetireRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidShipRetireRequest(request))
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if (snapshot == null)
                return new(LuaMShipPersistenceWriteStatus.NotFound, request.ShipId);
            if (snapshot.OwnerUserId != request.OwnerUserId.UserId)
                return new(LuaMShipPersistenceWriteStatus.OwnerMismatch, request.ShipId);
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
            {
                var leaseAfterRetirement = await db.DbContext.LuaMShipPresenceLeases.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
                if (snapshot.RetirementOperationId == request.OperationId && leaseAfterRetirement == null)
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.AlreadyProcessed,
                        snapshot.ShipId,
                        snapshot.Revision,
                        snapshot.Status,
                        Snapshot: ToShipSnapshotRecord(snapshot, null));
                }

                return ShipRetired(snapshot);
            }

            var initial = CheckShipIdentityRevision(snapshot, request.OwnerUserId, request.ExpectedRevision);
            if (initial != null)
                return initial;
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Quarantined)
                return ShipQuarantined(snapshot);
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMShipPersistenceWriteStatus.InvalidRequest, snapshot.ShipId, snapshot.Revision);

            var lease = await db.DbContext.LuaMShipPresenceLeases
                .SingleOrDefaultAsync(value => value.ShipId == request.ShipId, cancel);
            if ((lease == null && request.LeaseId != null) ||
                (lease != null && (request.LeaseId == null || lease.LeaseId != request.LeaseId.Value)))
            {
                return new(
                    LuaMShipPersistenceWriteStatus.LeaseConflict,
                    snapshot.ShipId,
                    snapshot.Revision,
                    snapshot.Status,
                    lease?.LeaseId,
                    lease?.Revision);
            }

            var retiredAt = MaxShipDate(snapshot.UpdatedAtUtc, AsUtc(request.RetiredAtUtc));
            snapshot.Status = DbLuaMShipSnapshotStatus.Retired;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = retiredAt;
            snapshot.RetiredAtUtc = retiredAt;
            snapshot.RetirementReason = request.Reason;
            snapshot.RetirementOperationId = request.OperationId;
            if (lease != null)
                db.DbContext.LuaMShipPresenceLeases.Remove(lease);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMShipPersistenceWriteStatus.Success,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                Snapshot: ToShipSnapshotRecord(snapshot, null));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveShipRetirementFailureAsync(request);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveShipRetirementFailureAsync(request);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM ship retirement outcome requires re-read: {exception.Message}");
            return await ResolveShipRetirementFailureAsync(request);
        }
    }

    public async Task<int> RecoverExpiredLuaMShipLeasesAsync(
        DateTime nowUtc,
        int maxCount = 100,
        CancellationToken cancel = default)
    {
        if (nowUtc == default || maxCount is <= 0 or > 1000)
            return 0;

        var now = AsUtc(nowUtc);
        (Guid ShipId, Guid LeaseId)[] candidates;
        await using (var db = await GetDb(cancel))
        {
            candidates = await db.DbContext.LuaMShipPresenceLeases.AsNoTracking()
                .Where(value => value.ExpiresAtUtc <= now)
                .OrderBy(value => value.ExpiresAtUtc)
                .ThenBy(value => value.ShipId)
                .Take(maxCount)
                .Select(value => new ValueTuple<Guid, Guid>(value.ShipId, value.LeaseId))
                .ToArrayAsync(cancel);
        }

        var recovered = 0;
        foreach (var candidate in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (await RecoverExpiredLuaMShipLeaseAsync(candidate.ShipId, candidate.LeaseId, now, cancel))
                recovered++;
        }

        return recovered;
    }

    private async Task<LuaMShipPersistenceWriteResult> TransitionLuaMShipRestoreAsync(
        Guid shipId,
        NetUserId ownerUserId,
        long expectedRevision,
        Guid leaseId,
        DbLuaMShipSnapshotStatus targetStatus,
        DateTime transitionAtUtc,
        bool keepLease,
        string? reason,
        DbLuaMShipSnapshotStatus expectedSourceStatus,
        CancellationToken cancel)
    {
        if (shipId == Guid.Empty || ownerUserId.UserId == Guid.Empty || expectedRevision < 0 ||
            leaseId == Guid.Empty || transitionAtUtc == default ||
            targetStatus is not (DbLuaMShipSnapshotStatus.Stored or DbLuaMShipSnapshotStatus.Active) ||
            expectedSourceStatus is not (DbLuaMShipSnapshotStatus.Restoring or DbLuaMShipSnapshotStatus.Active))
        {
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);
        }

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == shipId, cancel);
            if (snapshot == null)
                return new(LuaMShipPersistenceWriteStatus.NotFound, shipId);

            if (snapshot.OwnerUserId != ownerUserId.UserId)
                return new(LuaMShipPersistenceWriteStatus.OwnerMismatch, shipId);

            var lease = await db.DbContext.LuaMShipPresenceLeases
                .SingleOrDefaultAsync(value => value.ShipId == shipId, cancel);
            if (IsExactShipTransitionOutcome(
                    ToShipSnapshotRecord(snapshot, lease),
                    expectedRevision,
                    leaseId,
                    targetStatus,
                    keepLease))
            {
                return ShipAlreadyProcessed(snapshot, keepLease ? lease : null);
            }

            var initial = CheckShipIdentityRevision(snapshot, ownerUserId, expectedRevision);
            if (initial != null)
                return initial;
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Quarantined)
                return ShipQuarantined(snapshot);
            if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
                return ShipRetired(snapshot);
            if (snapshot.Status != expectedSourceStatus)
                return ShipInvalidState(snapshot);
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMShipPersistenceWriteStatus.InvalidRequest, snapshot.ShipId, snapshot.Revision);

            var transitionAt = AsUtc(transitionAtUtc);
            if (lease == null || lease.LeaseId != leaseId ||
                (keepLease && lease.ExpiresAtUtc <= transitionAt))
            {
                return new(
                    LuaMShipPersistenceWriteStatus.LeaseConflict,
                    snapshot.ShipId,
                    snapshot.Revision,
                    snapshot.Status,
                    lease?.LeaseId,
                    lease?.Revision);
            }

            snapshot.Status = targetStatus;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = MaxShipDate(snapshot.UpdatedAtUtc, transitionAt);
            if (targetStatus == DbLuaMShipSnapshotStatus.Active)
                snapshot.LastRestoredAtUtc = transitionAt;
            if (!keepLease)
                db.DbContext.LuaMShipPresenceLeases.Remove(lease);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMShipPersistenceWriteStatus.Success,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                keepLease ? lease.LeaseId : null,
                keepLease ? lease.Revision : null,
                ToShipSnapshotRecord(snapshot, keepLease ? lease : null));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveShipTransitionFailureAsync(
                shipId,
                ownerUserId,
                expectedRevision,
                leaseId,
                targetStatus,
                keepLease,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveShipTransitionFailureAsync(
                shipId,
                ownerUserId,
                expectedRevision,
                leaseId,
                targetStatus,
                keepLease,
                LuaMShipPersistenceWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM ship restore transition ({targetStatus}, {reason ?? "completed"}) " +
                            $"outcome requires re-read: {exception.Message}");
            return await ResolveShipTransitionFailureAsync(
                shipId,
                ownerUserId,
                expectedRevision,
                leaseId,
                targetStatus,
                keepLease,
                LuaMShipPersistenceWriteStatus.UnknownOutcome);
        }
    }

    private async Task<bool> RecoverExpiredLuaMShipLeaseAsync(
        Guid shipId,
        Guid leaseId,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var lease = await db.DbContext.LuaMShipPresenceLeases
                .SingleOrDefaultAsync(value => value.ShipId == shipId && value.LeaseId == leaseId, cancel);
            if (lease == null || lease.ExpiresAtUtc > nowUtc)
                return false;

            var snapshot = await db.DbContext.LuaMShipSnapshots
                .SingleOrDefaultAsync(value => value.ShipId == shipId, cancel);
            if (snapshot == null)
                return false;

            if (snapshot.Status == DbLuaMShipSnapshotStatus.Retired)
            {
                // Retirement is terminal. Only discard the impossible stale lease.
            }
            else if (snapshot.Status is DbLuaMShipSnapshotStatus.Restoring or DbLuaMShipSnapshotStatus.Active)
            {
                snapshot.UpdatedAtUtc = MaxShipDate(snapshot.UpdatedAtUtc, nowUtc);
                snapshot.Status = DbLuaMShipSnapshotStatus.Stored;
                if (snapshot.Revision < long.MaxValue)
                    snapshot.Revision++;
            }
            else if (snapshot.Status != DbLuaMShipSnapshotStatus.Quarantined)
            {
                snapshot.UpdatedAtUtc = MaxShipDate(snapshot.UpdatedAtUtc, nowUtc);
                snapshot.Status = DbLuaMShipSnapshotStatus.Quarantined;
                if (snapshot.Revision < long.MaxValue)
                    snapshot.Revision++;
                snapshot.QuarantinedAtUtc = snapshot.UpdatedAtUtc;
                snapshot.QuarantineReason = "inconsistent expired ship lease reconciled fail-closed";
            }

            db.DbContext.LuaMShipPresenceLeases.Remove(lease);
            await db.DbContext.SaveChangesAsync(cancel);
            await transaction.CommitAsync(cancel);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM expired ship lease recovery lost a race: {exception.Message}");
            return false;
        }
    }

    private Task<LuaMShipPersistenceWriteResult> ResolveShipStoreFailureAsync(
        LuaMShipSnapshotStoreRequest request,
        LuaMShipPersistenceWriteStatus fallback)
        => ResolveExactShipWriteFailureAsync(
            request.ShipId,
            request.OwnerUserId,
            fallback,
            snapshot => IsExactShipStoreOutcome(snapshot, request));

    private Task<LuaMShipPersistenceWriteResult> ResolveShipClaimFailureAsync(
        LuaMShipRestoreClaimRequest request,
        LuaMShipPersistenceWriteStatus fallback)
        => ResolveExactShipWriteFailureAsync(
            request.ShipId,
            request.OwnerUserId,
            fallback,
            snapshot => IsExactShipClaimOutcome(snapshot, request));

    private Task<LuaMShipPersistenceWriteResult> ResolveShipTransitionFailureAsync(
        Guid shipId,
        NetUserId ownerUserId,
        long expectedRevision,
        Guid leaseId,
        DbLuaMShipSnapshotStatus targetStatus,
        bool keepLease,
        LuaMShipPersistenceWriteStatus fallback)
        => ResolveExactShipWriteFailureAsync(
            shipId,
            ownerUserId,
            fallback,
            snapshot => IsExactShipTransitionOutcome(
                snapshot,
                expectedRevision,
                leaseId,
                targetStatus,
                keepLease));

    private Task<LuaMShipPersistenceWriteResult> ResolveShipRenewFailureAsync(
        LuaMShipLeaseRenewRequest request,
        LuaMShipPersistenceWriteStatus fallback)
        => ResolveExactShipWriteFailureAsync(
            request.ShipId,
            request.OwnerUserId,
            fallback,
            snapshot => IsExactShipRenewOutcome(snapshot, request));

    private async Task<LuaMShipPersistenceWriteResult> ResolveExactShipWriteFailureAsync(
        Guid shipId,
        NetUserId ownerUserId,
        LuaMShipPersistenceWriteStatus fallback,
        Func<LuaMShipSnapshotRecord, bool> isExactOutcome)
    {
        var snapshot = await GetLuaMShipSnapshotAsync(shipId, ownerUserId, CancellationToken.None);
        if (snapshot == null)
            return new(fallback, shipId);
        if (isExactOutcome(snapshot))
            return ShipAlreadyProcessed(snapshot);

        return ShipWriteFailure(fallback, snapshot);
    }

    private async Task<LuaMShipPersistenceWriteResult> ResolveShipWriteFailureAsync(
        Guid shipId,
        NetUserId ownerUserId,
        LuaMShipPersistenceWriteStatus fallback)
    {
        var snapshot = await GetLuaMShipSnapshotAsync(shipId, ownerUserId, CancellationToken.None);
        if (snapshot == null)
            return new(fallback, shipId);

        return ShipWriteFailure(fallback, snapshot);
    }

    private static LuaMShipPersistenceWriteResult ShipWriteFailure(
        LuaMShipPersistenceWriteStatus fallback,
        LuaMShipSnapshotRecord snapshot)
        => new(
            fallback,
            snapshot.ShipId,
            snapshot.Revision,
            snapshot.Status,
            snapshot.LeaseId,
            snapshot.LeaseRevision,
            snapshot);

    private static LuaMShipPersistenceWriteResult ShipAlreadyProcessed(
        LuaMShipSnapshot snapshot,
        LuaMShipPresenceLease? lease)
        => new(
            LuaMShipPersistenceWriteStatus.AlreadyProcessed,
            snapshot.ShipId,
            snapshot.Revision,
            snapshot.Status,
            lease?.LeaseId,
            lease?.Revision,
            ToShipSnapshotRecord(snapshot, lease));

    private static LuaMShipPersistenceWriteResult ShipAlreadyProcessed(LuaMShipSnapshotRecord snapshot)
        => new(
            LuaMShipPersistenceWriteStatus.AlreadyProcessed,
            snapshot.ShipId,
            snapshot.Revision,
            snapshot.Status,
            snapshot.LeaseId,
            snapshot.LeaseRevision,
            snapshot);

    private static bool IsExactShipStoreOutcome(
        LuaMShipSnapshotRecord snapshot,
        LuaMShipSnapshotStoreRequest request)
    {
        var desiredRevision = request.ExpectedRevision == null
            ? 0L
            : request.ExpectedRevision == long.MaxValue
                ? -1L
                : request.ExpectedRevision.Value + 1;
        return snapshot.OwnerUserId == request.OwnerUserId &&
               snapshot.Revision == desiredRevision &&
               snapshot.PayloadRevision == request.PayloadRevision &&
               snapshot.Status == DbLuaMShipSnapshotStatus.Stored &&
               snapshot.LeaseId == null &&
               snapshot.VesselPrototypeId == request.VesselPrototypeId &&
               snapshot.ShipName == request.ShipName &&
               snapshot.ShipNameSuffix == request.ShipNameSuffix &&
               snapshot.PurchasePrice == request.PurchasePrice &&
               snapshot.PurchasedWithVoucher == request.PurchasedWithVoucher &&
               snapshot.SchemaVersion == request.SchemaVersion &&
               snapshot.FormatVersion == request.FormatVersion &&
               snapshot.Payload.AsSpan().SequenceEqual(request.Payload) &&
               string.Equals(snapshot.PayloadHash, request.PayloadHash, StringComparison.OrdinalIgnoreCase) &&
               snapshot.PayloadSizeBytes == request.PayloadSizeBytes &&
               snapshot.EntityCount == request.EntityCount &&
               snapshot.SourceBuildVersion == request.SourceBuildVersion &&
               string.Equals(
                   snapshot.PrototypeManifestHash,
                   request.PrototypeManifestHash,
                   StringComparison.OrdinalIgnoreCase) &&
               snapshot.SourceRoundId == request.SourceRoundId;
    }

    private static bool IsExactShipClaimOutcome(
        LuaMShipSnapshotRecord snapshot,
        LuaMShipRestoreClaimRequest request)
        => request.ExpectedRevision < long.MaxValue &&
           snapshot.OwnerUserId == request.OwnerUserId &&
           snapshot.Revision == request.ExpectedRevision + 1 &&
           snapshot.Status == DbLuaMShipSnapshotStatus.Restoring &&
           snapshot.LastRestoreRoundId == request.RestoreRoundId &&
           snapshot.LeaseId == request.LeaseId &&
           snapshot.LeaseServerInstanceId == request.ServerInstanceId &&
           snapshot.LeaseRoundId == request.RestoreRoundId &&
           snapshot.LeaseRevision == 0 &&
           snapshot.LeaseRenewedAtUtc is { } renewedAt &&
           AreShipDatesEquivalent(renewedAt, request.ClaimedAtUtc) &&
           snapshot.LeaseExpiresAtUtc is { } expiresAt &&
           AreShipDatesEquivalent(expiresAt, request.LeaseExpiresAtUtc);

    private static bool IsExactShipTransitionOutcome(
        LuaMShipSnapshotRecord snapshot,
        long expectedRevision,
        Guid leaseId,
        DbLuaMShipSnapshotStatus targetStatus,
        bool keepLease)
        => expectedRevision < long.MaxValue &&
           snapshot.Revision == expectedRevision + 1 &&
           snapshot.Status == targetStatus &&
           (keepLease
               ? snapshot.LeaseId == leaseId && snapshot.LeaseRevision != null
               : snapshot.LeaseId == null && snapshot.LeaseRevision == null);

    private static bool IsExactShipRenewOutcome(
        LuaMShipSnapshotRecord snapshot,
        LuaMShipLeaseRenewRequest request)
        => request.ExpectedLeaseRevision < long.MaxValue &&
           snapshot.OwnerUserId == request.OwnerUserId &&
           snapshot.Revision == request.ExpectedSnapshotRevision &&
           snapshot.Status is DbLuaMShipSnapshotStatus.Restoring or DbLuaMShipSnapshotStatus.Active &&
           snapshot.LeaseId == request.LeaseId &&
           snapshot.LeaseRevision == request.ExpectedLeaseRevision + 1 &&
           snapshot.LeaseRenewedAtUtc is { } renewedAt &&
           AreShipDatesEquivalent(renewedAt, request.RenewedAtUtc) &&
           snapshot.LeaseExpiresAtUtc is { } expiresAt &&
           AreShipDatesEquivalent(expiresAt, request.LeaseExpiresAtUtc);

    private static bool AreShipDatesEquivalent(DateTime actual, DateTime requested)
        => Math.Abs((AsPersistedShipUtc(actual) - AsPersistedShipUtc(requested)).Ticks) <=
           TimeSpan.TicksPerMillisecond;

    private static DateTime AsPersistedShipUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private async Task<LuaMShipPersistenceWriteResult> ResolveShipRetirementFailureAsync(
        LuaMShipSnapshotRetireRequest request)
    {
        var snapshot = await GetLuaMShipSnapshotAsync(
            request.ShipId,
            request.OwnerUserId,
            CancellationToken.None);
        if (snapshot?.Status == DbLuaMShipSnapshotStatus.Retired &&
            snapshot.RetirementOperationId == request.OperationId && snapshot.LeaseId == null)
        {
            return new(
                LuaMShipPersistenceWriteStatus.AlreadyProcessed,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                Snapshot: snapshot);
        }

        return snapshot == null
            ? new(LuaMShipPersistenceWriteStatus.UnknownOutcome, request.ShipId)
            : new(
                LuaMShipPersistenceWriteStatus.UnknownOutcome,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status,
                snapshot.LeaseId,
                snapshot.LeaseRevision,
                snapshot);
    }

    private static LuaMShipPersistenceWriteResult? CheckShipIdentityRevision(
        LuaMShipSnapshot snapshot,
        NetUserId ownerUserId,
        long expectedRevision)
    {
        if (snapshot.OwnerUserId != ownerUserId.UserId)
            return new(LuaMShipPersistenceWriteStatus.OwnerMismatch, snapshot.ShipId);
        if (snapshot.Revision != expectedRevision)
        {
            return new(
                LuaMShipPersistenceWriteStatus.RevisionConflict,
                snapshot.ShipId,
                snapshot.Revision,
                snapshot.Status);
        }

        return null;
    }

    private static LuaMShipPersistenceWriteResult ShipInvalidState(LuaMShipSnapshot snapshot)
        => new(
            LuaMShipPersistenceWriteStatus.InvalidState,
            snapshot.ShipId,
            snapshot.Revision,
            snapshot.Status);

    private static LuaMShipPersistenceWriteResult ShipQuarantined(LuaMShipSnapshot snapshot)
        => new(
            LuaMShipPersistenceWriteStatus.Quarantined,
            snapshot.ShipId,
            snapshot.Revision,
            snapshot.Status);

    private static LuaMShipPersistenceWriteResult ShipRetired(LuaMShipSnapshot snapshot)
        => new(
            LuaMShipPersistenceWriteStatus.Retired,
            snapshot.ShipId,
            snapshot.Revision,
            snapshot.Status);

    private static LuaMShipSnapshotRecord ToShipSnapshotRecord(
        LuaMShipSnapshot snapshot,
        LuaMShipPresenceLease? lease)
        => new(
            snapshot.ShipId,
            new NetUserId(snapshot.OwnerUserId),
            snapshot.Revision,
            snapshot.PayloadRevision,
            snapshot.Status,
            snapshot.VesselPrototypeId,
            snapshot.ShipName,
            snapshot.ShipNameSuffix,
            snapshot.PurchasePrice,
            snapshot.PurchasedWithVoucher,
            snapshot.SchemaVersion,
            snapshot.FormatVersion,
            snapshot.Payload.ToArray(),
            snapshot.PayloadHash,
            snapshot.PayloadSizeBytes,
            snapshot.EntityCount,
            snapshot.SourceBuildVersion,
            snapshot.PrototypeManifestHash,
            snapshot.SourceRoundId,
            snapshot.LastRestoreRoundId,
            snapshot.CreatedAtUtc,
            snapshot.StoredAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.LastRestoredAtUtc,
            snapshot.QuarantinedAtUtc,
            snapshot.QuarantineReason,
            snapshot.RetiredAtUtc,
            snapshot.RetirementReason,
            snapshot.RetirementOperationId,
            lease?.LeaseId,
            lease?.ServerInstanceId,
            lease?.RoundId,
            lease?.Revision,
            lease?.ExpiresAtUtc,
            lease?.RenewedAtUtc);

    private static bool IsValidShipStoreRequest(LuaMShipSnapshotStoreRequest request)
        => request.ShipId != Guid.Empty && request.OwnerUserId.UserId != Guid.Empty &&
           request.ExpectedRevision is null or >= 0 &&
           request.PayloadRevision > 0 &&
           request.LeaseId != Guid.Empty &&
           request.VesselPrototypeId is { Length: > 0 } &&
           !string.IsNullOrWhiteSpace(request.VesselPrototypeId) &&
           request.VesselPrototypeId.Length <= LuaMShipPersistenceLimits.MaxVesselPrototypeIdLength &&
           request.ShipName is { Length: > 0 } &&
           !string.IsNullOrWhiteSpace(request.ShipName) &&
           request.ShipName.Length <= LuaMShipPersistenceLimits.MaxShipNameLength &&
           (request.ShipNameSuffix == null ||
            request.ShipNameSuffix.Length <= LuaMShipPersistenceLimits.MaxShipNameSuffixLength) &&
           request.PurchasePrice >= 0 &&
           request.SourceRoundId >= 0 && request.SchemaVersion > 0 && request.FormatVersion > 0 &&
           request.StoredAtUtc != default &&
           request.Payload is { Length: > 0 and <= LuaMShipPersistenceLimits.MaxPayloadBytes } &&
           request.PayloadSizeBytes == request.Payload.Length &&
           request.EntityCount is > 0 and <= LuaMShipPersistenceLimits.MaxEntityCount &&
           request.SourceBuildVersion is { Length: > 0 } &&
           request.SourceBuildVersion.Length <= LuaMShipPersistenceLimits.MaxBuildVersionLength &&
           IsShipSha256(request.PayloadHash) && IsShipSha256(request.PrototypeManifestHash) &&
           string.Equals(
               Convert.ToHexString(SHA256.HashData(request.Payload)),
               NormalizeShipSha256(request.PayloadHash),
               StringComparison.Ordinal);

    private static bool IsValidShipClaimRequest(LuaMShipRestoreClaimRequest request)
    {
        if (request.ShipId == Guid.Empty || request.OwnerUserId.UserId == Guid.Empty ||
            request.ExpectedRevision < 0 || request.RestoreRoundId < 0 || request.LeaseId == Guid.Empty ||
            request.ServerInstanceId is not { Length: > 0 } ||
            request.ServerInstanceId.Length > LuaMShipPersistenceLimits.MaxServerInstanceIdLength ||
            request.ClaimedAtUtc == default || request.LeaseExpiresAtUtc == default)
        {
            return false;
        }

        return AsUtc(request.ClaimedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);
    }

    private static bool IsValidShipLeaseRenewRequest(LuaMShipLeaseRenewRequest request)
        => request.ShipId != Guid.Empty && request.OwnerUserId.UserId != Guid.Empty &&
           request.ExpectedSnapshotRevision >= 0 && request.LeaseId != Guid.Empty &&
           request.ExpectedLeaseRevision >= 0 && request.RenewedAtUtc != default &&
           request.LeaseExpiresAtUtc != default &&
           AsUtc(request.RenewedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);

    private static bool IsValidShipQuarantineRequest(LuaMShipSnapshotQuarantineRequest request)
        => request.ShipId != Guid.Empty && request.OwnerUserId.UserId != Guid.Empty &&
           request.ExpectedRevision >= 0 && request.LeaseId != Guid.Empty &&
           request.QuarantinedAtUtc != default && request.Reason is { Length: > 0 } &&
           !string.IsNullOrWhiteSpace(request.Reason) &&
           request.Reason.Length <= LuaMShipPersistenceLimits.MaxReasonLength;

    private static bool IsValidShipRetireRequest(LuaMShipSnapshotRetireRequest request)
        => request.OperationId != Guid.Empty && request.ShipId != Guid.Empty &&
           request.OwnerUserId.UserId != Guid.Empty && request.ExpectedRevision >= 0 &&
           request.LeaseId != Guid.Empty && request.RetiredAtUtc != default &&
           request.Reason is { Length: > 0 } && !string.IsNullOrWhiteSpace(request.Reason) &&
           request.Reason.Length <= LuaMShipPersistenceLimits.MaxReasonLength;

    private static bool IsShipSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeShipSha256(string value)
        => value.ToUpperInvariant();

    private static DateTime MaxShipDate(DateTime first, DateTime second)
        => first >= second ? first : second;
}
