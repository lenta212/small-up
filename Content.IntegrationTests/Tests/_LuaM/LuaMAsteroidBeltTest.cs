using System.Linq;
using System.Numerics;
using Content.Server._LuaM.AsteroidBelt;
using Content.Server.Shuttles.Components;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Prototypes;
using Content.Shared.Parallax;
using Content.Shared.Shuttles.Components;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMAsteroidBeltTest
{
    [Test]
    public async Task EveryDiskLeadsToOneDenseCoordinateLockedWorld()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var system = entities.System<LuaMAsteroidBeltSystem>();
            var map = system.EnsureWorld();
            var secondEnsure = system.EnsureWorld();
            var diskA = entities.SpawnEntity(
                LuaMAsteroidBeltSystem.DiskPrototype,
                new EntityCoordinates(map, Vector2.Zero));
            var diskB = entities.SpawnEntity(
                LuaMAsteroidBeltSystem.DiskPrototype,
                new EntityCoordinates(map, Vector2.One));

            var mapComponent = entities.GetComponent<MapComponent>(map);
            var mapMarker = entities.GetComponent<LuaMAsteroidBeltMapComponent>(map);
            var selection = entities.GetComponent<BiomeSelectionComponent>(map);
            var destination = entities.GetComponent<FTLDestinationComponent>(map);
            var parallax = entities.GetComponent<ParallaxComponent>(map);
            var diskCoordinatesA = entities.GetComponent<ShuttleDestinationCoordinatesComponent>(diskA);
            var diskCoordinatesB = entities.GetComponent<ShuttleDestinationCoordinatesComponent>(diskB);
            var biome = prototypes.Index<BiomePrototype>(LuaMAsteroidBeltSystem.WorldgenConfig);
            var placer = (DebrisFeaturePlacerControllerComponent)
                biome.ChunkComponents[nameof(DebrisFeaturePlacerControllerComponent)
                    .Replace("Component", string.Empty)].Component;
            var poiInventory = prototypes.Index<VendingMachineInventoryPrototype>("NFSalvageEquipmentPOIInventory");
            var stationInventory = prototypes.Index<VendingMachineInventoryPrototype>("NFSalvageEquipmentInventory");

            Assert.Multiple(() =>
            {
                Assert.That(secondEnsure, Is.EqualTo(map));
                Assert.That(
                    entities.EntityQuery<LuaMAsteroidBeltMapComponent>().Count(),
                    Is.EqualTo(1),
                    "The round must contain exactly one shared asteroid-belt world.");
                Assert.That(mapComponent.MapId, Is.Not.EqualTo(MapId.Nullspace));
                Assert.That(entities.HasComponent<WorldControllerComponent>(map), Is.True);
                Assert.That(selection.Biomes, Is.EqualTo(new[] { LuaMAsteroidBeltSystem.WorldgenConfig }));
                Assert.That(destination.Enabled, Is.True);
                Assert.That(destination.RequireCoordinateDisk, Is.True);
                Assert.That(destination.BeaconsOnly, Is.False);
                Assert.That(parallax.Parallax, Is.EqualTo("FrontierStation"));
                Assert.That(diskCoordinatesA.Destination, Is.EqualTo(map));
                Assert.That(diskCoordinatesB.Destination, Is.EqualTo(map));
                Assert.That(biome.DistanceRange, Is.Null);
                Assert.That(placer.DensityNoiseChannel, Is.EqualTo("DensityUnclipped"));
                Assert.That(placer.RandomCancellationChance, Is.EqualTo(0.35f));
                Assert.That(
                    poiInventory.StartingInventory.ContainsKey(LuaMAsteroidBeltSystem.DiskPrototype),
                    Is.True,
                    "Station salvage vendors must sell the asteroid-belt disk.");
                Assert.That(
                    stationInventory.StartingInventory.ContainsKey(LuaMAsteroidBeltSystem.DiskPrototype),
                    Is.True,
                    "The ordinary station salvage vendor must also sell the asteroid-belt disk.");
            });

            Assert.That(mapMarker.EntryBeacon, Is.Not.Null);
            var beacon = mapMarker.EntryBeacon!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<FTLBeaconComponent>(beacon), Is.True);
                Assert.That(entities.GetComponent<TransformComponent>(beacon).MapUid, Is.EqualTo(map));
            });

            var gridsOnBelt = 0;
            var gridQuery = entities.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out _, out _, out var gridXform))
            {
                if (gridXform.MapUid == map)
                    gridsOnBelt++;
            }

            Assert.That(
                gridsOnBelt,
                Is.GreaterThan(0),
                "EnsureWorld must generate the shared asteroid-belt locations regardless of the active preset.");
        });

        await pair.CleanReturnAsync();
    }
}
