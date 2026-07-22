using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMAquilaShipPrototypeTest
{
    private static readonly ProtoId<VesselPrototype> AquilaVessel = "Aquila";
    private const string AquilaGameMap = "Aquila";

    [Test]
    public async Task AquilaVesselAndGameMapLoadTheSameStationGrid()
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
            var vessel = protoManager.Index(AquilaVessel);
            var gameMap = protoManager.Index<GameMapPrototype>(AquilaGameMap);

            Assert.Multiple(() =>
            {
                Assert.That(vessel.Name, Is.EqualTo("TSF-SKR Aquila"));
                Assert.That(vessel.Price, Is.EqualTo(52000));
                Assert.That(vessel.Category, Is.EqualTo(VesselSize.Small));
                Assert.That(vessel.Group, Is.EqualTo(ShipyardConsoleUiKey.Security));
                Assert.That(vessel.Access, Is.EqualTo("Security"));
                Assert.That(vessel.Classes, Is.EquivalentTo(new[]
                {
                    VesselClass.Pursuit,
                    VesselClass.Fighter,
                }));
                Assert.That(vessel.Engines, Is.EquivalentTo(new[] { VesselEngine.APU }));
                Assert.That(vessel.Company, Is.EquivalentTo(new[] { "TSF" }));
                Assert.That(gameMap.MapName, Is.EqualTo(vessel.Name));
                Assert.That(gameMap.MapPath, Is.EqualTo(vessel.ShuttlePath));
                Assert.That(gameMap.Stations.Keys, Is.EquivalentTo(new[] { "Aquila" }));
            });

            map.CreateMap(out var mapId);
            try
            {
                Assert.That(mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var shuttle), Is.True);
                Assert.That(shuttle.HasValue, Is.True);
                Assert.That(entManager.HasComponent<MapGridComponent>(shuttle.Value), Is.True);
                Assert.That(entManager.HasComponent<BecomesStationComponent>(shuttle.Value), Is.True);
                Assert.That(entManager.GetComponent<BecomesStationComponent>(shuttle.Value).Id, Is.EqualTo("Aquila"));

                var mapped = new Dictionary<string, List<EntityUid>>();
                var query = entManager.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out var metadata, out var xform))
                {
                    if (xform.GridUid != shuttle.Value || metadata.EntityPrototype?.ID is not { } prototype)
                        continue;

                    if (!mapped.TryGetValue(prototype, out var entities))
                    {
                        entities = new List<EntityUid>();
                        mapped.Add(prototype, entities);
                    }

                    entities.Add(uid);
                }

                EntityUid SingleMapped(string prototype)
                {
                    Assert.That(mapped.TryGetValue(prototype, out var entities), Is.True,
                        $"Missing mapped prototype {prototype}.");
                    Assert.That(entities, Has.Count.EqualTo(1),
                        $"Expected exactly one mapped {prototype}.");
                    return entities![0];
                }

                var dockingAirlock = SingleMapped("AirlockShuttleNfsdLocked");
                var dockingTransform = entManager.GetComponent<TransformComponent>(dockingAirlock);
                Assert.Multiple(() =>
                {
                    Assert.That(dockingTransform.Anchored, Is.True);
                    Assert.That(dockingTransform.LocalRotation, Is.EqualTo(Angle.FromDegrees(180)));
                    Assert.That(entManager.HasComponent<DockingComponent>(dockingAirlock), Is.True);
                });

                foreach (var prototype in new[] { "GasVentPump", "GasVentScrubber", "AirCanister", "StorageCanister" })
                {
                    var uid = SingleMapped(prototype);
                    Assert.That(entManager.GetComponent<TransformComponent>(uid).Anchored, Is.True,
                        $"{prototype} must be anchored.");
                    Assert.That(entManager.GetComponent<AtmosDeviceComponent>(uid).JoinedGrid, Is.EqualTo(shuttle.Value.Owner),
                        $"{prototype} must join Aquila's grid atmosphere.");
                }

                double appraisal = 0;
                pricing.AppraiseGrid(shuttle.Value, null, (_, price) => appraisal += price);
                TestContext.Progress.WriteLine($"TSF-SKR Aquila appraisal: {appraisal:F2}");
                Assert.That(vessel.Price, Is.AtLeast(appraisal * vessel.MinPriceMarkup),
                    "The Aquila shipyard price must not permit material resale arbitrage.");
                Assert.That(vessel.Price, Is.AtMost(appraisal * vessel.MaxPriceMarkup),
                    "The Aquila shipyard price must stay within the configured maximum markup.");
            }
            finally
            {
                map.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void AquilaMetadataDefinesTsfSecurityInterceptor()
    {
        var prototypes = LoadSequence("Resources/Prototypes/_LuaM/Shipyard/TSFMC/aquila.yml");
        var vessel = FindPrototype(prototypes, "vessel", "Aquila");
        var gameMap = FindPrototype(prototypes, "gameMap", "Aquila");

        Assert.Multiple(() =>
        {
            Assert.That(ScalarValue(vessel, "parent"), Is.EqualTo("BaseVesselAntag"));
            Assert.That(ScalarValue(vessel, "name"), Is.EqualTo("TSF-SKR Aquila"));
            Assert.That(ScalarValue(vessel, "price"), Is.EqualTo("52000"));
            Assert.That(ScalarValue(vessel, "category"), Is.EqualTo("Small"));
            Assert.That(ScalarValue(vessel, "group"), Is.EqualTo("Security"));
            Assert.That(ScalarValue(vessel, "access"), Is.EqualTo("Security"));
            Assert.That(ScalarValue(vessel, "shuttlePath"),
                Is.EqualTo("/Maps/_LuaM/Shuttles/TSFMC/Aquila.yml"));
            Assert.That(SequenceValues(Sequence(vessel, "class")),
                Is.EquivalentTo(new[] { "Pursuit", "Fighter" }));
            Assert.That(SequenceValues(Sequence(vessel, "engine")), Is.EquivalentTo(new[] { "APU" }));
            Assert.That(SequenceValues(Sequence(vessel, "company")), Is.EquivalentTo(new[] { "TSF" }));
            Assert.That(ScalarValue(gameMap, "mapName"), Is.EqualTo("TSF-SKR Aquila"));
            Assert.That(ScalarValue(gameMap, "mapPath"),
                Is.EqualTo("/Maps/_LuaM/Shuttles/TSFMC/Aquila.yml"));
        });

        var station = Mapping(Mapping(gameMap, "stations"), "Aquila");
        Assert.That(ScalarValue(station, "stationProto"), Is.EqualTo("StandardFrontierSecurityVessel"));

        var jobs = Mapping(FindComponent(station, "StationJobs"), "availableJobs");
        Assert.Multiple(() =>
        {
            Assert.That(jobs.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value),
                Is.EquivalentTo(new[] { "Deputy", "TsfBorg" }));
            Assert.That(SequenceValues(Sequence(jobs, "Deputy")), Is.EqualTo(new[] { "0", "0" }));
            Assert.That(SequenceValues(Sequence(jobs, "TsfBorg")), Is.EqualTo(new[] { "0", "0" }));
        });
    }

    [Test]
    public void AquilaMapContainsExactInterceptorSystemsAndSurvivalFit()
    {
        var map = LoadMapping("Resources/Maps/_LuaM/Shuttles/TSFMC/Aquila.yml");
        var groups = Sequence(map, "entities")
            .Children
            .OfType<YamlMappingNode>()
            .ToDictionary(
                group => ScalarValue(group, "proto"),
                group => Sequence(group, "entities").Children.Count);
        var protos = groups.Keys;

        Assert.Multiple(() =>
        {
            Assert.That(groups["WeaponLaserTurretL1Phalanx"], Is.EqualTo(2));
            Assert.That(groups["WeaponTurretLightMunitionsBay"], Is.EqualTo(1));
            Assert.That(groups["LightFighterOrdinanceZenithItem"], Is.EqualTo(2));
            Assert.That(groups["ThrusterNfsd"], Is.EqualTo(10));
            Assert.That(groups["SmallGyroscopeNfsd"], Is.EqualTo(2));
            Assert.That(groups["GeneratorWallmountAPU"], Is.EqualTo(3));
            Assert.That(groups["AirlockShuttleNfsdLocked"], Is.EqualTo(1));
            Assert.That(groups["GunneryServerLow"], Is.EqualTo(1));

            Assert.That(groups["ComputerShuttle"], Is.EqualTo(1));
            Assert.That(groups["ComputerGunneryConsole"], Is.EqualTo(1));
            Assert.That(groups["ComputerWallmountAdvancedRadar"], Is.EqualTo(1));
            Assert.That(groups["ComputerIFF"], Is.EqualTo(1));
            Assert.That(groups["MachineFTLDrive"], Is.EqualTo(1));
            Assert.That(groups["GravityGeneratorMini"], Is.EqualTo(1));

            Assert.That(groups["SubstationWallBasic"], Is.EqualTo(1));
            Assert.That(groups["APCBasic"], Is.EqualTo(1));
            Assert.That(protos, Does.Contain("CableHV"));
            Assert.That(protos, Does.Contain("CableMV"));
            Assert.That(protos, Does.Contain("CableApcExtension"));

            Assert.That(groups["AirCanister"], Is.EqualTo(1));
            Assert.That(groups["StorageCanister"], Is.EqualTo(1));
            Assert.That(groups["GasPort"], Is.EqualTo(2));
            Assert.That(groups["GasVentPump"], Is.EqualTo(1));
            Assert.That(groups["GasVentScrubber"], Is.EqualTo(1));
            Assert.That(groups["AirAlarm"], Is.EqualTo(1));
            Assert.That(protos, Does.Not.Contain("GasPipeStraight"),
                "A unary vent or scrubber cannot anchor on an overlapping straight pipe.");

            Assert.That(groups["SuitStorageWallmountEVANfsd"], Is.EqualTo(1));
            Assert.That(groups["ExtinguisherCabinetFilled"], Is.EqualTo(1));
            Assert.That(groups["DefibrillatorCabinetFilled"], Is.EqualTo(1));
            Assert.That(groups["MedkitCombatFilled"], Is.EqualTo(1));
            Assert.That(groups["SpawnPointLatejoin"], Is.EqualTo(1));
            Assert.That(groups["WarpPoint"], Is.EqualTo(1));

            Assert.That(protos, Does.Not.Contain("Bed"));
            Assert.That(protos, Does.Not.Contain("MedicalBed"));
            Assert.That(protos, Does.Not.Contain("StasisBed"));
            Assert.That(protos, Does.Not.Contain("OperatingTable"));
            Assert.That(protos.Any(proto => proto.StartsWith("ShieldGenerator")), Is.False,
                "Aquila is deliberately an unshielded short-range fighter.");
        });
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

    private static string ScalarValue(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), $"Expected YAML key {key} to be a scalar.");
        return ((YamlScalarNode) node).Value ?? string.Empty;
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
