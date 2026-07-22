using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Server._LuaM.ShipGen;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShipGeneratorMatrixRuntimeTest
{
    private const string FixturePath = "Tools/fixtures/luam_ship_generator_contract_v1.json";

    [Test]
    public async Task CompleteDeterministicPythonMatrixBuildsInEngine()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FullPath(FixturePath)));
        var fixtureSeed = fixture.RootElement.GetProperty("seed").GetString()!;
        var fixtureCases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.That(fixtureCases, Has.Length.EqualTo(9));

        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var atmosphere = entities.System<AtmosphereSystem>();
        var shipGenerator = entities.System<LuaMShipGeneratorSystem>();
        var jsonOptions = ProductionJsonOptions();
        var validate = typeof(LuaMShipGeneratorSystem).GetMethod(
            "TryValidateBlueprint",
            BindingFlags.Static | BindingFlags.NonPublic);
        var build = typeof(LuaMShipGeneratorSystem).GetMethod(
            "BuildShip",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Multiple(() =>
        {
            Assert.That(validate, Is.Not.Null);
            Assert.That(build, Is.Not.Null);
        });

        MapId mapId = default;
        var built = new List<BuiltCase>(fixtureCases.Length);
        await server.WaitPost(() =>
        {
            maps.CreateMap(out mapId);
            foreach (var contractCase in fixtureCases)
            {
                var preset = contractCase.GetProperty("preset").GetString()!;
                var size = contractCase.GetProperty("size").GetString()!;
                var response = contractCase.GetProperty("blueprint")
                    .Deserialize<LuaMShipBlueprintResponse>(jsonOptions);
                Assert.That(response, Is.Not.Null, $"{preset}/{size} fixture deserialized to null.");

                var request = new LuaMShipGenerationRequest(preset, size, fixtureSeed, string.Empty);
                object[] validationArguments = [request, response!, null, null];
                var accepted = (bool) validate!.Invoke(null, validationArguments)!;
                Assert.That(accepted, Is.True, $"{preset}/{size}: {validationArguments[3]}");

                var result = (LuaMShipGenerationResult) build!.Invoke(
                    shipGenerator,
                    [new MapCoordinates(Vector2.Zero, mapId), request, validationArguments[2]!])!;
                Assert.That(result.Success, Is.True, $"{preset}/{size}: {result.Message}");
                Assert.That(entities.EntityExists(result.GridUid), Is.True, $"{preset}/{size} grid is missing.");

                AssertInitialAtmosphere(atmosphere, result.GridUid, response!, preset, size);
                built.Add(new BuiltCase(preset, size, result.GridUid, response!));
            }
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(built.Select(item => item.GridUid).Distinct().ToArray(), Has.Length.EqualTo(9));
            foreach (var item in built)
            {
                Assert.That(entities.EntityExists(item.GridUid), Is.True, $"{item.Label} grid was deleted.");
                Assert.That(entities.HasComponent<MapGridComponent>(item.GridUid), Is.True);
                Assert.That(entities.HasComponent<GridAtmosphereComponent>(item.GridUid), Is.True);
                Assert.That(entities.HasComponent<GasTileOverlayComponent>(item.GridUid), Is.True);
                AssertSettledAtmosphere(atmosphere, item);

                var prototypes = PrototypeCountsOnGrid(entities, item.GridUid);
                var expectedPods = item.Response.Entities!
                    .Count(entity => entity!.Kind is "thruster" or "weapon");
                var expectedVents = item.Response.Entities.Count(entity => entity!.Kind == "vent");
                var expectedScrubbers = item.Response.Entities.Count(entity => entity!.Kind == "scrubber");
                Assert.Multiple(() =>
                {
                    Assert.That(prototypes.GetValueOrDefault("AtmosFixBlockerMarker"),
                        Is.EqualTo(expectedPods), $"{item.Label} vacuum marker count");
                    Assert.That(prototypes.GetValueOrDefault("GasVentPump"),
                        Is.EqualTo(expectedVents), $"{item.Label} vent count");
                    Assert.That(prototypes.GetValueOrDefault("GasVentScrubber"),
                        Is.EqualTo(expectedScrubbers), $"{item.Label} scrubber count");
                    Assert.That(prototypes.GetValueOrDefault("GasPort"),
                        Is.EqualTo(2), $"{item.Label} port count");
                    Assert.That(prototypes.GetValueOrDefault("AirCanister"),
                        Is.EqualTo(1), $"{item.Label} air reserve count");
                    Assert.That(prototypes.GetValueOrDefault("StorageCanister"),
                        Is.EqualTo(1), $"{item.Label} waste reserve count");
                    Assert.That(prototypes.GetValueOrDefault("GasPipeFourway"),
                        Is.GreaterThan(0), $"{item.Label} supply pipes");
                    Assert.That(prototypes.GetValueOrDefault("GasPipeFourwayAlt1"),
                        Is.GreaterThan(0), $"{item.Label} waste pipes");
                });
            }
        });

        await server.WaitPost(() => maps.DeleteMap(mapId));
        await pair.CleanReturnAsync();
    }

    private static void AssertInitialAtmosphere(
        AtmosphereSystem atmosphere,
        EntityUid gridUid,
        LuaMShipBlueprintResponse response,
        string preset,
        string size)
    {
        var pod = response.Entities!.First(entity => entity!.Kind == "thruster")!;
        var podAir = atmosphere.GetTileMixture(gridUid, null, new Vector2i(pod.X, pod.Y));
        Assert.That(podAir == null || podAir.TotalMoles <= 0.001f, Is.True,
            $"{preset}/{size} exposed pod was initialized with {podAir?.TotalMoles} moles.");

        var console = response.Entities.Single(entity => entity!.Kind == "console")!;
        var cabinAir = atmosphere.GetTileMixture(gridUid, null, new Vector2i(console.X, console.Y));
        Assert.That(cabinAir, Is.Not.Null, $"{preset}/{size} cabin atmosphere is missing.");
        Assert.That(cabinAir!.Pressure,
            Is.EqualTo(Atmospherics.OneAtmosphere).Within(5f),
            $"{preset}/{size} cabin did not initialize near one atmosphere.");
    }

    private static void AssertSettledAtmosphere(AtmosphereSystem atmosphere, BuiltCase item)
    {
        foreach (var pod in item.Response.Entities!
                     .Where(entity => entity!.Kind is "thruster" or "weapon"))
        {
            var podAir = atmosphere.GetTileMixture(
                item.GridUid,
                null,
                new Vector2i(pod!.X, pod.Y));
            Assert.That(podAir == null || podAir.TotalMoles <= 0.001f, Is.True,
                $"{item.Label} exposed {pod.Kind} pod at {pod.X},{pod.Y} filled with " +
                $"{podAir?.TotalMoles} moles after atmosphere processing.");
        }

        var console = item.Response.Entities.Single(entity => entity!.Kind == "console")!;
        var cabinAir = atmosphere.GetTileMixture(
            item.GridUid,
            null,
            new Vector2i(console.X, console.Y));
        Assert.That(cabinAir, Is.Not.Null, $"{item.Label} settled cabin atmosphere is missing.");
        Assert.That(cabinAir!.Pressure, Is.GreaterThan(75f),
            $"{item.Label} cabin did not remain pressurized after atmosphere processing.");
    }

    private static JsonSerializerOptions ProductionJsonOptions()
    {
        var field = typeof(LuaMShipGeneratorSystem).GetField(
            "JsonOptions",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        return (JsonSerializerOptions) field!.GetValue(null)!;
    }

    private static Dictionary<string, int> PrototypeCountsOnGrid(IEntityManager entities, EntityUid gridUid)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var query = entities.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var metadata, out var transform))
        {
            if (transform.GridUid != gridUid || metadata.EntityPrototype?.ID is not { } prototype)
                continue;
            counts[prototype] = counts.GetValueOrDefault(prototype) + 1;
        }
        return counts;
    }

    private static string FullPath(string relativePath)
    {
        var root = Path.GetFullPath(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", ".."));
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed record BuiltCase(
        string Preset,
        string Size,
        EntityUid GridUid,
        LuaMShipBlueprintResponse Response)
    {
        public string Label => $"{Preset}/{Size}";
    }
}
