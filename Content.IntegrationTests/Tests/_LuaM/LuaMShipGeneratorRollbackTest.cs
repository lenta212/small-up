using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Server._LuaM.ShipGen;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Serilog.Events;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShipGeneratorRollbackTest
{
    private const string InvalidAtmosPipePrototype = "ChairPilotSeat";

    [Test]
    public async Task PostLoadAtmosConstructionFailureRollsBackEveryCreatedEntity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var shipGenerator = entities.System<LuaMShipGeneratorSystem>();
        var request = new LuaMShipGenerationRequest("expedition", "medium", "rollback-test", string.Empty);
        var createdDuringBuild = new HashSet<EntityUid>();
        var baselineEntities = new HashSet<EntityUid>();
        MapId mapId = default;
        var mapCreated = false;
        var expectedRollbackLogs = 0;
        var logSync = new object();

        bool JudgeExpectedRollback(string sawmillName, LogEvent message)
        {
            if (sawmillName != "luam.shipgen" ||
                !message.RenderMessage().StartsWith(
                    $"Generated ship rollback: InvalidOperationException: {InvalidAtmosPipePrototype} has no atmosphere pipe-layer component",
                    StringComparison.Ordinal))
            {
                return false;
            }

            lock (logSync)
                expectedRollbackLogs++;
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedRollback;
        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                mapCreated = true;
                baselineEntities = EntitiesOnMap(entities, mapId);

                var validated = Validate(request, CreateValidBlueprint());
                ReplaceFirstAtmosPipePrototype(validated, InvalidAtmosPipePrototype);

                void TrackCreatedEntity(Entity<MetaDataComponent> entity)
                    => createdDuringBuild.Add(entity.Owner);

                entities.EntityAdded += TrackCreatedEntity;
                LuaMShipGenerationResult result;
                try
                {
                    var build = typeof(LuaMShipGeneratorSystem).GetMethod(
                        "BuildShip",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.That(build, Is.Not.Null);
                    result = (LuaMShipGenerationResult) build!.Invoke(
                        shipGenerator,
                        [new MapCoordinates(Vector2.Zero, mapId), request, validated])!;
                }
                finally
                {
                    entities.EntityAdded -= TrackCreatedEntity;
                }

                Assert.Multiple(() =>
                {
                    Assert.That(result.Success, Is.False);
                    Assert.That(result.GridUid, Is.EqualTo(EntityUid.Invalid));
                    Assert.That(result.Message,
                        Does.Contain($"{InvalidAtmosPipePrototype} has no atmosphere pipe-layer component"),
                        "The injected trusted prototype must pass existence checks and fail during atmosphere construction.");
                    Assert.That(createdDuringBuild, Is.Not.Empty,
                        "The failure injection must happen only after the seed grid has started loading entities.");
                });
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var leakedEntities = createdDuringBuild
                    .Where(entities.EntityExists)
                    .ToArray();
                var currentEntities = EntitiesOnMap(entities, mapId);

                Assert.Multiple(() =>
                {
                    Assert.That(leakedEntities, Is.Empty,
                        "Rollback must delete the loaded grid and every entity created beneath it.");
                    Assert.That(currentEntities, Is.EquivalentTo(baselineEntities),
                        "The target map must return exactly to its pre-build entity set.");
                    lock (logSync)
                    {
                        Assert.That(expectedRollbackLogs, Is.EqualTo(1),
                            "The construction failure must travel through the transactional rollback path exactly once.");
                    }
                });
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedRollback;
            if (mapCreated)
                await server.WaitPost(() => maps.DeleteMap(mapId));
            await pair.CleanReturnAsync();
        }
    }

    private static object Validate(
        LuaMShipGenerationRequest request,
        LuaMShipBlueprintResponse response)
    {
        var validate = typeof(LuaMShipGeneratorSystem).GetMethod(
            "TryValidateBlueprint",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(validate, Is.Not.Null);

        object[] args = [request, response, null, null];
        var accepted = (bool) validate!.Invoke(null, args)!;
        Assert.That(accepted, Is.True, args[3] as string);
        Assert.That(args[2], Is.Not.Null);
        return args[2]!;
    }

    private static void ReplaceFirstAtmosPipePrototype(object validated, string prototype)
    {
        var property = validated.GetType().GetProperty("AtmosPipes", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(property, Is.Not.Null);
        Assert.That(property!.GetValue(validated), Is.InstanceOf<IList>());
        var pipes = (IList) property.GetValue(validated)!;
        Assert.That(pipes, Is.Not.Empty);

        var original = pipes[0]!;
        var pipeType = original.GetType();
        var constructor = pipeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 3);
        var layer = pipeType.GetProperty("Layer")!.GetValue(original)!;
        var position = pipeType.GetProperty("Position")!.GetValue(original)!;

        pipes[0] = constructor.Invoke([prototype, layer, position]);
    }

    private static HashSet<EntityUid> EntitiesOnMap(IEntityManager entities, MapId mapId)
    {
        var result = new HashSet<EntityUid>();
        var query = entities.EntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out var transform))
        {
            if (transform.MapID == mapId)
                result.Add(uid);
        }
        return result;
    }

    private static LuaMShipBlueprintResponse CreateValidBlueprint()
    {
        const int width = 15;
        const int height = 19;
        var tiles = new List<LuaMShipTile>();
        var entities = new List<LuaMShipEntity>();
        var podTiles = new HashSet<(int X, int Y)>
        {
            (7, 0),
            (6, height - 1),
            (8, height - 1),
            (0, 9),
            (width - 1, 9),
        };

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                tiles.Add(new LuaMShipTile { X = x, Y = y });
                var boundary = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (!boundary || podTiles.Contains((x, y)))
                    continue;

                var kind = (x, y) switch
                {
                    (5, height - 1) => "airlock",
                    (7, height - 1) => "dock",
                    _ => "wall",
                };
                entities.Add(Entity(kind, x, y, kind == "dock" ? 180 : 0));
            }
        }

        entities.AddRange(
        [
            Entity("bulkhead", 7, 1),
            Entity("bulkhead", 6, 17),
            Entity("bulkhead", 8, 17),
            Entity("bulkhead", 1, 9),
            Entity("bulkhead", 13, 9),
            Entity("thruster", 7, 0, 180),
            Entity("thruster", 6, 18, 0),
            Entity("thruster", 8, 18, 0),
            Entity("thruster", 0, 9, 90),
            Entity("thruster", 14, 9, 270),
            Entity("apu", 3, 2),
            Entity("apu", 4, 2),
            Entity("apu", 3, 3),
            Entity("apc", 4, 3),
            Entity("substation", 3, 12),
            Entity("gyro", 11, 12),
            Entity("console", 7, 16),
            Entity("medical", 4, 9),
            Entity("science", 7, 9),
            Entity("salvage", 6, 12),
            Entity("research_server", 10, 9),
            Entity("air_storage", 2, 5, 90),
            Entity("vent", 5, 5, 270),
            Entity("vent", 5, 7, 270),
            Entity("waste_storage", 12, 5, 270),
            Entity("scrubber", 9, 5, 90),
            Entity("scrubber", 9, 7, 90),
        ]);

        return new LuaMShipBlueprintResponse
        {
            SchemaVersion = 1,
            GeneratorVersion = "rollback-test",
            Seed = "rollback-test",
            Name = "Rollback test",
            Preset = "expedition",
            Width = width,
            Height = height,
            Tiles = tiles,
            Entities = entities,
            Summary = JsonSerializer.SerializeToElement(new { tiles = tiles.Count, entities = entities.Count }),
            Preview = JsonSerializer.SerializeToElement(Enumerable.Repeat(new string('#', width), height).ToArray()),
        };
    }

    private static LuaMShipEntity Entity(string kind, int x, int y, int rotation = 0)
    {
        return new LuaMShipEntity
        {
            Kind = kind,
            X = x,
            Y = y,
            Rotation = rotation,
        };
    }
}
