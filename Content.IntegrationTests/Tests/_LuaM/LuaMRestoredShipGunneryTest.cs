using System.Collections.Generic;
using System.Numerics;
using Content.Server._LuaM.ShipPersistence;
using Content.Server._Mono.FireControl;
using Content.Server.Power.Components;
using Content.Shared._Mono.FireControl;
using Content.Shared.Maps;
using Content.Shared.Power;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMRestoredShipGunneryTest
{
    [Test]
    public async Task RestoredShipRebuildsGunneryLinksWhenConsolePowersBeforeServer()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var fireControl = entities.System<FireControlSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid firedRestoredGun = EntityUid.Invalid;
        var ammoBefore = 0;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap);
                for (var x = 0; x < 5; x++)
                    maps.SetTile(sourceGrid.Owner, sourceGrid.Comp, new Vector2i(x, 0), new Tile(1));

                // Spawn the console first to model the ordering that exposes the mid-round
                // power-event race after a saved map is loaded.
                var sourceConsole = SpawnAnchored(
                    "ComputerGunneryConsole",
                    sourceGrid.Owner,
                    new Vector2(0.5f, 0.5f));
                var sourceGun = SpawnAnchored(
                    "ShuttleGunKinetic",
                    sourceGrid.Owner,
                    new Vector2(1.5f, 0.5f));
                var sourceServer = SpawnAnchored(
                    "GunneryServerLow",
                    sourceGrid.Owner,
                    new Vector2(2.5f, 0.5f));

                PowerOn(sourceServer);
                PowerOn(sourceGun);
                PowerOn(sourceConsole);

                var sourceServerComponent = entities.GetComponent<FireControlServerComponent>(sourceServer);
                Assert.Multiple(() =>
                {
                    Assert.That(sourceServerComponent.ConnectedGrid, Is.EqualTo(sourceGrid.Owner));
                    Assert.That(sourceServerComponent.Controlled, Contains.Item(sourceGun));
                    Assert.That(sourceServerComponent.Consoles, Contains.Item(sourceConsole));
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(
                        sourceGrid.Owner,
                        1,
                        out var snapshot,
                        out var captureReason),
                    Is.True,
                    captureReason);

                maps.DeleteMap(sourceMap);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(
                        snapshot,
                        targetMap,
                        out var restoredGrid,
                        out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredConsole = RequirePrototypeDescendant(restoredGrid, "ComputerGunneryConsole");
                var restoredGun = RequirePrototypeDescendant(restoredGrid, "ShuttleGunKinetic");
                var restoredServer = RequirePrototypeDescendant(restoredGrid, "GunneryServerLow");
                var restoredGridControl = entities.EnsureComponent<FireControlGridComponent>(restoredGrid);
                var restoredConsoleComponent = entities.GetComponent<FireControlConsoleComponent>(restoredConsole);
                var restoredGunComponent = entities.GetComponent<FireControllableComponent>(restoredGun);
                var restoredServerComponent = entities.GetComponent<FireControlServerComponent>(restoredServer);

                // Recreate the runtime-only baseline, then raise the exact order that used to
                // strand a console until a player manually pressed refresh.
                restoredGridControl.ControllingServer = null;
                restoredConsoleComponent.ConnectedServer = null;
                restoredGunComponent.ControllingServer = null;
                restoredServerComponent.ConnectedGrid = null;
                restoredServerComponent.Controlled.Clear();
                restoredServerComponent.Consoles.Clear();

                PowerOn(restoredConsole);
                PowerOn(restoredGun);
                PowerOn(restoredServer);

                Assert.Multiple(() =>
                {
                    Assert.That(restoredGridControl.ControllingServer, Is.EqualTo(restoredServer));
                    Assert.That(restoredServerComponent.ConnectedGrid, Is.EqualTo(restoredGrid));
                    Assert.That(restoredGunComponent.ControllingServer, Is.EqualTo(restoredServer));
                    Assert.That(restoredServerComponent.Controlled, Contains.Item(restoredGun));
                    Assert.That(restoredConsoleComponent.ConnectedServer, Is.EqualTo(restoredServer),
                        "A console powered before its GCS must be retried when that server claims the restored grid.");
                    Assert.That(restoredServerComponent.Consoles, Contains.Item(restoredConsole));
                });

                Assert.That(
                    entities.RemoveComponent<RechargeBasicEntityAmmoComponent>(restoredGun),
                    Is.True,
                    "The fixture disables automatic ammo replacement so a completed shot remains observable.");
                var ammo = entities.GetComponent<BasicEntityAmmoProviderComponent>(restoredGun);
                firedRestoredGun = restoredGun;
                Assert.That(ammo.Count, Is.Not.Null);
                ammoBefore = ammo.Count.GetValueOrDefault();
                var target = entities.GetNetCoordinates(
                    new EntityCoordinates(restoredGrid, new Vector2(1.5f, 10.5f)));
                Assert.That(
                    fireControl.FireWeapons(
                        restoredServer,
                        [entities.GetNetEntity(restoredGun)],
                        target,
                        restoredServerComponent),
                    Is.True,
                    "The reconciled GCS must be able to fire its restored weapon.");

                var autoShoot = entities.GetComponent<AutoShootGunComponent>(restoredGun);
                var gun = entities.GetComponent<GunComponent>(restoredGun);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<TransformComponent>(restoredGun).Anchored, Is.True);
                    Assert.That(autoShoot.CanFire, Is.True,
                        "The restored powered and anchored shuttle gun must be enabled for automatic fire.");
                    Assert.That(autoShoot.RemainingTime, Is.GreaterThan(TimeSpan.Zero));
                    Assert.That(gun.FireRateModified, Is.GreaterThan(0f));
                });
            });

            await server.WaitRunTicks(2);
            await server.WaitPost(() =>
            {
                Assert.That(
                    entities.GetComponent<GunComponent>(firedRestoredGun).ShotCounter,
                    Is.GreaterThan(0),
                    "GunSystem must process the accepted GCS firing window.");
                Assert.That(
                    entities.GetComponent<BasicEntityAmmoProviderComponent>(firedRestoredGun).Count,
                    Is.LessThan(ammoBefore),
                    "The restored weapon must consume ammunition after the server accepts the shot.");
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

        EntityUid SpawnAnchored(string prototype, EntityUid grid, Vector2 position)
        {
            var uid = entities.SpawnEntity(prototype, new EntityCoordinates(grid, position));
            var xform = entities.GetComponent<TransformComponent>(uid);
            if (!xform.Anchored)
                transform.AnchorEntity(uid, xform);
            return uid;
        }

        void PowerOn(EntityUid uid)
        {
            entities.GetComponent<ApcPowerReceiverComponent>(uid).Powered = true;
            var powerChanged = new PowerChangedEvent(true, 0f);
            entities.EventBus.RaiseLocalEvent(uid, ref powerChanged);
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
