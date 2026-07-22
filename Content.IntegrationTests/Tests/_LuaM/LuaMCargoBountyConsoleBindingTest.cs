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
public sealed class LuaMCargoBountyConsoleBindingTest
{
    [Test]
    public async Task CargoBountyConsoleOnOrdinaryShuttleCreatesLocalBountyDatabase()
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

                Assert.That(entities.HasComponent<StationCargoBountyDatabaseComponent>(station), Is.False,
                    "Ordinary vessels should start without a cargo bounty database in this test.");

                entities.SpawnEntity(
                    "ComputerCargoBounty",
                    new EntityCoordinates(shuttle.Owner, new Vector2(0.5f, 0.5f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entities.TryGetComponent<StationCargoBountyDatabaseComponent>(station, out var database), Is.True,
                    "Installing a cargo bounty console on a shuttle station should bind it to that shuttle's own bounty database.");
                Assert.That(database!.MaxBounties, Is.GreaterThan(0));
                Assert.That(database.Bounties, Is.Not.Empty);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
