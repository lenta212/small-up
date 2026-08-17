using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._LuaM.ShipGen;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Components;
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
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    private string _serverInstanceId = "new_frontier";
    private TimeSpan _leaseDuration = DefaultLeaseDuration;
    private readonly Dictionary<Guid, LuaMActiveShipLease> _active = new();
    private readonly HashSet<LuaMShipRestoreScope> _pendingRestoreCommits = new();
    private readonly HashSet<LuaMShipRestoreScope> _pendingRestoreRollbacks = new();
    private readonly HashSet<NetUserId> _restoredOwners = new();
    private readonly object _maintenanceSync = new();
    private bool _maintenanceFrozen;
    private bool _maintenanceSaveInProgress;
    private bool _leaseMaintenanceRunning;
    private int _lifecycleMutationsInFlight;
    private TaskCompletionSource? _lifecycleMutationsDrained;
    private ISawmill _sawmill = default!;
    private float _renewAccumulator;
    private float _restoreRollbackRetryAccumulator;

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
        _restoreRollbackRetryAccumulator += frameTime;
        if (_restoreRollbackRetryAccumulator >= 1f)
        {
            _restoreRollbackRetryAccumulator = 0f;
            RetryPendingRestoreCommits();
            RetryPendingRestoreRollbacks();
        }

        _renewAccumulator += frameTime;
        var renewEvery = Math.Max(1, _leaseDuration.TotalSeconds / 2);
        if (_renewAccumulator < renewEvery)
            return;

        _renewAccumulator = 0;
        StartLeaseMaintenance();
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
        // Integration pairs are recycled without reconstructing every system.
        // Test configuration must not inherit leases or retry scopes from a
        // preceding fixture's mock database.
        _active.Clear();
        _pendingRestoreCommits.Clear();
        _pendingRestoreRollbacks.Clear();
        _restoredOwners.Clear();
        _renewAccumulator = 0;
        _restoreRollbackRetryAccumulator = 0;
        _leaseMaintenanceRunning = false;
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    public IReadOnlyCollection<LuaMActiveShipLease> ActiveLeases => _active.Values;

    public IReadOnlyList<LuaMActiveShipDiagnostic> GetActiveShipDiagnostics(Guid? shipId = null)
    {
        IEnumerable<LuaMActiveShipLease> leases = _active.Values;
        if (shipId != null)
            leases = leases.Where(lease => lease.ShipId == shipId.Value);

        return leases
            .OrderBy(lease => lease.Metadata.ShipName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(lease => lease.ShipId)
            .Select(lease =>
            {
                var gridExists = Exists(lease.Grid) && !TerminatingOrDeleted(lease.Grid);
                return new LuaMActiveShipDiagnostic(
                    lease.ShipId,
                    lease.OwnerUserId,
                    lease.Metadata.ShipName,
                    lease.Metadata.VesselPrototypeId,
                    lease.Grid,
                    gridExists,
                    gridExists ? CountMobStateEntities(lease.Grid) : 0,
                    lease.RegistryRevision,
                    lease.PayloadRevision,
                    lease.DurablePayloadRevision,
                    lease.LeaseId,
                    lease.LeaseRevision);
            })
            .ToList();
    }

    /// <summary>
    /// Once set, no new persistent-ship lifecycle mutation may begin in this
    /// process. A successful maintenance barrier therefore remains valid until
    /// the process is stopped for maintenance.
    /// </summary>
    public bool MaintenanceFrozen
    {
        get
        {
            lock (_maintenanceSync)
                return _maintenanceFrozen;
        }
    }

    private bool TryBeginLifecycleMutation()
    {
        lock (_maintenanceSync)
        {
            if (_maintenanceFrozen)
                return false;

            _lifecycleMutationsInFlight++;
            return true;
        }
    }

    private void EndLifecycleMutation()
    {
        TaskCompletionSource? drained = null;
        lock (_maintenanceSync)
        {
            if (_lifecycleMutationsInFlight <= 0)
                throw new InvalidOperationException("Persistent ship lifecycle mutation accounting underflowed.");

            _lifecycleMutationsInFlight--;
            if (_lifecycleMutationsInFlight == 0)
            {
                drained = _lifecycleMutationsDrained;
                _lifecycleMutationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private Task<LuaMShipOrchestrationResult> RunLifecycleMutationAsync(
        Func<Task<LuaMShipOrchestrationResult>> mutation)
    {
        if (!TryBeginLifecycleMutation())
        {
            return Task.FromResult(Failure(
                LuaMShipPersistenceWriteStatus.InvalidState,
                "ship persistence is frozen for maintenance"));
        }

        return CompleteLifecycleMutationAsync(mutation);
    }

    private async Task<LuaMShipOrchestrationResult> CompleteLifecycleMutationAsync(
        Func<Task<LuaMShipOrchestrationResult>> mutation)
    {
        try
        {
            return await mutation();
        }
        finally
        {
            EndLifecycleMutation();
        }
    }

    private Task RunLifecycleMutationAsync(Func<Task> mutation)
    {
        if (!TryBeginLifecycleMutation())
            return Task.CompletedTask;

        return CompleteLifecycleMutationAsync(mutation);
    }

    private async Task CompleteLifecycleMutationAsync(Func<Task> mutation)
    {
        try
        {
            await mutation();
        }
        finally
        {
            EndLifecycleMutation();
        }
    }

    private bool TryBeginMaintenanceSave(out Task lifecycleMutationsDrained)
    {
        lock (_maintenanceSync)
        {
            if (_maintenanceSaveInProgress)
            {
                lifecycleMutationsDrained = Task.CompletedTask;
                return false;
            }

            _maintenanceSaveInProgress = true;
            _maintenanceFrozen = true;

            if (_lifecycleMutationsInFlight == 0)
            {
                lifecycleMutationsDrained = Task.CompletedTask;
            }
            else
            {
                _lifecycleMutationsDrained ??=
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lifecycleMutationsDrained = _lifecycleMutationsDrained.Task;
            }

            return true;
        }
    }

    private void EndMaintenanceSave()
    {
        lock (_maintenanceSync)
            _maintenanceSaveInProgress = false;
    }

    public async Task<LuaMShipOrchestrationResult> RegisterAsync(
        EntityUid grid,
        NetUserId ownerUserId,
        LuaMShipSnapshotMetadata metadata,
        DateTime nowUtc,
        CancellationToken cancel = default)
        => await RunLifecycleMutationAsync(
            () => RegisterCoreAsync(grid, ownerUserId, metadata, nowUtc, cancel));

    private async Task<LuaMShipOrchestrationResult> RegisterCoreAsync(
        EntityUid grid,
        NetUserId ownerUserId,
        LuaMShipSnapshotMetadata metadata,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        var shipId = _runtime.GetOrAssignShipId(grid);
        if (_active.ContainsKey(shipId))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, "ship already active on this server");
        if (!_runtime.TryCaptureSnapshot(grid, 1, out var snapshot, out var reason))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, reason);
        if (!TryCreateStoreRequest(
                snapshot,
                ownerUserId,
                metadata,
                null,
                null,
                nowUtc,
                out var storeRequest,
                out reason))
        {
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, reason);
        }

        var write = await _database.StoreLuaMShipSnapshotAsync(
            storeRequest,
            cancel);
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
        => await RunLifecycleMutationAsync(
            () => RestoreCoreAsync(
                shipId,
                ownerUserId,
                roundId,
                targetMap,
                nowUtc,
                placeRestoredGrid,
                cancel));

    private async Task<LuaMShipOrchestrationResult> RestoreCoreAsync(
        Guid shipId,
        NetUserId ownerUserId,
        int roundId,
        MapId targetMap,
        DateTime nowUtc,
        Func<EntityUid, bool>? placeRestoredGrid,
        CancellationToken cancel)
    {
        if (_active.ContainsKey(shipId))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, "ship already active on this server");

        // A failed local teardown keeps its exact restore scope for retry. Do
        // not claim another durable snapshot while any such graph can still be
        // present: a rapid retry could otherwise see the old identity and turn
        // a recoverable cleanup fault into a quarantine-worthy mismatch.
        if (_pendingRestoreRollbacks.Any(scope => scope.ShipId == shipId))
        {
            RetryPendingRestoreRollbacks();
            if (_pendingRestoreRollbacks.Any(scope => scope.ShipId == shipId))
            {
                return Failure(
                    LuaMShipPersistenceWriteStatus.InvalidState,
                    "previous restored-ship cleanup is still pending");
            }
        }

        var stored = await _database.GetLuaMShipSnapshotAsync(shipId, ownerUserId, cancel);
        if (stored == null)
            return Failure(LuaMShipPersistenceWriteStatus.NotFound, "snapshot not found");

        if (stored.Status == DbLuaMShipSnapshotStatus.Quarantined)
        {
            var repair = await TryRepairQuarantinedSnapshotAsync(stored, nowUtc, cancel);
            if (!repair.Success)
                return Failure(repair.Status, $"quarantined snapshot repair failed: {repair.Status}");

            stored = await _database.GetLuaMShipSnapshotAsync(shipId, ownerUserId, cancel);
            if (stored == null)
                return Failure(LuaMShipPersistenceWriteStatus.NotFound, "snapshot disappeared after repair");
        }

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
            await QuarantineClaimedSnapshotAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                decodeReason, nowUtc, cancel);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, decodeReason);
        }

        if (!_runtime.TryBeginRestoreSnapshot(snapshot, targetMap, out var restore, out var restoreReason))
        {
            await QuarantineClaimedSnapshotAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                restoreReason, nowUtc, cancel);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, restoreReason);
        }

        var grid = restore.Grid;
        bool placementAccepted;
        try
        {
            placementAccepted = placeRestoredGrid == null || placeRestoredGrid(grid);
        }
        catch (Exception exception)
        {
            var rollbackFailure = TryRollbackRestore(restore);
            var placementReason = WithRollbackFailure(
                $"restored ship placement failed with {exception.GetType().Name}",
                rollbackFailure);
            await AbortOrQuarantineAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                placementReason, nowUtc, CancellationToken.None);
            return Failure(LuaMShipPersistenceWriteStatus.InvalidState, placementReason);
        }

        if (!placementAccepted)
        {
            var rollbackFailure = TryRollbackRestore(restore);
            var placementReason = WithRollbackFailure(
                "selected shipyard gate is occupied or cannot fit this ship",
                rollbackFailure);
            await AbortOrQuarantineAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                placementReason, nowUtc, CancellationToken.None);
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
            var rollbackFailure = TryRollbackRestore(restore);
            var completionReason = WithRollbackFailure(
                $"restore completion failed with {exception.GetType().Name}",
                rollbackFailure);
            await RollBackFailedRestoreAsync(claimed, claim.Revision.Value, leaseId, ownerUserId,
                completionReason, nowUtc);
            return Failure(LuaMShipPersistenceWriteStatus.UnknownOutcome, completionReason);
        }
        if (!complete.Success || complete.Revision == null || complete.LeaseRevision == null)
        {
            var rollbackFailure = TryRollbackRestore(restore);
            var completionReason = WithRollbackFailure(
                $"restore completion failed: {complete.Status}",
                rollbackFailure);
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

        var restoreCommitted = _runtime.CommitRestore(restore);
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
        if (!restoreCommitted)
        {
            _pendingRestoreCommits.Add(restore);
            _sawmill.Error(
                $"Ship restore {shipId} became durable before its station could be initialized; finalization was queued for retry.");
        }
        return new(true, complete.Status, active.RegistryRevision, active.LeaseId, grid, null);
    }

    private void RetryPendingRestoreCommits()
    {
        if (_pendingRestoreCommits.Count == 0)
            return;

        foreach (var restore in new List<LuaMShipRestoreScope>(_pendingRestoreCommits))
        {
            if (!Exists(restore.Grid) || TerminatingOrDeleted(restore.Grid))
            {
                _pendingRestoreCommits.Remove(restore);
                _sawmill.Warning(
                    $"Stopped retrying station finalization for ship {restore.ShipId} because its active grid no longer exists.");
                continue;
            }

            try
            {
                if (!_runtime.CommitRestore(restore))
                    continue;

                _pendingRestoreCommits.Remove(restore);
                _sawmill.Info($"Deferred station finalization for ship {restore.ShipId} completed successfully.");
            }
            catch (Exception exception)
            {
                _sawmill.Error(
                    $"Deferred station finalization for ship {restore.ShipId} is still failing: {exception}");
            }
        }
    }

    private bool TryFinalizePendingRestoreCommit(Guid shipId)
    {
        LuaMShipRestoreScope? pending = null;
        foreach (var restore in _pendingRestoreCommits)
        {
            if (restore.ShipId == shipId)
            {
                pending = restore;
                break;
            }
        }

        if (pending == null)
            return true;
        if (!_runtime.CommitRestore(pending))
            return false;

        _pendingRestoreCommits.Remove(pending);
        return true;
    }

    private void ForgetPendingRestoreCommit(Guid shipId)
    {
        _pendingRestoreCommits.RemoveWhere(restore => restore.ShipId == shipId);
    }

    private string? TryRollbackRestore(LuaMShipRestoreScope restore)
    {
        try
        {
            if (!_runtime.RollbackRestore(restore))
            {
                _pendingRestoreRollbacks.Remove(restore);
                return "restore scope was already finalized";
            }

            _pendingRestoreRollbacks.Remove(restore);
            return null;
        }
        catch (Exception exception)
        {
            // RollbackRestore keeps ownership in the scope until cleanup has
            // completed, so retain the scope and retry without preventing the
            // durable abort/release compensation from running now.
            _pendingRestoreRollbacks.Add(restore);
            _sawmill.Error(
                $"Ship restore rollback cleanup failed and was queued for retry: {exception}");
            return exception.GetType().Name;
        }
    }

    private void RetryPendingRestoreRollbacks()
    {
        if (_pendingRestoreRollbacks.Count == 0)
            return;

        foreach (var restore in new List<LuaMShipRestoreScope>(_pendingRestoreRollbacks))
        {
            try
            {
                if (_runtime.RollbackRestore(restore))
                {
                    _pendingRestoreRollbacks.Remove(restore);
                    _sawmill.Info("A deferred ship restore rollback completed successfully.");
                    continue;
                }

                _pendingRestoreRollbacks.Remove(restore);
                _sawmill.Warning("A deferred ship restore rollback scope was already finalized.");
            }
            catch (Exception exception)
            {
                _sawmill.Error($"Deferred ship restore rollback cleanup is still failing: {exception}");
            }
        }
    }

    private static string WithRollbackFailure(string reason, string? rollbackFailure)
    {
        return rollbackFailure == null
            ? reason
            : $"{reason}; local cleanup pending after {rollbackFailure}";
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
        => await RunLifecycleMutationAsync(() => RenewCoreAsync(shipId, nowUtc, cancel));

    private async Task<LuaMShipOrchestrationResult> RenewCoreAsync(
        Guid shipId,
        DateTime nowUtc,
        CancellationToken cancel)
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
        => await RunLifecycleMutationAsync(
            () => StoreAndDeactivateCoreAsync(shipId, roundId, nowUtc, cancel));

    private async Task<LuaMShipOrchestrationResult> StoreAndDeactivateCoreAsync(
        Guid shipId,
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel)
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

        if (!TryFinalizePendingRestoreCommit(shipId))
        {
            return Failure(
                LuaMShipPersistenceWriteStatus.InvalidState,
                "restored vessel station finalization is still pending");
        }

        if (active.PayloadRevision == long.MaxValue)
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, "snapshot revision exhausted");
        if (!_runtime.TryCaptureSnapshot(active.Grid, active.PayloadRevision + 1, out var snapshot, out var reason))
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, reason);
        if (!TryCreateStoreRequest(
                snapshot,
                active.OwnerUserId,
                active.Metadata with { SourceRoundId = roundId },
                active.RegistryRevision,
                active.LeaseId,
                nowUtc,
                out var storeRequest,
                out reason))
        {
            return Failure(LuaMShipPersistenceWriteStatus.InvalidRequest, reason);
        }

        var stored = await _database.StoreLuaMShipSnapshotAsync(
            storeRequest,
            cancel);
        if (!stored.Success)
            return Failure(stored.Status, $"snapshot store failed: {stored.Status}");
        QueueSavedShipAnalysis(snapshot);

        _active.Remove(shipId);
        ForgetPendingRestoreCommit(shipId);
        return new(true, stored.Status, stored.Revision, null, active.Grid, null);
    }

    /// <summary>
    /// Last-resort administrator operation for one exact active ship. It will
    /// never snapshot or delete a grid while any MobState entity is aboard.
    /// </summary>
    public async Task<LuaMShipOrchestrationResult> EmergencyStoreAndDeleteActiveShipAsync(
        Guid shipId,
        string requestedBy,
        DateTime nowUtc,
        CancellationToken cancel = default)
        => await RunLifecycleMutationAsync(
            () => EmergencyStoreAndDeleteActiveShipCoreAsync(shipId, requestedBy, nowUtc, cancel));

    private async Task<LuaMShipOrchestrationResult> EmergencyStoreAndDeleteActiveShipCoreAsync(
        Guid shipId,
        string requestedBy,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        if (!_active.TryGetValue(shipId, out var active))
            return Failure(LuaMShipPersistenceWriteStatus.NotFound, "active ship lease not found");
        if (!Exists(active.Grid) || TerminatingOrDeleted(active.Grid))
        {
            return Failure(
                LuaMShipPersistenceWriteStatus.InvalidState,
                "active ship grid does not exist");
        }

        var mobCount = CountMobStateEntities(active.Grid);
        if (mobCount > 0)
        {
            _sawmill.Warning(
                $"Emergency save of persistent ship {shipId} requested by {requestedBy} was refused: " +
                $"{mobCount} MobState entities are aboard.");
            return Failure(
                LuaMShipPersistenceWriteStatus.InvalidState,
                $"refusing emergency save while {mobCount} MobState entities are aboard");
        }

        _sawmill.Warning(
            $"Emergency save of persistent ship {shipId} was confirmed by {requestedBy}.");
        var stored = await StoreAndDeactivateCoreAsync(
            shipId,
            _gameTicker.RoundId,
            nowUtc,
            cancel);
        if (!stored.Success)
            return stored;

        if (Exists(active.Grid) && !TerminatingOrDeleted(active.Grid))
            QueueDel(active.Grid);

        _sawmill.Info(
            $"Emergency save of persistent ship {shipId} completed; grid deletion was queued.");
        return stored;
    }

    private int CountMobStateEntities(EntityUid root)
    {
        var count = 0;
        var visited = new HashSet<EntityUid>();
        var pending = new Stack<EntityUid>();
        pending.Push(root);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) || !Exists(current))
                continue;

            if (HasComp<MobStateComponent>(current))
                count++;

            if (!TryComp<TransformComponent>(current, out var transform))
                continue;

            var children = transform.ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        return count;
    }

    public async Task<LuaMShipOrchestrationResult> RetireAsync(
        Guid shipId,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel = default)
        => await RunLifecycleMutationAsync(() => RetireCoreAsync(shipId, reason, nowUtc, cancel));

    private async Task<LuaMShipOrchestrationResult> RetireCoreAsync(
        Guid shipId,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel)
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
        ForgetPendingRestoreCommit(shipId);
        return new(true, retired.Status, retired.Revision, null, active.Grid, null);
    }

    /// <summary>
    /// Freezes persistent-ship lifecycle changes for the remainder of this
    /// process and captures every active ship before planned maintenance.
    /// The receipt deliberately contains no ship, owner, payload, or lease
    /// identifiers.
    /// </summary>
    public async Task<LuaMShipMaintenanceSaveReceipt> SaveActiveShipsForMaintenanceAsync(
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel = default)
    {
        if (!TryBeginMaintenanceSave(out var lifecycleMutationsDrained))
        {
            return new(
                SchemaVersion: LuaMShipMaintenanceSaveReceipt.CurrentSchemaVersion,
                Ok: false,
                Busy: true,
                BarrierId: Guid.Empty,
                CreatedAtUtc: nowUtc,
                Attempted: 0,
                Saved: 0,
                Failed: 0,
                ActiveRemaining: _active.Count,
                Frozen: MaintenanceFrozen);
        }

        var barrierId = Guid.NewGuid();
        var attempted = 0;
        var saved = 0;
        var failed = 0;

        try
        {
            try
            {
                await lifecycleMutationsDrained.WaitAsync(cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return new(
                    LuaMShipMaintenanceSaveReceipt.CurrentSchemaVersion,
                    false,
                    false,
                    barrierId,
                    nowUtc,
                    attempted,
                    saved,
                    failed,
                    _active.Count,
                    true);
            }

            var activeShips = new List<Guid>(_active.Keys);
            attempted = activeShips.Count;
            for (var index = 0; index < activeShips.Count; index++)
            {
                LuaMShipOrchestrationResult result;
                try
                {
                    result = await StoreAndDeactivateCoreAsync(
                        activeShips[index],
                        roundId,
                        nowUtc,
                        cancel);
                }
                catch (Exception exception)
                {
                    failed++;
                    _sawmill.Error(
                        $"Maintenance ship-save barrier {barrierId} item {index + 1} failed with " +
                        $"{exception.GetType().Name}.");
                    continue;
                }

                if (result.Success)
                {
                    saved++;
                    continue;
                }

                failed++;
                _sawmill.Error(
                    $"Maintenance ship-save barrier {barrierId} item {index + 1} failed with status " +
                    $"{result.Status}.");
            }

            var activeRemaining = _active.Count;
            return new(
                LuaMShipMaintenanceSaveReceipt.CurrentSchemaVersion,
                failed == 0 && activeRemaining == 0,
                false,
                barrierId,
                nowUtc,
                attempted,
                saved,
                failed,
                activeRemaining,
                true);
        }
        finally
        {
            EndMaintenanceSave();
        }
    }

    public async Task SaveAllActiveShipsAsync(
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel = default)
        => await RunLifecycleMutationAsync(
            () => SaveAllActiveShipsCoreAsync(roundId, nowUtc, logFailures: true, cancel));

    private async Task SaveAllActiveShipsCoreAsync(
        int roundId,
        DateTime nowUtc,
        bool logFailures,
        CancellationToken cancel)
    {
        foreach (var shipId in new List<Guid>(_active.Keys))
        {
            var result = await StoreAndDeactivateCoreAsync(shipId, roundId, nowUtc, cancel);
            if (!result.Success && logFailures)
                _sawmill.Error($"Failed to save active ship {shipId}: {result.Reason ?? result.Status.ToString()}");
        }
    }

    public async Task FinalizeRoundCleanupAsync(
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel = default)
        => await RunLifecycleMutationAsync(
            () => FinalizeRoundCleanupCoreAsync(roundId, nowUtc, cancel));

    private async Task FinalizeRoundCleanupCoreAsync(
        int roundId,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        await SaveAllActiveShipsCoreAsync(roundId, nowUtc, logFailures: false, cancel);

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
        => await RunLifecycleMutationAsync(() => MaintainLeasesCoreAsync(nowUtc, cancel));

    private async Task MaintainLeasesCoreAsync(DateTime nowUtc, CancellationToken cancel)
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
        StartLeaseMaintenance();
    }

    private void StartLeaseMaintenance()
    {
        if (_leaseMaintenanceRunning)
            return;

        RunLeaseMaintenanceAsync();
    }

    private async void RunLeaseMaintenanceAsync()
    {
        _leaseMaintenanceRunning = true;
        try
        {
            await MaintainLeasesAsync(DateTime.UtcNow);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Asynchronous ship lease maintenance failed: {e}");
        }
        finally
        {
            _leaseMaintenanceRunning = false;
        }
    }

    private async Task<bool> RenewAllActiveShipsAsync(DateTime nowUtc, CancellationToken cancel)
    {
        var allSafe = true;
        foreach (var shipId in new List<Guid>(_active.Keys))
        {
            var result = await RenewCoreAsync(shipId, nowUtc, cancel);
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

    private async Task<bool> QuarantineClaimedSnapshotAsync(
        LuaMShipSnapshotRecord stored,
        long claimedRevision,
        Guid leaseId,
        NetUserId ownerUserId,
        string reason,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        var quarantined = await _database.QuarantineLuaMShipSnapshotAsync(new(
            stored.ShipId,
            ownerUserId,
            claimedRevision,
            leaseId,
            LimitReason(reason),
            nowUtc), cancel);
        if (!quarantined.Success &&
            quarantined.SnapshotStatus != DbLuaMShipSnapshotStatus.Quarantined)
        {
            _sawmill.Error(
                $"Could not quarantine invalid persistent ship snapshot {stored.ShipId}: " +
                $"{quarantined.Status}; {reason}");
            return false;
        }

        return true;
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
        ForgetPendingRestoreCommit(active.ShipId);
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

    private static bool TryCreateStoreRequest(
        LuaMFullShipSnapshot snapshot,
        NetUserId ownerUserId,
        LuaMShipSnapshotMetadata metadata,
        long? expectedRevision,
        Guid? leaseId,
        DateTime nowUtc,
        out LuaMShipSnapshotStoreRequest request,
        out string reason)
    {
        request = default!;
        reason = string.Empty;
        if (snapshot.Payload == null ||
            snapshot.PayloadSizeBytes <= 0 ||
            snapshot.PayloadSizeBytes != snapshot.Payload.Length ||
            snapshot.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes ||
            snapshot.EntityCount <= 0 ||
            snapshot.EntityCount > LuaMShipPersistenceLimits.MaxEntityCount)
        {
            reason = "snapshot exceeds durable envelope limits";
            return false;
        }

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            reason = $"snapshot envelope serialization failed: {exception.GetType().Name}";
            return false;
        }

        if (payload.Length == 0 || payload.Length > LuaMShipPersistenceLimits.MaxPayloadBytes)
        {
            reason = "snapshot durable envelope size limit exceeded";
            return false;
        }

        request = new(
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
        return true;
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

    /// <summary>
    /// Proves that a quarantined payload still restores, then repairs its stale
    /// entity-count/prototype-manifest metadata and clears the quarantine. The
    /// durable payload bytes are never changed; the loaded graph is rolled back
    /// after validation and the ship is left Stored for the normal claim path.
    /// </summary>
    private async Task<LuaMShipPersistenceWriteResult> TryRepairQuarantinedSnapshotAsync(
        LuaMShipSnapshotRecord stored,
        DateTime nowUtc,
        CancellationToken cancel)
    {
        if (!TryDecode(stored, out var snapshot, out var decodeReason))
        {
            _sawmill.Warning(
                $"Could not repair quarantined ship snapshot {stored.ShipId:N}: {decodeReason}");
            return new(LuaMShipPersistenceWriteStatus.InvalidRequest);
        }

        MapId repairMap = default;
        var mapCreated = false;
        try
        {
            _maps.CreateMap(out repairMap);
            mapCreated = true;

            if (!_runtime.TryBeginRestoreSnapshot(
                    snapshot,
                    repairMap,
                    out var restore,
                    out var restoreReason,
                    acceptDriftedManifest: true))
            {
                _sawmill.Warning(
                    $"Could not repair quarantined ship snapshot {stored.ShipId:N}: {restoreReason}");
                return new(LuaMShipPersistenceWriteStatus.InvalidRequest);
            }

            var repairedEntityCount = restore.RepairedEntityCount ?? stored.EntityCount;
            var repairedManifestHash =
                restore.RepairedPrototypeManifestHash ?? stored.PrototypeManifestHash;
            _runtime.RollbackRestore(restore);

            var reason = restore.RepairedEntityCount is { } repairedCount
                ? $"auto-repaired manifest after successful restore validation ({stored.EntityCount}->{repairedCount} entities)"
                : "auto-repaired quarantine after successful restore validation";
            return await _database.RepairQuarantinedLuaMShipSnapshotAsync(new(
                stored.ShipId,
                stored.OwnerUserId,
                stored.Revision,
                repairedEntityCount,
                repairedManifestHash,
                reason,
                nowUtc), cancel);
        }
        finally
        {
            if (mapCreated && _maps.MapExists(repairMap))
                _maps.DeleteMap(repairMap);
        }
    }

    private static bool TryDecode(LuaMShipSnapshotRecord stored, out LuaMFullShipSnapshot snapshot, out string reason)
    {
        snapshot = default!;
        reason = string.Empty;
        if (stored.Payload == null ||
            stored.PayloadSizeBytes <= 0 ||
            stored.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxPayloadBytes ||
            stored.PayloadSizeBytes != stored.Payload.Length ||
            stored.EntityCount <= 0 ||
            stored.EntityCount > LuaMShipPersistenceLimits.MaxEntityCount)
        {
            reason = "database snapshot envelope exceeds metadata limits";
            return false;
        }

        if (!LuaMFullShipPersistenceSystem.IsSupportedSnapshotFormatVersion(stored.SchemaVersion) ||
            !LuaMFullShipPersistenceSystem.IsSupportedSnapshotFormatVersion(stored.FormatVersion))
        {
            reason = $"unsupported database snapshot format {stored.SchemaVersion}/{stored.FormatVersion}";
            return false;
        }

        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(stored.Payload)),
                stored.PayloadHash,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "database snapshot payload hash mismatch";
            return false;
        }

        try
        {
            snapshot = JsonSerializer.Deserialize<LuaMFullShipSnapshot>(stored.Payload)!;
            if (snapshot == null ||
                snapshot.ShipId != stored.ShipId ||
                snapshot.Revision <= 0 ||
                (stored.PayloadRevision is { } payloadRevision && snapshot.Revision != payloadRevision) ||
                stored.SchemaVersion != snapshot.FormatVersion ||
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

public sealed record LuaMActiveShipDiagnostic(
    Guid ShipId,
    NetUserId OwnerUserId,
    string ShipName,
    string VesselPrototypeId,
    EntityUid Grid,
    bool GridExists,
    int MobStateEntityCount,
    long RegistryRevision,
    long PayloadRevision,
    long? DurablePayloadRevision,
    Guid LeaseId,
    long LeaseRevision);

public sealed record LuaMShipOrchestrationResult(
    bool Success,
    LuaMShipPersistenceWriteStatus Status,
    long? Revision,
    Guid? LeaseId,
    EntityUid? Grid,
    string? Reason);

public sealed record LuaMShipMaintenanceSaveReceipt(
    int SchemaVersion,
    bool Ok,
    bool Busy,
    Guid BarrierId,
    DateTime CreatedAtUtc,
    int Attempted,
    int Saved,
    int Failed,
    int ActiveRemaining,
    bool Frozen)
{
    public const int CurrentSchemaVersion = 1;
}
