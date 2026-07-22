using System;
using System.Linq;
using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Server._LuaM.AI;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Repairable;
using Content.Shared._LuaM.AI;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMWorkerBehaviorAdapterSystem))]
public sealed class LuaMWorkerBehaviorAdapterRuntimeTest
{
    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  parent: Crowbar
  id: LuaMWorkerTestRepairTool
  components:
  - type: Tool
    qualities:
    - Applicating

- type: entity
  parent: Brutepack
  id: LuaMWorkerTestMedicine
  components:
  - type: Healing
    damage:
      types:
        Blunt: -100
    delay: 0.25

- type: entity
  id: LuaMWorkerTestPatient
  components:
  - type: Damageable
    damageContainer: Biological
  - type: MobState
  - type: MobThresholds
    thresholds:
      0: Alive
      100: Critical
      200: Dead
""";

    [Test]
    public async Task EngineerAndMedicPerformRealWorkOrderInteractions()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var adapters = entities.System<LuaMWorkerBehaviorAdapterSystem>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var damage = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid engineer = default;
        EntityUid medic = default;
        EntityUid repairTarget = default;
        EntityUid patient = default;
        EntityUid board = default;
        uint repairOrderId = default;
        uint treatmentOrderId = default;

        await server.WaitAssertion(() =>
        {
            board = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");

            engineer = entities.SpawnEntity("LuaMWorkerEngineer", map.MapCoords);
            repairTarget = entities.SpawnEntity("MobFireBot", map.MapCoords);
            var repairTool = entities.SpawnEntity("LuaMWorkerTestRepairTool", map.MapCoords);
            Assert.That(hands.TryPickupAnyHand(engineer, repairTool), Is.True);
            var repairable = entities.GetComponent<RepairableComponent>(repairTarget);
            repairable.DoAfterDelay = 1;
            repairable.FuelCost = 0;
            Assert.That(damage.TryChangeDamage(
                repairTarget,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true), Is.Not.Null);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Repair,
                    Priority: 800,
                    Severity: 0.8f,
                    Target: repairTarget,
                    Destination: entities.GetComponent<TransformComponent>(repairTarget).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Repair },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out repairOrderId), Is.True);

            var engineerAdapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(engineer);
            engineerAdapter.WorkOrderBoard = board;
            engineerAdapter.BuiltInPerception = false;
            Assert.That(adapters.RefreshNow(engineer, engineerAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(engineer, out var repairDecision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engineerAdapter.ActiveWorkOrderId, Is.EqualTo(repairOrderId));
                Assert.That(repairDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.Repair));
                Assert.That(entities.GetComponent<HTNComponent>(engineer).RootTask.Task,
                    Is.EqualTo("LuaMBehaviorNavigateDecisionCompound"));
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(engineer), Is.True);
            });

            medic = entities.SpawnEntity("LuaMWorkerMedic", Offset(map.MapCoords, 0f, 3f));
            patient = entities.SpawnEntity("LuaMWorkerTestPatient", Offset(map.MapCoords, 0f, 3f));
            var medicine = entities.SpawnEntity("LuaMWorkerTestMedicine", Offset(map.MapCoords, 0f, 3f));
            Assert.That(hands.TryPickupAnyHand(medic, medicine), Is.True);
            Assert.That(damage.TryChangeDamage(
                patient,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true), Is.Not.Null);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Treat,
                    Priority: 900,
                    Severity: 0.9f,
                    Target: patient,
                    Destination: entities.GetComponent<TransformComponent>(patient).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Medical },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out treatmentOrderId), Is.True);

            var medicAdapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(medic);
            medicAdapter.WorkOrderBoard = board;
            medicAdapter.BuiltInPerception = false;
            Assert.That(adapters.RefreshNow(medic, medicAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(medic, out var treatmentDecision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(medicAdapter.ActiveWorkOrderId, Is.EqualTo(treatmentOrderId));
                Assert.That(treatmentDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.Treat));
                Assert.That(entities.GetComponent<HTNComponent>(medic).RootTask.Task,
                    Is.EqualTo("LuaMBehaviorNavigateDecisionCompound"));
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(medic), Is.True);
            });
        });

        await pair.RunSeconds(1.1f);

        await server.WaitAssertion(() =>
        {
            var engineerAdapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(engineer);
            var medicAdapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(medic);
            Assert.That(adapters.RefreshNow(engineer, engineerAdapter, force: true), Is.True);
            Assert.That(adapters.RefreshNow(medic, medicAdapter, force: true), Is.True);

            var boardComponent = entities.GetComponent<LuaMWorkOrderBoardComponent>(board);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<DamageableComponent>(repairTarget).TotalDamage,
                    Is.EqualTo(FixedPoint2.Zero));
                Assert.That(entities.GetComponent<DamageableComponent>(patient).TotalDamage,
                    Is.EqualTo(FixedPoint2.Zero));
                Assert.That(
                    boardComponent.Orders.Single(order => order.Id == repairOrderId).State,
                    Is.EqualTo(LuaMWorkOrderState.Completed));
                Assert.That(
                    boardComponent.Orders.Single(order => order.Id == treatmentOrderId).State,
                    Is.EqualTo(LuaMWorkOrderState.Completed));
                Assert.That(engineerAdapter.ActiveWorkOrderId, Is.Zero);
                Assert.That(medicAdapter.ActiveWorkOrderId, Is.Zero);
                Assert.That(entities.GetComponent<HTNComponent>(engineer).RootTask.Task,
                    Is.EqualTo("IdleCompound"));
                Assert.That(entities.GetComponent<HTNComponent>(medic).RootTask.Task,
                    Is.EqualTo("IdleCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingToolReleasesOrderWithBackoff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var adapters = entities.System<LuaMWorkerBehaviorAdapterSystem>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var damage = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var board = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            var engineer = entities.SpawnEntity("LuaMWorkerEngineer", map.MapCoords);
            var target = entities.SpawnEntity("MobFireBot", map.MapCoords);
            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");
            Assert.That(damage.TryChangeDamage(
                target,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true), Is.Not.Null);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Repair,
                    Priority: 800,
                    Severity: 0.8f,
                    Target: target,
                    Destination: entities.GetComponent<TransformComponent>(target).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Repair },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out var orderId), Is.True);

            var adapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(engineer);
            adapter.WorkOrderBoard = board;
            adapter.BuiltInPerception = false;
            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.True);

            var order = entities.GetComponent<LuaMWorkOrderBoardComponent>(board).Orders.Single();
            var attempt = order.Attempts.Single();
            Assert.Multiple(() =>
            {
                Assert.That(order.Id, Is.EqualTo(orderId));
                Assert.That(order.State, Is.EqualTo(LuaMWorkOrderState.Open));
                Assert.That(order.Leases, Is.Empty);
                Assert.That(order.Failures, Is.EqualTo(1));
                Assert.That(order.LastFailure, Is.EqualTo("no compatible repair tool in hands"));
                Assert.That(attempt.Agent, Is.EqualTo(engineer));
                Assert.That(attempt.Failures, Is.EqualTo(1));
                Assert.That(attempt.RetryAt, Is.GreaterThan(timing.CurTime));
                Assert.That(adapter.ActiveWorkOrderId, Is.Zero);
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(engineer), Is.False);
                Assert.That(entities.GetComponent<HTNComponent>(engineer).RootTask.Task,
                    Is.EqualTo("IdleCompound"));
            });

            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.True);
            Assert.That(order.Attempts.Single().Failures, Is.EqualTo(1),
                "the same worker must not bypass its retry backoff");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DirectPlayerControlPreventsClaimAndExecution()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var adapters = entities.System<LuaMWorkerBehaviorAdapterSystem>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var damage = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var board = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            var engineer = entities.SpawnEntity("LuaMWorkerEngineer", map.MapCoords);
            var target = entities.SpawnEntity("MobFireBot", map.MapCoords);
            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");
            Assert.That(damage.TryChangeDamage(
                target,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true), Is.Not.Null);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Repair,
                    Priority: 800,
                    Severity: 0.8f,
                    Target: target,
                    Destination: entities.GetComponent<TransformComponent>(target).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Repair },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out _), Is.True);

            var adapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(engineer);
            adapter.WorkOrderBoard = board;
            adapter.BuiltInPerception = false;
            entities.EnsureComponent<ActorComponent>(engineer);

            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.False);
            var order = entities.GetComponent<LuaMWorkOrderBoardComponent>(board).Orders.Single();
            Assert.Multiple(() =>
            {
                Assert.That(order.State, Is.EqualTo(LuaMWorkOrderState.Open));
                Assert.That(order.Leases, Is.Empty);
                Assert.That(order.Attempts, Is.Empty);
                Assert.That(adapter.ActiveWorkOrderId, Is.Zero);
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(engineer), Is.False);
                Assert.That(entities.GetComponent<DamageableComponent>(target).TotalDamage,
                    Is.GreaterThan(FixedPoint2.Zero));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SafetyDecisionPausesWorkWithoutLosingLease()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var adapters = entities.System<LuaMWorkerBehaviorAdapterSystem>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var damage = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var board = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            var engineer = entities.SpawnEntity("LuaMWorkerEngineer", map.MapCoords);
            var target = entities.SpawnEntity("MobFireBot", map.MapCoords);
            var hostile = entities.SpawnEntity("MobFireBot", Offset(map.MapCoords, 2f, 0f));
            var repairTool = entities.SpawnEntity("LuaMWorkerTestRepairTool", map.MapCoords);
            Assert.That(hands.TryPickupAnyHand(engineer, repairTool), Is.True);
            var repairable = entities.GetComponent<RepairableComponent>(target);
            repairable.DoAfterDelay = 1;
            repairable.FuelCost = 0;
            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");
            Assert.That(damage.TryChangeDamage(
                target,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true), Is.Not.Null);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Repair,
                    Priority: 800,
                    Severity: 0.8f,
                    Target: target,
                    Destination: entities.GetComponent<TransformComponent>(target).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Repair },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out var orderId), Is.True);

            var adapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(engineer);
            adapter.WorkOrderBoard = board;
            adapter.BuiltInPerception = false;
            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.True);
            Assert.That(entities.HasComponent<ActiveDoAfterComponent>(engineer), Is.True);

            var order = entities.GetComponent<LuaMWorkOrderBoardComponent>(board).Orders.Single();
            var leaseBefore = order.Leases.Single().ExpiresAt;
            Assert.That(behavior.ReportObservation(
                engineer,
                LuaMBehaviorStimulus.HostileThreat,
                severity: 1f,
                target: hostile,
                ttl: TimeSpan.FromSeconds(10),
                source: "worker-safety-test",
                evaluateNow: true), Is.True);
            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(engineer, out var decision), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Intent, Is.EqualTo(LuaMBehaviorIntent.Flee));
                Assert.That(decision.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(adapter.ActiveWorkOrderId, Is.EqualTo(orderId));
                Assert.That(adapter.Status, Does.Contain("paused by Flee"));
                Assert.That(order.State, Is.EqualTo(LuaMWorkOrderState.Claimed));
                Assert.That(order.Leases.Single().Agent, Is.EqualTo(engineer));
                Assert.That(order.Leases.Single().ExpiresAt, Is.GreaterThanOrEqualTo(leaseBefore));
                Assert.That(order.Failures, Is.Zero);
                Assert.That(order.Attempts, Is.Empty);
                Assert.That(
                    entities.GetComponent<DoAfterComponent>(engineer).DoAfters.Values.All(doAfter => doAfter.Cancelled),
                    Is.True);
                Assert.That(entities.GetComponent<DamageableComponent>(target).TotalDamage,
                    Is.GreaterThan(FixedPoint2.Zero));
                Assert.That(entities.GetComponent<HTNComponent>(engineer).RootTask.Task,
                    Is.EqualTo("LuaMBehaviorRetreatCompound"));
            });
        });

        await pair.RunTicksSync(1);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StalledRouteReleasesOrderWithBackoff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var adapters = entities.System<LuaMWorkerBehaviorAdapterSystem>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var damage = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var board = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            var engineer = entities.SpawnEntity("LuaMWorkerEngineer", map.MapCoords);
            var target = entities.SpawnEntity("MobFireBot", Offset(map.MapCoords, 20f, 0f));
            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");
            Assert.That(damage.TryChangeDamage(
                target,
                new DamageSpecifier(blunt, FixedPoint2.New(10)),
                ignoreResistances: true), Is.Not.Null);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Repair,
                    Priority: 800,
                    Severity: 0.8f,
                    Target: target,
                    Destination: entities.GetComponent<TransformComponent>(target).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Repair },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out var orderId), Is.True);

            var adapter = entities.GetComponent<LuaMWorkerBehaviorAdapterComponent>(engineer);
            adapter.WorkOrderBoard = board;
            adapter.BuiltInPerception = false;
            adapter.StallSeconds = 3f;
            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.True);
            Assert.That(adapter.ActiveWorkOrderId, Is.EqualTo(orderId));

            adapter.LastProgressAt = timing.CurTime - TimeSpan.FromSeconds(4);
            Assert.That(adapters.RefreshNow(engineer, adapter, force: true), Is.True);

            var order = entities.GetComponent<LuaMWorkOrderBoardComponent>(board).Orders.Single();
            var attempt = order.Attempts.Single();
            Assert.Multiple(() =>
            {
                Assert.That(order.State, Is.EqualTo(LuaMWorkOrderState.Open));
                Assert.That(order.Leases, Is.Empty);
                Assert.That(order.LastFailure, Is.EqualTo("route stalled"));
                Assert.That(order.Failures, Is.EqualTo(1));
                Assert.That(attempt.Agent, Is.EqualTo(engineer));
                Assert.That(attempt.Failures, Is.EqualTo(1));
                Assert.That(attempt.RetryAt, Is.GreaterThan(timing.CurTime));
                Assert.That(adapter.ActiveWorkOrderId, Is.Zero);
                Assert.That(entities.GetComponent<HTNComponent>(engineer).RootTask.Task,
                    Is.EqualTo("IdleCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static MapCoordinates Offset(MapCoordinates origin, float x, float y)
    {
        return new MapCoordinates(origin.Position + new Vector2(x, y), origin.MapId);
    }
}
