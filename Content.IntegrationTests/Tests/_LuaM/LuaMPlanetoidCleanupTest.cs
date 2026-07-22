using System.Numerics;
using Content.Server.StationEvents.Events;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LinkedLifecycleGridSystem))]
public sealed class LuaMPlanetoidCleanupTest
{
    [Test]
    public async Task CleanupKeepsPlayersAndCarriedItemsButDeletesFloorContents()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var lifecycle = entities.System<LinkedLifecycleGridSystem>();
        var testMap = await pair.CreateTestMap();

        EntityUid planetoid = default;
        EntityUid player = default;
        EntityUid carriedItem = default;
        EntityUid floorItem = default;

        try
        {
            await server.WaitPost(() =>
            {
                var grid = mapManager.CreateGridEntity(testMap.MapId);
                planetoid = grid.Owner;
                maps.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));

                var coordinates = new EntityCoordinates(planetoid, new Vector2(0.5f, 0.5f));
                player = entities.SpawnEntity("MobHuman", coordinates);
                entities.EnsureComponent<BankAccountComponent>(player);
                carriedItem = entities.SpawnEntity("Crowbar", new EntityCoordinates(player, Vector2.Zero));
                floorItem = entities.SpawnEntity("Crowbar", coordinates);

                lifecycle.UnparentPlayersFromGrid(planetoid, deleteGrid: true);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(planetoid), Is.False);
                    Assert.That(entities.EntityExists(floorItem), Is.False);
                    Assert.That(entities.EntityExists(player), Is.True);
                    Assert.That(entities.EntityExists(carriedItem), Is.True);
                    Assert.That(entities.GetComponent<TransformComponent>(player).MapID, Is.EqualTo(testMap.MapId));
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
