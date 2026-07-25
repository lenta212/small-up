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
    internal Func<CancellationToken, Task>? LuaMDeepCryoStoreNoActiveSnapshotObservedForTesting { get; set; }

    public async Task<LuaMDeepCryoStorePrecondition?> GetLuaMDeepCryoStorePreconditionAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
    {
        if (profileId <= 0 || slot < 0)
            return null;

        await using var db = await GetDb(cancel);
        var lifecycleRevision = await db.DbContext.Profile.AsNoTracking()
            .Where(value => value.Id == profileId &&
                            value.Slot == slot &&
                            !value.IsArchived &&
                            value.Preference.UserId == userId.UserId)
            .Select(value => (long?) value.LifecycleRevision)
            .SingleOrDefaultAsync(cancel);
        if (lifecycleRevision == null)
            return null;

        var active = await db.DbContext.LuaMDeepCryoSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId &&
                                           value.PlayerUserId == userId.UserId &&
                                           value.Slot == slot &&
                                           (value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                                            db.DbContext.LuaMCharacterPresenceLeases.Any(lease =>
                                                lease.ProfileId == value.ProfileId &&
                                                lease.SnapshotId == value.Id &&
                                                lease.Phase == DbLuaMCharacterPresencePhase.RestoreClaim)),
                cancel);
        var authorityLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId, cancel);
        if (active == null)
            return new(
                lifecycleRevision.Value,
                null,
                authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));

        var restoreLease = authorityLease is { Phase: DbLuaMCharacterPresencePhase.RestoreClaim } &&
                           authorityLease.SnapshotId == active.Id
            ? authorityLease
            : null;
        return new(
            lifecycleRevision.Value,
            ToDeepCryoSnapshotRecord(active, restoreLease),
            authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));
    }

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
                                           (value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                                            db.DbContext.LuaMCharacterPresenceLeases.Any(lease =>
                                                lease.ProfileId == value.ProfileId &&
                                                lease.SnapshotId == value.Id &&
                                                lease.Phase == DbLuaMCharacterPresencePhase.RestoreClaim)),
                cancel);
        if (snapshot == null)
            return null;

        var lease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId &&
                                           value.SnapshotId == snapshot.Id &&
                                           value.Phase == DbLuaMCharacterPresencePhase.RestoreClaim,
                cancel);
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
            {
                var inactiveLifecycleRevision = await FindCryoProfileLifecycleRevisionAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    cancel);
                if (inactiveLifecycleRevision != null &&
                    inactiveLifecycleRevision != request.ExpectedLifecycleRevision)
                {
                    return await CreateDeepCryoLifecycleConflictResultAsync(
                        db.DbContext,
                        request,
                        cancel);
                }

                return new(LuaMDeepCryoWriteStatus.ProfileNotActive);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest);
            if (profile.LifecycleRevision != request.ExpectedLifecycleRevision)
            {
                return await CreateDeepCryoLifecycleConflictResultAsync(
                    db.DbContext,
                    request,
                    cancel);
            }

            var presenceLease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (presenceLease == null || presenceLease.LeaseId != request.ExpectedPresenceLeaseId)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    LeaseId: presenceLease?.LeaseId,
                    Authority: presenceLease == null ? null : ToPresenceAuthorityRecord(presenceLease));
            }
            if (presenceLease.Phase != DbLuaMCharacterPresencePhase.Playable ||
                presenceLease.AuthorityLifecycleRevision != request.ExpectedLifecycleRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    LeaseId: presenceLease.LeaseId,
                    Authority: ToPresenceAuthorityRecord(presenceLease));
            }

            var active = await db.DbContext.LuaMDeepCryoSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId &&
                                               (value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                                                db.DbContext.LuaMCharacterPresenceLeases.Any(lease =>
                                                    lease.ProfileId == value.ProfileId &&
                                                    lease.SnapshotId == value.Id &&
                                                    lease.Phase == DbLuaMCharacterPresencePhase.RestoreClaim)),
                    cancel);
            if (active != null)
            {
                var activeLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ProfileId == active.ProfileId &&
                                                   value.SnapshotId == active.Id &&
                                                   value.Phase == DbLuaMCharacterPresencePhase.RestoreClaim,
                        cancel);
                return new(
                    active.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined
                        ? LuaMDeepCryoWriteStatus.Quarantined
                        : LuaMDeepCryoWriteStatus.InvalidState,
                    active.Id,
                    active.Revision,
                    active.Status,
                    activeLease?.LeaseId,
                    ToDeepCryoSnapshotRecord(active, activeLease));
            }

            if (LuaMDeepCryoStoreNoActiveSnapshotObservedForTesting is { } noActiveObserved)
                await noActiveObserved(cancel);

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
                if (now < career.UpdatedAtUtc)
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
            db.DbContext.LuaMCharacterPresenceLeases.Remove(presenceLease);

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
            return await ResolveDeepCryoStoreFailureAsync(
                request,
                operationIdentityKey,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveDeepCryoStoreFailureAsync(
                request,
                operationIdentityKey,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM deep-cryo store outcome requires re-read: {exception.Message}");
            return await ResolveDeepCryoStoreFailureAsync(
                request,
                operationIdentityKey,
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
                var inactiveLifecycleRevision = await FindCryoProfileLifecycleRevisionAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    cancel);
                if (inactiveLifecycleRevision != null &&
                    inactiveLifecycleRevision != request.ExpectedLifecycleRevision)
                {
                    return await CreateDeepCryoClaimLifecycleConflictResultAsync(
                        db.DbContext,
                        request,
                        cancel);
                }

                return new(LuaMDeepCryoWriteStatus.ProfileNotActive);
            }
            if (profile.LifecycleRevision > long.MaxValue - 4)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest);
            if (profile.LifecycleRevision != request.ExpectedLifecycleRevision)
            {
                return await CreateDeepCryoClaimLifecycleConflictResultAsync(
                    db.DbContext,
                    request,
                    cancel);
            }

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
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (existingLease != null)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status,
                    existingLease.LeaseId,
                    ToDeepCryoSnapshotRecord(snapshot, existingLease),
                    ToPresenceAuthorityRecord(existingLease));
            }

            var leaseTokenUsed = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                                     .AnyAsync(value => value.SnapshotId == request.SnapshotId ||
                                                        value.LeaseId == request.LeaseId,
                                         cancel) ||
                                 await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                                     .AnyAsync(value => value.LeaseId == request.LeaseId, cancel) ||
                await db.DbContext.LuaMCharacterPresenceOperations.AsNoTracking()
                    .AnyAsync(value => value.ResultLeaseId == request.LeaseId, cancel);
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
                Phase = DbLuaMCharacterPresencePhase.RestoreClaim,
                ServerInstanceId = request.ServerInstanceId,
                RoundId = request.RestoreRoundId,
                AcquiredAtUtc = claimedAt,
                RenewedAtUtc = claimedAt,
                ExpiresAtUtc = expiresAt,
                AuthorityLifecycleRevision = checked(request.ExpectedLifecycleRevision + 4),
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
                ToDeepCryoSnapshotRecord(snapshot, lease),
                ToPresenceAuthorityRecord(lease));
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
            CreatedAtUtc: request.CompletedAtUtc,
            RetainLease: true), cancel);

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

    public Task<LuaMDeepCryoWriteResult> RollbackLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoRollbackPublicationRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedCompletedRevision,
            request.LeaseId,
            // This remains an abort in the immutable operation journal. The
            // required CompleteRestore proof below distinguishes the narrow
            // post-completion compensation from an ordinary leased abort.
            DbLuaMDeepCryoOperationKind.AbortRestore,
            DbLuaMDeepCryoSnapshotStatus.Stored,
            AllowStored: false,
            AllowRestoring: false,
            AllowQuarantined: false,
            Reason: request.Reason,
            CreatedAtUtc: request.RolledBackAtUtc,
            RequiredCompletionOperationId: request.CompletionOperationId,
            AllowConsumed: true), cancel);

    public Task<LuaMDeepCryoWriteResult> AuthorizeLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoAuthorizePublicationRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedPreparedRevision,
            request.LeaseId,
            // Reuse CompleteRestore to stay inside the deployed kind constraint.
            // The strict reason marker distinguishes AUTH from initial PREPARE.
            DbLuaMDeepCryoOperationKind.CompleteRestore,
            DbLuaMDeepCryoSnapshotStatus.Consumed,
            AllowStored: false,
            AllowRestoring: false,
            AllowQuarantined: false,
            Reason: LuaMDeepCryoPublication.AuthorizationReason,
            CreatedAtUtc: request.AuthorizedAtUtc,
            RequiredCompletionOperationId: request.CompletionOperationId,
            AllowConsumed: true,
            RetainLease: true,
            RequireUnexpiredLease: true), cancel);

    public Task<LuaMDeepCryoWriteResult> AcknowledgeLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoAcknowledgePublicationRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedAuthorizedRevision,
            request.LeaseId,
            // Discard is reused as the immutable final-publication audit kind;
            // the authorization proof and consumed source make it distinct from a
            // fresh-spawn discard without requiring a schema migration.
            DbLuaMDeepCryoOperationKind.Discard,
            DbLuaMDeepCryoSnapshotStatus.Consumed,
            AllowStored: false,
            AllowRestoring: false,
            AllowQuarantined: false,
            Reason: null,
            CreatedAtUtc: request.AcknowledgedAtUtc,
            RequiredCompletionOperationId: request.AuthorizationOperationId,
            AllowConsumed: true,
            RetainLease: true,
            RequireAuthorizationProof: true,
            PromoteLeaseToPlayable: true,
            PlayableLeaseExpiresAtUtc: request.PlayableLeaseExpiresAtUtc), cancel);

    public Task<LuaMDeepCryoWriteResult> QuarantineAuthorizedLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoQuarantineAuthorizedPublicationRequest request,
        CancellationToken cancel = default)
        => TransitionLuaMDeepCryoSnapshotAsync(new DeepCryoTransitionRequest(
            request.OperationId,
            request.UserId,
            request.ProfileId,
            request.Slot,
            request.SnapshotId,
            request.ExpectedAuthorizedRevision,
            request.LeaseId,
            DbLuaMDeepCryoOperationKind.Quarantine,
            DbLuaMDeepCryoSnapshotStatus.Quarantined,
            AllowStored: false,
            AllowRestoring: false,
            AllowQuarantined: false,
            Reason: request.Reason,
            CreatedAtUtc: request.QuarantinedAtUtc,
            RequiredCompletionOperationId: request.AuthorizationOperationId,
            AllowConsumed: true,
            RequireAuthorizationProof: true), cancel);

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
            {
                var observedLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
                return new(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status,
                    observedLease?.LeaseId,
                    ToDeepCryoSnapshotRecord(snapshot, observedLease),
                    observedLease == null ? null : ToPresenceAuthorityRecord(observedLease));
            }
            if (!IsAllowedDeepCryoSourceStatus(snapshot.Status, request))
            {
                var observedLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
                return new(
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined
                        ? LuaMDeepCryoWriteStatus.Quarantined
                        : LuaMDeepCryoWriteStatus.InvalidState,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status,
                    observedLease?.LeaseId,
                    ToDeepCryoSnapshotRecord(snapshot, observedLease),
                    observedLease == null ? null : ToPresenceAuthorityRecord(observedLease));
            }
            if (request.RequiredCompletionOperationId is { } requiredCompletionOperationId)
            {
                var completion = await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.OperationId == requiredCompletionOperationId, cancel);
                if (completion == null ||
                    completion.SnapshotId != snapshot.Id ||
                    completion.ProfileId != snapshot.ProfileId ||
                    completion.Kind != DbLuaMDeepCryoOperationKind.CompleteRestore ||
                    completion.ResultStatus != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                    completion.ResultRevision != request.ExpectedRevision ||
                    completion.LeaseId != request.LeaseId ||
                    completion.Reason != (request.RequireAuthorizationProof
                        ? LuaMDeepCryoPublication.AuthorizationReason
                        : null))
                {
                    return new(
                        LuaMDeepCryoWriteStatus.InvalidState,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.Status);
                }

                var anotherActiveSnapshot = await db.DbContext.LuaMDeepCryoSnapshots.AsNoTracking()
                    .AnyAsync(value => value.ProfileId == snapshot.ProfileId &&
                                       value.Id != snapshot.Id &&
                                       (value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                                        db.DbContext.LuaMCharacterPresenceLeases.Any(lease =>
                                            lease.ProfileId == value.ProfileId &&
                                            lease.SnapshotId == value.Id &&
                                            lease.Phase == DbLuaMCharacterPresencePhase.RestoreClaim)),
                        cancel);
                if (anotherActiveSnapshot)
                {
                    return new(
                        LuaMDeepCryoWriteStatus.InvalidState,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.Status);
                }
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
                    lease.SnapshotId != snapshot.Id || lease.LeaseId != request.LeaseId.Value ||
                    lease.Phase != DbLuaMCharacterPresencePhase.RestoreClaim)
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

                if (lease.AuthorityLifecycleRevision < 3 ||
                    profile.LifecycleRevision != lease.AuthorityLifecycleRevision - 3)
                {
                    return new(
                        LuaMDeepCryoWriteStatus.LifecycleConflict,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.Status,
                        lease.LeaseId,
                        ToDeepCryoSnapshotRecord(snapshot, lease),
                        ToPresenceAuthorityRecord(lease));
                }
            }
            else if (snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Consumed && request.AllowConsumed)
            {
                // A consumed snapshot is mutable only while it is a prepared,
                // unpublished restore retaining the exact presence lease.
                if (request.LeaseId == null || lease == null ||
                    lease.SnapshotId != snapshot.Id || lease.LeaseId != request.LeaseId.Value ||
                    lease.Phase != DbLuaMCharacterPresencePhase.RestoreClaim)
                {
                    return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status,
                        lease?.LeaseId);
                }

                if (request.RequireUnexpiredLease &&
                    (lease.ExpiresAtUtc <= now || lease.ExpiresAtUtc <= DateTime.UtcNow))
                {
                    return new(LuaMDeepCryoWriteStatus.LeaseConflict, snapshot.Id, snapshot.Revision, snapshot.Status,
                        lease.LeaseId);
                }

                var remainingLifecycleTransitions = request.RequireAuthorizationProof ? 1L : 2L;
                if (lease.AuthorityLifecycleRevision < remainingLifecycleTransitions ||
                    profile.LifecycleRevision !=
                    lease.AuthorityLifecycleRevision - remainingLifecycleTransitions)
                {
                    return new(
                        LuaMDeepCryoWriteStatus.LifecycleConflict,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.Status,
                        lease.LeaseId,
                        ToDeepCryoSnapshotRecord(snapshot, lease),
                        ToPresenceAuthorityRecord(lease));
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
                DbLuaMDeepCryoSnapshotStatus.Consumed when lease is
                    { Phase: DbLuaMCharacterPresencePhase.RestoreClaim } =>
                    DbLuaMCharacterCareerStatus.RecoveryPending,
                _ => DbLuaMCharacterCareerStatus.Playable,
            };
            if (career == null || career.Status != expectedCareerStatus)
                return new(LuaMDeepCryoWriteStatus.InvalidState, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (career.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            DateTime? playableExpiresAt = null;
            if (request.PromoteLeaseToPlayable)
            {
                if (lease == null ||
                    profile.LifecycleRevision == long.MaxValue ||
                    profile.LifecycleRevision + 1 != lease.AuthorityLifecycleRevision ||
                    request.PlayableLeaseExpiresAtUtc is not { } playableExpiresRaw)
                {
                    return new(
                        LuaMDeepCryoWriteStatus.LifecycleConflict,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.Status,
                        lease?.LeaseId,
                        ToDeepCryoSnapshotRecord(snapshot, lease),
                        lease == null ? null : ToPresenceAuthorityRecord(lease));
                }

                var candidatePlayableExpiresAt = AsUtc(playableExpiresRaw);
                if (candidatePlayableExpiresAt <= now || candidatePlayableExpiresAt <= DateTime.UtcNow ||
                    lease.Revision == long.MaxValue)
                {
                    return new(
                        LuaMDeepCryoWriteStatus.InvalidRequest,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.Status,
                        lease.LeaseId,
                        ToDeepCryoSnapshotRecord(snapshot, lease),
                        ToPresenceAuthorityRecord(lease));
                }

                playableExpiresAt = candidatePlayableExpiresAt;
            }

            snapshot.Status = request.TargetStatus;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = now;
            snapshot.ConsumedAtUtc = request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Consumed ? now : null;
            snapshot.QuarantinedAtUtc = request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined ? now : null;
            snapshot.QuarantineReason = request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined
                ? request.Reason
                : null;

            career.Status = request.RetainLease
                ? request.PromoteLeaseToPlayable
                    ? DbLuaMCharacterCareerStatus.Playable
                    : DbLuaMCharacterCareerStatus.RecoveryPending
                : request.TargetStatus switch
            {
                DbLuaMDeepCryoSnapshotStatus.Stored => DbLuaMCharacterCareerStatus.CryoStored,
                DbLuaMDeepCryoSnapshotStatus.Consumed => DbLuaMCharacterCareerStatus.Playable,
                DbLuaMDeepCryoSnapshotStatus.Quarantined => DbLuaMCharacterCareerStatus.Quarantined,
                _ => DbLuaMCharacterCareerStatus.RecoveryPending,
            };
            career.Revision++;
            career.UpdatedAtUtc = now;
            profile.LifecycleRevision++;

            if (request.PromoteLeaseToPlayable)
            {
                // Both are proven non-null by the pre-mutation validation above.
                var playableLease = lease!;
                playableLease.Phase = DbLuaMCharacterPresencePhase.Playable;
                playableLease.RenewedAtUtc = now;
                playableLease.ExpiresAtUtc = playableExpiresAt!.Value;
                playableLease.Revision++;
                playableLease.LastRenewalOperationId = null;
                playableLease.LastRenewalOperationIdentityKey = null;
            }

            if (lease != null && !request.RetainLease)
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
                ToDeepCryoSnapshotRecord(snapshot, request.RetainLease ? lease : null),
                request.RetainLease && lease != null ? ToPresenceAuthorityRecord(lease) : null);
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
        Guid[]? protectedLeaseIds = null,
        int maxCount = 100,
        CancellationToken cancel = default)
    {
        if (nowUtc == default || maxCount is <= 0 or > 1000 ||
            protectedLeaseIds is { Length: > 1000 } ||
            protectedLeaseIds?.Any(value => value == Guid.Empty) == true)
            return 0;

        var requestedNow = AsUtc(nowUtc);
        var serverNow = DateTime.UtcNow;
        var now = requestedNow <= serverNow ? requestedNow : serverNow;
        var protectedLeases = protectedLeaseIds?.Distinct().ToArray() ?? Array.Empty<Guid>();
        (int ProfileId, long SnapshotId, Guid LeaseId, DateTime AcquiredAtUtc, DateTime ExpiresAtUtc)[] candidates;
        await using (var db = await GetDb(cancel))
        {
            candidates = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                .Where(value => value.Phase == DbLuaMCharacterPresencePhase.RestoreClaim &&
                                value.SnapshotId != null &&
                                value.ExpiresAtUtc <= now &&
                                !protectedLeases.Contains(value.LeaseId))
                .OrderBy(value => value.ExpiresAtUtc)
                .ThenBy(value => value.ProfileId)
                .Take(maxCount)
                .Select(value => new ValueTuple<int, long, Guid, DateTime, DateTime>(
                    value.ProfileId,
                    value.SnapshotId!.Value,
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
                                               value.LeaseId == leaseId &&
                                               value.Phase == DbLuaMCharacterPresencePhase.RestoreClaim,
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
            var currentCompletion = snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Consumed
                ? await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                    .SingleOrDefaultAsync(value =>
                            value.SnapshotId == snapshot.Id &&
                            value.ProfileId == snapshot.ProfileId &&
                            value.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
                            value.ResultStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
                            value.ResultRevision == snapshot.Revision &&
                            value.LeaseId == lease.LeaseId,
                        cancel)
                : null;
            var preparedCompletionProof = currentCompletion is { Reason: null };
            var authorizedCompletionProof = currentCompletion?.Reason == LuaMDeepCryoPublication.AuthorizationReason;
            var restoreEpochCoherent = snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Restoring &&
                                       lease.AuthorityLifecycleRevision >= 3 &&
                                       profile.LifecycleRevision == lease.AuthorityLifecycleRevision - 3;
            var preparedEpochCoherent = preparedCompletionProof &&
                                        lease.AuthorityLifecycleRevision >= 2 &&
                                        profile.LifecycleRevision == lease.AuthorityLifecycleRevision - 2;
            var authorizedEpochCoherent = authorizedCompletionProof &&
                                          lease.AuthorityLifecycleRevision >= 1 &&
                                          profile.LifecycleRevision == lease.AuthorityLifecycleRevision - 1;
            var normalRecovery = (snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Restoring ||
                                   preparedCompletionProof) &&
                                  (restoreEpochCoherent || preparedEpochCoherent) &&
                                  snapshot.Revision < long.MaxValue &&
                                  !profile.IsArchived && profile.Slot.HasValue &&
                                 career is
                                 {
                                     Status: DbLuaMCharacterCareerStatus.RecoveryPending,
                                     Revision: < long.MaxValue,
                                 };
            var recoveryReason = preparedCompletionProof
                ? "prepared publication lease expired"
                : "restore lease expired";

            if (normalRecovery)
            {
                snapshot.Status = DbLuaMDeepCryoSnapshotStatus.Stored;
                snapshot.Revision++;
                snapshot.UpdatedAtUtc = updatedAt;
                snapshot.ConsumedAtUtc = null;
                snapshot.QuarantinedAtUtc = null;
                snapshot.QuarantineReason = null;
                career!.Status = DbLuaMCharacterCareerStatus.CryoStored;
                career.Revision++;
                career.UpdatedAtUtc = updatedAt < career.UpdatedAtUtc ? career.UpdatedAtUtc : updatedAt;
            }
            else
            {
                recoveryReason = authorizedCompletionProof
                    ? authorizedEpochCoherent
                        ? "authorized publication lease expired; manual recovery required"
                        : "authorized publication authority epoch mismatch; manual recovery required"
                    : "inconsistent expired restore lease reconciled fail-closed";
                if (snapshot.Revision == long.MaxValue)
                    return false;

                snapshot.Status = DbLuaMDeepCryoSnapshotStatus.Quarantined;
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
                RoundId = lease.RoundId,
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
            var currentLease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ProfileId == operation.ProfileId, cancel);
            return new(
                LuaMDeepCryoWriteStatus.IdentityConflict,
                operation.SnapshotId,
                operation.ResultRevision,
                operation.ResultStatus,
                currentLease?.LeaseId,
                Authority: currentLease == null ? null : ToPresenceAuthorityRecord(currentLease));
        }

        var snapshot = await db.LuaMDeepCryoSnapshots.AsNoTracking()
            .SingleAsync(value => value.Id == operation.SnapshotId, cancel);
        var lease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == snapshot.ProfileId, cancel);
        var profileLifecycleRevision = await db.Profile.AsNoTracking()
            .Where(value => value.Id == snapshot.ProfileId)
            .Select(value => (long?) value.LifecycleRevision)
            .SingleOrDefaultAsync(cancel);
        var retainedRestoreOperation = operation.Kind is
            DbLuaMDeepCryoOperationKind.ClaimRestore or DbLuaMDeepCryoOperationKind.CompleteRestore;
        var acknowledgementShape = operation.Kind == DbLuaMDeepCryoOperationKind.Discard &&
                                   operation.ResultStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
                                   operation.ResultRevision > 0 &&
                                   operation.LeaseId.HasValue;
        var potentialRetainedAcknowledgement = acknowledgementShape &&
                                               await db.LuaMDeepCryoOperations.AsNoTracking()
                                                   .AnyAsync(value =>
                                                           value.SnapshotId == operation.SnapshotId &&
                                                           value.ProfileId == operation.ProfileId &&
                                                           value.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
                                                           value.ResultStatus ==
                                                           DbLuaMDeepCryoSnapshotStatus.Consumed &&
                                                           value.ResultRevision == operation.ResultRevision - 1 &&
                                                           value.LeaseId == operation.LeaseId &&
                                                           value.Reason ==
                                                           LuaMDeepCryoPublication.AuthorizationReason,
                                                       cancel);
        var retainedAcknowledgement = potentialRetainedAcknowledgement &&
                                      lease?.LeaseId == operation.LeaseId &&
                                      lease?.Phase == DbLuaMCharacterPresencePhase.Playable &&
                                      lease.SnapshotId == snapshot.Id &&
                                      lease.AuthorityLifecycleRevision == profileLifecycleRevision;
        var coherentLeaseState = retainedRestoreOperation
            ? operation.LeaseId.HasValue &&
              lease?.LeaseId == operation.LeaseId &&
              lease.Phase == DbLuaMCharacterPresencePhase.RestoreClaim &&
              lease.SnapshotId == snapshot.Id
            : retainedAcknowledgement || lease == null;
        var coherentCurrentState = snapshot.Revision == operation.ResultRevision &&
                                   snapshot.Status == operation.ResultStatus &&
                                   coherentLeaseState;
        if (potentialRetainedAcknowledgement && !retainedAcknowledgement)
        {
            return new(
                LuaMDeepCryoWriteStatus.LifecycleConflict,
                operation.SnapshotId,
                operation.ResultRevision,
                operation.ResultStatus,
                lease?.LeaseId,
                Snapshot: null,
                Authority: lease == null ? null : ToPresenceAuthorityRecord(lease));
        }

        return new(
            LuaMDeepCryoWriteStatus.AlreadyProcessed,
            operation.SnapshotId,
            operation.ResultRevision,
            operation.ResultStatus,
            operation.LeaseId,
            coherentCurrentState ? ToDeepCryoSnapshotRecord(snapshot, lease) : null,
            coherentCurrentState && lease != null ? ToPresenceAuthorityRecord(lease) : null);
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

        var authorityLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == snapshot.ProfileId, CancellationToken.None);
        var snapshotLease = authorityLease?.SnapshotId == snapshot.Id ? authorityLease : null;
        return new(
            fallback,
            snapshot.Id,
            snapshot.Revision,
            snapshot.Status,
            authorityLease?.LeaseId,
            ToDeepCryoSnapshotRecord(snapshot, snapshotLease),
            authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));
    }

    private async Task<LuaMDeepCryoWriteResult> ResolveDeepCryoStoreFailureAsync(
        LuaMDeepCryoStoreRequest request,
        string operationIdentityKey,
        LuaMDeepCryoWriteStatus fallback)
    {
        var replay = await FindDeepCryoReplayAsync(
            request.OperationId,
            operationIdentityKey,
            CancellationToken.None);
        if (replay != null)
            return replay;

        await using var db = await GetDb(CancellationToken.None);
        var currentLifecycleRevision = await FindCryoProfileLifecycleRevisionAsync(
            db.DbContext,
            request.UserId,
            request.ProfileId,
            CancellationToken.None);
        if (currentLifecycleRevision == null || currentLifecycleRevision == request.ExpectedLifecycleRevision)
            return new(fallback);

        return await CreateDeepCryoLifecycleConflictResultAsync(
            db.DbContext,
            request,
            CancellationToken.None);
    }

    private static async Task<LuaMDeepCryoWriteResult> CreateDeepCryoLifecycleConflictResultAsync(
        ServerDbContext db,
        LuaMDeepCryoStoreRequest request,
        CancellationToken cancel)
    {
        // The profile token proves that retrying this stale Store can create an
        // ABA duplicate. Prefer the semantic active row, but retain the newest
        // historical row as evidence when the winner already reached ACK and is
        // therefore Consumed without a lease.
        var snapshot = await db.LuaMDeepCryoSnapshots.AsNoTracking()
            .Where(value => value.ProfileId == request.ProfileId &&
                            value.PlayerUserId == request.UserId.UserId &&
                            value.Slot == request.Slot)
            .OrderBy(value => value.Status == DbLuaMDeepCryoSnapshotStatus.Consumed &&
                              !db.LuaMCharacterPresenceLeases.Any(lease =>
                                  lease.ProfileId == value.ProfileId &&
                                  lease.SnapshotId == value.Id))
            .ThenByDescending(value => value.UpdatedAtUtc)
            .ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancel);
        var authorityLease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
        if (snapshot == null)
        {
            return new(
                LuaMDeepCryoWriteStatus.LifecycleConflict,
                LeaseId: authorityLease?.LeaseId,
                Authority: authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));
        }

        var snapshotLease = authorityLease?.SnapshotId == snapshot.Id ? authorityLease : null;
        return new(
            LuaMDeepCryoWriteStatus.LifecycleConflict,
            snapshot.Id,
            snapshot.Revision,
            snapshot.Status,
            authorityLease?.LeaseId,
            ToDeepCryoSnapshotRecord(snapshot, snapshotLease),
            authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));
    }

    private static async Task<LuaMDeepCryoWriteResult> CreateDeepCryoClaimLifecycleConflictResultAsync(
        ServerDbContext db,
        LuaMDeepCryoClaimRequest request,
        CancellationToken cancel)
    {
        var snapshot = await db.LuaMDeepCryoSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == request.SnapshotId &&
                                           value.ProfileId == request.ProfileId &&
                                           value.PlayerUserId == request.UserId.UserId &&
                                           value.Slot == request.Slot,
                cancel);
        var authorityLease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
        if (snapshot == null)
        {
            return new(
                LuaMDeepCryoWriteStatus.LifecycleConflict,
                request.SnapshotId,
                LeaseId: authorityLease?.LeaseId,
                Authority: authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));
        }

        var snapshotLease = authorityLease?.SnapshotId == snapshot.Id ? authorityLease : null;
        return new(
            LuaMDeepCryoWriteStatus.LifecycleConflict,
            snapshot.Id,
            snapshot.Revision,
            snapshot.Status,
            authorityLease?.LeaseId,
            ToDeepCryoSnapshotRecord(snapshot, snapshotLease),
            authorityLease == null ? null : ToPresenceAuthorityRecord(authorityLease));
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

    private static Task<long?> FindCryoProfileLifecycleRevisionAsync(
        ServerDbContext db,
        NetUserId userId,
        int profileId,
        CancellationToken cancel)
        => db.Profile.AsNoTracking()
            .Where(value => value.Id == profileId &&
                            value.Preference.UserId == userId.UserId)
            .Select(value => (long?) value.LifecycleRevision)
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
            lease?.ExpiresAtUtc,
            lease == null ? null : ToPresenceAuthorityRecord(lease));

    private static bool IsValidDeepCryoStoreRequest(LuaMDeepCryoStoreRequest request)
        => request.OperationId != Guid.Empty &&
           request.ExpectedPresenceLeaseId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 && request.SourceRoundId >= 0 &&
           request.FormatVersion > 0 && request.StoredAtUtc != default &&
           request.ExpectedLifecycleRevision >= 0 &&
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
            request.ExpectedRevision < 0 || request.ExpectedLifecycleRevision < 0 ||
            request.ExpectedLifecycleRevision > long.MaxValue - 4 ||
            request.RestoreRoundId < 0 ||
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

        if (request.AllowConsumed)
        {
            if (request.RequiredCompletionOperationId is not { } completionOperationId ||
                completionOperationId == Guid.Empty ||
                request.LeaseId == null ||
                !((request.Kind == DbLuaMDeepCryoOperationKind.AbortRestore &&
                   request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Stored &&
                   !string.IsNullOrWhiteSpace(request.Reason)) ||
                  (request.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
                   request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
                   request.RetainLease &&
                   request.Reason == LuaMDeepCryoPublication.AuthorizationReason &&
                   !request.RequireAuthorizationProof) ||
                  (request.Kind == DbLuaMDeepCryoOperationKind.Discard &&
                   request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
                   request.Reason == null &&
                   request.RequireAuthorizationProof) ||
                  (request.Kind == DbLuaMDeepCryoOperationKind.Quarantine &&
                   request.TargetStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined &&
                   !string.IsNullOrWhiteSpace(request.Reason) &&
                   request.RequireAuthorizationProof)))
            {
                return false;
            }
        }
        else if (request.RequiredCompletionOperationId != null || request.RequireAuthorizationProof)
        {
            return false;
        }

        if (request.RetainLease &&
            (request.TargetStatus != DbLuaMDeepCryoSnapshotStatus.Consumed ||
             request.LeaseId == null ||
             !((request.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
                request.AllowRestoring &&
                !request.AllowConsumed &&
                request.Reason == null &&
                request.RequiredCompletionOperationId == null &&
                !request.PromoteLeaseToPlayable) ||
               (request.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
                !request.AllowRestoring &&
                request.AllowConsumed &&
                request.Reason == LuaMDeepCryoPublication.AuthorizationReason &&
                request.RequiredCompletionOperationId != null &&
                !request.RequireAuthorizationProof &&
                request.RequireUnexpiredLease &&
                !request.PromoteLeaseToPlayable) ||
               (request.Kind == DbLuaMDeepCryoOperationKind.Discard &&
                !request.AllowRestoring &&
                request.AllowConsumed &&
                request.Reason == null &&
                request.RequiredCompletionOperationId != null &&
                request.RequireAuthorizationProof &&
                !request.RequireUnexpiredLease &&
                request.PromoteLeaseToPlayable))))
        {
            return false;
        }

        if (request.PromoteLeaseToPlayable != request.PlayableLeaseExpiresAtUtc.HasValue ||
            request.PromoteLeaseToPlayable &&
            (request.PlayableLeaseExpiresAtUtc == default ||
             AsUtc(request.PlayableLeaseExpiresAtUtc!.Value) <= AsUtc(request.CreatedAtUtc)))
        {
            return false;
        }

        if (request.RequireUnexpiredLease &&
            !(request.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore &&
              request.AllowConsumed &&
              request.RetainLease &&
              request.Reason == LuaMDeepCryoPublication.AuthorizationReason))
        {
            return false;
        }

        if (request.Kind is DbLuaMDeepCryoOperationKind.AbortRestore or DbLuaMDeepCryoOperationKind.Quarantine)
            return !string.IsNullOrWhiteSpace(request.Reason);

        if (request.Kind == DbLuaMDeepCryoOperationKind.CompleteRestore && request.AllowConsumed)
            return request.Reason == LuaMDeepCryoPublication.AuthorizationReason;

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
            DbLuaMDeepCryoSnapshotStatus.Consumed => request.AllowConsumed,
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
            writer.Write(request.ExpectedLifecycleRevision);
            writer.Write(request.ExpectedPresenceLeaseId.ToByteArray());
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
            writer.Write(request.ExpectedLifecycleRevision);
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
            if (request.RequiredCompletionOperationId is { } completionOperationId)
            {
                writer.Write("prepared-publication-proof-v2");
                writer.Write(completionOperationId.ToByteArray());
            }
            writer.Write(request.RequireAuthorizationProof);
            writer.Write(request.RequireUnexpiredLease);
            writer.Write(request.PromoteLeaseToPlayable);
            if (request.PlayableLeaseExpiresAtUtc is { } playableLeaseExpiresAtUtc)
                writer.Write(AsUtc(playableLeaseExpiresAtUtc).Ticks);
            if (request.RetainLease)
                writer.Write("retain-publication-lease-v1");
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
        DateTime CreatedAtUtc,
        Guid? RequiredCompletionOperationId = null,
        bool AllowConsumed = false,
        bool RetainLease = false,
        bool RequireAuthorizationProof = false,
        bool RequireUnexpiredLease = false,
        bool PromoteLeaseToPlayable = false,
        DateTime? PlayableLeaseExpiresAtUtc = null);
}
