using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._LuaM.ShipGen;
using Content.Shared.GameTicking;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Asynchronous;
using Robust.Shared.Log;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Coordinates runtime ship snapshots with the database lease/CAS state machine.
/// </summary>
public sealed class LuaMShipPersistenceOrchestrator : EntitySystem
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private LuaMFullShipPersistenceSystem _runtime = default!;
    [Dependency] private LuaMShipGeneratorSystem _shipGenerator = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    private string _serverInstanceId = "new_frontier";
    private TimeSpan _leaseDuration = DefaultLeaseDuration;
    private readonly Dictionary<Guid, LuaMActiveShipLease> _active = new();
    private readonly HashSet<NetUserId> _restoredOwners = new();
    private ISawmill _sawmill = default!;
    private float _renewAccumulator;

    public LuaMShipPersistenceOrchestrator()
    {
    }

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("luam.ship-persistence");
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _renewAccumulator += frameTime;
        var renewEvery = Math.Max(1, _leaseDuration.TotalSeconds / 2);
        if (_renewAccumulator < renewEvery)
            return;

        _renewAccumulator = 0;
        var task = MaintainLeasesAsync(DateTime.UtcNow);
        _taskManager.BlockWaitOnTask(task);
    }

    public void ConfigureForTesting(
        IServerDbManager database,
        LuaMFullShipPersistenceSystem runtime,
        string serverInstanceId,
        TimeSpan? leaseDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverInstanceId);
        _database = database;
        _runtime = runtime;
        _serverInstanceId = serverInstanceId;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    public IReadOnlyCollection<LuaMActiveShipLease> ActiveLeases => _active.Values;

    public async Task<LuaMShipOrchestrationResult> RegisterAsync(
        EntityUid grid,
        NetUserId ownerUserId,
        LuaMShipSnapshotMetadata metadata,
        DateTime nowUtc,
        CancellationToken cancel = default)
    {
        var shipId = _runtime.GetOrAssignShipId(grid);
        if (_active.ContainsKey(shipId))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, "ship already active on this server");
        if (!_runtime.TryCaptureSnapshot(grid, 1, out var snapshot, out var reason))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, reason);

        var write = await _database.StoreLuaMShipSnapshotAsync(
            ToStoreRequest(snapshot, ownerUserId, metadata, null, null, nowUtc), cancel);
        if (!write.Success)
            return Failure(write.Status, $"snapshot registration failed: {write.Status}");
        QueueSavedShipAnalysis(snapshot);
        if (write.Revision == null)
        {
            return await CompensateFailedRegistrationAsync(
                grid,
                snapshot,
                ownerUserId,
                metadata,
                null,
                LuaMShipPersistenceWriteStatus.InvalidState,
                "snapshot registration returned no durable registry revision",
                nowUtc,
                CancellationToken.None);
        }

        var leaseId = Guid.NewGuid();
        LuaMShipPersistenceWriteResult claim;
        try
        {
            claim = await _database.ClaimLuaMShipRestoreAsync(new(
                shipId,
                ownerUserId,
                write.Revision.Value,
                metadata.SourceRoundId,
                leaseId,
                _serverInstanceId,
                nowUtc,
                nowUtc + _leaseDuration), cancel);
        }
        catch (Exception exception)
        {
            return await CompensateFailedRegistrationAsync(
                grid,
                snapshot,
                ownerUserId,
                metadata,
                leaseId,
                LuaMShipPersistenceWriteStatus.UnknownOutcome,
                $"registered ship activation claim failed with {exception.GetType().Name}",
                nowUtc,
                CancellationToken.None);
        }
        if (!claim.Success || claim.Revision == null || claim.LeaseRevision == null)
        {
            return await CompensateFailedRegistrationAsync(
                grid,
                snapshot,
                ownerUserId,
                metadata,
                leaseId,
                claim.Status,
                $"registered ship activation claim failed: {claim.Status}",
                nowUtc,
                CancellationToken.None);
        }

        LuaMShipPersistenceWriteResult complete;
        try
        {
            complete = await _database.CompleteLuaMShipRestoreAsync(new(
                shipId,
                ownerUserId,
                claim.Revision.Value,
                leaseId,
                nowUtc), cancel);
        }
        catch (Exception exception)
        {
            return await CompensateFailedRegistrationAsync(
                grid,
                snapshot,
                ownerUserId,
                metadata,
                leaseId,
                LuaMShipPersistenceWriteStatus.UnknownOutcome,
                $"registered ship activation failed with {exception.GetType().Name}",
                nowUtc,
                CancellationToken.None);
        }
        if (!complete.Success || complete.Revision == null || complete.LeaseRevision == null)
        {
            return await CompensateFailedRegistrationAsync(
                grid,
                snapshot,
                ownerUserId,
                metadata,
                leaseId,
                complete.Status,
                $"registered ship activation failed: {complete.Status}",
                nowUtc,
                CancellationToken.None);
        }

        if (!_runtime.TryCommitSnapshotRevision(grid, snapshot))
        {
            return await CompensateFailedRegistrationAsync(
                grid,
                snapshot,
                ownerUserId,
                metadata,
                leaseId,
                LuaMShipPersistenceWriteStatus.InvalidState,
                "registered ship live revision commit failed",
                nowUtc,
                CancellationToken.None);
        }
        var active = new LuaMActiveShipLease(
            shipId,
            ownerUserId,
            complete.Revision.Value,
            snapshot.Revision,
            leaseId,
            complete.LeaseRevision.Value,
            metadata,
            grid,
            snapshot.Revision);
        _active.Add(shipId, active);
        return new(true, complete.Status, active.RegistryRevision, active.LeaseId, grid, null);
    }

    public async Task<LuaMShipOrchestrationResult> RestoreAsync(
        Guid shipId,
        NetUserId ownerUserId,
        int roundId,
        MapId targetMap,
        DateTime nowUtc,
        Func<EntityUid, bool>? placeRestoredGrid = null,
        CancellationToken cancel = default)
    {
        if (_active.ContainsKey(shipId))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, "ship already active on this server");

        var stored = await _database.GetLuaMShipSnapshotAsync(shipId, ownerUserId, cancel);
        if (stored == null)
            return Failure(LuaMShipPersistenceWriteStatus.NotFound, "snapshot not found");

        var leaseId = Guid.NewGuid();
        var claim = await _database.ClaimLuaMShipRestoreAsync(new(
            shipId,
            ownerUserId,
            stored.Revision,
            roundId,
            leaseId,
            _serverInstanceId,
            nowUtc,
            nowUtc + _leaseDuration), cancel);
        if (!claim.Success || claim.Snapshot == null || claim.Revision == null || claim.LeaseRevision == null)
            return Failure(claim.Status, $"restore claim failed: {claim.Status}");

        var claimed = claim.Snapshot;
        if (!TryDecode(claimed, out var snapshot, out var decodeReason))
        {
            await AbortOrQuarantineAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                decodeReason, nowUtc, cancel);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, decodeReason);
        }

        if (!_runtime.TryRestoreSnapshot(snapshot, targetMap, out var grid, out var restoreReason))
        {
            await AbortOrQuarantineAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                restoreReason, nowUtc, cancel);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, restoreReason);
        }

        bool placementAccepted;
        try
        {
            placementAccepted = placeRestoredGrid == null || placeRestoredGrid(grid);
        }
        catch (Exception exception)
        {
            _runtime.DeleteGrid(grid);
            var placementReason = $"restored ship placement failed with {exception.GetType().Name}";
            await AbortOrQuarantineAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                placementReason, nowUtc, CancellationToken.None);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, placementReason);
        }

        if (!placementAccepted)
        {
            _runtime.DeleteGrid(grid);
            const string placementReason = "selected shipyard gate is occupied or cannot fit this ship";
            await AbortOrQuarantineAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                placementReason, nowUtc, cancel);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, placementReason);
        }

        LuaMShipPersistenceWriteResult complete;
        try
        {
            complete = await _database.CompleteLuaMShipRestoreAsync(new(
                shipId,
                ownerUserId,
                claim.Revision.Value,
                leaseId,
                nowUtc), cancel);
        }
        catch (Exception exception)
        {
            _runtime.DeleteGrid(grid);
            var completionReason = $"restore completion failed with {exception.GetType().Name}";
            await RollBackFailedRestoreAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                completionReason, nowUtc);
            return Failure(LuaMShipPersistenceWriteStatus.UnknownOutcome, completionReason);
        }
        if (!complete.Success || complete.Revision == null || complete.LeaseRevision == null)
        {
            _runtime.DeleteGrid(grid);
            var completionReason = $"restore completion failed: {complete.Status}";
            var rolledBack = await RollBackFailedRestoreAsync(
                claimed,
                claim.Revision.Value,
                leaseId,
                ownerUserId,
                completionReason,
                nowUtc);
            return Failure(
                rolledBack ? complete.Status : LuaMShipPersistenceWriteStatus.UnknownOutcome,
                completionReason);
        }

        var active = new LuaMActiveShipLease(
            shipId,
            ownerUserId,
            complete.Revision.Value,
            snapshot.Revision,
            leaseId,
            complete.LeaseRevision.Value,
            MetadataFrom(claimed),
            grid,
            claimed.PayloadRevision);
        _active.Add(shipId, active);
        return new(true, complete.Status, active.RegistryRevision, active.LeaseId, grid, null);
    }

    public Task<LuaMShipOrchestrationResult> RestoreClaimAsync(
        Guid shipId,
        NetUserId ownerUserId,
        int roundId,
        MapId targetMap,
        DateTime nowUtc,
        Func<EntityUid, bool>? placeRestoredGrid = null,
        CancellationToken cancel = default)
        => RestoreAsync(shipId, ownerUserId, roundId, targetMap, nowUtc, placeRestoredGrid, cancel);

    public async Task<LuaMShipOrchestrationResult> RenewAsync(
        Guid shipId,
        DateTime nowUtc,
        CancellationToken cancel = default)
    {
        if (!_active.TryGetValue(shipId, out var active))
            return Failure(LuaMShipPersistenceWriteStatus.NotFound, "active ship lease not found");
        if (!Exists(active.Grid) || TerminatingOrDeleted(active.Grid))
        {
            return await ReleaseAndDeactivateAsync(
                active,
                "active ship grid no longer exists",
                nowUtc,
                cancel);
        }

        var renewed = await _database.RenewLuaMShipLeaseAsync(new(
            active.ShipId,
            active.OwnerUserId,
            active.RegistryRevision,
            active.LeaseId,
            active.LeaseRevision,
            nowUtc,
            nowUtc + _leaseDuration), cancel);
        if (!renewed.Success || renewed.LeaseRevision == null)
        {
            return Failure(renewed.Status, $"lease renewal failed: {renewed.Status}");
        }

        _active[shipId] = active with { LeaseRevision = renewed.LeaseRevision.Value };
        return new(true, renewed.Status, active.RegistryRevision, active.LeaseId, active.Grid, null);
    }

    public async Task<LuaMShipOrchestrationResult> StoreAndDeactivateAsync(
        Guid shipId,
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel = default)
    {
        if (!_active.TryGetValue(shipId, out var active))
            return Failure(LuaMShipPersistenceWriteStatus.NotFound, "active ship lease not found");
        if (!Exists(active.Grid) || TerminatingOrDeleted(active.Grid))
        {
            return await ReleaseAndDeactivateAsync(
                active,
                "active ship grid no longer exists before snapshot capture",
                nowUtc,
                cancel);
        }

        if (active.PayloadRevision == long.MaxValue)
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, "snapshot revision exhausted");
        if (!_runtime.TryCaptureSnapshot(active.Grid, active.PayloadRevision + 1, out var snapshot, out var reason))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, reason);

        var stored = await _database.StoreLuaMShipSnapshotAsync(
            ToStoreRequest(snapshot, active.OwnerUserId, active.Metadata with { SourceRoundId = roundId },
                active.RegistryRevision, active.LeaseId, nowUtc), cancel);
        if (!stored.Success)
            return Failure(stored.Status, $"snapshot store failed: {stored.Status}");
        QueueSavedShipAnalysis(snapshot);

        _active.Remove(shipId);
        return new(true, stored.Status, stored.Revision, null, active.Grid, null);
    }

    public async Task<LuaMShipOrchestrationResult> RetireAsync(
        Guid shipId,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel = default)
    {
        if (!_active.TryGetValue(shipId, out var active))
            return Failure(LuaMShipPersistenceWriteStatus.NotFound, "active ship lease not found");

        var retired = await _database.RetireLuaMShipSnapshotAsync(new(
            Guid.NewGuid(),
            shipId,
            active.OwnerUserId,
            active.RegistryRevision,
            active.LeaseId,
            reason,
            nowUtc), cancel);
        if (!retired.Success)
            return Failure(retired.Status, $"snapshot retirement failed: {retired.Status}");

        _active.Remove(shipId);
        return new(true, retired.Status, retired.Revision, null, active.Grid, null);
    }

    public async Task SaveAllActiveShipsAsync(
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel = default)
        => await SaveAllActiveShipsAsync(roundId, nowUtc, logFailures: true, cancel: cancel);

    private async Task SaveAllActiveShipsAsync(
        int roundId,
        DateTime nowUtc,
        bool logFailures,
        CancellationToken cancel)
    {
        foreach (var shipId in new List<Guid>(_active.Keys))
        {
            var result = await StoreAndDeactivateAsync(shipId, roundId, nowUtc, cancel);
            if (!result.Success && logFailures)
                _sawmill.Error($"Failed to save active ship {shipId}: {result.Reason ?? result.Status.ToString()}");
        }
    }

    public async Task FinalizeRoundCleanupAsync(
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel = default)
    {
        await SaveAllActiveShipsAsync(roundId, nowUtc, logFailures: false, cancel: cancel);

        foreach (var shipId in new List<Guid>(_active.Keys))
        {
            if (!_active.TryGetValue(shipId, out var active))
                continue;

            var result = await ReleaseAndDeactivateAsync(
                active,
                "final round cleanup could not capture the live ship; preserving its last durable payload",
                nowUtc,
                cancel);
            if (!result.Success)
            {
                _sawmill.Error(
                    $"Failed to release active ship {shipId} during final cleanup: " +
                    $"{result.Reason ?? result.Status.ToString()}");
            }
        }
    }

    public async Task MaintainLeasesAsync(DateTime nowUtc, CancellationToken cancel = default)
    {
        var activeLeasesSafe = await RenewAllActiveShipsAsync(nowUtc, cancel);
        if (!activeLeasesSafe)
            return;

        await _database.RecoverExpiredLuaMShipLeasesAsync(nowUtc, 100, cancel);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_restoredOwners.Add(args.Player.UserId))
            return;

        // Parked ships stay stored until their owner explicitly chooses a free
        // shipyard gate. Login only recovers leases left by a crashed server.
        var task = MaintainLeasesAsync(DateTime.UtcNow);
        _taskManager.BlockWaitOnTask(task);
    }

    private async Task<bool> RenewAllActiveShipsAsync(DateTime nowUtc, CancellationToken cancel)
    {
        var allSafe = true;
        foreach (var shipId in new List<Guid>(_active.Keys))
        {
            var result = await RenewAsync(shipId, nowUtc, cancel);
            if (!result.Success)
            {
                allSafe = false;
                _sawmill.Error($"Failed to renew active lease for ship {shipId}: {result.Reason ?? result.Status.ToString()}");
            }
        }

        return allSafe;
    }

    private void OnGameRunLevelChanged(GameRunLevelChangedEvent args)
    {
        if (args.New != GameRunLevel.PostRound)
            return;

        SaveAllActiveShipsBlocking();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent args)
    {
        var task = FinalizeRoundCleanupAsync(_gameTicker.RoundId, DateTime.UtcNow);
        _taskManager.BlockWaitOnTask(task);
        _restoredOwners.Clear();
        _renewAccumulator = 0;
    }

    private void SaveAllActiveShipsBlocking()
    {
        var task = SaveAllActiveShipsAsync(_gameTicker.RoundId, DateTime.UtcNow);
        _taskManager.BlockWaitOnTask(task);
    }

    private async Task<bool> AbortOrQuarantineAsync(
        LuaMShipSnapshotRecord stored,
        long claimedRevision,
        Guid leaseId,
        NetUserId ownerUserId,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        var abort = await _database.AbortLuaMShipRestoreAsync(new(
            stored.ShipId, ownerUserId, claimedRevision, leaseId, LimitReason(reason), nowUtc), cancel);
        if (abort.Success)
            return true;

        var quarantined = await _database.QuarantineLuaMShipSnapshotAsync(new(
            stored.ShipId, ownerUserId, abort.Revision ?? claimedRevision, abort.LeaseId,
            LimitReason($"restore failed and abort was unsafe: {reason}"), nowUtc), cancel);
        return quarantined.Success || quarantined.SnapshotStatus == DbLuaMShipSnapshotStatus.Quarantined;
    }

    private async Task<bool> RollBackFailedRestoreAsync(
        LuaMShipSnapshotRecord claimed,
        long claimedRevision,
        Guid leaseId,
        NetUserId ownerUserId,
        string reason,
        DateTime nowUtc)
    {
        var current = await _database.GetLuaMShipSnapshotAsync(
            claimed.ShipId,
            ownerUserId,
            CancellationToken.None);
        if (current?.Status == DbLuaMShipSnapshotStatus.Stored &&
            current.Revision > claimedRevision &&
            current.PayloadRevision == claimed.PayloadRevision &&
            current.LeaseId == null)
        {
            return true;
        }

        if (current?.Status == DbLuaMShipSnapshotStatus.Active &&
            claimedRevision < long.MaxValue &&
            current.Revision == claimedRevision + 1 &&
            current.PayloadRevision == claimed.PayloadRevision &&
            current.LeaseId == leaseId)
        {
            var released = await _database.ReleaseLuaMShipPresenceAsync(new(
                claimed.ShipId,
                ownerUserId,
                current.Revision,
                leaseId,
                LimitReason(reason),
                nowUtc), CancellationToken.None);
            if (released.Success)
                return true;

            current = released.Snapshot ?? await _database.GetLuaMShipSnapshotAsync(
                claimed.ShipId,
                ownerUserId,
                CancellationToken.None);
            if (current?.Status == DbLuaMShipSnapshotStatus.Stored &&
                current.PayloadRevision == claimed.PayloadRevision &&
                current.LeaseId == null)
            {
                return true;
            }
        }

        if (current?.Status == DbLuaMShipSnapshotStatus.Restoring &&
            current.Revision == claimedRevision &&
            current.PayloadRevision == claimed.PayloadRevision &&
            current.LeaseId == leaseId)
        {
            return await AbortOrQuarantineAsync(
                claimed,
                claimedRevision,
                leaseId,
                ownerUserId,
                reason,
                nowUtc,
                CancellationToken.None);
        }

        if (current?.Status is DbLuaMShipSnapshotStatus.Active or DbLuaMShipSnapshotStatus.Restoring)
        {
            var quarantined = await _database.QuarantineLuaMShipSnapshotAsync(new(
                claimed.ShipId,
                ownerUserId,
                current.Revision,
                current.LeaseId,
                LimitReason($"restore completion outcome could not be rolled back: {reason}"),
                nowUtc), CancellationToken.None);
            return quarantined.Success || quarantined.SnapshotStatus == DbLuaMShipSnapshotStatus.Quarantined;
        }

        return await AbortOrQuarantineAsync(
            claimed,
            claimedRevision,
            leaseId,
            ownerUserId,
            reason,
            nowUtc,
            CancellationToken.None);
    }

    private async Task<LuaMShipOrchestrationResult> CompensateFailedRegistrationAsync(
        EntityUid grid,
        LuaMFullShipSnapshot snapshot,
        NetUserId ownerUserId,
        LuaMShipSnapshotMetadata metadata,
        Guid? leaseId,
        LuaMShipPersistenceWriteStatus originalStatus,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        var failureStatus = originalStatus is LuaMShipPersistenceWriteStatus.Success or
            LuaMShipPersistenceWriteStatus.AlreadyProcessed
                ? LuaMShipPersistenceWriteStatus.InvalidState
                : originalStatus;
        var retirementOperationId = Guid.NewGuid();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var durable = await _database.GetLuaMShipSnapshotAsync(snapshot.ShipId, ownerUserId, cancel);
            if (durable == null)
                break;
            if (durable.Status is DbLuaMShipSnapshotStatus.Quarantined or DbLuaMShipSnapshotStatus.Retired)
            {
                return Failure(failureStatus, reason);
            }

            if (durable.Status == DbLuaMShipSnapshotStatus.Restoring &&
                leaseId is { } restoringLease &&
                durable.LeaseId == restoringLease)
            {
                await _database.AbortLuaMShipRestoreAsync(new(
                    durable.ShipId,
                    ownerUserId,
                    durable.Revision,
                    restoringLease,
                    LimitReason($"registration compensation: {reason}"),
                    nowUtc), cancel);
                continue;
            }

            if (durable.Status is DbLuaMShipSnapshotStatus.Stored or DbLuaMShipSnapshotStatus.Active)
            {
                var durableLease = durable.Status == DbLuaMShipSnapshotStatus.Active &&
                                   leaseId is { } activeLease &&
                                   durable.LeaseId == activeLease
                    ? activeLease
                    : (Guid?) null;
                if (durable.Status == DbLuaMShipSnapshotStatus.Active && durableLease == null)
                    break;

                var retired = await _database.RetireLuaMShipSnapshotAsync(new(
                    retirementOperationId,
                    durable.ShipId,
                    ownerUserId,
                    durable.Revision,
                    durableLease,
                    LimitReason($"registration compensation: {reason}"),
                    nowUtc), cancel);
                if (retired.Success || retired.SnapshotStatus == DbLuaMShipSnapshotStatus.Retired)
                    return Failure(failureStatus, reason);
                continue;
            }

            break;
        }

        var unresolved = await _database.GetLuaMShipSnapshotAsync(snapshot.ShipId, ownerUserId, cancel);
        if (unresolved?.Status is DbLuaMShipSnapshotStatus.Quarantined or DbLuaMShipSnapshotStatus.Retired)
        {
            return Failure(failureStatus, reason);
        }

        if (unresolved?.Status == DbLuaMShipSnapshotStatus.Active &&
            leaseId is { } unresolvedLease &&
            unresolved.LeaseId == unresolvedLease &&
            unresolved.LeaseRevision is { } unresolvedLeaseRevision &&
            unresolved.PayloadRevision == snapshot.Revision &&
            Exists(grid) &&
            !TerminatingOrDeleted(grid))
        {
            _active[snapshot.ShipId] = new LuaMActiveShipLease(
                snapshot.ShipId,
                ownerUserId,
                unresolved.Revision,
                snapshot.Revision,
                unresolvedLease,
                unresolvedLeaseRevision,
                metadata,
                grid,
                unresolved.PayloadRevision);
        }

        return Failure(
            LuaMShipPersistenceWriteStatus.UnknownOutcome,
            $"registration compensation could not prove a non-callable durable row: {reason}");
    }

    private async Task<LuaMShipOrchestrationResult> ReleaseAndDeactivateAsync(
        LuaMActiveShipLease active,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        var released = await _database.ReleaseLuaMShipPresenceAsync(new(
            active.ShipId,
            active.OwnerUserId,
            active.RegistryRevision,
            active.LeaseId,
            LimitReason(reason),
            nowUtc), cancel);

        var durable = released.Snapshot;
        if (!IsSafeReleasedPresence(durable, active))
            durable = await _database.GetLuaMShipSnapshotAsync(active.ShipId, active.OwnerUserId, cancel);
        if (!IsSafeReleasedPresence(durable, active))
        {
            return Failure(
                released.Status,
                $"active presence release could not be proven safe: {released.Status}");
        }

        _active.Remove(active.ShipId);
        return new(
            true,
            released.Success ? released.Status : LuaMShipPersistenceWriteStatus.AlreadyProcessed,
            durable!.Revision,
            null,
            active.Grid,
            null);
    }

    private static bool IsSafeReleasedPresence(
        LuaMShipSnapshotRecord? stored,
        LuaMActiveShipLease active)
        => stored != null &&
           stored.ShipId == active.ShipId &&
           stored.OwnerUserId == active.OwnerUserId &&
           active.RegistryRevision < long.MaxValue &&
           stored.Revision == active.RegistryRevision + 1 &&
           stored.PayloadRevision == active.DurablePayloadRevision &&
           stored.Status == DbLuaMShipSnapshotStatus.Stored &&
           stored.LeaseId == null &&
           stored.LeaseRevision == null;

    private static LuaMShipSnapshotStoreRequest ToStoreRequest(
        LuaMFullShipSnapshot snapshot,
        NetUserId ownerUserId,
        LuaMShipSnapshotMetadata metadata,
        long? expectedRevision,
        Guid? leaseId,
        DateTime nowUtc)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return new(
            snapshot.ShipId,
            ownerUserId,
            expectedRevision,
            snapshot.Revision,
            leaseId,
            metadata.VesselPrototypeId,
            metadata.ShipName,
            metadata.ShipNameSuffix,
            metadata.PurchasePrice,
            metadata.PurchasedWithVoucher,
            metadata.SourceRoundId,
            LuaMFullShipPersistenceSystem.SnapshotFormatVersion,
            snapshot.FormatVersion,
            payload,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload.Length,
            snapshot.EntityCount,
            snapshot.SourceBuildVersion,
            snapshot.PrototypeManifestHash,
            nowUtc);
    }

    private void QueueSavedShipAnalysis(LuaMFullShipSnapshot snapshot)
    {
        if (!_runtime.TryGetSavedShipManifest(snapshot, out var manifest, out var reason))
        {
            _sawmill.Warning($"Saved ship manifest was not queued for analysis: {reason}");
            return;
        }

        _ = _shipGenerator.AnalyzeSavedShipAsync(manifest);
    }

    private static bool TryDecode(LuaMShipSnapshotRecord stored, out LuaMFullShipSnapshot snapshot, out string reason)
    {
        snapshot = default!;
        reason = string.Empty;
        if (stored.PayloadSizeBytes != stored.Payload.Length ||
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(stored.Payload)),
                stored.PayloadHash,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "database snapshot payload size or hash mismatch";
            return false;
        }

        try
        {
            snapshot = JsonSerializer.Deserialize<LuaMFullShipSnapshot>(stored.Payload)!;
            if (snapshot == null ||
                snapshot.ShipId != stored.ShipId ||
                snapshot.Revision <= 0 ||
                (stored.PayloadRevision is { } payloadRevision && snapshot.Revision != payloadRevision) ||
                stored.SchemaVersion != LuaMFullShipPersistenceSystem.SnapshotFormatVersion ||
                stored.FormatVersion != snapshot.FormatVersion)
            {
                reason = "database snapshot envelope identity or revision mismatch";
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            reason = $"database snapshot envelope is invalid: {exception.Message}";
            return false;
        }
    }

    private static LuaMShipSnapshotMetadata MetadataFrom(LuaMShipSnapshotRecord stored)
        => new(stored.VesselPrototypeId, stored.ShipName, stored.ShipNameSuffix, stored.PurchasePrice,
            stored.PurchasedWithVoucher, stored.SourceRoundId);

    private static string LimitReason(string reason)
        => reason.Length <= LuaMShipPersistenceLimits.MaxReasonLength
            ? reason
            : reason[..LuaMShipPersistenceLimits.MaxReasonLength];

    private static LuaMShipOrchestrationResult Failure(LuaMShipPersistenceWriteStatus status, string reason)
        => new(false, status, null, null, null, reason);
}

public sealed record LuaMShipSnapshotMetadata(
    string VesselPrototypeId,
    string ShipName,
    string? ShipNameSuffix,
    int PurchasePrice,
    bool PurchasedWithVoucher,
    int SourceRoundId);

public sealed record LuaMActiveShipLease(
    Guid ShipId,
    NetUserId OwnerUserId,
    long RegistryRevision,
    long PayloadRevision,
    Guid LeaseId,
    long LeaseRevision,
    LuaMShipSnapshotMetadata Metadata,
    EntityUid Grid,
    long? DurablePayloadRevision = null);

public sealed record LuaMShipOrchestrationResult(
    bool Success,
    LuaMShipPersistenceWriteStatus Status,
    long? Revision,
    Guid? LeaseId,
    EntityUid? Grid,
    string? Reason);
