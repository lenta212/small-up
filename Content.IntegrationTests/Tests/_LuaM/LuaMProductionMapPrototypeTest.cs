using System.Linq;
using Content.Client.Guidebook;
using Content.Client.Guidebook.Richtext;
using Content.Shared._Funkystation.Atmos.Prototypes;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Cargo.Components;
using Content.Shared.RCD.Components;
using Content.Shared.RCD;
using Content.Shared.Lathe;
using Content.Server.Construction.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.FixedPoint;
using Content.Shared.Guidebook;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Content.Shared.VendingMachines;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMProductionMapPrototypeTest
{
    [Test]
    public async Task ProductionMapMarkupParses()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var client = pair.Client;
        await client.WaitIdleAsync();

        var resources = client.ResolveDependency<IResourceManager>();
        var parser = client.ResolveDependency<DocumentParsingManager>();
        var prototypes = client.ResolveDependency<IPrototypeManager>();

        await client.WaitAssertion(() =>
        {
            var guide = prototypes.Index<GuideEntryPrototype>("EconomyRecipes");
            using var reader = resources.ContentFileReadText(guide.Text);
            var document = new Document();
            Assert.That(parser.TryAddMarkup(document, reader.ReadToEnd()), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ProductionMapMatchesCurrentRecipesAndVendorPrices()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var guide = prototypes.Index<GuideEntryPrototype>("EconomyRecipes");
            Assert.That(
                guide.Text.ToString(),
                Is.EqualTo("/ServerInfo/_Mono/Guidebook/Economy/EconomyRecipes.xml"));

            AssertRecipeMaterials(prototypes, "ConveyorBeltAssembly", ("Steel", 250), ("Plastic", 50));
            AssertRecipeMaterials(prototypes, "CableStack", ("Copper", 30));
            AssertRecipeMaterials(prototypes, "Screwdriver", ("Steel", 200), ("Plastic", 50));
            AssertRecipeMaterials(prototypes, "Wrench", ("Steel", 200));
            AssertRecipeMaterials(prototypes, "CrowbarGreen", ("Steel", 200));
            AssertRecipeMaterials(prototypes, "Welder", ("Steel", 400));
            AssertRecipeMaterials(prototypes, "Multitool", ("Steel", 200), ("Plastic", 200));
            AssertRecipeMaterials(
                prototypes,
                "MatterBinStockPart",
                ("Steel", 50),
                ("Copper", 50),
                ("Plastic", 50));
            AssertRecipeMaterials(prototypes, "DoorElectronics", ("Steel", 250), ("Plastic", 150));
            AssertRecipeMaterials(prototypes, "SheetRGlass", ("Glass", 100), ("Steel", 50));
            AssertRecipeMaterials(prototypes, "FloorTileItemSteel", ("Steel", 25));
            AssertRecipeMaterials(prototypes, "SheetSteel", ("RawIron", 100), ("Coal", 30));
            AssertRecipeMaterials(prototypes, "SheetGlass1", ("RawQuartz", 100));
            AssertRecipeMaterials(prototypes, "NFBioGenSheetPlastic", ("Biomass", 5));
            AssertRecipeMaterials(
                prototypes,
                "DiagnosticCyberneticEyes",
                ("Steel", 1000),
                ("Glass", 500),
                ("Plastic", 500),
                ("Gold", 300),
                ("Silver", 300));
            AssertRecipeMaterials(
                prototypes,
                "BasicCyberneticLiver",
                ("Steel", 2000),
                ("Glass", 1000),
                ("Plastic", 1000),
                ("Silver", 1500),
                ("Gold", 600),
                ("Bluespace", 200));
            AssertRecipeMaterials(
                prototypes,
                "BasicCyberneticLungs",
                ("Steel", 2500),
                ("Glass", 500),
                ("Plastic", 2000),
                ("Silver", 1000),
                ("Plasma", 1250),
                ("Bluespace", 200));
            AssertRecipeMaterials(
                prototypes,
                "BasicCyberneticHeart",
                ("Steel", 3000),
                ("Glass", 500),
                ("Plastic", 1500),
                ("Gold", 1000),
                ("Plasma", 1250),
                ("Bluespace", 200));
            AssertRecipeMaterials(
                prototypes,
                "UpgradedCyberneticHeart",
                ("Plasteel", 3000),
                ("Glass", 1500),
                ("Plastic", 2500),
                ("Gold", 1000),
                ("Plasma", 2500),
                ("Bluespace", 400));
            AssertRecipeMaterials(
                prototypes,
                "UpgradedCyberneticLungs",
                ("Plasteel", 3500),
                ("Glass", 1500),
                ("Plastic", 2000),
                ("Silver", 1000),
                ("Plasma", 2500),
                ("Bluespace", 400));
            AssertRecipeMaterials(
                prototypes,
                "ClothingHandsGlovesSurgical",
                ("Plastic", 800),
                ("Silver", 500),
                ("Bluespace", 100));
            AssertRecipeMaterials(
                prototypes,
                "AdvancedBoneGel",
                ("Steel", 600),
                ("Glass", 150),
                ("Plasma", 150));

            AssertTechnologyUnlocks(
                prototypes,
                "CyberneticOrgans",
                "BasicCyberneticHeart",
                "BasicCyberneticLungs",
                "BasicCyberneticLiver");
            AssertTechnologyUnlocks(
                prototypes,
                "UpgradedCyberneticOrgans",
                "UpgradedCyberneticHeart",
                "UpgradedCyberneticLungs");
            AssertTechnologyUnlocks(
                prototypes,
                "HighEndSurgery",
                "AdvancedBoneGel",
                "ClothingHandsGlovesSurgical");
            AssertTechnologyUnlocks(
                prototypes,
                "AdvancedAtmospherics",
                "PlasticProcessorMachineCircuitboard",
                "CrystallizerMachineCircuitboard");

            var plasticReaction = prototypes.Index<ReactionPrototype>("PlasticSheet");
            Assert.Multiple(() =>
            {
                Assert.That(plasticReaction.MinimumTemperature, Is.EqualTo(374f));
                Assert.That(plasticReaction.Reactants["Oil"].Amount, Is.EqualTo(FixedPoint2.New(5)));
                Assert.That(plasticReaction.Reactants["Ash"].Amount, Is.EqualTo(FixedPoint2.New(3)));
                Assert.That(plasticReaction.Reactants["SulfuricAcid"].Amount, Is.EqualTo(FixedPoint2.New(2)));
            });

            var plasticCrystallizer = prototypes.Index<CrystallizerRecipePrototype>("PlasticRecipe");
            Assert.Multiple(() =>
            {
                Assert.That(plasticCrystallizer.MinimumTemperature, Is.EqualTo(500f));
                Assert.That(plasticCrystallizer.MaximumTemperature, Is.EqualTo(800f));
                Assert.That(plasticCrystallizer.MinimumRequirements[2], Is.EqualTo(4f), "CO2 cost changed.");
                Assert.That(plasticCrystallizer.MinimumRequirements[5], Is.EqualTo(6f), "Water vapor cost changed.");
                Assert.That(plasticCrystallizer.Products["SheetPlastic10"], Is.EqualTo(1));
            });

            var plasticRecipe = prototypes.Index<LatheRecipePrototype>("PlasticFromCoalPlasma");
            var plasticProcessor = prototypes.Index<EntityPrototype>("PlasticProcessor");
            var plasticBoard = prototypes.Index<EntityPrototype>("PlasticProcessorMachineCircuitboard");
            var rcd = prototypes.Index<EntityPrototype>("RCD");
            var plasteelWallRcd = prototypes.Index<RCDPrototype>("WallPlasteel");
            var plasteelWall = prototypes.Index<EntityPrototype>("WallPlasteel");
            Assert.Multiple(() =>
            {
                Assert.That(plasticRecipe.Result, Is.EqualTo("SheetPlastic10"));
                Assert.That(plasticRecipe.Materials[new ProtoId<MaterialPrototype>("Coal")], Is.EqualTo(500));
                Assert.That(plasticRecipe.Materials[new ProtoId<MaterialPrototype>("RawPlasma")], Is.EqualTo(500));

                Assert.That(plasticProcessor.TryGetComponent<LatheComponent>(out var plasticLathe, components), Is.True);
                Assert.That(plasticLathe!.StaticPacks.Select(id => id.Id), Does.Contain("PlasticProcessing"));
                Assert.That(plasticProcessor.TryGetComponent<MachineComponent>(out var plasticMachine, components), Is.True);
                Assert.That(plasticMachine!.Board!.Value.Id, Is.EqualTo("PlasticProcessorMachineCircuitboard"));

                Assert.That(plasticBoard.TryGetComponent<MachineBoardComponent>(out var board, components), Is.True);
                Assert.That(board!.Prototype, Is.EqualTo("PlasticProcessor"));

                Assert.That(rcd.TryGetComponent<RCDComponent>(out var rcdComponent, components), Is.True);
                Assert.That(rcdComponent!.AvailablePrototypes.Select(id => id.Id), Does.Contain("WallPlasteel"));
                Assert.That(plasteelWallRcd.Prototype, Is.EqualTo("WallPlasteel"));
                Assert.That(plasteelWallRcd.Cost, Is.EqualTo(8));
                Assert.That(plasteelWall.TryGetComponent<RCDDeconstructableComponent>(out var deconstructable, components), Is.True);
                Assert.That(deconstructable!.Cost, Is.EqualTo(8));
            });

            var regularInventory = prototypes.Index<VendingMachineInventoryPrototype>("FlatpackVendInventory");
            AssertInventoryContains(
                regularInventory,
                "AutolatheFlatpack",
                "OreProcessorFlatpack",
                "MaterialReclaimerFlatpack",
                "MachineFlatpackerFlatpack",
                "MaterialSiloFlatpack",
                "StationLaserDrillFlatpack");

            var expeditionInventory = prototypes.Index<VendingMachineInventoryPrototype>("ExpeditionaryFlatpackVendInventory");
            AssertInventoryContains(expeditionInventory, "LaserDrillFlatpack");

            AssertStaticPrice(prototypes, components, "AutolatheFlatpack", 150, 0);
            AssertStaticPrice(prototypes, components, "OreProcessorFlatpack", 150, 0);
            AssertStaticPrice(prototypes, components, "MaterialReclaimerFlatpack", 150, 0);
            AssertStaticPrice(prototypes, components, "MachineFlatpackerFlatpack", 250, 0);
            AssertStaticPrice(prototypes, components, "MaterialSiloFlatpack", 400, 0);
            AssertStaticPrice(prototypes, components, "LaserDrillFlatpack", 3000, 35000);
            AssertStaticPrice(prototypes, components, "StationLaserDrillFlatpack", 5000, 50000);

            var vendor = prototypes.Index<EntityPrototype>("VendingMachineFlatpackVend");
            Assert.That(
                vendor.TryGetComponent<MarketModifierComponent>(out var modifier, components),
                Is.True,
                "The production map derives ordinary flatpack prices from the vendor multiplier.");
            Assert.That(modifier.Mod, Is.EqualTo(25f));
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertRecipeMaterials(
        IPrototypeManager prototypes,
        string recipeId,
        params (string Material, int Amount)[] expected)
    {
        var recipe = prototypes.Index<LatheRecipePrototype>(recipeId);
        Assert.Multiple(() =>
        {
            Assert.That(
                recipe.Materials,
                Has.Count.EqualTo(expected.Length),
                $"{recipeId} gained or lost a material; update the production map.");
            foreach (var (material, amount) in expected)
            {
                Assert.That(
                    recipe.Materials.TryGetValue(new ProtoId<MaterialPrototype>(material), out var actual),
                    Is.True,
                    $"{recipeId} no longer uses {material}.");
                Assert.That(actual, Is.EqualTo(amount), $"{recipeId} {material} cost changed.");
            }
        });
    }

    private static void AssertInventoryContains(
        VendingMachineInventoryPrototype inventory,
        params string[] prototypeIds)
    {
        Assert.Multiple(() =>
        {
            foreach (var prototypeId in prototypeIds)
            {
                Assert.That(
                    inventory.StartingInventory.ContainsKey(prototypeId),
                    Is.True,
                    $"{prototypeId} is no longer sold by {inventory.ID}.");
            }
        });
    }

    private static void AssertTechnologyUnlocks(
        IPrototypeManager prototypes,
        string technologyId,
        params string[] recipeIds)
    {
        var technology = prototypes.Index<TechnologyPrototype>(technologyId);
        Assert.Multiple(() =>
        {
            foreach (var recipeId in recipeIds)
            {
                Assert.That(
                    technology.RecipeUnlocks.Select(id => id.Id),
                    Does.Contain(recipeId),
                    $"{technologyId} no longer unlocks {recipeId}; update the production map.");
            }
        });
    }

    private static void AssertStaticPrice(
        IPrototypeManager prototypes,
        IComponentFactory components,
        string prototypeId,
        double price,
        double vendPrice)
    {
        var prototype = prototypes.Index<EntityPrototype>(prototypeId);
        Assert.That(
            prototype.TryGetComponent<StaticPriceComponent>(out var staticPrice, components),
            Is.True,
            $"{prototypeId} must have a static price.");
        Assert.Multiple(() =>
        {
            Assert.That(staticPrice.Price, Is.EqualTo(price));
            Assert.That(staticPrice.VendPrice, Is.EqualTo(vendPrice));
        });
    }
}
