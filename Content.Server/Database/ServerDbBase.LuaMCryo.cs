using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public abstract partial class ServerDbBase
{
    public async Task<bool> ValidateLuaMDeepCryoProfileAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
    {
        if (profileId <= 0 || slot < 0)
            return false;

        await using var db = await GetDb(cancel);
        return await FindActiveCryoProfilePreferenceIdAsync(
            db.DbContext,
            userId,
            profileId,
            slot,
            cancel) != null;
    }

    public async Task<LuaMDeepCryoSnapshotRecord?> GetLuaMDeepCryoSnapshotAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
    {
        if (profileId <= 0 || slot < 0)
            return null;

        await using var db = await GetDb(cancel);
        if (await FindActiveCryoProfilePreferenceIdAsync(
                db.DbContext,
                userId,
                profileId,
                slot,
                cancel) == null)
        {
            return null;
        }

        var snapshot = await db.DbContext.LuaMDeepCryoSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId &&
                                           value.PlayerUserId == userId.UserId &&
                                           value.Slot == slot &&
                                           value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed,
                cancel);
        if (snapshot == null)
            return null;

        var lease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId && value.SnapshotId == snapshot.Id, cancel);
        return ToDeepCryoSnapshotRecord(snapshot, lease);
    }

    public async Task<LuaMDeepCryoWriteResult> StoreLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoStoreRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidDeepCryoStoreRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var operationIdentityKey = CreateDeepCryoStoreIdentityKey(request);
        var replay = await FindDeepCryoReplayAsync(request.OperationId, operationIdentityKey, cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindDeepCryoReplayAsync(
                db.DbContext,
                request.OperationId,
                operationIdentityKey,
                cancel);
            if (replay != null)
                return replay;

            var profile = await FindActiveCryoProfileForMutationAsync(
                db.DbContext,
                request.UserId,
                request.ProfileId,
                request.Slot,
                cancel);
            if (profile == null)
                return new(LuaMDeepCryoWriteStatus.ProfileNotActive);
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest);

            var active = await db.DbContext.LuaMDeepCryoSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId &&
                                               value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed,
                    cancel);
            if (active != null)
            {
                return new(
                    active.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined
                        ? LuaMDeepCryoWriteStatus.Quarantined
                        : LuaMDeepCryoWriteStatus.InvalidState,
                    active.Id,
                    active.Revision,
                    active.Status);
            }

            var now = AsUtc(request.StoredAtUtc);
            var career = await db.DbContext.LuaMCharacterCareers
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (career == null)
            {
                career = new LuaMCharacterCareer
                {
                    ProfileId = request.ProfileId,
                    Status = DbLuaMCharacterCareerStatus.CryoStored,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                db.DbContext.LuaMCharacterCareers.Add(career);
            }
            else
            {
                if (career.Status != DbLuaMCharacterCareerStatus.Playable)
                    return new(LuaMDeepCryoWriteStatus.InvalidState, Revision: career.Revision);
                if (career.Revision == long.MaxValue)
                    return new(LuaMDeepCryoWriteStatus.InvalidRequest, Revision: career.Revision);

                career.Status = DbLuaMCharacterCareerStatus.CryoStored;
                career.Revision++;
                career.UpdatedAtUtc = now;
            }

            var snapshot = new LuaMDeepCryoSnapshot
            {
                PlayerUserId = request.UserId.UserId,
                PreferenceId = profile.PreferenceId,
                ProfileId = request.ProfileId,
                Slot = request.Slot,
                SourceRoundId = request.SourceRoundId,
                Status = DbLuaMDeepCryoSnapshotStatus.Stored,
                FormatVersion = request.FormatVersion,
                Payload = request.Payload.ToArray(),
                PayloadHash = NormalizeSha256(request.PayloadHash),
                PayloadSizeBytes = request.PayloadSizeBytes,
                EntityCount = request.EntityCount,
                SourceBuildVersion = request.SourceBuildVersion,
                PrototypeManifestHash = NormalizeSha256(request.PrototypeManifestHash),
                StoredAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.DbContext.LuaMDeepCryoSnapshots.Add(snapshot);
            profile.LifecycleRevision++;

            // The generated snapshot id and the operation proof still commit as one transaction.
            await db.DbContext.SaveChangesAsync(cancel);
            db.DbContext.LuaMDeepCryoOperations.Add(new LuaMDeepCryoOperation
            {
                OperationId = request.OperationId,
                OperationIdentityKey = operationIdentityKey,
                SnapshotId = snapshot.Id,
                ProfileId = snapshot.ProfileId,
                Kind = DbLuaMDeepCryoOperationKind.Store,
                ResultStatus = snapshot.Status,
                ResultRevision = snapshot.Revision,
                RoundId = request.SourceRoundId,
                CreatedAtUtc = now,
            });
            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);

            return new(
                LuaMDeepCryoWriteStatus.Success,
                snapshot.Id,
                snapshot.Revision,
                snapshot.Status,
                Snapshot: ToDeepCryoSnapshotRecord(snapshot, null));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                null,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                null,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM deep-cryo store outcome requires re-read: {exception.Message}");
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                null,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMDeepCryoWriteResult> ClaimLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoClaimRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidDeepCryoClaimRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var operationIdentityKey = CreateDeepCryoClaimIdentityKey(request);
        var replay = await FindDeepCryoReplayAsync(request.OperationId, operationIdentityKey, cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindDeepCryoReplayAsync(
                db.DbContext,
                request.OperationId,
                operationIdentityKey,
                cancel);
            if (replay != null)
                return replay;

            var profile = await FindActiveCryoProfileForMutationAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    cancel);
            if (profile == null)
            {
                return new(LuaMDeepCryoWriteStatus.ProfileNotActive);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest);

            var snapshot = await db.DbContext.LuaMDeepCryoSnapshots
                .SingleOrDefaultAsync(value => value.Id == request.SnapshotId &&
                                               value.ProfileId == request.ProfileId &&
                                               value.PlayerUserId == request.UserId.UserId &&
                                               value.Slot == request.Slot,
                    cancel);
            if (snapshot == null)
                return new(LuaMDeepCryoWriteStatus.NotFound);
            if (snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined)
                return new(LuaMDeepCryoWriteStatus.Quarantined, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (snapshot.Revision != request.ExpectedRevision)
                return new(LuaMDeepCryoWriteStatus.RevisionConflict, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (snapshot.Status != DbLuaMDeepCryoSnapshotStatus.Stored)
                return new(LuaMDeepCryoWriteStatus.InvalidState, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            var existingLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId ||
                                               value.SnapshotId == request.SnapshotId ||
                                               value.LeaseId == request.LeaseId,
                    cancel);
            if (existingLease != null)
                return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status,
                    existingLease.LeaseId);

            var leaseTokenUsed = await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                .AnyAsync(value => value.Kind == DbLuaMDeepCryoOperationKind.ClaimRestore &&
                                   value.LeaseId == request.LeaseId,
                    cancel);
            if (leaseTokenUsed)
                return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status);

            var career = await db.DbContext.LuaMCharacterCareers
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (career == null || career.Status != DbLuaMCharacterCareerStatus.CryoStored)
                return new(LuaMDeepCryoWriteStatus.InvalidState, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (career.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            var claimedAt = AsUtc(request.ClaimedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            if (claimedAt < snapshot.UpdatedAtUtc || expiresAt <= DateTime.UtcNow)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            snapshot.Status = DbLuaMDeepCryoSnapshotStatus.Restoring;
            snapshot.LastRestoreRoundId = request.RestoreRoundId;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = claimedAt;
            career.Status = DbLuaMCharacterCareerStatus.RecoveryPending;
            career.Revision++;
            career.UpdatedAtUtc = claimedAt;
            profile.LifecycleRevision++;

            var lease = new LuaMCharacterPresenceLease
            {
                ProfileId = request.ProfileId,
                SnapshotId = request.SnapshotId,
                LeaseId = request.LeaseId,
                ServerInstanceId = request.ServerInstanceId,
                RestoreRoundId = request.RestoreRoundId,
                AcquiredAtUtc = claimedAt,
                RenewedAtUtc = claimedAt,
                ExpiresAtUtc = expiresAt,
            };
            db.DbContext.LuaMCharacterPresenceLeases.Add(lease);
            db.DbContext.LuaMDeepCryoOperations.Add(new LuaMDeepCryoOperation
            {
                OperationId = request.OperationId,
                OperationIdentityKey = operationIdentityKey,
                SnapshotId = snapshot.Id,
                ProfileId = snapshot.ProfileId,
                Kind = DbLuaMDeepCryoOperationKind.ClaimRestore,
                ResultStatus = snapshot.Status,
                ResultRevision = snapshot.Revision,
                LeaseId = lease.LeaseId,
                RoundId = request.RestoreRoundId,
                CreatedAtUtc = claimedAt,
            });

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMDeepCryoWriteStatus.Success,
                snapshot.Id,
                snapshot.Revision,
                snapshot.Status,
                lease.LeaseId,
                ToDeepCryoSnapshotRecord(snapshot, lease));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM deep-cryo claim outcome requires re-read: {exception.Message}");
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public Task<LuaMDeepCryoWriteResult> CompleteLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoCompleteRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMDeepCryoOperationKind.CompleteRestore,
            DbLuaMDeepCryoSnapshotStatus.Consumed,
            AllowStored: false,
            AllowRestoring: true,
            AllowQuarantined: false,
            Reason: null,
            CreatedAtUtc: request.CompletedAtUtc), cancel);

    public Task<LuaMDeepCryoWriteResult> AbortLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoAbortRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMDeepCryoOperationKind.AbortRestore,
            DbLuaMDeepCryoSnapshotStatus.Stored,
            AllowStored: false,
            AllowRestoring: true,
            AllowQuarantined: false,
            Reason: request.Reason,
            CreatedAtUtc: request.AbortedAtUtc), cancel);

    public Task<LuaMDeepCryoWriteResult> DiscardLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoDiscardRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMDeepCryoOperationKind.Discard,
            DbLuaMDeepCryoSnapshotStatus.Consumed,
            AllowStored: true,
            AllowRestoring: true,
            AllowQuarantined: false,
            Reason: null,
            CreatedAtUtc: request.DiscardedAtUtc), cancel);

    public Task<LuaMDeepCryoWriteResult> QuarantineLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoQuarantineRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedRevision,
            request.LeaseId,
            DbLuaMDeepCryoOperationKind.Quarantine,
            DbLuaMDeepCryoSnapshotStatus.Quarantined,
            AllowStored: true,
            AllowRestoring: true,
            AllowQuarantined: false,
            Reason: request.Reason,
            CreatedAtUtc: request.QuarantinedAtUtc), cancel);

    private async Task<LuaMDeepCryoWriteResult> TransitionLuaMDeepCryoSnapshotAsync(
        DeepCryoTransitionRequest request,
        CancellationToken cancel)
    {
        if (!IsValidDeepCryoTransitionRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var operationIdentityKey = CreateDeepCryoTransitionIdentityKey(request);
        var replay = await FindDeepCryoReplayAsync(request.OperationId, operationIdentityKey, cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindDeepCryoReplayAsync(
                db.DbContext,
                request.OperationId,
                operationIdentityKey,
                cancel);
            if (replay != null)
                return replay;

            var profile = await FindActiveCryoProfileForMutationAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    cancel);
            if (profile == null)
            {
                return new(LuaMDeepCryoWriteStatus.ProfileNotActive);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest);

            var snapshot = await db.DbContext.LuaMDeepCryoSnapshots
                .SingleOrDefaultAsync(value => value.Id == request.SnapshotId &&
                                               value.ProfileId == request.ProfileId &&
                                               value.PlayerUserId == request.UserId.UserId &&
                                               value.Slot == request.Slot,
                    cancel);
            if (snapshot == null)
                return new(LuaMDeepCryoWriteStatus.NotFound);
            if (snapshot.Revision != request.ExpectedRevision)
                return new(LuaMDeepCryoWriteStatus.RevisionConflict, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (!IsAllowedDeepCryoSourceStatus(snapshot.Status, request))
            {
                return new(
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined
                        ? LuaMDeepCryoWriteStatus.Quarantined
                        : LuaMDeepCryoWriteStatus.InvalidState,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status);
            }
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            var now = AsUtc(request.CreatedAtUtc);
            if (now < snapshot.UpdatedAtUtc)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Restoring)
            {
                if (request.LeaseId == null || lease == null ||
                    lease.SnapshotId != snapshot.Id || lease.LeaseId != request.LeaseId.Value)
                {
                    return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status,
                        lease?.LeaseId);
                }

                if (request.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
                    (lease.ExpiresAtUtc <= now || lease.ExpiresAtUtc <= DateTime.UtcNow))
                {
                    return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status,
                        lease.LeaseId);
                }
            }
            else if (lease != null || request.LeaseId != null)
            {
                return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status,
                    lease?.LeaseId);
            }

            var career = await db.DbContext.LuaMCharacterCareers
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            var expectedCareerStatus = snapshot.Status switch
            {
                DbLuaMDeepCryoSnapshotStatus.Stored => DbLuaMCharacterCareerStatus.CryoStored,
                DbLuaMDeepCryoSnapshotStatus.Restoring => DbLuaMCharacterCareerStatus.RecoveryPending,
                DbLuaMDeepCryoSnapshotStatus.Quarantined => DbLuaMCharacterCareerStatus.Quarantined,
                _ => DbLuaMCharacterCareerStatus.Playable,
            };
            if (career == null || career.Status != expectedCareerStatus)
                return new(LuaMDeepCryoWriteStatus.InvalidState, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (career.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            snapshot.Status = request.TargetStatus;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = now;
            snapshot.ConsumedAtUtc = request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Consumed ? now : null;
            snapshot.QuarantinedAtUtc = request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined ? now : null;
            snapshot.QuarantineReason = request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined
                ? request.Reason
                : null;

            career.Status = request.TargetStatus switch
            {
                DbLuaMDeepCryoSnapshotStatus.Stored => DbLuaMCharacterCareerStatus.CryoStored,
                DbLuaMDeepCryoSnapshotStatus.Consumed => DbLuaMCharacterCareerStatus.Playable,
                DbLuaMDeepCryoSnapshotStatus.Quarantined => DbLuaMCharacterCareerStatus.Quarantined,
                _ => DbLuaMCharacterCareerStatus.RecoveryPending,
            };
            career.Revision++;
            career.UpdatedAtUtc = now;
            profile.LifecycleRevision++;

            if (lease != null)
                db.DbContext.LuaMCharacterPresenceLeases.Remove(lease);

            db.DbContext.LuaMDeepCryoOperations.Add(new LuaMDeepCryoOperation
            {
                OperationId = request.OperationId,
                OperationIdentityKey = operationIdentityKey,
                SnapshotId = snapshot.Id,
                ProfileId = snapshot.ProfileId,
                Kind = request.Kind,
                ResultStatus = snapshot.Status,
                ResultRevision = snapshot.Revision,
                LeaseId = request.LeaseId,
                RoundId = snapshot.LastRestoreRoundId ?? snapshot.SourceRoundId,
                Reason = request.Reason,
                CreatedAtUtc = now,
            });

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMDeepCryoWriteStatus.Success,
                snapshot.Id,
                snapshot.Revision,
                snapshot.Status,
                request.LeaseId,
                ToDeepCryoSnapshotRecord(snapshot, null));
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM deep-cryo transition outcome requires re-read: {exception.Message}");
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                operationIdentityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<int> RecoverExpiredLuaMDeepCryoLeasesAsync(
        DateTime nowUtc,
        int maxCount = 100,
        CancellationToken cancel = default)
    {
        if (nowUtc == default || maxCount is <= 0 or > 1000)
            return 0;

        var now = AsUtc(nowUtc);
        (int ProfileId, long SnapshotId, Guid LeaseId, DateTime AcquiredAtUtc, DateTime ExpiresAtUtc)[] candidates;
        await using (var db = await GetDb(cancel))
        {
            candidates = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                .Where(value => value.ExpiresAtUtc <= now)
                .OrderBy(value => value.ExpiresAtUtc)
                .ThenBy(value => value.ProfileId)
                .Take(maxCount)
                .Select(value => new ValueTuple<int, long, Guid, DateTime, DateTime>(
                    value.ProfileId,
                    value.SnapshotId,
                    value.LeaseId,
                    value.AcquiredAtUtc,
                    value.ExpiresAtUtc))
                .ToArrayAsync(cancel);
        }

        var recovered = 0;
        foreach (var candidate in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (await RecoverExpiredLuaMDeepCryoLeaseAsync(
                    candidate.ProfileId,
                    candidate.SnapshotId,
                    candidate.LeaseId,
                    candidate.AcquiredAtUtc,
                    candidate.ExpiresAtUtc,
                    now,
                    cancel))
            {
                recovered++;
            }
        }

        return recovered;
    }

    private async Task<bool> RecoverExpiredLuaMDeepCryoLeaseAsync(
        int profileId,
        long snapshotId,
        Guid leaseId,
        DateTime leaseAcquiredAtUtc,
        DateTime leaseExpiresAtUtc,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        var operationId = CreateDeepCryoRecoveryOperationId(
            profileId,
            snapshotId,
            leaseId,
            leaseAcquiredAtUtc,
            leaseExpiresAtUtc);
        var operationIdentityKey = CreateDeepCryoRecoveryIdentityKey(
            profileId,
            snapshotId,
            leaseId,
            leaseAcquiredAtUtc,
            leaseExpiresAtUtc);
        var replay = await FindDeepCryoReplayAsync(operationId, operationIdentityKey, cancel);
        if (replay != null)
            return false;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindDeepCryoReplayAsync(db.DbContext, operationId, operationIdentityKey, cancel);
            if (replay != null)
                return false;

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == profileId &&
                                               value.SnapshotId == snapshotId &&
                                               value.LeaseId == leaseId,
                    cancel);
            if (lease == null || lease.ExpiresAtUtc > nowUtc)
                return false;

            var snapshot = await db.DbContext.LuaMDeepCryoSnapshots
                .SingleOrDefaultAsync(value => value.Id == snapshotId && value.ProfileId == profileId, cancel);
            var profile = await db.DbContext.Profile
                .SingleOrDefaultAsync(value => value.Id == profileId, cancel);
            var career = await db.DbContext.LuaMCharacterCareers
                .SingleOrDefaultAsync(value => value.ProfileId == profileId, cancel);
            if (snapshot == null || profile == null)
                return false;

            var updatedAt = nowUtc < snapshot.UpdatedAtUtc ? snapshot.UpdatedAtUtc : nowUtc;
            var normalRecovery = snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Restoring &&
                                 snapshot.Revision < long.MaxValue &&
                                 !profile.IsArchived && profile.Slot.HasValue &&
                                 career is
                                 {
                                     Status: DbLuaMCharacterCareerStatus.RecoveryPending,
                                     Revision: < long.MaxValue,
                                 };
            var recoveryReason = "restore lease expired";

            if (normalRecovery)
            {
                snapshot.Status = DbLuaMDeepCryoSnapshotStatus.Stored;
                snapshot.Revision++;
                snapshot.UpdatedAtUtc = updatedAt;
                career!.Status = DbLuaMCharacterCareerStatus.CryoStored;
                career.Revision++;
                career.UpdatedAtUtc = updatedAt < career.UpdatedAtUtc ? career.UpdatedAtUtc : updatedAt;
            }
            else
            {
                recoveryReason = "inconsistent expired restore lease reconciled fail-closed";
                if (snapshot.Status != DbLuaMDeepCryoSnapshotStatus.Consumed)
                {
                    snapshot.Status = DbLuaMDeepCryoSnapshotStatus.Quarantined;
                    if (snapshot.Revision < long.MaxValue)
                        snapshot.Revision++;
                    snapshot.UpdatedAtUtc = updatedAt;
                    snapshot.ConsumedAtUtc = null;
                    snapshot.QuarantinedAtUtc = updatedAt;
                    snapshot.QuarantineReason = recoveryReason;

                    if (career == null)
                    {
                        career = new LuaMCharacterCareer
                        {
                            ProfileId = profileId,
                            Status = DbLuaMCharacterCareerStatus.Quarantined,
                            CreatedAtUtc = updatedAt,
                            UpdatedAtUtc = updatedAt,
                        };
                        db.DbContext.LuaMCharacterCareers.Add(career);
                    }
                    else
                    {
                        career.Status = DbLuaMCharacterCareerStatus.Quarantined;
                        if (career.Revision < long.MaxValue)
                            career.Revision++;
                        career.UpdatedAtUtc = updatedAt < career.UpdatedAtUtc ? career.UpdatedAtUtc : updatedAt;
                    }
                }
            }

            if (profile.LifecycleRevision < long.MaxValue)
                profile.LifecycleRevision++;
            else
                db.DbContext.Entry(profile).Property(value => value.IsArchived).IsModified = true;

            db.DbContext.LuaMCharacterPresenceLeases.Remove(lease);
            db.DbContext.LuaMDeepCryoOperations.Add(new LuaMDeepCryoOperation
            {
                OperationId = operationId,
                OperationIdentityKey = operationIdentityKey,
                SnapshotId = snapshot.Id,
                ProfileId = snapshot.ProfileId,
                Kind = DbLuaMDeepCryoOperationKind.RecoverExpiredLease,
                ResultStatus = snapshot.Status,
                ResultRevision = snapshot.Revision,
                LeaseId = lease.LeaseId,
                RoundId = lease.RestoreRoundId,
                Reason = recoveryReason,
                CreatedAtUtc = nowUtc,
            });

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return true;
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            replay = await FindDeepCryoReplayAsync(operationId, operationIdentityKey, CancellationToken.None);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            _ = await FindDeepCryoReplayAsync(operationId, operationIdentityKey, CancellationToken.None);
            return false;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM deep-cryo lease recovery outcome requires re-read: {exception.Message}");
            _ = await FindDeepCryoReplayAsync(operationId, operationIdentityKey, CancellationToken.None);
            return false;
        }
    }

    private async Task<LuaMDeepCryoWriteResult?> FindDeepCryoReplayAsync(
        Guid operationId,
        string operationIdentityKey,
        CancellationToken cancel)
    {
        await using var db = await GetDb(cancel);
        return await FindDeepCryoReplayAsync(db.DbContext, operationId, operationIdentityKey, cancel);
    }

    private static async Task<LuaMDeepCryoWriteResult?> FindDeepCryoReplayAsync(
        ServerDbContext db,
        Guid operationId,
        string operationIdentityKey,
        CancellationToken cancel)
    {
        var operation = await db.LuaMDeepCryoOperations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.OperationId == operationId, cancel);
        if (operation == null)
            return null;

        if (!string.Equals(operation.OperationIdentityKey, operationIdentityKey, StringComparison.Ordinal))
        {
            return new(
                LuaMDeepCryoWriteStatus.IdentityConflict,
                operation.SnapshotId,
                operation.ResultRevision,
                operation.ResultStatus,
                operation.LeaseId);
        }

        var snapshot = await db.LuaMDeepCryoSnapshots.AsNoTracking()
            .SingleAsync(value => value.Id == operation.SnapshotId, cancel);
        var lease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == snapshot.ProfileId && value.SnapshotId == snapshot.Id,
                cancel);
        var coherentLeaseState = operation.Kind == DbLuaMDeepCryoOperationKind.ClaimRestore
            ? operation.LeaseId.HasValue && lease?.LeaseId == operation.LeaseId
            : lease == null;
        var coherentCurrentState = snapshot.Revision == operation.ResultRevision &&
                                   snapshot.Status == operation.ResultStatus &&
                                   coherentLeaseState;
        return new(
            LuaMDeepCryoWriteStatus.AlreadyProcessed,
            operation.SnapshotId,
            operation.ResultRevision,
            operation.ResultStatus,
            operation.LeaseId,
            coherentCurrentState ? ToDeepCryoSnapshotRecord(snapshot, lease) : null);
    }

    private async Task<LuaMDeepCryoWriteResult> ResolveDeepCryoFailureAsync(
        Guid operationId,
        string operationIdentityKey,
        long? snapshotId,
        LuaMDeepCryoWriteStatus fallback)
    {
        var replay = await FindDeepCryoReplayAsync(operationId, operationIdentityKey, CancellationToken.None);
        if (replay != null)
            return replay;

        if (snapshotId == null)
            return new(fallback);

        await using var db = await GetDb(CancellationToken.None);
        var snapshot = await db.DbContext.LuaMDeepCryoSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == snapshotId.Value, CancellationToken.None);
        if (snapshot == null)
            return new(fallback, snapshotId);

        var lease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == snapshot.ProfileId && value.SnapshotId == snapshot.Id,
                CancellationToken.None);
        return new(
            fallback,
            snapshot.Id,
            snapshot.Revision,
            snapshot.Status,
            lease?.LeaseId,
            ToDeepCryoSnapshotRecord(snapshot, lease));
    }

    private static Task<int?> FindActiveCryoProfilePreferenceIdAsync(
        ServerDbContext db,
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel)
        => db.Profile.AsNoTracking()
            .Where(value => value.Id == profileId &&
                            value.Slot == slot &&
                            !value.IsArchived &&
                            value.Preference.UserId == userId.UserId)
            .Select(value => (int?) value.PreferenceId)
            .SingleOrDefaultAsync(cancel);

    private static Task<Profile?> FindActiveCryoProfileForMutationAsync(
        ServerDbContext db,
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel)
        => db.Profile
            .SingleOrDefaultAsync(value => value.Id == profileId &&
                                           value.Slot == slot &&
                                           !value.IsArchived &&
                                           value.Preference.UserId == userId.UserId,
                cancel);

    private static LuaMDeepCryoSnapshotRecord ToDeepCryoSnapshotRecord(
        LuaMDeepCryoSnapshot snapshot,
        LuaMCharacterPresenceLease? lease)
        => new(
            snapshot.Id,
            new NetUserId(snapshot.PlayerUserId),
            snapshot.PreferenceId,
            snapshot.ProfileId,
            snapshot.Slot,
            snapshot.SourceRoundId,
            snapshot.LastRestoreRoundId,
            snapshot.Status,
            snapshot.Revision,
            snapshot.FormatVersion,
            snapshot.Payload.ToArray(),
            snapshot.PayloadHash,
            snapshot.PayloadSizeBytes,
            snapshot.EntityCount,
            snapshot.SourceBuildVersion,
            snapshot.PrototypeManifestHash,
            snapshot.StoredAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.ConsumedAtUtc,
            snapshot.QuarantinedAtUtc,
            snapshot.QuarantineReason,
            lease?.LeaseId,
            lease?.ExpiresAtUtc);

    private static bool IsValidDeepCryoStoreRequest(LuaMDeepCryoStoreRequest request)
        => request.OperationId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 && request.SourceRoundId >= 0 &&
           request.FormatVersion > 0 && request.StoredAtUtc != default &&
           request.Payload != null && request.Payload.Length is > 0 and <= LuaMDeepCryoLimits.MaxPayloadBytes &&
           request.PayloadSizeBytes == request.Payload.Length &&
           request.EntityCount is > 0 and <= LuaMDeepCryoLimits.MaxEntityCount &&
           request.SourceBuildVersion is { Length: > 0 } &&
           request.SourceBuildVersion.Length <= LuaMDeepCryoLimits.MaxBuildVersionLength &&
           IsSha256Hex(request.PayloadHash) && IsSha256Hex(request.PrototypeManifestHash) &&
           string.Equals(
               Convert.ToHexString(SHA256.HashData(request.Payload)),
               NormalizeSha256(request.PayloadHash),
               StringComparison.Ordinal);

    private static bool IsValidDeepCryoClaimRequest(LuaMDeepCryoClaimRequest request)
    {
        if (request.OperationId == Guid.Empty || request.LeaseId == Guid.Empty ||
            request.ProfileId <= 0 || request.Slot < 0 || request.SnapshotId <= 0 ||
            request.ExpectedRevision < 0 || request.RestoreRoundId < 0 ||
            request.ServerInstanceId is not { Length: > 0 } ||
            request.ServerInstanceId.Length > LuaMDeepCryoLimits.MaxServerInstanceIdLength ||
            request.ClaimedAtUtc == default || request.LeaseExpiresAtUtc == default)
        {
            return false;
        }

        return AsUtc(request.ClaimedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);
    }

    private static bool IsValidDeepCryoTransitionRequest(DeepCryoTransitionRequest request)
    {
        if (request.OperationId == Guid.Empty || request.ProfileId <= 0 || request.Slot < 0 ||
            request.SnapshotId <= 0 || request.ExpectedRevision < 0 || request.CreatedAtUtc == default ||
            request.LeaseId == Guid.Empty ||
            request.Reason is { Length: > LuaMDeepCryoLimits.MaxReasonLength })
        {
            return false;
        }

        if (request.Kind is DbLuaMDeepCryoOperationKind.AbortRestore or DbLuaMDeepCryoOperationKind.Quarantine)
            return !string.IsNullOrWhiteSpace(request.Reason);

        return request.Reason == null;
    }

    private static bool IsAllowedDeepCryoSourceStatus(
        DbLuaMDeepCryoSnapshotStatus status,
        DeepCryoTransitionRequest request)
        => status switch
        {
            DbLuaMDeepCryoSnapshotStatus.Stored => request.AllowStored,
            DbLuaMDeepCryoSnapshotStatus.Restoring => request.AllowRestoring,
            DbLuaMDeepCryoSnapshotStatus.Quarantined => request.AllowQuarantined,
            _ => false,
        };

    private static bool IsSha256Hex(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeSha256(string value)
        => value.ToUpperInvariant();

    private static string CreateDeepCryoStoreIdentityKey(LuaMDeepCryoStoreRequest request)
        => HashDeepCryoIdentity(writer =>
        {
            writer.Write((int) DbLuaMDeepCryoOperationKind.Store);
            writer.Write(request.OperationId.ToByteArray());
            writer.Write(request.UserId.UserId.ToByteArray());
            writer.Write(request.ProfileId);
            writer.Write(request.Slot);
            writer.Write(request.SourceRoundId);
            writer.Write(request.FormatVersion);
            writer.Write(NormalizeSha256(request.PayloadHash));
            writer.Write(request.PayloadSizeBytes);
            writer.Write(request.EntityCount);
            writer.Write(request.SourceBuildVersion);
            writer.Write(NormalizeSha256(request.PrototypeManifestHash));
            writer.Write(AsUtc(request.StoredAtUtc).Ticks);
        });

    private static string CreateDeepCryoClaimIdentityKey(LuaMDeepCryoClaimRequest request)
        => HashDeepCryoIdentity(writer =>
        {
            writer.Write((int) DbLuaMDeepCryoOperationKind.ClaimRestore);
            writer.Write(request.OperationId.ToByteArray());
            writer.Write(request.UserId.UserId.ToByteArray());
            writer.Write(request.ProfileId);
            writer.Write(request.Slot);
            writer.Write(request.SnapshotId);
            writer.Write(request.ExpectedRevision);
            writer.Write(request.RestoreRoundId);
            writer.Write(request.LeaseId.ToByteArray());
            writer.Write(request.ServerInstanceId);
            writer.Write(AsUtc(request.ClaimedAtUtc).Ticks);
            writer.Write(AsUtc(request.LeaseExpiresAtUtc).Ticks);
        });

    private static string CreateDeepCryoTransitionIdentityKey(DeepCryoTransitionRequest request)
        => HashDeepCryoIdentity(writer =>
        {
            writer.Write((int) request.Kind);
            writer.Write(request.OperationId.ToByteArray());
            writer.Write(request.UserId.UserId.ToByteArray());
            writer.Write(request.ProfileId);
            writer.Write(request.Slot);
            writer.Write(request.SnapshotId);
            writer.Write(request.ExpectedRevision);
            writer.Write(request.LeaseId.HasValue);
            if (request.LeaseId.HasValue)
                writer.Write(request.LeaseId.Value.ToByteArray());
            writer.Write((int) request.TargetStatus);
            writer.Write(request.Reason ?? string.Empty);
            writer.Write(AsUtc(request.CreatedAtUtc).Ticks);
        });

    private static string CreateDeepCryoRecoveryIdentityKey(
        int profileId,
        long snapshotId,
        Guid leaseId,
        DateTime leaseAcquiredAtUtc,
        DateTime leaseExpiresAtUtc)
        => HashDeepCryoIdentity(writer =>
        {
            writer.Write((int) DbLuaMDeepCryoOperationKind.RecoverExpiredLease);
            writer.Write(profileId);
            writer.Write(snapshotId);
            writer.Write(leaseId.ToByteArray());
            writer.Write(AsUtc(leaseAcquiredAtUtc).Ticks);
            writer.Write(AsUtc(leaseExpiresAtUtc).Ticks);
        });

    private static Guid CreateDeepCryoRecoveryOperationId(
        int profileId,
        long snapshotId,
        Guid leaseId,
        DateTime leaseAcquiredAtUtc,
        DateTime leaseExpiresAtUtc)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("luam-deep-cryo-recovery-id-v1");
            writer.Write(profileId);
            writer.Write(snapshotId);
            writer.Write(leaseId.ToByteArray());
            writer.Write(AsUtc(leaseAcquiredAtUtc).Ticks);
            writer.Write(AsUtc(leaseExpiresAtUtc).Ticks);
        }

        var hash = SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int) buffer.Length)));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string HashDeepCryoIdentity(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("luam-deep-cryo-operation-v1");
            write(writer);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int) buffer.Length))));
    }

    private sealed record DeepCryoTransitionRequest(
        Guid OperationId,
        NetUserId UserId,
        int ProfileId,
        int Slot,
        long SnapshotId,
        long ExpectedRevision,
        Guid? LeaseId,
        DbLuaMDeepCryoOperationKind Kind,
        DbLuaMDeepCryoSnapshotStatus TargetStatus,
        bool AllowStored,
        bool AllowRestoring,
        bool AllowQuarantined,
        string? Reason,
        DateTime CreatedAtUtc);
}
