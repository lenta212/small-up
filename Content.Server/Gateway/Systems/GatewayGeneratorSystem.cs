using System.Linq;
using Content.Server.Gateway.Components;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural;
using Content.Shared.Salvage;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
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
    [Dependency] private BiomeSystem _biome = default!;
    [Dependency] private DungeonSystem _dungeon = default!;
    [Dependency] private GatewaySystem _gateway = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedSalvageSystem _salvage = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TileSystem _tile = default!;

    [ValidatePrototypeId<LocalizedDatasetPrototype>]
    private const string PlanetNames = "NamesBorer";

    private const int InitialDestinationCount = 3;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private TimeSpan _nextCleanup;

    // TODO:
    // Fix shader some more
    // Show these in UI
    // Use regular mobs for thingo.

    // Use salvage mission params
    // Add the funny song
    // Put salvage params in the UI

    // Re-use salvage config stuff for the RNG
    // Have it in the UI like expeditions.

    // Also add weather coz it's funny.

    // Add songs (incl. the downloaded one) to the ambient music playlist for planet probably.
    // Copy most of salvage mission spawner

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
    /// Deletes expired unopened destinations, removes dead references and enforces the configured hard cap.
    /// Loaded destinations are deliberately retained because players or property may still be on them.
    /// </summary>
    internal int CleanupExpiredDestinations(EntityUid uid, GatewayGeneratorComponent generator)
    {
        var removed = 0;
        var ttlSeconds = _cfgManager.GetCVar(CCVars.GatewayGeneratorDestinationTtl);
        var ttl = float.IsFinite(ttlSeconds) && ttlSeconds > 0f
            ? TimeSpan.FromSeconds(Math.Min(ttlSeconds, TimeSpan.MaxValue.TotalSeconds / 2d))
            : TimeSpan.Zero;

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

            if (destination.Loaded || ttl == TimeSpan.Zero)
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

        if (removed > 0)
            _gateway.UpdateAllGateways();

        return removed;
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

        var tileDef = _tileDefManager["FloorSteel"];
        const int MaxOffset = 256;
        var tiles = new List<(Vector2i Index, Tile Tile)>();
        var seed = _random.Next();
        var random = new Random(seed);
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

        _biome.EnsurePlanet(mapUid, _protoManager.Index<BiomeTemplatePrototype>("Continental"), seed);

        var grid = Comp<MapGridComponent>(mapUid);

        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                tiles.Add((new Vector2i(x, y) + origin, new Tile(tileDef.TileId, variant: _tile.PickVariant((ContentTileDefinition) tileDef, random))));
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

        // Create the gateway.
        var gatewayUid = SpawnAtPosition(generator.Proto, originCoords);
        var gatewayComp = Comp<GatewayComponent>(gatewayUid);
        _gateway.SetDestinationName(gatewayUid, FormattedMessage.FromMarkupOrThrow($"[color=#D381C996]{gatewayName}[/color]"), gatewayComp);
        _gateway.SetEnabled(gatewayUid, true, gatewayComp);
        generator.Generated.Add(mapUid);
        return true;
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
        var dungeonDistance = random.Next(3, 6);
        var dungeonRotation = _dungeon.GetDungeonRotation(seed);
        var dungeonPosition = (origin + dungeonRotation.RotateVec(new Vector2i(0, dungeonDistance))).Floored();

        _dungeon.GenerateDungeon(_protoManager.Index<DungeonConfigPrototype>("Experiment"), "Experiment", args.MapUid, grid, dungeonPosition, seed); // Frontier: added "Experiment"

        // TODO: Dungeon mobs + loot.

        // Do markers on the map.
        if (TryComp(ent.Owner, out BiomeComponent? biomeComp) && generatorComp != null)
        {
            // - Loot
            var lootLayers = generatorComp.LootLayers.ToList();

            for (var i = 0; i < generatorComp.LootLayerCount; i++)
            {
                var layerIdx = random.Next(lootLayers.Count);
                var layer = lootLayers[layerIdx];
                lootLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(ent.Owner, biomeComp, layer.Id);
            }

            // - Mobs
            var mobLayers = generatorComp.MobLayers.ToList();

            for (var i = 0; i < generatorComp.MobLayerCount; i++)
            {
                var layerIdx = random.Next(mobLayers.Count);
                var layer = mobLayers[layerIdx];
                mobLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(ent.Owner, biomeComp, layer.Id);
            }
        }
    }
}
