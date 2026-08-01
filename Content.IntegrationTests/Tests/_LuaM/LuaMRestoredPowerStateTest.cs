using System.Collections.Generic;
using System.Numerics;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Power.Components;
using Content.Shared.Maps;
using Content.Shared.Power.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMRestoredPowerStateTest
{
    private const string FixturePrototype = "LuaMRestoredPowerStateFixture";
    private const string FiniteFixturePrototype = "LuaMRestoredPowerStateFiniteFixture";

    [TestPrototypes]
    private const string TestPrototypes = @"
- type: entity
  id: LuaMRestoredPowerStateFixture
  components:
  - type: Transform
    anchored: true
  - type: Battery
    maxCharge: 100
    startingCharge: NaN
  - type: PowerNetworkBattery
    supplyRampPosition: NaN
    currentSupply: NaN
    currentReceiving: NaN
    loadingNetworkDemand: NaN
  - type: ApcPowerReceiver
  - type: PowerCharge
    maxCharge: 1
    charge: NaN

- type: entity
  id: LuaMRestoredPowerStateFiniteFixture
  components:
  - type: Transform
    anchored: true
  - type: Battery
    maxCharge: 100
    startingCharge: 50
  - type: PowerNetworkBattery
    supplyRampPosition: 10
    currentSupply: 20
    currentReceiving: 30
    loadingNetworkDemand: 40
  - type: ApcPowerReceiver
  - type: PowerCharge
    maxCharge: 1
    charge: 0.5
";

    [Test]
    public async Task RestoreRepairsNonFinitePowerState()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap);
                maps.SetTile(sourceGrid.Owner, sourceGrid.Comp, Vector2i.Zero, new Tile(1));
                var sourceFixture = entities.SpawnEntity(
                    FixturePrototype,
                    new EntityCoordinates(sourceGrid.Owner, new Vector2(0.5f, 0.5f)));
                entities.SpawnEntity(
                    FiniteFixturePrototype,
                    new EntityCoordinates(sourceGrid.Owner, new Vector2(0.75f, 0.5f)));

                Assert.Multiple(() =>
                {
                    Assert.That(float.IsNaN(entities.GetComponent<BatteryComponent>(sourceFixture).CurrentCharge), Is.True);
                    Assert.That(
                        float.IsNaN(entities.GetComponent<PowerNetworkBatteryComponent>(sourceFixture).CurrentSupply),
                        Is.True);
                    Assert.That(float.IsNaN(entities.GetComponent<PowerChargeComponent>(sourceFixture).Charge), Is.True);
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid.Owner, 1, out var poisonedSnapshot, out var captureReason),
                    Is.True,
                    captureReason);

                maps.DeleteMap(sourceMap);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(poisonedSnapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restored = RequirePrototypeDescendant(restoredGrid, FixturePrototype);
                var battery = entities.GetComponent<BatteryComponent>(restored);
                var network = entities.GetComponent<PowerNetworkBatteryComponent>(restored).NetworkBattery;
                var charge = entities.GetComponent<PowerChargeComponent>(restored);
                var restoredFinite = RequirePrototypeDescendant(restoredGrid, FiniteFixturePrototype);
                var finiteBattery = entities.GetComponent<BatteryComponent>(restoredFinite);
                var finiteNetwork = entities.GetComponent<PowerNetworkBatteryComponent>(restoredFinite).NetworkBattery;
                var finiteCharge = entities.GetComponent<PowerChargeComponent>(restoredFinite);
                Assert.Multiple(() =>
                {
                    Assert.That(battery.CurrentCharge, Is.Zero);
                    Assert.That(network.CurrentStorage, Is.Zero);
                    Assert.That(network.SupplyRampPosition, Is.Zero);
                    Assert.That(network.CurrentSupply, Is.Zero);
                    Assert.That(network.CurrentReceiving, Is.Zero);
                    Assert.That(network.LoadingNetworkDemand, Is.Zero);
                    Assert.That(charge.Charge, Is.Zero);
                    Assert.That(charge.Active, Is.False);
                    Assert.That(charge.NeedUIUpdate, Is.True);
                    Assert.That(finiteBattery.CurrentCharge, Is.EqualTo(50f));
                    Assert.That(finiteNetwork.SupplyRampPosition, Is.EqualTo(10f));
                    Assert.That(finiteNetwork.CurrentSupply, Is.EqualTo(20f));
                    Assert.That(finiteNetwork.CurrentReceiving, Is.EqualTo(30f));
                    Assert.That(finiteNetwork.LoadingNetworkDemand, Is.EqualTo(40f));
                    Assert.That(finiteCharge.Charge, Is.EqualTo(0.5f));
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

        EntityUid RequirePrototypeDescendant(EntityUid root, string prototypeId)
        {
            var pending = new Stack<EntityUid>();
            var visited = new HashSet<EntityUid>();
            pending.Push(root);
            while (pending.TryPop(out var uid))
            {
                if (!visited.Add(uid) || !entities.EntityExists(uid))
                    continue;
                if (entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototypeId)
                    return uid;
                var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    pending.Push(child);
            }

            Assert.Fail($"Could not find restored descendant with prototype '{prototypeId}'.");
            return EntityUid.Invalid;
        }
    }
}
