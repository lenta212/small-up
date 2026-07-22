#nullable enable

using System.Linq;
using System.Reflection;
using Content.Server._LuaM.Radar;
using Content.Server._Mono.Radar;
using Robust.Server.Containers;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMFreeBrainRadarSystem))]
public sealed class LuaMFreeBrainRadarTest
{
    private const string HumanBrain = "OrganHumanBrain";
    private const string DionaBrain = "OrganDionaBrain";
    private const string ForeignBlipBrain = "LuaMForeignBlipBrain";

    [TestPrototypes]
    private const string TestPrototypes = @"
- type: entity
  id: LuaMForeignBlipBrain
  parent: BaseItem
  components:
  - type: LuaMFreeBrainRadar
  - type: RadarBlip
    radarColor: ""#FF0000""
    maxDistance: 73
";

    [Test]
    public async Task FreeHumanAndDionaBrainsReceiveOwnedRadarBlips()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var human = entities.SpawnEntity(HumanBrain, MapCoordinates.Nullspace);
            var diona = entities.SpawnEntity(DionaBrain, MapCoordinates.Nullspace);

            Assert.Multiple(() =>
            {
                AssertOwnedBrainBlip(entities, human);
                AssertOwnedBrainBlip(entities, diona);
                Assert.That(
                    typeof(LuaMFreeBrainRadarComponent)
                        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(field => field.FieldType == typeof(EntityUid)),
                    Is.False,
                    "The persistent marker must not contain a runtime entity reference.");
                Assert.That(
                    typeof(LuaMFreeBrainRadarOwnedComponent)
                        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(field => field.FieldType == typeof(EntityUid)),
                    Is.False,
                    "Owned-blip provenance must be reference-free and safe for deep cryo.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InsertingAndRemovingBrainUpdatesOnlyOwnedBlip()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();

        await server.WaitAssertion(() =>
        {
            var brain = entities.SpawnEntity(HumanBrain, MapCoordinates.Nullspace);
            var owner = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var container = containers.EnsureContainer<Container>(owner, "LuaMFreeBrainRadarTestContainer");

            AssertOwnedBrainBlip(entities, brain);
            Assert.That(containers.Insert(brain, container), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<RadarBlipComponent>(brain), Is.False);
                Assert.That(entities.HasComponent<LuaMFreeBrainRadarOwnedComponent>(brain), Is.False);
                Assert.That(entities.HasComponent<LuaMFreeBrainRadarComponent>(brain), Is.True);
            });

            Assert.That(containers.Remove(brain, container), Is.True);
            AssertOwnedBrainBlip(entities, brain);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PreExistingForeignRadarBlipIsNeverRemoved()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();

        await server.WaitAssertion(() =>
        {
            var brain = entities.SpawnEntity(ForeignBlipBrain, MapCoordinates.Nullspace);
            var owner = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var container = containers.EnsureContainer<Container>(owner, "LuaMForeignBrainRadarTestContainer");

            var original = entities.GetComponent<RadarBlipComponent>(brain);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMFreeBrainRadarOwnedComponent>(brain), Is.False);
                Assert.That(original.Config.Color, Is.EqualTo(Color.Red));
                Assert.That(original.MaxDistance, Is.EqualTo(73f));
            });

            Assert.That(containers.Insert(brain, container), Is.True);
            var afterInsert = entities.GetComponent<RadarBlipComponent>(brain);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMFreeBrainRadarOwnedComponent>(brain), Is.False);
                Assert.That(afterInsert.Config.Color, Is.EqualTo(Color.Red));
                Assert.That(afterInsert.MaxDistance, Is.EqualTo(73f));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BrainMarkerPrototypesLoadOnServerAndClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var serverPrototypes = pair.Server.ResolveDependency<IPrototypeManager>();
        var clientPrototypes = pair.Client.ResolveDependency<IPrototypeManager>();
        var components = pair.Server.ResolveDependency<IComponentFactory>();

        await pair.Server.WaitAssertion(() =>
        {
            foreach (var id in new[] { HumanBrain, DionaBrain })
            {
                var prototype = serverPrototypes.Index<EntityPrototype>(id);
                Assert.That(
                    prototype.TryGetComponent<LuaMFreeBrainRadarComponent>(out _, components),
                    Is.True,
                    $"Server prototype {id} must opt into free-brain radar handling.");
            }
        });

        await pair.Client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(clientPrototypes.HasIndex<EntityPrototype>(HumanBrain), Is.True);
                Assert.That(clientPrototypes.HasIndex<EntityPrototype>(DionaBrain), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertOwnedBrainBlip(IEntityManager entities, EntityUid brain)
    {
        Assert.Multiple(() =>
        {
            Assert.That(entities.HasComponent<LuaMFreeBrainRadarComponent>(brain), Is.True);
            Assert.That(entities.HasComponent<LuaMFreeBrainRadarOwnedComponent>(brain), Is.True);
            Assert.That(entities.HasComponent<RadarBlipComponent>(brain), Is.True);
        });

        var blip = entities.GetComponent<RadarBlipComponent>(brain);
        Assert.Multiple(() =>
        {
            Assert.That(blip.Config.Color, Is.EqualTo(Color.FromHex("#E0FFFF")));
            Assert.That(blip.Config.Shape, Is.EqualTo(Content.Shared._Mono.Radar.RadarBlipShape.Star));
            Assert.That(blip.RequireNoGrid, Is.True);
            Assert.That(blip.VisibleFromOtherGrids, Is.True);
            Assert.That(blip.MaxDistance, Is.EqualTo(512f));
        });
    }
}
