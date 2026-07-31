using System.Collections.Generic;
using System.Numerics;
using Content.Server._Crescent.ShipShields;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Power.Components;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMRestoredShipShieldTest
{
    [Test]
    public async Task RestoredShipRebuildsTransientShieldEnvelope()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var shields = entities.System<ShipShieldsSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap);
                for (var x = 0; x < 5; x++)
                {
                    for (var y = 0; y < 5; y++)
                        maps.SetTile(sourceGrid.Owner, sourceGrid.Comp, new Vector2i(x, y), new Tile(1));
                }

                var sourceEmitter = entities.SpawnEntity(
                    "ShieldGeneratorSmall",
                    new EntityCoordinates(sourceGrid.Owner, new Vector2(2.5f, 2.5f)));
                entities.GetComponent<ApcPowerReceiverComponent>(sourceEmitter).Powered = true;
                shields.Update(1.6f);

                AssertValidShieldLinks(sourceGrid.Owner, sourceEmitter);

                // Damage and recharge state are durable. Only the derived envelope and its
                // reciprocal runtime references should be rebuilt during restore.
                var sourceEmitterComponent = entities.GetComponent<ShipShieldEmitterComponent>(sourceEmitter);
                sourceEmitterComponent.Damage = 1000f;
                sourceEmitterComponent.Accumulator = 0.25f;
                sourceEmitterComponent.Recharging = false;
                sourceEmitterComponent.OverloadAccumulator = 0.5f;

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
                    persistence.TryBeginRestoreSnapshot(
                        snapshot,
                        targetMap,
                        out var rollbackScope,
                        out var beginReason),
                    Is.True,
                    beginReason);
                var pendingEmitter = RequirePrototypeDescendant(
                    rollbackScope.Grid,
                    "ShieldGeneratorSmall");
                entities.GetComponent<ApcPowerReceiverComponent>(pendingEmitter).Powered = true;
                shields.Update(1.6f);
                AssertValidShieldLinks(rollbackScope.Grid, pendingEmitter);
                var pendingEnvelope = entities
                    .GetComponent<ShipShieldEmitterComponent>(pendingEmitter)
                    .Shield!.Value;

                Assert.That(persistence.RollbackRestore(rollbackScope), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(rollbackScope.Grid), Is.False);
                    Assert.That(entities.EntityExists(pendingEmitter), Is.False);
                    Assert.That(entities.EntityExists(pendingEnvelope), Is.False,
                        "A shield recreated while database completion is pending must be owned by rollback, not detached as retained state.");
                });

                Assert.That(
                    persistence.TryRestoreSnapshot(
                        snapshot,
                        targetMap,
                        out var restoredGrid,
                        out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredEmitter = RequirePrototypeDescendant(restoredGrid, "ShieldGeneratorSmall");
                var restoredEmitterComponent = entities.GetComponent<ShipShieldEmitterComponent>(restoredEmitter);

                Assert.Multiple(() =>
                {
                    Assert.That(FindShieldEnvelopes(restoredGrid), Is.Empty,
                        "A serialized shield envelope has invalid runtime references and must be discarded.");
                    Assert.That(entities.HasComponent<ShipShieldedComponent>(restoredGrid), Is.False,
                        "The copied grid marker must not block creation of a replacement shield.");
                    Assert.That(restoredEmitterComponent.Shield, Is.Null);
                    Assert.That(restoredEmitterComponent.Shielded, Is.Null);
                    Assert.That(restoredEmitterComponent.Damage, Is.EqualTo(1000f));
                    Assert.That(restoredEmitterComponent.Recharging, Is.False);
                    Assert.That(restoredEmitterComponent.OverloadAccumulator, Is.EqualTo(0.5f));
                });

                // Normal shield processing recreates the derived entity only after the restored
                // generator actually has power again.
                entities.GetComponent<ApcPowerReceiverComponent>(restoredEmitter).Powered = true;
                shields.Update(1.6f);

                AssertValidShieldLinks(restoredGrid, restoredEmitter);
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

        void AssertValidShieldLinks(EntityUid grid, EntityUid emitterUid)
        {
            var emitter = entities.GetComponent<ShipShieldEmitterComponent>(emitterUid);
            Assert.That(emitter.Shield, Is.Not.Null);
            Assert.That(emitter.Shielded, Is.EqualTo(grid));

            var envelope = emitter.Shield!.Value;
            Assert.That(FindShieldEnvelopes(grid), Is.EqualTo(new[] { envelope }));

            var shield = entities.GetComponent<ShipShieldComponent>(envelope);
            var shielded = entities.GetComponent<ShipShieldedComponent>(grid);
            Assert.Multiple(() =>
            {
                Assert.That(shield.Source, Is.EqualTo(emitterUid));
                Assert.That(shield.Shielded, Is.EqualTo(grid));
                Assert.That(shielded.Source, Is.EqualTo(emitterUid));
                Assert.That(shielded.Shield, Is.EqualTo(envelope));
            });
        }

        List<EntityUid> FindShieldEnvelopes(EntityUid root)
        {
            var found = new List<EntityUid>();
            var pending = new Stack<EntityUid>();
            var visited = new HashSet<EntityUid>();
            pending.Push(root);

            while (pending.TryPop(out var uid))
            {
                if (!visited.Add(uid) || !entities.EntityExists(uid))
                    continue;

                if (entities.HasComponent<ShipShieldComponent>(uid))
                    found.Add(uid);

                var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    pending.Push(child);
            }

            return found;
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
