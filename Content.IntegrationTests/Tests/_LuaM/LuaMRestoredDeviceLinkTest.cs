using System.Collections.Generic;
using System.Numerics;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Atmos.Piping.Binary.Components;
using Content.Server.DeviceLinking.Systems;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMRestoredDeviceLinkTest
{
    [Test]
    public async Task RestoredButtonInvokesLinkedValve()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var deviceLinks = entities.System<DeviceLinkSystem>();
        var deviceNetworks = entities.System<DeviceNetworkSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid restoredButton = default;
        EntityUid restoredValve = default;
        bool valveWasOpen = default;
        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var grid = mapManager.CreateGridEntity(sourceMap);
                maps.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));

                var button = entities.SpawnEntity(
                    "SignalButton",
                    new EntityCoordinates(grid.Owner, new Vector2(0.5f, 0.5f)));
                var valve = entities.SpawnEntity(
                    "SignalControlledValve",
                    new EntityCoordinates(grid.Owner, new Vector2(0.75f, 0.5f)));

                var sourceComp = entities.GetComponent<DeviceLinkSourceComponent>(button);
                var sinkComp = entities.GetComponent<DeviceLinkSinkComponent>(valve);
                deviceLinks.SaveLinks(
                    null,
                    button,
                    valve,
                    [("Pressed", "Toggle")],
                    sourceComp,
                    sinkComp);

                Assert.Multiple(() =>
                {
                    Assert.That(deviceLinks.GetLinks(button, valve).Count, Is.GreaterThan(0),
                        "link must exist before capture");
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(grid.Owner, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                maps.DeleteMap(sourceMap);
                sourceMap = default;
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                restoredButton = RequirePrototypeDescendant(restoredGrid, "SignalButton");
                restoredValve = RequirePrototypeDescendant(restoredGrid, "SignalControlledValve");

                Assert.Multiple(() =>
                {
                    Assert.That(deviceLinks.GetLinks(restoredButton, restoredValve).Count, Is.GreaterThan(0),
                        "link must survive restore");
                    Assert.That(
                        deviceNetworks.IsDeviceConnected(
                            restoredButton,
                            entities.GetComponent<DeviceNetworkComponent>(restoredButton)),
                        Is.True,
                        "button must rejoin the wireless net after restore");
                    Assert.That(
                        deviceNetworks.IsDeviceConnected(
                            restoredValve,
                            entities.GetComponent<DeviceNetworkComponent>(restoredValve)),
                        Is.True,
                        "valve must rejoin the wireless net after restore");
                });

                valveWasOpen = entities.GetComponent<GasValveComponent>(restoredValve).Open;
            });

            await server.WaitPost(() =>
            {
                deviceLinks.InvokePort(restoredButton, "Pressed");
            });

            // Wireless packets are processed on the following tick.
            await server.WaitRunTicks(2);

            await server.WaitPost(() =>
            {
                var valveComp = entities.GetComponent<GasValveComponent>(restoredValve);
                Assert.That(valveComp.Open, Is.Not.EqualTo(valveWasOpen),
                    "valve must react to the restored button");
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
