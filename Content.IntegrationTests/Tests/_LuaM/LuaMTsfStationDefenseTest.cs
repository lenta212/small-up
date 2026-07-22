using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server._Mono.NPC.HTN;
using Content.Server._Mono.NPC.HTN.Operators;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Server.NPC.Queries;
using Content.Server.NPC.Queries.Queries;
using Content.Server.NPC.Systems;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Company;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMTsfStationDefenseTest
{
    [Test]
    public async Task DefenseControllerAndQueryTargetOnlyPdvShips()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var componentFactory = server.ResolveDependency<IComponentFactory>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var maps = entities.System<SharedMapSystem>();
        var metadata = entities.System<MetaDataSystem>();
        var shuttle = entities.System<ShuttleSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var utility = entities.System<NPCUtilitySystem>();
        var testMap = await pair.CreateTestMap();

        try
        {
            await server.WaitAssertion(() =>
            {
                var serverPrototype = prototypes.Index<EntityPrototype>("GunneryServerStationTsfDefense");
                Assert.That(
                    serverPrototype.TryGetComponent<HTNComponent>(out var htn, componentFactory),
                    Is.True);
                Assert.That(htn.RootTask.Task, Is.EqualTo("TsfStationDefenseCompound"));

                var compound = prototypes.Index<HTNCompoundPrototype>("TsfStationDefenseCompound");
                var tasks = compound.Branches.Single().Tasks.Cast<HTNPrimitiveTask>().ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(tasks, Has.Length.EqualTo(2));
                    Assert.That(tasks[0].Operator, Is.TypeOf<UtilityOperator>());
                    Assert.That(((UtilityOperator)tasks[0].Operator).Prototype,
                        Is.EqualTo("NearbyPdvShuttleTargets"));
                    Assert.That(tasks[1].Operator, Is.TypeOf<ShipFireGunsOperator>());
                    Assert.That(tasks[1].Services.Single().Prototype,
                        Is.EqualTo("NearbyPdvShuttleTargets"));
                });

                var queryPrototype = prototypes.Index<UtilityQueryPrototype>("NearbyPdvShuttleTargets");
                var targetQuery = queryPrototype.Query.OfType<NearbyNpcTargetsQuery>().Single();
                Assert.That(targetQuery.TargetCompanies, Is.EquivalentTo(new[]
                {
                    (ProtoId<CompanyPrototype>) "PDV",
                }));

                AssertGridCompany(prototypes, "TSFMCHalcyon", "TSFMCHalcyon", "TSF");
                AssertGridCompany(prototypes, "HeliosFortress", "HeliosFortress", "PDV");

                var observerGrid = CreateGrid(Vector2.Zero);
                var pdvGrid = CreateGrid(new Vector2(10f, 0f), "PDV");
                var tsfGrid = CreateGrid(new Vector2(20f, 0f), "TSF");
                var unmarkedGrid = CreateGrid(new Vector2(30f, 0f));
                metadata.SetEntityName(pdvGrid, "Vanguard Test Ship");
                var observer = entities.SpawnEntity(null, new EntityCoordinates(observerGrid, new Vector2(0.5f, 0.5f)));
                var pdvTarget = CreateTarget(pdvGrid);
                var tsfTarget = CreateTarget(tsfGrid);
                var unmarkedTarget = CreateTarget(unmarkedGrid);
                var blackboard = new NPCBlackboard();
                blackboard.SetValue(NPCBlackboard.Owner, observer);

                var result = utility.GetEntities(blackboard, "NearbyPdvShuttleTargets", bestOnly: false);

                Assert.Multiple(() =>
                {
                    Assert.That(result.Entities.Keys, Is.EquivalentTo(new[] { pdvTarget }));
                    Assert.That(result.Entities, Does.Not.ContainKey(tsfTarget));
                    Assert.That(result.Entities, Does.Not.ContainKey(unmarkedTarget));
                    Assert.That(shuttle.GetIFFLabel(pdvGrid), Is.EqualTo("Vanguard Test Ship\nPDV"));
                });

                EntityUid CreateGrid(Vector2 position, string company = null)
                {
                    var grid = mapManager.CreateGridEntity(testMap.MapId);
                    maps.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
                    transform.SetLocalPosition(grid.Owner, position);

                    if (company != null)
                        entities.AddComponent<CompanyComponent>(grid.Owner).CompanyName = company;

                    return grid.Owner;
                }

                EntityUid CreateTarget(EntityUid grid)
                {
                    var target = entities.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
                    entities.AddComponent<ShipNpcTargetComponent>(target);
                    return target;
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public void HalcyonMapUsesAutomatedTsfGunneryServer()
    {
        var map = LoadMapping("Resources/Maps/_LuaM/POI/HalcyonLuaM.yml");
        var prototypes = Sequence(map, "entities")
            .Children
            .OfType<YamlMappingNode>()
            .Select(group => ScalarValue(group, "proto"))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(prototypes, Contains.Item("GunneryServerStationTsfDefense"));
            Assert.That(prototypes, Does.Not.Contain("GunneryServerStation"));
        });
    }

    private static void AssertGridCompany(
        IPrototypeManager prototypes,
        string mapId,
        string stationId,
        string expectedCompany)
    {
        var gameMap = prototypes.Index<GameMapPrototype>(mapId);
        var company = gameMap.Stations[stationId]
            .gridComponents
            .Values
            .Select(entry => entry.Component)
            .OfType<CompanyComponent>()
            .Single();

        Assert.That(company.CompanyName, Is.EqualTo((ProtoId<CompanyPrototype>)expectedCompany));
    }

    private static YamlMappingNode LoadMapping(string relativePath)
    {
        using var reader = new StreamReader(FullPath(relativePath));
        var stream = new YamlStream();
        stream.Load(reader);

        Assert.That(stream.Documents, Has.Count.EqualTo(1));
        Assert.That(stream.Documents[0].RootNode, Is.TypeOf<YamlMappingNode>());
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlSequenceNode>(), $"Expected YAML key {key} to be a sequence.");
        return (YamlSequenceNode)node;
    }

    private static string ScalarValue(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), $"Expected YAML key {key} to be a scalar.");
        return ((YamlScalarNode)node).Value ?? string.Empty;
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
