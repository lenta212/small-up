using System.Linq;
using System.Numerics;
using Content.Server._LuaM.ShipPersistence;
using Content.Shared.Maps;
using Content.Shared.SmartFridge;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMSmartStorageShipPersistenceRuntimeTest
{
    [Test]
    public async Task FilledSmartArmoryRoundTripsPhysicalContentsAndRebuildsUiIndex()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap).Owner;
                maps.SetTile(sourceGrid, entities.GetComponent<MapGridComponent>(sourceGrid), Vector2i.Zero, new Tile(1));

                var armory = entities.SpawnEntity(
                    "SmartArmoryStorage",
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                var weapon = entities.SpawnEntity(
                    "WeaponPistolMk58",
                    new EntityCoordinates(sourceGrid, new Vector2(0.75f, 0.5f)));
                var smart = entities.GetComponent<SmartFridgeComponent>(armory);
                Assert.That(containers.TryGetContainer(armory, smart.Container, out var inventory), Is.True);
                Assert.That(containers.Insert(weapon, inventory!), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(inventory!.ContainedEntities, Is.EqualTo(new[] { weapon }));
                    Assert.That(smart.ContainedEntries.Values.SelectMany(set => set).Count(), Is.EqualTo(1));
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                entities.DeleteEntity(sourceGrid);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredArmory = RequirePrototypeOnGrid(entities, restoredGrid, "SmartArmoryStorage");
                var restoredSmart = entities.GetComponent<SmartFridgeComponent>(restoredArmory);
                Assert.That(
                    containers.TryGetContainer(restoredArmory, restoredSmart.Container, out var restoredInventory),
                    Is.True);
                Assert.That(restoredInventory!.ContainedEntities, Has.Count.EqualTo(1));
                var restoredWeapon = restoredInventory.ContainedEntities.Single();

                Assert.Multiple(() =>
                {
                    Assert.That(
                        entities.GetComponent<MetaDataComponent>(restoredWeapon).EntityPrototype?.ID,
                        Is.EqualTo("WeaponPistolMk58"));
                    Assert.That(restoredSmart.Entries, Has.Count.EqualTo(1));
                    Assert.That(restoredSmart.ContainedEntries.Values.SelectMany(set => set),
                        Is.EquivalentTo(new[] { entities.GetNetEntity(restoredWeapon) }));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });
            pair.Kill();
        }
    }

    private static EntityUid RequirePrototypeOnGrid(
        IEntityManager entities,
        EntityUid grid,
        string prototype)
    {
        foreach (var uid in entities.GetEntities())
        {
            if (!entities.TryGetComponent<TransformComponent>(uid, out var xform) ||
                xform.GridUid != grid ||
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID != prototype)
            {
                continue;
            }

            return uid;
        }

        Assert.Fail($"Prototype {prototype} was not restored on grid {grid}.");
        return EntityUid.Invalid;
    }
}
