using System.Linq;
using System;
using System.Collections.Generic;
using Content.Server._LuaM.Donation;
using Content.Server.Mind;
using Content.Shared.Paper;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMDonationShopTest
{
    private static readonly ResPath DonationShopLedgerPath = new("/luam/donation-shop.json");

    [Test]
    public async Task DonationShopRequiresManualAccessAndConsumesBalance()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var resources = server.ResolveDependency<IResourceManager>();
            var mindSystem = entMan.System<MindSystem>();
            var shop = entMan.System<LuaMDonationShopSystem>();

            var testMap = await pair.CreateTestMap();
            EntityUid buyer = default;

            await server.WaitPost(() =>
            {
                buyer = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMDonationShopTest");
                mindSystem.TransferTo(mind, buyer);
                playerMan.SetAttachedEntity(serverSession, buyer);
                ResetDonationShopLedger(resources);
            });

            await pair.RunTicksSync(5);

            var success = false;
            var status = string.Empty;

            await server.WaitPost(() =>
            {
                shop.GrantBalance(serverSession.UserId, serverSession.Name, 1500, "integration-test", "seed support balance");
                success = shop.TryPurchase(buyer, "supporter-badge", out status);
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(success, Is.False);
                Assert.That(status, Is.EqualTo(Loc.GetString("comp-pda-ui-donation-shop-status-locked")));
                Assert.That(shop.TryGetRecord(serverSession.UserId, out var record), Is.True);
                Assert.That(record.Balance, Is.EqualTo(1500));
                Assert.That(record.Purchases, Is.Empty);
            });

            await server.WaitPost(() =>
            {
                var accessRecord = shop.GrantAccessUnits(serverSession.UserId, serverSession.Name, 1, "integration-test", "unlock shop");
                Assert.That(accessRecord.Access, Is.True);
                Assert.That(accessRecord.AccessUntil, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(27)));
                Assert.That(accessRecord.AccessUntil, Is.LessThan(DateTimeOffset.UtcNow.AddDays(32)));
            });

            await pair.RunTicksSync(2);

            HashSet<EntityUid> beforePapers = [];
            await server.WaitAssertion(() =>
            {
                beforePapers = entMan.AllComponents<PaperComponent>().Select(component => component.Uid).ToHashSet();
            });

            var duplicateSuccess = true;
            var duplicateStatus = string.Empty;

            await server.WaitPost(() =>
            {
                success = shop.TryPurchase(buyer, "supporter-badge", out status);
                duplicateSuccess = shop.TryPurchase(buyer, "supporter-badge", out duplicateStatus);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(success, Is.True, status);
                Assert.That(status, Does.Contain(Loc.GetString("comp-pda-ui-donation-shop-item-supporter-badge")));
                Assert.That(status, Does.Contain("1000"));
                Assert.That(duplicateSuccess, Is.False);
                Assert.That(duplicateStatus, Is.EqualTo(Loc.GetString("comp-pda-ui-donation-shop-status-owned")));

                Assert.That(shop.TryGetRecord(serverSession.UserId, out var record), Is.True);
                Assert.That(record.Balance, Is.EqualTo(1000));
                Assert.That(record.Purchases.ContainsKey("supporter-badge"), Is.True);
                Assert.That(record.Purchases["supporter-badge"], Is.EqualTo(1));
                Assert.That(record.Ledger.Select(entry => entry.Action), Does.Contain("grant-balance"));
                Assert.That(record.Ledger.Select(entry => entry.Action), Does.Contain("grant-access"));
                Assert.That(record.Ledger.Select(entry => entry.Action).Count(action => action == "purchase"), Is.EqualTo(1));

                var papers = entMan.AllComponents<PaperComponent>().Select(component => component.Uid).ToList();
                Assert.That(papers, Has.Count.EqualTo(beforePapers.Count));
            });

            await server.WaitPost(() =>
            {
                success = shop.TryPurchase(buyer, "sector-certificate", out status);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(success, Is.True, status);
                Assert.That(status, Does.Contain(Loc.GetString("comp-pda-ui-donation-shop-item-sector-certificate")));
                Assert.That(status, Does.Contain("750"));

                Assert.That(shop.TryGetRecord(serverSession.UserId, out var record), Is.True);
                Assert.That(record.Balance, Is.EqualTo(750));
                Assert.That(record.Purchases.ContainsKey("sector-certificate"), Is.True);
                Assert.That(record.Purchases["sector-certificate"], Is.EqualTo(1));
                Assert.That(record.Ledger.Select(entry => entry.Action).Count(action => action == "purchase"), Is.EqualTo(2));

                var papers = entMan.AllComponents<PaperComponent>().Select(component => component.Uid).ToList();
                Assert.That(papers, Has.Count.EqualTo(beforePapers.Count + 1));

                var certificate = papers.Single(uid => !beforePapers.Contains(uid));
                var paper = entMan.GetComponent<PaperComponent>(certificate);
                Assert.That(paper.Content, Does.Contain(serverSession.Name));
                Assert.That(
                    paper.Content,
                    Does.Contain(Loc.GetString("comp-pda-ui-donation-shop-item-sector-certificate")));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static void ResetDonationShopLedger(IResourceManager resources)
    {
        if (resources.UserData.Exists(DonationShopLedgerPath))
            resources.UserData.Delete(DonationShopLedgerPath);
    }
}
