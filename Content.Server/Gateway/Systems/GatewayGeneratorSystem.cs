using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Mono.Cleanup;
using Content.Server.Administration.Logs;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Gateway.Components;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Weather;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.Database;
using Content.Shared.Gateway;
using Content.Shared.Ghost;
using Content.Shared.Maps;
using Content.Shared.Mind.Components;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
using Content.Shared.Procedural;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions.Modifiers;
using Content.Shared.Weather;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Gateway.Systems;

/// <summary>
/// Maintains a bounded pool of generated gateway destinations.
/// </summary>
public sealed partial class GatewayGeneratorSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfgManager = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _protoManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private BiomeSystem _biome = default!;
    [Dependency] private DungeonSystem _dungeon = default!;
    [Dependency] private GatewaySystem _gateway = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedSalvageSystem _salvage = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TileSystem _tile = default!;
    [Dependency] private WeatherSystem _weather = default!;

    [ValidatePrototypeId<LocalizedDatasetPrototype>]
    private const string PlanetNames = "NamesBorer";

    private const int InitialDestinationCount = 3;
    private const float DungeonBoundaryMargin = 2f;
    private const float MaxGeneratedWorldRange = 160f;
    private static readonly TimeSpan FailedDestinationRetention = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SafetyCleanupInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<EntityUid, Task<List<Dungeon>>> _generationTasks = new();
    private TimeSpan _nextSafetyCleanup;
    private TimeSpan _nextCleanup;

    // TODO: Add profile-aware ambient music to generated planets.

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GatewayGeneratorComponent, MapInitEvent>(OnGeneratorMapInit);
        SubscribeLocalEvent<GatewayGeneratorComponent, ComponentShutdown>(OnGeneratorShutdown);
        SubscribeLocalEvent<GatewayGeneratorDestinationComponent, AttemptGatewayOpenEvent>(OnGeneratorAttemptOpen);
        SubscribeLocalEvent<GatewayGeneratorDestinationComponent, GatewayOpenEvent>(OnGeneratorOpen);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        ProcessGenerationTasks();

        if (_timing.CurTime >= _nextSafetyCleanup)
        {
            _nextSafetyCleanup = _timing.CurTime + SafetyCleanupInterval;
            CleanupFailedAndOrphanedDestinations();

            if (_cfgManager.GetCVar(CCVars.GatewayGeneratorEnabled))
            {
                var repairQuery = EntityQueryEnumerator<GatewayGeneratorComponent>();
                while (repairQuery.MoveNext(out var generatorUid, out var generator))
                {
                    RepairDestinationGateways(generatorUid, generator);
                }
            }
        }

        if (_timing.CurTime < _nextCleanup)
            return;

        _nextCleanup = _timing.CurTime + CleanupInterval;

        var query = EntityQueryEnumerator<GatewayGeneratorComponent>();
        while (query.MoveNext(out var uid, out var generator))
        {
            CleanupExpiredDestinations(uid, generator);

            if (_cfgManager.GetCVar(CCVars.GatewayGeneratorEnabled))
                EnsureDestinationPool(uid, generator);
        }
    }

    private void OnGeneratorShutdown(EntityUid uid, GatewayGeneratorComponent component, ComponentShutdown args)
    {
        foreach (var genUid in component.Generated.ToArray())
        {
            if (!TryComp(genUid, out GatewayGeneratorDestinationComponent? destination))
            {
                QueueDestinationMapDeletion(genUid);
                continue;
            }

            destination.Generator = EntityUid.Invalid;
            destination.Orphaned = true;
            destination.Locked = true;
            destination.EmptySince = TimeSpan.Zero;
            destination.RotationState = GatewayDestinationRotationState.WaitingForClearance;
        }

        component.Generated.Clear();
        _gateway.UpdateAllGateways();
    }

    private void OnGeneratorMapInit(EntityUid uid, GatewayGeneratorComponent generator, MapInitEvent args)
    {
        if (!_cfgManager.GetCVar(CCVars.GatewayGeneratorEnabled))
            return;

        generator.NextUnlock = TimeSpan.FromMinutes(5);

        EnsureDestinationPool(uid, generator);
    }

    /// <summary>
    /// Deletes expired unopened destinations, safely retires eligible opened destinations,
    /// removes dead references, and enforces the configured hard cap.
    /// </summary>
    internal int CleanupExpiredDestinations(EntityUid uid, GatewayGeneratorComponent generator)
    {
        var removed = 0;
        var uiChanged = false;
        var ttlSeconds = _cfgManager.GetCVar(CCVars.GatewayGeneratorDestinationTtl);
        var ttl = GetConfiguredDuration(ttlSeconds);

        for (var i = generator.Generated.Count - 1; i >= 0; i--)
        {
            var destinationUid = generator.Generated[i];
            if (!destinationUid.IsValid() ||
                !Exists(destinationUid) ||
                Terminating(destinationUid))
            {
                generator.Generated.RemoveAt(i);
                continue;
            }

            if (!TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination))
            {
                generator.Generated.RemoveAt(i);
                QueueDestinationMapDeletion(destinationUid);
                removed++;
                continue;
            }

            if (destination.Generator != uid)
            {
                generator.Generated.RemoveAt(i);
                continue;
            }

            if (destination.GenerationState is GatewayDestinationGenerationState.Generating or
                GatewayDestinationGenerationState.Failed)
            {
                continue;
            }

            if (destination.Loaded)
            {
                if (!TryRetireLoadedDestination(destinationUid, destination, out var stateChanged))
                {
                    uiChanged |= stateChanged;
                    continue;
                }

                generator.Generated.RemoveAt(i);
                LogAutomaticRotation(destinationUid, destination, "opened destination completed safe rotation");
                QueueDestinationMapDeletion(destinationUid);
                removed++;
                continue;
            }

            if (ttl == TimeSpan.Zero)
                continue;

            if (destination.GeneratedAt + ttl > _timing.CurTime)
                continue;

            generator.Generated.RemoveAt(i);
            LogAutomaticRotation(destinationUid, destination, "unopened destination expired");
            QueueDestinationMapDeletion(destinationUid);
            removed++;
        }

        var maxDestinations = Math.Max(0, _cfgManager.GetCVar(CCVars.GatewayGeneratorMaxDestinations));
        for (var i = 0; generator.Generated.Count > maxDestinations && i < generator.Generated.Count;)
        {
            var destinationUid = generator.Generated[i];
            if (TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination) &&
                (destination.Loaded ||
                 destination.GenerationState == GatewayDestinationGenerationState.Generating))
            {
                i++;
                continue;
            }

            generator.Generated.RemoveAt(i);
            if (TryComp(destinationUid, out destination))
                LogAutomaticRotation(destinationUid, destination, "destination exceeded the configured hard cap");
            QueueDestinationMapDeletion(destinationUid);
            removed++;
        }

        if (removed > 0 || uiChanged)
            _gateway.UpdateAllGateways();

        return removed;
    }

    private bool TryRetireLoadedDestination(
        EntityUid destinationUid,
        GatewayGeneratorDestinationComponent destination,
        out bool stateChanged)
    {
        stateChanged = false;
        var openedTtl = GetConfiguredDuration(_cfgManager.GetCVar(CCVars.GatewayGeneratorOpenedDestinationTtl));
        if (openedTtl == TimeSpan.Zero)
        {
            stateChanged = SetRotationState(destination, GatewayDestinationRotationState.None);
            if (destination.RetireAt != TimeSpan.Zero)
            {
                destination.RetireAt = TimeSpan.Zero;
                stateChanged = true;
            }

            destination.EmptySince = TimeSpan.Zero;
            return false;
        }

        if (destination.RetireAt == TimeSpan.Zero)
        {
            var openedAt = destination.OpenedAt == TimeSpan.Zero
                ? _timing.CurTime
                : destination.OpenedAt;
            destination.RetireAt = openedAt + openedTtl;
            stateChanged = true;
        }

        if (_timing.CurTime < destination.RetireAt)
        {
            stateChanged |= SetRotationState(destination, GatewayDestinationRotationState.Scheduled);
            if (destination.EmptySince != TimeSpan.Zero)
            {
                destination.EmptySince = TimeSpan.Zero;
                stateChanged = true;
            }

            return false;
        }

        if (IsDestinationProtected(destinationUid))
        {
            if (destination.EmptySince != TimeSpan.Zero)
            {
                destination.EmptySince = TimeSpan.Zero;
                stateChanged = true;
            }

            stateChanged |= SetRotationState(destination, GatewayDestinationRotationState.WaitingForClearance);
            return false;
        }

        var emptyGrace = GetConfiguredDuration(_cfgManager.GetCVar(CCVars.GatewayGeneratorEmptyGrace));
        if (emptyGrace != TimeSpan.Zero && destination.EmptySince == TimeSpan.Zero)
        {
            destination.EmptySince = _timing.CurTime;
            stateChanged = true;
        }

        if (emptyGrace != TimeSpan.Zero &&
            destination.EmptySince + emptyGrace > _timing.CurTime)
        {
            stateChanged |= SetRotationState(destination, GatewayDestinationRotationState.EmptyGracePeriod);
            return false;
        }

        if (destination.Gateway.IsValid() && Exists(destination.Gateway))
        {
            _gateway.ClosePortal(
                destination.Gateway,
                reason: GatewayPortalCloseReason.AutomaticRotation);
        }

        return true;
    }

    private bool IsDestinationProtected(EntityUid mapUid)
    {
        var actorQuery = AllEntityQuery<ActorComponent, TransformComponent>();
        while (actorQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid == mapUid && !HasComp<GhostComponent>(uid))
                return true;
        }

        var mindQuery = AllEntityQuery<MindContainerComponent, TransformComponent>();
        while (mindQuery.MoveNext(out var uid, out var mind, out var xform))
        {
            if (mind.HasMind &&
                xform.MapUid == mapUid &&
                !HasComp<GhostComponent>(uid))
            {
                return true;
            }
        }

        // A shuttle or any other additional grid represents recoverable player property.
        // The generated planet itself is both the map entity and its primary grid.
        var gridQuery = AllEntityQuery<MapGridComponent, TransformComponent>();
        while (gridQuery.MoveNext(out var gridUid, out _, out var xform))
        {
            if (gridUid != mapUid && xform.MapUid == mapUid && !Terminating(gridUid))
                return true;
        }

        return false;
    }

    private static bool SetRotationState(
        GatewayGeneratorDestinationComponent destination,
        GatewayDestinationRotationState state)
    {
        if (destination.RotationState == state)
            return false;

        destination.RotationState = state;
        return true;
    }

    private static TimeSpan GetConfiguredDuration(float seconds)
    {
        return float.IsFinite(seconds) && seconds > 0f
            ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2d))
            : TimeSpan.Zero;
    }

    private void ProcessGenerationTasks()
    {
        var uiChanged = false;

        foreach (var (destinationUid, task) in _generationTasks.ToArray())
        {
            if (!task.IsCompleted)
                continue;

            _generationTasks.Remove(destinationUid);

            try
            {
                var dungeons = task.GetAwaiter().GetResult();
                if (!TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination))
                    continue;

                if (!TryComp(destinationUid, out RestrictedRangeComponent? restricted))
                    throw new InvalidOperationException("Generated gateway destination has no restricted range.");

                if (!TryGetRequiredDungeonRange(
                        dungeons,
                        restricted.Origin,
                        out var requiredRange,
                        out var furthestTile))
                {
                    var detail = furthestTile is { } tile
                        ? $"tile {tile} requires range {requiredRange:0.##}, above the hard limit {MaxGeneratedWorldRange}"
                        : "the dungeon produced no traversable tiles";
                    throw new InvalidOperationException(
                        $"Gateway dungeon bounds validation failed: {detail}.");
                }

                if (!ValidateDungeonBounds(dungeons, restricted, out var invalidTile))
                {
                    throw new InvalidOperationException(
                        $"Gateway dungeon bounds validation failed: tile {invalidTile} is outside " +
                        $"range {restricted.Range} around {restricted.Origin}.");
                }

                if (!_protoManager.TryIndex(
                        destination.Profile,
                        out GatewayWorldProfilePrototype? profile))
                {
                    throw new InvalidOperationException(
                        $"Gateway world profile '{destination.Profile.Id}' no longer exists.");
                }

                AddWorldMarkerLayers(destinationUid, destination, profile);
                destination.DungeonBoundsValidated = true;
                destination.GenerationState = GatewayDestinationGenerationState.Ready;
                destination.RetryAt = TimeSpan.Zero;
                Log.Info(
                    $"Gateway destination {ToPrettyString(destinationUid)} ({destination.Address}, " +
                    $"profile {destination.Profile.Id}) is ready with restricted range {restricted.Range:0.##}.");
                uiChanged = true;
            }
            catch (Exception exception)
            {
                if (TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination))
                {
                    MarkGenerationFailed(destinationUid, destination, exception);
                    uiChanged = true;
                }
                else
                {
                    Log.Error(
                        $"Gateway destination {ToPrettyString(destinationUid)} disappeared while generation completed: {exception}");
                }
            }
        }

        if (uiChanged)
            _gateway.UpdateAllGateways();
    }

    private static bool TryGetRequiredDungeonRange(
        IReadOnlyCollection<Dungeon> dungeons,
        Vector2 origin,
        out float requiredRange,
        out Vector2i? furthestTile)
    {
        requiredRange = 0f;
        furthestTile = null;

        foreach (var dungeon in dungeons)
        {
            foreach (var tile in dungeon.AllTiles)
            {
                var delta = (Vector2) tile - origin;
                // RestrictedRangeSystem creates a four-sided boundary. Its usable area is
                // therefore a diamond, so the matching distance is Manhattan rather than
                // Euclidean.
                var range = MathF.Abs(delta.X) + MathF.Abs(delta.Y) + DungeonBoundaryMargin;
                if (range <= requiredRange)
                    continue;

                requiredRange = range;
                furthestTile = tile;
            }
        }

        return furthestTile != null && requiredRange <= MaxGeneratedWorldRange;
    }

    private static bool ValidateDungeonBounds(
        IReadOnlyCollection<Dungeon> dungeons,
        RestrictedRangeComponent restricted,
        out Vector2i? invalidTile)
    {
        invalidTile = null;
        var hasTiles = false;
        var safeRange = Math.Max(0f, restricted.Range);

        foreach (var dungeon in dungeons)
        {
            foreach (var tile in dungeon.AllTiles)
            {
                hasTiles = true;
                var delta = (Vector2) tile - restricted.Origin;
                var requiredRange =
                    MathF.Abs(delta.X) + MathF.Abs(delta.Y) + DungeonBoundaryMargin;
                if (requiredRange <= safeRange)
                    continue;

                invalidTile = tile;
                return false;
            }
        }

        return hasTiles;
    }

    private void MarkGenerationFailed(
        EntityUid destinationUid,
        GatewayGeneratorDestinationComponent destination,
        Exception exception)
    {
        destination.GenerationState = GatewayDestinationGenerationState.Failed;
        destination.DungeonBoundsValidated = false;
        destination.Locked = true;
        destination.RetryAt = _timing.CurTime + FailedDestinationRetention;

        var message =
            $"Gateway destination {ToPrettyString(destinationUid)} ({destination.Address}, seed {destination.Seed}, " +
            $"profile {destination.Profile.Id}) failed generation and will be replaced: {exception}";
        Log.Warning(message);
        _adminLogger.Add(LogType.Action, LogImpact.High, $"{message}");
    }

    /// <summary>
    /// Discards failed transactions after a short observable failure state and safely retires worlds
    /// whose creating generator no longer exists.
    /// </summary>
    internal int CleanupFailedAndOrphanedDestinations()
    {
        var removed = 0;
        var uiChanged = false;
        var refillGenerators = new HashSet<EntityUid>();
        var emptyGrace = GetConfiguredDuration(_cfgManager.GetCVar(CCVars.GatewayGeneratorEmptyGrace));
        var query = EntityQueryEnumerator<GatewayGeneratorDestinationComponent>();

        while (query.MoveNext(out var destinationUid, out var destination))
        {
            if (destination.GenerationState == GatewayDestinationGenerationState.Failed)
            {
                if (destination.RetryAt > _timing.CurTime)
                    continue;

                if (IsDestinationProtected(destinationUid))
                {
                    destination.EmptySince = TimeSpan.Zero;
                    uiChanged |= SetRotationState(
                        destination,
                        GatewayDestinationRotationState.WaitingForClearance);
                    continue;
                }

                if ((destination.RotationState is GatewayDestinationRotationState.WaitingForClearance or
                        GatewayDestinationRotationState.EmptyGracePeriod) &&
                    emptyGrace != TimeSpan.Zero)
                {
                    if (destination.EmptySince == TimeSpan.Zero)
                    {
                        destination.EmptySince = _timing.CurTime;
                        uiChanged = true;
                    }

                    if (destination.EmptySince + emptyGrace > _timing.CurTime)
                    {
                        uiChanged |= SetRotationState(
                            destination,
                            GatewayDestinationRotationState.EmptyGracePeriod);
                        continue;
                    }
                }

                if (TryComp(destination.Generator, out GatewayGeneratorComponent? generator))
                {
                    generator.Generated.Remove(destinationUid);
                    refillGenerators.Add(destination.Generator);
                }

                LogAutomaticRotation(destinationUid, destination, "failed generation transaction was rolled back");
                QueueDestinationMapDeletion(destinationUid);
                removed++;
                uiChanged = true;
                continue;
            }

            if (!destination.Orphaned)
                continue;

            if (destination.GenerationState == GatewayDestinationGenerationState.Generating)
                continue;

            if (IsDestinationProtected(destinationUid))
            {
                if (destination.EmptySince != TimeSpan.Zero)
                {
                    destination.EmptySince = TimeSpan.Zero;
                    uiChanged = true;
                }

                uiChanged |= SetRotationState(
                    destination,
                    GatewayDestinationRotationState.WaitingForClearance);
                continue;
            }

            if (emptyGrace != TimeSpan.Zero && destination.EmptySince == TimeSpan.Zero)
            {
                destination.EmptySince = _timing.CurTime;
                uiChanged = true;
            }

            if (emptyGrace != TimeSpan.Zero &&
                destination.EmptySince + emptyGrace > _timing.CurTime)
            {
                uiChanged |= SetRotationState(
                    destination,
                    GatewayDestinationRotationState.EmptyGracePeriod);
                continue;
            }

            if (destination.Gateway.IsValid() && Exists(destination.Gateway))
            {
                _gateway.ClosePortal(
                    destination.Gateway,
                    reason: GatewayPortalCloseReason.AutomaticRotation);
            }

            LogAutomaticRotation(destinationUid, destination, "orphaned destination became safely empty");
            QueueDestinationMapDeletion(destinationUid);
            removed++;
            uiChanged = true;
        }

        foreach (var generatorUid in refillGenerators)
        {
            if (_cfgManager.GetCVar(CCVars.GatewayGeneratorEnabled) &&
                TryComp(generatorUid, out GatewayGeneratorComponent? generator))
            {
                EnsureDestinationPool(generatorUid, generator);
            }
        }

        if (uiChanged)
            _gateway.UpdateAllGateways();

        return removed;
    }

    private void LogAutomaticRotation(
        EntityUid destinationUid,
        GatewayGeneratorDestinationComponent destination,
        string reason)
    {
        _adminLogger.Add(
            LogType.Action,
            LogImpact.Medium,
            $"Gateway destination {ToPrettyString(destinationUid)} ({destination.Address}, seed {destination.Seed}, " +
            $"profile {destination.Profile.Id}) rotated automatically: {reason}.");
    }

    private void EnsureDestinationPool(EntityUid uid, GatewayGeneratorComponent generator)
    {
        var maxDestinations = Math.Max(0, _cfgManager.GetCVar(CCVars.GatewayGeneratorMaxDestinations));
        var targetAvailable = Math.Min(InitialDestinationCount, maxDestinations);

        while (CountAvailableDestinations(generator) < targetAvailable &&
               generator.Generated.Count < maxDestinations)
        {
            if (!TryGenerateDestination(uid, generator))
                break;
        }
    }

    private int CountAvailableDestinations(GatewayGeneratorComponent generator)
    {
        var count = 0;
        foreach (var destinationUid in generator.Generated)
        {
            if (TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination) &&
                !destination.Loaded &&
                destination.GenerationState != GatewayDestinationGenerationState.Failed &&
                IsDestinationGatewayUsable(destinationUid, destination, out _) &&
                !Terminating(destinationUid))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Restores a generated world's endpoint if an unrelated cleanup or deletion removed it.
    /// </summary>
    private int RepairDestinationGateways(
        EntityUid generatorUid,
        GatewayGeneratorComponent? generator = null)
    {
        if (!Resolve(generatorUid, ref generator))
            return 0;

        var repaired = 0;
        var uiChanged = false;

        foreach (var destinationUid in generator.Generated.ToArray())
        {
            if (!TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination) ||
                destination.Orphaned ||
                destination.GenerationState == GatewayDestinationGenerationState.Failed ||
                Terminating(destinationUid))
            {
                continue;
            }

            if (IsDestinationGatewayUsable(
                    destinationUid,
                    destination,
                    out var existingGateway))
            {
                EnsureComp<CleanupImmuneComponent>(destination.Gateway);
                if (!existingGateway.Enabled)
                {
                    _gateway.SetEnabled(destination.Gateway, true, existingGateway);
                    repaired++;
                    uiChanged = true;
                }

                continue;
            }

            try
            {
                var previousGateway = destination.Gateway;
                if (previousGateway.IsValid() &&
                    Exists(previousGateway) &&
                    !Terminating(previousGateway))
                {
                    if (TryComp(previousGateway, out GatewayComponent? staleGateway))
                    {
                        _gateway.ClosePortal(
                            previousGateway,
                            staleGateway,
                            update: false,
                            reason: GatewayPortalCloseReason.System);
                        _gateway.SetEnabled(previousGateway, false, staleGateway);
                    }

                    QueueDel(previousGateway);
                }

                if (!_protoManager.TryIndex(
                        destination.Profile,
                        out GatewayWorldProfilePrototype? profile))
                {
                    throw new InvalidOperationException(
                        $"Gateway world profile '{destination.Profile.Id}' no longer exists.");
                }

                var gatewayName = MetaData(destinationUid).EntityName;
                destination.Gateway = SpawnDestinationGateway(
                    destinationUid,
                    destination,
                    generator,
                    profile,
                    gatewayName);
                repaired++;
                uiChanged = true;

                var message =
                    $"Gateway destination {ToPrettyString(destinationUid)} ({destination.Address}, " +
                    $"profile {destination.Profile.Id}) restored missing endpoint {ToPrettyString(previousGateway)} " +
                    $"as {ToPrettyString(destination.Gateway)}.";
                Log.Warning(message);
                _adminLogger.Add(LogType.Action, LogImpact.Medium, $"{message}");
            }
            catch (Exception exception)
            {
                MarkGenerationFailed(destinationUid, destination, exception);
                uiChanged = true;
            }
        }

        if (uiChanged)
            _gateway.UpdateAllGateways();

        return repaired;
    }

    private bool IsDestinationGatewayUsable(
        EntityUid destinationUid,
        GatewayGeneratorDestinationComponent destination,
        [NotNullWhen(true)] out GatewayComponent? gateway)
    {
        gateway = null;
        if (!destination.Gateway.IsValid() ||
            TerminatingOrDeleted(destination.Gateway) ||
            !TryComp(destination.Gateway, out gateway) ||
            !TryComp(destination.Gateway, out TransformComponent? gatewayTransform))
        {
            return false;
        }

        return gatewayTransform.MapUid == destinationUid;
    }

    internal bool TryGenerateDestination(EntityUid uid, GatewayGeneratorComponent? generator = null)
    {
        if (!Resolve(uid, ref generator))
            return false;

        if (!_cfgManager.GetCVar(CCVars.GatewayGeneratorEnabled))
            return false;

        CleanupExpiredDestinations(uid, generator);
        var maxDestinations = Math.Max(0, _cfgManager.GetCVar(CCVars.GatewayGeneratorMaxDestinations));
        if (generator.Generated.Count >= maxDestinations)
            return false;

        var seed = _random.Next();
        var random = new Random(seed);
        if (!TryPickWorldProfile(generator, random, out var profile))
        {
            Log.Error($"Gateway generator {ToPrettyString(uid)} has no valid world profiles.");
            return false;
        }

        const int MaxOffset = 256;
        var mapId = MapId.Nullspace;
        var mapUid = EntityUid.Invalid;

        try
        {
            // Resolve every required prototype before committing the new map to the generator pool.
            var tileDef = _tileDefManager["FloorSteel"];
            var planetNames = _protoManager.Index<LocalizedDatasetPrototype>(PlanetNames);
            _protoManager.Index(profile.Biome);
            _protoManager.Index(profile.Air);
            if (profile.Weather is { } weather)
                _protoManager.Index(weather);
            var dungeon = _protoManager.Index(profile.Dungeon);

            mapUid = _maps.CreateMap(out mapId, runMapInit: false);
            var gatewayName = _salvage.GetFTLName(planetNames, seed);
            _metadata.SetEntityName(mapUid, gatewayName);

            var origin = new Vector2i(random.Next(-MaxOffset, MaxOffset), random.Next(-MaxOffset, MaxOffset));
            var restricted = new RestrictedRangeComponent
            {
                Origin = origin,
                Range = MaxGeneratedWorldRange
            };
            AddComp(mapUid, restricted);

            _biome.EnsurePlanet(
                mapUid,
                _protoManager.Index(profile.Biome),
                seed,
                mapLight: profile.LightColor);
            ApplyWorldEnvironment(mapUid, mapId, profile);

            var grid = Comp<MapGridComponent>(mapUid);
            var tiles = new List<(Vector2i Index, Tile Tile)>();
            for (var x = -2; x <= 2; x++)
            {
                for (var y = -2; y <= 2; y++)
                {
                    tiles.Add((
                        new Vector2i(x, y) + origin,
                        new Tile(
                            tileDef.TileId,
                            variant: _tile.PickVariant((ContentTileDefinition) tileDef, random))));
                }
            }

            // Clear area nearby as a landing pad.
            _maps.SetTiles(mapUid, grid, tiles);

            var genDest = AddComp<GatewayGeneratorDestinationComponent>(mapUid);
            genDest.Origin = origin;
            genDest.Seed = seed;
            genDest.Generator = uid;
            genDest.GeneratedAt = _timing.CurTime;
            genDest.Profile = profile.ID;
            genDest.Address = FormatAddress(seed);
            genDest.GenerationState = GatewayDestinationGenerationState.Generating;

            genDest.Gateway = SpawnDestinationGateway(
                mapUid,
                genDest,
                generator,
                profile,
                gatewayName);

            generator.Generated.Add(mapUid);
            _maps.InitializeMap(mapUid);
            StartDungeonGeneration(mapUid, grid, genDest, profile, dungeon);
            return true;
        }
        catch (Exception exception)
        {
            if (mapUid.IsValid())
                generator.Generated.Remove(mapUid);

            var message =
                $"Gateway generator {ToPrettyString(uid)} failed to create a destination transaction " +
                $"(seed {seed}, profile {profile.ID}): {exception}";
            Log.Error(message);
            _adminLogger.Add(LogType.Action, LogImpact.High, $"{message}");

            if (mapId != MapId.Nullspace && _maps.MapExists(mapId))
                _maps.DeleteMap(mapId);
            else if (mapUid.IsValid() && Exists(mapUid))
                QueueDel(mapUid);

            _gateway.UpdateAllGateways();
            return false;
        }
    }

    private EntityUid SpawnDestinationGateway(
        EntityUid mapUid,
        GatewayGeneratorDestinationComponent destination,
        GatewayGeneratorComponent generator,
        GatewayWorldProfilePrototype profile,
        string gatewayName)
    {
        if (generator.Proto is not { } gatewayPrototype)
            throw new InvalidOperationException("Gateway generator has no endpoint prototype.");

        var gatewayUid = SpawnAtPosition(
            gatewayPrototype,
            new EntityCoordinates(mapUid, destination.Origin));
        EnsureComp<CleanupImmuneComponent>(gatewayUid);

        var gateway = Comp<GatewayComponent>(gatewayUid);
        _gateway.SetDestinationName(
            gatewayUid,
            FormattedMessage.FromMarkupOrThrow(
                $"[color={profile.AccentColor.ToHex()}]{gatewayName}[/color]"),
            gateway);
        _gateway.SetEnabled(gatewayUid, true, gateway);
        return gatewayUid;
    }

    private void StartDungeonGeneration(
        EntityUid mapUid,
        MapGridComponent grid,
        GatewayGeneratorDestinationComponent destination,
        GatewayWorldProfilePrototype profile,
        DungeonConfigPrototype dungeon)
    {
        var random = new Random(destination.Seed);
        var dungeonDistanceMin = Math.Max(1, profile.DungeonDistanceMin);
        var dungeonDistanceMax = Math.Max(dungeonDistanceMin, profile.DungeonDistanceMax);
        var dungeonDistance = random.Next(dungeonDistanceMin, dungeonDistanceMax + 1);
        var dungeonRotation = _dungeon.GetDungeonRotation(destination.Seed);
        var dungeonPosition =
            (destination.Origin + dungeonRotation.RotateVec(new Vector2i(0, dungeonDistance))).Floored();

        var task = _dungeon.GenerateDungeonAsync(
            dungeon,
            dungeon.ID,
            mapUid,
            grid,
            dungeonPosition,
            destination.Seed);
        _generationTasks.Add(mapUid, task);
    }

    private void AddWorldMarkerLayers(
        EntityUid destinationUid,
        GatewayGeneratorDestinationComponent destination,
        GatewayWorldProfilePrototype profile)
    {
        if (!TryComp(destinationUid, out BiomeComponent? biome))
            return;

        TryComp(destination.Generator, out GatewayGeneratorComponent? generator);
        var random = new Random(destination.Seed);

        var lootLayers = profile.LootLayers.Count > 0
            ? profile.LootLayers.ToList()
            : generator?.LootLayers.ToList() ?? new List<ProtoId<BiomeMarkerLayerPrototype>>();
        var lootLayerCount = Math.Min(
            profile.LootLayers.Count > 0
                ? profile.LootLayerCount
                : generator?.LootLayerCount ?? 0,
            lootLayers.Count);

        for (var i = 0; i < lootLayerCount; i++)
        {
            var layerIdx = random.Next(lootLayers.Count);
            var layer = lootLayers[layerIdx];
            lootLayers.RemoveSwap(layerIdx);
            _biome.AddMarkerLayer(destinationUid, biome, layer.Id);
        }

        var mobLayers = profile.MobLayers.Count > 0
            ? profile.MobLayers.ToList()
            : generator?.MobLayers.ToList() ?? new List<ProtoId<BiomeMarkerLayerPrototype>>();
        var mobLayerCount = Math.Min(
            profile.MobLayers.Count > 0
                ? profile.MobLayerCount
                : generator?.MobLayerCount ?? 0,
            mobLayers.Count);

        for (var i = 0; i < mobLayerCount; i++)
        {
            var layerIdx = random.Next(mobLayers.Count);
            var layer = mobLayers[layerIdx];
            mobLayers.RemoveSwap(layerIdx);
            _biome.AddMarkerLayer(destinationUid, biome, layer.Id);
        }
    }

    private bool TryPickWorldProfile(
        GatewayGeneratorComponent generator,
        Random random,
        out GatewayWorldProfilePrototype profile)
    {
        var profiles = new List<GatewayWorldProfilePrototype>();
        foreach (var profileId in generator.Profiles.Distinct())
        {
            if (string.IsNullOrEmpty(profileId.Id))
                continue;

            if (_protoManager.TryIndex(profileId, out GatewayWorldProfilePrototype? indexed))
                profiles.Add(indexed);
        }

        if (profiles.Count == 0)
        {
            profile = default!;
            return false;
        }

        profiles.Sort((left, right) => string.CompareOrdinal(left.ID, right.ID));
        var activeProfiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destinationUid in generator.Generated)
        {
            if (TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination) &&
                !string.IsNullOrEmpty(destination.Profile.Id))
            {
                activeProfiles.Add(destination.Profile.Id);
            }
        }

        var unused = profiles.Where(candidate => !activeProfiles.Contains(candidate.ID)).ToList();
        var candidates = unused.Count > 0 ? unused : profiles;
        profile = candidates[random.Next(candidates.Count)];
        return true;
    }

    private void ApplyWorldEnvironment(
        EntityUid mapUid,
        MapId mapId,
        GatewayWorldProfilePrototype profile)
    {
        var air = _protoManager.Index(profile.Air);
        var moles = new float[Atmospherics.AdjustedNumberOfGases];
        air.Gases.CopyTo(moles, 0);

        var atmosphere = EnsureComp<MapAtmosphereComponent>(mapUid);
        _atmosphere.SetMapSpace(mapUid, air.Space, atmosphere);
        _atmosphere.SetMapGasMixture(mapUid, new GasMixture(moles, profile.Temperature), atmosphere);

        if (!air.Space && profile.Weather is { } weatherId)
            _weather.SetWeather(mapId, _protoManager.Index(weatherId), null);
    }

    private static string FormatAddress(int seed)
    {
        var address = unchecked((uint)seed);
        return $"GW-{address >> 24:X2}-{(address >> 16) & 0xFF:X2}-{(address >> 8) & 0xFF:X2}-{address & 0xFF:X2}";
    }

    private void QueueDestinationMapDeletion(EntityUid destinationUid)
    {
        if (!destinationUid.IsValid() ||
            !Exists(destinationUid) ||
            Terminating(destinationUid))
        {
            return;
        }

        var mapId = _transform.GetMapId(destinationUid);
        if (mapId == MapId.Nullspace)
        {
            QueueDel(destinationUid);
            return;
        }

        _maps.QueueDeleteMap(mapId);
    }

    private void OnGeneratorAttemptOpen(Entity<GatewayGeneratorDestinationComponent> ent, ref AttemptGatewayOpenEvent args)
    {
        if (args.Cancelled)
            return;

        if (ent.Comp.Orphaned ||
            ent.Comp.GenerationState != GatewayDestinationGenerationState.Ready)
        {
            args.Cancelled = true;
            return;
        }

        if (ent.Comp.Loaded)
            return;

        if (!TryComp(ent.Comp.Generator, out GatewayGeneratorComponent? generatorComp))
        {
            args.Cancelled = true;
            return;
        }

        if (generatorComp.NextUnlock + _metadata.GetPauseTime(ent.Owner) <= _timing.CurTime)
            return;

        args.Cancelled = true;
    }

    private void OnGeneratorOpen(Entity<GatewayGeneratorDestinationComponent> ent, ref GatewayOpenEvent args)
    {
        if (ent.Comp.Loaded)
            return;

        if (ent.Comp.Orphaned ||
            ent.Comp.GenerationState != GatewayDestinationGenerationState.Ready)
            return;

        ent.Comp.Locked = false;
        ent.Comp.Loaded = true;
        ent.Comp.OpenedAt = _timing.CurTime;
        var openedTtl = GetConfiguredDuration(_cfgManager.GetCVar(CCVars.GatewayGeneratorOpenedDestinationTtl));
        if (openedTtl != TimeSpan.Zero)
        {
            ent.Comp.RetireAt = _timing.CurTime + openedTtl;
            ent.Comp.RotationState = GatewayDestinationRotationState.Scheduled;
        }

        if (TryComp(ent.Comp.Generator, out GatewayGeneratorComponent? generatorComp))
        {
            generatorComp.NextUnlock = _timing.CurTime + generatorComp.UnlockCooldown;
            _gateway.UpdateAllGateways();
            EnsureDestinationPool(ent.Comp.Generator, generatorComp);
        }
    }
}
