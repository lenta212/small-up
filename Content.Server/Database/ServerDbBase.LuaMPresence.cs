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
    public async Task<LuaMCharacterPresenceAuthorityRecord?> GetLuaMCharacterPresenceAuthorityAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
    {
        if (profileId <= 0 || slot < 0)
            return null;

        await using var db = await GetDb(cancel);
        var validProfile = await db.DbContext.Profile.AsNoTracking()
            .AnyAsync(value => value.Id == profileId &&
                               value.Slot == slot &&
                               !value.IsArchived &&
                               value.Preference.UserId == userId.UserId,
                cancel);
        if (!validProfile)
            return null;

        var lease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId, cancel);
        return lease == null ? null : ToPresenceAuthorityRecord(lease);
    }

    public async Task<LuaMCharacterPresenceWriteResult> ReserveLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReserveRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidPresenceReserveRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var identityKey = CreatePresenceReserveIdentityKey(request);
        var replay = await FindPresenceReplayAsync(
            request.OperationId,
            identityKey,
            request.UserId,
            request.ProfileId,
            request.Slot,
            cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindPresenceReplayAsync(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
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
            if (profile.LifecycleRevision != request.ExpectedLifecycleRevision)
            {
                return await CreatePresenceConflictResultAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    cancel);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, LifecycleRevision: profile.LifecycleRevision);

            var existingLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (existingLease != null)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    ToPresenceAuthorityRecord(existingLease),
                    profile.LifecycleRevision);
            }

            var reusedLeaseToken = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                                       .AnyAsync(value => value.LeaseId == request.LeaseId, cancel) ||
                                   await db.DbContext.LuaMCharacterPresenceOperations.AsNoTracking()
                                       .AnyAsync(value => value.ResultLeaseId == request.LeaseId, cancel) ||
                                   await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                                       .AnyAsync(value => value.LeaseId == request.LeaseId, cancel);
            if (reusedLeaseToken)
                return new(LuaMDeepCryoWriteStatus.LeaseConflict, LifecycleRevision: profile.LifecycleRevision);

            var hasActiveSnapshot = await HasSemanticActiveCryoSnapshotAsync(
                db.DbContext,
                request.ProfileId,
                cancel);
            if (hasActiveSnapshot)
                return new(LuaMDeepCryoWriteStatus.InvalidState, LifecycleRevision: profile.LifecycleRevision);

            var career = await db.DbContext.LuaMCharacterCareers
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (career is { Status: not DbLuaMCharacterCareerStatus.Playable })
                return new(LuaMDeepCryoWriteStatus.InvalidState, LifecycleRevision: profile.LifecycleRevision);

            var reservedAt = AsUtc(request.ReservedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            if (expiresAt <= DateTime.UtcNow)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, LifecycleRevision: profile.LifecycleRevision);

            profile.LifecycleRevision++;
            var lease = new LuaMCharacterPresenceLease
            {
                ProfileId = request.ProfileId,
                SnapshotId = null,
                LeaseId = request.LeaseId,
                Phase = DbLuaMCharacterPresencePhase.FreshReserved,
                ServerInstanceId = request.ServerInstanceId,
                RoundId = request.RoundId,
                AcquiredAtUtc = reservedAt,
                RenewedAtUtc = reservedAt,
                ExpiresAtUtc = expiresAt,
                AuthorityLifecycleRevision = profile.LifecycleRevision,
            };
            db.DbContext.LuaMCharacterPresenceLeases.Add(lease);
            var authority = ToPresenceAuthorityRecord(lease);
            AddPresenceOperation(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.ProfileId,
                DbLuaMCharacterPresenceOperationKind.Reserve,
                authority,
                profile.LifecycleRevision,
                null,
                reservedAt);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(
                LuaMDeepCryoWriteStatus.Success,
                ToPresenceAuthorityRecord(lease),
                profile.LifecycleRevision);
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.LifecycleConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM presence reserve outcome requires re-read: {exception.Message}");
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMCharacterPresenceWriteResult> PublishLuaMCharacterPresenceAsync(
        LuaMCharacterPresencePublishRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidPresencePublishRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var identityKey = CreatePresencePublishIdentityKey(request);
        var replay = await FindPresenceReplayAsync(
            request.OperationId,
            identityKey,
            request.UserId,
            request.ProfileId,
            request.Slot,
            cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindPresenceReplayAsync(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
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
            if (profile.LifecycleRevision != request.ExpectedLifecycleRevision)
            {
                return await CreatePresenceConflictResultAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    cancel);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, LifecycleRevision: profile.LifecycleRevision);

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (lease == null || lease.LeaseId != request.LeaseId)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    lease == null ? null : ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Phase != DbLuaMCharacterPresencePhase.FreshReserved ||
                lease.AuthorityLifecycleRevision != request.ExpectedLifecycleRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.InvalidState,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Revision != request.ExpectedLeaseRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            var publishedAt = AsUtc(request.PublishedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            if (publishedAt < lease.RenewedAtUtc || expiresAt <= DateTime.UtcNow)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            profile.LifecycleRevision++;
            lease.Phase = DbLuaMCharacterPresencePhase.Playable;
            lease.RenewedAtUtc = publishedAt;
            lease.ExpiresAtUtc = expiresAt;
            lease.Revision++;
            lease.AuthorityLifecycleRevision = profile.LifecycleRevision;
            lease.LastRenewalOperationId = null;
            lease.LastRenewalOperationIdentityKey = null;
            var authority = ToPresenceAuthorityRecord(lease);
            AddPresenceOperation(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.ProfileId,
                DbLuaMCharacterPresenceOperationKind.Publish,
                authority,
                profile.LifecycleRevision,
                null,
                publishedAt);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(LuaMDeepCryoWriteStatus.Success, ToPresenceAuthorityRecord(lease),
                profile.LifecycleRevision);
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM presence publish outcome requires re-read: {exception.Message}");
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMCharacterPresenceWriteResult> RenewLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceRenewRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidPresenceRenewRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var identityKey = CreatePresenceRenewIdentityKey(request);
        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            var profile = await FindActiveCryoProfileForMutationAsync(
                db.DbContext,
                request.UserId,
                request.ProfileId,
                request.Slot,
                cancel);
            if (profile == null)
                return new(LuaMDeepCryoWriteStatus.ProfileNotActive);

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (lease == null || lease.LeaseId != request.LeaseId)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    lease == null ? null : ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (profile.LifecycleRevision != request.ExpectedAuthorityLifecycleRevision ||
                lease.AuthorityLifecycleRevision != request.ExpectedAuthorityLifecycleRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Phase != DbLuaMCharacterPresencePhase.Playable)
            {
                return new(
                    LuaMDeepCryoWriteStatus.InvalidState,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }

            // Heartbeats are bounded: only the last renewal proof lives on the
            // lease row. This resolves commit-then-throw without an unbounded
            // append-only operation per heartbeat.
            if (lease.LastRenewalOperationId == request.OperationId)
            {
                return lease.LastRenewalOperationIdentityKey == identityKey &&
                       lease.Revision == request.ExpectedLeaseRevision + 1
                    ? new(
                        LuaMDeepCryoWriteStatus.AlreadyProcessed,
                        ToPresenceAuthorityRecord(lease),
                        profile.LifecycleRevision)
                    : new(
                        LuaMDeepCryoWriteStatus.IdentityConflict,
                        ToPresenceAuthorityRecord(lease),
                        profile.LifecycleRevision);
            }
            if (lease.Revision != request.ExpectedLeaseRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            var renewedAt = AsUtc(request.RenewedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            if (renewedAt < lease.RenewedAtUtc || expiresAt <= DateTime.UtcNow)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            lease.RenewedAtUtc = renewedAt;
            lease.ExpiresAtUtc = expiresAt;
            lease.Revision++;
            lease.LastRenewalOperationId = request.OperationId;
            lease.LastRenewalOperationIdentityKey = identityKey;

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(LuaMDeepCryoWriteStatus.Success, ToPresenceAuthorityRecord(lease),
                profile.LifecycleRevision);
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolvePresenceRenewFailureAsync(request, identityKey, LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolvePresenceRenewFailureAsync(request, identityKey,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM presence renewal outcome requires re-read: {exception.Message}");
            return await ResolvePresenceRenewFailureAsync(request, identityKey, LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMCharacterPresenceWriteResult> ReleaseLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReleaseRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidPresenceReleaseRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var identityKey = CreatePresenceReleaseIdentityKey(request);
        var replay = await FindPresenceReplayAsync(
            request.OperationId,
            identityKey,
            request.UserId,
            request.ProfileId,
            request.Slot,
            cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindPresenceReplayAsync(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
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
            if (profile.LifecycleRevision != request.ExpectedAuthorityLifecycleRevision)
            {
                return await CreatePresenceConflictResultAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    cancel);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, LifecycleRevision: profile.LifecycleRevision);

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (lease == null || lease.LeaseId != request.LeaseId)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    lease == null ? null : ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Phase != request.ExpectedPhase ||
                lease.SnapshotId != request.ExpectedSnapshotId ||
                lease.AuthorityLifecycleRevision != request.ExpectedAuthorityLifecycleRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.InvalidState,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Revision != request.ExpectedLeaseRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }

            var releasedAt = AsUtc(request.ReleasedAtUtc);
            if (releasedAt < lease.RenewedAtUtc)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            profile.LifecycleRevision++;
            db.DbContext.LuaMCharacterPresenceLeases.Remove(lease);
            AddPresenceOperation(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.ProfileId,
                DbLuaMCharacterPresenceOperationKind.Release,
                null,
                profile.LifecycleRevision,
                request.Reason,
                releasedAt);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(LuaMDeepCryoWriteStatus.Success, LifecycleRevision: profile.LifecycleRevision);
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM presence release outcome requires re-read: {exception.Message}");
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMCharacterPresenceWriteResult> ReclaimLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReclaimRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidPresenceReclaimRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var identityKey = CreatePresenceReclaimIdentityKey(request);
        var replay = await FindPresenceReplayAsync(
            request.OperationId,
            identityKey,
            request.UserId,
            request.ProfileId,
            request.Slot,
            cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindPresenceReplayAsync(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
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
            if (profile.LifecycleRevision != request.ExpectedAuthorityLifecycleRevision)
            {
                return await CreatePresenceConflictResultAsync(
                    db.DbContext,
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    cancel);
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, LifecycleRevision: profile.LifecycleRevision);

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (lease == null || lease.LeaseId != request.ExpectedLeaseId)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    lease == null ? null : ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Phase != request.ExpectedPhase ||
                lease.SnapshotId != request.ExpectedSnapshotId ||
                lease.AuthorityLifecycleRevision != request.ExpectedAuthorityLifecycleRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.InvalidState,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Revision != request.ExpectedLeaseRevision)
            {
                return new(
                    LuaMDeepCryoWriteStatus.RevisionConflict,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }
            if (lease.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            var reclaimedAt = AsUtc(request.ReclaimedAtUtc);
            var expiresAt = AsUtc(request.LeaseExpiresAtUtc);
            var serverNow = DateTime.UtcNow;
            if (lease.ExpiresAtUtc > serverNow ||
                reclaimedAt < lease.RenewedAtUtc ||
                reclaimedAt > serverNow.AddMinutes(5) ||
                expiresAt <= serverNow)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);
            }

            var reusedLeaseToken = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                                       .AnyAsync(value => value.LeaseId == request.NewLeaseId, cancel) ||
                                   await db.DbContext.LuaMCharacterPresenceOperations.AsNoTracking()
                                       .AnyAsync(value => value.ResultLeaseId == request.NewLeaseId, cancel) ||
                                   await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                                       .AnyAsync(value => value.LeaseId == request.NewLeaseId, cancel);
            if (reusedLeaseToken)
                return new(LuaMDeepCryoWriteStatus.LeaseConflict, ToPresenceAuthorityRecord(lease),
                    profile.LifecycleRevision);

            profile.LifecycleRevision++;
            lease.LeaseId = request.NewLeaseId;
            lease.Phase = DbLuaMCharacterPresencePhase.FreshReserved;
            lease.SnapshotId = null;
            lease.ServerInstanceId = request.NewServerInstanceId;
            lease.RoundId = request.NewRoundId;
            lease.AcquiredAtUtc = reclaimedAt;
            lease.RenewedAtUtc = reclaimedAt;
            lease.ExpiresAtUtc = expiresAt;
            lease.Revision++;
            lease.AuthorityLifecycleRevision = profile.LifecycleRevision;
            lease.LastRenewalOperationId = null;
            lease.LastRenewalOperationIdentityKey = null;
            var authority = ToPresenceAuthorityRecord(lease);
            AddPresenceOperation(
                db.DbContext,
                request.OperationId,
                identityKey,
                request.ProfileId,
                DbLuaMCharacterPresenceOperationKind.Reclaim,
                authority,
                profile.LifecycleRevision,
                null,
                reclaimedAt);

            await db.DbContext.SaveChangesAsync(cancel);
            commitAttempted = true;
            await transaction.CommitAsync(cancel);
            return new(LuaMDeepCryoWriteStatus.Success, ToPresenceAuthorityRecord(lease),
                profile.LifecycleRevision);
        }
        catch (OperationCanceledException) when (commitAttempted)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM presence reclaim outcome requires re-read: {exception.Message}");
            return await ResolvePresenceFailureAsync(
                request.OperationId,
                identityKey,
                request.UserId,
                request.ProfileId,
                request.Slot,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    public async Task<LuaMDeepCryoWriteResult> QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoQuarantineAcknowledgedPublicationRequest request,
        CancellationToken cancel = default)
    {
        if (!IsValidQuarantineAcknowledgedRequest(request))
            return new(LuaMDeepCryoWriteStatus.InvalidRequest);

        var identityKey = CreateQuarantineAcknowledgedIdentityKey(request);
        var replay = await FindDeepCryoReplayAsync(request.OperationId, identityKey, cancel);
        if (replay != null)
            return replay;

        var commitAttempted = false;
        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

            replay = await FindDeepCryoReplayAsync(db.DbContext, request.OperationId, identityKey, cancel);
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
            if (profile.LifecycleRevision != request.ExpectedLifecycleRevision)
            {
                var currentLease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
                return new(
                    LuaMDeepCryoWriteStatus.LifecycleConflict,
                    request.SnapshotId,
                    LeaseId: currentLease?.LeaseId,
                    Authority: currentLease == null ? null : ToPresenceAuthorityRecord(currentLease));
            }
            if (profile.LifecycleRevision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, request.SnapshotId);

            var snapshot = await db.DbContext.LuaMDeepCryoSnapshots
                .SingleOrDefaultAsync(value => value.Id == request.SnapshotId &&
                                               value.ProfileId == request.ProfileId &&
                                               value.PlayerUserId == request.UserId.UserId &&
                                               value.Slot == request.Slot,
                    cancel);
            if (snapshot == null)
                return new(LuaMDeepCryoWriteStatus.NotFound, request.SnapshotId);
            if (snapshot.Status != DbLuaMDeepCryoSnapshotStatus.Consumed)
            {
                return new(
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined
                        ? LuaMDeepCryoWriteStatus.Quarantined
                        : LuaMDeepCryoWriteStatus.InvalidState,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status);
            }
            if (snapshot.Revision != request.ExpectedAcknowledgedRevision)
                return new(LuaMDeepCryoWriteStatus.RevisionConflict, snapshot.Id, snapshot.Revision, snapshot.Status);
            if (snapshot.Revision == long.MaxValue)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            var lease = await db.DbContext.LuaMCharacterPresenceLeases
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (lease == null || lease.LeaseId != request.LeaseId)
            {
                return new(
                    LuaMDeepCryoWriteStatus.LeaseConflict,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status,
                    lease?.LeaseId,
                    ToDeepCryoSnapshotRecord(snapshot, lease),
                    lease == null ? null : ToPresenceAuthorityRecord(lease));
            }
            if (lease.Phase != DbLuaMCharacterPresencePhase.Playable ||
                lease.SnapshotId != snapshot.Id ||
                lease.Revision != request.ExpectedPresenceRevision ||
                lease.AuthorityLifecycleRevision != request.ExpectedLifecycleRevision)
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

            var acknowledgement = await db.DbContext.LuaMDeepCryoOperations.AsNoTracking()
                .SingleOrDefaultAsync(value => value.OperationId == request.AcknowledgementOperationId, cancel);
            if (acknowledgement == null ||
                acknowledgement.SnapshotId != snapshot.Id ||
                acknowledgement.ProfileId != snapshot.ProfileId ||
                acknowledgement.Kind != DbLuaMDeepCryoOperationKind.Discard ||
                acknowledgement.ResultStatus != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                acknowledgement.ResultRevision != request.ExpectedAcknowledgedRevision ||
                acknowledgement.LeaseId != request.LeaseId ||
                acknowledgement.Reason != null)
            {
                return new(
                    LuaMDeepCryoWriteStatus.InvalidState,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status,
                    lease.LeaseId,
                    ToDeepCryoSnapshotRecord(snapshot, lease),
                    ToPresenceAuthorityRecord(lease));
            }

            var career = await db.DbContext.LuaMCharacterCareers
                .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
            if (career == null || career.Status != DbLuaMCharacterCareerStatus.Playable ||
                career.Revision == long.MaxValue)
            {
                return new(
                    LuaMDeepCryoWriteStatus.InvalidState,
                    snapshot.Id,
                    snapshot.Revision,
                    snapshot.Status,
                    lease.LeaseId,
                    ToDeepCryoSnapshotRecord(snapshot, lease),
                    ToPresenceAuthorityRecord(lease));
            }

            var quarantinedAt = AsUtc(request.QuarantinedAtUtc);
            if (quarantinedAt < snapshot.UpdatedAtUtc || quarantinedAt < lease.RenewedAtUtc)
                return new(LuaMDeepCryoWriteStatus.InvalidRequest, snapshot.Id, snapshot.Revision, snapshot.Status);

            snapshot.Status = DbLuaMDeepCryoSnapshotStatus.Quarantined;
            snapshot.Revision++;
            snapshot.UpdatedAtUtc = quarantinedAt;
            snapshot.ConsumedAtUtc = null;
            snapshot.QuarantinedAtUtc = quarantinedAt;
            snapshot.QuarantineReason = request.Reason;
            career.Status = DbLuaMCharacterCareerStatus.Quarantined;
            career.Revision++;
            career.UpdatedAtUtc = quarantinedAt;
            profile.LifecycleRevision++;
            db.DbContext.LuaMCharacterPresenceLeases.Remove(lease);
            db.DbContext.LuaMDeepCryoOperations.Add(new LuaMDeepCryoOperation
            {
                OperationId = request.OperationId,
                OperationIdentityKey = identityKey,
                SnapshotId = snapshot.Id,
                ProfileId = snapshot.ProfileId,
                Kind = DbLuaMDeepCryoOperationKind.Quarantine,
                ResultStatus = snapshot.Status,
                ResultRevision = snapshot.Revision,
                LeaseId = request.LeaseId,
                RoundId = lease.RoundId,
                Reason = request.Reason,
                CreatedAtUtc = quarantinedAt,
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
                identityKey,
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
                identityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.RevisionConflict);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM acknowledged-publication quarantine outcome requires re-read: {exception.Message}");
            return await ResolveDeepCryoFailureAsync(
                request.OperationId,
                identityKey,
                request.SnapshotId,
                LuaMDeepCryoWriteStatus.UnknownOutcome);
        }
    }

    private async Task<LuaMCharacterPresenceWriteResult?> FindPresenceReplayAsync(
        Guid operationId,
        string identityKey,
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel)
    {
        await using var db = await GetDb(cancel);
        return await FindPresenceReplayAsync(
            db.DbContext,
            operationId,
            identityKey,
            userId,
            profileId,
            slot,
            cancel);
    }

    private static async Task<LuaMCharacterPresenceWriteResult?> FindPresenceReplayAsync(
        ServerDbContext db,
        Guid operationId,
        string identityKey,
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel)
    {
        var operation = await db.LuaMCharacterPresenceOperations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.OperationId == operationId, cancel);
        if (operation == null)
            return null;
        if (!string.Equals(operation.OperationIdentityKey, identityKey, StringComparison.Ordinal))
            return new(LuaMDeepCryoWriteStatus.IdentityConflict);

        var lifecycleRevision = await db.Profile.AsNoTracking()
            .Where(value => value.Id == profileId &&
                            value.Slot == slot &&
                            !value.IsArchived &&
                            value.Preference.UserId == userId.UserId)
            .Select(value => (long?) value.LifecycleRevision)
            .SingleOrDefaultAsync(cancel);
        var currentLease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId, cancel);
        var currentAuthority = currentLease == null ? null : ToPresenceAuthorityRecord(currentLease);
        var historicalAuthority = ToPresenceAuthorityRecord(operation);

        // An immutable operation proves historical success, but publication may
        // use it only while that exact result remains the current row and profile
        // epoch. Reclaim/Store/release therefore turn old exact replays into an
        // authoritative conflict instead of returning a stale ticket.
        var resultStillCurrent = lifecycleRevision == operation.ResultLifecycleRevision &&
                                 (operation.ResultHasAuthority
                                     ? PresenceAuthorityEquals(currentAuthority, historicalAuthority)
                                     : currentAuthority == null);
        if (!resultStillCurrent)
        {
            return new(
                LuaMDeepCryoWriteStatus.LifecycleConflict,
                currentAuthority,
                lifecycleRevision);
        }

        return new(
            LuaMDeepCryoWriteStatus.AlreadyProcessed,
            currentAuthority,
            lifecycleRevision);
    }

    private async Task<LuaMCharacterPresenceWriteResult> ResolvePresenceFailureAsync(
        Guid operationId,
        string identityKey,
        NetUserId userId,
        int profileId,
        int slot,
        LuaMDeepCryoWriteStatus fallback)
    {
        var replay = await FindPresenceReplayAsync(
            operationId,
            identityKey,
            userId,
            profileId,
            slot,
            CancellationToken.None);
        if (replay != null)
            return replay;

        await using var db = await GetDb(CancellationToken.None);
        return await CreatePresenceConflictResultAsync(
            db.DbContext,
            userId,
            profileId,
            slot,
            fallback,
            CancellationToken.None);
    }

    private async Task<LuaMCharacterPresenceWriteResult> ResolvePresenceRenewFailureAsync(
        LuaMCharacterPresenceRenewRequest request,
        string identityKey,
        LuaMDeepCryoWriteStatus fallback)
    {
        await using var db = await GetDb(CancellationToken.None);
        var lifecycleRevision = await db.DbContext.Profile.AsNoTracking()
            .Where(value => value.Id == request.ProfileId &&
                            value.Slot == request.Slot &&
                            !value.IsArchived &&
                            value.Preference.UserId == request.UserId.UserId)
            .Select(value => (long?) value.LifecycleRevision)
            .SingleOrDefaultAsync(CancellationToken.None);
        var lease = await db.DbContext.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, CancellationToken.None);
        if (lease == null)
            return new(fallback, LifecycleRevision: lifecycleRevision);

        var authority = ToPresenceAuthorityRecord(lease);
        if (lease.LeaseId != request.LeaseId ||
            lease.Phase != DbLuaMCharacterPresencePhase.Playable ||
            lease.AuthorityLifecycleRevision != request.ExpectedAuthorityLifecycleRevision ||
            lifecycleRevision != request.ExpectedAuthorityLifecycleRevision)
        {
            return new(LuaMDeepCryoWriteStatus.LeaseConflict, authority, lifecycleRevision);
        }

        if (lease.LastRenewalOperationId == request.OperationId)
        {
            return lease.LastRenewalOperationIdentityKey == identityKey &&
                   lease.Revision == request.ExpectedLeaseRevision + 1
                ? new(LuaMDeepCryoWriteStatus.AlreadyProcessed, authority, lifecycleRevision)
                : new(LuaMDeepCryoWriteStatus.IdentityConflict, authority, lifecycleRevision);
        }

        return new(fallback, authority, lifecycleRevision);
    }

    private static async Task<LuaMCharacterPresenceWriteResult> CreatePresenceConflictResultAsync(
        ServerDbContext db,
        NetUserId userId,
        int profileId,
        int slot,
        LuaMDeepCryoWriteStatus status,
        CancellationToken cancel)
    {
        var lifecycleRevision = await db.Profile.AsNoTracking()
            .Where(value => value.Id == profileId &&
                            value.Slot == slot &&
                            !value.IsArchived &&
                            value.Preference.UserId == userId.UserId)
            .Select(value => (long?) value.LifecycleRevision)
            .SingleOrDefaultAsync(cancel);
        var lease = await db.LuaMCharacterPresenceLeases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId, cancel);
        return new(
            status,
            lease == null ? null : ToPresenceAuthorityRecord(lease),
            lifecycleRevision);
    }

    private static Task<bool> HasSemanticActiveCryoSnapshotAsync(
        ServerDbContext db,
        int profileId,
        CancellationToken cancel)
        => db.LuaMDeepCryoSnapshots.AsNoTracking()
            .AnyAsync(value => value.ProfileId == profileId &&
                               (value.Status != DbLuaMDeepCryoSnapshotStatus.Consumed ||
                                db.LuaMCharacterPresenceLeases.Any(lease =>
                                    lease.ProfileId == value.ProfileId &&
                                    lease.SnapshotId == value.Id &&
                                    lease.Phase == DbLuaMCharacterPresencePhase.RestoreClaim)),
                cancel);

    private static LuaMCharacterPresenceAuthorityRecord ToPresenceAuthorityRecord(
        LuaMCharacterPresenceLease lease)
        => new(
            lease.ProfileId,
            lease.SnapshotId,
            lease.LeaseId,
            lease.Phase,
            lease.ServerInstanceId,
            lease.RoundId,
            lease.AcquiredAtUtc,
            lease.RenewedAtUtc,
            lease.ExpiresAtUtc,
            lease.Revision,
            lease.AuthorityLifecycleRevision);

    private static LuaMCharacterPresenceAuthorityRecord? ToPresenceAuthorityRecord(
        LuaMCharacterPresenceOperation operation)
    {
        if (!operation.ResultHasAuthority ||
            operation.ResultLeaseId == null ||
            operation.ResultPhase == null ||
            operation.ResultServerInstanceId == null ||
            operation.ResultRoundId == null ||
            operation.ResultAcquiredAtUtc == null ||
            operation.ResultRenewedAtUtc == null ||
            operation.ResultExpiresAtUtc == null ||
            operation.ResultLeaseRevision == null ||
            operation.ResultAuthorityLifecycleRevision == null)
        {
            return null;
        }

        return new(
            operation.ProfileId,
            operation.ResultSnapshotId,
            operation.ResultLeaseId.Value,
            operation.ResultPhase.Value,
            operation.ResultServerInstanceId,
            operation.ResultRoundId.Value,
            operation.ResultAcquiredAtUtc.Value,
            operation.ResultRenewedAtUtc.Value,
            operation.ResultExpiresAtUtc.Value,
            operation.ResultLeaseRevision.Value,
            operation.ResultAuthorityLifecycleRevision.Value);
    }

    private static bool PresenceAuthorityEquals(
        LuaMCharacterPresenceAuthorityRecord? left,
        LuaMCharacterPresenceAuthorityRecord? right)
        => left == right;

    private static void AddPresenceOperation(
        ServerDbContext db,
        Guid operationId,
        string identityKey,
        int profileId,
        DbLuaMCharacterPresenceOperationKind kind,
        LuaMCharacterPresenceAuthorityRecord? authority,
        long lifecycleRevision,
        string? reason,
        DateTime createdAt)
    {
        db.LuaMCharacterPresenceOperations.Add(new LuaMCharacterPresenceOperation
        {
            OperationId = operationId,
            OperationIdentityKey = identityKey,
            ProfileId = profileId,
            Kind = kind,
            ResultHasAuthority = authority != null,
            ResultSnapshotId = authority?.SnapshotId,
            ResultLeaseId = authority?.LeaseId,
            ResultPhase = authority?.Phase,
            ResultServerInstanceId = authority?.ServerInstanceId,
            ResultRoundId = authority?.RoundId,
            ResultAcquiredAtUtc = authority?.AcquiredAtUtc,
            ResultRenewedAtUtc = authority?.RenewedAtUtc,
            ResultExpiresAtUtc = authority?.ExpiresAtUtc,
            ResultLeaseRevision = authority?.Revision,
            ResultAuthorityLifecycleRevision = authority?.AuthorityLifecycleRevision,
            ResultLifecycleRevision = lifecycleRevision,
            Reason = reason,
            CreatedAtUtc = createdAt,
        });
    }

    private static bool IsValidPresenceReserveRequest(LuaMCharacterPresenceReserveRequest request)
        => request.OperationId != Guid.Empty &&
           request.LeaseId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 && request.RoundId >= 0 &&
           request.ExpectedLifecycleRevision >= 0 &&
           !string.IsNullOrWhiteSpace(request.ServerInstanceId) &&
           request.ServerInstanceId.Length <= LuaMDeepCryoLimits.MaxServerInstanceIdLength &&
           request.ReservedAtUtc != default && request.LeaseExpiresAtUtc != default &&
           AsUtc(request.ReservedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);

    private static bool IsValidPresencePublishRequest(LuaMCharacterPresencePublishRequest request)
        => request.OperationId != Guid.Empty &&
           request.LeaseId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 &&
           request.ExpectedLeaseRevision >= 0 && request.ExpectedLifecycleRevision >= 0 &&
           request.PublishedAtUtc != default && request.LeaseExpiresAtUtc != default &&
           AsUtc(request.PublishedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);

    private static bool IsValidPresenceRenewRequest(LuaMCharacterPresenceRenewRequest request)
        => request.OperationId != Guid.Empty &&
           request.LeaseId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 &&
           request.ExpectedLeaseRevision >= 0 && request.ExpectedAuthorityLifecycleRevision >= 0 &&
           request.RenewedAtUtc != default && request.LeaseExpiresAtUtc != default &&
           AsUtc(request.RenewedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);

    private static bool IsValidPresenceReleaseRequest(LuaMCharacterPresenceReleaseRequest request)
        => request.OperationId != Guid.Empty &&
           request.LeaseId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 &&
           (request.ExpectedPhase is DbLuaMCharacterPresencePhase.FreshReserved or
               DbLuaMCharacterPresencePhase.Playable) &&
           (request.ExpectedPhase != DbLuaMCharacterPresencePhase.FreshReserved ||
            request.ExpectedSnapshotId == null) &&
           request.ExpectedLeaseRevision >= 0 && request.ExpectedAuthorityLifecycleRevision >= 0 &&
           !string.IsNullOrWhiteSpace(request.Reason) &&
           request.Reason.Length <= LuaMDeepCryoLimits.MaxReasonLength &&
           request.ReleasedAtUtc != default;

    private static bool IsValidPresenceReclaimRequest(LuaMCharacterPresenceReclaimRequest request)
        => request.OperationId != Guid.Empty &&
           request.ExpectedLeaseId != Guid.Empty && request.NewLeaseId != Guid.Empty &&
           request.ExpectedLeaseId != request.NewLeaseId &&
           request.ProfileId > 0 && request.Slot >= 0 && request.NewRoundId >= 0 &&
           (request.ExpectedPhase is DbLuaMCharacterPresencePhase.FreshReserved or
               DbLuaMCharacterPresencePhase.Playable) &&
           (request.ExpectedPhase != DbLuaMCharacterPresencePhase.FreshReserved ||
            request.ExpectedSnapshotId == null) &&
           request.ExpectedLeaseRevision >= 0 && request.ExpectedAuthorityLifecycleRevision >= 0 &&
           !string.IsNullOrWhiteSpace(request.NewServerInstanceId) &&
           request.NewServerInstanceId.Length <= LuaMDeepCryoLimits.MaxServerInstanceIdLength &&
           request.ReclaimedAtUtc != default && request.LeaseExpiresAtUtc != default &&
           AsUtc(request.ReclaimedAtUtc) < AsUtc(request.LeaseExpiresAtUtc);

    private static bool IsValidQuarantineAcknowledgedRequest(
        LuaMDeepCryoQuarantineAcknowledgedPublicationRequest request)
        => request.OperationId != Guid.Empty &&
           request.AcknowledgementOperationId != Guid.Empty &&
           request.LeaseId != Guid.Empty &&
           request.ProfileId > 0 && request.Slot >= 0 && request.SnapshotId > 0 &&
           request.ExpectedAcknowledgedRevision >= 0 && request.ExpectedPresenceRevision >= 0 &&
           request.ExpectedLifecycleRevision >= 0 &&
           !string.IsNullOrWhiteSpace(request.Reason) &&
           request.Reason.Length <= LuaMDeepCryoLimits.MaxReasonLength &&
           request.QuarantinedAtUtc != default;

    private static string CreatePresenceReserveIdentityKey(LuaMCharacterPresenceReserveRequest request)
        => HashPresenceIdentity(writer =>
        {
            writer.Write((int) DbLuaMCharacterPresenceOperationKind.Reserve);
            WritePresenceIdentity(writer, request.OperationId, request.UserId, request.ProfileId, request.Slot);
            writer.Write(request.LeaseId.ToByteArray());
            writer.Write(request.ServerInstanceId);
            writer.Write(request.RoundId);
            writer.Write(AsUtc(request.ReservedAtUtc).Ticks);
            writer.Write(AsUtc(request.LeaseExpiresAtUtc).Ticks);
            writer.Write(request.ExpectedLifecycleRevision);
        });

    private static string CreatePresencePublishIdentityKey(LuaMCharacterPresencePublishRequest request)
        => HashPresenceIdentity(writer =>
        {
            writer.Write((int) DbLuaMCharacterPresenceOperationKind.Publish);
            WritePresenceIdentity(writer, request.OperationId, request.UserId, request.ProfileId, request.Slot);
            writer.Write(request.LeaseId.ToByteArray());
            writer.Write(request.ExpectedLeaseRevision);
            writer.Write(request.ExpectedLifecycleRevision);
            writer.Write(AsUtc(request.PublishedAtUtc).Ticks);
            writer.Write(AsUtc(request.LeaseExpiresAtUtc).Ticks);
        });

    private static string CreatePresenceRenewIdentityKey(LuaMCharacterPresenceRenewRequest request)
        => HashPresenceIdentity(writer =>
        {
            writer.Write("bounded-renew-v1");
            WritePresenceIdentity(writer, request.OperationId, request.UserId, request.ProfileId, request.Slot);
            writer.Write(request.LeaseId.ToByteArray());
            writer.Write(request.ExpectedLeaseRevision);
            writer.Write(request.ExpectedAuthorityLifecycleRevision);
            writer.Write(AsUtc(request.RenewedAtUtc).Ticks);
            writer.Write(AsUtc(request.LeaseExpiresAtUtc).Ticks);
        });

    private static string CreatePresenceReleaseIdentityKey(LuaMCharacterPresenceReleaseRequest request)
        => HashPresenceIdentity(writer =>
        {
            writer.Write((int) DbLuaMCharacterPresenceOperationKind.Release);
            WritePresenceIdentity(writer, request.OperationId, request.UserId, request.ProfileId, request.Slot);
            writer.Write(request.LeaseId.ToByteArray());
            writer.Write((int) request.ExpectedPhase);
            writer.Write(request.ExpectedSnapshotId.HasValue);
            if (request.ExpectedSnapshotId.HasValue)
                writer.Write(request.ExpectedSnapshotId.Value);
            writer.Write(request.ExpectedLeaseRevision);
            writer.Write(request.ExpectedAuthorityLifecycleRevision);
            writer.Write(request.Reason);
            writer.Write(AsUtc(request.ReleasedAtUtc).Ticks);
        });

    private static string CreatePresenceReclaimIdentityKey(LuaMCharacterPresenceReclaimRequest request)
        => HashPresenceIdentity(writer =>
        {
            writer.Write((int) DbLuaMCharacterPresenceOperationKind.Reclaim);
            WritePresenceIdentity(writer, request.OperationId, request.UserId, request.ProfileId, request.Slot);
            writer.Write(request.ExpectedLeaseId.ToByteArray());
            writer.Write(request.NewLeaseId.ToByteArray());
            writer.Write((int) request.ExpectedPhase);
            writer.Write(request.ExpectedSnapshotId.HasValue);
            if (request.ExpectedSnapshotId.HasValue)
                writer.Write(request.ExpectedSnapshotId.Value);
            writer.Write(request.ExpectedLeaseRevision);
            writer.Write(request.ExpectedAuthorityLifecycleRevision);
            writer.Write(request.NewServerInstanceId);
            writer.Write(request.NewRoundId);
            writer.Write(AsUtc(request.ReclaimedAtUtc).Ticks);
            writer.Write(AsUtc(request.LeaseExpiresAtUtc).Ticks);
        });

    private static string CreateQuarantineAcknowledgedIdentityKey(
        LuaMDeepCryoQuarantineAcknowledgedPublicationRequest request)
        => HashPresenceIdentity(writer =>
        {
            writer.Write("quarantine-acknowledged-publication-v1");
            WritePresenceIdentity(writer, request.OperationId, request.UserId, request.ProfileId, request.Slot);
            writer.Write(request.AcknowledgementOperationId.ToByteArray());
            writer.Write(request.SnapshotId);
            writer.Write(request.ExpectedAcknowledgedRevision);
            writer.Write(request.LeaseId.ToByteArray());
            writer.Write(request.ExpectedPresenceRevision);
            writer.Write(request.ExpectedLifecycleRevision);
            writer.Write(request.Reason);
            writer.Write(AsUtc(request.QuarantinedAtUtc).Ticks);
        });

    private static void WritePresenceIdentity(
        BinaryWriter writer,
        Guid operationId,
        NetUserId userId,
        int profileId,
        int slot)
    {
        writer.Write(operationId.ToByteArray());
        writer.Write(userId.UserId.ToByteArray());
        writer.Write(profileId);
        writer.Write(slot);
    }

    private static string HashPresenceIdentity(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("luam-character-presence-operation-v1");
            write(writer);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int) buffer.Length))));
    }
}
