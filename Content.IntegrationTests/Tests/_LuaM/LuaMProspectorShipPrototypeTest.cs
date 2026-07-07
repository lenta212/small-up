using System.IO;
using System.Linq;
using System.Text;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMProspectorShipPrototypeTest
{
    private static readonly ProtoId<VesselPrototype> ProspectorVessel = "LuaMProspector";

    [Test]
    public async Task ProspectorVesselPrototypeLoadsSpawnableGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var map = entManager.System<MapSystem>();

        await server.WaitPost(() =>
        {
            var vessel = protoManager.Index(ProspectorVessel);

            map.CreateMap(out var mapId);
            try
            {
                Assert.That(mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var shuttle), Is.True);
                Assert.That(shuttle.HasValue, Is.True);
                Assert.That(entManager.HasComponent<MapGridComponent>(shuttle.Value), Is.True);
            }
            finally
            {
                map.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void ProspectorIsRegisteredAsMediumExpeditionSupportShip()
    {
        var prototypes = LoadSequence("Resources/Prototypes/_LuaM/Shipyard/prospector.yml");
        var vessel = FindPrototype(prototypes, "vessel", "LuaMProspector");
        var gameMap = FindPrototype(prototypes, "gameMap", "LuaMProspector");

        Assert.That(ScalarValue(vessel, "name"), Is.EqualTo("LMC Prospector"));
        Assert.That(ScalarValue(vessel, "category"), Is.EqualTo("Medium"));
        Assert.That(ScalarValue(vessel, "group"), Is.EqualTo("Expedition"));
        Assert.That(ScalarValue(vessel, "shuttlePath"), Is.EqualTo("/Maps/_LuaM/Shuttles/prospector.yml"));
        Assert.That(SequenceValues(Sequence(vessel, "class")), Is.EquivalentTo(new[]
        {
            "Expedition",
            "Science",
            "Salvage",
            "Medical",
        }));

        Assert.That(ScalarValue(gameMap, "mapName"), Is.EqualTo("LMC Prospector"));
        Assert.That(ScalarValue(gameMap, "mapPath"), Is.EqualTo("/Maps/_LuaM/Shuttles/prospector.yml"));

        var station = Mapping(Mapping(gameMap, "stations"), "LuaMProspector");
        Assert.That(ScalarValue(station, "stationProto"), Is.EqualTo("StandardFrontierExpeditionVessel"));

        var jobs = Mapping(FindComponent(station, "StationJobs"), "availableJobs");
        Assert.That(jobs.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value), Does.Contain("MdMedic"));
    }

    [Test]
    public void ProspectorMapContainsExpeditionMiningScienceAndFieldMedicalEquipment()
    {
        var map = LoadMapping("Resources/Maps/_LuaM/Shuttles/prospector.yml");
        Assert.That(ScalarValue(Mapping(map, "meta"), "entityCount"), Is.EqualTo("633"));

        var protos = Sequence(map, "entities")
            .Children
            .OfType<YamlMappingNode>()
            .Select(group => ScalarValue(group, "proto"))
            .ToHashSet();

        Assert.That(protos, Does.Contain("ComputerSalvageExpedition"));
        Assert.That(protos, Does.Contain("OreProcessor"));
        Assert.That(protos, Does.Contain("SalvageTechfabNF"));
        Assert.That(protos, Does.Contain("ComputerResearchAndDevelopment"));
        Assert.That(protos, Does.Contain("ResearchAndDevelopmentServer"));
        Assert.That(protos, Does.Contain("MachineArtifactAnalyzer"));
        Assert.That(protos, Does.Contain("Protolathe"));
        Assert.That(protos, Does.Contain("DefibrillatorCabinetFilled"));
        Assert.That(protos, Does.Contain("MedkitFilled"));
        Assert.That(protos, Does.Contain("MedkitBruteFilled"));
        Assert.That(protos, Does.Contain("MedkitBurnFilled"));
        Assert.That(protos, Does.Contain("MedkitOxygenFilled"));
        Assert.That(protos, Does.Contain("MedkitToxinFilled"));
    }

    private static YamlMappingNode FindComponent(YamlMappingNode entity, string componentType)
    {
        var matches = Sequence(entity, "components")
            .Children
            .OfType<YamlMappingNode>()
            .Where(component => ScalarValue(component, "type") == componentType)
            .ToList();

        Assert.That(matches, Has.Count.EqualTo(1), $"Expected one {componentType} component.");
        return matches[0];
    }

    private static YamlMappingNode FindPrototype(YamlSequenceNode prototypes, string type, string id)
    {
        var matches = prototypes
            .Children
            .OfType<YamlMappingNode>()
            .Where(prototype => ScalarValue(prototype, "type") == type && ScalarValue(prototype, "id") == id)
            .ToList();

        Assert.That(matches, Has.Count.EqualTo(1), $"Expected one {type} prototype with id {id}.");
        return matches[0];
    }

    private static string[] SequenceValues(YamlSequenceNode sequence)
    {
        return sequence.Children.Select(ScalarValue).ToArray();
    }

    private static YamlSequenceNode LoadSequence(string relativePath)
    {
        using var reader = new StreamReader(FullPath(relativePath), Encoding.UTF8);
        var stream = new YamlStream();
        stream.Load(reader);

        Assert.That(stream.Documents, Has.Count.EqualTo(1));
        Assert.That(stream.Documents[0].RootNode, Is.TypeOf<YamlSequenceNode>());
        return (YamlSequenceNode) stream.Documents[0].RootNode;
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

    private static YamlMappingNode Mapping(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlMappingNode>(), $"Expected YAML key {key} to be a mapping.");
        return (YamlMappingNode) node;
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlSequenceNode>(), $"Expected YAML key {key} to be a sequence.");
        return (YamlSequenceNode) node;
    }

    private static YamlScalarNode Scalar(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), $"Expected YAML key {key} to be a scalar.");
        return (YamlScalarNode) node;
    }

    private static string ScalarValue(YamlMappingNode mapping, string key)
    {
        return Scalar(mapping, key).Value ?? string.Empty;
    }

    private static string ScalarValue(YamlNode node)
    {
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), "Expected YAML sequence entry to be a scalar.");
        return ((YamlScalarNode) node).Value ?? string.Empty;
    }

    private static YamlNode Node(YamlMappingNode mapping, string key)
    {
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode { Value: var childKey } &&
                childKey == key)
            {
                return child.Value;
            }
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
