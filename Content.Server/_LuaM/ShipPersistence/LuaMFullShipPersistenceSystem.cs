using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Database;
using Content.Server._NF.CryoSleep;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Mind;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
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

    internal LuaMShipRestoreScope(Guid shipId, EntityUid grid, HashSet<EntityUid> createdEntities)
    {
        ShipId = shipId;
        Grid = grid;
        _createdEntities = createdEntities;
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
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private ShuttleConsoleLockSystem _consoleLocks = default!;
    [Dependency] private MindSystem _minds = default!;

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
        CommitRestore(restore);
        return true;
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
        Angle rotation = default)
    {
        restore = default!;
        reason = string.Empty;

        if (!TryValidateSnapshot(snapshot, out var yaml, out reason))
            return false;

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
            using var reader = new StringReader(yaml);
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
                    out reason) ||
                entityCount != snapshot.EntityCount ||
                !FixedHashEquals(prototypeManifestHash, snapshot.PrototypeManifestHash))
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                if (string.IsNullOrEmpty(reason))
                    reason = "restored-entity-graph-or-manifest-mismatch";
                return false;
            }

            // Defence in depth for a malformed or manually repaired current
            // snapshot. Legacy v1 is rejected before deserialization.
            SanitizeRestoredPlayerBodies(createdEntities, restoredGrid);

            // Player minds are runtime ownership, not portable ship content.
            SanitizeRestoredMinds(createdEntities);
            createdEntities.RemoveWhere(uid => !Exists(uid));

            if (!_consoleLocks.TryBindPersistentShipSecurity(
                    restoredGrid,
                    snapshot.ShipId,
                    out var bindingReason))
            {
                CleanupPartialLoadEntities(entitiesBeforeLoad);
                reason = $"restored-ship-security-binding-failed:{bindingReason}";
                return false;
            }

            restore = new LuaMShipRestoreScope(snapshot.ShipId, restoredGrid, createdEntities);
            return true;
        }
        catch (Exception exception)
        {
            CleanupPartialLoadEntities(entitiesBeforeLoad);
            reason = $"snapshot-restore-exception:{exception.GetType().Name}";
            return false;
        }
        finally
        {
            _busyShips.Remove(snapshot.ShipId);
        }
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
            var changed = false;
            foreach (var node in prototypeGroups.Children)
            {
                if (node is not YamlMappingNode group ||
                    !TryGetScalar(group, "proto", out var prototype) ||
                    !TryGetSequence(group, "entities", out var entities))
                {
                    reason = "snapshot-yaml-entity-group-invalid";
                    return false;
                }

                foreach (var entityNode in entities.Children)
                {
                    if (entityNode is not YamlMappingNode entity)
                    {
                        reason = "snapshot-yaml-entity-invalid";
                        return false;
                    }

                    countedEntities = checked(countedEntities + 1);
                    if (countedEntities > LuaMShipPersistenceLimits.MaxEntityCount)
                    {
                        reason = "snapshot-entity-count-limit-exceeded";
                        return false;
                    }

                    changed |= SanitizeEntityComponents(entity);
                }

                prototypeCounts[prototype] = checked(
                    prototypeCounts.GetValueOrDefault(prototype) + entities.Children.Count);
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

    private static bool SanitizeEntityComponents(YamlMappingNode entity)
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
                            changed |= SanitizeContainerReferenceNode(container);
                    }
                    break;
                case "Strap":
                    if (TryGetNode(component, "buckledEntities", out var buckled))
                        changed |= RemoveInvalidReferences(buckled);
                    break;
                case "Storage":
                    if (TryGetMapping(component, "storedItems", out var storedItems))
                        changed |= RemoveInvalidMappingKeys(storedItems);
                    break;
                case "Puller":
                    changed |= ReplaceInvalidReferenceWithNull(component, "pulling");
                    break;
                case "Pullable":
                    if (ReplaceInvalidReferenceWithNull(component, "puller"))
                    {
                        changed = true;
                        changed |= ReplaceScalarWithNull(component, "pullJointId");
                    }
                    break;
                case "Joint":
                    changed |= ReplaceInvalidReferenceWithNull(component, "relay");
                    if (TryGetMapping(component, "joints", out var joints))
                        changed |= RemoveMappingsContainingInvalidReference(joints);
                    break;
                case "StationMember":
                    if (HasInvalidReference(component, "station"))
                    {
                        // A restored portable hull is assigned to its destination
                        // station by the caller. Keeping an invalid station member
                        // is worse than having no membership.
                        components.Children.RemoveAt(index);
                        changed = true;
                    }
                    break;
                case "StationTracker":
                    changed |= ReplaceInvalidReferenceWithNull(component, "station");
                    break;
            }
        }

        return changed;
    }

    private static bool SanitizeContainerReferenceNode(YamlNode node)
    {
        var changed = false;

        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (keyNode, valueNode) in mapping.Children.ToArray())
                {
                    if (keyNode is YamlScalarNode key)
                    {
                        if (key.Value == "ent" && IsInvalidReference(valueNode))
                        {
                            mapping.Children[keyNode] = new YamlScalarNode("null");
                            changed = true;
                            continue;
                        }

                        if (key.Value == "ents")
                            changed |= RemoveInvalidReferences(valueNode);
                    }

                    changed |= SanitizeContainerReferenceNode(valueNode);
                }

                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                    changed |= SanitizeContainerReferenceNode(child);
                break;
        }

        return changed;
    }

    private static bool RemoveInvalidReferences(YamlNode node)
    {
        if (node is not YamlSequenceNode sequence)
            return false;

        var changed = false;
        for (var i = sequence.Children.Count - 1; i >= 0; i--)
        {
            if (!IsInvalidReference(sequence.Children[i]))
                continue;

            sequence.Children.RemoveAt(i);
            changed = true;
        }

        return changed;
    }

    private static bool IsInvalidReference(YamlNode node)
        => node is YamlScalarNode { Value: "invalid" };

    private static bool HasInvalidReference(YamlMappingNode mapping, string key)
        => TryGetNode(mapping, key, out var value) && IsInvalidReference(value);

    private static bool ReplaceInvalidReferenceWithNull(YamlMappingNode mapping, string key)
    {
        if (!TryGetNode(mapping, key, out var value) || !IsInvalidReference(value))
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

    private static bool RemoveInvalidMappingKeys(YamlMappingNode mapping)
    {
        var changed = false;
        foreach (var key in mapping.Children.Keys.ToArray())
        {
            if (!IsInvalidReference(key))
                continue;

            mapping.Children.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static bool RemoveMappingsContainingInvalidReference(YamlMappingNode mapping)
    {
        var changed = false;
        foreach (var (key, value) in mapping.Children.ToArray())
        {
            if (!ContainsInvalidReference(value))
                continue;

            mapping.Children.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static bool ContainsInvalidReference(YamlNode node)
    {
        if (IsInvalidReference(node))
            return true;

        return node switch
        {
            YamlSequenceNode sequence => sequence.Children.Any(ContainsInvalidReference),
            YamlMappingNode mapping => mapping.Children.Any(pair =>
                ContainsInvalidReference(pair.Key) || ContainsInvalidReference(pair.Value)),
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
        => formatVersion == SnapshotFormatVersion;

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
    /// Forgets the exact restored entity set after the database CAS transition succeeds.
    /// </summary>
    public bool CommitRestore(LuaMShipRestoreScope restore)
    {
        return restore.TryTakeCreatedEntities(out _);
    }

    /// <summary>
    /// Synchronously deletes the exact entity set owned by an uncommitted restore.
    /// </summary>
    public bool RollbackRestore(LuaMShipRestoreScope restore)
    {
        if (!restore.TryGetCreatedEntities(out var createdEntities))
            return false;

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
