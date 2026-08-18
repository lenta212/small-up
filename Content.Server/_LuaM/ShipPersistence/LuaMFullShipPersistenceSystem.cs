using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Database;
using Content.Server._Crescent.ShipShields;
using Content.Server._NF.CryoSleep;
using Content.Server._NF.Station.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Salvage;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.SmartFridge;
using Content.Server.Station.Systems;
using Content.Server.Mind;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.Maps;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Station;
using Content.Shared.Station.Components;
using Content.Shared.Timing;
using Content.Shared.Power.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Owns every entity created by one successful ship deserialization until the
/// database restore transition is either committed or rolled back.
/// </summary>
public sealed class LuaMShipRestoreScope
{
    private HashSet<EntityUid>? _createdEntities;

    public Guid ShipId { get; }
    public EntityUid Grid { get; }
    public int? RepairedEntityCount { get; }
    public string? RepairedPrototypeManifestHash { get; }
    internal ProtoId<VesselPrototype>? VesselId { get; }
    internal StationConfig? VesselStationConfig { get; }

    internal LuaMShipRestoreScope(
        Guid shipId,
        EntityUid grid,
        HashSet<EntityUid> createdEntities,
        int? repairedEntityCount,
        string? repairedPrototypeManifestHash,
        ProtoId<VesselPrototype>? vesselId,
        StationConfig? vesselStationConfig)
    {
        ShipId = shipId;
        Grid = grid;
        _createdEntities = createdEntities;
        RepairedEntityCount = repairedEntityCount;
        RepairedPrototypeManifestHash = repairedPrototypeManifestHash;
        VesselId = vesselId;
        VesselStationConfig = vesselStationConfig;
    }

    internal bool TryTakeCreatedEntities(out IReadOnlySet<EntityUid> createdEntities)
    {
        if (_createdEntities == null)
        {
            createdEntities = default!;
            return false;
        }

        createdEntities = _createdEntities;
        _createdEntities = null;
        return true;
    }

    internal bool TryGetCreatedEntities(out IReadOnlySet<EntityUid> createdEntities)
    {
        if (_createdEntities == null)
        {
            createdEntities = default!;
            return false;
        }

        createdEntities = _createdEntities;
        return true;
    }
}

/// <summary>
/// Round-local evidence that an entity has been controlled by a player.
/// Actor and mind components disappear on detach, while this marker remains
/// until the body itself is deleted.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMPlayerControlledBodyComponent : Component
{
}

/// <summary>
/// Runtime core for complete ship-grid snapshots.
/// </summary>
/// <remarks>
/// This system serializes the complete ship-owned transform graph while
/// excluding player-controlled bodies and runtime ownership entities.
/// References to live entities outside the ship, such as its online owner, are
/// deliberately not pulled into the snapshot. Database ownership, leases, and
/// atomic activation are layered above this API.
/// </remarks>
public sealed class LuaMFullShipPersistenceSystem : EntitySystem
{
    public const int LegacySnapshotFormatVersion = 1;
    public const int SnapshotFormatVersion = 2;
    public const int MaxBuildMetadataLength = 128;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed class SnapshotPayloadLimitExceededException : IOException
    {
    }

    /// <summary>
    /// Stops the serializer before it can build an unbounded in-memory YAML
    /// document. UTF-8 accounting is deliberately conservative when a surrogate
    /// pair is split across writes: an early rejection is safe, an undercount is
    /// not.
    /// </summary>
    private sealed class Utf8SizeLimitedTextWriter(int maxBytes) : TextWriter
    {
        private readonly StringBuilder _builder = new();
        private int _bytesWritten;

        public override Encoding Encoding => StrictUtf8;
        public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;

        public override void Write(char value)
        {
            Span<char> buffer = stackalloc char[1];
            buffer[0] = value;
            Append(buffer);
        }

        public override void Write(string? value)
        {
            if (value != null)
                Append(value.AsSpan());
        }

        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Append(buffer.AsSpan(index, count));
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            Append(buffer);
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private void Append(ReadOnlySpan<char> value)
        {
            var addedBytes = System.Text.Encoding.UTF8.GetByteCount(value);
            if (addedBytes > maxBytes - _bytesWritten)
                throw new SnapshotPayloadLimitExceededException();

            _bytesWritten += addedBytes;
            _builder.Append(value);
        }
    }

    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private ShuttleConsoleLockSystem _consoleLocks = default!;
    [Dependency] private MindSystem _minds = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private GunSystem _gun = default!;
    [Dependency] private SalvageSystem _salvage = default!;
    [Dependency] private ShipShieldsSystem _shipShields = default!;
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private BatterySystem _battery = default!;
    [Dependency] private PowerChargeSystem _powerCharge = default!;
    [Dependency] private SmartFridgeSystem _smartFridges = default!;

    private readonly HashSet<Guid> _busyShips = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
    }

    private void OnPlayerAttached(PlayerAttachedEvent args)
    {
        if (Exists(args.Entity) &&
            !HasComp<MapComponent>(args.Entity) &&
            !HasComp<MapGridComponent>(args.Entity))
        {
            EnsureComp<LuaMPlayerControlledBodyComponent>(args.Entity);
        }
    }

    /// <summary>
    /// Returns the stable identity of a grid, assigning a collision-checked UUID
    /// when it does not have one yet.
    /// </summary>
    public Guid GetOrAssignShipId(EntityUid grid, LuaMShipIdentityComponent? identity = null)
    {
        if (!Exists(grid) || !HasComp<MapGridComponent>(grid) || HasComp<MapComponent>(grid))
            throw new ArgumentException("Ship identity can only be assigned to a standalone map grid.", nameof(grid));

        identity ??= EnsureComp<LuaMShipIdentityComponent>(grid);
        if (identity.ShipId != Guid.Empty)
            return identity.ShipId;

        Guid shipId;
        do
        {
            shipId = Guid.NewGuid();
        } while (TryFindActiveShip(shipId, null, out _));

        identity.ShipId = shipId;
        return shipId;
    }

    /// <summary>
    /// Advances the live grid identity after the matching snapshot has been
    /// durably committed and activated by the registry.
    /// </summary>
    public bool TryCommitSnapshotRevision(EntityUid grid, LuaMFullShipSnapshot snapshot)
    {
        if (!Exists(grid) ||
            !TryComp<LuaMShipIdentityComponent>(grid, out var identity) ||
            identity.ShipId != snapshot.ShipId ||
            snapshot.Revision <= identity.SnapshotRevision)
        {
            return false;
        }

        identity.SnapshotRevision = snapshot.Revision;
        return true;
    }

    /// <summary>
    /// Captures a complete, in-memory UTF-8 YAML snapshot of a ship grid.
    /// </summary>
    public bool TryCaptureSnapshot(
        EntityUid grid,
        long revision,
        out LuaMFullShipSnapshot snapshot,
        out string reason)
    {
        snapshot = default!;
        reason = string.Empty;

        if (!Exists(grid) || !HasComp<MapGridComponent>(grid) || HasComp<MapComponent>(grid))
        {
            reason = "entity-is-not-a-standalone-grid";
            return false;
        }

        if (revision <= 0)
        {
            reason = "snapshot-revision-invalid";
            return false;
        }

        var identity = EnsureComp<LuaMShipIdentityComponent>(grid);
        var shipId = GetOrAssignShipId(grid, identity);
        if (revision <= identity.SnapshotRevision)
        {
            reason = "snapshot-revision-not-newer";
            return false;
        }

        if (TryFindActiveShip(shipId, grid, out _))
        {
            reason = "duplicate-active-ship-identity";
            return false;
        }

        if (!_busyShips.Add(shipId))
        {
            reason = "ship-persistence-operation-in-progress";
            return false;
        }

        var previousRevision = identity.SnapshotRevision;
        var externalDockPairs = new List<(EntityUid ShipDock, EntityUid ExternalDock)>();
        var requiredEntities = new HashSet<EntityUid>();
        var changedPrototypes = new Dictionary<EntityPrototype, bool>();
        var rejectedRequiredEntities = new HashSet<EntityUid>();

        void ObserveSerialization(Entity<MetaDataComponent> entity, ref bool serializable)
        {
            // Bodies and everything carried by them belong to the character, not
            // to the shuttle. They must never become part of a ship snapshot.
            if (IsPlayerBody(entity.Owner))
            {
                serializable = false;
                return;
            }

            if (!serializable && requiredEntities.Contains(entity.Owner))
                rejectedRequiredEntities.Add(entity.Owner);
        }

        try
        {
            try
            {
                if (!_consoleLocks.TryBindPersistentShipSecurity(grid, shipId, out var bindingReason))
                {
                    reason = $"ship-security-binding-failed:{bindingReason}";
                    return false;
                }

                CaptureSalvageExpeditionCooldown(grid);

                // A dock weld and DockedWith both point at the station grid. Save
                // the vessel in its portable, undocked state, then restore the
                // live connection synchronously before returning to the caller.
                externalDockPairs = CollectExternalGridConnections(grid);
                UndockExternalGridConnections(externalDockPairs);

                requiredEntities = CollectTransformGraph(grid);
                if (requiredEntities.Count == 0 ||
                    requiredEntities.Count > LuaMShipPersistenceLimits.MaxEntityCount)
                {
                    reason = "snapshot-entity-count-limit-exceeded";
                    return false;
                }

                changedPrototypes = EnableCompleteGraphSerialization(requiredEntities);
                _mapLoader.OnIsSerializable += ObserveSerialization;

                identity.SnapshotRevision = revision;

                using var writer = new Utf8SizeLimitedTextWriter(
                    LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes);
                var options = SerializationOptions.Default with
                {
                    // A portable ship owns exactly its transform graph. Runtime
                    // station roots, minds, network helpers, and other null-space
                    // entities remain owned by the current round and must never be
                    // pulled into a durable hull snapshot.
                    MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                    EntityExceptionBehaviour = EntityExceptionBehaviour.Rethrow,
                    ErrorOnOrphan = true,
                    LogAutoInclude = null,
                };

                if (!_mapLoader.TrySaveGrid(grid, writer, options))
                {
                    reason = "grid-serialization-failed";
                    return false;
                }

                if (rejectedRequiredEntities.Count != 0)
                {
                    reason = "grid-serialization-rejected-required-entity";
                    return false;
                }

                if (!TryInspectAndSanitizeSerializedShipYaml(
                        writer.ToString(),
                        out var yaml,
                        out var entityCount,
                        out var prototypeCounts,
                        out var prototypeManifestHash,
                        out var sanitizationReason))
                {
                    reason = $"snapshot-reference-sanitization-failed:{sanitizationReason}";
                    return false;
                }

                byte[] payload;
                try
                {
                    payload = StrictUtf8.GetBytes(yaml);
                }
                catch (EncoderFallbackException)
                {
                    reason = "snapshot-is-not-valid-utf8";
                    return false;
                }

                if (payload.Length == 0 ||
                    payload.Length > LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes)
                {
                    reason = "snapshot-payload-size-limit-exceeded";
                    return false;
                }

                if (!ContainsRequiredGraph(requiredEntities, prototypeCounts, out reason))
                {
                    return false;
                }

                var buildVersion = NormalizeBuildMetadata(_configuration.GetCVar(CVars.BuildVersion));
                var buildHash = NormalizeBuildMetadata(_configuration.GetCVar(CVars.BuildHash));
                snapshot = new LuaMFullShipSnapshot(
                    SnapshotFormatVersion,
                    shipId,
                    revision,
                    DateTime.UtcNow,
                    payload,
                    payload.Length,
                    ComputeHash(payload),
                    entityCount,
                    prototypeManifestHash,
                    buildVersion,
                    buildHash);
                return true;
            }
            finally
            {
                _mapLoader.OnIsSerializable -= ObserveSerialization;
                RestorePrototypeSaveFlags(changedPrototypes);
                // Capturing a payload is not a durable commit. Keep the live grid on
                // its previous revision so a failed DB/CAS operation can retry the
                // same registry-derived revision without memory/DB divergence.
                if (Exists(grid) && TryComp<LuaMShipIdentityComponent>(grid, out var liveIdentity))
                {
                    liveIdentity.SnapshotRevision = previousRevision;
                }

                if (!TryRestoreExternalGridConnections(externalDockPairs, out var restoreReason))
                {
                    reason = $"ship-docking-restore-failed:{restoreReason}";
                    throw new InvalidOperationException(reason);
                }
            }
        }
        catch (SnapshotPayloadLimitExceededException)
        {
            reason = "snapshot-payload-size-limit-exceeded";
            return false;
        }
        catch (Exception exception)
        {
            Log.Error($"Ship snapshot capture failed for {ToPrettyString(grid)}: {exception}");
            if (!reason.StartsWith("ship-docking-restore-failed:", StringComparison.Ordinal))
                reason = $"snapshot-capture-exception:{exception.GetType().Name}";
            return false;
        }
        finally
        {
            _busyShips.Remove(shipId);
        }
    }

    private List<(EntityUid ShipDock, EntityUid ExternalDock)> CollectExternalGridConnections(EntityUid grid)
    {
        var result = new List<(EntityUid ShipDock, EntityUid ExternalDock)>();
        var seenExternalDocks = new HashSet<EntityUid>();
        foreach (var dock in _docking.GetDocks(grid))
        {
            if (dock.Comp.DockedWith is not { Valid: true } otherDock)
                continue;

            if (!TryComp<TransformComponent>(otherDock, out var otherTransform) ||
                !TryComp<DockingComponent>(otherDock, out var otherDockComponent))
            {
                throw new InvalidOperationException($"Dock {dock.Owner} points to missing dock {otherDock}.");
            }

            if (otherTransform.GridUid == grid)
                continue;

            if (otherDockComponent.DockedWith != dock.Owner || !seenExternalDocks.Add(otherDock))
            {
                throw new InvalidOperationException(
                    $"Dock {dock.Owner} and external dock {otherDock} are not a reciprocal unique pair.");
            }

            result.Add((dock.Owner, otherDock));
        }

        return result;
    }

    private void UndockExternalGridConnections(
        IReadOnlyList<(EntityUid ShipDock, EntityUid ExternalDock)> pairs)
    {
        foreach (var (shipDock, externalDock) in pairs)
        {
            if (!TryComp<DockingComponent>(shipDock, out var shipDockComponent) ||
                !TryComp<DockingComponent>(externalDock, out var externalDockComponent) ||
                shipDockComponent.DockedWith != externalDock ||
                externalDockComponent.DockedWith != shipDock)
            {
                throw new InvalidOperationException(
                    $"Docking pair {shipDock} <-> {externalDock} changed before snapshot isolation.");
            }

            _docking.Undock((shipDock, shipDockComponent));
            if (shipDockComponent.Docked || externalDockComponent.Docked)
            {
                throw new InvalidOperationException(
                    $"Docking pair {shipDock} <-> {externalDock} did not fully detach for snapshot isolation.");
            }
        }
    }

    private bool TryRestoreExternalGridConnections(
        IReadOnlyList<(EntityUid ShipDock, EntityUid ExternalDock)> pairs,
        out string reason)
    {
        reason = string.Empty;
        var toRestore = new List<(Entity<DockingComponent> ShipDock, Entity<DockingComponent> ExternalDock)>();

        // Resolve and validate every pair before mutating any of them. Pairs
        // that remained docked are valid when a prior Undock call failed before
        // touching that pair.
        foreach (var (shipDock, externalDock) in pairs)
        {
            if (!TryComp<DockingComponent>(shipDock, out var shipDockComponent) ||
                !TryComp<DockingComponent>(externalDock, out var externalDockComponent))
            {
                reason = $"missing-dock:{shipDock}:{externalDock}";
                return false;
            }

            if (shipDockComponent.DockedWith == externalDock &&
                externalDockComponent.DockedWith == shipDock)
            {
                continue;
            }

            if (shipDockComponent.Docked || externalDockComponent.Docked)
            {
                reason = $"dock-pair-was-reused:{shipDock}:{externalDock}";
                return false;
            }

            var resolvedPair = (
                ShipDock: new Entity<DockingComponent>(shipDock, shipDockComponent),
                ExternalDock: new Entity<DockingComponent>(externalDock, externalDockComponent));
            if (!_docking.CanDock(resolvedPair.ShipDock, resolvedPair.ExternalDock))
            {
                reason = $"dock-pair-no-longer-aligns:{shipDock}:{externalDock}";
                return false;
            }

            toRestore.Add(resolvedPair);
        }

        Exception? firstException = null;
        foreach (var pair in toRestore)
        {
            try
            {
                _docking.Dock(pair.ShipDock, pair.ExternalDock);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
                Log.Error(
                    $"Could not restore docking pair {pair.ShipDock.Owner} <-> " +
                    $"{pair.ExternalDock.Owner} after ship snapshot capture: {exception}");
            }
        }

        foreach (var (shipDock, externalDock) in pairs)
        {
            if (!TryComp<DockingComponent>(shipDock, out var shipDockComponent) ||
                !TryComp<DockingComponent>(externalDock, out var externalDockComponent) ||
                shipDockComponent.DockedWith != externalDock ||
                externalDockComponent.DockedWith != shipDock)
            {
                reason = firstException == null
                    ? $"dock-pair-not-restored:{shipDock}:{externalDock}"
                    : $"dock-pair-restore-exception:{firstException.GetType().Name}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Restores a validated snapshot onto an existing target map and transfers
    /// ownership of the restored entities directly to the caller.
    /// </summary>
    public bool TryRestoreSnapshot(
        LuaMFullShipSnapshot snapshot,
        MapId targetMap,
        out EntityUid grid,
        out string reason,
        Vector2 offset = default,
        Angle rotation = default)
    {
        grid = EntityUid.Invalid;
        if (!TryBeginRestoreSnapshot(snapshot, targetMap, out var restore, out reason, offset, rotation))
            return false;

        grid = restore.Grid;
        if (CommitRestore(restore))
            return true;

        try
        {
            RollbackRestore(restore);
            reason = "restored-vessel-station-initialization-failed";
        }
        catch (Exception exception)
        {
            Log.Error(
                $"Could not clean direct persistent ship restore {snapshot.ShipId} after station initialization failed: {exception}");
            reason = $"restored-vessel-station-initialization-and-cleanup-failed:{exception.GetType().Name}";
        }

        grid = EntityUid.Invalid;
        return false;
    }

    /// <summary>
    /// Restores a validated snapshot and retains ownership of every created
    /// entity until the returned scope is explicitly committed or rolled back.
    /// </summary>
    /// <remarks>
    /// Every entity created during the synchronous load is tracked. If loading or
    /// post-load validation fails, the entire created set (including auto-included
    /// null-space entities) is deleted before this method returns.
    /// </remarks>
    public bool TryBeginRestoreSnapshot(
        LuaMFullShipSnapshot snapshot,
        MapId targetMap,
        out LuaMShipRestoreScope restore,
        out string reason,
        Vector2 offset = default,
        Angle rotation = default,
        bool acceptDriftedManifest = false)
    {
        restore = default!;
        reason = string.Empty;

        if (!TryValidateSnapshot(snapshot, out var yaml, out reason))
            return false;

        if (!TryPrepareLegacySnapshotForRestore(
                yaml,
                out var restoreYaml,
                out var detachedEntityCount,
                out reason))
        {
            return false;
        }

        if (detachedEntityCount > 0)
        {
            Log.Warning(
                $"Prepared {detachedEntityCount} legacy snapshot support " +
                "entity reference(s) whose transform parent is no longer present.");
        }

        if (!_maps.MapExists(targetMap))
        {
            reason = "target-map-does-not-exist";
            return false;
        }

        if (TryFindActiveShip(snapshot.ShipId, null, out _))
        {
            reason = "ship-already-active";
            return false;
        }

        if (!_busyShips.Add(snapshot.ShipId))
        {
            reason = "ship-persistence-operation-in-progress";
            return false;
        }

        var entitiesBeforeLoad = EntityManager.GetEntities().ToHashSet();
        HashSet<EntityUid>? createdEntities = null;
        try
        {
            using var reader = new StringReader(restoreYaml);
            var options = DeserializationOptions.Default with
            {
                LogInvalidEntities = true,
                LogOrphanedGrids = true,
            };

            if (!_mapLoader.TryLoadGrid(
                    targetMap,
                    reader,
                    $"full-ship:{snapshot.ShipId:N}:r{snapshot.Revision}",
                    out var loaded,
                    options,
                    offset,
                    rotation) ||
                loaded == null)
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                reason = "grid-deserialization-failed";
                return false;
            }

            var restoredGrid = loaded.Value.Owner;
            createdEntities = EntityManager.GetEntities()
                .Where(uid => !entitiesBeforeLoad.Contains(uid))
                .ToHashSet();
            if (!TryComp<LuaMShipIdentityComponent>(restoredGrid, out var identity) ||
                identity.ShipId != snapshot.ShipId ||
                identity.SnapshotRevision != snapshot.Revision)
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                reason = "restored-ship-identity-or-revision-mismatch";
                return false;
            }

            if (TryFindActiveShip(snapshot.ShipId, restoredGrid, out _))
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                reason = "duplicate-active-ship-identity";
                return false;
            }

            if (!createdEntities.Contains(restoredGrid) ||
                !TryInspectEntitySet(
                    createdEntities,
                    out var entityCount,
                    out var prototypeManifestHash,
                    out reason))
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                if (string.IsNullOrEmpty(reason))
                    reason = "restored-entity-graph-or-manifest-mismatch";
                return false;
            }

            var manifestDrifted =
                entityCount != snapshot.EntityCount;
            if (manifestDrifted && !acceptDriftedManifest)
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                reason = "restored-entity-graph-or-manifest-mismatch";
                return false;
            }

            // Defence in depth for a malformed, manually repaired, or legacy
            // snapshot. Historical v1 hulls are accepted only after the same
            // bounded hash/manifest validation, then copied player state is
            // removed before the restored graph is handed to the caller.
            SanitizeRestoredPlayerBodies(createdEntities, restoredGrid);

            // Player minds are runtime ownership, not portable ship content.
            SanitizeRestoredMinds(createdEntities);
            RebuildRestoredSmartStorageIndexes(createdEntities);
            _shipShields.ReconcileRestoredShipShields(restoredGrid, createdEntities);
            createdEntities.RemoveWhere(uid => !Exists(uid));
            ExpireImpossibleRestoredUseDelays(createdEntities);
            ExpireImpossibleRestoredBatteryRechargeDelays(createdEntities);
            SanitizeRestoredPowerState(createdEntities);
            RefreshRestoredGunModifiers(createdEntities);

            if (!TryResolveVesselStationConfig(
                    restoredGrid,
                    out var vesselId,
                    out var vesselStationConfig,
                    out reason))
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                return false;
            }

            if (!_consoleLocks.TryBindPersistentShipSecurity(
                    restoredGrid,
                    snapshot.ShipId,
                    out var bindingReason))
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                reason = $"restored-ship-security-binding-failed:{bindingReason}";
                return false;
            }

            restore = new LuaMShipRestoreScope(
                snapshot.ShipId,
                restoredGrid,
                createdEntities,
                manifestDrifted ? entityCount : null,
                manifestDrifted ? prototypeManifestHash : null,
                vesselId,
                vesselStationConfig);
            return true;
        }
        catch (Exception exception)
        {
            CleanupPartialLoadEntities(entitiesBeforeLoad);
            Log.Error($"Persistent ship {snapshot.ShipId} snapshot restore failed: {exception}");
            reason = $"snapshot-restore-exception:{exception.GetType().Name}";
            return false;
        }
        finally
        {
            _busyShips.Remove(snapshot.ShipId);
        }
    }

    /// <summary>
    /// Expires only cooldown timestamps that cannot have been produced by
    /// <see cref="UseDelaySystem"/> on the current server time base.
    /// </summary>
    /// <remarks>
    /// A park-and-call cycle in one round must retain a genuinely active delay.
    /// A delay started on the current time base cannot have both its start and end
    /// in the future. Checking the start avoids expiring a legitimate active delay
    /// whose configured length was shortened after it began.
    /// </remarks>
    private void ExpireImpossibleRestoredUseDelays(IReadOnlySet<EntityUid> restoredEntities)
    {
        var now = _gameTiming.CurTime;
        var expiredAt = now == TimeSpan.MinValue ? now : now - TimeSpan.FromTicks(1);
        var expiredEntries = 0;

        foreach (var uid in restoredEntities)
        {
            if (!TryComp<UseDelayComponent>(uid, out var component))
                continue;

            var dirty = false;
            foreach (var delay in component.Delays.Values)
            {
                if (delay.EndTime <= now || delay.StartTime <= now)
                    continue;

                delay.StartTime = expiredAt;
                delay.EndTime = expiredAt;
                expiredEntries++;
                dirty = true;
            }

            if (dirty)
                Dirty(uid, component);
        }

        if (expiredEntries > 0)
        {
            Log.Warning(
                $"Expired {expiredEntries} impossible use-delay timestamp(s) while restoring a persistent ship.");
        }
    }

    /// <summary>
    /// Expires self-recharge pauses whose absolute timestamp belongs to an older round time base.
    /// </summary>
    /// <remarks>
    /// Battery self-rechargers store <c>NextAutoRecharge</c> as an absolute timestamp. A legitimate
    /// pause can never end later than <c>CurTime + AutoRechargePauseTime</c>, so only values beyond
    /// that bound are stale. This preserves same-round park-and-call pauses while repairing older
    /// snapshots whose energy weapons would otherwise remain unable to recharge for hours.
    /// </remarks>
    private void ExpireImpossibleRestoredBatteryRechargeDelays(IReadOnlySet<EntityUid> restoredEntities)
    {
        var now = _gameTiming.CurTime;
        var expiredEntries = 0;

        foreach (var uid in restoredEntities)
        {
            if (!TryComp<BatterySelfRechargerComponent>(uid, out var component) ||
                !component.AutoRechargePause)
            {
                continue;
            }

            var pauseSeconds = float.IsFinite(component.AutoRechargePauseTime)
                ? Math.Max(component.AutoRechargePauseTime, 0f)
                : 0f;
            var remainingTimeCapacity = TimeSpan.MaxValue - now;
            var latestValidEnd = pauseSeconds >= remainingTimeCapacity.TotalSeconds
                ? TimeSpan.MaxValue
                : now + TimeSpan.FromSeconds(pauseSeconds);

            if (component.NextAutoRecharge <= latestValidEnd)
                continue;

            component.NextAutoRecharge = now;
            expiredEntries++;
        }

        if (expiredEntries > 0)
        {
            Log.Warning(
                $"Expired {expiredEntries} impossible battery self-recharge timestamp(s) while restoring a persistent ship.");
        }
    }

    private void RebuildRestoredSmartStorageIndexes(IEnumerable<EntityUid> createdEntities)
    {
        foreach (var uid in createdEntities)
        {
            if (TryComp<Content.Shared.SmartFridge.SmartFridgeComponent>(uid, out var smartFridge))
                _smartFridges.RebuildContentsIndex((uid, smartFridge));
        }
    }

    /// <summary>
    /// Removes non-finite runtime values that would otherwise poison an entire restored power net.
    /// </summary>
    /// <remarks>
    /// Valid battery charge is durable and remains untouched. A non-finite charge has no meaningful
    /// recoverable value, so it is reset empty; the normal solver can then recharge it from the
    /// ship's surviving generators and SMES units. Transient solver fields are reset to neutral.
    /// </remarks>
    private void SanitizeRestoredPowerState(IReadOnlySet<EntityUid> restoredEntities)
    {
        var repairedEntities = 0;
        var repairedFields = 0;

        foreach (var uid in restoredEntities)
        {
            var repaired = false;
            float? batteryCharge = null;

            if (TryComp<BatteryComponent>(uid, out var battery))
            {
                if (!float.IsFinite(battery.CurrentCharge))
                {
                    _battery.SetCharge(uid, 0f, battery);
                    repairedFields++;
                    repaired = true;
                }

                batteryCharge = float.IsFinite(battery.CurrentCharge)
                    ? Math.Clamp(battery.CurrentCharge, 0f, Math.Max(battery.MaxCharge, 0f))
                    : 0f;
            }

            if (TryComp<PowerNetworkBatteryComponent>(uid, out var network))
            {
                var state = network.NetworkBattery;
                repaired |= ResetNonFinite(ref state.SupplyRampPosition);
                repaired |= ResetNonFinite(ref state.CurrentSupply);
                repaired |= ResetNonFinite(ref state.CurrentReceiving);
                repaired |= ResetNonFinite(ref state.LoadingNetworkDemand);
                repaired |= ResetNonFinite(ref state.AvailableSupply);
                repaired |= ResetNonFinite(ref state.DesiredPower);
                repaired |= ResetNonFinite(ref state.SupplyRampTarget);
                repaired |= ResetNonFinite(ref state.MaxEffectiveSupply);
                repaired |= ResetNonFinite(ref network.LastSupply);

                if (!float.IsFinite(state.CurrentStorage))
                {
                    state.CurrentStorage = batteryCharge ?? 0f;
                    repairedFields++;
                    repaired = true;
                }
            }

            if (_powerCharge.SanitizeNonFiniteCharge(uid))
            {
                repairedFields++;
                repaired = true;
            }

            if (repaired)
                repairedEntities++;
        }

        if (repairedFields > 0)
        {
            Log.Warning(
                $"Repaired {repairedFields} non-finite power field(s) on {repairedEntities} entity/entities while restoring a persistent ship.");
        }

        bool ResetNonFinite(ref float value)
        {
            if (float.IsFinite(value))
                return false;

            value = 0f;
            repairedFields++;
            return true;
        }
    }

    /// <summary>
    /// Recomputes derived gun values normally initialized by a map-init event.
    /// </summary>
    private void RefreshRestoredGunModifiers(IReadOnlySet<EntityUid> restoredEntities)
    {
        foreach (var uid in restoredEntities)
        {
            if (TryComp<GunComponent>(uid, out var gun))
                _gun.RefreshModifiers((uid, gun));
        }
    }

    /// <summary>
    /// Resolves all prototype data needed to recreate a vessel station without creating runtime entities.
    /// </summary>
    /// <remarks>
    /// A missing VesselComponent is supported for legacy and non-vessel snapshots. Once the component
    /// is present, however, an unknown map prototype or station key is invalid persistent data and must
    /// fail before the database restore transition can be committed.
    /// </remarks>
    private bool TryResolveVesselStationConfig(
        EntityUid restoredGrid,
        out ProtoId<VesselPrototype>? vesselId,
        out StationConfig? stationConfig,
        out string reason)
    {
        vesselId = null;
        stationConfig = null;
        reason = string.Empty;

        if (!TryComp<VesselComponent>(restoredGrid, out var vessel))
            return true;

        if (!_prototypes.TryIndex<GameMapPrototype>(vessel.VesselId.Id, out var gameMap))
        {
            reason = $"restored-vessel-game-map-prototype-not-found:{vessel.VesselId.Id}";
            return false;
        }

        if (!gameMap.Stations.TryGetValue(vessel.VesselId.Id, out stationConfig))
        {
            reason = $"restored-vessel-station-config-not-found:{vessel.VesselId.Id}";
            return false;
        }

        vesselId = vessel.VesselId;
        return true;
    }

    /// <summary>
    /// Recreates the round-local station root after the durable restore transition has committed.
    /// </summary>
    private void RestoreVesselStation(LuaMShipRestoreScope restore)
    {
        if (restore.VesselId is not { } vesselId || restore.VesselStationConfig is not { } stationConfig)
            return;

        if (_stations.GetOwningStation(restore.Grid) is { Valid: true } existingStation)
        {
            if (!TryComp<StationDataComponent>(existingStation, out var existingData) ||
                existingData.Grids.Count != 1 ||
                _stations.GetLargestGrid((existingStation, existingData)) != restore.Grid ||
                !TryComp<ExtraShuttleInformationComponent>(existingStation, out var existingVessel) ||
                existingVessel.Vessel != vesselId)
            {
                throw new InvalidOperationException(
                    "Restored vessel already belongs to an inconsistent round-local station.");
            }

            RestoreSalvageExpeditionCooldown(restore.Grid, existingStation);
            _salvage.RefreshExpeditionConsoles(existingStation);
            return;
        }

        var vesselName = Name(restore.Grid);
        var station = _stations.InitializeNewStation(stationConfig, [restore.Grid], vesselName);
        EnsureComp<ExtraShuttleInformationComponent>(station).Vessel = vesselId;
        RestoreSalvageExpeditionCooldown(restore.Grid, station);
        _salvage.RefreshExpeditionConsoles(station);

        if (_stations.GetOwningStation(restore.Grid) != station)
            throw new InvalidOperationException("Restored vessel station did not own its grid after initialization.");
    }

    private void CaptureSalvageExpeditionCooldown(EntityUid grid)
    {
        var station = _stations.GetOwningStation(grid);
        if (station is { } stationUid &&
            TryComp<SalvageExpeditionDataComponent>(stationUid, out var data) &&
            (data.Cooldown || data.Claimed))
        {
            var remaining = data.NextOffer > _gameTiming.CurTime
                ? data.NextOffer - _gameTiming.CurTime
                : TimeSpan.Zero;
            EnsureComp<LuaMSalvageExpeditionCooldownComponent>(grid).RemainingCooldown = remaining;
            return;
        }

        if (HasComp<LuaMSalvageExpeditionCooldownComponent>(grid))
            RemComp<LuaMSalvageExpeditionCooldownComponent>(grid);
    }

    private void RestoreSalvageExpeditionCooldown(EntityUid grid, EntityUid station)
    {
        if (!TryComp<LuaMSalvageExpeditionCooldownComponent>(grid, out var saved))
        {
            if (TryComp<SalvageExpeditionDataComponent>(station, out var freshData))
            {
                freshData.Cooldown = false;
                freshData.NextOffer = TimeSpan.Zero;
            }

            return;
        }

        var data = EnsureComp<SalvageExpeditionDataComponent>(station);
        var remaining = saved.RemainingCooldown > TimeSpan.Zero
            ? saved.RemainingCooldown
            : TimeSpan.Zero;
        data.Cooldown = true;
        data.NextOffer = remaining > TimeSpan.MaxValue - _gameTiming.CurTime
            ? TimeSpan.MaxValue
            : _gameTiming.CurTime + remaining;
    }

    private void SanitizeRestoredMinds(IReadOnlySet<EntityUid> createdEntities)
    {
        foreach (var uid in createdEntities)
        {
            if (!Exists(uid))
                continue;

            if (TryComp<MindContainerComponent>(uid, out var container) &&
                container.Mind is { } mind)
            {
                if (mind.IsValid() && createdEntities.Contains(mind) && TryComp<MindComponent>(mind, out var mindComp))
                    _minds.TransferTo(mind, null, mind: mindComp, createGhost: false);
                else
                    _minds.ClearMindContainer(uid, container);
            }

            if (HasComp<MindComponent>(uid))
                Del(uid);
        }
    }

    private void SanitizeRestoredPlayerBodies(IReadOnlySet<EntityUid> createdEntities, EntityUid restoredGrid)
    {
        var bodies = createdEntities
            .Where(uid => uid != restoredGrid && Exists(uid) && IsPlayerBody(uid))
            .ToArray();

        foreach (var body in bodies)
        {
            if (!Exists(body))
                continue;

            // Detach a copied mind before deleting its body. Otherwise normal
            // body termination may create a ghost or attach a live session.
            if (TryComp<MindContainerComponent>(body, out var container) &&
                container.Mind is { } mind)
            {
                if (mind.IsValid() &&
                    createdEntities.Contains(mind) &&
                    TryComp<MindComponent>(mind, out var mindComp))
                {
                    _minds.TransferTo(mind, null, mind: mindComp, createGhost: false);
                }
                else
                {
                    _minds.ClearMindContainer(body, container);
                }
            }

            var graph = CollectExistingTransformGraph(body);
            for (var i = graph.Count - 1; i >= 0; i--)
            {
                var uid = graph[i];
                if (Exists(uid))
                    Del(uid);
            }
        }
    }

    /// <summary>
    /// Parses the bounded grid document once, validates its manifest, and cleans
    /// only known entity-reference fields on known components. Arbitrary
    /// component data with fields named <c>containers</c> or
    /// <c>buckledEntities</c> is deliberately left untouched.
    /// </summary>
    internal static bool TryInspectAndSanitizeSerializedShipYaml(
        string yaml,
        out string sanitizedYaml,
        out int entityCount,
        out Dictionary<string, int> prototypeCounts,
        out string prototypeManifestHash,
        out string reason)
    {
        sanitizedYaml = string.Empty;
        entityCount = 0;
        prototypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        prototypeManifestHash = string.Empty;
        reason = string.Empty;

        try
        {
            if (string.IsNullOrEmpty(yaml) ||
                StrictUtf8.GetByteCount(yaml) > LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes)
            {
                reason = "snapshot-payload-size-limit-exceeded";
                return false;
            }

            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode root ||
                !TryGetMapping(root, "meta", out var meta) ||
                !TryGetScalar(meta, "category", out var category) ||
                !string.Equals(category, "Grid", StringComparison.Ordinal) ||
                !TryGetScalar(meta, "entityCount", out var countText) ||
                !int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out entityCount) ||
                entityCount <= 0 ||
                entityCount > LuaMShipPersistenceLimits.MaxEntityCount ||
                !TryGetSequence(root, "entities", out var prototypeGroups))
            {
                reason = "snapshot-yaml-metadata-invalid";
                return false;
            }

            var countedEntities = 0;
            var validUids = new HashSet<string>(StringComparer.Ordinal);
            var changed = false;
            var parsedGroups = new List<(YamlMappingNode Group, YamlSequenceNode Entities)>();
            foreach (var node in prototypeGroups.Children)
            {
                if (node is not YamlMappingNode group ||
                    !TryGetScalar(group, "proto", out var prototype) ||
                    !TryGetSequence(group, "entities", out var entities))
                {
                    reason = "snapshot-yaml-entity-group-invalid";
                    return false;
                }

                parsedGroups.Add((group, entities));
                foreach (var entityNode in entities.Children)
                {
                    if (entityNode is not YamlMappingNode entity)
                    {
                        reason = "snapshot-yaml-entity-invalid";
                        return false;
                    }

                    if (TryGetScalar(entity, "uid", out var uidText))
                        validUids.Add(uidText);

                    countedEntities = checked(countedEntities + 1);
                    if (countedEntities > LuaMShipPersistenceLimits.MaxEntityCount)
                    {
                        reason = "snapshot-entity-count-limit-exceeded";
                        return false;
                    }
                }

                var prototypeKey = string.Equals(prototype, "null", StringComparison.Ordinal)
                    ? string.Empty
                    : prototype;
                prototypeCounts[prototypeKey] = checked(
                    prototypeCounts.GetValueOrDefault(prototypeKey) + entities.Children.Count);
            }

            foreach (var (_, entities) in parsedGroups)
            {
                foreach (var entityNode in entities.Children)
                {
                    changed |= SanitizeEntityComponents((YamlMappingNode) entityNode, validUids);
                }
            }

            if (countedEntities != entityCount)
            {
                reason = "snapshot-yaml-entity-count-mismatch";
                return false;
            }

            prototypeManifestHash = ComputePrototypeManifestHash(prototypeCounts);
            if (!changed)
            {
                sanitizedYaml = yaml;
                return true;
            }

            using var writer = new Utf8SizeLimitedTextWriter(
                LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes);
            // YamlDotNet otherwise treats custom mapping tags as implicit and
            // drops tags such as !type:ContainerSlot. The map loader then tries
            // to instantiate the abstract BaseContainer value from the dictionary.
            stream.Save(new YamlMappingFix(new Emitter(writer)), assignAnchors: false);
            sanitizedYaml = writer.ToString();
            return true;
        }
        catch (SnapshotPayloadLimitExceededException)
        {
            reason = "snapshot-payload-size-limit-exceeded";
            return false;
        }
        catch (EncoderFallbackException)
        {
            reason = "snapshot-is-not-valid-utf8";
            return false;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not StackOverflowException)
        {
            reason = $"snapshot-yaml-parse-failed:{exception.GetType().Name}";
            return false;
        }
    }

    private static bool SanitizeEntityComponents(YamlMappingNode entity, HashSet<string> validUids)
    {
        if (!TryGetSequence(entity, "components", out var components))
            return false;

        var changed = false;
        for (var index = components.Children.Count - 1; index >= 0; index--)
        {
            if (components.Children[index] is not YamlMappingNode component ||
                !TryGetScalar(component, "type", out var componentType))
            {
                continue;
            }

            switch (componentType)
            {
                case "ContainerContainer":
                    if (TryGetMapping(component, "containers", out var containers))
                    {
                        foreach (var container in containers.Children.Values)
                            changed |= SanitizeContainerReferenceNode(container, validUids);
                    }
                    break;
                case "Strap":
                    if (TryGetNode(component, "buckledEntities", out var buckled))
                        changed |= RemoveInvalidReferences(buckled, validUids);
                    break;
                case "FactionException":
                    // Player- or round-scoped faction exceptions become EntityUid
                    // 0 after a portable save. Rehydration then tries to add a
                    // tracker to the invalid entity and aborts the whole load.
                    if (TryGetNode(component, "ignored", out var ignored))
                        changed |= RemoveInvalidReferences(ignored, validUids);
                    if (TryGetNode(component, "hostiles", out var hostiles))
                        changed |= RemoveInvalidReferences(hostiles, validUids);
                    break;
                case "Storage":
                    if (TryGetMapping(component, "storedItems", out var storedItems))
                        changed |= RemoveInvalidMappingKeys(storedItems, validUids);
                    break;
                case "SmartFridge":
                    // These fields are a UI index derived from the physical
                    // smart_fridge_inventory container. NetEntity values cannot
                    // survive a portable map round-trip and previously made a
                    // non-empty smart storage reject the whole ship snapshot.
                    changed |= RemoveMappingField(component, "entries");
                    changed |= RemoveMappingField(component, "containedEntries");
                    break;
                case "MagicMirror":
                    // Handheld barber scissors store the last UI target as a
                    // runtime EntityUid. If the target is a player body or any
                    // other entity outside the portable hull, saving the shuttle
                    // must not fail because of this transient UI pointer.
                    changed |= ReplaceInvalidReferenceWithNull(component, "target", validUids);
                    changed |= RemoveMappingField(component, "doAfter");
                    break;
                case "Puller":
                    changed |= ReplaceInvalidReferenceWithNull(component, "pulling", validUids);
                    break;
                case "Pullable":
                    if (ReplaceInvalidReferenceWithNull(component, "puller", validUids))
                    {
                        changed = true;
                        changed |= ReplaceScalarWithNull(component, "pullJointId");
                    }
                    break;
                case "Joint":
                    changed |= ReplaceInvalidReferenceWithNull(component, "relay", validUids);
                    if (TryGetMapping(component, "joints", out var joints))
                        changed |= RemoveMappingsContainingInvalidReference(joints, validUids);
                    break;
                case "StationMember":
                    if (HasInvalidReference(component, "station", validUids))
                    {
                        // A restored portable hull is assigned to its destination
                        // station by the caller. Keeping an invalid station member
                        // is worse than having no membership.
                        components.Children.RemoveAt(index);
                        changed = true;
                    }
                    break;
                case "StationTracker":
                    changed |= ReplaceInvalidReferenceWithNull(component, "station", validUids);
                    break;
                case "ShipRepairData":
                    // Repair chunks cache original NetEntity references that
                    // cannot survive a portable hull round-trip. Dropping the
                    // cache is safer than failing the whole restore; the repair
                    // system rebuilds chunks on demand.
                    changed |= RemoveMappingField(component, "chunks");
                    break;
                case "ShuttleConsoleJobSlots":
                    // The owning station lives outside the portable hull; the
                    // restore caller assigns the destination station after load.
                    changed |= ReplaceInvalidReferenceWithNull(component, "owningStation", validUids);
                    break;
                case "CloningConsole":
                    changed |= ReplaceInvalidReferenceWithNull(component, "geneticScanner", validUids);
                    changed |= ReplaceInvalidReferenceWithNull(component, "cloningPod", validUids);
                    break;
                case "CloningPod":
                    changed |= ReplaceInvalidReferenceWithNull(component, "connectedConsole", validUids);
                    break;
                case "MedicalScanner":
                    changed |= ReplaceInvalidReferenceWithNull(component, "connectedConsole", validUids);
                    break;
                case "DeviceLinkSource":
                    // LinkedPorts keys are sink EntityUids; stale ones break
                    // buttons and console links after a portable round-trip.
                    if (TryGetMapping(component, "linkedPorts", out var linkedPorts))
                        changed |= RemoveMappingsContainingInvalidReference(linkedPorts, validUids);
                    break;
                case "MaterialStorageMagnetPickup":
                    // Reset the scan timer and force the resource magnet back on
                    // so restored lathes/techfabs keep attracting materials.
                    changed |= RemoveMappingField(component, "nextScan");
                    component.Children[new YamlScalarNode("magnetEnabled")] = new YamlScalarNode("true");
                    changed = true;
                    break;
            }
        }

        return changed;
    }

    private static bool SanitizeContainerReferenceNode(YamlNode node, HashSet<string> validUids)
    {
        var changed = false;

        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (keyNode, valueNode) in mapping.Children.ToArray())
                {
                    if (keyNode is YamlScalarNode key)
                    {
                        if (key.Value == "ent" && IsInvalidReference(valueNode, validUids))
                        {
                            mapping.Children[keyNode] = new YamlScalarNode("null");
                            changed = true;
                            continue;
                        }

                        if (key.Value == "ents")
                            changed |= RemoveInvalidReferences(valueNode, validUids);
                    }

                    changed |= SanitizeContainerReferenceNode(valueNode, validUids);
                }

                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                    changed |= SanitizeContainerReferenceNode(child, validUids);
                break;
        }

        return changed;
    }

    private static bool RemoveMappingField(YamlMappingNode mapping, string key)
    {
        foreach (var keyNode in mapping.Children.Keys)
        {
            if (keyNode is not YamlScalarNode scalar || scalar.Value != key)
                continue;

            return mapping.Children.Remove(keyNode);
        }

        return false;
    }

    private static bool RemoveInvalidReferences(YamlNode node, HashSet<string> validUids)
    {
        if (node is not YamlSequenceNode sequence)
            return false;

        var changed = false;
        for (var i = sequence.Children.Count - 1; i >= 0; i--)
        {
            if (!IsInvalidReference(sequence.Children[i], validUids))
                continue;

            sequence.Children.RemoveAt(i);
            changed = true;
        }

        return changed;
    }

    private static bool IsInvalidReference(YamlNode node, HashSet<string> validUids)
    {
        if (node is not YamlScalarNode scalar)
            return false;

        if (scalar.Value == "invalid")
            return true;

        if (scalar.Value == "null")
            return false;

        return long.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               !validUids.Contains(scalar.Value);
    }

    private static bool HasInvalidReference(YamlMappingNode mapping, string key, HashSet<string> validUids)
        => TryGetNode(mapping, key, out var value) && IsInvalidReference(value, validUids);

    private static bool ReplaceInvalidReferenceWithNull(YamlMappingNode mapping, string key, HashSet<string> validUids)
    {
        if (!TryGetNode(mapping, key, out var value) || !IsInvalidReference(value, validUids))
            return false;

        return ReplaceScalarWithNull(mapping, key);
    }

    private static bool ReplaceScalarWithNull(YamlMappingNode mapping, string key)
    {
        foreach (var keyNode in mapping.Children.Keys)
        {
            if (keyNode is not YamlScalarNode { Value: var candidate } || candidate != key)
                continue;

            mapping.Children[keyNode] = new YamlScalarNode("null");
            return true;
        }

        return false;
    }

    private static bool RemoveInvalidMappingKeys(YamlMappingNode mapping, HashSet<string> validUids)
    {
        var changed = false;
        foreach (var key in mapping.Children.Keys.ToArray())
        {
            if (!IsInvalidReference(key, validUids))
                continue;

            mapping.Children.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static bool RemoveMappingsContainingInvalidReference(YamlMappingNode mapping, HashSet<string> validUids)
    {
        var changed = false;
        foreach (var (key, value) in mapping.Children.ToArray())
        {
            if (!ContainsInvalidReference(value, validUids))
                continue;

            mapping.Children.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static bool ContainsInvalidReference(YamlNode node, HashSet<string> validUids)
    {
        if (IsInvalidReference(node, validUids))
            return true;

        return node switch
        {
            YamlSequenceNode sequence => sequence.Children.Any(child => ContainsInvalidReference(child, validUids)),
            YamlMappingNode mapping => mapping.Children.Any(pair =>
                ContainsInvalidReference(pair.Key, validUids) || ContainsInvalidReference(pair.Value, validUids)),
            _ => false,
        };
    }

    private bool TryValidateSnapshot(
        LuaMFullShipSnapshot? snapshot,
        out string yaml,
        out string reason)
    {
        return TryValidateSnapshot(
            snapshot,
            out yaml,
            out _,
            out _,
            out reason);
    }

    private bool TryValidateSnapshot(
        LuaMFullShipSnapshot? snapshot,
        out string yaml,
        out Dictionary<string, int> prototypeCounts,
        out string prototypeManifestHash,
        out string reason)
    {
        yaml = string.Empty;
        prototypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        prototypeManifestHash = string.Empty;
        reason = string.Empty;

        if (snapshot == null)
        {
            reason = "snapshot-is-null";
            return false;
        }

        if (!IsSupportedSnapshotFormatVersion(snapshot.FormatVersion))
        {
            reason = $"unsupported-snapshot-format-{snapshot.FormatVersion}";
            return false;
        }

        if (snapshot.ShipId == Guid.Empty ||
            snapshot.Revision <= 0 ||
            snapshot.CreatedAtUtc.Kind != DateTimeKind.Utc)
        {
            reason = "snapshot-metadata-invalid";
            return false;
        }

        if (snapshot.Payload == null ||
            snapshot.PayloadSizeBytes <= 0 ||
            snapshot.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes ||
            snapshot.PayloadSizeBytes != snapshot.Payload.Length ||
            snapshot.EntityCount <= 0 ||
            snapshot.EntityCount > LuaMShipPersistenceLimits.MaxEntityCount ||
            string.IsNullOrWhiteSpace(snapshot.SourceBuildVersion) ||
            snapshot.SourceBuildVersion.Length > MaxBuildMetadataLength ||
            string.IsNullOrWhiteSpace(snapshot.SourceBuildHash) ||
            snapshot.SourceBuildHash.Length > MaxBuildMetadataLength)
        {
            reason = "snapshot-metadata-caps-invalid";
            return false;
        }

        var payloadHash = ComputeHash(snapshot.Payload);
        if (!FixedHashEquals(payloadHash, snapshot.PayloadHash))
        {
            reason = "snapshot-payload-hash-mismatch";
            return false;
        }

        try
        {
            yaml = StrictUtf8.GetString(snapshot.Payload);
        }
        catch (DecoderFallbackException)
        {
            reason = "snapshot-is-not-valid-utf8";
            return false;
        }

        var serializedYaml = yaml;
        if (!TryInspectAndSanitizeSerializedShipYaml(
                serializedYaml,
                out yaml,
                out var entityCount,
                out prototypeCounts,
                out prototypeManifestHash,
                out reason) ||
            entityCount != snapshot.EntityCount ||
            !FixedHashEquals(prototypeManifestHash, snapshot.PrototypeManifestHash))
        {
            if (string.IsNullOrEmpty(reason))
                reason = "snapshot-payload-metadata-mismatch";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Older snapshots can contain a referenced support entity after its transform
    /// parent was omitted from the portable graph. The engine resolves that parent
    /// to EntityUid.Invalid, but format-7 metadata still does not classify the
    /// entity as a root and debug builds reject the otherwise recoverable graph.
    /// Preserve the exact entity and prototype manifest while explicitly marking
    /// those detached entities as null-space roots for this restore only.
    /// </summary>
    internal static bool TryPrepareLegacySnapshotForRestore(
        string yaml,
        out string preparedYaml,
        out int detachedEntityCount,
        out string reason)
    {
        preparedYaml = yaml;
        detachedEntityCount = 0;
        reason = string.Empty;

        try
        {
            yaml = NormalizeLegacySnapshotText(yaml);
            preparedYaml = yaml;

            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode root ||
                !TryGetSequence(root, "entities", out var prototypeGroups) ||
                !TryGetSequence(root, "maps", out var maps) ||
                !TryGetSequence(root, "grids", out var grids) ||
                !TryGetSequence(root, "orphans", out var orphans) ||
                !TryGetSequence(root, "nullspace", out var nullspace))
            {
                reason = "snapshot-yaml-root-metadata-invalid";
                return false;
            }

            var entities = new List<(int Uid, YamlMappingNode Node)>();
            var entityIds = new HashSet<int>();
            foreach (var groupNode in prototypeGroups.Children)
            {
                if (groupNode is not YamlMappingNode group ||
                    !TryGetSequence(group, "entities", out var groupEntities))
                {
                    reason = "snapshot-yaml-entity-group-invalid";
                    return false;
                }

                foreach (var entityNode in groupEntities.Children)
                {
                    if (entityNode is not YamlMappingNode entity ||
                        !TryGetScalar(entity, "uid", out var uidText) ||
                        !int.TryParse(
                            uidText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var uid) ||
                        uid <= 0 ||
                        !entityIds.Add(uid))
                    {
                        reason = "snapshot-yaml-entity-uid-invalid";
                        return false;
                    }

                    entities.Add((uid, entity));
                }
            }

            var classifiedRoots = new HashSet<int>();
            var nullspaceIds = new List<int>();
            foreach (var roots in new[] { maps, grids, orphans, nullspace })
            {
                foreach (var rootNode in roots.Children)
                {
                    if (rootNode is not YamlScalarNode { Value: { } rootText } ||
                        !int.TryParse(
                            rootText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var rootUid) ||
                        !entityIds.Contains(rootUid))
                    {
                        reason = "snapshot-yaml-root-uid-invalid";
                        return false;
                    }

                    classifiedRoots.Add(rootUid);
                    if (ReferenceEquals(roots, nullspace))
                        nullspaceIds.Add(rootUid);
                }
            }

            var detachedIds = new List<int>();
            foreach (var (uid, entity) in entities)
            {
                if (classifiedRoots.Contains(uid) ||
                    !TryGetSequence(entity, "components", out var components))
                {
                    continue;
                }

                foreach (var componentNode in components.Children)
                {
                    if (componentNode is not YamlMappingNode component ||
                        !TryGetScalar(component, "type", out var componentType) ||
                        !string.Equals(componentType, "Transform", StringComparison.Ordinal) ||
                        !TryGetScalar(component, "parent", out var parentText))
                    {
                        continue;
                    }

                    var parentMissing = string.Equals(parentText, "invalid", StringComparison.Ordinal) ||
                        int.TryParse(
                            parentText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var parentUid) &&
                        !entityIds.Contains(parentUid);
                    if (!parentMissing)
                        break;

                    classifiedRoots.Add(uid);
                    detachedIds.Add(uid);
                    detachedEntityCount++;
                    break;
                }
            }

            if (detachedEntityCount == 0)
                return true;

            var sequenceStartIndex = nullspace.Start.Index;
            var sequenceEndIndex = nullspace.End.Index;
            if (sequenceStartIndex < 0 ||
                sequenceEndIndex < sequenceStartIndex ||
                sequenceEndIndex > yaml.Length)
            {
                reason = "snapshot-yaml-nullspace-location-invalid";
                return false;
            }

            var sequenceStart = checked((int) sequenceStartIndex);
            var sequenceEnd = checked((int) sequenceEndIndex);
            if (sequenceEnd == sequenceStart &&
                yaml.AsSpan(sequenceStart).StartsWith("[]", StringComparison.Ordinal))
            {
                sequenceEnd += 2;
            }

            var replacement = string.Join(
                "\n",
                nullspaceIds
                    .Concat(detachedIds)
                    .Select(uid => $"- {uid.ToString(CultureInfo.InvariantCulture)}"));
            if (sequenceStart > 0 && yaml[sequenceStart - 1] != '\n')
                replacement = "\n" + replacement;

            preparedYaml = yaml[..sequenceStart] + replacement + yaml[sequenceEnd..];
            return true;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not StackOverflowException)
        {
            reason = $"snapshot-compatibility-parse-failed:{exception.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    /// Normalizes historical snapshot YAML that predates current serialization
    /// rules. Older saves wrote primitive type tags using short names that the
    /// serializer no longer resolves, and some Goobstation prototypes were
    /// renamed after ships were saved.
    /// </summary>
    private static string NormalizeLegacySnapshotText(string yaml)
    {
        var normalized = yaml
            .Replace("!type:Int32 ", string.Empty, StringComparison.Ordinal)
            .Replace("!type:Single ", string.Empty, StringComparison.Ordinal)
            .Replace("!type:Double ", string.Empty, StringComparison.Ordinal)
            .Replace("!type:Boolean ", string.Empty, StringComparison.Ordinal)
            .Replace("!type:String ", string.Empty, StringComparison.Ordinal);

        foreach (var (legacyId, currentId) in LegacySnapshotPrototypeAliases)
        {
            normalized = normalized.Replace(
                $"- proto: {legacyId}",
                $"- proto: {currentId}",
                StringComparison.Ordinal);
        }

        return normalized;
    }

    /// <summary>
    /// Prototype renames that may be present in historical ship snapshots.
    /// Every content rename that could exist inside a saved ship must keep an
    /// entry here, otherwise the affected ship is quarantined on restore.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> LegacySnapshotPrototypeAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WeaponShotgunKammererPMC"] = "WeaponShotgunKammerer",
            ["Audio"] = "PaperBin20",
        };

    /// <summary>
    /// Re-validates a durable snapshot and extracts only aggregate prototype counts
    /// for local Python analysis. Raw saved state and player identifiers are never
    /// included in the returned manifest.
    /// </summary>
    public bool TryGetSavedShipManifest(
        LuaMFullShipSnapshot snapshot,
        out LuaMSavedShipManifest manifest,
        out string reason)
    {
        manifest = default!;
        if (!TryValidateSnapshot(
                snapshot,
                out _,
                out var prototypeCounts,
                out var prototypeManifestHash,
                out reason))
        {
            return false;
        }

        manifest = new LuaMSavedShipManifest(
            snapshot.FormatVersion,
            snapshot.EntityCount,
            snapshot.PayloadSizeBytes,
            prototypeManifestHash,
            prototypeCounts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new LuaMSavedShipPrototypeCount(pair.Key, pair.Value))
                .ToArray());
        return true;
    }

    private bool TryFindActiveShip(Guid shipId, EntityUid? except, out EntityUid grid)
    {
        var query = EntityQueryEnumerator<LuaMShipIdentityComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var identity, out _))
        {
            if (uid != except && identity.ShipId == shipId)
            {
                grid = uid;
                return true;
            }
        }

        grid = EntityUid.Invalid;
        return false;
    }

    private HashSet<EntityUid> CollectTransformGraph(EntityUid root)
    {
        var result = new HashSet<EntityUid>();
        var pending = new Stack<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (!result.Add(uid) || !Exists(uid))
                continue;

            if (uid != root && IsPlayerBody(uid))
            {
                result.Remove(uid);
                continue;
            }

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        return result;
    }

    private bool IsPlayerBody(EntityUid uid)
    {
        if (HasComp<ActorComponent>(uid) ||
            HasComp<PlayerJobComponent>(uid) ||
            HasComp<LuaMPlayerControlledBodyComponent>(uid))
        {
            return true;
        }

        return TryComp<MindContainerComponent>(uid, out var container) &&
               container.Mind is { } mind &&
               TryComp<MindComponent>(mind, out var mindComponent) &&
               mindComponent.UserId != null;
    }

    public static bool IsSupportedSnapshotFormatVersion(int formatVersion)
        => formatVersion is LegacySnapshotFormatVersion or SnapshotFormatVersion;

    private List<EntityUid> CollectExistingTransformGraph(EntityUid root)
    {
        var result = new List<EntityUid>();
        var visited = new HashSet<EntityUid>();
        var pending = new Stack<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            result.Add(uid);
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        return result;
    }

    private Dictionary<EntityPrototype, bool> EnableCompleteGraphSerialization(IEnumerable<EntityUid> entities)
    {
        var changed = new Dictionary<EntityPrototype, bool>();
        foreach (var uid in entities)
        {
            if (!Exists(uid) || MetaData(uid).EntityPrototype is not { } prototype)
                continue;
            if (changed.ContainsKey(prototype) || prototype.MapSavable)
                continue;

            changed.Add(prototype, prototype.MapSavable);
            prototype.MapSavable = true;
        }

        return changed;
    }

    private static void RestorePrototypeSaveFlags(IReadOnlyDictionary<EntityPrototype, bool> changed)
    {
        foreach (var (prototype, original) in changed)
            prototype.MapSavable = original;
    }

    private bool ContainsRequiredGraph(
        IReadOnlySet<EntityUid> requiredEntities,
        IReadOnlyDictionary<string, int> serializedPrototypeCounts,
        out string reason)
    {
        reason = string.Empty;
        var requiredCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var uid in requiredEntities)
        {
            if (!Exists(uid))
            {
                reason = "grid-graph-changed-during-capture";
                return false;
            }

            var prototype = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
            requiredCounts[prototype] = requiredCounts.GetValueOrDefault(prototype) + 1;
        }

        foreach (var (prototype, count) in requiredCounts)
        {
            if (serializedPrototypeCounts.GetValueOrDefault(prototype) < count)
            {
                reason = "snapshot-omitted-grid-entity";
                return false;
            }
        }

        return true;
    }

    private bool TryInspectEntitySet(
        IReadOnlySet<EntityUid> entities,
        out int entityCount,
        out string prototypeManifestHash,
        out string reason)
    {
        entityCount = 0;
        prototypeManifestHash = string.Empty;
        reason = string.Empty;
        var prototypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var uid in entities)
        {
            if (!Exists(uid))
            {
                reason = "restored-entity-set-changed-during-validation";
                return false;
            }

            var prototype = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
            prototypeCounts[prototype] = prototypeCounts.GetValueOrDefault(prototype) + 1;
            entityCount++;
        }

        prototypeManifestHash = ComputePrototypeManifestHash(prototypeCounts);
        return true;
    }

    /// <summary>
    /// Creates the deferred vessel station, then forgets the exact restored entity set after
    /// the database CAS transition succeeds.
    /// </summary>
    public bool CommitRestore(LuaMShipRestoreScope restore)
    {
        if (!restore.TryGetCreatedEntities(out _))
            return false;

        var entitiesBeforeStation = EntityManager.GetEntities().ToHashSet();
        try
        {
            RestoreVesselStation(restore);
        }
        catch (Exception exception)
        {
            Log.Error(
                $"Persistent ship {restore.ShipId} committed without its vessel station because station initialization failed: {exception}");

            var partialStationEntities = EntityManager.GetEntities()
                .Where(uid => !entitiesBeforeStation.Contains(uid))
                .ToHashSet();
            try
            {
                CleanupRestoreEntities(partialStationEntities);
            }
            catch (Exception cleanupException)
            {
                Log.Error(
                    $"Could not completely clean partial station entities for committed persistent ship {restore.ShipId}: {cleanupException}");
            }

            return false;
        }

        return restore.TryTakeCreatedEntities(out _);
    }

    /// <summary>
    /// Synchronously deletes the exact entity set owned by an uncommitted restore.
    /// </summary>
    public bool RollbackRestore(LuaMShipRestoreScope restore)
    {
        if (!restore.TryGetCreatedEntities(out var createdEntities))
            return false;

        // A powered emitter can recreate its derived envelope while database completion is
        // pending. It was not part of deserialization and would otherwise be treated as a
        // retained passenger, detached from the deleted grid, and leaked into the map/PVS.
        _shipShields.ReconcileRestoredShipShields(restore.Grid, createdEntities);
        CleanupRestoreEntities(createdEntities);
        return restore.TryTakeCreatedEntities(out _);
    }

    private void CleanupRestoreEntities(IReadOnlySet<EntityUid> ownedEntities)
    {
        // Take the rollback-local baseline before any cleanup event can create a
        // helper. No entity baseline crosses the database await; entities that
        // appeared while the restore was pending are retained unless they were
        // created by the teardown below.
        var retainedEntities = EntityManager.GetEntities()
            .Where(uid => !ownedEntities.Contains(uid))
            .ToHashSet();

        // Deleting an owned transform root recursively deletes all of its current
        // children, even when those children were not part of deserialization.
        // Move every retained boundary root (passengers, newly inserted items,
        // and their complete retained subtrees) to the map before deleting the
        // restored graph.
        PreserveRetainedTransformSubtrees(ownedEntities, retainedEntities);

        // A successful placement may have docked the restored ship to a
        // pre-existing station gate before the database transition failed. Dock
        // shutdown is skipped once recursive deletion marks the ship graph as
        // terminating, so explicitly sever only exact reciprocal pairs that
        // cross the restore ownership boundary before deleting either side.
        UndockOwnedExternalConnections(ownedEntities);

        CleanupEntities(ownedEntities);
        for (var pass = 0; pass < 3; pass++)
        {
            var teardownEntities = EntityManager.GetEntities()
                .Where(uid => !retainedEntities.Contains(uid))
                .ToHashSet();
            if (teardownEntities.Count == 0)
                return;

            CleanupEntities(teardownEntities);
        }
    }

    private void PreserveRetainedTransformSubtrees(
        IReadOnlySet<EntityUid> ownedEntities,
        IReadOnlySet<EntityUid> retainedEntities)
    {
        var retainedBoundaryRoots = retainedEntities
            .Where(uid =>
                Exists(uid) &&
                TryComp<TransformComponent>(uid, out var xform) &&
                ownedEntities.Contains(xform.ParentUid))
            .ToArray();

        foreach (var uid in retainedBoundaryRoots)
        {
            if (!Exists(uid) || !TryComp<TransformComponent>(uid, out var xform))
                throw new InvalidOperationException("A retained restore-boundary entity vanished during rollback.");

            var mapCoordinates = _transform.GetMapCoordinates(xform);
            var worldRotation = _transform.GetWorldRotation(xform);

            // A retained item can have been inserted into a restored container
            // while database completion was pending. Prove that the container is
            // owned, then require force-removal to clear its index and metadata
            // before changing the transform parent.
            if (_containers.IsEntityInContainer(uid))
            {
                if (!_containers.TryGetContainingContainer(uid, out var containingContainer) ||
                    !ownedEntities.Contains(containingContainer.Owner) ||
                    !_containers.TryRemoveFromContainer(uid, force: true))
                {
                    throw new InvalidOperationException(
                        $"Retained entity {uid} could not leave its restored container safely.");
                }
            }

            if (!Exists(uid) || !TryComp<TransformComponent>(uid, out xform))
                throw new InvalidOperationException("A retained entity was deleted while leaving a restored container.");

            if (mapCoordinates.MapId != MapId.Nullspace && _maps.MapExists(mapCoordinates.MapId))
            {
                var mapUid = _maps.GetMap(mapCoordinates.MapId);
                _transform.SetCoordinates(
                    uid,
                    xform,
                    new EntityCoordinates(mapUid, mapCoordinates.Position),
                    worldRotation);
            }
            else
            {
                _transform.DetachEntity(uid, xform);
            }

            if (Exists(uid) && HasOwnedTransformAncestor(uid, ownedEntities))
            {
                throw new InvalidOperationException(
                    $"Retained entity {uid} could not be detached from the restored ship graph.");
            }

            if (Exists(uid) &&
                (_containers.IsEntityInContainer(uid) ||
                 _containers.TryGetContainingContainer(uid, out var remainingContainer) &&
                 ownedEntities.Contains(remainingContainer.Owner)))
            {
                throw new InvalidOperationException(
                    $"Retained entity {uid} still belongs to a restored container after detachment.");
            }
        }
    }

    private bool HasOwnedTransformAncestor(EntityUid uid, IReadOnlySet<EntityUid> ownedEntities)
    {
        var visited = new HashSet<EntityUid>();
        while (Exists(uid) && TryComp<TransformComponent>(uid, out var xform) && xform.ParentUid.IsValid())
        {
            var parent = xform.ParentUid;
            if (ownedEntities.Contains(parent))
                return true;

            if (!visited.Add(parent))
                throw new InvalidOperationException($"Transform cycle encountered while preserving retained entity {uid}.");

            uid = parent;
        }

        return false;
    }

    private void UndockOwnedExternalConnections(IReadOnlySet<EntityUid> ownedEntities)
    {
        foreach (var uid in ownedEntities)
        {
            if (!Exists(uid) ||
                !TryComp<DockingComponent>(uid, out var ownedDock) ||
                ownedDock.DockedWith is not { } externalDockUid ||
                ownedEntities.Contains(externalDockUid) ||
                !TryComp<DockingComponent>(externalDockUid, out var externalDock) ||
                externalDock.DockedWith != uid)
            {
                continue;
            }

            _docking.Undock((uid, ownedDock));
        }
    }

    private void CleanupPartialLoadEntities(IReadOnlySet<EntityUid> entitiesBeforeLoad)
    {
        // A failed deserializer cannot return an ownership scope. It is still
        // synchronous here, so a bounded baseline diff can recover partial output.
        for (var pass = 0; pass < 3; pass++)
        {
            var created = EntityManager.GetEntities()
                .Where(uid => !entitiesBeforeLoad.Contains(uid))
                .ToHashSet();
            if (created.Count == 0)
                return;

            CleanupEntities(created);
        }
    }

    private void CleanupEntities(IReadOnlySet<EntityUid> entities)
    {
        // Delete roots first so normal transform/container teardown can run. A
        // second pass catches auto-included null-space entities and anything a
        // teardown hook detached from its restored root.
        foreach (var uid in entities)
        {
            if (!Exists(uid))
                continue;

            var parent = Transform(uid).ParentUid;
            if (!parent.IsValid() || !entities.Contains(parent))
                Del(uid);
        }

        foreach (var uid in entities)
        {
            if (Exists(uid))
                Del(uid);
        }
    }

    private static string ComputePrototypeManifestHash(IReadOnlyDictionary<string, int> prototypeCounts)
    {
        var builder = new StringBuilder();
        foreach (var (prototype, count) in prototypeCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(prototype).Append('\t').Append(count).Append('\n');

        return ComputeHash(StrictUtf8.GetBytes(builder.ToString()));
    }

    private static string ComputeHash(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static bool FixedHashEquals(string actual, string expected)
    {
        if (actual.Length != 64 || expected.Length != 64)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(expected.ToLowerInvariant()));
    }

    private static string NormalizeBuildMetadata(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        return value.Length <= MaxBuildMetadataLength
            ? value
            : value[..MaxBuildMetadataLength];
    }

    private static bool TryGetMapping(
        YamlMappingNode mapping,
        string key,
        out YamlMappingNode value)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlMappingNode result)
        {
            value = result;
            return true;
        }

        value = default!;
        return false;
    }

    private static bool TryGetSequence(
        YamlMappingNode mapping,
        string key,
        out YamlSequenceNode value)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlSequenceNode result)
        {
            value = result;
            return true;
        }

        value = default!;
        return false;
    }

    private static bool TryGetScalar(
        YamlMappingNode mapping,
        string key,
        out string value)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlScalarNode { Value: { } result })
        {
            value = result;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var (candidate, node) in mapping.Children)
        {
            if (candidate is YamlScalarNode { Value: var candidateKey } && candidateKey == key)
            {
                value = node;
                return true;
            }
        }

        value = default!;
        return false;
    }
}
