using Content.Shared.Cargo.Components;
using Content.Shared.Stacks;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMFuelVendStackPriceTest
{
    private static readonly (string Entity, string Stack, double UnitPrice)[] FuelBundles =
    [
        ("FuelPlasma", "FuelPlasma", 60),
        ("FuelUranium", "FuelUranium", 125),
        ("FuelBananium", "FuelBananium", 250),
    ];

    [Test]
    public async Task FuelVendBundlesContainSeventyFiveUnitsAtPreservedUnitPrices()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var inventory = prototypes.Index<VendingMachineInventoryPrototype>("VendingMachineFuelVendInventory");

            Assert.Multiple(() =>
            {
                foreach (var (entityId, stackId, unitPrice) in FuelBundles)
                {
                    Assert.That(
                        inventory.StartingInventory.ContainsKey(entityId),
                        Is.True,
                        $"FuelVend no longer sells {entityId}.");

                    var stackPrototype = prototypes.Index<StackPrototype>(stackId);
                    Assert.That(
                        stackPrototype.MaxCount,
                        Is.EqualTo(75),
                        $"{stackId} must fit the full 75-unit fuel bundle.");

                    var entityPrototype = prototypes.Index<EntityPrototype>(entityId);
                    Assert.That(
                        entityPrototype.TryGetComponent<StackComponent>(out var stack, components),
                        Is.True,
                        $"{entityId} must be a stack.");
                    Assert.That(
                        stack.Count,
                        Is.EqualTo(75),
                        $"{entityId} must dispense a full 75-unit bundle.");

                    Assert.That(
                        entityPrototype.TryGetComponent<StaticPriceComponent>(out var price, components),
                        Is.True,
                        $"{entityId} must have an explicit FuelVend price.");
                    Assert.That(price.Price, Is.Zero, $"{entityId} must not gain a cargo resale price.");
                    Assert.That(
                        price.VendPrice,
                        Is.EqualTo(unitPrice * stack.Count),
                        $"{entityId} changed its price per fuel unit.");
                }
            });
        });

        await pair.CleanReturnAsync();
    }
}
