using System.Collections.Generic;
using Content.Server._Mono.VendingMachine;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.VendingMachines;
using Content.Shared._Mono.VendingMachine;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[TestOf(typeof(VendingMachineSystem))]
public sealed class LuaMVendingBatchPurchaseTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: LuaMVendingBatchBuyer
  name: batch vending buyer

- type: entity
  id: LuaMVendingBatchProduct
  name: batch vending product
  components:
  - type: StaticPrice
    price: 10

- type: vendingMachineInventory
  id: LuaMVendingBatchInventory
  startingInventory:
    LuaMVendingBatchProduct: 5

- type: entity
  parent: VendingMachine
  id: LuaMVendingBatchMachine
  name: batch vending machine
  components:
  - type: VendingMachine
    pack: LuaMVendingBatchInventory
    ejectDelay: 0
    requiresCash: false
  - type: ApcPowerReceiver
    needsPower: false
  - type: Sprite
    sprite: error.rsi

- type: entity
  parent: LuaMVendingBatchMachine
  id: LuaMVendingBatchPaidMachine
  name: paid batch vending machine
  components:
  - type: VendingMachine
    pack: LuaMVendingBatchInventory
    ejectDelay: 0
    requiresCash: true
  - type: MarketModifier
    mod: 1
";

    [Test]
    public void BatchTotalRejectsInvalidQuantityAndOverflow()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(10, 3, out var total), Is.True);
            Assert.That(total, Is.EqualTo(30));

            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(0, 30, out var freeTotal), Is.True);
            Assert.That(freeTotal, Is.Zero);

            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(10, 0, out _), Is.False);
            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(10, -1, out _), Is.False);
            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(
                10,
                VendingMachineComponent.MaxPurchaseQuantity + 1,
                out _), Is.False);
            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(int.MaxValue, 2, out _), Is.False);
            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(-1, 1, out _), Is.False);
        });
    }

    [Test]
    public void BatchStockIsExactAndUnlimitedStockStaysEligible()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VendingMachineSystem.HasSufficientStock(5, 5), Is.True);
            Assert.That(VendingMachineSystem.HasSufficientStock(5, 6), Is.False);
            Assert.That(VendingMachineSystem.HasSufficientStock(uint.MaxValue, 30), Is.True);
            Assert.That(VendingMachineSystem.HasSufficientStock(uint.MaxValue, 31), Is.False);
            Assert.That(VendingMachineSystem.HasSufficientStock(5, 0), Is.False);
        });
    }

    [Test]
    public void PurchaseMessagesCarryQuantityAndDisplayedTotal()
    {
        var batch = new VendingMachineEjectMessage(
            InventoryType.Regular,
            "LuaMVendingBatchProduct",
            3,
            30);
        var single = new VendingMachineEjectMessage(InventoryType.Regular, "LuaMVendingBatchProduct");
        var state = new VendingMachineBoundUserInterfaceState(new Dictionary<string, int>
        {
            ["LuaMVendingBatchProduct"] = 10,
        }, true);

        Assert.Multiple(() =>
        {
            Assert.That(batch.Quantity, Is.EqualTo(3));
            Assert.That(batch.ExpectedTotalPrice, Is.EqualTo(30));
            Assert.That(single.Quantity, Is.EqualTo(1));
            Assert.That(single.ExpectedTotalPrice, Is.EqualTo(-1));
            Assert.That(state.UnitPrices["LuaMVendingBatchProduct"], Is.EqualTo(10));
            Assert.That(state.RequiresCash, Is.True);
        });
    }

    [Test]
    public async Task FreeBatchReservesAndMaterializesExactQuantityWithUnitProvenance()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var systems = server.ResolveDependency<IEntitySystemManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var testMap = await pair.CreateTestMap();

        EntityUid machine = default;

        await server.WaitAssertion(() =>
        {
            var buyer = entityManager.SpawnEntity("LuaMVendingBatchBuyer", testMap.GridCoords);
            machine = entityManager.SpawnEntity("LuaMVendingBatchMachine", testMap.GridCoords);
            var component = entityManager.GetComponent<VendingMachineComponent>(machine);
            var system = systems.GetEntitySystem<VendingMachineSystem>();
            var power = systems.GetEntitySystem<PowerReceiverSystem>();
            var receiver = entityManager.GetComponent<ApcPowerReceiverComponent>(machine);
            power.SetNeedsPower(machine, false, receiver);
            receiver.Powered = true;

            var paidMachine = entityManager.SpawnEntity("LuaMVendingBatchPaidMachine", testMap.GridCoords);
            var paidComponent = entityManager.GetComponent<VendingMachineComponent>(paidMachine);
            var productPrototype = prototypes.Index<EntityPrototype>("LuaMVendingBatchProduct");
            Assert.That(system.TryGetUnitPrice(paidComponent, productPrototype, out var unitPrice), Is.True);
            Assert.That(unitPrice, Is.EqualTo(10));
            Assert.That(VendingMachineSystem.TryCalculateBatchTotal(unitPrice, 3, out var quotedTotal), Is.True);
            Assert.That(quotedTotal, Is.EqualTo(30));

            system.AuthorizedVend(
                machine,
                buyer,
                InventoryType.Regular,
                "LuaMVendingBatchProduct",
                component,
                6);

            Assert.That(component.Inventory["LuaMVendingBatchProduct"].Amount, Is.EqualTo(5));
            Assert.That(component.NextItemToEject, Is.Null);
            Assert.That(component.NextItemToEjectQuantity, Is.EqualTo(1));
            Assert.That(component.Ejecting, Is.False);

            system.AuthorizedVend(
                machine,
                buyer,
                InventoryType.Regular,
                "LuaMVendingBatchProduct",
                component,
                3);

            Assert.That(component.Inventory["LuaMVendingBatchProduct"].Amount, Is.EqualTo(2));
            Assert.That(component.NextItemToEjectQuantity, Is.EqualTo(3));
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var productCount = 0;
            var query = entityManager.EntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var product, out var metadata))
            {
                if (!metadata.Deleted && metadata.EntityPrototype?.ID == "LuaMVendingBatchProduct")
                {
                    productCount++;
                    var provenance = entityManager.GetComponent<VendingMachinePurchaseComponent>(product);
                    Assert.That(provenance.OriginalPurchasePrice, Is.Zero);
                }
            }

            Assert.That(productCount, Is.EqualTo(3));

            var component = entityManager.GetComponent<VendingMachineComponent>(machine);
            Assert.That(component.NextItemToEject, Is.Null);
            Assert.That(component.NextItemToEjectQuantity, Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }
}
