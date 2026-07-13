using System.Collections.Generic;
using System.Numerics;
using Content.Server.Nutrition.EntitySystems;
using Content.Shared.CCVar;
using Content.Shared.Nutrition.AnimalHusbandry;
using Content.Shared.Storage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(AnimalHusbandrySystem))]
public sealed class LuaMAnimalHusbandryIntervalTest
{
    private static readonly TimeSpan ExpectedBreedInterval = TimeSpan.FromHours(1);

    [Test]
    public async Task AnimalsScheduleBreedingOncePerHourWithoutCatchUpBursts()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = entManager.System<SharedMapSystem>();

        MapId mapId = default;
        EntityUid chicken = default;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out mapId);
            chicken = entManager.SpawnEntity("MobChicken", new MapCoordinates(Vector2.Zero, mapId));
        });

        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            var reproductive = entManager.GetComponent<ReproductiveComponent>(chicken);
            Assert.Multiple(() =>
            {
                Assert.That(reproductive.MinBreedAttemptInterval, Is.EqualTo(ExpectedBreedInterval));
                Assert.That(reproductive.MaxBreedAttemptInterval, Is.EqualTo(ExpectedBreedInterval));
                Assert.That(
                    (reproductive.NextBreedAttempt - timing.CurTime).TotalSeconds,
                    Is.InRange(ExpectedBreedInterval.TotalSeconds - 1, ExpectedBreedInterval.TotalSeconds));
            });
        });

        await server.WaitPost(() =>
        {
            var reproductive = entManager.GetComponent<ReproductiveComponent>(chicken);
            reproductive.NextBreedAttempt = timing.CurTime - TimeSpan.FromHours(24);
        });

        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            var reproductive = entManager.GetComponent<ReproductiveComponent>(chicken);
            Assert.That(
                (reproductive.NextBreedAttempt - timing.CurTime).TotalSeconds,
                Is.InRange(ExpectedBreedInterval.TotalSeconds - 1, ExpectedBreedInterval.TotalSeconds),
                "An overdue animal must schedule from the current time instead of replaying every missed interval.");
        });

        await server.WaitPost(() => mapSystem.DeleteMap(mapId));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnimalBirthsRespectHardMapPopulationCapIncludingPendingEggs()
    {
        Assert.That(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap.DefaultValue, Is.EqualTo(32));

        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var husbandry = entManager.System<AnimalHusbandrySystem>();

        var oldPopulationCap = CCVars.LuaMAnimalHusbandryMaxPopulationPerMap.DefaultValue;
        var maps = new List<MapId>();
        var cows = new List<EntityUid>();
        EntityUid cowParent = default;
        EntityUid chickenParent = default;
        MapId cowMap = default;
        MapId chickenMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                oldPopulationCap = server.CfgMan.GetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap);
                server.CfgMan.SetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap, 4);

                mapSystem.CreateMap(out cowMap);
                maps.Add(cowMap);
                mapSystem.CreateMap(out chickenMap);
                maps.Add(chickenMap);

                for (var i = 0; i < 3; i++)
                {
                    cows.Add(entManager.SpawnEntity(
                        "MobCow",
                        new MapCoordinates(new Vector2(i, 0), cowMap)));
                }

                cowParent = cows[0];
                var cowReproductive = entManager.GetComponent<ReproductiveComponent>(cowParent);
                cowReproductive.MakeOffspringInfant = false;
                cowReproductive.Offspring = new List<EntitySpawnEntry>
                {
                    new()
                    {
                        PrototypeId = "MobCow",
                        Amount = 3,
                        MaxAmount = 3,
                    },
                };

                chickenParent = entManager.SpawnEntity(
                    "MobChicken",
                    new MapCoordinates(Vector2.Zero, chickenMap));
                entManager.SpawnEntity(
                    "MobChicken",
                    new MapCoordinates(Vector2.One, chickenMap));

                var chickenReproductive = entManager.GetComponent<ReproductiveComponent>(chickenParent);
                chickenReproductive.Offspring = new List<EntitySpawnEntry>
                {
                    new()
                    {
                        PrototypeId = "FoodEggChickenFertilized",
                        Amount = 3,
                        MaxAmount = 3,
                    },
                };
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                var cowReproductive = entManager.GetComponent<ReproductiveComponent>(cowParent);
                for (var i = 0; i < 50; i++)
                {
                    cowReproductive.Gestating = true;
                    husbandry.Birth(cowParent, cowReproductive);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(CountOnMap<ReproductivePartnerComponent>(entManager, cowMap), Is.EqualTo(4));
                    Assert.That(CountPopulationUnits(entManager, cowMap), Is.EqualTo(4));
                    Assert.That(cowReproductive.Gestating, Is.False);
                    Assert.That(cowReproductive.GestationEndTime, Is.Null);
                });

                cowReproductive.BreedChance = 1f;
                Assert.That(
                    husbandry.TryReproduce(cowParent, cows[1], cowReproductive),
                    Is.False,
                    "A full map must reject conception before consuming hunger or starting gestation.");
                Assert.That(cowReproductive.Gestating, Is.False);

                var chickenReproductive = entManager.GetComponent<ReproductiveComponent>(chickenParent);
                for (var i = 0; i < 50; i++)
                {
                    chickenReproductive.Gestating = true;
                    husbandry.Birth(chickenParent, chickenReproductive);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(CountPrototypeOnMap(entManager, chickenMap, "FoodEggChickenFertilized"), Is.EqualTo(2));
                    Assert.That(CountOnMap<ReproductivePartnerComponent>(entManager, chickenMap), Is.EqualTo(2));
                    Assert.That(CountPopulationUnits(entManager, chickenMap), Is.EqualTo(4));
                    Assert.That(CountPopulationUnits(entManager, cowMap), Is.EqualTo(4),
                        "Population capacity must be isolated per map.");
                });
            });

            await pair.RunSeconds(22f);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(CountPrototypeOnMap(entManager, chickenMap, "FoodEggChickenFertilized"), Is.Zero);
                    Assert.That(CountOnMap<ReproductivePartnerComponent>(entManager, chickenMap), Is.EqualTo(4));
                    Assert.That(CountPopulationUnits(entManager, chickenMap), Is.EqualTo(4),
                        "Hatching must replace reserved egg slots without exceeding the hard cap.");
                    Assert.That(CountPopulationUnits(entManager, cowMap), Is.EqualTo(4));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                server.CfgMan.SetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap, oldPopulationCap);
                foreach (var mapId in maps)
                {
                    if (mapSystem.MapExists(mapId))
                        mapSystem.DeleteMap(mapId);
                }
            });

            await pair.CleanReturnAsync();
        }
    }

    private static int CountOnMap<T>(IEntityManager entManager, MapId mapId) where T : IComponent
    {
        var count = 0;
        var query = entManager.EntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out _, out _, out var transform))
        {
            if (transform.MapID == mapId)
                count++;
        }

        return count;
    }

    private static int CountPopulationUnits(IEntityManager entManager, MapId mapId)
    {
        var population = new HashSet<EntityUid>();
        AddPopulationUnits<ReproductivePartnerComponent>(population, entManager, mapId);
        AddPopulationUnits<AnimalHusbandryOffspringComponent>(population, entManager, mapId);
        return population.Count;
    }

    private static void AddPopulationUnits<T>(
        HashSet<EntityUid> population,
        IEntityManager entManager,
        MapId mapId) where T : IComponent
    {
        var query = entManager.EntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.MapID == mapId)
                population.Add(uid);
        }
    }

    private static int CountPrototypeOnMap(IEntityManager entManager, MapId mapId, string prototypeId)
    {
        var count = 0;
        var query = entManager.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var metadata, out var transform))
        {
            if (transform.MapID == mapId && metadata.EntityPrototype?.ID == prototypeId)
                count++;
        }

        return count;
    }
}
