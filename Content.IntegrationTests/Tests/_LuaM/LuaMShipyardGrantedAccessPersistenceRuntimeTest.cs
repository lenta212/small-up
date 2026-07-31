using System.Linq;
using Content.Server._LuaM.ShipPersistence;
using Content.Server._NF.Shipyard.Components;
using Content.Shared.Access;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShipyardGrantedAccessPersistenceRuntimeTest
{
    [Test]
    public async Task SecurityShipyardGrantRoundTripsWithFullShipSnapshot()
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
                var grid = mapManager.CreateGridEntity(sourceMap);
                maps.SetTile(grid, Vector2i.Zero, new Tile(1));

                var grant = entities.EnsureComponent<PersistentShipyardAccessComponent>(grid.Owner);
                grant.GrantedLevels.UnionWith(new ProtoId<AccessLevelPrototype>[]
                {
                    "Captain",
                    "Security",
                    "Brig",
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(grid.Owner, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                entities.DeleteEntity(grid.Owner);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredGrant = entities.GetComponent<PersistentShipyardAccessComponent>(restoredGrid);
                Assert.That(restoredGrant.GrantedLevels.Select(level => level.Id),
                    Is.EquivalentTo(new[] { "Captain", "Security", "Brig" }));
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
}
