using System.IO;
using System.Collections.Generic;
using Content.Server._Mono.VendingMachine;
using Content.Server._NF.Market.Systems;
using Content.Server.Cargo.Systems;
using Content.Server.VendingMachines;
using Content.Shared._NF.Market;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMTransactionalCommerceContractTest
{
    [Test]
    public void ClientPackagingExplicitlyExcludesServerOnlyResources()
    {
        var source = ReadSource("Content.Packaging/ClientPackaging.cs");
        var audit = ReadSource("Tools/audit_release_surface.ps1");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("\"ServerOnly\""));
            Assert.That(source, Does.Contain("ClientOnlyIgnoredResources"));
            Assert.That(source, Does.Contain("RobustClientPackaging.WriteClientResources("));
            Assert.That(source, Does.Contain("ClientOnlyIgnoredResources,"));
            Assert.That(audit, Does.Contain("Test-ForbiddenClientEntry"));
            Assert.That(audit, Does.Contain("server-only resource in client package"));
            Assert.That(audit, Does.Contain("server assembly in client package"));
            Assert.That(audit, Does.Contain("server-only canary in client package"));
            Assert.That(audit, Does.Contain("server-only canary missing from server package"));
            Assert.That(audit, Does.Contain("$serverOnlyCanarySeen = $true"));
        });
    }

    [Test]
    public void MonoCoinsNotificationsAreOutsideTheCommittedMutationBoundary()
    {
        var source = ReadSource("Content.Server/_Mono/MonoCoins/CurrencyTransferCommand.cs");
        var commit = source.IndexOf("committedSenderBalance = result.SenderBalance;", StringComparison.Ordinal);
        var mutationCatch = source.IndexOf("catch (Exception ex)", commit, StringComparison.Ordinal);
        var senderNotification = source.IndexOf("Successfully transferred", mutationCatch, StringComparison.Ordinal);
        var targetNotification = source.IndexOf("ChatMessageToOne(", mutationCatch, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(commit, Is.GreaterThanOrEqualTo(0));
            Assert.That(mutationCatch, Is.GreaterThan(commit));
            Assert.That(senderNotification, Is.GreaterThan(mutationCatch));
            Assert.That(targetNotification, Is.GreaterThan(mutationCatch));
            Assert.That(source, Does.Contain("Console delivery is best effort only"));
            Assert.That(source, Does.Contain("Chat delivery is best effort only"));
            Assert.That(source, Does.Contain("TransferMonoCoinsAsync("));
            Assert.That(source, Does.Not.Contain("UpdateMonoCoinsBalanceExactAsync("));
            Assert.That(source, Does.Not.Contain("CompensateSenderAsync("));
        });
    }

    [Test]
    public void PaidLoadoutNeverRefundsAfterAnUnrollbackableWorldMutation()
    {
        var source = ReadSource("Content.Server/Station/Systems/StationSpawningSystem.cs");
        var finalizer = Slice(
            source,
            "private bool FinalizePaidLoadout(",
            "private void FinalizePaidLoadoutFallbacks(");
        var materializer = Slice(
            source,
            "private bool TryMaterializePendingLoadouts(",
            "private async Task ObservePaidLoadoutDebitAsync(");

        var materialize = finalizer.IndexOf(
            "var retainDebit = TryMaterializePendingLoadouts(",
            StringComparison.Ordinal);
        var rejectBeforeMutation = finalizer.IndexOf(
            "if (!retainDebit)",
            StringComparison.Ordinal);
        var retainAfterPartialRollback = finalizer.IndexOf(
            "if (!complete)",
            StringComparison.Ordinal);
        var rejectsOnlyBeforeRetainingValue = rejectBeforeMutation >= 0 &&
                                              retainAfterPartialRollback > rejectBeforeMutation &&
                                              finalizer[rejectBeforeMutation..retainAfterPartialRollback]
                                                  .Contains("return false;", StringComparison.Ordinal);
        var refundsAfterRetainingValue = retainAfterPartialRollback >= 0 &&
                                         finalizer[retainAfterPartialRollback..]
                                             .Contains("return false;", StringComparison.Ordinal);
        var spawn = materializer.IndexOf("var spawnedEntity = Spawn(prototype, coordinates);", StringComparison.Ordinal);
        var tracked = materializer.IndexOf("spawned.Add((spawnedEntity, prototype));", StringComparison.Ordinal);
        var equip = materializer.IndexOf("InventorySystem.TryEquip(", StringComparison.Ordinal);
        var pickup = materializer.IndexOf("_hands.TryPickupAnyHand(", StringComparison.Ordinal);
        var storage = materializer.IndexOf("_storage.Insert(", StringComparison.Ordinal);
        var rollbackObserved = materializer.IndexOf("var rollbackComplete = spawned.All(", StringComparison.Ordinal);
        var rollbackDecision = materializer.IndexOf("return !rollbackComplete;", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(materialize, Is.GreaterThanOrEqualTo(0));
            Assert.That(rejectBeforeMutation, Is.GreaterThan(materialize));
            Assert.That(retainAfterPartialRollback, Is.GreaterThan(rejectBeforeMutation));
            Assert.That(rejectsOnlyBeforeRetainingValue, Is.True);
            Assert.That(refundsAfterRetainingValue, Is.False,
                "Once materialized value survives rollback, the debit must not become refundable.");
            Assert.That(spawn, Is.GreaterThanOrEqualTo(0));
            Assert.That(tracked, Is.GreaterThan(spawn));
            Assert.That(equip, Is.GreaterThan(tracked));
            Assert.That(pickup, Is.GreaterThan(tracked));
            Assert.That(storage, Is.GreaterThan(tracked));
            Assert.That(rollbackObserved, Is.GreaterThan(storage));
            Assert.That(rollbackDecision, Is.GreaterThan(rollbackObserved));
            Assert.That(materializer, Does.Contain(
                "spawned.All(value => !Exists(value.Entity) || Deleted(value.Entity))"));
        });
    }

    [Test]
    public void MarketCrateReservationAndMaterializationAreExceptionAtomic()
    {
        var market = ReadSource("Content.Server/_NF/Market/Systems/MarketSystem.CrateMachine.cs");
        var stack = ReadSource("Content.Server/Stack/StackSystem.cs");
        var openedStart = market.IndexOf(
            "private void OnCrateMachineOpened(",
            StringComparison.Ordinal);
        Assert.That(openedStart, Is.GreaterThanOrEqualTo(0));
        var opened = market[openedStart..];

        Assert.Multiple(() =>
        {
            Assert.That(market, Does.Contain("var previousPayload = currentSpawner.ItemsToSpawn;"));
            Assert.That(market, Does.Contain("_crateMachine.RestoreOpeningTime("));
            Assert.That(market, Does.Contain("currentConsole.CartDataList = previousCart;"));
            Assert.That(market, Does.Contain("TryCalculateMarketCost(purchasedItems"));
            Assert.That(market, Does.Contain("transactionCost, out var spawnCost"));
            Assert.That(market, Does.Contain("currentConsole.TransactionCost != transactionCost"));
            Assert.That(market, Does.Contain("currentSpawner.ItemsToSpawn.Count != 0"));
            Assert.That(market, Does.Contain("!double.IsFinite(item.Price)"));
            Assert.That(market, Does.Contain("total > int.MaxValue"));
            Assert.That(opened.IndexOf("itemSpawner.ItemsToSpawn = [];", StringComparison.Ordinal),
                Is.LessThan(opened.IndexOf("_crateMachine.SpawnCrate", StringComparison.Ordinal)));
            Assert.That(opened, Does.Contain("QueueDel(spawned)"));
            Assert.That(stack, Does.Contain("Never leak a partially-created payout"));
            Assert.That(stack, Does.Contain("corresponding cash entity cannot"));
        });
    }

    [Test]
    public void VendingRejectsInvalidPricesAndRestoresReservationsBeforeRefund()
    {
        var source = ReadSource("Content.Server/VendingMachines/VendingMachineSystem.cs");
        var client = ReadSource("Content.Client/VendingMachines/UI/VendingMachineMenu.xaml.cs");
        var pricing = Slice(
            source,
            "internal bool TryGetUnitPrice(",
            "private void UpdatePriceQuotes(");
        var method = Slice(
            source,
            "private async Task<bool> AuthorizedVendAsync(",
            "public void TryUpdateVisualState(");
        var eject = Slice(
            source,
            "private void EjectItem(",
            "private VendingMachineInventoryEntry? GetEntry(");

        Assert.Multiple(() =>
        {
            Assert.That(pricing, Does.Contain("!double.IsFinite(price)"));
            Assert.That(pricing, Does.Contain("price > int.MaxValue"));
            Assert.That(client, Does.Not.Contain("private int GetPrice("));
            Assert.That(method, Does.Contain("expected != totalPrice"));
            Assert.That(method, Does.Contain("UpdatePriceQuotes(uid, component);"));
            Assert.That(method, Does.Contain("var availableFunds = (long) bankBalance + cashSlotBalance;"));
            Assert.That(method, Does.Contain("bool ResolveVendFailure(Exception failure)"));
            Assert.That(method, Does.Contain("ResolveVendingCashRollback("));
            Assert.That(method, Does.Contain("VendingCashRollbackOutcome.Spent"));
            Assert.That(method, Does.Contain("VendingCashRollbackOutcome.Unknown"));
            Assert.That(method, Does.Contain("reservedEntry.Amount = previousAmount"));
            Assert.That(method, Does.Contain("component.Ejecting = previousEjecting;"));
            Assert.That(method, Does.Contain("component.NextItemToEjectQuantity = previousNextItemQuantity;"));
            Assert.That(method, Does.Contain("component.LastPurchasePrice = unitPrice;"));
            Assert.That(method, Does.Not.Contain("component.LastPurchasePrice = totalPrice;"));
            Assert.That(method, Does.Contain("return ResolveVendFailure(exception);"));
            Assert.That(method, Does.Contain("Tax projection is ancillary after a successful vend."));
            Assert.That(eject.IndexOf("vendComponent.NextItemToEject = null;", StringComparison.Ordinal),
                Is.LessThan(eject.IndexOf("ent = Spawn(itemToEject", StringComparison.Ordinal)));
            Assert.That(eject.IndexOf("vendComponent.NextItemToEjectQuantity = 1;", StringComparison.Ordinal),
                Is.LessThan(eject.IndexOf("ent = Spawn(itemToEject", StringComparison.Ordinal)));
            Assert.That(eject, Does.Contain("for (var i = 0; i < itemQuantity; i++)"));
            Assert.That(eject, Does.Contain("discarded payout"));
        });
    }

    [Test]
    public void MarketCostIncludesFeeAndRejectsZeroRoundedOrOverflowingLines()
    {
        var items = new List<MarketData>
        {
            new("LuaMMarketTestItem", null, 2, 100d),
        };

        Assert.That(MarketSystem.TryCalculateMarketCost(items, 1.25f, 600, out var cost), Is.True);
        Assert.That(cost, Is.EqualTo(850));

        Assert.Multiple(() =>
        {
            Assert.That(MarketSystem.TryCalculateMarketCost(items, 1f, -1, out _), Is.False);

            items[0].Price = 0d;
            Assert.That(MarketSystem.TryCalculateMarketCost(items, 1f, 600, out _), Is.False);

            items[0].Price = 0.1d;
            items[0].Quantity = 1;
            Assert.That(MarketSystem.TryCalculateMarketCost(items, 1f, 600, out _), Is.False);

            items[0].Price = int.MaxValue;
            Assert.That(MarketSystem.TryCalculateMarketCost(items, 1f, 1, out _), Is.False);
        });
    }

    [Test]
    public void MarketEntityCountTreatsNonStacksByQuantityAndSaturates()
    {
        var items = new List<MarketData>
        {
            new("LuaMNonStack", null, 30, 1d),
        };

        Assert.That(MarketSystem.CalculateEntityAmount(items, _ => 1), Is.EqualTo(30));

        items[0].Quantity = 31;
        Assert.That(MarketSystem.CalculateEntityAmount(items, _ => 1), Is.EqualTo(31));

        items.Add(new MarketData("LuaMOverflow", null, int.MaxValue, 1d));
        Assert.That(MarketSystem.CalculateEntityAmount(items, _ => 1), Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void VendingCashRollbackUsesObservedStateAfterInjectedFaults()
    {
        var currentCount = 4;
        var loggedFailures = 0;
        var restoredAfterThrow = VendingMachineSystem.ResolveVendingCashRollback(
            cashMutationAttempted: true,
            previousCount: 10,
            spentCount: 4,
            restoreCash: () =>
            {
                currentCount = 10;
                throw new InvalidOperationException("appearance failed after count restoration");
            },
            observeCash: () => new VendingCashObservation(true, true, currentCount),
            onFailure: _ => loggedFailures++);

        currentCount = 4;
        var spentAfterThrow = VendingMachineSystem.ResolveVendingCashRollback(
            true,
            10,
            4,
            restoreCash: () => throw new InvalidOperationException("restoration failed before mutation"),
            observeCash: () => new VendingCashObservation(true, true, currentCount));

        currentCount = 7;
        var partial = VendingMachineSystem.ResolveVendingCashRollback(
            true,
            10,
            4,
            restoreCash: () => throw new InvalidOperationException("partial mutation"),
            observeCash: () => new VendingCashObservation(true, true, currentCount));

        var missing = VendingMachineSystem.ResolveVendingCashRollback(
            true,
            10,
            4,
            restoreCash: () => { },
            observeCash: () => new VendingCashObservation(false, false, 0));

        Assert.Multiple(() =>
        {
            Assert.That(restoredAfterThrow.Outcome, Is.EqualTo(VendingCashRollbackOutcome.Restored));
            Assert.That(restoredAfterThrow.ObservedCount, Is.EqualTo(10));
            Assert.That(loggedFailures, Is.EqualTo(1));
            Assert.That(spentAfterThrow.Outcome, Is.EqualTo(VendingCashRollbackOutcome.Spent));
            Assert.That(partial.Outcome, Is.EqualTo(VendingCashRollbackOutcome.Unknown));
            Assert.That(partial.ObservedCount, Is.EqualTo(7));
            Assert.That(missing.Outcome, Is.EqualTo(VendingCashRollbackOutcome.Unknown));
            Assert.That(missing.ObservedCount, Is.Null);
        });
    }

    [Test]
    public void VendingProvenanceCapsFullResaleValueOnEveryGrid()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VendingMachinePurchaseSystem.CalculateResaleCap(0d), Is.Zero);
            Assert.That(VendingMachinePurchaseSystem.CalculateResaleCap(100d), Is.EqualTo(50d));
            Assert.That(VendingMachinePurchaseSystem.CalculateResaleCap(double.NaN), Is.Zero);
            Assert.That(PricingSystem.ApplyVendingResaleCap(1_000d, 0d), Is.Zero);
            Assert.That(PricingSystem.ApplyVendingResaleCap(1_000d, 50d), Is.EqualTo(50d));
            Assert.That(PricingSystem.ApplyVendingResaleCap(25d, 50d), Is.EqualTo(25d));
        });
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
    {
        var root = Path.GetFullPath(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", ".."));
        return File.ReadAllText(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
