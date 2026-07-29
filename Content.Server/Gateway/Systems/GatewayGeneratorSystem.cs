using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Gateway.Components;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Weather;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
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
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private IPrototypeManager _protoManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ITileDefinitionManager _tileDefManager = default!;
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
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
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
            QueueDestinationMapDeletion(genUid);
        }

        component.Generated.Clear();
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

            if (destination.Loaded)
            {
                if (!TryRetireLoadedDestination(destinationUid, destination, out var stateChanged))
                {
                    uiChanged |= stateChanged;
                    continue;
                }

                generator.Generated.RemoveAt(i);
                QueueDestinationMapDeletion(destinationUid);
                removed++;
                continue;
            }

            if (ttl == TimeSpan.Zero)
                continue;

            if (destination.GeneratedAt + ttl > _timing.CurTime)
                continue;

            generator.Generated.RemoveAt(i);
            QueueDestinationMapDeletion(destinationUid);
            removed++;
        }

        var maxDestinations = Math.Max(0, _cfgManager.GetCVar(CCVars.GatewayGeneratorMaxDestinations));
        for (var i = 0; generator.Generated.Count > maxDestinations && i < generator.Generated.Count;)
        {
            var destinationUid = generator.Generated[i];
            if (TryComp(destinationUid, out GatewayGeneratorDestinationComponent? destination) && destination.Loaded)
            {
                i++;
                continue;
            }

            generator.Generated.RemoveAt(i);
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
            _gateway.ClosePortal(destination.Gateway);

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
                !Terminating(destinationUid))
            {
                count++;
            }
        }

        return count;
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

        var tileDef = _tileDefManager["FloorSteel"];
        const int MaxOffset = 256;
        var tiles = new List<(Vector2i Index, Tile Tile)>();
        var mapId = _mapManager.CreateMap();
        var mapUid = _mapManager.GetMapEntityId(mapId);

        var gatewayName = _salvage.GetFTLName(_protoManager.Index<LocalizedDatasetPrototype>(PlanetNames), seed);
        _metadata.SetEntityName(mapUid, gatewayName);

        var origin = new Vector2i(random.Next(-MaxOffset, MaxOffset), random.Next(-MaxOffset, MaxOffset));
        var restricted = new RestrictedRangeComponent
        {
            Origin = origin
        };
        AddComp(mapUid, restricted);

        _biome.EnsurePlanet(
            mapUid,
            _protoManager.Index(profile.Biome),
            seed,
            mapLight: profile.LightColor);
        ApplyWorldEnvironment(mapUid, mapId, profile);

        var grid = Comp<MapGridComponent>(mapUid);

        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                tiles.Add((new Vector2i(x, y) + origin, new Tile(tileDef.TileId, variant: _tile.PickVariant((ContentTileDefinition)tileDef, random))));
            }
        }

        // Clear area nearby as a sort of landing pad.
        _maps.SetTiles(mapUid, grid, tiles);

        _metadata.SetEntityName(mapUid, gatewayName);
        var originCoords = new EntityCoordinates(mapUid, origin);

        var genDest = AddComp<GatewayGeneratorDestinationComponent>(mapUid);
        genDest.Origin = origin;
        genDest.Seed = seed;
        genDest.Generator = uid;
        genDest.GeneratedAt = _timing.CurTime;
        genDest.Profile = profile.ID;
        genDest.Address = FormatAddress(seed);

        // Create the gateway.
        var gatewayUid = SpawnAtPosition(generator.Proto, originCoords);
        genDest.Gateway = gatewayUid;
        var gatewayComp = Comp<GatewayComponent>(gatewayUid);
        _gateway.SetDestinationName(
            gatewayUid,
            FormattedMessage.FromMarkupOrThrow($"[color={profile.AccentColor.ToHex()}]{gatewayName}[/color]"),
            gatewayComp);
        _gateway.SetEnabled(gatewayUid, true, gatewayComp);
        generator.Generated.Add(mapUid);
        return true;
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
        if (ent.Comp.Loaded || args.Cancelled)
            return;

        if (!TryComp(ent.Comp.Generator, out GatewayGeneratorComponent? generatorComp))
            return;

        if (generatorComp.NextUnlock + _metadata.GetPauseTime(ent.Owner) <= _timing.CurTime)
            return;

        args.Cancelled = true;
    }

    private void OnGeneratorOpen(Entity<GatewayGeneratorDestinationComponent> ent, ref GatewayOpenEvent args)
    {
        if (ent.Comp.Loaded)
            return;

        if (!TryComp(args.MapUid, out MapGridComponent? grid))
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

        // Do dungeon
        var seed = ent.Comp.Seed;
        var origin = ent.Comp.Origin;
        var random = new Random(seed);
        GatewayWorldProfilePrototype? profile = null;
        if (!string.IsNullOrEmpty(ent.Comp.Profile.Id))
            _protoManager.TryIndex(ent.Comp.Profile, out profile);

        var dungeonDistanceMin = Math.Max(1, profile?.DungeonDistanceMin ?? 3);
        var dungeonDistanceMax = Math.Max(dungeonDistanceMin, profile?.DungeonDistanceMax ?? 5);
        var dungeonDistance = random.Next(dungeonDistanceMin, dungeonDistanceMax + 1);
        var dungeonRotation = _dungeon.GetDungeonRotation(seed);
        var dungeonPosition = (origin + dungeonRotation.RotateVec(new Vector2i(0, dungeonDistance))).Floored();

        var dungeon = profile == null
            ? _protoManager.Index<DungeonConfigPrototype>("Experiment")
            : _protoManager.Index(profile.Dungeon);
        _dungeon.GenerateDungeon(dungeon, dungeon.ID, args.MapUid, grid, dungeonPosition, seed);

        // TODO: Add dungeon-specific mobs and loot.

        // Do markers on the map.
        if (TryComp(ent.Owner, out BiomeComponent? biomeComp) && generatorComp != null)
        {
            // - Loot
            var lootLayers = profile is { LootLayers.Count: > 0 }
                ? profile.LootLayers.ToList()
                : generatorComp.LootLayers.ToList();
            var lootLayerCount = Math.Min(
                profile?.LootLayerCount ?? generatorComp.LootLayerCount,
                lootLayers.Count);

            for (var i = 0; i < lootLayerCount; i++)
            {
                var layerIdx = random.Next(lootLayers.Count);
                var layer = lootLayers[layerIdx];
                lootLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(ent.Owner, biomeComp, layer.Id);
            }

            // - Mobs
            var mobLayers = profile is { MobLayers.Count: > 0 }
                ? profile.MobLayers.ToList()
                : generatorComp.MobLayers.ToList();
            var mobLayerCount = Math.Min(
                profile?.MobLayerCount ?? generatorComp.MobLayerCount,
                mobLayers.Count);

            for (var i = 0; i < mobLayerCount; i++)
            {
                var layerIdx = random.Next(mobLayers.Count);
                var layer = mobLayers[layerIdx];
                mobLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(ent.Owner, biomeComp, layer.Id);
            }
        }
    }
}
