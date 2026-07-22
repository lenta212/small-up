using System.Linq;
using Content.Shared.Materials;
using Content.Shared.Mining;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMCopperIndustryPrototypeTest
{
    private static readonly ProtoId<MaterialPrototype> Copper = new("Copper");
    private static readonly ProtoId<MaterialPrototype> CopperOre = new("CopperOre");

    [Test]
    public async Task CopperFeedsCablesAndEveryComponentRecipe()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            AssertCopperResourceChain(prototypes);
            AssertCableRecipes(prototypes);
            AssertComponentRecipes(prototypes);
            AssertMachinePartTiers(prototypes);
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertCopperResourceChain(IPrototypeManager prototypes)
    {
        var material = prototypes.Index<MaterialPrototype>(Copper);
        var rawMaterial = prototypes.Index<MaterialPrototype>(CopperOre);
        var stack = prototypes.Index<StackPrototype>("Copper");
        var rawStack = prototypes.Index<StackPrototype>("CopperOre");
        var ore = prototypes.Index<OrePrototype>("CopperOre");
        var smelting = prototypes.Index<LatheRecipePrototype>("SheetCopper");

        Assert.Multiple(() =>
        {
            Assert.That(material.StackEntity, Is.EqualTo(new EntProtoId("SheetCopper1")));
            Assert.That(rawMaterial.StackEntity, Is.EqualTo(new EntProtoId("CopperOre1")));
            Assert.That(stack.Spawn, Is.EqualTo(new EntProtoId("SheetCopper1")));
            Assert.That(rawStack.Spawn, Is.EqualTo(new EntProtoId("CopperOre1")));
            Assert.That(ore.OreEntity, Is.EqualTo(new EntProtoId("CopperOre1")));
            Assert.That(ore.MinOreYield, Is.EqualTo(1));
            Assert.That(ore.MaxOreYield, Is.EqualTo(5));
            Assert.That(smelting.Materials[CopperOre], Is.EqualTo(100));
        });
    }

    private static void AssertCableRecipes(IPrototypeManager prototypes)
    {
        foreach (var recipeId in new[] { "CableStack", "CableMVStack", "CableHVStack" })
        {
            var recipe = prototypes.Index<LatheRecipePrototype>(recipeId);
            Assert.Multiple(() =>
            {
                Assert.That(recipe.Materials[Copper], Is.EqualTo(30), $"{recipeId} copper cost changed.");
                Assert.That(
                    recipe.Materials.ContainsKey(new ProtoId<MaterialPrototype>("Steel")),
                    Is.False,
                    $"{recipeId} must use copper instead of steel.");
            });
        }
    }

    private static void AssertComponentRecipes(IPrototypeManager prototypes)
    {
        var componentRecipes = prototypes
            .EnumeratePrototypes<LatheRecipePrototype>()
            .Where(recipe => recipe.Categories.Any(category => category.Id == "Parts"))
            .ToArray();
        var recipesWithoutCopper = componentRecipes
            .Where(recipe => !recipe.Materials.TryGetValue(Copper, out var amount) || amount <= 0)
            .Select(recipe => recipe.ID)
            .OrderBy(id => id)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(componentRecipes, Is.Not.Empty);
            Assert.That(
                recipesWithoutCopper,
                Is.Empty,
                $"Every recipe in the Components category must consume copper: {string.Join(", ", recipesWithoutCopper)}");
        });
    }

    private static void AssertMachinePartTiers(IPrototypeManager prototypes)
    {
        AssertCopperCost(prototypes, 50,
            "CapacitorStockPart",
            "MatterBinStockPart",
            "MicroManipulatorStockPart");
        AssertCopperCost(prototypes, 100,
            "AdvancedCapacitorStockPart",
            "AdvancedMatterBinStockPart",
            "NanoManipulatorStockPart");
        AssertCopperCost(prototypes, 150,
            "SuperCapacitorStockPart",
            "SuperMatterBinStockPart",
            "PicoManipulatorStockPart");
        AssertCopperCost(prototypes, 200,
            "QuadraticCapacitorStockPart",
            "BluespaceMatterBinStockPart",
            "FemtoManipulatorStockPart");
    }

    private static void AssertCopperCost(
        IPrototypeManager prototypes,
        int expectedCost,
        params string[] recipeIds)
    {
        foreach (var recipeId in recipeIds)
        {
            var recipe = prototypes.Index<LatheRecipePrototype>(recipeId);
            Assert.That(
                recipe.Materials[Copper],
                Is.EqualTo(expectedCost),
                $"{recipeId} copper cost changed.");
        }
    }
}
