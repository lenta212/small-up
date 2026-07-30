using Content.Server.Cargo.Systems;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Cargo.Components;
using Content.Shared.Contraband;
using Content.Shared.Damage.Components;
using Content.Shared.Explosion.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMSelectiveUpstreamFixTest
{
    [Test]
    public async Task SelectedPrototypeFixesStayApplied()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
        });

        try
        {
            var server = pair.Server;
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var components = server.ResolveDependency<IComponentFactory>();

            await server.WaitAssertion(() =>
            {
                var l27 = prototypes.Index<EntityPrototype>("ClothingOuterHardsuitUsspL27");
                Assert.That(
                    l27.TryGetComponent<ContrabandComponent>(out _, components),
                    Is.False,
                    "The L-27 must not inherit a redeemable other-faction contraband value.");

                AssertFaction(
                    prototypes.Index<EntityPrototype>("MobAsakimGhostrole"),
                    components,
                    "Asakim ghost role");
                AssertFaction(
                    prototypes.Index<EntityPrototype>("BorgChassisRedacted"),
                    components,
                    "redacted borg");

                var light = prototypes.Index<EntityPrototype>("AlwaysPoweredWallLight");
                Assert.That(
                    light.TryGetComponent<RequireProjectileTargetComponent>(out _, components),
                    Is.True,
                    "Always-powered lights must require deliberate projectile targeting.");

                var camera = prototypes.Index<EntityPrototype>("SurveillanceCameraConstructed");
                Assert.Multiple(() =>
                {
                    Assert.That(
                        camera.TryGetComponent<RequireProjectileTargetComponent>(out _, components),
                        Is.True,
                        "Cameras must require deliberate projectile targeting.");
                    Assert.That(
                        camera.TryGetComponent<ExplosionResistanceComponent>(out var resistance, components),
                        Is.True,
                        "Cameras must retain their explosion resistance.");
                    Assert.That(resistance.DamageCoefficient, Is.EqualTo(0.04f).Within(0.0001f));
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task BodyPartPricingDoesNotDoubleCountContainedOrgans()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
        });

        try
        {
            var server = pair.Server;
            var testMap = await pair.CreateTestMap();
            var entities = server.ResolveDependency<IEntityManager>();
            var containers = entities.System<SharedContainerSystem>();
            var pricing = entities.System<PricingSystem>();

            await server.WaitAssertion(() =>
            {
                var bodyPart = entities.SpawnEntity(null, testMap.MapCoords);
                entities.AddComponent<BodyPartComponent>(bodyPart);
                entities.AddComponent<StaticPriceComponent>(bodyPart).Price = 10d;
                var bodyPartContainer =
                    containers.EnsureContainer<Container>(bodyPart, "LuaMBodyPartPricingContainer");

                var ordinaryContainerOwner = entities.SpawnEntity(null, testMap.MapCoords);
                entities.AddComponent<StaticPriceComponent>(ordinaryContainerOwner).Price = 10d;
                var ordinaryContainer =
                    containers.EnsureContainer<Container>(ordinaryContainerOwner, "LuaMOrdinaryPricingContainer");

                var organ = entities.SpawnEntity(null, testMap.MapCoords);
                entities.AddComponent<OrganComponent>(organ);
                entities.AddComponent<StaticPriceComponent>(organ).Price = 250d;

                Assert.That(containers.Insert(organ, bodyPartContainer), Is.True);
                AssertPrices(pricing, bodyPart, testMap.Grid.Owner, 10d);

                Assert.That(containers.Remove(organ, bodyPartContainer), Is.True);
                Assert.That(containers.Insert(organ, ordinaryContainer), Is.True);
                AssertPrices(pricing, ordinaryContainerOwner, testMap.Grid.Owner, 260d);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static void AssertFaction(
        EntityPrototype prototype,
        IComponentFactory components,
        string description)
    {
        Assert.That(
            prototype.TryGetComponent<NpcFactionMemberComponent>(out var faction, components),
            Is.True,
            $"{description} must have an NPC faction.");

        var siliconFaction = new ProtoId<NpcFactionPrototype>("SiliconsExpeditionNF");
        var syndicateFaction = new ProtoId<NpcFactionPrototype>("Syndicate");
        Assert.Multiple(() =>
        {
            Assert.That(faction.Factions, Does.Contain(siliconFaction), description);
            Assert.That(faction.Factions, Does.Not.Contain(syndicateFaction), description);
        });
    }

    private static void AssertPrices(
        PricingSystem pricing,
        EntityUid entity,
        EntityUid currentGrid,
        double expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(pricing.GetPrice(entity), Is.EqualTo(expected).Within(0.001d));
            Assert.That(
                pricing.GetPriceWithVendingDiscount(entity, currentGrid),
                Is.EqualTo(expected).Within(0.001d));
            Assert.That(pricing.GetPriceConditional(entity), Is.EqualTo(expected).Within(0.001d));
        });
    }
}
