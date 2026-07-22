using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.Construction.Components;
using Content.Server.Spawners.Components;
using Content.Server._Mono.FireControl;
using Content.Server._NF.Shipyard.Components;
using Content.Shared.Cargo.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Maps;
using Content.Shared.Store;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMImportedShipContentPrototypeTest
{
    private static readonly ProtoId<CurrencyPrototype> FederationMilitaryCredit = "FederationMilitaryCredit";

    [Test]
    public async Task VesselGameMapVoucherListingAndGunneryContractsAreComplete()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();
        var resources = server.ResolveDependency<IResourceManager>();

        await server.WaitAssertion(() =>
        {
            AssertVessel(
                prototypes,
                resources,
                "TSFMCArrow",
                "TSF-VSK Arrow",
                320000,
                2,
                ShipyardConsoleUiKey.Security,
                VesselSize.Large,
                "/Maps/_LuaM/Shuttles/TSFMC/Arrow.yml",
                false,
                new[] { VesselClass.Frigate },
                new[] { VesselEngine.Plasma },
                new[] { "TSF" });

            AssertVessel(
                prototypes,
                resources,
                "TSFMCRising",
                "TSF-VSK Rising",
                320000,
                1,
                ShipyardConsoleUiKey.Security,
                VesselSize.Large,
                "/Maps/_LuaM/Shuttles/TSFMC/Rising.yml",
                false,
                new[] { VesselClass.Frigate, VesselClass.Pursuit },
                new[] { VesselEngine.Plasma },
                new[] { "TSF" });

            AssertVessel(
                prototypes,
                resources,
                "Phoenix",
                "FXD Phoenix",
                420000,
                2,
                ShipyardConsoleUiKey.Expedition,
                VesselSize.Large,
                "/Maps/_LuaM/Shuttles/Expedition/Phoenix.yml",
                true,
                new[]
                {
                    VesselClass.Medical,
                    VesselClass.Chemistry,
                    VesselClass.Pursuit,
                    VesselClass.Expedition,
                    VesselClass.Science,
                    VesselClass.Salvage,
                },
                new[] { VesselEngine.NFR },
                Array.Empty<string>());

            var phoenix = prototypes.Index<VesselPrototype>("Phoenix");
            Assert.That(phoenix.Classes, Does.Not.Contain(VesselClass.Capital),
                "The corrected Phoenix must not be treated as a capital vessel.");

            AssertVoucher(prototypes, components, "ShipVoucherArrow", "TSFMCArrow", 7200, 600000);
            AssertVoucher(prototypes, components, "ShipVoucherRising", "TSFMCRising", 10800, 800000);

            var randomT2 = prototypes.Index<EntityPrototype>("ShipVoucherTsfT2Random");
            Assert.That(
                randomT2.TryGetComponent<RandomSpawnerComponent>(out var randomSpawner, components),
                Is.True);
            Assert.That(randomSpawner.Prototypes.Select(id => id.Id), Does.Contain("ShipVoucherArrow"));

            AssertListing(
                prototypes,
                "UplinkSecurityT2VoucherArrow",
                "ShipVoucherArrow",
                175,
                "uplink-security-t2-arrow-voucher-name",
                "uplink-security-t2-arrow-voucher-desc");
            AssertListing(
                prototypes,
                "UplinkSecurityT3VoucherRising",
                "ShipVoucherRising",
                350,
                "uplink-security-t3-rising-voucher-name",
                "uplink-security-t3-rising-voucher-desc");

            var gunneryServer = prototypes.Index<EntityPrototype>("GunneryServerExtra");
            Assert.That(
                gunneryServer.TryGetComponent<FireControlServerComponent>(out var fireControl, components),
                Is.True);
            Assert.That(fireControl.ProcessingPower, Is.EqualTo(120));
            Assert.That(gunneryServer.TryGetComponent<MachineComponent>(out var machine, components), Is.True);
            Assert.That(machine.Board?.Id, Is.EqualTo("MachineGCSExtraCircuitboard"));

            var boardPrototype = prototypes.Index<EntityPrototype>("MachineGCSExtraCircuitboard");
            Assert.That(
                boardPrototype.TryGetComponent<MachineBoardComponent>(out var board, components),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(board.Prototype.Id, Is.EqualTo("GunneryServerExtra"));
                Assert.That(board.Requirements[(ProtoId<MachinePartPrototype>) "Capacitor"], Is.EqualTo(2));
                Assert.That(board.Requirements[(ProtoId<MachinePartPrototype>) "Manipulator"], Is.EqualTo(2));
                Assert.That(board.StackRequirements[(ProtoId<Content.Shared.Stacks.StackPrototype>) "Glass"], Is.EqualTo(10));
                Assert.That(board.StackRequirements[(ProtoId<Content.Shared.Stacks.StackPrototype>) "Steel"], Is.EqualTo(10));
            });

            AssertLocalizationContains(resources, "/Locale/en-US/_LuaM/shipyard.ftl", "vessel-arrow-name", "ent-ShipVoucherRising");
            AssertLocalizationContains(resources, "/Locale/ru-RU/_LuaM/shipyard.ftl", "vessel-rising-name", "ent-GunneryServerExtra");
            AssertLocalizationContains(resources, "/Locale/en-US/_LuaM/catalog/tsfmc_uplink_catalog.ftl", "uplink-security-t2-arrow-voucher-name", "uplink-security-t3-rising-voucher-name");
            AssertLocalizationContains(resources, "/Locale/ru-RU/_LuaM/catalog/tsfmc_uplink_catalog.ftl", "uplink-security-t2-arrow-voucher-name", "uplink-security-t3-rising-voucher-name");
            AssertLocalizationContains(resources, "/Locale/ru-RU/ss14-ru/prototypes/_LuaM/entities/objects/Devices/ship_vouchers.ftl", "ent-ShipVoucherScorpionLuaM", "ent-ShipVoucherArrow", "ent-ShipVoucherRising");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ImportedShuttleMapsLoadWithRegisteredPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var mapLoader = entities.System<MapLoaderSystem>();
        var mapSystem = entities.System<MapSystem>();

        await server.WaitPost(() =>
        {
            foreach (var vesselId in new[] { "TSFMCArrow", "TSFMCRising", "Phoenix" })
            {
                var vessel = prototypes.Index<VesselPrototype>(vesselId);
                mapSystem.CreateMap(out var mapId);
                try
                {
                    Assert.That(
                        mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var grid),
                        Is.True,
                        $"Failed to load imported shuttle map for {vesselId}.");
                    Assert.That(grid.HasValue, Is.True);
                    Assert.That(entities.HasComponent<MapGridComponent>(grid.Value), Is.True);
                }
                finally
                {
                    mapSystem.DeleteMap(mapId);
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertVessel(
        IPrototypeManager prototypes,
        IResourceManager resources,
        string id,
        string name,
        int price,
        int limit,
        ShipyardConsoleUiKey group,
        VesselSize category,
        string mapPath,
        bool purchasable,
        IEnumerable<VesselClass> classes,
        IEnumerable<VesselEngine> engines,
        IEnumerable<string> companies)
    {
        var vessel = prototypes.Index<VesselPrototype>(id);
        var gameMap = prototypes.Index<GameMapPrototype>(id);
        var expectedPath = new ResPath(mapPath);

        Assert.Multiple(() =>
        {
            Assert.That(vessel.Name, Is.EqualTo(name));
            Assert.That(vessel.Description, Is.Not.Empty);
            Assert.That(vessel.Price, Is.EqualTo(price));
            Assert.That(vessel.LimitActive, Is.EqualTo(limit));
            Assert.That(vessel.Group, Is.EqualTo(group));
            Assert.That(vessel.Category, Is.EqualTo(category));
            Assert.That(vessel.Purchasable, Is.EqualTo(purchasable));
            Assert.That(vessel.ShuttlePath, Is.EqualTo(expectedPath));
            Assert.That(vessel.Classes, Is.EquivalentTo(classes));
            Assert.That(vessel.Engines, Is.EquivalentTo(engines));
            Assert.That(vessel.Company, Is.EquivalentTo(companies));
            Assert.That(gameMap.MapPath, Is.EqualTo(expectedPath));
            Assert.That(gameMap.MapName, Is.EqualTo(name));
            Assert.That(gameMap.Stations.Keys, Does.Contain(id));
            Assert.That(resources.ContentFileExists(expectedPath), Is.True,
                $"Missing shuttle map resource {expectedPath} for vessel {id}.");
        });
    }

    private static void AssertVoucher(
        IPrototypeManager prototypes,
        IComponentFactory components,
        string voucherId,
        string vesselId,
        int cooldownSeconds,
        double vendPrice)
    {
        var prototype = prototypes.Index<EntityPrototype>(voucherId);
        Assert.That(
            prototype.TryGetComponent<ShipyardVoucherComponent>(out var voucher, components),
            Is.True);
        Assert.That(prototype.TryGetComponent<StaticPriceComponent>(out var price, components), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(voucher.ConsoleType, Is.EqualTo(ShipyardConsoleUiKey.Security));
            Assert.That(voucher.CompanyName, Is.EqualTo("TSF"));
            Assert.That(voucher.DestroyOnEmpty, Is.False, "LuaM LPC ship vouchers must be reusable and limited by cooldown.");
            Assert.That(voucher.RedemptionsLeft, Is.EqualTo(1), "The default redemption count is ignored for reusable cooldown vouchers.");
            Assert.That(voucher.Cooldown, Is.EqualTo(TimeSpan.FromSeconds(cooldownSeconds)));
            Assert.That(voucher.Vessels.Select(id => id.Id), Is.EquivalentTo(new[] { vesselId }));
            Assert.That(price.VendPrice, Is.EqualTo(vendPrice));
        });
    }

    private static void AssertListing(
        IPrototypeManager prototypes,
        string listingId,
        string productEntity,
        int cost,
        string name,
        string description)
    {
        var listing = prototypes.Index<ListingPrototype>(listingId);
        Assert.Multiple(() =>
        {
            Assert.That(listing.ProductEntity?.Id, Is.EqualTo(productEntity));
            Assert.That(listing.Cost[FederationMilitaryCredit], Is.EqualTo(FixedPoint2.New(cost)));
            Assert.That(listing.Categories.Select(id => id.Id), Does.Contain("UplinkSecurityVouchers"));
            Assert.That(listing.Name, Is.EqualTo(name));
            Assert.That(listing.Description, Is.EqualTo(description));
            Assert.That(listing.Conditions, Has.Count.EqualTo(1));
        });
    }

    private static void AssertLocalizationContains(
        IResourceManager resources,
        string path,
        params string[] expectedKeys)
    {
        var resourcePath = new ResPath(path);
        Assert.That(resources.ContentFileExists(resourcePath), Is.True, $"Missing localization resource {path}.");

        using var stream = resources.ContentFileRead(resourcePath);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        foreach (var key in expectedKeys)
            Assert.That(text, Does.Contain(key), $"Missing localization key {key} in {path}.");
    }
}
