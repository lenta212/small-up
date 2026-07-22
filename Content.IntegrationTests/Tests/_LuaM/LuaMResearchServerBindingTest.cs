using System.Numerics;
using Content.Server.Research.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared.Research.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMResearchServerBindingTest
{
    [Test]
    public async Task ResearchClientCannotRegisterToAnotherShuttleStationsServer()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
        });

        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var research = entities.System<ResearchSystem>();
        var stations = entities.System<StationSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var testMap = await pair.CreateTestMap();

        EntityUid client = EntityUid.Invalid;
        EntityUid localServer = EntityUid.Invalid;
        EntityUid remoteServer = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                entities.DeleteEntity(testMap.Grid);

                var stationA = entities.SpawnEntity("StandardFrontierVessel", MapCoordinates.Nullspace);
                var gridA = mapManager.CreateGridEntity(testMap.MapId);
                maps.SetTile(gridA.Owner, gridA.Comp, Vector2i.Zero, new Tile(1));
                maps.SetTile(gridA.Owner, gridA.Comp, new Vector2i(1, 0), new Tile(1));
                transform.SetLocalPosition(gridA.Owner, Vector2.Zero);
                if (!entities.HasComponent<ShuttleComponent>(gridA.Owner))
                    entities.AddComponent<ShuttleComponent>(gridA.Owner);
                stations.AddGridToStation(stationA, gridA.Owner);

                var stationB = entities.SpawnEntity("StandardFrontierVessel", MapCoordinates.Nullspace);
                var gridB = mapManager.CreateGridEntity(testMap.MapId);
                maps.SetTile(gridB.Owner, gridB.Comp, Vector2i.Zero, new Tile(1));
                transform.SetLocalPosition(gridB.Owner, new Vector2(8, 0));
                if (!entities.HasComponent<ShuttleComponent>(gridB.Owner))
                    entities.AddComponent<ShuttleComponent>(gridB.Owner);
                stations.AddGridToStation(stationB, gridB.Owner);

                client = entities.SpawnEntity(
                    "ComputerResearchAndDevelopment",
                    new EntityCoordinates(gridA.Owner, new Vector2(0.5f, 0.5f)));
                localServer = entities.SpawnEntity(
                    "ResearchAndDevelopmentServer",
                    new EntityCoordinates(gridA.Owner, new Vector2(1.5f, 0.5f)));
                remoteServer = entities.SpawnEntity(
                    "ResearchAndDevelopmentServer",
                    new EntityCoordinates(gridB.Owner, new Vector2(0.5f, 0.5f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entities.TryGetComponent<ResearchClientComponent>(client, out var clientComponent), Is.True);
                Assert.That(entities.TryGetComponent<ResearchServerComponent>(localServer, out var localServerComponent), Is.True);
                Assert.That(entities.TryGetComponent<ResearchServerComponent>(remoteServer, out var remoteServerComponent), Is.True);

                var clientXform = entities.GetComponent<TransformComponent>(client);
                var localServerXform = entities.GetComponent<TransformComponent>(localServer);
                var remoteServerXform = entities.GetComponent<TransformComponent>(remoteServer);
                Assert.Multiple(() =>
                {
                    Assert.That(clientXform.GridUid, Is.EqualTo(localServerXform.GridUid));
                    Assert.That(clientXform.GridUid, Is.Not.EqualTo(remoteServerXform.GridUid));
                    Assert.That(stations.GetOwningStation(client, clientXform),
                        Is.EqualTo(stations.GetOwningStation(localServer, localServerXform)));
                    Assert.That(stations.GetOwningStation(client, clientXform),
                        Is.Not.EqualTo(stations.GetOwningStation(remoteServer, remoteServerXform)));
                });

                research.RegisterClient(client, remoteServer, clientComponent, remoteServerComponent);
                Assert.That(clientComponent!.Server, Is.Not.EqualTo(remoteServer),
                    "A research console on one shuttle station must not be able to bind to another shuttle station's R&D server by id.");
                Assert.That(remoteServerComponent!.Clients, Does.Not.Contain(client));

                research.RegisterClient(client, localServer, clientComponent, localServerComponent);
                Assert.That(clientComponent.Server, Is.EqualTo(localServer),
                    "A research console should still be able to bind to its own shuttle station's R&D server.");
                Assert.That(localServerComponent!.Clients, Does.Contain(client));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
