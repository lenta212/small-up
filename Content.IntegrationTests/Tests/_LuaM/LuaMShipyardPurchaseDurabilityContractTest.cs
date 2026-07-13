using System.IO;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipyardPurchaseDurabilityContractTest
{
    [Test]
    public void PurchaseUsesDurableDebitWithSynchronousWorldFinalizer()
    {
        var source = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ObservePurchaseMessageAsync"));
            Assert.That(source, Does.Contain("await _bank.TryBankWithdrawAsync("));
            Assert.That(source, Does.Contain("finalizeAfterCommit: FinalizeAfterCommit"));
            Assert.That(source, Does.Contain("TryCreatePurchasedShuttle("));
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
            Assert.That(consoles, Does.Contain("BlockShuttlePurchase(userId, targetId)"));
            Assert.That(consoles, Does.Contain("TryCleanupFailedShuttlePurchase("));
            Assert.That(consoles, Does.Contain("if (!finalized && stagedShuttleUid != null)"));
            Assert.That(system, Does.Contain("_shuttlePurchaseUsersInFlight.Clear()"));
            Assert.That(system, Does.Contain("_shuttlePurchaseTargetsInFlight.Clear()"));
            Assert.That(system, Does.Not.Contain("_shuttlePurchaseUsersBlocked.Clear()"));
            Assert.That(system, Does.Not.Contain("_shuttlePurchaseTargetsBlocked.Clear()"));
        });
    }

    [Test]
    public void PurchasePublishesDeedBeforeExternalTargetMutations()
    {
        var source = ReadSource(
            "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var methodStart = source.IndexOf("private bool TryCreatePurchasedShuttle(");
        var methodEnd = source.IndexOf("private bool TryCleanupFailedShuttlePurchase(", methodStart);
        Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(methodEnd, Is.GreaterThan(methodStart));

        var finalizer = source[methodStart..methodEnd];
        var deedMarker = finalizer.IndexOf("targetDeedPublished = true;");
        Assert.Multiple(() =>
        {
            Assert.That(deedMarker, Is.GreaterThanOrEqualTo(0));
            Assert.That(finalizer.IndexOf("voucher.RedemptionsLeft--;"), Is.GreaterThan(deedMarker));
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
