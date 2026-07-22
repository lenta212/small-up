using System;
using System.Linq;
using System.Numerics;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.Power.EntitySystems;
using Content.Server._LuaM.AI;
using Content.Shared.Atmos.Components;
using Content.Shared.Power.Components;
using Content.Shared._LuaM.AI;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMServiceBotBehaviorAdapterSystem))]
[TestOf(typeof(LuaMStationaryTurretBehaviorAdapterSystem))]
public sealed class LuaMStandardBehaviorAdapterRuntimeTest
{
    private const string TestSource = "standard-behavior-test";

    [Test]
    public async Task ServiceBotsDriveFireCleaningAndSafetyPlans()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var adapters = entities.System<LuaMServiceBotBehaviorAdapterSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var firebot = entities.SpawnEntity("MobFireBot", map.MapCoords);
            var burning = entities.SpawnEntity("MobHuman", Offset(map.MapCoords, 2f, 0f));
            entities.GetComponent<FlammableComponent>(burning).OnFire = true;
            var fireAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(firebot);

            Assert.That(adapters.RefreshNow(firebot, fireAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(firebot, out var fireDecision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(fireDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.ExtinguishFire));
                Assert.That(fireDecision.Target, Is.EqualTo(burning));
                Assert.That(
                    entities.GetComponent<HTNComponent>(firebot).RootTask.Task,
                    Is.EqualTo("FirebotCompound"));
            });

            var threat = entities.SpawnEntity(null, Offset(map.MapCoords, 1f, 0f));
            Assert.That(
                behavior.ReportObservation(
                    firebot,
                    LuaMBehaviorStimulus.HostileThreat,
                    1f,
                    target: threat,
                    ttl: TimeSpan.FromMinutes(1),
                    source: TestSource,
                    evaluateNow: true),
                Is.True);
            Assert.That(behavior.GetDecision(firebot, out var safetyDecision), Is.True);
            var fireHtn = entities.GetComponent<HTNComponent>(firebot);
            Assert.Multiple(() =>
            {
                Assert.That(safetyDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.Flee));
                Assert.That(safetyDecision.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(fireHtn.RootTask.Task, Is.EqualTo("LuaMBehaviorRetreatCompound"));
                Assert.That(fireHtn.Blackboard.GetValue<EntityUid>("Target"), Is.EqualTo(threat));
            });

            var cleanbot = entities.SpawnEntity("MobCleanBot", Offset(map.MapCoords, 0f, 3f));
            var puddle = entities.SpawnEntity("PuddleVomit", Offset(map.MapCoords, 1f, 3f));
            var cleanAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(cleanbot);
            Assert.That(adapters.RefreshNow(cleanbot, cleanAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(cleanbot, out var cleanDecision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(cleanDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.Clean));
                Assert.That(cleanDecision.Target, Is.EqualTo(puddle));
                Assert.That(
                    entities.GetComponent<HTNComponent>(cleanbot).RootTask.Task,
                    Is.EqualTo("CleanbotCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ServiceRoleBotsPreserveRolePlansAndExecuteCompatibleWorkOrders()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var adapters = entities.System<LuaMServiceBotBehaviorAdapterSystem>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var honkbot = entities.SpawnEntity("MobHonkBot", map.MapCoords);
            var honkAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(honkbot);
            Assert.That(adapters.RefreshNow(honkbot, honkAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(honkbot, out var honkDecision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(honkDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.Patrol));
                Assert.That(entities.GetComponent<HTNComponent>(honkbot).RootTask.Task,
                    Is.EqualTo("HonkbotCompound"));
            });

            var mimebot = entities.SpawnEntity("MobMimeBot", Offset(map.MapCoords, 0f, 2f));
            var mimeAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(mimebot);
            Assert.That(mimeAdapter.Role, Is.EqualTo(LuaMServiceBotRole.Entertainment));
            Assert.That(adapters.RefreshNow(mimebot, mimeAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(mimebot, out var mimeDecision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(mimeDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.Patrol));
                Assert.That(entities.GetComponent<HTNComponent>(mimebot).RootTask.Task,
                    Is.EqualTo("IdleCompound"));
            });

            var hoverTaxi = entities.SpawnEntity("MobHoverTaxiBot", Offset(map.MapCoords, 0f, 4f));
            var hoverAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(hoverTaxi);
            Assert.Multiple(() =>
            {
                Assert.That(hoverAdapter.Role, Is.EqualTo(LuaMServiceBotRole.Taxi));
                Assert.That(entities.HasComponent<HTNComponent>(hoverTaxi), Is.True);
            });

            var board = entities.SpawnEntity(null, map.MapCoords);
            var boardComponent = entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            var supplyDestination = entities.SpawnEntity(null, Offset(map.MapCoords, 6f, 0f));
            var taxiDestination = entities.SpawnEntity(null, Offset(map.MapCoords, 0f, 6f));

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Navigate,
                    Priority: 900,
                    Severity: 0.8f,
                    Target: taxiDestination,
                    Destination: entities.GetComponent<TransformComponent>(taxiDestination).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Carry },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out var taxiOrderId), Is.True);
            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Deliver,
                    Priority: 700,
                    Severity: 0.7f,
                    Target: supplyDestination,
                    Destination: entities.GetComponent<TransformComponent>(supplyDestination).Coordinates,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Haul },
                    Lifetime: TimeSpan.FromMinutes(1)),
                out var supplyOrderId), Is.True);

            var supplybot = entities.SpawnEntity("MobSupplyBot", map.MapCoords);
            var supplyAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(supplybot);
            supplyAdapter.WorkOrderBoard = board;
            Assert.That(adapters.RefreshNow(supplybot, supplyAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(supplybot, out var delivery), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(supplyAdapter.ActiveWorkOrderId, Is.EqualTo(supplyOrderId));
                Assert.That(delivery.Intent, Is.EqualTo(LuaMBehaviorIntent.Deliver));
                Assert.That(delivery.Target, Is.EqualTo(supplyDestination));
                Assert.That(entities.GetComponent<HTNComponent>(supplybot).RootTask.Task,
                    Is.EqualTo("LuaMBehaviorNavigateDecisionCompound"));
            });

            var taxibot = entities.SpawnEntity("MobTaxiBot", Offset(map.MapCoords, 2f, 0f));
            var taxiAdapter = entities.GetComponent<LuaMServiceBotBehaviorAdapterComponent>(taxibot);
            taxiAdapter.WorkOrderBoard = board;
            Assert.That(adapters.RefreshNow(taxibot, taxiAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(taxibot, out var navigation), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(taxiAdapter.ActiveWorkOrderId, Is.EqualTo(taxiOrderId));
                Assert.That(navigation.Intent, Is.EqualTo(LuaMBehaviorIntent.ExecuteWorkOrder));
                Assert.That(navigation.Target, Is.EqualTo(taxiDestination));
                Assert.That(entities.GetComponent<HTNComponent>(taxibot).RootTask.Task,
                    Is.EqualTo("LuaMBehaviorNavigateDecisionCompound"));
            });

            transform.SetMapCoordinates(
                supplybot,
                transform.ToMapCoordinates(entities.GetComponent<TransformComponent>(supplyDestination).Coordinates));
            Assert.That(adapters.RefreshNow(supplybot, supplyAdapter, force: true), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(supplyAdapter.ActiveWorkOrderId, Is.Zero);
                Assert.That(
                    boardComponent.Orders.Single(order => order.Id == supplyOrderId).State,
                    Is.EqualTo(LuaMWorkOrderState.Completed));
                Assert.That(entities.GetComponent<HTNComponent>(supplybot).RootTask.Task,
                    Is.EqualTo("IdleCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TurretUsesFactionTargetAndStopsWhenWeaponIsUnavailable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var adapters = entities.System<LuaMStationaryTurretBehaviorAdapterSystem>();
        var batteries = entities.System<BatterySystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var turret = entities.SpawnEntity("WeaponTurretSyndicate", map.MapCoords);
            var hostile = entities.SpawnEntity("MobCivilian", Offset(map.MapCoords, 3f, 0f));
            var adapter = entities.GetComponent<LuaMStationaryTurretBehaviorAdapterComponent>(turret);

            Assert.That(adapters.RefreshNow(turret, adapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(turret, out var defense), Is.True);
            var htn = entities.GetComponent<HTNComponent>(turret);
            Assert.Multiple(() =>
            {
                Assert.That(defense.Intent, Is.EqualTo(LuaMBehaviorIntent.DefendSelf));
                Assert.That(defense.Target, Is.EqualTo(hostile));
                Assert.That(htn.RootTask.Task, Is.EqualTo("LuaMBehaviorTurretDecisionCompound"));
                Assert.That(htn.Blackboard.GetValue<EntityUid>("Target"), Is.EqualTo(hostile));
            });

            var battery = entities.GetComponent<BatteryComponent>(turret);
            batteries.SetCharge(turret, 0f, battery);
            Assert.That(adapters.RefreshNow(turret, adapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(turret, out var unavailable), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(unavailable.Intent, Is.EqualTo(LuaMBehaviorIntent.RequestAssistance));
                Assert.That(unavailable.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(htn.RootTask.Task, Is.EqualTo("IdleSpinCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HumanoidAndAnimalSwitchRealHtnPlansAndRestoreNormalWork()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var humanoid = entities.SpawnEntity("MobSyndicateFootsoldier", map.MapCoords);
            var humanoidThreat = entities.SpawnEntity(null, Offset(map.MapCoords, 2f, 0f));
            var humanoidAgent = entities.GetComponent<LuaMBehaviorAgentComponent>(humanoid);
            humanoidAgent.BuiltInPerception = false;
            var humanoidHtn = entities.GetComponent<HTNComponent>(humanoid);

            Report(behavior, humanoid, LuaMBehaviorStimulus.HostileThreat, 0.8f, humanoidThreat);
            Assert.That(behavior.EvaluateNow(humanoid, out var defense), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(defense.Intent, Is.EqualTo(LuaMBehaviorIntent.DefendSelf));
                Assert.That(humanoidHtn.RootTask.Task, Is.EqualTo("SimpleHumanoidHostileCompound"));
                Assert.That(humanoidHtn.Blackboard.GetValue<EntityUid>("Target"), Is.EqualTo(humanoidThreat));
            });

            Report(behavior, humanoid, LuaMBehaviorStimulus.Overwhelmed, 1f, humanoidThreat);
            Assert.That(behavior.EvaluateNow(humanoid, out var retreat), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(retreat.Intent, Is.EqualTo(LuaMBehaviorIntent.Retreat));
                Assert.That(humanoidHtn.RootTask.Task, Is.EqualTo("LuaMBehaviorRetreatCompound"));
            });

            humanoidAgent.Decision.CommitUntil = TimeSpan.Zero;
            behavior.ClearObservations(humanoid, source: TestSource);
            Assert.That(behavior.EvaluateNow(humanoid, out var resumed), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(resumed.Intent, Is.EqualTo(LuaMBehaviorIntent.Standby));
                Assert.That(humanoidHtn.RootTask.Task, Is.EqualTo("SimpleHumanoidHostileCompound"));
                Assert.That(humanoidHtn.Blackboard.ContainsKey("Target"), Is.False);
            });

            var animal = entities.SpawnEntity("MobChicken", Offset(map.MapCoords, 0f, 4f));
            var animalThreat = entities.SpawnEntity(null, Offset(map.MapCoords, 1f, 4f));
            var animalAgent = entities.GetComponent<LuaMBehaviorAgentComponent>(animal);
            animalAgent.BuiltInPerception = false;
            var animalHtn = entities.GetComponent<HTNComponent>(animal);

            Report(behavior, animal, LuaMBehaviorStimulus.HostileThreat, 0.8f, animalThreat);
            Assert.That(behavior.EvaluateNow(animal, out var animalDefense), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(animalDefense.Intent, Is.EqualTo(LuaMBehaviorIntent.DefendSelf));
                Assert.That(animalHtn.RootTask.Task, Is.EqualTo("LuaMBehaviorMeleeDecisionCompound"));
                Assert.That(animalHtn.Blackboard.GetValue<EntityUid>("Target"), Is.EqualTo(animalThreat));
            });

            Report(behavior, animal, LuaMBehaviorStimulus.Overwhelmed, 1f, animalThreat);
            Assert.That(behavior.EvaluateNow(animal, out var animalRetreat), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(animalRetreat.Intent, Is.EqualTo(LuaMBehaviorIntent.Retreat));
                Assert.That(animalHtn.RootTask.Task, Is.EqualTo("LuaMBehaviorRetreatCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void Report(
        LuaMBehaviorSystem behavior,
        EntityUid actor,
        LuaMBehaviorStimulus stimulus,
        float severity,
        EntityUid target)
    {
        Assert.That(
            behavior.ReportObservation(
                actor,
                stimulus,
                severity,
                target: target,
                ttl: TimeSpan.FromMinutes(1),
                source: TestSource),
            Is.True);
    }

    private static MapCoordinates Offset(MapCoordinates origin, float x, float y)
    {
        return new MapCoordinates(origin.Position + new Vector2(x, y), origin.MapId);
    }
}
