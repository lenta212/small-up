using System.IO;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipyardPurchaseDurabilityContractTest
{
    [Test]
    public void PurchaseUsesDurableDebitWithAwaitedWorldFinalizer()
    {
        var source = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ObservePurchaseMessageAsync"));
            Assert.That(source, Does.Contain("await _bank.TryBankWithdrawAsync("));
            Assert.That(source, Does.Contain("finalizeAfterCommit: FinalizeAfterCommit"));
            Assert.That(source, Does.Contain("await TryCreatePurchasedShuttleAsync("));
            Assert.That(source, Does.Not.Contain("_bank.TryBankWithdraw(player, vessel.Price)"));
        });
    }

    [Test]
    public void PurchaseRevalidatesAndFailsClosedOnAmbiguousOutcome()
    {
        var consoles = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var system = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs");

        Assert.Multiple(() =>
        {
            Assert.That(
                consoles.Split("TryValidateShuttlePurchase(").Length - 1,
                Is.GreaterThanOrEqualTo(3),
                "Purchase must validate before debit and again inside the bank callback.");
            Assert.That(consoles, Does.Contain("catch (BankMutationRollbackException"));
            Assert.That(consoles, Does.Contain(
                "BlockShuttlePurchase(userId, targetId, purchaseReservationId)"));
            Assert.That(consoles, Does.Contain("await TryCleanupFailedShuttlePurchaseAsync("));
            Assert.That(consoles, Does.Contain("if (!finalized && finalizationState.StagedShuttleUid != null)"));
            Assert.That(system, Does.Contain("_shuttlePurchaseUsersInFlight.Clear()"));
            Assert.That(system, Does.Contain("_deedMutationCardsInFlight.Clear()"));
            Assert.That(system, Does.Not.Contain("_shuttlePurchaseUsersBlocked.Clear()"));
            Assert.That(system, Does.Not.Contain("_deedMutationCardsBlocked.Clear()"));
        });
    }

    [Test]
    public void ReusableVouchersUseCooldownInsteadOfConsumingTheirOnlyRedemption()
    {
        var source = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var validation = source[source.IndexOf("private bool TryValidateShuttlePurchase(", StringComparison.Ordinal)..source.IndexOf("private async Task<bool> TryCreatePurchasedShuttleAsync(", StringComparison.Ordinal)];
        var finalizer = source[source.IndexOf("private async Task<bool> TryCreatePurchasedShuttleAsync(", StringComparison.Ordinal)..source.IndexOf("private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(validation, Does.Contain("voucher.DestroyOnEmpty && voucher.RedemptionsLeft <= 0"));
            Assert.That(validation, Does.Contain("_timing.CurTime < voucher.NextBuyAt"));
            Assert.That(finalizer, Does.Contain("voucher.NextBuyAt = _timing.CurTime + voucher.Cooldown;"));
            Assert.That(finalizer, Does.Contain("if (voucher.DestroyOnEmpty)"));
            Assert.That(finalizer.IndexOf("voucher.NextBuyAt = _timing.CurTime + voucher.Cooldown;", StringComparison.Ordinal),
                Is.LessThan(finalizer.IndexOf("if (voucher.DestroyOnEmpty)", StringComparison.Ordinal)));
            Assert.That(finalizer.IndexOf("if (voucher.DestroyOnEmpty)", StringComparison.Ordinal),
                Is.LessThan(finalizer.IndexOf("voucher.RedemptionsLeft--;", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void PurchasePublishesDeedBeforeExternalTargetMutations()
    {
        var source = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var methodStart = source.IndexOf("private async Task<bool> TryCreatePurchasedShuttleAsync(");
        var methodEnd = source.IndexOf(
            "private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(",
            methodStart);
        Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(methodEnd, Is.GreaterThan(methodStart));

        var finalizer = source[methodStart..methodEnd];
        var deedMarker = finalizer.IndexOf("state.TargetDeedPublished = true;");
        Assert.Multiple(() =>
        {
            Assert.That(deedMarker, Is.GreaterThanOrEqualTo(0));
            Assert.That(finalizer.IndexOf("if (voucher.DestroyOnEmpty)"), Is.GreaterThan(deedMarker));
            Assert.That(finalizer.IndexOf("voucher.RedemptionsLeft--;"), Is.GreaterThan(finalizer.IndexOf("if (voucher.DestroyOnEmpty)", StringComparison.Ordinal)));
            Assert.That(finalizer.IndexOf("_accessSystem.TrySetTags("), Is.GreaterThan(deedMarker));
            Assert.That(finalizer.IndexOf("_shipOwnership.RegisterShipOwnership("), Is.GreaterThan(deedMarker));
        });
    }

    private static string ReadSource(string relativePath)
    {
        var root = Path.GetFullPath(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", ".."));
        return File.ReadAllText(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
