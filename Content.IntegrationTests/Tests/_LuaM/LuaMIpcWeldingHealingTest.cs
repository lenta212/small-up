using System.Linq;
using Content.Server._EinsteinEngines.Silicon.WeldingHealing;
using Content.Server.Tools;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[TestOf(typeof(WeldingHealingComponent))]
public sealed class LuaMIpcWeldingHealingTest
{
    private const string DamageContainerId = "LuaMIpcHealingTestContainer";
    private const string TargetPrototypeId = "LuaMIpcHealingTestTarget";
    private const string ToolPrototypeId = "LuaMIpcHealingTestTool";

    [TestPrototypes]
    private const string Prototypes = """
- type: damageContainer
  id: LuaMIpcHealingTestContainer
  supportedTypes:
  - Blunt

- type: entity
  id: LuaMIpcHealingTestTarget
  components:
  - type: DoAfter
  - type: Hands
  - type: Damageable
    damageContainer: LuaMIpcHealingTestContainer
  - type: WeldingHealable

- type: entity
  parent: NaniteApplicator
  id: LuaMIpcHealingTestTool
  components:
  - type: WeldingHealing
    doAfterDelay: 1
    damageContainers:
    - LuaMIpcHealingTestContainer
""";

    [Test]
    public async Task UseInHandRepairsOnceAndStopsWhenFullyHealed()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var damageableSystem = entities.System<DamageableSystem>();
        var handsSystem = entities.System<SharedHandsSystem>();
        var toolSystem = entities.System<ToolSystem>();
        var map = await pair.CreateTestMap();

        EntityUid target = default;
        EntityUid tool = default;
        FixedPoint2 fuelBeforeRepair = default;
        int ticksToComplete = default;

        await server.WaitAssertion(() =>
        {
            target = entities.SpawnEntity(TargetPrototypeId, map.MapCoords);
            tool = entities.SpawnEntity(ToolPrototypeId, map.MapCoords);
            handsSystem.AddHand(target, "test-hand", HandLocation.Middle);
            Assert.That(handsSystem.TryPickupAnyHand(target, tool), Is.True);

            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");
            damageableSystem.TryChangeDamage(
                target,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true);

            var damageable = entities.GetComponent<DamageableComponent>(target);
            var healing = entities.GetComponent<WeldingHealingComponent>(tool);
            Assert.Multiple(() =>
            {
                Assert.That(damageable.TotalDamage, Is.EqualTo(FixedPoint2.New(10)));
                Assert.That(damageable.Damage.DamageDict.ContainsKey("Piercing"), Is.False);
                Assert.That(healing.Damage.DamageDict.ContainsKey("Piercing"), Is.True);
                Assert.That(healing.DamageContainers, Does.Contain(DamageContainerId));
                Assert.That(healing.SelfHealPenalty, Is.EqualTo(3f));
                Assert.That(healing.BreakOnMove, Is.True);
                Assert.That(healing.BreakOnDamage, Is.True);
            });

            fuelBeforeRepair = toolSystem.GetWelderFuelAndCapacity(tool).fuel;

            var use = new UseInHandEvent(target);
            entities.EventBus.RaiseLocalEvent(tool, use);
            Assert.That(use.Handled, Is.True);

            var doAfters = entities.GetComponent<DoAfterComponent>(target);
            Assert.That(CountRunning(doAfters), Is.EqualTo(1));

            var repairDelay = TimeSpan.FromSeconds(healing.DoAfterDelay * healing.SelfHealPenalty);
            ticksToComplete = (int) Math.Ceiling(repairDelay.Ticks / (double) timing.TickPeriod.Ticks) + 2;
        });

        await pair.RunTicksSync(ticksToComplete);

        await server.WaitAssertion(() =>
        {
            var damageable = entities.GetComponent<DamageableComponent>(target);
            var healing = entities.GetComponent<WeldingHealingComponent>(tool);
            var doAfters = entities.GetComponent<DoAfterComponent>(target);
            var fuelAfterRepair = toolSystem.GetWelderFuelAndCapacity(tool).fuel;

            Assert.Multiple(() =>
            {
                Assert.That(damageable.TotalDamage, Is.EqualTo(FixedPoint2.Zero));
                Assert.That(fuelAfterRepair, Is.EqualTo(fuelBeforeRepair - healing.FuelCost));
                Assert.That(CountRunning(doAfters), Is.Zero,
                    "A completed repair must not schedule another do-after when no repairable damage remains.");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static int CountRunning(DoAfterComponent doAfters)
    {
        return doAfters.DoAfters.Values.Count(doAfter => !doAfter.Cancelled && !doAfter.Completed);
    }
}
