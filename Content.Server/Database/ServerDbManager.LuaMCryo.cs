using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
    public Task<LuaMCharacterPresenceAuthorityRecord?> GetLuaMCharacterPresenceAuthorityAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.GetLuaMCharacterPresenceAuthorityAsync(userId, profileId, slot, cancel));

    public Task<LuaMCharacterPresenceWriteResult> ReserveLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReserveRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReserveLuaMCharacterPresenceAsync(request, cancel));

    public Task<LuaMCharacterPresenceWriteResult> PublishLuaMCharacterPresenceAsync(
        LuaMCharacterPresencePublishRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.PublishLuaMCharacterPresenceAsync(request, cancel));

    public Task<LuaMCharacterPresenceWriteResult> RenewLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceRenewRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RenewLuaMCharacterPresenceAsync(request, cancel));

    public Task<LuaMCharacterPresenceWriteResult> ReleaseLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReleaseRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReleaseLuaMCharacterPresenceAsync(request, cancel));

    public Task<LuaMCharacterPresenceWriteResult> ReclaimLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReclaimRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReclaimLuaMCharacterPresenceAsync(request, cancel));

    public Task<LuaMDeepCryoStorePrecondition?> GetLuaMDeepCryoStorePreconditionAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, slot, cancel));

    public Task<LuaMDeepCryoWriteResult> StoreLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoStoreRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.StoreLuaMDeepCryoSnapshotAsync(request, cancel));

    public Task<LuaMDeepCryoSnapshotRecord?> GetLuaMDeepCryoSnapshotAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.GetLuaMDeepCryoSnapshotAsync(userId, profileId, slot, cancel));

    public Task<LuaMDeepCryoWriteResult> ClaimLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoClaimRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ClaimLuaMDeepCryoRestoreAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> CompleteLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoCompleteRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.CompleteLuaMDeepCryoRestoreAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> AbortLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoAbortRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.AbortLuaMDeepCryoRestoreAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> RollbackLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoRollbackPublicationRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RollbackLuaMDeepCryoPublicationAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> AuthorizeLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoAuthorizePublicationRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.AuthorizeLuaMDeepCryoPublicationAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> AcknowledgeLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoAcknowledgePublicationRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.AcknowledgeLuaMDeepCryoPublicationAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> QuarantineAuthorizedLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoQuarantineAuthorizedPublicationRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoQuarantineAcknowledgedPublicationRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> DiscardLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoDiscardRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.DiscardLuaMDeepCryoSnapshotAsync(request, cancel));

    public Task<LuaMDeepCryoWriteResult> QuarantineLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoQuarantineRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.QuarantineLuaMDeepCryoSnapshotAsync(request, cancel));

    public Task<int> RecoverExpiredLuaMDeepCryoLeasesAsync(
        DateTime nowUtc,
        Guid[]? protectedLeaseIds = null,
        int maxCount = 100,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RecoverExpiredLuaMDeepCryoLeasesAsync(
            nowUtc,
            protectedLeaseIds,
            maxCount,
            cancel));

    public Task<bool> ValidateLuaMDeepCryoProfileAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ValidateLuaMDeepCryoProfileAsync(userId, profileId, slot, cancel));
}
