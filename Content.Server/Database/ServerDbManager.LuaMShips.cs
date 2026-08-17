using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
    public Task<LuaMShipPersistenceWriteResult> StoreLuaMShipSnapshotAsync(
        LuaMShipSnapshotStoreRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.StoreLuaMShipSnapshotAsync(request, cancel));

    public Task<LuaMShipSnapshotRecord?> GetLuaMShipSnapshotAsync(
        Guid shipId,
        NetUserId ownerUserId,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.GetLuaMShipSnapshotAsync(shipId, ownerUserId, cancel));

    public Task<IReadOnlyList<LuaMShipRegistryRecord>> GetLuaMShipSnapshotsByOwnerAsync(
        NetUserId ownerUserId,
        bool includeRetired = false,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.GetLuaMShipSnapshotsByOwnerAsync(ownerUserId, includeRetired, cancel));

    public Task<LuaMShipPersistenceWriteResult> ClaimLuaMShipRestoreAsync(
        LuaMShipRestoreClaimRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ClaimLuaMShipRestoreAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> CompleteLuaMShipRestoreAsync(
        LuaMShipRestoreCompleteRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.CompleteLuaMShipRestoreAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> AbortLuaMShipRestoreAsync(
        LuaMShipRestoreAbortRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.AbortLuaMShipRestoreAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> ReleaseLuaMShipPresenceAsync(
        LuaMShipPresenceReleaseRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReleaseLuaMShipPresenceAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> RenewLuaMShipLeaseAsync(
        LuaMShipLeaseRenewRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RenewLuaMShipLeaseAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> QuarantineLuaMShipSnapshotAsync(
        LuaMShipSnapshotQuarantineRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.QuarantineLuaMShipSnapshotAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> RepairQuarantinedLuaMShipSnapshotAsync(
        LuaMShipSnapshotRepairRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RepairQuarantinedLuaMShipSnapshotAsync(request, cancel));

    public Task<LuaMShipPersistenceWriteResult> RetireLuaMShipSnapshotAsync(
        LuaMShipSnapshotRetireRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RetireLuaMShipSnapshotAsync(request, cancel));

    public Task<int> RecoverExpiredLuaMShipLeasesAsync(
        DateTime nowUtc,
        int maxCount = 100,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RecoverExpiredLuaMShipLeasesAsync(nowUtc, maxCount, cancel));
}
