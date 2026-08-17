using System.Numerics;
using System.Linq;
using Content.Server.GameTicking.Events;
using Content.Server._NF.GameRule;
using Content.Server.Shuttles.Components;
using Content.Server.Worldgen.Prototypes;
using Content.Shared.GameTicking;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._LuaM.AsteroidBelt;

public sealed class LuaMAsteroidBeltSystem : EntitySystem
{
    public const string DiskPrototype = "LuaMCriticalAsteroidBeltCoordinatesDisk";
    public const string WorldgenConfig = "LuaMCriticalAsteroidBelt";

    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly PointOfInterestSystem _poi = default!;

    private EntityUid? _beltMap;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<LuaMAsteroidBeltCoordinatesDiskComponent, ComponentStartup>(OnDiskStartup);
        SubscribeLocalEvent<LuaMAsteroidBeltMapComponent, EntityTerminatingEvent>(OnMapTerminating);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        EnsureWorld();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _beltMap = null;
    }

    private void OnMapTerminating(
        Entity<LuaMAsteroidBeltMapComponent> entity,
        ref EntityTerminatingEvent args)
    {
        if (_beltMap == entity.Owner)
            _beltMap = null;
    }

    private void OnDiskStartup(
        Entity<LuaMAsteroidBeltCoordinatesDiskComponent> entity,
        ref ComponentStartup args)
    {
        if (_beltMap is { } map && Exists(map))
            SetDiskDestination(entity.Owner, map);
    }

    public EntityUid EnsureWorld()
    {
        if (_beltMap is { } existing && Exists(existing))
        {
            AssignAllDisks(existing);
            return existing;
        }

        var map = _maps.CreateMap(out var mapId, runMapInit: false);
        _metadata.SetEntityName(map, Loc.GetString("luam-asteroid-belt-map-name"));

        var config = _prototypes.Index<WorldgenConfigPrototype>(WorldgenConfig);
        config.Apply(map, _serialization, EntityManager);

        var destination = EnsureComp<FTLDestinationComponent>(map);
        destination.Enabled = true;
        destination.BeaconsOnly = false;
        destination.RequireCoordinateDisk = true;

        var marker = EnsureComp<LuaMAsteroidBeltMapComponent>(map);
        _maps.InitializeMap(map);

        var beacon = Spawn("FTLPoint", new EntityCoordinates(map, Vector2.Zero));
        _metadata.SetEntityName(beacon, Loc.GetString("luam-asteroid-belt-entry-name"));
        marker.EntryBeacon = beacon;

        var beltPois = _prototypes
            .EnumeratePrototypes<PointOfInterestPrototype>()
            .Where(prototype => prototype.AsteroidBelt)
            .ToList();
        // The belt is one shared world for every round, independent of the
        // active game preset. Its fixed POI set must spawn even for presets
        // that are not listed in the main-sector SpawnGamePreset whitelists.
        _poi.GenerateRequireds(mapId, beltPois, out _, ignorePresetFilter: true);

        // Station post-init re-registers every station grid's map as an ordinary
        // free FTL destination, which clears the belt's coordinate-disk lock.
        // Re-assert the locked destination after all belt POIs have loaded.
        destination.Enabled = true;
        destination.BeaconsOnly = false;
        destination.RequireCoordinateDisk = true;

        _beltMap = map;
        AssignAllDisks(map);
        return map;
    }

    private void AssignAllDisks(EntityUid map)
    {
        var query = EntityQueryEnumerator<LuaMAsteroidBeltCoordinatesDiskComponent>();
        while (query.MoveNext(out var disk, out _))
        {
            SetDiskDestination(disk, map);
        }
    }

    private void SetDiskDestination(EntityUid disk, EntityUid map)
    {
        var coordinates = EnsureComp<ShuttleDestinationCoordinatesComponent>(disk);
        if (coordinates.Destination == map)
            return;

        coordinates.Destination = map;
        Dirty(disk, coordinates);
    }
}
