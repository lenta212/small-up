using System.Numerics;
using Content.Server.Cargo.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMCargoOrderConsoleBindingTest
{
    [Test]
    public async Task CargoOrderConsoleOnOrdinaryShuttleCreatesLocalOrderDatabase()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
        });

        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var stations = entities.System<StationSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var testMap = await pair.CreateTestMap();

        EntityUid station = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                entities.DeleteEntity(testMap.Grid);

                station = entities.SpawnEntity("StandardFrontierVessel", MapCoordinates.Nullspace);
                var shuttle = mapManager.CreateGridEntity(testMap.MapId);
                maps.SetTile(shuttle.Owner, shuttle.Comp, Vector2i.Zero, new Tile(1));
                transform.SetLocalPosition(shuttle.Owner, Vector2.Zero);

                if (!entities.HasComponent<ShuttleComponent>(shuttle.Owner))
                    entities.AddComponent<ShuttleComponent>(shuttle.Owner);

                stations.AddGridToStation(station, shuttle.Owner);

                Assert.That(entities.HasComponent<StationCargoOrderDatabaseComponent>(station), Is.False,
                    "Ordinary vessels should start without a cargo order database in this test.");

                entities.SpawnEntity(
                    "ComputerCargoOrders",
                    new EntityCoordinates(shuttle.Owner, new Vector2(0.5f, 0.5f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entities.TryGetComponent<StationCargoOrderDatabaseComponent>(station, out var database), Is.True,
                    "Installing a cargo order console on a shuttle station should bind it to that shuttle's own cargo order database.");
                Assert.That(database!.Capacity, Is.GreaterThan(0));
                Assert.That(database.Orders, Is.Empty);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
