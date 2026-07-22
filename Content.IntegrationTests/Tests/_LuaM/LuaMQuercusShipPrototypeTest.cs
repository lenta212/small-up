using System.IO;
using System.Linq;
using System.Text;
using Content.Server.Cargo.Systems;
using Content.Server.Station.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMQuercusShipPrototypeTest
{
    private static readonly ProtoId<VesselPrototype> QuercusVessel = "LuaMQuercus";
    private const string QuercusGameMap = "LuaMQuercus";

    [Test]
    public async Task QuercusVesselAndGameMapLoadTheSameStationGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var map = entManager.System<MapSystem>();
        var pricing = entManager.System<PricingSystem>();

        await server.WaitPost(() =>
        {
            var vessel = protoManager.Index(QuercusVessel);
            var gameMap = protoManager.Index<GameMapPrototype>(QuercusGameMap);

            Assert.Multiple(() =>
            {
                Assert.That(vessel.Name, Is.EqualTo("LMC Quercus"));
                Assert.That(vessel.Price, Is.EqualTo(160000));
                Assert.That(vessel.Category, Is.EqualTo(VesselSize.Medium));
                Assert.That(vessel.Group, Is.EqualTo(ShipyardConsoleUiKey.Expedition));
                Assert.That(vessel.Classes, Is.EquivalentTo(new[]
                {
                    VesselClass.Expedition,
                    VesselClass.Science,
                    VesselClass.Salvage,
                    VesselClass.Medical,
                }));
                Assert.That(vessel.Engines, Is.EquivalentTo(new[] { VesselEngine.Uranium }));
                Assert.That(gameMap.MapName, Is.EqualTo(vessel.Name));
                Assert.That(gameMap.MapPath, Is.EqualTo(vessel.ShuttlePath));
                Assert.That(gameMap.Stations.Keys, Does.Contain("LuaMQuercus"));
            });

            map.CreateMap(out var mapId);
            try
            {
                Assert.That(mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var shuttle), Is.True);
                Assert.That(shuttle.HasValue, Is.True);
                Assert.That(entManager.HasComponent<MapGridComponent>(shuttle.Value), Is.True);
                Assert.That(entManager.HasComponent<BecomesStationComponent>(shuttle.Value), Is.True);
                Assert.That(entManager.GetComponent<BecomesStationComponent>(shuttle.Value).Id, Is.EqualTo("LuaMQuercus"));

                double appraisal = 0;
                pricing.AppraiseGrid(shuttle.Value, null, (_, price) => appraisal += price);
                TestContext.Progress.WriteLine($"LMC Quercus appraisal: {appraisal:F2}");
                Assert.That(vessel.Price, Is.AtLeast(appraisal * vessel.MinPriceMarkup),
                    "The Quercus shipyard price must not permit material resale arbitrage.");
                Assert.That(vessel.Price, Is.AtMost(appraisal * vessel.MaxPriceMarkup),
                    "The Quercus shipyard price must stay within the configured maximum markup.");
            }
            finally
            {
                map.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void QuercusMapContainsFlightExpeditionScienceMedicalAndSalvageSystems()
    {
        var map = LoadMapping("Resources/Maps/_LuaM/Shuttles/quercus.yml");
        var groups = Sequence(map, "entities")
            .Children
            .OfType<YamlMappingNode>()
            .ToDictionary(
                group => ScalarValue(group, "proto"),
                group => Sequence(group, "entities").Children.Count);
        var protos = groups.Keys;

        Assert.Multiple(() =>
        {
            Assert.That(protos, Does.Contain("ComputerTabletopShuttle"), "Quercus needs a flight console.");
            Assert.That(protos, Does.Contain("Thruster"), "Quercus needs conventional maneuvering thrust.");
            Assert.That(protos, Does.Contain("Gyroscope"), "Quercus needs rotational control.");
            Assert.That(protos, Does.Contain("GravityGeneratorMini"), "Quercus needs onboard gravity.");

            Assert.That(protos, Does.Contain("ComputerTabletopSalvageExpedition"), "Quercus needs an expedition console.");

            Assert.That(protos, Does.Contain("ComputerTabletopResearchAndDevelopment"), "Quercus needs an R&D console.");
            Assert.That(protos, Does.Contain("ComputerTabletopAnalysisConsole"), "Quercus needs an artifact analysis console.");
            Assert.That(protos, Does.Contain("ResearchAndDevelopmentServer"), "Quercus needs an R&D server.");
            Assert.That(protos, Does.Contain("MachineArtifactAnalyzer"), "Quercus needs artifact research equipment.");
            Assert.That(protos, Does.Contain("Protolathe"), "Quercus needs an R&D protolathe.");
            Assert.That(protos, Does.Contain("CircuitImprinter"), "Quercus needs an R&D circuit imprinter.");

            Assert.That(protos, Does.Contain("MedicalBed"), "Quercus needs a treatment bed.");
            Assert.That(protos, Does.Contain("StasisBed"), "Quercus needs a stasis bed for critical patients.");
            Assert.That(protos, Does.Contain("OperatingTable"), "Quercus needs a surgical station.");
            Assert.That(protos, Does.Contain("DefibrillatorCabinetFilled"), "Quercus needs emergency resuscitation equipment.");
            Assert.That(protos, Does.Contain("MedicalTechFab"), "Quercus needs a medical fabricator.");
            Assert.That(protos, Does.Contain("ChemDispenser"), "Quercus needs compact pharmaceutical production.");
            Assert.That(protos, Does.Contain("ChemMaster"), "Quercus needs medicine packaging equipment.");
            Assert.That(protos, Does.Contain("MedicalScanner"), "Quercus needs diagnostic equipment.");
            Assert.That(protos, Does.Contain("ComputerTabletopCloningConsole"), "Quercus needs a console linked to its medical scanner.");

            Assert.That(protos, Does.Contain("OreProcessor"), "Quercus needs limited ore processing.");
            Assert.That(protos, Does.Contain("SalvageTechfabNF"), "Quercus needs a compact salvage workshop.");
            Assert.That(protos, Does.Contain("ScrapProcessor"), "Quercus needs scrap processing.");
            Assert.That(protos, Does.Contain("MaterialReclaimer"), "Quercus needs a recycling system.");

            Assert.That(groups["Thruster"], Is.EqualTo(11), "Quercus should have five cruise, two braking and four lateral thrusters.");
            Assert.That(groups["Gyroscope"], Is.EqualTo(2), "Quercus needs redundant rotational control.");
            Assert.That(groups["AirlockGlassShuttle"] + groups["AirlockShuttle"], Is.EqualTo(4),
                "Quercus should keep its main, salvage and medical docking routes distinct.");
            Assert.That(groups["PortableGeneratorSuperPacmanShuttle"], Is.EqualTo(2),
                "Quercus needs enough generation for simultaneous research and flight loads.");
            Assert.That(groups["SubstationBasic"], Is.EqualTo(2));
            Assert.That(groups["APCBasic"], Is.EqualTo(5));
            Assert.That(groups["GasVentPump"], Is.EqualTo(4));
            Assert.That(groups["GasVentScrubber"], Is.EqualTo(4));
            Assert.That(groups["AirCanister"], Is.EqualTo(2));
            Assert.That(groups["MedicalBed"], Is.EqualTo(2));
            Assert.That(ContainsScalar(map, "ArtifactAnalyzerSender"), Is.True,
                "The artifact console must be linked to the analyzer in the saved grid.");
            Assert.That(ContainsScalar(map, "MedicalScannerSender"), Is.True,
                "The medical console must be linked to the scanner in the saved grid.");
        });
    }

    private static bool ContainsScalar(YamlNode node, string value)
    {
        if (node is YamlScalarNode scalar)
            return scalar.Value == value;
        if (node is YamlSequenceNode sequence)
            return sequence.Children.Any(child => ContainsScalar(child, value));
        if (node is YamlMappingNode mapping)
            return mapping.Children.Any(child =>
                ContainsScalar(child.Key, value) || ContainsScalar(child.Value, value));
        return false;
    }

    private static YamlMappingNode LoadMapping(string relativePath)
    {
        using var reader = new StreamReader(FullPath(relativePath), Encoding.UTF8);
        var stream = new YamlStream();
        stream.Load(reader);

        Assert.That(stream.Documents, Has.Count.EqualTo(1));
        Assert.That(stream.Documents[0].RootNode, Is.TypeOf<YamlMappingNode>());
        return (YamlMappingNode) stream.Documents[0].RootNode;
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlSequenceNode>(), $"Expected YAML key {key} to be a sequence.");
        return (YamlSequenceNode) node;
    }

    private static string ScalarValue(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), $"Expected YAML key {key} to be a scalar.");
        return ((YamlScalarNode) node).Value ?? string.Empty;
    }

    private static YamlNode Node(YamlMappingNode mapping, string key)
    {
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode { Value: var childKey } && childKey == key)
                return child.Value;
        }

        Assert.Fail($"Missing YAML key {key}.");
        throw new InvalidDataException($"Missing YAML key {key}.");
    }

    private static string FullPath(string relativePath)
    {
        return Path.Combine(
            Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..")),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
