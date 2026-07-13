using System.Collections.Generic;
using System.Numerics;
using Content.Server.Spawners.Components;
using Content.Server.Spawners.EntitySystems;
using Content.Server.StationEvents.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[TestOf(typeof(SpawnerSystem))]
public sealed class LuaMTimedSpawnerLimitTest
{
    private static readonly HashSet<string> BoundedPestMigrations =
    [
        "MouseMigration",
        "CockroachMigration",
        "SnailMigrationLowPop",
        "SnailMigration",
    ];

    [Test]
    public async Task RodentSpawnersStopAfterTheirLifetimeBudget()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = entManager.System<SharedMapSystem>();

        MapId mapId = default;
        EntityUid mouseSpawner = default;
        EntityUid cockroachSpawner = default;
        var mapCreated = false;

        try
        {
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out mapId);
                mapCreated = true;
                mouseSpawner = entManager.SpawnEntity("MouseTimedSpawner", new MapCoordinates(Vector2.Zero, mapId));
                cockroachSpawner = entManager.SpawnEntity("CockroachTimedSpawner", new MapCoordinates(Vector2.One, mapId));
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(
                        entManager.GetComponent<TimedSpawnerComponent>(mouseSpawner).MaximumTotalSpawns,
                        Is.EqualTo(5),
                        "The week-long LuaM round must keep the original five-mouse design budget.");
                    Assert.That(
                        entManager.GetComponent<TimedSpawnerComponent>(cockroachSpawner).MaximumTotalSpawns,
                        Is.EqualTo(5),
                        "The inherited cockroach spawner must have the same finite budget.");
                });
            });

            await server.WaitPost(() =>
            {
                entManager.DeleteEntity(cockroachSpawner);

                var component = entManager.GetComponent<TimedSpawnerComponent>(mouseSpawner);
                component.Prototypes = ["MobMouse"];
                component.Chance = 1f;
                component.MinimumEntitiesSpawned = 1;
                component.MaximumEntitiesSpawned = 1;
                component.MaximumTotalSpawns = 2;
                component.TotalSpawned = 0;
                component.IntervalSeconds = TimeSpan.Zero;
                component.NextFire = timing.CurTime;
            });

            await pair.RunTicksSync(8);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(CountMiceOnMap(), Is.EqualTo(2));
                    Assert.That(
                        entManager.GetComponent<TimedSpawnerComponent>(mouseSpawner).TotalSpawned,
                        Is.EqualTo(2));
                });
            });

            await pair.RunTicksSync(8);
            await server.WaitAssertion(() =>
            {
                Assert.That(
                    CountMiceOnMap(),
                    Is.EqualTo(2),
                    "An exhausted timed spawner must remain inert even when its timer fires every tick.");
            });
        }
        finally
        {
            if (mapCreated)
                await server.WaitPost(() => mapSystem.DeleteMap(mapId));

            await pair.CleanReturnAsync();
        }

        int CountMiceOnMap()
        {
            var count = 0;
            var query = entManager.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out _, out var metadata, out var transform))
            {
                if (transform.MapID == mapId && metadata.EntityPrototype?.ID == "MobMouse")
                    count++;
            }

            return count;
        }
    }

    [Test]
    public async Task PestMigrationsRunAtMostOnceAndDoNotGuaranteeBonusMobs()
    {
        var pair = await PoolManager.GetServerClient();

        try
        {
            var stationEvents = new Dictionary<string, StationEventComponent>();
            foreach (var (prototype, component) in pair.GetPrototypesWithComponent<StationEventComponent>())
            {
                if (BoundedPestMigrations.Contains(prototype.ID))
                    stationEvents.Add(prototype.ID, component);
            }

            var ventRules = new Dictionary<string, VentCrittersRuleComponent>();
            foreach (var (prototype, component) in pair.GetPrototypesWithComponent<VentCrittersRuleComponent>())
            {
                if (BoundedPestMigrations.Contains(prototype.ID))
                    ventRules.Add(prototype.ID, component);
            }

            Assert.Multiple(() =>
            {
                Assert.That(stationEvents.Keys, Is.EquivalentTo(BoundedPestMigrations));
                Assert.That(ventRules.Keys, Is.EquivalentTo(BoundedPestMigrations));

                foreach (var eventId in BoundedPestMigrations)
                {
                    Assert.That(
                        stationEvents[eventId].MaxOccurrences,
                        Is.EqualTo(1),
                        $"{eventId} must not repeat throughout a week-long campaign round.");
                    Assert.That(
                        ventRules[eventId].SpecialEntries,
                        Is.Empty,
                        $"{eventId} must not bypass spawn probabilities with a guaranteed bonus mob.");
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
