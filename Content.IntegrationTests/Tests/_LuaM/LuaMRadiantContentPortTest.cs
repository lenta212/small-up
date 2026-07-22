using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server._Mono.Radar;
using Content.Server._NF.CryoSleep;
using Content.Shared.FixedPoint;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Silicons.Bots;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMRadiantContentPortTest
{
    [Test]
    public async Task AdaptedShipAndMedibotPrototypeContractsAreStable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();
        var resources = server.ResolveDependency<IResourceManager>();

        await server.WaitAssertion(() =>
        {
            AssertVessel(
                prototypes,
                resources,
                "Gornyak",
                "HOM Горняк",
                69000,
                ShipyardConsoleUiKey.Shipyard,
                new[] { VesselClass.Salvage },
                new[] { VesselEngine.Uranium },
                "/Maps/_LuaM/Shuttles/Gornyak.yml");

            AssertVessel(
                prototypes,
                resources,
                "Salomandra",
                "USS Саламандра",
                105000,
                ShipyardConsoleUiKey.Medical,
                new[] { VesselClass.Medical, VesselClass.Chemistry, VesselClass.Botany },
                new[] { VesselEngine.Bananium },
                "/Maps/_LuaM/Shuttles/Salomandra.yml");

            var thrusterPrototype = prototypes.Index<EntityPrototype>("ThrusterRadiantMediumLuaM");
            Assert.That(
                thrusterPrototype.TryGetComponent<ThrusterComponent>(out var thruster, components),
                Is.True);
            Assert.That(
                thrusterPrototype.TryGetComponent<ApcPowerReceiverComponent>(out var power, components),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(thruster.BaseThrust, Is.EqualTo(300));
                Assert.That(thruster.Thrust, Is.EqualTo(300));
                Assert.That(power.Load, Is.EqualTo(3750));
                Assert.That(thrusterPrototype.TryGetComponent<RadarBlipComponent>(out _, components), Is.True);
                Assert.That(thrusterPrototype.TryGetComponent<ShipRepairableComponent>(out _, components), Is.True);
            });

            var medibotPrototype = prototypes.Index<EntityPrototype>("MobMedibot");
            Assert.That(
                medibotPrototype.TryGetComponent<MedibotComponent>(out var medibot, components),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(medibot.Treatments[MobState.Alive].Reagent.Id, Is.EqualTo("Tricordrazine"));
                Assert.That(medibot.Treatments[MobState.Alive].Quantity, Is.EqualTo(FixedPoint2.New(10)));
                Assert.That(medibot.Treatments[MobState.Critical].Reagent.Id, Is.EqualTo("Inaprovaline"));
            });

            var graph = ReadResource(resources, "/Prototypes/Recipes/Crafting/Graphs/bots/medibot.yml");
            foreach (var key in new[]
                     {
                         "construction-graph-tag-medkit",
                         "construction-graph-tag-health-analyzer",
                         "construction-graph-tag-proximity-sensor",
                         "construction-graph-tag-borg-arm",
                     })
            {
                Assert.That(graph, Does.Contain($"name: {key}"));
                Assert.That(ReadResource(resources, "/Locale/en-US/_NF/recipes/tags.ftl"), Does.Contain(key));
                Assert.That(ReadResource(resources, "/Locale/ru-RU/recipes/tags.ftl"), Does.Contain(key));
            }

            AssertAdaptedMapText(
                ReadResource(resources, "/Maps/_LuaM/Shuttles/Gornyak.yml"),
                "RadiantWallPlastitanium",
                "WindowRadiant",
                "ClothingUnderwearBottomRadiantPantiesPink");
            AssertAdaptedMapText(
                ReadResource(resources, "/Maps/_LuaM/Shuttles/Salomandra.yml"),
                "ThrusterZeta",
                "FloorDarkMonoRadiant",
                "FloorShuttleBlackRadiant",
                "techfloororange_",
                "trimline_",
                "bordercolorhalf_");

            var gornyakPrototypeText =
                ReadResource(resources, "/Prototypes/_LuaM/Shipyard/Gornyak.yml");
            var salomandraPrototypeText =
                ReadResource(resources, "/Prototypes/_LuaM/Shipyard/Salomandra.yml");
            Assert.Multiple(() =>
            {
                Assert.That(gornyakPrototypeText, Does.Contain("StandardFrontierExpeditionVessel"));
                Assert.That(gornyakPrototypeText, Does.Not.Contain("ContractorInterview"));
                Assert.That(salomandraPrototypeText, Does.Contain("MdMedic: [ 0, 0 ]"));
                Assert.That(salomandraPrototypeText, Does.Not.Contain("PilotInterview"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AdaptedShipMapsLoadAndKeepRequiredEquipment()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var mapLoader = entities.System<MapLoaderSystem>();
        var mapSystem = entities.System<MapSystem>();
        var pricing = entities.System<PricingSystem>();

        await server.WaitPost(() =>
        {
            foreach (var vesselId in new[] { "Gornyak", "Salomandra" })
            {
                var vessel = prototypes.Index<VesselPrototype>(vesselId);
                mapSystem.CreateMap(out var mapId);
                try
                {
                    Assert.That(
                        mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var grid),
                        Is.True,
                        $"Failed to load adapted Radiant ship {vesselId}.");
                    Assert.That(grid.HasValue, Is.True);
                    Assert.That(entities.HasComponent<MapGridComponent>(grid.Value), Is.True);

                    var mapped = EntitiesOnGridByPrototype(entities, grid.Value);
                    if (vesselId == "Gornyak")
                    {
                        AssertCount(mapped, "MiningDrill", 5);
                        AssertCount(mapped, "OreProcessor", 1);
                        AssertCount(mapped, "ComputerTabletopShuttle", 1);
                        AssertCount(mapped, "ComputerTabletopRadar", 1);
                    }
                    else
                    {
                        AssertCount(mapped, "CryoPod", 1);
                        AssertCount(mapped, "StasisBed", 1);
                        AssertCount(mapped, "OperatingTable", 1);
                        AssertCount(mapped, "MedicalTechFab", 1);
                        AssertCount(mapped, "ChemDispenser", 1);
                        AssertCount(mapped, "ThrusterRadiantMediumLuaM", 2);

                        var cryoPod = mapped["CryoPod"].Single();
                        Assert.That(
                            entities.HasComponent<CryoSleepComponent>(cryoPod),
                            Is.False,
                            "Salomandra uses a medical cryopod, not LuaM deep cryo.");
                    }

                    double appraisal = 0;
                    pricing.AppraiseGrid(grid.Value, null, (_, price) => appraisal += price);
                    TestContext.Progress.WriteLine($"{vessel.Name} appraisal: {appraisal:F2}");
                    Assert.That(vessel.Price, Is.AtLeast(appraisal * vessel.MinPriceMarkup),
                        $"{vessel.Name} must not allow material resale arbitrage.");
                    Assert.That(vessel.Price, Is.AtMost(appraisal * vessel.MaxPriceMarkup),
                        $"{vessel.Name} must stay inside the configured maximum markup.");
                }
                finally
                {
                    mapSystem.DeleteMap(mapId);
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertVessel(
        IPrototypeManager prototypes,
        IResourceManager resources,
        string id,
        string name,
        int price,
        ShipyardConsoleUiKey group,
        IEnumerable<VesselClass> classes,
        IEnumerable<VesselEngine> engines,
        string mapPath)
    {
        var vessel = prototypes.Index<VesselPrototype>(id);
        var gameMap = prototypes.Index<GameMapPrototype>(id);
        var expectedPath = new ResPath(mapPath);

        Assert.Multiple(() =>
        {
            Assert.That(vessel.Name, Is.EqualTo(name));
            Assert.That(vessel.Description, Is.Not.Empty);
            Assert.That(vessel.Price, Is.EqualTo(price));
            Assert.That(vessel.Category, Is.EqualTo(VesselSize.Medium));
            Assert.That(vessel.Group, Is.EqualTo(group));
            Assert.That(vessel.Purchasable, Is.True);
            Assert.That(vessel.Classes, Is.EquivalentTo(classes));
            Assert.That(vessel.Engines, Is.EquivalentTo(engines));
            Assert.That(vessel.ShuttlePath, Is.EqualTo(expectedPath));
            Assert.That(gameMap.MapName, Is.EqualTo(name));
            Assert.That(gameMap.MapPath, Is.EqualTo(expectedPath));
            Assert.That(gameMap.Stations.Keys, Does.Contain(id));
            Assert.That(resources.ContentFileExists(expectedPath), Is.True);
        });
    }

    private static Dictionary<string, List<EntityUid>> EntitiesOnGridByPrototype(
        IEntityManager entities,
        EntityUid gridUid)
    {
        var mapped = new Dictionary<string, List<EntityUid>>();
        var query = entities.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var metadata, out var transform))
        {
            if (transform.GridUid != gridUid || metadata.EntityPrototype?.ID is not { } prototype)
                continue;

            if (!mapped.TryGetValue(prototype, out var matches))
            {
                matches = new List<EntityUid>();
                mapped.Add(prototype, matches);
            }

            matches.Add(uid);
        }

        return mapped;
    }

    private static void AssertCount(
        IReadOnlyDictionary<string, List<EntityUid>> mapped,
        string prototype,
        int expected)
    {
        Assert.That(mapped.TryGetValue(prototype, out var matches), Is.True,
            $"Missing required mapped prototype {prototype}.");
        Assert.That(matches, Has.Count.EqualTo(expected), $"Unexpected mapped {prototype} count.");
    }

    private static string ReadResource(IResourceManager resources, string path)
    {
        var resourcePath = new ResPath(path);
        Assert.That(resources.ContentFileExists(resourcePath), Is.True, $"Missing resource {path}.");
        using var stream = resources.ContentFileRead(resourcePath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertAdaptedMapText(string text, params string[] forbidden)
    {
        Assert.That(text, Does.Contain("Source (pinned):"));
        foreach (var value in forbidden)
            Assert.That(text, Does.Not.Contain(value), $"Incompatible Radiant token remains: {value}");
    }
}
