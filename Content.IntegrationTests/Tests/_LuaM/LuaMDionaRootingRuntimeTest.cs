using System.Linq;
using Content.Server._LuaM.Species;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Movement.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared._LuaM.Species;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMDionaRootingRuntimeTest
{
    [Test]
    public async Task RootingTradesNutritionAndMobilityForPhysicalHealingAndDamageInterruptsIt()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var damage = entities.System<DamageableSystem>();
        var hunger = entities.System<HungerSystem>();
        var rootingSystem = entities.System<LuaMDionaRootingSystem>();
        var map = await pair.CreateTestMap();
        EntityUid diona = default;
        float nutritionBefore = default;
        double damageBefore = default;

        await server.WaitAssertion(() =>
        {
            diona = entities.SpawnEntity("MobDiona", map.GridCoords);
            entities.RemoveComponent<RespiratorComponent>(diona); // Keep the fixture focused: the bare test grid has no air.
            entities.RemoveComponent<BloodstreamComponent>(diona);
            entities.RemoveComponent<TemperatureComponent>(diona);
            entities.RemoveComponent<ThirstComponent>(diona);
            Assert.That(entities.GetComponent<ActionGrantComponent>(diona).Actions.Any(a => a == "ActionLuaMDionaToggleRooting"),
                Is.True, "Every player Diona must receive the rooting action.");

            hunger.SetHunger(diona, 150, entities.GetComponent<HungerComponent>(diona));
            damage.TryChangeDamage(diona,
                new DamageSpecifier(prototypes.Index<DamageTypePrototype>("Blunt"), 20),
                ignoreResistances: true);
            nutritionBefore = hunger.GetHunger(entities.GetComponent<HungerComponent>(diona));
            damageBefore = entities.GetComponent<DamageableComponent>(diona).TotalDamage.Double();

            var rooting = entities.GetComponent<LuaMDionaRootingComponent>(diona);
            rootingSystem.SetRooted((diona, rooting), true);
            Assert.That(entities.GetComponent<MovementSpeedModifierComponent>(diona).CurrentWalkSpeed, Is.Zero,
                "Rooted Diona must actually be immobile.");
            rooting.NextHeal = TimeSpan.Zero;
            rootingSystem.Update(0f);
        });

        await server.WaitAssertion(() =>
        {
            var rooting = entities.GetComponent<LuaMDionaRootingComponent>(diona);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<DamageableComponent>(diona).TotalDamage.Double(), Is.LessThan(damageBefore));
                Assert.That(hunger.GetHunger(entities.GetComponent<HungerComponent>(diona)), Is.LessThan(nutritionBefore));
                Assert.That(rooting.Rooted, Is.True);
            });

            damage.TryChangeDamage(diona,
                new DamageSpecifier(prototypes.Index<DamageTypePrototype>("Heat"), 1),
                ignoreResistances: true);
            Assert.Multiple(() =>
            {
                Assert.That(rooting.Rooted, Is.False, "Incoming damage must interrupt rooted healing.");
                Assert.That(entities.GetComponent<MovementSpeedModifierComponent>(diona).CurrentWalkSpeed, Is.GreaterThan(0));
            });
        });

        await pair.CleanReturnAsync();
    }
}
