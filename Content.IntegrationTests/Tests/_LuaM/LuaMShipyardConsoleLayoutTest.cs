using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Client._NF.Shipyard.BUI;
using Content.Client._NF.Shipyard.UI;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipyardConsoleLayoutTest
{
    private static readonly Vector2 MinimumViewport = new(640f, 520f);

    [Test]
    public async Task ShipyardSeparatesCatalogAndFleetWithoutNestedCardScrolling()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
        });

        try
        {
            var activator = pair.Client.ResolveDependency<IDynamicTypeFactory>();

            await pair.Client.WaitAssertion(() =>
            {
                var boundUi = new ShipyardConsoleBoundUserInterface(
                    new EntityUid(1),
                    ShipyardConsoleUiKey.Shipyard);
                using var menu = new ShipyardConsoleMenu(boundUi);
                using var row = activator.CreateInstance<VesselRow>(oneOff: true, inject: false);

                Arrange(menu, MinimumViewport);

                var tabs = menu.FindControl<TabContainer>("ModeTabs");
                var vesselScroll = menu.FindControl<ScrollContainer>("VesselScroll");
                var vessels = menu.FindControl<BoxContainer>("Vessels");

                Assert.Multiple(() =>
                {
                    Assert.That(menu.Resizable, Is.True);
                    Assert.That(menu.MinSize, Is.EqualTo(MinimumViewport));
                    Assert.That(menu.Size, Is.EqualTo(MinimumViewport));
                    Assert.That(tabs.ChildCount, Is.EqualTo(2));
                    Assert.That(tabs.GetActualTabTitle(0), Is.Not.Null.And.Not.Empty);
                    Assert.That(tabs.GetActualTabTitle(1), Is.Not.Null.And.Not.Empty);
                    Assert.That(vesselScroll.VScrollEnabled, Is.True);
                    Assert.That(vesselScroll.HScrollEnabled, Is.False);
                    Assert.That(vesselScroll.Width, Is.GreaterThan(0f));
                    Assert.That(vesselScroll.Height, Is.GreaterThan(0f));
                    Assert.That(vessels.GetSelfAndLogicalAncestors().Contains(vesselScroll), Is.True);
                    Assert.That(Descendants(row).OfType<ScrollContainer>(), Is.Empty,
                        "Individual hull cards must grow inside the single catalog viewport, not create nested scrollbars.");
                });

                var ownedBoundUi = new ShipyardConsoleBoundUserInterface(
                    new EntityUid(2),
                    ShipyardConsoleUiKey.Shipyard);
                using var ownedMenu = new ShipyardConsoleMenu(ownedBoundUi);
                var ownedTabs = ownedMenu.FindControl<TabContainer>("ModeTabs");
                ownedTabs.CurrentTab = 1;
                var arrangedOwnedScroll = ownedMenu.FindControl<ScrollContainer>("OwnedShipsScroll");
                Arrange(ownedMenu, MinimumViewport);

                Assert.Multiple(() =>
                {
                    Assert.That(arrangedOwnedScroll.VScrollEnabled, Is.True);
                    Assert.That(arrangedOwnedScroll.HScrollEnabled, Is.False);
                    Assert.That(arrangedOwnedScroll.Width, Is.GreaterThan(0f));
                    Assert.That(arrangedOwnedScroll.Height, Is.GreaterThan(0f));
                    Assert.That(ownedMenu.FindControl<BoxContainer>("OwnedShipsPage")
                        .GetSelfAndLogicalAncestors().Contains(arrangedOwnedScroll), Is.True);
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task ProcurementExplainsEveryGlobalLockAndGateLegendUsesSymbols()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
        });

        try
        {
            await pair.Client.WaitAssertion(() =>
            {
                var boundUi = new ShipyardConsoleBoundUserInterface(
                    new EntityUid(1),
                    ShipyardConsoleUiKey.Shipyard);
                using var menu = new ShipyardConsoleMenu(boundUi);
                using var dockWindow = new ShipyardDockSelectionWindow();

                menu.UpdateState(CreateState(
                    hasCredential: false,
                    accessGranted: true,
                    gateAvailable: true));
                AssertBlocked(menu, "shipyard-console-purchase-blocked-no-id");

                menu.UpdateState(CreateState(
                    hasCredential: true,
                    accessGranted: false,
                    gateAvailable: true));
                AssertBlocked(menu, "shipyard-console-purchase-blocked-access");

                menu.UpdateState(CreateState(
                    hasCredential: true,
                    accessGranted: true,
                    gateAvailable: false));
                AssertBlocked(menu, "shipyard-console-purchase-blocked-no-gate");

                menu.UpdateState(CreateState(
                    hasCredential: true,
                    accessGranted: true,
                    gateAvailable: true));

                Assert.That(menu.CanPurchase, Is.True);
                Assert.That(menu.FindControl<Label>("PurchaseStatusTitle").Text,
                    Is.EqualTo(Loc.GetString("shipyard-console-procurement-ready-title")));

                menu.UpdateState(CreateState(
                    hasCredential: true,
                    accessGranted: true,
                    gateAvailable: true,
                    deedName: "Rim Warden",
                    canSell: true,
                    canUnassign: true));

                var legend = dockWindow.FindControl<BoxContainer>("GateLegend");
                var legendText = string.Join(" ", legend.Children.OfType<Label>().Select(label => label.Text));
                Assert.Multiple(() =>
                {
                    Assert.That(menu.CanPurchase, Is.False);
                    Assert.That(menu.FindControl<RichTextLabel>("PurchaseStatusLabel").Text,
                        Is.EqualTo(Loc.GetString("shipyard-console-purchase-blocked-existing-deed")));
                    Assert.That(menu.FindControl<ConfirmButton>("SellShipButton").ConfirmationText,
                        Does.Contain("Rim Warden"));
                    Assert.That(menu.FindControl<ConfirmButton>("UnassignDeedButton").ConfirmationText,
                        Does.Contain("Rim Warden"));
                    Assert.That(legendText, Does.Contain("[+]"));
                    Assert.That(legendText, Does.Contain("[×]"));
                    Assert.That(legendText, Does.Contain("[◎]"));
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task OperationalCopyResolvesInEnglishAndRussian()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
        });

        try
        {
            var localization = pair.Client.ResolveDependency<ILocalizationManager>();

            await pair.Client.WaitAssertion(() =>
            {
                var originalCulture = localization.DefaultCulture;
                try
                {
                    foreach (var cultureName in new[] { "en-US", "ru-RU" })
                    {
                        var culture = CultureInfo.GetCultureInfo(cultureName);
                        if (!localization.HasCulture(culture))
                            localization.LoadCulture(culture);
                        localization.DefaultCulture = culture;

                        foreach (var key in new[]
                                 {
                                     "shipyard-console-tab-catalog",
                                     "shipyard-console-tab-owned",
                                     "shipyard-console-purchase-blocked-no-id",
                                     "shipyard-console-purchase-blocked-access",
                                     "shipyard-console-purchase-blocked-no-gate",
                                     "shipyard-console-confirm-sell",
                                     "shipyard-console-gate-legend-available",
                                 })
                        {
                            Assert.That(
                                localization.TryGetString(key, out var value, ("ship", "Rim Warden")),
                                Is.True,
                                $"{cultureName} is missing {key}");
                            Assert.That(value, Is.Not.Null.And.Not.Empty);
                        }
                    }
                }
                finally
                {
                    localization.DefaultCulture = originalCulture;
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static ShipyardConsoleInterfaceState CreateState(
        bool hasCredential,
        bool accessGranted,
        bool gateAvailable,
        string? deedName = null,
        bool canSell = false,
        bool canUnassign = false)
    {
        return new ShipyardConsoleInterfaceState(
            25000,
            accessGranted,
            deedName,
            12500,
            hasCredential,
            hasCredential ? new NetEntity(20) : null,
            (byte) ShipyardConsoleUiKey.Shipyard,
            (new List<string>(), new List<string>()),
            "Frontier",
            false,
            0.5f,
            new List<ShipyardGateInfo>
            {
                new(new NetEntity(21), "RIM-01", gateAvailable, Vector2.Zero),
            },
            new NetEntity(22),
            false,
            new List<ShipyardStoredShipInfo>(),
            null,
            false,
            canSell,
            false,
            canUnassign);
    }

    private static void AssertBlocked(ShipyardConsoleMenu menu, string reasonKey)
    {
        Assert.Multiple(() =>
        {
            Assert.That(menu.CanPurchase, Is.False);
            Assert.That(menu.FindControl<RichTextLabel>("PurchaseStatusLabel").Text,
                Is.EqualTo(Loc.GetString(reasonKey)));
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children)
        {
            yield return child;

            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void Arrange(Control control, Vector2 viewport)
    {
        control.SetSize = viewport;
        control.Measure(viewport);
        control.Arrange(UIBox2.FromDimensions(Vector2.Zero, viewport));
    }
}
