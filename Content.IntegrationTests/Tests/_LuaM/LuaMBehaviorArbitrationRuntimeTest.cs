using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Server._LuaM.AI;
using Content.Server._LuaM.Sector;
using Content.Shared.CCVar;
using Content.Shared._LuaM.AI;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMBehaviorSystem))]
public sealed class LuaMBehaviorArbitrationRuntimeTest
{
    [Test]
    public async Task RoleMatrixSelectsSafetyAndCapabilityCorrectIntent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var target = entities.SpawnEntity(null, map.MapCoords);

            AssertDecision(
                "LuaMHumanoidWorkerBehavior",
                LuaMBehaviorIntent.FollowOrder,
                (LuaMBehaviorStimulus.DirectOrder, 0.8f));
            AssertDecision(
                "LuaMHumanoidWorkerBehavior",
                LuaMBehaviorIntent.Flee,
                (LuaMBehaviorStimulus.DirectOrder, 1f),
                (LuaMBehaviorStimulus.SelfOnFire, 1f));
            AssertDecision(
                "LuaMEngineerBehavior",
                LuaMBehaviorIntent.ExtinguishSelf,
                (LuaMBehaviorStimulus.SelfOnFire, 1f));
            AssertDecision(
                "LuaMHumanoidWorkerBehavior",
                LuaMBehaviorIntent.SeekSafeAtmosphere,
                (LuaMBehaviorStimulus.UnsafeAtmosphere, 0.9f));
            AssertDecision(
                "LuaMServiceRobotBehavior",
                LuaMBehaviorIntent.Patrol,
                (LuaMBehaviorStimulus.UnsafeAtmosphere, 1f),
                (LuaMBehaviorStimulus.PatrolDue, 0.7f));
            AssertDecision(
                "LuaMSecurityBehavior",
                LuaMBehaviorIntent.DefendSelf,
                (LuaMBehaviorStimulus.HostileThreat, 0.6f));
            AssertDecision(
                "LuaMHumanoidWorkerBehavior",
                LuaMBehaviorIntent.Flee,
                (LuaMBehaviorStimulus.HostileThreat, 0.6f));
            AssertDecision(
                "LuaMHumanoidWorkerBehavior",
                LuaMBehaviorIntent.RequestAssistance,
                (LuaMBehaviorStimulus.HostileThreat, 0.6f),
                (LuaMBehaviorStimulus.Immobilized, 1f));
            AssertDecision(
                "LuaMSecurityBehavior",
                LuaMBehaviorIntent.DefendSelf,
                (LuaMBehaviorStimulus.HostileThreat, 0.6f),
                (LuaMBehaviorStimulus.Immobilized, 1f));
            AssertDecision(
                "LuaMSecurityBehavior",
                LuaMBehaviorIntent.Retreat,
                (LuaMBehaviorStimulus.HostileThreat, 0.8f),
                (LuaMBehaviorStimulus.Overwhelmed, 0.9f));
            AssertDecision(
                "LuaMCombatDroneBehavior",
                LuaMBehaviorIntent.TakeCover,
                (LuaMBehaviorStimulus.HostileThreat, 0.8f),
                (LuaMBehaviorStimulus.LowAmmunition, 1f));
            AssertDecision(
                "LuaMSecurityBehavior",
                LuaMBehaviorIntent.DefendSelf,
                (LuaMBehaviorStimulus.HostileThreat, 0.8f),
                (LuaMBehaviorStimulus.LowAmmunition, 1f));
            AssertDecision(
                "LuaMMedicBehavior",
                LuaMBehaviorIntent.Revive,
                (LuaMBehaviorStimulus.RecoverableDeadAlly, 1f));
            AssertDecision(
                "LuaMEngineerBehavior",
                LuaMBehaviorIntent.Repair,
                (LuaMBehaviorStimulus.RecoverableDeadAlly, 1f),
                (LuaMBehaviorStimulus.RepairNeeded, 0.6f));
            AssertDecision(
                "LuaMEngineerBehavior",
                LuaMBehaviorIntent.ExtinguishFire,
                (LuaMBehaviorStimulus.LocalFire, 0.8f));
            AssertDecision(
                "LuaMEngineerBehavior",
                LuaMBehaviorIntent.RestorePower,
                (LuaMBehaviorStimulus.PowerFailure, 0.8f));
            AssertDecision(
                "LuaMEngineerBehavior",
                LuaMBehaviorIntent.Repair,
                (LuaMBehaviorStimulus.StructuralFailure, 0.8f));
            AssertDecision(
                "LuaMHumanoidWorkerBehavior",
                LuaMBehaviorIntent.EvacuateHazard,
                (LuaMBehaviorStimulus.ExplosionRisk, 1f));
            AssertDecision(
                "LuaMMedicBehavior",
                LuaMBehaviorIntent.Treat,
                (LuaMBehaviorStimulus.DirectOrder, 1f),
                (LuaMBehaviorStimulus.AllyCritical, 0.9f));
            AssertDecision(
                "LuaMCombatShipBehavior",
                LuaMBehaviorIntent.EngageShip,
                (LuaMBehaviorStimulus.HostileThreat, 0.7f));
            AssertDecision(
                "LuaMCombatShipBehavior",
                LuaMBehaviorIntent.DisengageShip,
                (LuaMBehaviorStimulus.HostileThreat, 0.7f),
                (LuaMBehaviorStimulus.LowPower, 0.8f));
            AssertDecision(
                "LuaMCombatShipBehavior",
                LuaMBehaviorIntent.DisengageShip,
                (LuaMBehaviorStimulus.HostileThreat, 0.7f),
                (LuaMBehaviorStimulus.LowAmmunition, 0.9f));
            AssertDecision(
                "LuaMCivilianShipBehavior",
                LuaMBehaviorIntent.DisengageShip,
                (LuaMBehaviorStimulus.HostileThreat, 0.7f));
            AssertDecision(
                "LuaMStationaryTurretBehavior",
                LuaMBehaviorIntent.DefendSelf,
                (LuaMBehaviorStimulus.HostileThreat, 0.7f));
            AssertDecision(
                "LuaMMiningDroneBehavior",
                LuaMBehaviorIntent.ReturnCargo,
                (LuaMBehaviorStimulus.MineableResource, 1f),
                (LuaMBehaviorStimulus.CargoFull, 0.8f));
            AssertDecision(
                "LuaMServiceRobotBehavior",
                LuaMBehaviorIntent.Recharge,
                (LuaMBehaviorStimulus.LowPower, 0.9f));
            AssertDecision(
                "LuaMCleanBotBehavior",
                LuaMBehaviorIntent.Clean,
                (LuaMBehaviorStimulus.CleaningNeeded, 0.8f));
            AssertDecision(
                "LuaMEngineerBehavior",
                LuaMBehaviorIntent.ReplanRoute,
                (LuaMBehaviorStimulus.NoPath, 0.8f));
            AssertDecision(
                "LuaMAnimalBehavior",
                LuaMBehaviorIntent.Retreat,
                (LuaMBehaviorStimulus.HostileThreat, 0.8f),
                (LuaMBehaviorStimulus.Overwhelmed, 0.8f));

            void AssertDecision(
                string profileId,
                LuaMBehaviorIntent expected,
                params (LuaMBehaviorStimulus Stimulus, float Severity)[] inputs)
            {
                var profile = prototypes.Index<LuaMBehaviorProfilePrototype>(profileId);
                var observations = inputs.Select(input => new LuaMBehaviorObservation
                {
                    Stimulus = input.Stimulus,
                    Severity = input.Severity,
                    Confidence = 1f,
                    Target = NeedsTarget(input.Stimulus) ? target : null,
                    ObservedAt = timing.CurTime,
                    ExpiresAt = timing.CurTime + TimeSpan.FromMinutes(1),
                    Source = "scenario-matrix",
                }).ToArray();

                var decision = LuaMBehaviorArbiter.Decide(profile, observations, null, timing.CurTime);
                Assert.That(
                    decision.Intent,
                    Is.EqualTo(expected),
                    $"profile={profileId}; inputs={string.Join(',', inputs.Select(input => input.Stimulus))}; " +
                    $"actual={decision.Intent}; reason={decision.Reason}");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TargetHysteresisPreventsThrashingButAllowsUrgentPreemption()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var firstPatient = entities.SpawnEntity(null, map.MapCoords);
            var secondPatient = entities.SpawnEntity(null, map.MapCoords);
            var profile = prototypes.Index<LuaMBehaviorProfilePrototype>("LuaMMedicBehavior");
            var observations = new List<LuaMBehaviorObservation>
            {
                Observation(LuaMBehaviorStimulus.AllyCritical, 0.80f, firstPatient),
                Observation(LuaMBehaviorStimulus.AllyCritical, 0.70f, secondPatient),
            };

            var first = LuaMBehaviorArbiter.Decide(profile, observations, null, timing.CurTime);
            Assert.Multiple(() =>
            {
                Assert.That(first.Intent, Is.EqualTo(LuaMBehaviorIntent.Treat));
                Assert.That(first.Target, Is.EqualTo(firstPatient));
            });

            observations[1].Severity = 0.84f;
            var stable = LuaMBehaviorArbiter.Decide(
                profile,
                observations,
                first,
                timing.CurTime + TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(stable.Target, Is.EqualTo(firstPatient));
                Assert.That(stable.Generation, Is.EqualTo(first.Generation));
            });

            observations[0].Severity = 0.10f;
            observations[1].Severity = 1f;
            var switched = LuaMBehaviorArbiter.Decide(
                profile,
                observations,
                stable,
                timing.CurTime + TimeSpan.FromSeconds(4));
            Assert.Multiple(() =>
            {
                Assert.That(switched.Target, Is.EqualTo(secondPatient));
                Assert.That(switched.Generation, Is.GreaterThan(stable.Generation));
            });

            observations.Add(Observation(LuaMBehaviorStimulus.SelfOnFire, 1f, null));
            var emergency = LuaMBehaviorArbiter.Decide(
                profile,
                observations,
                switched,
                timing.CurTime + TimeSpan.FromSeconds(4.1));
            Assert.Multiple(() =>
            {
                Assert.That(emergency.Intent, Is.EqualTo(LuaMBehaviorIntent.Flee));
                Assert.That(emergency.Tier, Is.EqualTo(LuaMBehaviorTier.Critical));
                Assert.That(emergency.Generation, Is.GreaterThan(switched.Generation));
            });

            LuaMBehaviorObservation Observation(
                LuaMBehaviorStimulus stimulus,
                float severity,
                EntityUid? target)
            {
                return new LuaMBehaviorObservation
                {
                    Stimulus = stimulus,
                    Severity = severity,
                    Confidence = 1f,
                    Target = target,
                    ObservedAt = timing.CurTime,
                    ExpiresAt = timing.CurTime + TimeSpan.FromMinutes(1),
                    Source = "hysteresis",
                };
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WorkOrdersRespectCapabilitiesExclusiveLeasesAndRecovery()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var workOrders = entities.System<LuaMWorkOrderSystem>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var map = await pair.CreateTestMap();

        EntityUid board = default;
        EntityUid firstEngineer = default;
        EntityUid secondEngineer = default;
        EntityUid medic = default;
        EntityUid repairTarget = default;
        EntityUid patient = default;
        uint repairOrder = 0;
        uint medicalOrder = 0;

        await server.WaitAssertion(() =>
        {
            board = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<LuaMWorkOrderBoardComponent>(board);
            firstEngineer = SpawnAgent("LuaMEngineerBehavior");
            secondEngineer = SpawnAgent("LuaMEngineerBehavior");
            medic = SpawnAgent("LuaMMedicBehavior");
            repairTarget = entities.SpawnEntity(null, map.MapCoords);
            patient = entities.SpawnEntity(null, map.MapCoords);

            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Repair,
                    Priority: 500,
                    Severity: 0.6f,
                    Target: repairTarget,
                    Destination: null,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Repair },
                    Lifetime: TimeSpan.FromMinutes(2)),
                out repairOrder), Is.True);
            Assert.That(workOrders.Publish(
                board,
                new LuaMWorkOrderRequest(
                    LuaMBehaviorIntent.Treat,
                    Priority: 900,
                    Severity: 0.9f,
                    Target: patient,
                    Destination: null,
                    RequiredCapabilities: new[] { LuaMBehaviorCapability.Medical },
                    Lifetime: TimeSpan.FromMinutes(2)),
                out medicalOrder), Is.True);

            Assert.That(
                workOrders.TryClaimBest(board, firstEngineer, TimeSpan.FromSeconds(30), out var engineerClaim),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engineerClaim?.Id, Is.EqualTo(repairOrder));
                Assert.That(behavior.GetDecision(firstEngineer, out var decision), Is.True);
                Assert.That(decision.Intent, Is.EqualTo(LuaMBehaviorIntent.Repair));
                Assert.That(decision.Target, Is.EqualTo(repairTarget));
            });

            Assert.That(
                workOrders.TryClaimBest(board, secondEngineer, TimeSpan.FromSeconds(30), out _),
                Is.False,
                "a single-assignee repair target was leased twice");

            Assert.That(workOrders.Release(
                board,
                repairOrder,
                firstEngineer,
                "route blocked"), Is.True);
            Assert.That(
                workOrders.TryClaimBest(board, firstEngineer, TimeSpan.FromSeconds(30), out _),
                Is.False,
                "an agent immediately reclaimed the work order it just failed");
            Assert.That(
                workOrders.TryClaimBest(board, secondEngineer, TimeSpan.FromSeconds(30), out var reassignedClaim),
                Is.True);
            Assert.That(reassignedClaim?.Id, Is.EqualTo(repairOrder));

            Assert.That(
                workOrders.TryClaimBest(board, medic, TimeSpan.FromSeconds(30), out var medicClaim),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(medicClaim?.Id, Is.EqualTo(medicalOrder));
                Assert.That(behavior.GetDecision(medic, out var decision), Is.True);
                Assert.That(decision.Intent, Is.EqualTo(LuaMBehaviorIntent.Treat));
                Assert.That(decision.Target, Is.EqualTo(patient));
            });

            var boardComponent = entities.GetComponent<LuaMWorkOrderBoardComponent>(board);
            boardComponent.Orders.Single(order => order.Id == repairOrder).Leases.Single().ExpiresAt =
                timing.CurTime - TimeSpan.FromSeconds(1);
            boardComponent.Orders.Single(order => order.Id == repairOrder).Attempts
                .Single(attempt => attempt.Agent == firstEngineer).RetryAt = timing.CurTime - TimeSpan.FromSeconds(1);
            Assert.That(
                workOrders.TryClaimBest(board, firstEngineer, TimeSpan.FromSeconds(30), out var recoveredClaim),
                Is.True);
            Assert.That(recoveredClaim?.Id, Is.EqualTo(repairOrder));
            Assert.That(workOrders.Complete(board, repairOrder, firstEngineer), Is.True);
            Assert.That(
                boardComponent.Orders.Single(order => order.Id == repairOrder).State,
                Is.EqualTo(LuaMWorkOrderState.Completed));

            EntityUid SpawnAgent(string profile)
            {
                var uid = entities.SpawnEntity(null, map.MapCoords);
                var component = entities.EnsureComponent<LuaMBehaviorAgentComponent>(uid);
                component.Profile = profile;
                component.BuiltInPerception = false;
                return uid;
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AdaptiveShipSwitchesRealHtnRootBetweenCombatAndDisengage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var shipBrain = entities.SpawnEntity("LuaMAdaptiveCombatShipAiCore", map.MapCoords);
            var target = entities.SpawnEntity(null, map.MapCoords);
            var agent = entities.GetComponent<LuaMBehaviorAgentComponent>(shipBrain);
            var htn = entities.GetComponent<HTNComponent>(shipBrain);
            agent.BuiltInPerception = false;

            Assert.That(behavior.ReportObservation(
                shipBrain,
                LuaMBehaviorStimulus.HostileThreat,
                0.8f,
                target: target,
                ttl: TimeSpan.FromMinutes(1),
                source: "ship-adapter",
                evaluateNow: true), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(agent.Decision.Intent, Is.EqualTo(LuaMBehaviorIntent.EngageShip));
                Assert.That(htn.RootTask.Task, Is.EqualTo("AttackerShuttleSmartCompound"));
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityUid>(
                        LuaMBehaviorSystem.TargetKey,
                        out var blackboardTarget,
                        entities) && blackboardTarget == target,
                    Is.True);
            });

            Assert.That(behavior.ReportObservation(
                shipBrain,
                LuaMBehaviorStimulus.LowPower,
                0.9f,
                ttl: TimeSpan.FromMinutes(1),
                source: "ship-adapter",
                evaluateNow: true), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(agent.Decision.Intent, Is.EqualTo(LuaMBehaviorIntent.DisengageShip));
                Assert.That(agent.Decision.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(htn.RootTask.Task, Is.EqualTo("LuaMShipDisengageCompound"));
            });

            Assert.That(behavior.ClearObservations(
                shipBrain,
                stimulus: LuaMBehaviorStimulus.LowPower,
                source: "ship-adapter"), Is.EqualTo(1));
            Assert.That(behavior.EvaluateNow(shipBrain, out var recovered), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(recovered.Intent, Is.EqualTo(LuaMBehaviorIntent.EngageShip));
                Assert.That(htn.RootTask.Task, Is.EqualTo("AttackerShuttleSmartCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DirectEvaluationCannotTakeControlFromPlayer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var shipBrain = entities.SpawnEntity("LuaMAdaptiveCombatShipAiCore", map.MapCoords);
            var target = entities.SpawnEntity(null, map.MapCoords);
            var agent = entities.GetComponent<LuaMBehaviorAgentComponent>(shipBrain);
            var htn = entities.GetComponent<HTNComponent>(shipBrain);
            agent.BuiltInPerception = false;

            Assert.That(behavior.ReportObservation(
                shipBrain,
                LuaMBehaviorStimulus.HostileThreat,
                0.8f,
                target: target,
                ttl: TimeSpan.FromMinutes(1),
                source: "manual-control-test",
                evaluateNow: true), Is.True);
            Assert.That(agent.Decision.Intent, Is.EqualTo(LuaMBehaviorIntent.EngageShip));
            var generation = agent.Decision.Generation;
            var rootTask = htn.RootTask.Task;

            entities.EnsureComponent<ActorComponent>(shipBrain);
            Assert.That(behavior.ReportObservation(
                shipBrain,
                LuaMBehaviorStimulus.LowPower,
                1f,
                ttl: TimeSpan.FromMinutes(1),
                source: "manual-control-test",
                evaluateNow: true), Is.True);
            Assert.That(behavior.EvaluateNow(shipBrain, out var suspended), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(suspended.Intent, Is.EqualTo(LuaMBehaviorIntent.EngageShip));
                Assert.That(agent.Decision.Generation, Is.EqualTo(generation));
                Assert.That(htn.RootTask.Task, Is.EqualTo(rootTask));
                Assert.That(agent.LastStatus, Does.Contain("player controlled"));
            });

            Assert.That(entities.RemoveComponent<ActorComponent>(shipBrain), Is.True);
            Assert.That(behavior.EvaluateNow(shipBrain, out var resumed), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(resumed.Intent, Is.EqualTo(LuaMBehaviorIntent.DisengageShip));
                Assert.That(htn.RootTask.Task, Is.EqualTo("LuaMShipDisengageCompound"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LogisticsShipCoreSwitchesBetweenDeliveryRouteFailureAndDisengage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = server.ResolveDependency<IMapManager>();
        var adapter = entities.System<LuaMAiLogisticsShipBehaviorAdapterSystem>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.LuaMAiPhysicalBaseEnabled, true));
        await server.WaitAssertion(() =>
        {
            var anchor = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(200f, 0f), map.MapId));
            var anchorComponent = entities.EnsureComponent<LuaMAiBaseAnchorComponent>(anchor);
            anchorComponent.BaseId = "behavior-test-base";

            var shipGrid = maps.CreateGridEntity(map.MapId).Owner;
            var ship = entities.EnsureComponent<LuaMAiLogisticsShipComponent>(shipGrid);
            ship.BaseId = anchorComponent.BaseId;
            ship.NextCycle = TimeSpan.FromDays(1);

            Assert.That(adapter.RefreshNow(shipGrid, ship, force: true), Is.True);
            Assert.That(ship.BehaviorCore, Is.Not.Null);
            var core = ship.BehaviorCore!.Value;
            var agent = entities.GetComponent<LuaMBehaviorAgentComponent>(core);
            agent.BuiltInPerception = false;
            var htn = entities.GetComponent<HTNComponent>(core);
            Assert.That(behavior.GetDecision(core, out var delivery), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(delivery.Intent, Is.EqualTo(LuaMBehaviorIntent.Deliver));
                Assert.That(delivery.Target, Is.EqualTo(anchor));
                Assert.That(htn.RootTask.Task, Is.EqualTo("LuaMShipNavigateBehaviorCompound"));
                Assert.That(ship.BehaviorState, Is.EqualTo("delivering"));
            });

            ship.RouteBlocked = true;
            Assert.That(adapter.RefreshNow(shipGrid, ship, force: true), Is.True);
            Assert.That(behavior.GetDecision(core, out var replanning), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(replanning.Intent, Is.EqualTo(LuaMBehaviorIntent.ReplanRoute));
                Assert.That(replanning.Tier, Is.EqualTo(LuaMBehaviorTier.Support));
                Assert.That(ship.BehaviorState, Is.EqualTo("replanning_route"));
                Assert.That(htn.RootTask.Task, Is.EqualTo("LuaMShipNavigateBehaviorCompound"));
            });

            ship.RouteBlocked = false;
            Assert.That(adapter.RefreshNow(shipGrid, ship, force: true), Is.True);
            var hostile = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(20f, 0f), map.MapId));
            Assert.That(behavior.ReportObservation(
                core,
                LuaMBehaviorStimulus.HostileThreat,
                0.9f,
                target: hostile,
                ttl: TimeSpan.FromMinutes(1),
                source: "logistics-test-threat",
                evaluateNow: true), Is.True);
            Assert.That(behavior.GetDecision(core, out var disengage), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(disengage.Intent, Is.EqualTo(LuaMBehaviorIntent.DisengageShip));
                Assert.That(disengage.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(htn.RootTask.Task, Is.EqualTo("LuaMShipDisengageCompound"));
                Assert.That(ship.BehaviorState, Is.EqualTo("disengaging"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PriorityAndCapabilityInvariantsHoldForEveryConcreteProfile()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var target = entities.SpawnEntity(null, map.MapCoords);
            var profiles = prototypes.EnumeratePrototypes<LuaMBehaviorProfilePrototype>()
                .Where(profile => !profile.Abstract)
                .OrderBy(profile => profile.ID)
                .ToArray();
            Assert.That(profiles, Has.Length.GreaterThanOrEqualTo(10));

            var bodyMovementIntents = new HashSet<LuaMBehaviorIntent>
            {
                LuaMBehaviorIntent.Flee,
                LuaMBehaviorIntent.EvacuateHazard,
                LuaMBehaviorIntent.SeekSafeAtmosphere,
                LuaMBehaviorIntent.Retreat,
                LuaMBehaviorIntent.TakeCover,
                LuaMBehaviorIntent.EvadeProjectile,
            };

            foreach (var profile in profiles)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(profile.Capabilities, Does.Not.Contain(LuaMBehaviorCapability.None), profile.ID);
                    Assert.That(profile.Capabilities.Distinct().Count(), Is.EqualTo(profile.Capabilities.Count), profile.ID);
                    Assert.That(profile.Rules.Select(rule => rule.Id), Has.All.Not.Empty, profile.ID);
                    Assert.That(
                        profile.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count(),
                        Is.EqualTo(profile.Rules.Count),
                        profile.ID);
                });

                var hardStop = Decide(
                    profile,
                    Observation(LuaMBehaviorStimulus.SelfIncapacitated, 1f),
                    Observation(LuaMBehaviorStimulus.DirectOrder, 1f, target),
                    Observation(LuaMBehaviorStimulus.HostileThreat, 1f, target),
                    Observation(LuaMBehaviorStimulus.AllyCritical, 1f, target),
                    Observation(LuaMBehaviorStimulus.RepairNeeded, 1f, target));
                Assert.Multiple(() =>
                {
                    Assert.That(hardStop.Intent, Is.EqualTo(LuaMBehaviorIntent.AwaitRescue), profile.ID);
                    Assert.That(hardStop.Tier, Is.EqualTo(LuaMBehaviorTier.HardStop), profile.ID);
                });

                if (!profile.HasCapability(LuaMBehaviorCapability.Medical))
                {
                    var patient = Decide(profile, Observation(LuaMBehaviorStimulus.AllyCritical, 1f, target));
                    Assert.That(patient.Intent, Is.Not.EqualTo(LuaMBehaviorIntent.Treat), profile.ID);
                }

                if (!profile.HasCapability(LuaMBehaviorCapability.Repair))
                {
                    var repair = Decide(profile, Observation(LuaMBehaviorStimulus.RepairNeeded, 1f, target));
                    Assert.That(repair.Intent, Is.Not.EqualTo(LuaMBehaviorIntent.Repair), profile.ID);
                }

                if (profile.HasCapability(LuaMBehaviorCapability.Stationary))
                {
                    var pressure = Decide(
                        profile,
                        Observation(LuaMBehaviorStimulus.HostileThreat, 1f, target),
                        Observation(LuaMBehaviorStimulus.Overwhelmed, 1f, target),
                        Observation(LuaMBehaviorStimulus.ProjectileThreat, 1f, target));
                    Assert.That(bodyMovementIntents, Does.Not.Contain(pressure.Intent), profile.ID);
                }

                if (profile.HasCapability(LuaMBehaviorCapability.ShipControl) &&
                    !profile.HasCapability(LuaMBehaviorCapability.ShipWeapons))
                {
                    var civilianContact = Decide(
                        profile,
                        Observation(LuaMBehaviorStimulus.HostileThreat, 1f, target));
                    Assert.That(
                        civilianContact.Intent,
                        Is.EqualTo(LuaMBehaviorIntent.DisengageShip),
                        profile.ID);
                }
            }

            LuaMBehaviorDecision Decide(
                LuaMBehaviorProfilePrototype profile,
                params LuaMBehaviorObservation[] observations)
            {
                return LuaMBehaviorArbiter.Decide(profile, observations, null, timing.CurTime);
            }

            LuaMBehaviorObservation Observation(
                LuaMBehaviorStimulus stimulus,
                float severity,
                EntityUid? observationTarget = null)
            {
                return new LuaMBehaviorObservation
                {
                    Stimulus = stimulus,
                    Severity = severity,
                    Confidence = 1f,
                    Target = observationTarget,
                    ObservedAt = timing.CurTime,
                    ExpiresAt = timing.CurTime + TimeSpan.FromMinutes(1),
                    Source = "profile-invariant",
                };
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorDroneAdapterTranslatesDomainTasksAndRouteFailure()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var adapter = entities.System<LuaMAiDroneBehaviorAdapterSystem>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var miningSystem = entities.System<LuaMAiMiningDroneSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var zone = entities.SpawnEntity(null, map.GridCoords.Offset(new Vector2(5f, 0f)));
            var droneUid = entities.SpawnEntity(null, map.MapCoords);
            var drone = entities.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneRole = "miner";
            var task = entities.EnsureComponent<LuaMAiDroneTaskComponent>(droneUid);
            task.TaskType = "mine_route";
            task.ZoneType = "mining";
            task.TargetZone = zone;

            Assert.That(adapter.RefreshNow(droneUid, drone, force: true), Is.True);
            Assert.That(behavior.GetDecision(droneUid, out var mining), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(mining.Intent, Is.EqualTo(LuaMBehaviorIntent.Mine));
                Assert.That(mining.Target, Is.EqualTo(zone));
                Assert.That(drone.State, Is.EqualTo("assigned_mining"));
            });

            task.IsStuck = true;
            Assert.That(adapter.RefreshNow(droneUid, drone, force: true), Is.True);
            Assert.That(behavior.GetDecision(droneUid, out var replanning), Is.True);
            var beforeDetour = entities.GetComponent<TransformComponent>(droneUid).Coordinates.Position;
            Assert.That(miningSystem.TryExecuteBehaviorMovement(droneUid, drone), Is.True);
            var afterDetour = entities.GetComponent<TransformComponent>(droneUid).Coordinates.Position;
            Assert.Multiple(() =>
            {
                Assert.That(replanning.Intent, Is.EqualTo(LuaMBehaviorIntent.ReplanRoute));
                Assert.That(replanning.Tier, Is.EqualTo(LuaMBehaviorTier.Support));
                Assert.That(drone.State, Is.EqualTo("replanning_route"));
                Assert.That(task.TaskStage, Is.EqualTo("replanning"));
                Assert.That(afterDetour, Is.Not.EqualTo(beforeDetour));
            });

            task.IsStuck = false;
            drone.DroneRole = "repair";
            task.TaskType = "repair_watch";
            task.ZoneType = "dock";
            Assert.That(adapter.RefreshNow(droneUid, drone, force: true), Is.True);
            Assert.That(behavior.GetDecision(droneUid, out var repair), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(repair.Intent, Is.EqualTo(LuaMBehaviorIntent.Repair));
                Assert.That(drone.State, Is.EqualTo("assigned_repair"));
                Assert.That(
                    entities.GetComponent<LuaMBehaviorAgentComponent>(droneUid).Profile.ToString(),
                    Is.EqualTo("LuaMRepairRobotBehavior"));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static bool NeedsTarget(LuaMBehaviorStimulus stimulus)
    {
        return stimulus is LuaMBehaviorStimulus.HostileThreat or
            LuaMBehaviorStimulus.Overwhelmed or
            LuaMBehaviorStimulus.RecoverableDeadAlly or
            LuaMBehaviorStimulus.AllyCritical or
            LuaMBehaviorStimulus.AllyInjured or
            LuaMBehaviorStimulus.RepairNeeded or
            LuaMBehaviorStimulus.CleaningNeeded or
            LuaMBehaviorStimulus.MineableResource or
            LuaMBehaviorStimulus.SalvageTarget or
            LuaMBehaviorStimulus.DirectOrder;
    }
}
