using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Server._LuaM.ShipGen;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Unary.Components;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.Piping.Unary.Components;
using Content.Shared.NodeContainer;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShipGeneratorAtmosRuntimeTest
{
    private static readonly Vector2i[] VacuumPodTiles =
    [
        new(7, 0),
        new(5, 18),
        new(9, 18),
        new(0, 9),
        new(14, 9),
    ];

    [Test]
    public async Task GeneratedShipBuildsBoundedLayeredAtmosphereAndVacuumPods()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var atmosphere = entities.System<AtmosphereSystem>();
        var shipGenerator = entities.System<LuaMShipGeneratorSystem>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var request = new LuaMShipGenerationRequest("expedition", "medium", "atmos-runtime", string.Empty);
        var response = CreateValidBlueprint();
        var expectedVacuumTiles = response.Entities!
            .Count(entity => entity!.Kind is "thruster" or "weapon");
        var endpointRotations = response.Entities
            .Where(entity => entity!.Kind is "vent" or "scrubber" or "air_storage" or "waste_storage")
            .Select(entity => entity!.Rotation)
            .ToHashSet();
        Assert.That(endpointRotations, Is.EquivalentTo(new[] { 0, 90, 180, 270 }),
            "The hand-authored runtime fixture must exercise every supported unary endpoint rotation.");
        MapId mapId = default;
        LuaMShipGenerationResult result = default;

        await server.WaitPost(() =>
        {
            maps.CreateMap(out mapId);
            var validated = Validate(request, response);
            var build = typeof(LuaMShipGeneratorSystem).GetMethod(
                "BuildShip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(build, Is.Not.Null);

            result = (LuaMShipGenerationResult) build!.Invoke(
                shipGenerator,
                [new MapCoordinates(Vector2.Zero, mapId), request, validated])!;
            Assert.That(result.Success, Is.True, result.Message);
            AssertFixedAtmosphere(atmosphere, result.GridUid);
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(entities.EntityExists(result.GridUid), Is.True);
            Assert.That(entities.HasComponent<MapGridComponent>(result.GridUid), Is.True);
            AssertSettledAtmosphere(atmosphere, result.GridUid);

            var mapped = EntitiesOnGridByPrototype(entities, result.GridUid);

            var vacuumMarkers = mapped.GetValueOrDefault("AtmosFixBlockerMarker");
            Assert.That(vacuumMarkers, Has.Count.EqualTo(expectedVacuumTiles),
                "Every exposed thruster/weapon pod must remain marked as vacuum for future fixgridatmos runs.");
            foreach (var marker in vacuumMarkers!)
            {
                Assert.That(entities.GetComponent<TransformComponent>(marker).Anchored, Is.True,
                    $"Vacuum marker {marker} must stay anchored so future fixgridatmos runs can find it.");
                var airtight = entities.GetComponent<AirtightComponent>(marker);
                Assert.Multiple(() =>
                {
                    Assert.That(airtight.AirBlocked, Is.True,
                        $"Vacuum marker {marker} must keep its pod isolated during atmosphere processing.");
                    Assert.That(airtight.NoAirWhenFullyAirBlocked, Is.True,
                        $"Vacuum marker {marker} must keep its own pod tile airless.");
                });
            }

            var supplyPipes = RequireMapped(mapped, "GasPipeFourway");
            var wastePipes = RequireMapped(mapped, "GasPipeFourwayAlt1");
            Assert.That(supplyPipes, Is.Not.Empty);
            Assert.That(wastePipes, Is.Not.Empty);
            Assert.That(PipeLayers(entities, supplyPipes), Is.EquivalentTo(new[] { AtmosPipeLayer.Primary }));
            Assert.That(PipeLayers(entities, wastePipes), Is.EquivalentTo(new[] { AtmosPipeLayer.Secondary }));

            var ports = RequireMapped(mapped, "GasPort");
            Assert.That(ports, Has.Count.EqualTo(2));
            Assert.That(PipeLayers(entities, ports),
                Is.EquivalentTo(new[] { AtmosPipeLayer.Primary, AtmosPipeLayer.Secondary }));

            var airCanister = RequireSingleMapped(mapped, "AirCanister");
            var storageCanister = RequireSingleMapped(mapped, "StorageCanister");
            AssertFiniteCanister(entities, airCanister, "AirCanister");
            AssertFiniteCanister(entities, storageCanister, "StorageCanister");
            AssertAtmosDevicesOperational(entities, mapped);
            AssertAtmosNodeConnectivity(
                entities,
                mapped,
                supplyPipes,
                wastePipes,
                ports,
                airCanister,
                storageCanister);
        });

        // The markers are deliberately permanent. A later admin repair pass must reconstruct
        // normal cabin air while the airtight pod markers continue enforcing vacuum.
        await server.WaitPost(() =>
        {
            var emptiedCabin = atmosphere.GetTileMixture(result.GridUid, null, new Vector2i(7, 8));
            Assert.That(emptiedCabin, Is.Not.Null);
            emptiedCabin!.Clear();
            Assert.That(emptiedCabin.TotalMoles, Is.EqualTo(0f));

            console.ExecuteCommand(null, $"fixgridatmos {entities.GetNetEntity(result.GridUid)}");
            AssertFixedAtmosphere(atmosphere, result.GridUid);
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            AssertSettledAtmosphere(atmosphere, result.GridUid);
            AssertAtmosDevicesOperational(entities, EntitiesOnGridByPrototype(entities, result.GridUid));
        });

        float supplyReserveBefore = 0f;
        float wasteCarbonDioxideBefore = 0f;
        float perturbedCabinPressure = 0f;
        float injectedTileCarbonDioxide = 0f;

        // Create deficits that only the live devices can move into their respective finite
        // networks: low (but non-lockout) cabin pressure for supply, and CO2 for waste.
        await server.WaitPost(() =>
        {
            var mapped = EntitiesOnGridByPrototype(entities, result.GridUid);
            var airCanister = RequireSingleMapped(mapped, "AirCanister");
            var storageCanister = RequireSingleMapped(mapped, "StorageCanister");
            var vent = RequireMapped(mapped, "GasVentPump")[0];
            var scrubber = RequireMapped(mapped, "GasVentScrubber")[0];
            var supplyPipe = RequireNode<PipeNode>(entities, vent, "pipe", "GasVentPump").Air;
            var wastePipe = RequireNode<PipeNode>(entities, scrubber, "pipe", "GasVentScrubber").Air;
            var supplyCanister = entities.GetComponent<GasCanisterComponent>(airCanister).Air;
            var wasteCanister = entities.GetComponent<GasCanisterComponent>(storageCanister).Air;

            supplyReserveBefore = supplyCanister.TotalMoles + supplyPipe.TotalMoles;
            wasteCarbonDioxideBefore =
                wasteCanister.GetMoles(Gas.CarbonDioxide) + wastePipe.GetMoles(Gas.CarbonDioxide);

            for (var y = 1; y < 18; y++)
            {
                for (var x = 1; x < 14; x++)
                {
                    atmosphere.GetTileMixture(result.GridUid, null, new Vector2i(x, y))?.Multiply(0.85f);
                }
            }

            var ventTile = atmosphere.GetTileMixture(result.GridUid, null, new Vector2i(5, 5));
            var scrubberTile = atmosphere.GetTileMixture(result.GridUid, null, new Vector2i(9, 5));
            Assert.That(ventTile, Is.Not.Null);
            Assert.That(scrubberTile, Is.Not.Null);
            perturbedCabinPressure = ventTile!.Pressure;
            scrubberTile!.AdjustMoles(Gas.CarbonDioxide, 20f);
            injectedTileCarbonDioxide = scrubberTile.GetMoles(Gas.CarbonDioxide);
        });

        await pair.RunTicksSync(120);

        await server.WaitAssertion(() =>
        {
            var mapped = EntitiesOnGridByPrototype(entities, result.GridUid);
            var airCanister = RequireSingleMapped(mapped, "AirCanister");
            var storageCanister = RequireSingleMapped(mapped, "StorageCanister");
            var vent = RequireMapped(mapped, "GasVentPump")[0];
            var scrubber = RequireMapped(mapped, "GasVentScrubber")[0];
            var supplyPipe = RequireNode<PipeNode>(entities, vent, "pipe", "GasVentPump").Air;
            var wastePipe = RequireNode<PipeNode>(entities, scrubber, "pipe", "GasVentScrubber").Air;
            var supplyCanister = entities.GetComponent<GasCanisterComponent>(airCanister).Air;
            var wasteCanister = entities.GetComponent<GasCanisterComponent>(storageCanister).Air;
            var supplyReserveAfter = supplyCanister.TotalMoles + supplyPipe.TotalMoles;
            var wasteCarbonDioxideAfter =
                wasteCanister.GetMoles(Gas.CarbonDioxide) + wastePipe.GetMoles(Gas.CarbonDioxide);
            var ventTile = atmosphere.GetTileMixture(result.GridUid, null, new Vector2i(5, 5));
            var scrubberTile = atmosphere.GetTileMixture(result.GridUid, null, new Vector2i(9, 5));

            Assert.Multiple(() =>
            {
                Assert.That(supplyReserveAfter, Is.LessThan(supplyReserveBefore - 0.1f),
                    "Powered vents must draw a measurable amount from the finite supply reservoir.");
                Assert.That(ventTile, Is.Not.Null);
                Assert.That(ventTile!.Pressure, Is.GreaterThan(perturbedCabinPressure + 0.1f),
                    "Cabin pressure must rise after the finite supply network runs.");
                Assert.That(wasteCarbonDioxideAfter, Is.GreaterThan(wasteCarbonDioxideBefore + 0.1f),
                    "Powered scrubbers must move injected CO2 into the finite waste reservoir.");
                Assert.That(scrubberTile, Is.Not.Null);
                Assert.That(scrubberTile!.GetMoles(Gas.CarbonDioxide),
                    Is.LessThan(injectedTileCarbonDioxide - 0.1f),
                    "The scrubber tile must lose a measurable amount of injected CO2.");
            });
        });

        await server.WaitPost(() => maps.DeleteMap(mapId));
        await pair.CleanReturnAsync();
    }

    private static void AssertFixedAtmosphere(AtmosphereSystem atmosphere, EntityUid gridUid)
    {
        // BuildShip executes fixgridatmos synchronously. Check its direct result before normal
        // atmosphere processing starts redistributing pressure between adjacent ship tiles.
        AssertVacuumPods(atmosphere, gridUid);

        var cabinAir = atmosphere.GetTileMixture(gridUid, null, new Vector2i(7, 8));
        Assert.That(cabinAir, Is.Not.Null, "The sealed cabin must have a tile atmosphere after fixgridatmos.");
        Assert.Multiple(() =>
        {
            Assert.That(cabinAir!.TotalMoles, Is.GreaterThan(0f));
            Assert.That(cabinAir.Pressure,
                Is.EqualTo(Atmospherics.OneAtmosphere).Within(5f),
                "The sealed cabin must start near one atmosphere.");
        });
    }

    private static void AssertSettledAtmosphere(AtmosphereSystem atmosphere, EntityUid gridUid)
    {
        AssertVacuumPods(atmosphere, gridUid);

        var cabinAir = atmosphere.GetTileMixture(gridUid, null, new Vector2i(7, 8));
        Assert.That(cabinAir, Is.Not.Null, "The sealed cabin must retain a tile atmosphere after settling.");
        Assert.That(cabinAir!.Pressure, Is.GreaterThan(75f),
            "The generated cabin must remain pressurized after atmosphere redistribution.");
    }

    private static void AssertVacuumPods(AtmosphereSystem atmosphere, EntityUid gridUid)
    {
        foreach (var podTile in VacuumPodTiles)
        {
            var podAir = atmosphere.GetTileMixture(gridUid, null, podTile);
            Assert.That(podAir == null || podAir.TotalMoles <= 0.001f, Is.True,
                $"Exposed pod {podTile} must be vacuum after fixgridatmos, but contained {podAir?.TotalMoles} moles.");
        }
    }

    private static object Validate(
        LuaMShipGenerationRequest request,
        LuaMShipBlueprintResponse response)
    {
        var validate = typeof(LuaMShipGeneratorSystem).GetMethod(
            "TryValidateBlueprint",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(validate, Is.Not.Null);

        object[] args = [request, response, null, null];
        var accepted = (bool) validate!.Invoke(null, args)!;
        Assert.That(accepted, Is.True, args[3] as string);
        Assert.That(args[2], Is.Not.Null);
        return args[2]!;
    }

    private static Dictionary<string, List<EntityUid>> EntitiesOnGridByPrototype(
        IEntityManager entities,
        EntityUid gridUid)
    {
        var mapped = new Dictionary<string, List<EntityUid>>(StringComparer.Ordinal);
        var query = entities.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var metadata, out var transform))
        {
            if (transform.GridUid != gridUid || metadata.EntityPrototype?.ID is not { } prototype)
                continue;

            if (!mapped.TryGetValue(prototype, out var matches))
            {
                matches = new List<EntityUid>();
                mapped.Add(prototype, matches);
            }
            matches.Add(uid);
        }
        return mapped;
    }

    private static List<EntityUid> RequireMapped(
        IReadOnlyDictionary<string, List<EntityUid>> mapped,
        string prototype)
    {
        Assert.That(mapped.TryGetValue(prototype, out var matches), Is.True,
            $"Generated grid is missing prototype {prototype}.");
        return matches!;
    }

    private static EntityUid RequireSingleMapped(
        IReadOnlyDictionary<string, List<EntityUid>> mapped,
        string prototype)
    {
        var matches = RequireMapped(mapped, prototype);
        Assert.That(matches, Has.Count.EqualTo(1), $"Expected exactly one {prototype}.");
        return matches[0];
    }

    private static HashSet<AtmosPipeLayer> PipeLayers(
        IEntityManager entities,
        IEnumerable<EntityUid> uids)
    {
        return uids
            .Select(uid => entities.GetComponent<AtmosPipeLayersComponent>(uid).CurrentPipeLayer)
            .ToHashSet();
    }

    private static void AssertFiniteCanister(IEntityManager entities, EntityUid uid, string prototype)
    {
        var canister = entities.GetComponent<GasCanisterComponent>(uid);
        Assert.Multiple(() =>
        {
            Assert.That(canister.Air.Immutable, Is.False, $"{prototype} must not be an infinite gas source.");
            Assert.That(float.IsFinite(canister.Air.Volume), Is.True);
            Assert.That(canister.Air.Volume, Is.EqualTo(1500f));
            Assert.That(float.IsFinite(canister.Air.TotalMoles), Is.True);
            Assert.That(canister.Air.TotalMoles, Is.GreaterThanOrEqualTo(0f));
        });
    }

    private static void AssertAtmosDevicesOperational(
        IEntityManager entities,
        IReadOnlyDictionary<string, List<EntityUid>> mapped)
    {
        foreach (var uid in RequireMapped(mapped, "GasVentPump"))
        {
            var power = entities.GetComponent<ApcPowerReceiverComponent>(uid);
            var vent = entities.GetComponent<GasVentPumpComponent>(uid);
            Assert.Multiple(() =>
            {
                Assert.That(power.Powered, Is.True, $"GasVentPump {uid} must receive settled APC power.");
                Assert.That(power.NeedsPower, Is.True, $"GasVentPump {uid} must use its APC receiver.");
                Assert.That(power.Provider, Is.Not.Null, $"GasVentPump {uid} must be wired to an APC provider.");
                Assert.That(vent.Enabled, Is.True, $"GasVentPump {uid} must start enabled.");
                Assert.That(vent.PumpDirection, Is.EqualTo(VentPumpDirection.Releasing),
                    $"GasVentPump {uid} must start in releasing mode.");
            });
        }

        foreach (var uid in RequireMapped(mapped, "GasVentScrubber"))
        {
            var power = entities.GetComponent<ApcPowerReceiverComponent>(uid);
            var scrubber = entities.GetComponent<GasVentScrubberComponent>(uid);
            Assert.Multiple(() =>
            {
                Assert.That(power.Powered, Is.True, $"GasVentScrubber {uid} must receive settled APC power.");
                Assert.That(power.NeedsPower, Is.True, $"GasVentScrubber {uid} must use its APC receiver.");
                Assert.That(power.Provider, Is.Not.Null, $"GasVentScrubber {uid} must be wired to an APC provider.");
                Assert.That(scrubber.Enabled, Is.True, $"GasVentScrubber {uid} must start enabled.");
                Assert.That(scrubber.PumpDirection, Is.EqualTo(ScrubberPumpDirection.Scrubbing),
                    $"GasVentScrubber {uid} must start in scrubbing mode.");
                Assert.That(scrubber.FilterGases, Is.EquivalentTo(GasVentScrubberData.DefaultFilterGases),
                    $"GasVentScrubber {uid} must start with the trusted default gas filter set.");
            });
        }
    }

    private static void AssertAtmosNodeConnectivity(
        IEntityManager entities,
        IReadOnlyDictionary<string, List<EntityUid>> mapped,
        IReadOnlyCollection<EntityUid> supplyPipes,
        IReadOnlyCollection<EntityUid> wastePipes,
        IReadOnlyCollection<EntityUid> ports,
        EntityUid airCanister,
        EntityUid storageCanister)
    {
        var supplyPort = ports.Single(uid =>
            entities.GetComponent<AtmosPipeLayersComponent>(uid).CurrentPipeLayer == AtmosPipeLayer.Primary);
        var wastePort = ports.Single(uid =>
            entities.GetComponent<AtmosPipeLayersComponent>(uid).CurrentPipeLayer == AtmosPipeLayer.Secondary);

        var supplyPortNode = RequireNode<PortPipeNode>(entities, supplyPort, "connected", "primary GasPort");
        var wastePortNode = RequireNode<PortPipeNode>(entities, wastePort, "connected", "secondary GasPort");
        var airPortableNode = RequireNode<PortablePipeNode>(entities, airCanister, "port", "AirCanister");
        var wastePortableNode = RequireNode<PortablePipeNode>(entities, storageCanister, "port", "StorageCanister");

        Assert.Multiple(() =>
        {
            Assert.That(supplyPortNode.NodeGroup, Is.Not.Null,
                "The primary GasPort must have joined a live supply pipe network.");
            Assert.That(wastePortNode.NodeGroup, Is.Not.Null,
                "The secondary GasPort must have joined a live waste pipe network.");
            Assert.That(wastePortNode.NodeGroup, Is.Not.SameAs(supplyPortNode.NodeGroup),
                "Supply and waste endpoints must resolve to distinct NodeContainer networks.");
            Assert.That(supplyPortNode.ReachableNodes.Contains(airPortableNode), Is.True,
                "The primary GasPort must directly reach the colocated AirCanister portable node.");
            Assert.That(airPortableNode.ReachableNodes.Contains(supplyPortNode), Is.True,
                "The AirCanister portable node must directly reach the primary GasPort.");
            Assert.That(wastePortNode.ReachableNodes.Contains(wastePortableNode), Is.True,
                "The secondary GasPort must directly reach the colocated StorageCanister portable node.");
            Assert.That(wastePortableNode.ReachableNodes.Contains(wastePortNode), Is.True,
                "The StorageCanister portable node must directly reach the secondary GasPort.");
        });

        AssertSharesNodeGroup(airPortableNode, supplyPortNode,
            "AirCanister must belong to the same supply network as the primary GasPort.");
        AssertSharesNodeGroup(wastePortableNode, wastePortNode,
            "StorageCanister must belong to the same waste network as the secondary GasPort.");

        foreach (var vent in RequireMapped(mapped, "GasVentPump"))
        {
            var node = RequireNode<PipeNode>(entities, vent, "pipe", "GasVentPump");
            Assert.That(node.CurrentPipeLayer, Is.EqualTo(AtmosPipeLayer.Primary));
            AssertSharesNodeGroup(node, supplyPortNode,
                $"GasVentPump {vent} is disconnected from the finite supply network.");
        }

        foreach (var scrubber in RequireMapped(mapped, "GasVentScrubber"))
        {
            var node = RequireNode<PipeNode>(entities, scrubber, "pipe", "GasVentScrubber");
            Assert.That(node.CurrentPipeLayer, Is.EqualTo(AtmosPipeLayer.Secondary));
            AssertSharesNodeGroup(node, wastePortNode,
                $"GasVentScrubber {scrubber} is disconnected from the finite waste network.");
        }

        foreach (var pipe in supplyPipes)
        {
            var node = RequireNode<PipeNode>(entities, pipe, "pipe", "GasPipeFourway");
            AssertSharesNodeGroup(node, supplyPortNode,
                $"Supply pipe {pipe} is outside the primary supply network.");
        }

        foreach (var pipe in wastePipes)
        {
            var node = RequireNode<PipeNode>(entities, pipe, "pipe", "GasPipeFourwayAlt1");
            AssertSharesNodeGroup(node, wastePortNode,
                $"Waste pipe {pipe} is outside the secondary waste network.");
        }
    }

    private static T RequireNode<T>(
        IEntityManager entities,
        EntityUid uid,
        string name,
        string label)
        where T : Node
    {
        var container = entities.GetComponent<NodeContainerComponent>(uid);
        Assert.That(container.Nodes.TryGetValue(name, out var node), Is.True,
            $"{label} is missing its expected NodeContainer node '{name}'.");
        Assert.That(node, Is.TypeOf<T>(),
            $"{label} node '{name}' must be a {typeof(T).Name}.");
        return (T) node!;
    }

    private static void AssertSharesNodeGroup(Node node, Node networkAnchor, string message)
    {
        Assert.That(node.NodeGroup, Is.Not.Null, message);
        Assert.That(node.NodeGroup, Is.SameAs(networkAnchor.NodeGroup), message);
    }

    private static LuaMShipBlueprintResponse CreateValidBlueprint()
    {
        const int width = 15;
        const int height = 19;
        var tiles = new List<LuaMShipTile>();
        var entities = new List<LuaMShipEntity>();
        var podTiles = new HashSet<(int X, int Y)>
        {
            (7, 0),
            (5, height - 1),
            (9, height - 1),
            (0, 9),
            (width - 1, 9),
        };

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                tiles.Add(new LuaMShipTile { X = x, Y = y });
                var boundary = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (!boundary || podTiles.Contains((x, y)))
                    continue;

                var kind = (x, y) switch
                {
                    (6, height - 1) => "airlock",
                    (7, height - 1) => "dock",
                    _ => "wall",
                };
                entities.Add(Entity(kind, x, y, kind == "dock" ? 180 : 0));
            }
        }

        entities.AddRange(
        [
            Entity("bulkhead", 7, 1),
            Entity("bulkhead", 5, 17),
            Entity("bulkhead", 9, 17),
            Entity("bulkhead", 1, 9),
            Entity("bulkhead", 13, 9),
            Entity("thruster", 7, 0, 180),
            Entity("thruster", 5, 18, 0),
            Entity("thruster", 9, 18, 0),
            Entity("thruster", 0, 9, 90),
            Entity("thruster", 14, 9, 270),
            Entity("apu", 3, 2),
            Entity("apu", 4, 2),
            Entity("apu", 3, 3),
            Entity("apc", 4, 3),
            Entity("substation", 3, 12),
            Entity("gyro", 11, 12),
            Entity("console", 7, 16),
            Entity("medical", 4, 9),
            Entity("science", 7, 9),
            Entity("salvage", 6, 12),
            Entity("research_server", 10, 9),
            Entity("air_storage", 2, 5, 90),
            Entity("vent", 5, 5, 270),
            Entity("vent", 5, 7, 0),
            Entity("waste_storage", 12, 5, 270),
            Entity("scrubber", 9, 5, 90),
            Entity("scrubber", 9, 7, 180),
        ]);

        return new LuaMShipBlueprintResponse
        {
            SchemaVersion = 1,
            GeneratorVersion = "atmos-runtime-test",
            Seed = "atmos-runtime",
            Name = "Atmos runtime test",
            Preset = "expedition",
            Width = width,
            Height = height,
            Tiles = tiles,
            Entities = entities,
            Summary = JsonSerializer.SerializeToElement(new { tiles = tiles.Count, entities = entities.Count }),
            Preview = JsonSerializer.SerializeToElement(Enumerable.Repeat(new string('#', width), height).ToArray()),
        };
    }

    private static LuaMShipEntity Entity(string kind, int x, int y, int rotation = 0)
    {
        return new LuaMShipEntity
        {
            Kind = kind,
            X = x,
            Y = y,
            Rotation = rotation,
        };
    }
}
