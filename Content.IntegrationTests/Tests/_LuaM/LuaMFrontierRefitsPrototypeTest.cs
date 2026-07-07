using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMFrontierRefitsPrototypeTest
{
    private const string FrontierMapRoot = "/Maps/_LuaM/Shuttles/Frontier/";

    private static readonly Regex ShipWeaponRegex = new(@"(?m)^- proto: (Weapon|ShuttleGun|ComputerGunnery)");
    private static readonly Regex ThrusterRegex = new(@"(?m)^- proto: .*Thruster");
    private static readonly Regex GyroscopeRegex = new(@"(?m)^- proto: Gyroscope");
    private static readonly Regex OrdinaryHullRegex = new(@"(?m)^- proto: (WallSolid|WallSolidDiagonal|Window|WindowDirectional|WindowDiagonal)\s*$");

    private static readonly Dictionary<string, int> BasePrices = new()
    {
        ["LuaMFrontierOre01"] = 155000,
        ["LuaMFrontierOre02"] = 104900,
        ["LuaMFrontierOre03"] = 110000,
        ["LuaMFrontierOre04"] = 52100,
        ["LuaMFrontierOre05"] = 59525,
        ["LuaMFrontierOre06"] = 45000,
        ["LuaMFrontierOre07"] = 58250,
        ["LuaMFrontierOre08"] = 37900,
        ["LuaMFrontierOre09"] = 128000,
        ["LuaMFrontierOre10"] = 106000,
        ["LuaMFrontierExpedition01"] = 40600,
        ["LuaMFrontierExpedition02"] = 69420,
        ["LuaMFrontierExpedition03"] = 70001,
        ["LuaMFrontierExpedition04"] = 77150,
        ["LuaMFrontierExpedition05"] = 79000,
        ["LuaMFrontierExpedition06"] = 80750,
        ["LuaMFrontierExpedition07"] = 83000,
        ["LuaMFrontierExpedition08"] = 213000,
        ["LuaMFrontierExpedition09"] = 245000,
        ["LuaMFrontierExpedition10"] = 120000,
        ["LuaMFrontierMedical01"] = 120000,
        ["LuaMFrontierMedical02"] = 134750,
        ["LuaMFrontierMedical03"] = 145530,
        ["LuaMFrontierMedical04"] = 69500,
        ["LuaMFrontierMedical05"] = 76080,
        ["LuaMFrontierMedical06"] = 76500,
        ["LuaMFrontierMedical07"] = 95000,
        ["LuaMFrontierMedical08"] = 106500,
        ["LuaMFrontierMedical09"] = 157000,
        ["LuaMFrontierMedical10"] = 49200,
        ["LuaMFrontierScience01"] = 120000,
        ["LuaMFrontierScience02"] = 250000,
        ["LuaMFrontierScience03"] = 270000,
        ["LuaMFrontierScience04"] = 99000,
        ["LuaMFrontierScience05"] = 36900,
        ["LuaMFrontierScience06"] = 91000,
        ["LuaMFrontierScience07"] = 57500,
        ["LuaMFrontierScience08"] = 875800,
        ["LuaMFrontierScience09"] = 80000,
        ["LuaMFrontierScience10"] = 262000,
    };

    [Test]
    public async Task FrontierRefitsLoadAsVesselPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoManager = server.ResolveDependency<IPrototypeManager>();

        await server.WaitPost(() =>
        {
            var vessels = protoManager
                .EnumeratePrototypes<VesselPrototype>()
                .Where(proto => proto.ID.StartsWith("LuaMFrontier"))
                .ToList();

            Assert.That(vessels, Has.Count.EqualTo(40));
            Assert.That(vessels.Count(proto => proto.ID.StartsWith("LuaMFrontierOre")), Is.EqualTo(10));
            Assert.That(vessels.Count(proto => proto.ID.StartsWith("LuaMFrontierExpedition")), Is.EqualTo(10));
            Assert.That(vessels.Count(proto => proto.ID.StartsWith("LuaMFrontierMedical")), Is.EqualTo(10));
            Assert.That(vessels.Count(proto => proto.ID.StartsWith("LuaMFrontierScience")), Is.EqualTo(10));
            Assert.That(vessels.All(proto => proto.Price > BasePrices[proto.ID]), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void FrontierRefitsProvideTenArmedVariantsPerRole()
    {
        var prototypes = LoadSequence("Resources/Prototypes/_LuaM/Shipyard/frontier_refits.yml");
        var vessels = PrototypesOfType(prototypes, "vessel").ToList();
        var gameMaps = PrototypesOfType(prototypes, "gameMap").ToDictionary(proto => ScalarValue(proto, "id"));

        Assert.That(vessels, Has.Count.EqualTo(40));
        Assert.That(vessels.Count(proto => ScalarValue(proto, "id").StartsWith("LuaMFrontierOre")), Is.EqualTo(10));
        Assert.That(vessels.Count(proto => ScalarValue(proto, "id").StartsWith("LuaMFrontierExpedition")), Is.EqualTo(10));
        Assert.That(vessels.Count(proto => ScalarValue(proto, "id").StartsWith("LuaMFrontierMedical")), Is.EqualTo(10));
        Assert.That(vessels.Count(proto => ScalarValue(proto, "id").StartsWith("LuaMFrontierScience")), Is.EqualTo(10));
        Assert.That(Directory.GetFiles(FullPath("Resources/Maps/_LuaM/Shuttles/Frontier"), "LuaMFrontier*.yml"), Has.Length.EqualTo(40));

        foreach (var vessel in vessels)
        {
            var id = ScalarValue(vessel, "id");
            var name = ScalarValue(vessel, "name");
            Assert.That(BasePrices, Does.ContainKey(id));
            Assert.That(int.Parse(ScalarValue(vessel, "price")), Is.GreaterThan(BasePrices[id]), id);
            Assert.That(ScalarValue(vessel, "description"), Does.Contain("orvarod"), id);
            Assert.That(ScalarValue(vessel, "description"), Does.Contain("armed"), id);

            var path = ScalarValue(vessel, "shuttlePath");
            Assert.That(path, Does.StartWith(FrontierMapRoot), id);
            Assert.That(Path.GetFileNameWithoutExtension(path), Is.EqualTo(id), id);
            Assert.That(gameMaps, Does.ContainKey(id), id);
            Assert.That(ScalarValue(gameMaps[id], "mapPath"), Is.EqualTo(path), id);
            Assert.That(ScalarValue(gameMaps[id], "mapName"), Is.EqualTo(name), id);

            var mapText = File.ReadAllText(FullPath(Path.Combine("Resources", path.TrimStart('/'))), Encoding.UTF8);
            Assert.That(mapText, Does.Contain("# GitHub: orvarod"), id);
            Assert.That(HasFrontierGridName(mapText, name), Is.True, $"{id} should have a physical map grid name.");
            Assert.That(HasGeneratedStationId(mapText, id), Is.True, $"{id} should have its own BecomesStation id.");
            Assert.That(OrdinaryHullRegex.IsMatch(mapText), Is.False, $"{id} should not keep ordinary wall/window hull pieces.");
            Assert.That(HasShipWeapon(mapText), Is.True, $"{id} should use an armed base map.");
            Assert.That(HasDriveEquipment(mapText), Is.True, $"{id} should use a mobile ship base map.");
        }
    }

    [Test]
    public void FrontierMedicalAndExpeditionRefitsExposeExpectedStationSupport()
    {
        var prototypes = LoadSequence("Resources/Prototypes/_LuaM/Shipyard/frontier_refits.yml");
        var gameMaps = PrototypesOfType(prototypes, "gameMap").ToList();

        foreach (var gameMap in gameMaps.Where(proto => ScalarValue(proto, "id").StartsWith("LuaMFrontierExpedition")))
        {
            var id = ScalarValue(gameMap, "id");
            var station = Mapping(Mapping(gameMap, "stations"), id);
            Assert.That(ScalarValue(station, "stationProto"), Is.EqualTo("StandardFrontierExpeditionVessel"), id);
        }

        foreach (var gameMap in gameMaps.Where(proto => ScalarValue(proto, "id").StartsWith("LuaMFrontierMedical")))
        {
            var id = ScalarValue(gameMap, "id");
            var station = Mapping(Mapping(gameMap, "stations"), id);
            var jobs = Mapping(FindComponent(station, "StationJobs"), "availableJobs");
            Assert.That(jobs.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value), Does.Contain("MdMedic"), id);
        }
    }

    private static bool HasShipWeapon(string mapText)
    {
        return ShipWeaponRegex.IsMatch(mapText);
    }

    private static bool HasDriveEquipment(string mapText)
    {
        return ThrusterRegex.IsMatch(mapText) &&
               GyroscopeRegex.IsMatch(mapText);
    }

    private static bool HasFrontierGridName(string mapText, string name)
    {
        return HasLine(mapText, $"      name: {name}");
    }

    private static bool HasGeneratedStationId(string mapText, string id)
    {
        return HasLine(mapText, $"      id: {id}");
    }

    private static bool HasLine(string text, string line)
    {
        return text
            .Split('\n')
            .Any(rawLine => rawLine.TrimEnd('\r') == line);
    }

    private static IEnumerable<YamlMappingNode> PrototypesOfType(YamlSequenceNode prototypes, string type)
    {
        return prototypes
            .Children
            .OfType<YamlMappingNode>()
            .Where(prototype => ScalarValue(prototype, "type") == type);
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

    private static YamlSequenceNode LoadSequence(string relativePath)
    {
        using var reader = new StreamReader(FullPath(relativePath), Encoding.UTF8);
        var stream = new YamlStream();
        stream.Load(reader);

        Assert.That(stream.Documents, Has.Count.EqualTo(1));
        Assert.That(stream.Documents[0].RootNode, Is.TypeOf<YamlSequenceNode>());
        return (YamlSequenceNode) stream.Documents[0].RootNode;
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
