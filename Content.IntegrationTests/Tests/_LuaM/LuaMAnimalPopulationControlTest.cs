using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Mind;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.Nutrition.EntitySystems;
using Content.Server.Spawners.Components;
using Content.Server._LuaM.Animals;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Nutrition.AnimalHusbandry;
using Content.Shared.Storage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMAnimalPopulationSystem))]
public sealed class LuaMAnimalPopulationControlTest
{
    [Test]
    public async Task PestsShareTheHardMapCapWithHusbandryAndTimedSpawners()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var population = entManager.System<LuaMAnimalPopulationSystem>();
        var husbandry = entManager.System<AnimalHusbandrySystem>();
        var previousLimit = server.CfgMan.GetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap);
        MapId mapId = default;
        MapId husbandrySpawnerMapId = default;
        EntityUid cowParent = default;
        EntityUid cockroach = default;
        EntityUid timedSpawner = default;
        EntityUid cowTimedSpawner = default;

        try
        {
            await server.WaitPost(() =>
            {
                server.CfgMan.SetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap, 4);
                mapSystem.CreateMap(out mapId);

                entManager.SpawnEntity("MobMouse", new MapCoordinates(Vector2.Zero, mapId));
                cockroach = entManager.SpawnEntity("MobCockroach", new MapCoordinates(Vector2.One, mapId));
                cowParent = entManager.SpawnEntity("MobCow", new MapCoordinates(new Vector2(2, 0), mapId));
                entManager.SpawnEntity("MobCow", new MapCoordinates(new Vector2(3, 0), mapId));

                var reproductive = entManager.GetComponent<ReproductiveComponent>(cowParent);
                reproductive.MakeOffspringInfant = false;
                reproductive.Offspring = new List<EntitySpawnEntry>
                {
                    new()
                    {
                        PrototypeId = "MobCow",
                        Amount = 2,
                        MaxAmount = 2,
                    },
                };

                timedSpawner = entManager.SpawnEntity(
                    "MouseTimedSpawner",
                    new MapCoordinates(new Vector2(4, 0), mapId));
                var spawner = entManager.GetComponent<TimedSpawnerComponent>(timedSpawner);
                spawner.Prototypes = ["MobSnail"];
                spawner.Chance = 1f;
                spawner.MinimumEntitiesSpawned = 2;
                spawner.MaximumEntitiesSpawned = 2;
                spawner.MaximumTotalSpawns = null;
                spawner.TotalSpawned = 0;
                spawner.IntervalSeconds = TimeSpan.Zero;
                spawner.NextFire = timing.CurTime;

                mapSystem.CreateMap(out husbandrySpawnerMapId);
                for (var i = 0; i < 3; i++)
                {
                    entManager.SpawnEntity(
                        "MobCow",
                        new MapCoordinates(new Vector2(i, 0), husbandrySpawnerMapId));
                }

                cowTimedSpawner = entManager.SpawnEntity(
                    "MouseTimedSpawner",
                    new MapCoordinates(new Vector2(4, 0), husbandrySpawnerMapId));
                var cowSpawner = entManager.GetComponent<TimedSpawnerComponent>(cowTimedSpawner);
                cowSpawner.Prototypes = ["MobCow"];
                cowSpawner.Chance = 1f;
                cowSpawner.MinimumEntitiesSpawned = 2;
                cowSpawner.MaximumEntitiesSpawned = 2;
                cowSpawner.MaximumTotalSpawns = null;
                cowSpawner.TotalSpawned = 0;
                cowSpawner.IntervalSeconds = TimeSpan.Zero;
                cowSpawner.NextFire = timing.CurTime;
            });

            await pair.RunTicksSync(4);

            await server.WaitAssertion(() =>
            {
                var report = population.GetPopulationReport(mapId);
                Assert.Multiple(() =>
                {
                    Assert.That(report.Total, Is.EqualTo(4));
                    Assert.That(report.Species["mouse"], Is.EqualTo(1));
                    Assert.That(report.Species["cockroach"], Is.EqualTo(1));
                    Assert.That(report.Species["MobCow"], Is.EqualTo(2));
                    Assert.That(CountPrototype(entManager, mapId, "MobSnail"), Is.Zero,
                        "A known timed pest must not cross the shared map cap.");
                    Assert.That(CountPrototype(entManager, husbandrySpawnerMapId, "MobCow"), Is.EqualTo(4),
                        "External spawners must trim reproductive species to the shared map cap.");
                    Assert.That(
                        entManager.GetComponent<TimedSpawnerComponent>(cowTimedSpawner).TotalSpawned,
                        Is.EqualTo(1));
                    Assert.That(population.CountPopulationUnits(husbandrySpawnerMapId), Is.EqualTo(4));
                });

                var reproductive = entManager.GetComponent<ReproductiveComponent>(cowParent);
                reproductive.Gestating = true;
                husbandry.Birth(cowParent, reproductive);
                Assert.That(population.CountPopulationUnits(mapId), Is.EqualTo(4),
                    "Known pests must consume capacity before a husbandry birth.");
            });

            await server.WaitPost(() => entManager.DeleteEntity(cockroach));
            await pair.RunTicksSync(4);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(CountPrototype(entManager, mapId, "MobSnail"), Is.EqualTo(1));
                    Assert.That(population.CountPopulationUnits(mapId), Is.EqualTo(4));
                    Assert.That(
                        entManager.GetComponent<TimedSpawnerComponent>(timedSpawner).TotalSpawned,
                        Is.EqualTo(1));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                server.CfgMan.SetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap, previousLimit);
                if (mapSystem.MapExists(mapId))
                    mapSystem.DeleteMap(mapId);
                if (mapSystem.MapExists(husbandrySpawnerMapId))
                    mapSystem.DeleteMap(husbandrySpawnerMapId);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task CleanupRequiresUnchangedPreviewAndProtectsPlayerLikeOrSpecialPests()
    {
        const int mouseCount = 120;
        const int cockroachCount = 100;

        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var metadataSystem = entManager.System<MetaDataSystem>();
        var mindSystem = entManager.System<MindSystem>();
        var damageableSystem = entManager.System<DamageableSystem>();
        var imprintingSystem = entManager.System<NPCImprintingOnSpawnBehaviourSystem>();
        var population = entManager.System<LuaMAnimalPopulationSystem>();
        MapId mapId = default;
        var mice = new List<EntityUid>();
        var cockroaches = new List<EntityUid>();

        try
        {
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out mapId);
                for (var i = 0; i < mouseCount; i++)
                {
                    mice.Add(entManager.SpawnEntity(
                        "MobMouse",
                        new MapCoordinates(new Vector2(i, 0), mapId)));
                }

                for (var i = 0; i < cockroachCount; i++)
                {
                    cockroaches.Add(entManager.SpawnEntity(
                        "MobCockroach",
                        new MapCoordinates(new Vector2(i, 1), mapId)));
                }

                var mind = mindSystem.CreateMind(null, "protected test pest");
                mindSystem.TransferTo(mind, mice[0], createGhost: false, mind: mind.Comp);
                metadataSystem.SetEntityName(mice[1], "department pet mouse");
                var criticalInjury = new DamageSpecifier();
                criticalInjury.DamageDict.Add("Blunt", 10);
                Assert.That(
                    damageableSystem.TryChangeDamage(mice[2], criticalInjury, ignoreResistances: true),
                    Is.Not.Null,
                    "The protected critical pest must be put into a threshold-backed critical state.");
                var imprinting = entManager.EnsureComponent<NPCImprintingOnSpawnBehaviourComponent>(cockroaches[^1]);
                var friend = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
                imprintingSystem.AddImprintingTarget(cockroaches[^1], friend, imprinting);
            });

            await pair.RunTicksSync(2);

            LuaMAnimalCleanupPreview preview = default!;
            await server.WaitAssertion(() =>
            {
                Assert.That(
                    entManager.GetComponent<MobStateComponent>(mice[2]).CurrentState,
                    Is.EqualTo(MobState.Critical),
                    "The threshold-backed critical pest must remain protected through the preview tick.");
                preview = population.BuildCleanupPreview(mapId, 4);
                Assert.Multiple(() =>
                {
                    Assert.That(preview.TotalPopulation, Is.EqualTo(mouseCount + cockroachCount));
                    Assert.That(preview.Overflow, Is.EqualTo(mouseCount + cockroachCount - 4));
                    Assert.That(preview.SafeCandidates, Is.EqualTo(mouseCount + cockroachCount - 4));
                    Assert.That(preview.SelectedEntities, Has.Count.EqualTo(mouseCount + cockroachCount - 5));
                    Assert.That(preview.PopulationAfterCleanup, Is.EqualTo(5),
                        "Protected pests and the two-per-species floor may keep the map above target.");
                    Assert.That(preview.SelectedEntities, Does.Not.Contain(mice[0]));
                    Assert.That(preview.SelectedEntities, Does.Not.Contain(mice[1]));
                    Assert.That(preview.SelectedEntities, Does.Not.Contain(mice[2]));
                    Assert.That(preview.SelectedEntities, Does.Not.Contain(cockroaches[^1]));
                });
            });

            await server.WaitPost(() =>
                entManager.SpawnEntity("MobSnail", new MapCoordinates(new Vector2(8, 0), mapId)));
            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(
                    population.TryExecuteCleanup(
                        mapId,
                        4,
                        preview.Fingerprint,
                        "integration-test",
                        out _,
                        out var error),
                    Is.False);
                Assert.That(error, Does.Contain("Population changed"));
            });

            await server.WaitPost(() =>
            {
                var current = population.BuildCleanupPreview(mapId, 4);
                Assert.That(
                    population.TryExecuteCleanup(
                        mapId,
                        4,
                        current.Fingerprint,
                        "integration-test",
                        out var result,
                        out var error),
                    Is.True,
                    error);
                Assert.Multiple(() =>
                {
                    Assert.That(result.QueuedForDeletion, Is.EqualTo(mouseCount + cockroachCount - 5));
                    Assert.That(result.PopulationAfterCleanup, Is.EqualTo(6));
                });
            });
            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entManager.EntityExists(mice[0]), Is.True);
                    Assert.That(entManager.EntityExists(mice[1]), Is.True);
                    Assert.That(entManager.EntityExists(mice[2]), Is.True);
                    Assert.That(entManager.EntityExists(cockroaches[^1]), Is.True);
                    Assert.That(population.CountPopulationUnits(mapId), Is.EqualTo(6));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (mapSystem.MapExists(mapId))
                    mapSystem.DeleteMap(mapId);
            });
            await pair.CleanReturnAsync();
        }
    }

    private static int CountPrototype(IEntityManager entManager, MapId mapId, string prototypeId)
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
