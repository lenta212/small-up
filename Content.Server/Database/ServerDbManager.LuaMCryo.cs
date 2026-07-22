using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
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
        int maxCount = 100,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RecoverExpiredLuaMDeepCryoLeasesAsync(nowUtc, maxCount, cancel));

    public Task<bool> ValidateLuaMDeepCryoProfileAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ValidateLuaMDeepCryoProfileAsync(userId, profileId, slot, cancel));
}
