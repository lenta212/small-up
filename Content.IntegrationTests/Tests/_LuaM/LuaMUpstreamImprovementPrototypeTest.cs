using System.Linq;
using Content.Server.Body.Components;
using Content.Server.RequiresGrid;
using Content.Shared.Body.Components;
using Content.Shared.Body.Organ;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wieldable.Components;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared._Shitmed.Cybernetics;
using Content.Shared._Shitmed.Medical.Surgery.Tools;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMUpstreamImprovementPrototypeTest
{
    [Test]
    public async Task SelectedUpstreamMechanicsRemainIntegratedWithLuaMContent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            AssertCyberneticOrganContracts(prototypes, components);
            AssertConstructionAndWeaponContracts(prototypes, components);
            AssertSurgeryContracts(prototypes, components);
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertCyberneticOrganContracts(
        IPrototypeManager prototypes,
        IComponentFactory components)
    {
        var basicLungs = prototypes.Index<EntityPrototype>("BasicCyberneticLungs");
        Assert.Multiple(() =>
        {
            Assert.That(basicLungs.TryGetComponent<CyberneticsComponent>(out _, components), Is.True);
            Assert.That(basicLungs.TryGetComponent<OrganComponent>(out var organ, components), Is.True);
            Assert.That(organ.SlotId, Is.EqualTo("lungs"));
            Assert.That(basicLungs.TryGetComponent<LungComponent>(out _, components), Is.True);
            Assert.That(basicLungs.TryGetComponent<MetabolizerComponent>(out var metabolizer, components), Is.True);
            Assert.That(metabolizer.SolutionOnBody, Is.False);
            Assert.That(metabolizer.RemoveEmpty, Is.True);
            Assert.That(metabolizer.MetabolizerTypes?.Select(id => id.Id), Does.Contain("Cybernetic"));
        });

        var upgradedLungs = prototypes.Index<EntityPrototype>("UpgradedCyberneticLungs");
        Assert.That(upgradedLungs.TryGetComponent<OrganComponent>(out var upgradedLungOrgan, components), Is.True);
        Assert.That(upgradedLungOrgan.OnAdd, Is.Not.Null);
        Assert.That(upgradedLungOrgan.OnAdd, Does.ContainKey("BreathingImmunity"));

        var upgradedHeart = prototypes.Index<EntityPrototype>("UpgradedCyberneticHeart");
        Assert.That(upgradedHeart.TryGetComponent<OrganComponent>(out var upgradedHeartOrgan, components), Is.True);
        Assert.That(upgradedHeartOrgan.OnAdd, Is.Not.Null);
        Assert.That(upgradedHeartOrgan.OnAdd, Does.ContainKey("PressureImmunity"));
    }

    private static void AssertConstructionAndWeaponContracts(
        IPrototypeManager prototypes,
        IComponentFactory components)
    {
        var solidWall = prototypes.Index<EntityPrototype>("WallSolid");
        Assert.That(solidWall.TryGetComponent<RequiresGridComponent>(out _, components), Is.True,
            "Detached walls must not survive after their grid is destroyed.");

        foreach (var vectorId in new[]
                 {
                     "WeaponSubMachineGunVector9x19mm",
                     "WeaponSubMachineGunVector45_ACP",
                     "WeaponSubMachineGunVectorNtsfHclm",
                 })
        {
            var vector = prototypes.Index<EntityPrototype>(vectorId);
            Assert.That(vector.TryGetComponent<GunComponent>(out var gun, components), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(gun.MinAngle, Is.EqualTo(Angle.FromDegrees(2)));
                Assert.That(gun.MaxAngle, Is.EqualTo(Angle.FromDegrees(12)));
                Assert.That(vector.TryGetComponent<WieldableComponent>(out _, components), Is.False,
                    $"{vectorId} is intentionally a one-handed SMG.");
                Assert.That(vector.TryGetComponent<GunWieldBonusComponent>(out _, components), Is.False,
                    $"{vectorId} must not retain a two-handed accuracy bonus.");
            });
        }

        var sultan = prototypes.Index<EntityPrototype>("WeaponShotgunSultanPulsar");
        Assert.That(sultan.TryGetComponent<GunSpreadModifierComponent>(out var spread, components), Is.True);
        Assert.That(spread.Spread, Is.EqualTo(1f));
    }

    private static void AssertSurgeryContracts(
        IPrototypeManager prototypes,
        IComponentFactory components)
    {
        var gloves = prototypes.Index<EntityPrototype>("ClothingHandsGlovesSurgical");
        Assert.That(
            gloves.TryGetComponent<ClothingGrantComponentComponent>(out var grant, components),
            Is.True);
        Assert.That(grant.Components, Does.ContainKey("SurgeryIgnoreClothing"));

        var boneTool = prototypes.Index<EntityPrototype>("AdvancedBoneGel");
        Assert.That(boneTool.TryGetComponent<BoneGelComponent>(out var gel, components), Is.True);
        Assert.That(boneTool.TryGetComponent<BoneSetterComponent>(out var setter, components), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(gel.Speed, Is.EqualTo(3f));
            Assert.That(setter.Speed, Is.EqualTo(3f));
        });
    }
}
