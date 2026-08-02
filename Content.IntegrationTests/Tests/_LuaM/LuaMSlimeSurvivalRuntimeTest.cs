using System.Linq;
using Content.Server.Polymorph.Systems;
using Content.Server._LuaM.Species;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMSlimeSurvivalRuntimeTest
{
    [Test]
    public async Task CriticalSlimeGetsSmallDoorPassingFormAndFoodRestoresBody()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mobState = entities.System<MobStateSystem>();
        var polymorph = entities.System<PolymorphSystem>();
        var hunger = entities.System<HungerSystem>();
        var map = await pair.CreateTestMap();
        EntityUid slime = default;
        EntityUid mini = default;

        await server.WaitAssertion(() =>
        {
            slime = entities.SpawnEntity("MobSlimePerson", map.GridCoords);
            mobState.ChangeMobState(slime, MobState.Critical);
            var stateActions = entities.GetComponent<MobStateActionsComponent>(slime);
            Assert.That(stateActions.GrantedActions.Any(action =>
                    entities.GetComponent<MetaDataComponent>(action).EntityPrototype?.ID == "ActionLuaMSlimeSurvivalForm"),
                Is.True,
                "Only the critical-state hotbar should expose the survival form.");

            mini = polymorph.PolymorphEntity(slime, "LuaMSlimeSurvivalForm")!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMMiniSlimeComponent>(mini), Is.True);
                Assert.That(entities.GetComponent<FixturesComponent>(mini).Fixtures["fix1"].CollisionMask,
                    Is.EqualTo((int) CollisionGroup.SmallMobMask),
                    "The mini form must pass airlock Mid/High layers but still collide with Impassable walls.");
                Assert.That(entities.System<MobThresholdSystem>().GetThresholdForState(mini, MobState.Dead).Double(),
                    Is.EqualTo(30).Within(0.001),
                    "The escape form must remain fragile rather than becoming a combat upgrade.");
            });

            hunger.SetHunger(mini, 100, entities.GetComponent<HungerComponent>(mini));
        });

        await server.WaitRunTicks(90);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.Deleted(mini), Is.True, "A fed mini-slime should be consumed by reversion.");
            Assert.That(entities.Deleted(slime), Is.False, "The original slime body should be restored.");
        });

        await pair.CleanReturnAsync();
    }
}
