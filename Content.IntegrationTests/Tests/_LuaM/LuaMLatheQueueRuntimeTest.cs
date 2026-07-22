using Content.Server.Lathe;
using Content.Server.Lathe.Components;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[TestOf(typeof(LatheSystem))]
public sealed class LuaMLatheQueueRuntimeTest
{
    private const string MachineId = "LuaMLatheQueueMachine";
    private const string FirstRecipeId = "LuaMLatheQueueFirstRecipe";
    private const string SecondRecipeId = "LuaMLatheQueueSecondRecipe";
    private const string ForeignRecipeId = "LuaMLatheQueueForeignRecipe";
    private const string FirstProductId = "LuaMLatheQueueFirstProduct";
    private const string SecondProductId = "LuaMLatheQueueSecondProduct";

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: LuaMLatheQueueActor
  name: lathe queue actor

- type: entity
  id: LuaMLatheQueueFirstProduct
  name: first lathe queue product

- type: entity
  id: LuaMLatheQueueSecondProduct
  name: second lathe queue product

- type: latheRecipe
  id: LuaMLatheQueueFirstRecipe
  result: LuaMLatheQueueFirstProduct
  completetime: 600
  materials:
    Steel: 100

- type: latheRecipe
  id: LuaMLatheQueueSecondRecipe
  result: LuaMLatheQueueSecondProduct
  completetime: 600
  materials:
    Steel: 100

- type: latheRecipe
  id: LuaMLatheQueueForeignRecipe
  result: LuaMLatheQueueSecondProduct
  completetime: 600
  materials:
    Steel: 100

- type: latheRecipePack
  id: LuaMLatheQueuePack
  recipes:
  - LuaMLatheQueueFirstRecipe
  - LuaMLatheQueueSecondRecipe

- type: entity
  id: LuaMLatheQueueMachine
  name: lathe queue machine
  components:
  - type: Transform
  - type: Appearance
  - type: MaterialStorage
    storageLimit: 10000
  - type: Lathe
    staticPacks:
    - LuaMLatheQueuePack
    maxQueuedItems: 5
";

    [Test]
    public async Task KnownRecipeQueuesWhileProducingAndWaitsForMaterials()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var latheSystem = server.System<LatheSystem>();
        var materialSystem = server.System<SharedMaterialStorageSystem>();
        var map = await pair.CreateTestMap();

        var firstRecipe = prototypes.Index<LatheRecipePrototype>(FirstRecipeId);
        var secondRecipe = prototypes.Index<LatheRecipePrototype>(SecondRecipeId);
        EntityUid machine = default;

        await server.WaitAssertion(() =>
        {
            machine = entities.SpawnEntity(MachineId, map.GridCoords);
            var actor = entities.SpawnEntity("LuaMLatheQueueActor", map.GridCoords);
            var lathe = entities.GetComponent<LatheComponent>(machine);

            Assert.That(materialSystem.TryChangeMaterialAmount(machine, "Steel", 100), Is.True);
            Assert.That(latheSystem.TryAddToQueue(machine, firstRecipe, 1, lathe, actor), Is.True);
            Assert.That(latheSystem.TryStartProducing(machine, lathe), Is.True);
            Assert.That(lathe.CurrentRecipe?.ID, Is.EqualTo(FirstRecipeId));
            Assert.That(materialSystem.GetMaterialAmount(machine, "Steel"), Is.Zero);

            Assert.That(latheSystem.TryAddToQueue(machine, secondRecipe, 1, lathe, actor), Is.True,
                "A known recipe must be accepted while another product is running, even without current materials.");
            Assert.That(lathe.Queue, Has.Count.EqualTo(1));
            Assert.That(lathe.Queue[0].Recipe.ID, Is.EqualTo(SecondRecipeId));

            latheSystem.FinishProducing(machine, lathe);
            Assert.That(lathe.CurrentRecipe, Is.Null);
            Assert.That(lathe.Queue, Has.Count.EqualTo(1),
                "The next recipe must remain queued when its materials are unavailable.");
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var lathe = entities.GetComponent<LatheComponent>(machine);
            Assert.That(entities.HasComponent<LatheProducingComponent>(machine), Is.False);
            Assert.That(CountProducts(entities, FirstProductId), Is.EqualTo(1));
            Assert.That(CountProducts(entities, SecondProductId), Is.Zero);

            Assert.That(materialSystem.TryChangeMaterialAmount(machine, "Steel", 100), Is.True);
            Assert.That(latheSystem.TryStartProducing(machine, lathe), Is.True);
            Assert.That(lathe.CurrentRecipe?.ID, Is.EqualTo(SecondRecipeId));
            Assert.That(lathe.Queue, Is.Empty);
            Assert.That(materialSystem.GetMaterialAmount(machine, "Steel"), Is.Zero);

            latheSystem.FinishProducing(machine, lathe);
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(CountProducts(entities, FirstProductId), Is.EqualTo(1));
            Assert.That(CountProducts(entities, SecondProductId), Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task QueueValidatesRecipeLimitOverflowAndActorAwareMerging()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var latheSystem = server.System<LatheSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var machine = entities.SpawnEntity(MachineId, map.GridCoords);
            var firstActor = entities.SpawnEntity("LuaMLatheQueueActor", map.GridCoords);
            var secondActor = entities.SpawnEntity("LuaMLatheQueueActor", map.GridCoords);
            var lathe = entities.GetComponent<LatheComponent>(machine);
            var knownRecipe = prototypes.Index<LatheRecipePrototype>(FirstRecipeId);
            var foreignRecipe = prototypes.Index<LatheRecipePrototype>(ForeignRecipeId);

            Assert.That(lathe.MaxQueuedItems, Is.EqualTo(5));
            Assert.That(latheSystem.TryAddToQueue(machine, foreignRecipe, 1, lathe, firstActor), Is.False,
                "A custom client must not queue a recipe absent from this lathe's packs.");
            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 0, lathe, firstActor), Is.False);
            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, -1, lathe, firstActor), Is.False);

            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 1, lathe, firstActor), Is.True);
            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 1, lathe, firstActor), Is.True);
            Assert.That(lathe.Queue, Has.Count.EqualTo(1));
            Assert.That(lathe.Queue[0].ItemsRequested, Is.EqualTo(2),
                "Consecutive requests from the same actor should merge into one batch.");

            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 1, lathe, secondActor), Is.True);
            Assert.That(lathe.Queue, Has.Count.EqualTo(2),
                "Requests from different actors must retain separate provenance batches.");
            Assert.That(lathe.Queue[0].Actor, Is.Not.EqualTo(lathe.Queue[1].Actor));

            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 2, lathe, secondActor), Is.True);
            Assert.That(lathe.Queue[1].ItemsRequested, Is.EqualTo(3));
            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 1, lathe, secondActor), Is.False,
                "The configured future-item limit must be enforced without consuming materials.");

            lathe.Queue.Clear();
            lathe.MaxQueuedItems = int.MaxValue;
            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, int.MaxValue, lathe, firstActor), Is.True);
            Assert.That(latheSystem.TryAddToQueue(machine, knownRecipe, 1, lathe, firstActor), Is.False,
                "Queued-item arithmetic must not wrap past Int32.MaxValue.");
            Assert.That(lathe.Queue[0].ItemsRequested, Is.EqualTo(int.MaxValue));
        });

        await pair.CleanReturnAsync();
    }

    private static int CountProducts(IEntityManager entities, string prototypeId)
    {
        var count = 0;
        var query = entities.EntityQueryEnumerator<MetaDataComponent>();
        while (query.MoveNext(out _, out var metadata))
        {
            if (!metadata.Deleted && metadata.EntityPrototype?.ID == prototypeId)
                count++;
        }

        return count;
    }
}
