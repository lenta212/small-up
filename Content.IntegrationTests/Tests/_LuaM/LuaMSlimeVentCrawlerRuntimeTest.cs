using System.Numerics;
using Content.Server._LuaM.Species;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMSlimeVentCrawlerRuntimeTest
{
    [Test]
    public async Task OnlyNakedSlimeCanUseConnectedNearbyVentAsEntry()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var crawler = entities.System<LuaMSlimeVentCrawlerSystem>();
        var map = await pair.CreateTestMap();
        EntityUid slime = default;
        EntityUid entry = default;
        EntityUid exit = default;
        EntityUid disconnected = default;

        await server.WaitAssertion(() =>
        {
            for (var x = -1; x <= 6; x++)
            {
                for (var y = -2; y <= 1; y++)
                    maps.SetTile(map.Grid.Owner, map.Grid, new Vector2i(x, y), new Tile(1));
            }

            entry = entities.SpawnEntity("GasVentPump", new EntityCoordinates(map.Grid.Owner, Vector2.Zero));
            entities.SpawnEntity("GasPipeStraight", new EntityCoordinates(map.Grid.Owner, new Vector2(0, -1)));
            exit = entities.SpawnEntity("GasVentPump", new EntityCoordinates(map.Grid.Owner, new Vector2(0, -2)));
            disconnected = entities.SpawnEntity("GasVentPump", new EntityCoordinates(map.Grid.Owner, new Vector2(5, 0)));
            transform.SetLocalRotation(exit, Angle.FromDegrees(180));
            slime = entities.SpawnEntity("MobSlimePerson", new EntityCoordinates(map.Grid.Owner, new Vector2(0, 0.25f)));
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(crawler.TryVentCrawl(slime, exit), Is.True,
                "A naked slime beside one vent must reach another vent on the same pipe net.");
            Assert.That(transform.GetMapCoordinates(slime).Position,
                Is.EqualTo(transform.GetMapCoordinates(exit).Position));

            transform.SetCoordinates(slime,
                entities.GetComponent<TransformComponent>(slime),
                entities.GetComponent<TransformComponent>(entry).Coordinates);
            Assert.That(crawler.TryVentCrawl(slime, disconnected), Is.False,
                "A nearby entry must never permit arbitrary travel to a disconnected vent.");

            var crowbar = entities.SpawnEntity("Crowbar", entities.GetComponent<TransformComponent>(slime).Coordinates);
            Assert.That(hands.TryPickupAnyHand(slime, crowbar), Is.True);
            Assert.That(crawler.TryVentCrawl(slime, exit), Is.False,
                "Even one held item must block full-size slime vent travel.");
        });

        await pair.CleanReturnAsync();
    }
}
