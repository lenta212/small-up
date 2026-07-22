using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Server._LuaM.ShipGen;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipGeneratorValidationTest
{
    [Test]
    public void ValidSealedBlueprintIsAccepted()
    {
        var (accepted, error) = Validate(CreateValidBlueprint());

        Assert.That(accepted, Is.True, error);
        Assert.That(error, Is.Empty);
    }

    [Test]
    public void MissingAtmosStorageIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Entities!.RemoveAll(entity => entity!.Kind == "air_storage");

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("exactly one air_storage"));
    }

    [Test]
    public void DuplicateAtmosStorageIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Entities!.Add(Entity("waste_storage", 9, 6, 270));

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("waste_storage"));
    }

    [TestCase("vent")]
    [TestCase("scrubber")]
    public void WrongAtmosDeviceCountIsRejected(string kind)
    {
        var blueprint = CreateValidBlueprint();
        var device = blueprint.Entities!.First(entity => entity!.Kind == kind);
        blueprint.Entities.Remove(device);

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain($"exactly 2 {kind}"));
    }

    [Test]
    public void AtmosEndpointFacingBoundaryIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        var storageIndex = blueprint.Entities!.FindIndex(entity => entity!.Kind == "air_storage");
        blueprint.Entities[storageIndex] = Entity("air_storage", 1, 5, 270);

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("does not face a safe interior pipe tile"));
    }

    [Test]
    public void UnknownGasProducingKindIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Entities!.Add(Entity("gas_miner", 9, 6));

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("gas_miner"));
    }

    [Test]
    public void DerivedAtmosPlanUsesTrustedLayersAndKeepsPodsVacuum()
    {
        var response = CreateValidBlueprint();
        var (accepted, validated, error) = ValidateDetailed(response);

        Assert.That(accepted, Is.True, error);
        Assert.That(validated, Is.Not.Null);

        var blueprintType = validated!.GetType();
        var pipes = ReadItems(blueprintType, validated, "AtmosPipes");
        var ports = ReadItems(blueprintType, validated, "GasPorts");
        var vacuumTiles = ReadItems(blueprintType, validated, "VacuumTiles");

        Assert.That(pipes, Is.Not.Empty);
        Assert.That(
            pipes.Select(ReadPrototype).Distinct(),
            Is.EquivalentTo(new[] { "GasPipeFourway", "GasPipeFourwayAlt1" }));
        Assert.That(
            pipes.Select(ReadLayer).Distinct(),
            Is.EquivalentTo(new[] { "Primary", "Secondary" }));
        Assert.That(ports, Has.Length.EqualTo(2));
        Assert.That(ports.Select(ReadPrototype), Is.All.EqualTo("GasPort"));
        Assert.That(
            ports.Select(ReadLayer),
            Is.EquivalentTo(new[] { "Primary", "Secondary" }));

        foreach (var pipe in pipes)
        {
            var (x, y) = ReadPosition(pipe);
            Assert.Multiple(() =>
            {
                Assert.That(x, Is.GreaterThan(0).And.LessThan(response.Width - 1));
                Assert.That(y, Is.GreaterThan(0).And.LessThan(response.Height - 1));
            });
        }

        Assert.That(
            vacuumTiles.Select(ReadVector),
            Is.EquivalentTo(new[]
            {
                (7, 0),
                (6, response.Height - 1),
                (8, response.Height - 1),
                (0, 9),
                (response.Width - 1, 9),
            }));
    }

    [Test]
    public void DisconnectedFloorIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Tiles!.RemoveAll(tile => tile!.X == 2);

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("связную"));
    }

    [Test]
    public void MissingBoundaryHullIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Entities!.RemoveAt(0);

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("boundary tile"));
    }

    [Test]
    public void MultipleSolidEntitiesOnOneTileAreRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Entities!.Add(new LuaMShipEntity
        {
            Kind = "chair",
            X = 7,
            Y = 16,
            Rotation = 0,
        });

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("solid entities"));
    }

    [Test]
    public void ExpeditionWeaponIsRejected()
    {
        var blueprint = CreateValidBlueprint();
        blueprint.Entities!.Add(new LuaMShipEntity
        {
            Kind = "weapon",
            X = 1,
            Y = 3,
            Rotation = 0,
        });

        var (accepted, error) = Validate(blueprint);

        Assert.That(accepted, Is.False);
        Assert.That(error, Does.Contain("запрещён"));
    }

    [Test]
    public void ObjectSummaryDeserializesWithoutAContractError()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "generatorVersion": "test-1",
              "seed": "seed-1",
              "name": "Test",
              "preset": "expedition",
              "width": 1,
              "height": 1,
              "tiles": [{"x": 0, "y": 0}],
              "entities": [],
              "summary": {"tiles": 1, "entities": 0},
              "preview": ["#"]
            }
            """;

        var response = JsonSerializer.Deserialize<LuaMShipBlueprintResponse>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Summary.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    private static LuaMShipBlueprintResponse CreateValidBlueprint()
    {
        const int blueprintWidth = 15;
        const int blueprintHeight = 19;
        var tiles = new List<LuaMShipTile>();
        var entities = new List<LuaMShipEntity>();
        var podTiles = new HashSet<(int X, int Y)>
        {
            (7, 0),
            (6, blueprintHeight - 1),
            (8, blueprintHeight - 1),
            (0, 9),
            (blueprintWidth - 1, 9),
        };

        for (var y = 0; y < blueprintHeight; y++)
        {
            for (var x = 0; x < blueprintWidth; x++)
            {
                tiles.Add(new LuaMShipTile { X = x, Y = y });
                var boundary = x == 0 || y == 0 || x == blueprintWidth - 1 || y == blueprintHeight - 1;
                if (boundary && !podTiles.Contains((x, y)))
                {
                    var kind = (x, y) switch
                    {
                        (5, blueprintHeight - 1) => "airlock",
                        (7, blueprintHeight - 1) => "dock",
                        _ => "wall",
                    };
                    entities.Add(new LuaMShipEntity
                    {
                        Kind = kind,
                        X = x,
                        Y = y,
                        Rotation = kind == "dock" ? 180 : 0,
                    });
                }
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
            Entity("vent", 2, 5, 90),
            Entity("vent", 2, 13, 90),
            Entity("air_storage", 4, 5, 90),
            Entity("scrubber", 12, 5, 270),
            Entity("scrubber", 12, 13, 270),
            Entity("waste_storage", 10, 5, 270),
        ]);

        return new LuaMShipBlueprintResponse
        {
            SchemaVersion = 1,
            GeneratorVersion = "test-1",
            Seed = "seed-1",
            Name = "Test ship",
            Preset = "expedition",
            Width = blueprintWidth,
            Height = blueprintHeight,
            Tiles = tiles,
            Entities = entities,
            Summary = JsonSerializer.SerializeToElement(new { tiles = tiles.Count, entities = entities.Count }),
            Preview = JsonSerializer.SerializeToElement(new[]
            {
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
                "###############",
            }),
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

    private static (bool Accepted, string Error) Validate(LuaMShipBlueprintResponse response)
    {
        var (accepted, _, error) = ValidateDetailed(response);
        return (accepted, error);
    }

    private static (bool Accepted, object Blueprint, string Error) ValidateDetailed(
        LuaMShipBlueprintResponse response)
    {
        var method = typeof(LuaMShipGeneratorSystem).GetMethod(
            "TryValidateBlueprint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null);

        var request = new LuaMShipGenerationRequest("expedition", "medium", "seed-1", string.Empty);
        object[] args = [request, response, null, null];
        var accepted = (bool) method!.Invoke(null, args)!;
        return (accepted, args[2], (string) args[3]!);
    }

    private static object[] ReadItems(Type blueprintType, object blueprint, string propertyName)
    {
        var property = blueprintType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(property, Is.Not.Null);
        Assert.That(property!.GetValue(blueprint), Is.InstanceOf<IEnumerable>());
        return ((IEnumerable) property.GetValue(blueprint)!).Cast<object>().ToArray();
    }

    private static string ReadPrototype(object item)
    {
        return (string) item.GetType().GetProperty("Prototype")!.GetValue(item)!;
    }

    private static string ReadLayer(object item)
    {
        return item.GetType().GetProperty("Layer")!.GetValue(item)!.ToString()!;
    }

    private static (int X, int Y) ReadPosition(object item)
    {
        return ReadVector(item.GetType().GetProperty("Position")!.GetValue(item)!);
    }

    private static (int X, int Y) ReadVector(object vector)
    {
        var position = (Vector2i) vector;
        return (position.X, position.Y);
    }
}
