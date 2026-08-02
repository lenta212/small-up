using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Materials;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Cargo.Systems;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Maps;
using Content.Shared.Construction;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Content.Shared._LuaM.Materials;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMResourceStorageCrateRuntimeTest
{
    private const string SiloPrototype = "LuaMResourceCrateTestSilo";

    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: LuaMResourceCrateTestSilo
          name: resource crate test silo
          components:
          - type: Transform
          - type: OreSilo
            range: 125
          - type: MaterialStorage
            storageLimit: 2000000
        """;

    [Test]
    public async Task TransfersAreLocalAtomicPricedSealedAndPersistent()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var materials = entities.System<SharedMaterialStorageSystem>();
        var transfers = entities.System<LuaMResourceStorageCrateSystem>();
        var pricing = entities.System<PricingSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap).Owner;
                maps.SetTile(sourceGrid, entities.GetComponent<MapGridComponent>(sourceGrid), Vector2i.Zero, new Tile(1));

                var silo = entities.SpawnEntity(SiloPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.25f, 0.5f)));
                var crate = entities.SpawnEntity("LuaMResourceStorageCrate",
                    new EntityCoordinates(sourceGrid, new Vector2(0.75f, 0.5f)));
                Assert.That(materials.TryChangeMaterialAmount(silo, "Steel", 5050, localOnly: true), Is.True);
                Link(entities, silo, crate);

                Assert.That(
                    transfers.TryTransfer(crate, "Steel", 0, true,
                        LuaMResourceTransferDirection.SiloToCrate, out var allWholeSheets, out var allStatus),
                    Is.True,
                    allStatus);
                Assert.Multiple(() =>
                {
                    Assert.That(allWholeSheets, Is.EqualTo(5000));
                    Assert.That(materials.GetMaterialAmount(silo, "Steel", localOnly: true), Is.EqualTo(50),
                        "A sub-sheet remainder must stay in the silo instead of entering a crate that cannot physically drop it.");
                    Assert.That(materials.GetMaterialAmount(crate, "Steel", localOnly: true), Is.EqualTo(5000));
                });

                Assert.That(
                    transfers.TryTransfer(crate, "Steel", 0, true,
                        LuaMResourceTransferDirection.CrateToSilo, out var reset, out var resetStatus),
                    Is.True,
                    resetStatus);
                Assert.That(reset, Is.EqualTo(5000));

                Assert.That(
                    transfers.TryTransfer(crate, "Steel", 1200, false,
                        LuaMResourceTransferDirection.SiloToCrate, out var loaded, out var loadStatus),
                    Is.True,
                    loadStatus);
                Assert.That(loaded, Is.EqualTo(1200));
                Assert.Multiple(() =>
                {
                    Assert.That(materials.GetMaterialAmount(silo, "Steel", localOnly: true), Is.EqualTo(3850));
                    Assert.That(materials.GetMaterialAmount(crate, "Steel", localOnly: true), Is.EqualTo(1200));
                    Assert.That(materials.GetMaterialAmount(crate, "Steel"), Is.EqualTo(5050),
                        "Ordinary material consumers may still see the linked aggregate, while transfers use local-only balances.");
                });

                Assert.That(
                    transfers.TryTransfer(crate, "Steel", 200, false,
                        LuaMResourceTransferDirection.CrateToSilo, out var unloaded, out var unloadStatus),
                    Is.True,
                    unloadStatus);
                Assert.That(unloaded, Is.EqualTo(200));
                Assert.Multiple(() =>
                {
                    Assert.That(materials.GetMaterialAmount(silo, "Steel", localOnly: true), Is.EqualTo(4050));
                    Assert.That(materials.GetMaterialAmount(crate, "Steel", localOnly: true), Is.EqualTo(1000));
                });

                var steel = prototypes.Index<MaterialPrototype>("Steel");
                Assert.That(pricing.GetPrice(crate), Is.EqualTo(1000d + steel.Price * 1000).Within(0.001d),
                    "The crate must be worth its fixed 1000 credits plus the standard material value one-for-one.");

                var openAttempt = new StorageOpenAttemptEvent(crate, true);
                entities.EventBus.RaiseLocalEvent(crate, ref openAttempt);
                Assert.That(openAttempt.Cancelled, Is.True, "The resource transport crate must not expose normal crate storage.");

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
                var restoredCrate = RequirePrototypeOnGrid(entities, restoredGrid, "LuaMResourceStorageCrate");
                var restoredSilo = RequirePrototypeOnGrid(entities, restoredGrid, SiloPrototype);
                Assert.Multiple(() =>
                {
                    Assert.That(materials.GetMaterialAmount(restoredCrate, "Steel", localOnly: true), Is.EqualTo(1000));
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Steel", localOnly: true), Is.EqualTo(4050));
                    Assert.That(entities.GetComponent<OreSiloClientComponent>(restoredCrate).Silo,
                        Is.EqualTo(restoredSilo));
                    Assert.That(entities.GetComponent<OreSiloComponent>(restoredSilo).Clients,
                        Does.Contain(restoredCrate));
                    Assert.That(pricing.GetPrice(restoredCrate),
                        Is.EqualTo(1000d + steel.Price * 1000).Within(0.001d));
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

    [Test]
    public async Task DeconstructionDropsEveryWholeStoredSheet()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var materials = entities.System<SharedMaterialStorageSystem>();
        var stacks = entities.System<SharedStackSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var crate = entities.SpawnEntity("LuaMResourceStorageCrate",
                new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
            Assert.That(materials.TryChangeMaterialAmount(crate, "Steel", 1000, localOnly: true), Is.True);
            var existing = entities.GetEntities().ToHashSet();

            entities.EventBus.RaiseLocalEvent(crate, new MachineDeconstructedEvent());
            var dropped = entities.GetEntities()
                .Where(uid => !existing.Contains(uid) &&
                              entities.TryGetComponent<PhysicalCompositionComponent>(uid, out var composition) &&
                              composition.MaterialComposition.ContainsKey("Steel"))
                .Sum(uid => stacks.GetCount(uid));

            Assert.That(dropped, Is.EqualTo(10),
                "One thousand stored Steel units must deconstruct into ten physical steel sheets.");
        });

        await pair.CleanReturnAsync();
    }

    private static void Link(IEntityManager entities, EntityUid silo, EntityUid crate)
    {
        var message = new ToggleOreSiloClientMessage(entities.GetNetEntity(crate));
        entities.EventBus.RaiseLocalEvent(silo, message);
        Assert.That(entities.GetComponent<OreSiloClientComponent>(crate).Silo, Is.EqualTo(silo));
    }

    private static EntityUid RequirePrototypeOnGrid(IEntityManager entities, EntityUid grid, string prototype)
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
