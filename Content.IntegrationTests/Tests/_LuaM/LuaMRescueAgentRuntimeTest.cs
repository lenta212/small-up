using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Buckle.Systems;
using Content.Server.Mind;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server._LuaM.Rescue;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.MedicalScanner;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Misc;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Server.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueAgentSystem))]
public sealed class LuaMRescueAgentRuntimeTest
{
    private const float RescueActionRange = 1.5f;
    private const int AsyncTickLimit = 120;

    [Test]
    public async Task ManualOrderRejectsPlayerBorerAndContainedPatientBeforePulling()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            var borer = entities.SpawnEntity(
                "MobCorticalBorer",
                new MapCoordinates(new Vector2(0.5f, 0f), map.MapId));
            mobState.ChangeMobState(borer, MobState.Dead);
            // A bare ActorComponent has no player session and makes unrelated
            // respawn/playtime handlers throw during a state transition. Add it
            // only after the transition; the manual eligibility gate still sees
            // the same player-shaped unsupported target.
            entities.EnsureComponent<ActorComponent>(borer);

            Assert.That(agentSystem.TryOrderAgent(agent, borer, out var borerStatus), Is.False);
            Assert.That(borerStatus, Does.Contain(nameof(LuaMRescueFailureReason.UnsupportedSpecies)));
            AssertRejectedWithoutPull(
                entities,
                coordinator,
                agent,
                borer,
                LuaMRescueFailureReason.UnsupportedSpecies);

            var containedPatient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));
            mobState.ChangeMobState(containedPatient, MobState.Critical);
            var containerOwner = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));
            var container = containers.EnsureContainer<Container>(
                containerOwner,
                "LuaMRescueAgentRuntimeContainer");
            Assert.That(containers.Insert(containedPatient, container), Is.True);
            Assert.That(containers.IsEntityOrParentInContainer(containedPatient), Is.True);

            Assert.That(
                agentSystem.TryOrderAgent(agent, containedPatient, out var containedStatus),
                Is.False);
            Assert.That(containedStatus, Does.Contain(nameof(LuaMRescueFailureReason.ContainedTarget)));
            AssertRejectedWithoutPull(
                entities,
                coordinator,
                agent,
                containedPatient,
                LuaMRescueFailureReason.ContainedTarget);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualOverrideAssignmentRemainsAuthoritativeAcrossRefreshes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = server.System<SharedMapSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid hostilePatient = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 4);
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.TargetRefreshInterval = 0.001f;

            hostilePatient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 3.5f, 0.5f));
            entities.EnsureComponent<NpcFactionMemberComponent>(hostilePatient);
            factions.RemoveFaction(hostilePatient, "NanoTrasen");
            factions.AddFaction(hostilePatient, "Syndicate");
            Assert.That(factions.IsEntityHostile(agent, hostilePatient), Is.True);
            var injury = new DamageSpecifier();
            injury.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(hostilePatient, injury, ignoreResistances: true),
                Is.Not.Null);

            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    agent,
                    hostilePatient,
                    LuaMRescuePatientRequestKind.AutomaticTreatment,
                    manualOverride: false,
                    out var automaticFailure),
                Is.False);
            Assert.That(automaticFailure, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh));
            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    agent,
                    hostilePatient,
                    LuaMRescuePatientRequestKind.Manual,
                    manualOverride: true,
                    out var manualFailure),
                Is.True,
                manualFailure.ToString());
        });

        await AwaitReachableRoute(pair, navigation, agent, hostilePatient);

        uint generation = 0;
        await server.WaitAssertion(() =>
        {
            Assert.That(
                agentSystem.TryOrderAgent(agent, hostilePatient, out var status),
                Is.True,
                status);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            generation = rescue.ActivityContext.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostilePatient));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(hostilePatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(hostilePatient));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(hostilePatient));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        for (var refresh = 0; refresh < 3; refresh++)
        {
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                agentSystem.Update(rescue.TargetRefreshInterval);
                Assert.Multiple(() =>
                {
                    Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostilePatient));
                    Assert.That(rescue.AssignedTarget, Is.EqualTo(hostilePatient));
                    Assert.That(rescue.TaskPatientTarget, Is.EqualTo(hostilePatient));
                    Assert.That(rescue.ActivityContext.Target, Is.EqualTo(hostilePatient));
                    Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                    Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(generation));
                    Assert.That(rescue.LastTargetTrackingStatus, Does.Not.Contain("target_rejected"));
                });
            });
        }

        EntityUid criticalPatient = default;
        await server.WaitAssertion(() =>
        {
            criticalPatient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 1.5f, 0.5f));
            mobState.ChangeMobState(criticalPatient, MobState.Critical);
        });
        await AwaitReachableRoute(pair, navigation, agent, criticalPatient);

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            agentSystem.Update(rescue.TargetRefreshInterval);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(criticalPatient));
                Assert.That(rescue.DeferredPatientTargets, Does.ContainKey(hostilePatient));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostilePatient),
                    "Automatic urgency preemption must preserve the displaced manual provenance.");
            });
        });

        await server.WaitAssertion(() =>
        {
            mobState.ChangeMobState(criticalPatient, MobState.Alive);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            agentSystem.Update(rescue.TargetRefreshInterval);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(hostilePatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(hostilePatient));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostilePatient));
                Assert.That(rescue.DeferredPatientTargets, Does.Not.ContainKey(hostilePatient));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(hostilePatient));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.ActivityContext.Generation, Is.GreaterThan(generation));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RejectedPlayerActionPreservesManualOwnershipUntilAcceptedReplacement()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 3);
            var agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;

            var patient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 1.5f, 0.5f));
            var injury = new DamageSpecifier();
            injury.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(patient, injury, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(agentSystem.TryOrderAgent(agent, patient, out var orderStatus), Is.True, orderStatus);

            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var followBefore,
                    entities),
                Is.True);
            var generationBefore = rescue.ActivityContext.Generation;
            var activityBefore = rescue.ActivityContext.Activity;
            var terminalBefore = rescue.ActivityContext.TerminalStatus;
            var taskBefore = rescue.TaskStage;
            var pendingBefore = rescue.PendingPlayerAction;
            var pendingTargetBefore = rescue.PendingPlayerActionTarget;

            Assert.That(
                agentSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.UnequipSlot,
                    null,
                    "__missing_slot__",
                    out var rejectedStatus),
                Is.False,
                rejectedStatus);

            Assert.Multiple(() =>
            {
                Assert.That(rejectedStatus, Does.Contain("empty or unavailable"));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskStage, Is.EqualTo(taskBefore));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(pendingBefore));
                Assert.That(rescue.PendingPlayerActionTarget, Is.EqualTo(pendingTargetBefore));
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(generationBefore));
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(activityBefore));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(terminalBefore));
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var followAfter,
                        entities),
                    Is.True);
                Assert.That(followAfter, Is.EqualTo(followBefore));
            });

            var replacement = entities.SpawnEntity(
                null,
                GridCoordinates(map.Grid, 2.5f, 0.5f));
            Assert.That(
                agentSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Interact,
                    replacement,
                    out var acceptedStatus),
                Is.True,
                acceptedStatus);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.ManualOverrideTarget, Is.Null);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.Interact));
                Assert.That(rescue.PendingPlayerActionTarget, Is.EqualTo(replacement));
                Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.ManualAction));
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.TaskSupplyTarget, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Generation, Is.GreaterThan(generationBefore));
                Assert.That(
                    rescue.ActivityContext.Activity,
                    Is.AnyOf(LuaMRescueActivity.ManualAction, LuaMRescueActivity.PlanningRoute));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReplacingManualTargetCancelsOldHtnAndSteeringIntent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid firstPatient = default;
        EntityUid secondPatient = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, -14, 14);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;

            firstPatient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 12.5f, 0.5f));
            secondPatient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, -11.5f, 0.5f));
        });

        await AwaitReachableRoute(pair, navigation, agent, firstPatient);
        await AwaitReachableRoute(pair, navigation, agent, secondPatient);

        uint firstGeneration = 0;
        await server.WaitAssertion(() =>
        {
            Assert.That(agentSystem.TryOrderAgent(agent, firstPatient, out var status), Is.True, status);
            Assert.That(coordinator.GetSnapshot(agent, out var firstIntent), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(firstIntent.Target, Is.EqualTo(firstPatient));
                Assert.That(firstIntent.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(entities.GetComponent<LuaMRescueAgentComponent>(agent).AssignedTarget,
                    Is.EqualTo(firstPatient));
            });

            firstGeneration = firstIntent.Generation;
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var firstFollow,
                    entities),
                Is.True);
            Assert.That(firstFollow.EntityId, Is.EqualTo(firstPatient));
        });

        Assert.That(
            await AwaitSteeringTarget(pair, entities, agent, firstPatient),
            Is.True,
            "The first MoveTo never became an active steering operator.");

        // Refresh the second route after planning the first move so target replacement itself
        // executes synchronously against a completed authoritative path query.
        await AwaitReachableRoute(pair, navigation, agent, secondPatient);

        await server.WaitAssertion(() =>
        {
            Assert.That(agentSystem.TryOrderAgent(agent, secondPatient, out var status), Is.True, status);
            Assert.That(coordinator.GetSnapshot(agent, out var replacement), Is.True);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var htn = entities.GetComponent<HTNComponent>(agent);

            Assert.Multiple(() =>
            {
                Assert.That(replacement.Generation, Is.GreaterThan(firstGeneration));
                Assert.That(replacement.Target, Is.EqualTo(secondPatient));
                Assert.That(replacement.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(secondPatient));
                Assert.That(rescue.EvacuatingTarget, Is.Null);
                Assert.That(htn.Plan, Is.Null, "The old HTN plan must be shut down before replanning.");
                Assert.That(htn.PlanningJob, Is.Null, "A stale planner job must not survive generation change.");
            });

            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var replacementFollow,
                    entities),
                Is.True);
            Assert.That(replacementFollow.EntityId, Is.EqualTo(secondPatient));

            if (entities.TryGetComponent<NPCSteeringComponent>(agent, out var steering))
                Assert.That(steering.Coordinates.EntityId, Is.Not.EqualTo(firstPatient));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReissuingCancelledExplicitPatientOrderCreatesNewActiveGeneration()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 6);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 4.5f, 0.5f));

            // This fixture exercises explicit intent ownership only. Vacuum and
            // autonomous executors must not mutate the patient between orders.
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoRouteShuttleToTargets = false;
            rescue.AutoReturnShuttle = false;

            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 100);
            Assert.That(
                damageable.TryChangeDamage(patient, damage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(patient), Is.True,
                "The explicit-order target must be a physically critical patient.");
        });

        await AwaitReachableRoute(pair, navigation, agent, patient);

        uint cancelledGeneration = 0;
        EntityUid? legacyAssignedTarget = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(agentSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
            Assert.That(coordinator.GetSnapshot(agent, out var active), Is.True);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(active.Target, Is.EqualTo(patient));
                Assert.That(active.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
            });

            Assert.That(
                coordinator.Fail(agent, active.Generation, LuaMRescueFailureReason.ActionCancelled, out var cancelled),
                Is.True);
            cancelledGeneration = cancelled.Generation;
            legacyAssignedTarget = rescue.AssignedTarget;
            Assert.Multiple(() =>
            {
                Assert.That(cancelled.Generation, Is.EqualTo(active.Generation));
                Assert.That(cancelled.Target, Is.EqualTo(patient));
                Assert.That(cancelled.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(cancelled.FailureReason, Is.EqualTo(LuaMRescueFailureReason.ActionCancelled));
                Assert.That(legacyAssignedTarget, Is.EqualTo(patient),
                    "A terminal coordinator update does not itself clear the legacy owner mirror.");
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(legacyAssignedTarget, Is.EqualTo(patient));
            Assert.That(agentSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
            Assert.That(coordinator.GetSnapshot(agent, out var reissued), Is.True);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(reissued.Generation, Is.GreaterThan(cancelledGeneration));
                Assert.That(reissued.Target, Is.EqualTo(patient));
                Assert.That(reissued.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PriorityOverrideTransfersEveryOwnerBeforeStartingTreatment()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var damageable = entities.System<DamageableSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid displaced = default;
        EntityUid replacement = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 14);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = true;

            // Both patients remain in the same Injured urgency category, so this
            // exercises the deterministic acuity/distance override rather than
            // the separate Critical/Severe preemption path.
            displaced = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 12.5f, 0.5f));
            var minorDamage = new DamageSpecifier();
            minorDamage.DamageDict.Add("Blunt", 1);
            Assert.That(
                damageable.TryChangeDamage(displaced, minorDamage, ignoreResistances: true),
                Is.Not.Null);
        });

        await AwaitReachableRoute(pair, navigation, agent, displaced);
        await server.WaitAssertion(() =>
        {
            Assert.That(agentSystem.TryOrderAgent(agent, displaced, out var status), Is.True, status);
            var htn = entities.GetComponent<HTNComponent>(agent);

            // Seed the exact stale low-level owner that the priority override
            // must clear. An order does not synchronously start MoveTo, and
            // waiting for steering here would duplicate the dedicated movement
            // cancellation regression above instead of exercising the
            // immediate-treatment hand-off.
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(displaced, Vector2.Zero));
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var oldFollow,
                    entities),
                Is.True);
            Assert.That(oldFollow.EntityId, Is.EqualTo(displaced));
        });

        // ReplacingManualTargetCancelsOldHtnAndSteeringIntent above exercises a
        // fully active steering operator. This regression targets the distinct
        // immediate-treatment branch where stale FollowTarget ownership used to
        // survive even before/without a steering component being observable.

        uint generationBeforeReplacement = 0;
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            generationBeforeReplacement = rescue.ActivityContext.Generation;
            replacement = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 1f, 0.5f));
            var treatableDamage = new DamageSpecifier();
            treatableDamage.DamageDict.Add("Blunt", 20);
            Assert.That(
                damageable.TryChangeDamage(replacement, treatableDamage, ignoreResistances: true),
                Is.Not.Null);
            rescue.NextAutoTreatmentAttempt = timing.CurTime + TimeSpan.FromSeconds(10);
        });

        // The priority selector requires authoritative route facts for both the
        // current and candidate patient. Without refreshing both probes this
        // scenario can fall through to generic acquisition and never exercise
        // the atomic priority-transfer/deferred-memory contract it asserts.
        await AwaitReachableRoute(pair, navigation, agent, displaced);
        await AwaitReachableRoute(pair, navigation, agent, replacement);
        await server.WaitAssertion(() =>
        {
            entities.GetComponent<LuaMRescueAgentComponent>(agent).AutoAcquireTargets = true;
        });

        var replacementObserved = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                replacementObserved = rescue.AssignedTarget == replacement &&
                                      rescue.ActivityContext.Target == replacement &&
                                      rescue.ActivityContext.Activity == LuaMRescueActivity.TreatPatient;
            });
            if (replacementObserved)
                break;
        }

        Assert.That(replacementObserved, Is.True,
            "The in-range higher-acuity patient never became the sole treatment intent during cooldown.");

        uint replacementGeneration = 0;
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var htn = entities.GetComponent<HTNComponent>(agent);
            replacementGeneration = rescue.ActivityContext.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(replacementGeneration, Is.GreaterThan(generationBeforeReplacement));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacement));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.TreatPatient));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.LastAutoTreatmentStatus, Does.Contain("treatment cooldown"));
                Assert.That(rescue.EvacuatingTarget, Is.Null);
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(
                    rescue.DeferredPatientTargets,
                    Does.ContainKey(displaced),
                    $"Deferred ownership was lost: last={rescue.LastDeferredPatientStatus}; " +
                    $"displacedDamage={entities.GetComponent<DamageableComponent>(displaced).TotalDamage.Float():0.###}; " +
                    $"assigned={rescue.AssignedTarget}; task={rescue.TaskPatientTarget}; " +
                    $"activity={rescue.ActivityContext.Activity}@g{rescue.ActivityContext.Generation}; " +
                    $"cancellation={rescue.LastIntentCancellationStatus}");
                Assert.That(htn.Plan, Is.Null,
                    "The displaced patient's HTN plan must be shut down before treatment starts.");
            });

            if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var follow,
                    entities))
            {
                Assert.That(follow.EntityId, Is.EqualTo(replacement));
            }

            if (entities.TryGetComponent<NPCSteeringComponent>(agent, out var steering))
                Assert.That(steering.Coordinates.EntityId, Is.Not.EqualTo(displaced));
        });

        var cooldownIntentTrace = new List<string>();
        for (var tick = 0; tick < 3; tick++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var distance = entities.GetComponent<TransformComponent>(agent).Coordinates.TryDistance(
                    entities,
                    entities.GetComponent<TransformComponent>(replacement).Coordinates,
                    out var measuredDistance)
                    ? measuredDistance
                    : float.PositiveInfinity;
                var patientDamage = entities.GetComponent<DamageableComponent>(replacement).TotalDamage.Float();
                cooldownIntentTrace.Add(
                    $"tick={tick + 1}; generation={rescue.ActivityContext.Generation}; " +
                    $"activity={rescue.ActivityContext.Activity}; terminal={rescue.ActivityContext.TerminalStatus}; " +
                    $"task={rescue.TaskStage}; target={rescue.ActivityContext.Target}; taskPatient={rescue.TaskPatientTarget}; " +
                    $"assigned={rescue.AssignedTarget}; evacuating={rescue.EvacuatingTarget}; " +
                    $"onboard={rescue.OnboardCareTarget}; deathSignal={rescue.DeathSignalTarget}; " +
                    $"route={rescue.ActivityContext.RouteStatus}; distance={distance:0.000}; damage={patientDamage:0.000}; " +
                    $"now={timing.CurTime.TotalSeconds:0.000}; cooldown={rescue.NextAutoTreatmentAttempt.TotalSeconds:0.000}; " +
                    $"terminalTreatment={rescue.TerminalTreatmentFailures.GetValueOrDefault(replacement, "none")}; " +
                    $"tracking={rescue.LastTargetTrackingStatus}; treatment={rescue.LastAutoTreatmentStatus}; " +
                    $"treatmentDecision={rescue.LastAutoTreatmentDecisionStatus}; " +
                    $"cancellation={rescue.LastIntentCancellationStatus}; " +
                    $"taskStatus={rescue.LastTaskStatus}");
            });
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacement));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(replacementGeneration),
                    "A treatment cooldown must not churn TreatPatient/ApproachPatient generations. " +
                    string.Join(" | ", cooldownIntentTrace));
            });

            rescue.NextAutoTreatmentAttempt = timing.CurTime;
        });

        var treatmentStarted = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                treatmentStarted = rescue.PendingMedicalDoAfterTarget == replacement;
            });
            if (treatmentStarted)
                break;
        }

        Assert.That(treatmentStarted, Is.True,
            "Releasing the cooldown never started treatment on the replacement patient.");
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacement));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacement));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(replacement));
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(replacementGeneration));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExplicitOrderAtomicallyClearsAllStalePatientOwners()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var pulling = entities.System<PullingSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid previous = default;
        EntityUid replacement = default;
        uint previousGeneration = 0;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 2);
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.25f, 0.5f));
            previous = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 0.75f, 0.5f));
            replacement = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 1.25f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(previous);
            entities.RemoveComponent<BarotraumaComponent>(replacement);
            var replacementDamage = new DamageSpecifier();
            replacementDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(replacement, replacementDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(replacement), Is.True);

            var staleStrap = entities.SpawnEntity(
                null,
                GridCoordinates(map.Grid, 1.75f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AssignedTarget = previous;
            rescue.EvacuatingTarget = previous;
            rescue.OnboardCareTarget = previous;
            rescue.DeathSignalTarget = previous;
            rescue.DeathSignalDispatchReported = true;
            rescue.AssignedPatientStrap = staleStrap;
            rescue.TaskStage = LuaMRescueTaskStage.EvacuatingPatient;
            rescue.TaskPatientTarget = previous;

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.Pulling,
                    previous,
                    new EntityCoordinates(previous, Vector2.Zero),
                    out var previousIntent),
                Is.True);
            previousGeneration = previousIntent.Generation;
            Assert.That(
                pulling.TryStartPull(
                    agent,
                    previous,
                    entities.GetComponent<PullerComponent>(agent),
                    entities.GetComponent<PullableComponent>(previous)),
                Is.True);
            Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.EqualTo(previous));

            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsCritical(replacement), Is.True);
                Assert.That(
                    coordinator.GetPatientUrgency(replacement),
                    Is.EqualTo(LuaMRescuePatientUrgency.Critical));
            });
            Assert.That(agentSystem.TryOrderAgent(agent, replacement, out var status), Is.True, status);
        });

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var replacementIntent), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(replacementIntent.Generation, Is.GreaterThan(previousGeneration));
                Assert.That(replacementIntent.Target, Is.EqualTo(replacement));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacement));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacement));
                Assert.That(rescue.EvacuatingTarget, Is.Null);
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.DeathSignalTarget, Is.Null);
                Assert.That(rescue.DeathSignalDispatchReported, Is.False);
                Assert.That(rescue.AssignedPatientStrap, Is.Null);
                Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.Null);
                Assert.That(entities.GetComponent<PullableComponent>(previous).Puller, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RealCriticalAndDeathEventsRemainQueuedBehindBusySingleton()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var minds = entities.System<MindSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var session = pair.Server.ResolveDependency<IPlayerManager>()
            .GetSessionById(clientSession!.UserId);

        EntityUid agent = default;
        EntityUid current = default;
        EntityUid criticalSignal = default;
        EntityUid deathSignal = default;
        var before = 0;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 2);
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.25f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;

            // Establish an equal-urgency current intent without producing an
            // automatic signal for the fixture itself.
            current = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 0.75f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(current);
            var currentDamage = new DamageSpecifier();
            currentDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(current, currentDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(current), Is.True);
            Assert.That(agentSystem.TryOrderAgent(agent, current, out var orderStatus), Is.True, orderStatus);

            criticalSignal = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 1.25f, 0.5f));
            deathSignal = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 1.75f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(criticalSignal);
            entities.RemoveComponent<BarotraumaComponent>(deathSignal);
            var deathMind = minds.CreateMind(null, "LuaM real queued death signal");
            minds.TransferTo(deathMind, deathSignal, createGhost: false, mind: deathMind.Comp);

            // Attach the real integration session before each transition. A
            // bare ActorComponent is not player-controlled and also gives
            // unrelated ForceSay handlers no valid network recipient.
            before = shuttle.PendingAutomaticDispatchCount;
            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsCritical(current), Is.True);
                Assert.That(
                    coordinator.GetPatientUrgency(current),
                    Is.EqualTo(LuaMRescuePatientUrgency.Critical));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(current));
            });
            pair.Server.PlayerMan.SetAttachedEntity(session, criticalSignal, true);
            Assert.That(entities.HasComponent<ActorComponent>(criticalSignal), Is.True);
            var criticalDamage = new DamageSpecifier();
            criticalDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(criticalSignal, criticalDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsCritical(criticalSignal), Is.True);
                Assert.That(
                    coordinator.GetPatientUrgency(criticalSignal),
                    Is.EqualTo(LuaMRescuePatientUrgency.Critical));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == criticalSignal),
                    Is.True);
            });
            pair.Server.PlayerMan.SetAttachedEntity(session, deathSignal, true);
            Assert.That(entities.HasComponent<ActorComponent>(deathSignal), Is.True);
            var lethalDamage = new DamageSpecifier();
            lethalDamage.DamageDict.Add("Blunt", 210);
            Assert.That(
                damageable.TryChangeDamage(deathSignal, lethalDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsDead(deathSignal), Is.True);
                Assert.That(
                    coordinator.GetPatientUrgency(deathSignal),
                    Is.EqualTo(LuaMRescuePatientUrgency.RecoverableDead));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
            });
            pair.Server.PlayerMan.SetAttachedEntity(session, null, true);

            var pending = shuttle.GetPendingAutomaticDispatches();
            Assert.Multiple(() =>
            {
                Assert.That(shuttle.PendingAutomaticDispatchCount, Is.EqualTo(before + 2));
                Assert.That(pending.Count(entry => entry.Target == criticalSignal), Is.EqualTo(1));
                Assert.That(pending.Count(entry => entry.Target == deathSignal), Is.EqualTo(1));
                Assert.That(pending.Any(entry =>
                    entry.Target == criticalSignal && entry.Kind == LuaMRescueMedicalSignalKind.Critical), Is.True);
                Assert.That(pending.Any(entry =>
                    entry.Target == deathSignal && entry.Kind == LuaMRescueMedicalSignalKind.Death), Is.True);
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
            });
        });

        // Exercise the periodic queue processor as well. Busy singleton pressure
        // must retain both event-produced entries without consuming retry budget.
        await pair.RunSeconds(2.2f);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var pending = shuttle.GetPendingAutomaticDispatches();
            var queuedCritical = pending.Single(entry => entry.Target == criticalSignal);
            var queuedDeath = pending.Single(entry => entry.Target == deathSignal);
            Assert.Multiple(() =>
            {
                Assert.That(shuttle.PendingAutomaticDispatchCount, Is.EqualTo(before + 2));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
                Assert.That(queuedCritical.Terminal, Is.False);
                Assert.That(queuedDeath.Terminal, Is.False);
                Assert.That(queuedCritical.Attempts, Is.Zero);
                Assert.That(queuedDeath.Attempts, Is.Zero);
                Assert.That(queuedCritical.LastStatus, Does.Contain("busy"));
                Assert.That(queuedDeath.LastStatus, Does.Contain("busy"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NearbyEligibleHumanoidAndAnimalCannotStealCoordinatorAssignedPatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid assignedPatient = default;
        EntityUid injuredHumanoid = default;
        EntityUid injuredAnimal = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 2);
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.25f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;

            assignedPatient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 1.25f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(assignedPatient);
            var assignedDamage = new DamageSpecifier();
            assignedDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(assignedPatient, assignedDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(assignedPatient), Is.True);
            Assert.That(agentSystem.TryOrderAgent(agent, assignedPatient, out var status), Is.True, status);

            // This is a closer, visible and otherwise fully eligible automatic
            // treatment candidate. Its lower urgency must not make the medibot
            // selection cycle override coordinator ownership.
            injuredHumanoid = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 0.75f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(injuredHumanoid);
            var humanoidDamage = new DamageSpecifier();
            humanoidDamage.DamageDict.Add("Blunt", 60);
            Assert.That(
                damageable.TryChangeDamage(injuredHumanoid, humanoidDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsAlive(injuredHumanoid), Is.True);
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        injuredHumanoid,
                        LuaMRescuePatientRequestKind.AutomaticTreatment,
                        manualOverride: false,
                        out var eligibilityFailure),
                    Is.True,
                    eligibilityFailure.ToString());
                Assert.That(coordinator.GetPatientUrgency(injuredHumanoid),
                    Is.LessThan(LuaMRescuePatientUrgency.Critical));
            });

            injuredAnimal = entities.SpawnEntity(
                "MobMouse",
                GridCoordinates(map.Grid, 0.75f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(injuredAnimal);
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 10);
            Assert.That(damageable.TryChangeDamage(injuredAnimal, damage, ignoreResistances: true), Is.Not.Null);

            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsCritical(assignedPatient), Is.True);
                Assert.That(
                    coordinator.GetPatientUrgency(assignedPatient),
                    Is.EqualTo(LuaMRescuePatientUrgency.Critical));
                Assert.That(
                    coordinator.GetPatientUrgency(injuredHumanoid),
                    Is.LessThan(LuaMRescuePatientUrgency.Critical));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(assignedPatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(assignedPatient));
            });
        });

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(assignedPatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(assignedPatient));
                Assert.That(rescue.AssignedTarget, Is.Not.EqualTo(injuredHumanoid));
                Assert.That(rescue.AssignedTarget, Is.Not.EqualTo(injuredAnimal));
                if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var follow,
                        entities))
                {
                    Assert.That(follow.EntityId, Is.EqualTo(assignedPatient));
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AutomaticAcquisitionRequiresActualPerceptionButKeepsVisiblePatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid wall = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 3);
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoTreatWithCarriedItems = false;

            wall = entities.SpawnEntity(
                "WallSolid",
                GridCoordinates(map.Grid, 1.5f, 0.5f));
            patient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 2.5f, 0.5f));
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 20);
            Assert.That(
                damageable.TryChangeDamage(patient, damage, ignoreResistances: true),
                Is.Not.Null);
        });

        await pair.RunTicksSync(30);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
            });
        });

        await server.WaitAssertion(() => entities.DeleteEntity(wall));
        await pair.RunTicksSync(AsyncTickLimit);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(
                rescue.AssignedTarget == patient || rescue.TaskPatientTarget == patient,
                Is.True,
                "A newly visible injured humanoid should become a remembered coordinator target.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualOverrideResumesAfterTemporaryRouteSkipWithoutExplicitRedispatch()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid wall = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 7);
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.5f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<RespiratorComponent>(agent);
            wall = entities.SpawnEntity(
                "WallSolid",
                GridCoordinates(map.Grid, 3.5f, 0.5f));

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 1_000f;
            rescue.TargetRefreshAccumulator = 0f;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.TargetSkipSeconds = 0.01f;

            patient = entities.SpawnEntity(
                "MobHumanSyndicateAgentBase",
                GridCoordinates(map.Grid, 6.5f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(patient);
            var injury = new DamageSpecifier();
            injury.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(patient, injury, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(rescue.ActivityRoleProfile.MaxAttempts, Is.GreaterThan(1));
            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    agent,
                    patient,
                    LuaMRescuePatientRequestKind.AutomaticTreatment,
                    manualOverride: false,
                    out var automaticFailure),
                Is.False);
            Assert.That(automaticFailure, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh));
            Assert.That(agentSystem.TryOrderAgent(agent, patient, out var orderStatus), Is.True, orderStatus);
            Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
        });

        await AwaitRouteState(
            pair,
            navigation,
            agent,
            patient,
            LuaMRescuePathProbeState.NoPath);

        uint generationAfterSkip = 0;
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            agentSystem.Update(0f);
            generationAfterSkip = rescue.ActivityContext.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(rescue.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(1));
                Assert.That(rescue.SkippedTargets, Does.ContainKey(patient));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.EvacuatingTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.DormantRouteTarget, Is.Null);
            });
        });

        await pair.RunSeconds(0.05f);

        await server.WaitAssertion(() =>
        {
            // A fresh probe stays pending long enough to observe ownership
            // restoration itself. No second TryOrderAgent call is allowed.
            navigation.CancelRoute(agent, patient);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            agentSystem.Update(0f);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.SkippedTargets, Does.Not.ContainKey(patient));
                Assert.That(rescue.DormantRouteTarget, Is.Null);
                Assert.That(rescue.ActivityContext.Generation, Is.GreaterThan(generationAfterSkip));
                Assert.That(
                    rescue.ActivityContext.Activity,
                    Is.AnyOf(LuaMRescueActivity.ApproachPatient, LuaMRescueActivity.PlanningRoute));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExhaustedNoPathUsesDormantProbeAndResumesExactlyOnceWhenRouteOpens()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid wall = default;
        await server.WaitAssertion(() =>
        {
            // Keep one physically connected grid throughout the test. A solid
            // wall blocks the one-tile corridor until it is deleted below; using
            // separate floor islands lets grid auto-splitting turn NoPath into
            // DifferentGrid and no longer exercises dormant route recovery.
            BuildHorizontalFloor(mapSystem, map, 0, 7);

            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.5f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<RespiratorComponent>(agent);
            wall = entities.SpawnEntity(
                "WallSolid",
                GridCoordinates(map.Grid, 3.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 1_000f;
            rescue.TargetRefreshAccumulator = 0f;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.TargetSkipSeconds = 0.01f;
            rescue.DormantRouteObservationSeconds = 0.1f;
            rescue.DormantRouteProbeTimeoutSeconds = 1f;

            patient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map.Grid, 6.5f, 0.5f));
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(patient);
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 30);
            Assert.That(
                damageable.TryChangeDamage(patient, damage, ignoreResistances: true),
                Is.Not.Null);
            mobState.ChangeMobState(patient, MobState.Critical);
        });

        int maxAttempts = 0;
        await server.WaitAssertion(() =>
        {
            maxAttempts = entities.GetComponent<LuaMRescueAgentComponent>(agent)
                .ActivityRoleProfile.MaxAttempts;
            Assert.That(maxAttempts, Is.GreaterThanOrEqualTo(1));
        });

        // Exercise one fresh authoritative NoPath result per retry. Explicit
        // redispatch intentionally preserves a completed same-target route cache,
        // so clear only the consumed probe between non-terminal attempts; without
        // that boundary a single cached result can be counted twice or a later
        // redispatch can clear an already-armed dormant owner.
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var consumedSynchronously = false;
            await server.WaitAssertion(() =>
            {
                navigation.CancelRoute(agent, patient);
                Assert.That(agentSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var observedAttempts = rescue.RouteFailureAttempts.GetValueOrDefault(patient);
                consumedSynchronously = observedAttempts == attempt;
                if (!consumedSynchronously)
                {
                    Assert.That(observedAttempts, Is.EqualTo(attempt - 1), status);
                    Assert.That(rescue.AssignedTarget, Is.EqualTo(patient), status);
                    Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient), status);
                    Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient), status);
                    Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active), status);
                }
            });

            if (!consumedSynchronously)
            {
                await AwaitRouteState(
                    pair,
                    navigation,
                    agent,
                    patient,
                    LuaMRescuePathProbeState.NoPath);

                await server.WaitAssertion(() =>
                {
                    // Consume the completed route through the normal runtime owner.
                    // A second administrative order is an intent replacement and is
                    // allowed to invalidate the probe before the agent observes it.
                    var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                    Assert.That(mobState.IsAlive(agent), Is.True, "The route fixture rescuer must remain operational.");
                    Assert.That(mobState.IsCritical(patient), Is.True, "The route fixture patient must remain eligible.");
                    rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
                    agentSystem.Update(0f);
                });
            }

            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var observedAttempts = rescue.RouteFailureAttempts.GetValueOrDefault(patient);
                Assert.That(
                    observedAttempts,
                    Is.EqualTo(attempt),
                    $"assigned={rescue.AssignedTarget}; task={rescue.TaskPatientTarget}/{rescue.TaskStage}; " +
                    $"skipped={rescue.SkippedTargets.ContainsKey(patient)}; defib={rescue.LastAutoDefibStatus}; " +
                    $"activity={rescue.ActivityContext.Activity}/{rescue.ActivityContext.TerminalStatus}/" +
                    $"{rescue.ActivityContext.FailureReason}@g{rescue.ActivityContext.Generation}; " +
                    $"tracking={rescue.LastTargetTrackingStatus}");
                if (attempt == maxAttempts)
                    Assert.That(rescue.DormantRouteTarget, Is.EqualTo(patient));
                else
                    Assert.That(rescue.DormantRouteTarget, Is.Null);
            });
        }

        uint generationBeforeRecovery = 0;
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            generationBeforeRecovery = rescue.ActivityContext.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DormantRouteTarget, Is.EqualTo(patient));
                Assert.That(rescue.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(maxAttempts));
                Assert.That(rescue.DormantRouteResumeCount, Is.Zero);
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
            });

            // Resume normal server updates only after the terminal budget has
            // moved the target out of active coordinator ownership.
            rescue.TargetRefreshInterval = 0.001f;
            rescue.TargetRefreshAccumulator = 1f;
        });

        await pair.RunTicksSync(Math.Max(2, server.Timing.TickRate / 20));
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DormantRouteResumeCount, Is.Zero);
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(generationBeforeRecovery));
                Assert.That(rescue.DormantRouteProbeCount, Is.LessThanOrEqualTo(1),
                    "Dormant observation must not start one path query per update tick.");
            });
        });

        EntityUid shuttle = default;
        EntityUid shuttleAnchor = default;
        await server.WaitAssertion(() =>
        {
            // Reproduce the production return-to-shuttle cleanup path: the
            // dormant patient probe starts while a completed standby FollowTarget
            // still points at the shuttle anchor. Clearing that HTN goal must not
            // cancel the independent patient route query.
            shuttle = entities.SpawnEntity(null, GridCoordinates(map.Grid, 0.5f, 0.5f));
            shuttleAnchor = entities.SpawnEntity(null, GridCoordinates(map.Grid, 0.5f, 0.5f));

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.EvacuateTargetsToShuttle = true;
            rescue.AssignedShuttle = shuttle;
            rescue.AssignedShuttleAnchor = shuttleAnchor;
            rescue.NextDormantRouteProbeAt = TimeSpan.Zero;

            var htn = entities.GetComponent<HTNComponent>(agent);
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(shuttleAnchor, Vector2.Zero));
        });

        var dormantProbeReachedTerminalNoPath = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            // A real standby controller may refresh its return goal while the
            // agent settles at the anchor. Reinsert it to prove repeated HTN
            // cleanup cannot starve the patient query.
            await server.WaitAssertion(() =>
            {
                var htn = entities.GetComponent<HTNComponent>(agent);
                htn.Blackboard.SetValue(
                    NPCBlackboard.FollowTarget,
                    new EntityCoordinates(shuttleAnchor, Vector2.Zero));
            });
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                dormantProbeReachedTerminalNoPath =
                    !rescue.DormantRouteProbeInFlight &&
                    rescue.LastDormantRouteStatus.Contains("remains NoPath", StringComparison.Ordinal);
            });

            if (dormantProbeReachedTerminalNoPath)
                break;
        }

        Assert.That(
            dormantProbeReachedTerminalNoPath,
            Is.True,
            "Standby FollowTarget cleanup repeatedly cancelled the dormant patient route query.");

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AssignedShuttle = null;
            rescue.AssignedShuttleAnchor = null;
            entities.GetComponent<HTNComponent>(agent)
                .Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        });

        await server.WaitAssertion(() => entities.DeleteEntity(wall));

        var resumed = false;
        for (var i = 0; i < AsyncTickLimit * 3; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                resumed = rescue.DormantRouteResumeCount == 1 &&
                          rescue.DormantRouteTarget == null &&
                          rescue.AssignedTarget == patient &&
                          rescue.ActivityContext.Target == patient &&
                          rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active;
            });

            if (resumed)
                break;
        }

        Assert.That(resumed, Is.True, "A cleared route never produced the single fresh patient intent.");

        uint resumedGeneration = 0;
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            resumedGeneration = rescue.ActivityContext.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DormantRouteResumeCount, Is.EqualTo(1));
                Assert.That(resumedGeneration, Is.GreaterThan(generationBeforeRecovery));
                Assert.That(rescue.RouteFailureAttempts.ContainsKey(patient), Is.False);
            });
        });

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DormantRouteResumeCount, Is.EqualTo(1),
                    "A cached Reachable result must not create duplicate resumed intents.");
                Assert.That(rescue.DormantRouteTarget, Is.Null,
                    "The recovered patient must not be re-armed as a dormant route.");
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient),
                    "Normal post-recovery activity transitions must preserve patient ownership.");
                Assert.That(rescue.ActivityContext.Generation, Is.GreaterThanOrEqualTo(resumedGeneration),
                    "Activity generation may advance as the recovered patient moves through normal action phases.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PatientBuckleFailureUsesBoundedPairRetryBudgetAndPreservesRequiredCustody(
        bool requiredHandoff)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = server.System<SharedMapSystem>();
        var mobState = entities.System<MobStateSystem>();
        var pulling = entities.System<PullingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        EntityUid unreachableBed = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, -2, 4);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 1.5f, 0.5f));
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map.Grid, 1.5f, 0.5f));
            unreachableBed = entities.SpawnEntity("MedicalBed", GridCoordinates(map.Grid, 20.5f, 0.5f));
            // This fixture owns the patient's state and retry clock. A pooled
            // Barotrauma update must not incapacitate the rescuer or mutate the
            // patient while the pair-scoped buckle backoff is being observed.
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            mobState.ChangeMobState(patient, MobState.Critical);

            // Keep pulling and the spatial contract valid while making the
            // authoritative buckle operation reject this patient consistently.
            Assert.That(pulling.TryStartPull(agent, patient), Is.True);
            entities.EnsureComponent<TetheredComponent>(patient);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.01f;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AutoUnbucklePatientsForEvacuation = false;
            rescue.AutoRouteShuttleToTargets = false;
            rescue.AutoReturnShuttle = false;
            rescue.EvacuateTargetsToShuttle = true;
            rescue.BucklePatientsOnShuttle = true;
            rescue.AssignedShuttle = map.Grid;
            rescue.AssignedShuttleAnchor = bed;
            rescue.AssignedTarget = patient;
            rescue.EvacuatingTarget = patient;
            rescue.TaskPatientTarget = patient;
            rescue.TaskStage = LuaMRescueTaskStage.EvacuatingPatient;
            rescue.ActivityRoleProfile.MaxAttempts = 2;
            rescue.ActivityRoleProfile.BaseRetryBackoff = TimeSpan.FromSeconds(0.5);
            rescue.ActivityRoleProfile.MaxRetryBackoff = TimeSpan.FromSeconds(0.5);
            if (requiredHandoff)
                rescue.RequiredOnboardHandoffPatients.Add(patient);
        });

        var pairKey = (Patient: patient, Strap: bed);
        var firstAttemptObserved = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                firstAttemptObserved = rescue.PatientBuckleAttempts.GetValueOrDefault(pairKey) == 1;
            });

            if (firstAttemptObserved)
                break;
        }

        Assert.That(firstAttemptObserved, Is.True, "The first physical buckle attempt never ran.");
        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.PatientBuckleAttempts.GetValueOrDefault(pairKey), Is.EqualTo(1),
                    "The same refresh must not consume a second physical attempt during retry backoff.");
                Assert.That(rescue.NextPatientBuckleAttemptAt.ContainsKey(pairKey), Is.True);
                Assert.That(rescue.TerminalPatientBuckleFailures.ContainsKey(pairKey), Is.False);
            });
        });

        await pair.RunSeconds(0.6f);
        // SecondsToTicks can stop on the retry boundary before the next system
        // update observes it. Poll a bounded number of ticks for the second
        // physical attempt instead of coupling the contract to tick ordering.
        var terminalBuckleObserved = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                terminalBuckleObserved = entities
                    .GetComponent<LuaMRescueAgentComponent>(agent)
                    .TerminalPatientBuckleFailures
                    .ContainsKey(pairKey);
            });

            if (terminalBuckleObserved)
                break;
        }
        Assert.That(terminalBuckleObserved, Is.True,
            "The bounded patient/strap retry did not reach its terminal attempt.");
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var retryAt = rescue.NextPatientBuckleAttemptAt.GetValueOrDefault(pairKey);
            var pullingTarget = entities.TryGetComponent<PullerComponent>(agent, out var puller)
                ? puller.Pulling
                : null;
            var patientPuller = entities.TryGetComponent<PullableComponent>(patient, out var pullable)
                ? pullable.Puller
                : null;
            var diagnostic =
                $"now={timing.CurTime.TotalSeconds:0.000}; retryAt={retryAt.TotalSeconds:0.000}; " +
                $"strap={rescue.AssignedPatientStrap}; pulling={pullingTarget}; patientPuller={patientPuller}; " +
                $"activity={rescue.ActivityContext.Activity}/{rescue.ActivityContext.TerminalStatus}; " +
                $"task={rescue.TaskStage}; status={rescue.LastAutoEvacuationStatus}";
            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.PatientBuckleAttempts.GetValueOrDefault(pairKey),
                    Is.EqualTo(2),
                    diagnostic);
                Assert.That(rescue.TerminalPatientBuckleFailures.ContainsKey(pairKey), Is.True,
                    $"The impossible patient/strap pair must become durable after the bounded budget. {diagnostic}");
                Assert.That(
                    rescue.TerminalPatientBuckleFailures.ContainsKey((patient, unreachableBed)),
                    Is.False,
                    "An unreachable alternate bed did not consume a physical buckle attempt.");
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.Null);
                Assert.That(rescue.SkippedDeliveryTargets.ContainsKey(bed), Is.False,
                    "A pair-specific terminal failure must not globally poison this bed for other patients.");
            });
        });

        // The empty alternate bed exists but has no navigable floor. Once its
        // route probe is authoritatively non-viable, it must not keep this
        // terminal patient in a periodic 45-second reacquire loop.
        var durableBuckleSkipObserved = false;
        for (var i = 0; i < AsyncTickLimit * 3; i++)
        {
            await pair.RunTicksSync(1);
            await Task.Delay(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                durableBuckleSkipObserved =
                    rescue.SkippedTargets.TryGetValue(patient, out var skipUntil) &&
                    skipUntil == TimeSpan.MaxValue;
            });

            if (durableBuckleSkipObserved)
                break;
        }

        Assert.That(durableBuckleSkipObserved, Is.True,
            "An authoritatively exhausted buckle mission must latch a durable patient skip.");
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var agentStillPullsPatient = entities.TryGetComponent<PullerComponent>(agent, out var puller) &&
                                         puller.Pulling == patient;
            var patientStillPulledByAgent = entities.TryGetComponent<PullableComponent>(patient, out var pullable) &&
                                            pullable.Puller == agent;
            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.EvacuatingTarget,
                    requiredHandoff ? Is.EqualTo(patient) : Is.Null);
                Assert.That(
                    rescue.AssignedTarget,
                    requiredHandoff ? Is.EqualTo(patient) : Is.Null);
                Assert.That(
                    rescue.TaskPatientTarget,
                    requiredHandoff ? Is.EqualTo(patient) : Is.Null);
                Assert.That(
                    rescue.RequiredOnboardHandoffPatients.Contains(patient),
                    Is.EqualTo(requiredHandoff));
                Assert.That(rescue.TerminalPatientBuckleFailures.ContainsKey(pairKey), Is.True);
                Assert.That(rescue.SkippedTargets.GetValueOrDefault(patient), Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(agentStillPullsPatient, Is.False);
                Assert.That(patientStillPulledByAgent, Is.False);
                if (!requiredHandoff)
                    Assert.That(rescue.ActivityContext.Target, Is.Not.EqualTo(patient));
            });
        });

        if (requiredHandoff)
        {
            uint heldGeneration = 0;
            System.Threading.CancellationTokenSource sentinelPlanningToken = null;
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var htn = entities.GetComponent<HTNComponent>(agent);
                Assert.Multiple(() =>
                {
                    Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Handoff));
                    Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                    Assert.That(rescue.ActivityContext.Fallback, Is.EqualTo(LuaMRescueActivity.Handoff));
                    Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget), Is.False);
                });

                heldGeneration = rescue.ActivityContext.Generation;
                entities.RemoveComponent<ActiveNPCComponent>(agent);
                htn.PlanningToken?.Cancel();
                htn.PlanningToken?.Dispose();
                htn.PlanningJob = null;
                sentinelPlanningToken = new System.Threading.CancellationTokenSource();
                htn.PlanningToken = sentinelPlanningToken;
            });

            await pair.RunTicksSync(3);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var htn = entities.GetComponent<HTNComponent>(agent);
                Assert.Multiple(() =>
                {
                    Assert.That(htn.PlanningToken, Is.SameAs(sentinelPlanningToken),
                        "Refreshing an already-held Required handoff must not cancel/replan HTN movement again.");
                    Assert.That(sentinelPlanningToken!.IsCancellationRequested, Is.False);
                    Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(heldGeneration),
                        "Refreshing canonical Required custody must not replace the blocked Handoff intent.");
                    Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                    Assert.That(rescue.EvacuatingTarget, Is.EqualTo(patient));
                });
            });
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RequiredHandoffRetriesWhenDisabledAlternatePatientBedBecomesAvailable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var mobState = entities.System<MobStateSystem>();
        var pulling = entities.System<PullingSystem>();
        var buckle = entities.System<BuckleSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid primaryBed = default;
        EntityUid alternateBed = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, -2, 4);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 1.5f, 0.5f));
            primaryBed = entities.SpawnEntity("MedicalBed", GridCoordinates(map.Grid, 1.5f, 0.5f));
            alternateBed = entities.SpawnEntity("MedicalBed", GridCoordinates(map.Grid, 2.5f, 0.5f));
            // This test owns the evacuation state. Vacuum/barotrauma updates on
            // the bare test grid can otherwise incapacitate the rescuer before
            // the first pair-scoped buckle attempt, making the assertion depend
            // on system ordering in the full integration suite.
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            mobState.ChangeMobState(patient, MobState.Critical);

            Assert.That(pulling.TryStartPull(agent, patient), Is.True);
            entities.EnsureComponent<TetheredComponent>(patient);
            buckle.StrapSetEnabled(alternateBed, false);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.01f;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AutoUnbucklePatientsForEvacuation = false;
            rescue.AutoRouteShuttleToTargets = false;
            rescue.AutoReturnShuttle = false;
            rescue.EvacuateTargetsToShuttle = true;
            rescue.BucklePatientsOnShuttle = true;
            rescue.AssignedShuttle = map.Grid;
            rescue.AssignedShuttleAnchor = primaryBed;
            rescue.AssignedTarget = patient;
            rescue.EvacuatingTarget = patient;
            rescue.TaskPatientTarget = patient;
            rescue.TaskStage = LuaMRescueTaskStage.EvacuatingPatient;
            rescue.ActivityRoleProfile.MaxAttempts = 1;
            rescue.RequiredOnboardHandoffPatients.Add(patient);
        });

        var primaryPair = (Patient: patient, Strap: primaryBed);
        var terminalPrimaryObserved = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await pair.RunTicksSync(1);
            await Task.Delay(1);
            await server.WaitAssertion(() =>
            {
                terminalPrimaryObserved = entities
                    .GetComponent<LuaMRescueAgentComponent>(agent)
                    .TerminalPatientBuckleFailures
                    .ContainsKey(primaryPair);
            });

            if (terminalPrimaryObserved)
                break;
        }

        Assert.That(terminalPrimaryObserved, Is.True,
            "The primary patient/bed pair never reached its bounded terminal buckle failure.");

        uint blockedGeneration = 0;
        var canonicalTemporaryHoldObserved = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await pair.RunTicksSync(1);
            await Task.Delay(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                canonicalTemporaryHoldObserved =
                    rescue.ActivityContext.Activity == LuaMRescueActivity.Handoff &&
                    rescue.ActivityContext.Target == patient &&
                    rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Blocked &&
                    rescue.AssignedTarget == patient &&
                    rescue.EvacuatingTarget == patient &&
                    rescue.AssignedPatientStrap == null;
                if (canonicalTemporaryHoldObserved)
                    blockedGeneration = rescue.ActivityContext.Generation;
            });

            if (canonicalTemporaryHoldObserved)
                break;
        }

        Assert.That(canonicalTemporaryHoldObserved, Is.True,
            "A temporarily disabled alternate bed did not enter canonical Required custody.");
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.SkippedTargets.TryGetValue(patient, out var skipUntil) &&
                            skipUntil == TimeSpan.MaxValue,
                    Is.False,
                    "A disabled alternate is recoverable and must not permanently latch the patient.");
                Assert.That(rescue.TerminalPatientBuckleFailures.ContainsKey(primaryPair), Is.True);
                Assert.That(
                    rescue.TerminalPatientBuckleFailures.ContainsKey((patient, alternateBed)),
                    Is.False);
            });

            buckle.StrapSetEnabled(alternateBed, true);
        });

        var alternatePair = (Patient: patient, Strap: alternateBed);
        var alternateAttemptObserved = false;
        for (var i = 0; i < AsyncTickLimit * 3; i++)
        {
            await pair.RunTicksSync(1);
            await Task.Delay(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                alternateAttemptObserved = rescue.PatientBuckleAttempts.GetValueOrDefault(alternatePair) >= 1;
            });

            if (alternateAttemptObserved)
                break;
        }

        Assert.That(alternateAttemptObserved, Is.True,
            "The Required handoff did not automatically resume after the alternate bed was enabled.");
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.ActivityContext.Generation, Is.GreaterThan(blockedGeneration));
                Assert.That(rescue.TerminalPatientBuckleFailures.ContainsKey(primaryPair), Is.True,
                    "Resuming on an alternate bed must preserve the terminal primary-pair exclusion.");
                Assert.That(rescue.RequiredOnboardHandoffPatients.Contains(patient), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PatientLifecycleSynchronouslyPurgesControlledAgentStateWithoutUidInheritance()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var doAfter = entities.System<SharedDoAfterSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var bed = entities.SpawnEntity("MedicalBed", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var htn = entities.GetComponent<HTNComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.AssignedShuttle = map.Grid;
            rescue.AssignedShuttleAnchor = bed;
            entities.EnsureComponent<ActorComponent>(agent);

            (uint ActivityGeneration, uint ManualGeneration) SeedPatientState(EntityUid target)
            {
                if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
                {
                    coordinator.Cancel(
                        agent,
                        rescue.ActivityContext.Generation,
                        LuaMRescueFailureReason.Cancelled,
                        out _);
                }

                Assert.That(
                    coordinator.BeginOrReplaceIntent(
                        agent,
                        LuaMRescueRole.Aibolit,
                        LuaMRescueActivity.Handoff,
                        target,
                        new EntityCoordinates(target, Vector2.Zero),
                        out var active),
                    Is.True);
                Assert.That(
                    coordinator.Block(
                        agent,
                        active.Generation,
                        LuaMRescueFailureReason.ShuttleUnavailable,
                        LuaMRescueActivity.Handoff,
                        out var blocked),
                    Is.True);

                rescue.ManualOverrideGeneration++;
                rescue.ManualOverrideTarget = target;
                rescue.AssignedTarget = target;
                rescue.DeathSignalTarget = target;
                rescue.DeathSignalDispatchReported = true;
                rescue.ArrivalReportedTarget = target;
                rescue.TriageReportedTarget = target;
                rescue.ShuttleRoutedTarget = target;
                rescue.EvacuatingTarget = target;
                rescue.OnboardCareTarget = target;
                rescue.AssignedPatientStrap = bed;
                rescue.TaskStage = LuaMRescueTaskStage.DeliveringPatient;
                rescue.TaskPatientTarget = target;
                rescue.TaskSupplyTarget = bed;
                rescue.ProgressTarget = target;
                rescue.ProgressGoal = bed;
                rescue.RouteBlockHoldTarget = target;
                rescue.RouteBlockHoldGoal = bed;
                rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Treat;
                rescue.PendingPlayerActionTarget = target;
                rescue.PendingMedicalDoAfterTarget = target;
                rescue.PendingMedicalIntentGeneration = blocked.Generation;
                rescue.DormantRouteTarget = target;
                rescue.DormantRouteProbeCount = 4;
                rescue.DormantRouteResumeCount = 2;
                rescue.SkippedTargets[target] = TimeSpan.MaxValue;
                rescue.DeferredPatientTargets[target] = TimeSpan.MaxValue;
                rescue.RouteFailureAttempts[target] = 3;
                rescue.AnalyzedTargets[target] = TimeSpan.MaxValue;
                rescue.AnalysisAttempts[target] = 2;
                rescue.TerminalAnalysisFailures[target] = "stale analyzer";
                rescue.TreatmentAttempts[target] = 2;
                rescue.TerminalTreatmentFailures[target] = "stale treatment";
                rescue.TerminalTreatmentFailureDamage[target] = 50f;
                rescue.DefibrillationAttempts[target] = 2;
                rescue.CompletedDefibrillationFailures[target] = 1;
                rescue.DefibrillationStartedAt[target] = TimeSpan.MaxValue;
                rescue.TerminalDefibrillationFailures[target] = "stale defib";
                rescue.PullAttempts[target] = 2;
                rescue.NextPullAttemptAt[target] = TimeSpan.MaxValue;
                rescue.TerminalPullFailures[target] = "stale pull";
                rescue.EvacuationUnbuckleAttempts[target] = 2;
                rescue.NextEvacuationUnbuckleAttemptAt[target] = TimeSpan.MaxValue;
                rescue.TerminalEvacuationUnbuckleFailures[target] = "stale unbuckle";
                rescue.PatientBuckleAttempts[(target, bed)] = 2;
                rescue.NextPatientBuckleAttemptAt[(target, bed)] = TimeSpan.MaxValue;
                rescue.TerminalPatientBuckleFailures[(target, bed)] = "stale buckle";
                rescue.OnboardCareAttempts[target] = 2;
                rescue.TerminalOnboardCareFailures[target] = "stale onboard";
                rescue.OnboardHandoffAttempts[target] = 2;
                rescue.IgnoredOnboardPatients.Add(target);
                rescue.RequiredOnboardHandoffPatients.Add(target);
                htn.Blackboard.SetValue(
                    NPCBlackboard.FollowTarget,
                    new EntityCoordinates(target, Vector2.Zero));
                return (blocked.Generation, rescue.ManualOverrideGeneration);
            }

            void AssertPatientStatePurged(
                EntityUid target,
                uint previousActivityGeneration,
                uint previousManualGeneration)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(previousActivityGeneration + 1));
                    Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Standby));
                    Assert.That(rescue.ActivityContext.Target, Is.Null);
                    Assert.That(rescue.ActivityContext.Destination, Is.Null);
                    Assert.That(rescue.ManualOverrideTarget, Is.Null);
                    Assert.That(rescue.ManualOverrideGeneration, Is.EqualTo(previousManualGeneration + 1));
                    Assert.That(rescue.AssignedTarget, Is.Null);
                    Assert.That(rescue.DeathSignalTarget, Is.Null);
                    Assert.That(rescue.DeathSignalDispatchReported, Is.False);
                    Assert.That(rescue.ArrivalReportedTarget, Is.Null);
                    Assert.That(rescue.TriageReportedTarget, Is.Null);
                    Assert.That(rescue.ShuttleRoutedTarget, Is.Null);
                    Assert.That(rescue.EvacuatingTarget, Is.Null);
                    Assert.That(rescue.OnboardCareTarget, Is.Null);
                    Assert.That(rescue.AssignedPatientStrap, Is.Null);
                    Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.None));
                    Assert.That(rescue.TaskPatientTarget, Is.Null);
                    Assert.That(rescue.TaskSupplyTarget, Is.Null);
                    Assert.That(rescue.ProgressTarget, Is.Null);
                    Assert.That(rescue.ProgressGoal, Is.Null);
                    Assert.That(rescue.RouteBlockHoldTarget, Is.Null);
                    Assert.That(rescue.RouteBlockHoldGoal, Is.Null);
                    Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
                    Assert.That(rescue.PendingPlayerActionTarget, Is.Null);
                    Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                    Assert.That(rescue.DormantRouteTarget, Is.Null);
                    Assert.That(rescue.DormantRouteProbeCount, Is.Zero);
                    Assert.That(rescue.DormantRouteResumeCount, Is.Zero);
                    Assert.That(rescue.SkippedTargets.ContainsKey(target), Is.False);
                    Assert.That(rescue.DeferredPatientTargets.ContainsKey(target), Is.False);
                    Assert.That(rescue.RouteFailureAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.AnalyzedTargets.ContainsKey(target), Is.False);
                    Assert.That(rescue.AnalysisAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalAnalysisFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.TreatmentAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalTreatmentFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalTreatmentFailureDamage.ContainsKey(target), Is.False);
                    Assert.That(rescue.DefibrillationAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.CompletedDefibrillationFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.DefibrillationStartedAt.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalDefibrillationFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.PullAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.NextPullAttemptAt.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalPullFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.EvacuationUnbuckleAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.NextEvacuationUnbuckleAttemptAt.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalEvacuationUnbuckleFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.PatientBuckleAttempts.Keys.Any(pair => pair.Patient == target), Is.False);
                    Assert.That(rescue.NextPatientBuckleAttemptAt.Keys.Any(pair => pair.Patient == target), Is.False);
                    Assert.That(rescue.TerminalPatientBuckleFailures.Keys.Any(pair => pair.Patient == target), Is.False);
                    Assert.That(rescue.OnboardCareAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.TerminalOnboardCareFailures.ContainsKey(target), Is.False);
                    Assert.That(rescue.OnboardHandoffAttempts.ContainsKey(target), Is.False);
                    Assert.That(rescue.IgnoredOnboardPatients.Contains(target), Is.False);
                    Assert.That(rescue.RequiredOnboardHandoffPatients.Contains(target), Is.False);
                    Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget), Is.False);
                    Assert.That(rescue.AssignedShuttle, Is.EqualTo((EntityUid?) map.Grid));
                    Assert.That(rescue.AssignedShuttleAnchor, Is.EqualTo(bed));
                });
            }

            var terminatingTarget = entities.SpawnEntity("MobHuman", map.MapCoords);
            var terminatingState = SeedPatientState(terminatingTarget);
            entities.DeleteEntity(terminatingTarget);
            AssertPatientStatePurged(
                terminatingTarget,
                terminatingState.ActivityGeneration,
                terminatingState.ManualGeneration);

            var startupTarget = entities.SpawnEntity(null, map.MapCoords);
            var startupState = SeedPatientState(startupTarget);
            entities.EnsureComponent<MobStateComponent>(startupTarget);
            AssertPatientStatePurged(
                startupTarget,
                startupState.ActivityGeneration,
                startupState.ManualGeneration);

            var shutdownState = SeedPatientState(startupTarget);
            entities.RemoveComponent<MobStateComponent>(startupTarget);
            AssertPatientStatePurged(
                startupTarget,
                shutdownState.ActivityGeneration,
                shutdownState.ManualGeneration);

            entities.DeleteEntity(startupTarget);

            // Passive lineage for one patient may coexist with an authoritative
            // active mission for another. Purging the passive identity must not
            // cancel the active patient's movement or erase its shared state.
            var passiveTarget = entities.SpawnEntity("MobHuman", map.MapCoords);
            var activeTarget = entities.SpawnEntity("MobHuman", map.MapCoords);
            if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
            {
                coordinator.Cancel(
                    agent,
                    rescue.ActivityContext.Generation,
                    LuaMRescueFailureReason.Cancelled,
                    out _);
            }

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    activeTarget,
                    new EntityCoordinates(activeTarget, Vector2.Zero),
                    out var activeMission),
                Is.True);
            rescue.AssignedTarget = activeTarget;
            rescue.EvacuatingTarget = activeTarget;
            rescue.AssignedPatientStrap = bed;
            rescue.TaskStage = LuaMRescueTaskStage.DeliveringPatient;
            rescue.TaskPatientTarget = activeTarget;
            rescue.TaskSupplyTarget = bed;
            rescue.ProgressTarget = activeTarget;
            rescue.ProgressGoal = bed;
            rescue.RouteBlockHoldTarget = activeTarget;
            rescue.RouteBlockHoldGoal = bed;
            rescue.RequiredOnboardHandoffPatients.Add(activeTarget);
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(activeTarget, Vector2.Zero));

            rescue.ManualOverrideGeneration++;
            var passiveManualGeneration = rescue.ManualOverrideGeneration;
            rescue.ManualOverrideTarget = passiveTarget;
            rescue.DeathSignalTarget = passiveTarget;
            rescue.ArrivalReportedTarget = passiveTarget;
            rescue.TriageReportedTarget = passiveTarget;
            rescue.ShuttleRoutedTarget = passiveTarget;
            rescue.DormantRouteTarget = passiveTarget;
            rescue.DormantRouteProbeCount = 4;
            rescue.DormantRouteResumeCount = 2;
            rescue.SkippedTargets[passiveTarget] = TimeSpan.MaxValue;
            rescue.DeferredPatientTargets[passiveTarget] = TimeSpan.MaxValue;
            rescue.RouteFailureAttempts[passiveTarget] = 3;
            rescue.TerminalTreatmentFailures[passiveTarget] = "passive treatment";
            rescue.TerminalPatientBuckleFailures[(passiveTarget, bed)] = "passive buckle";
            rescue.IgnoredOnboardPatients.Add(passiveTarget);
            rescue.RequiredOnboardHandoffPatients.Add(passiveTarget);
            rescue.PendingMedicalDoAfterTarget = passiveTarget;
            rescue.PendingMedicalDoAfterKind = "passive tracked action";

            Assert.That(
                doAfter.TryStartDoAfter(
                    new DoAfterArgs(
                        entities,
                        agent,
                        TimeSpan.FromMinutes(1),
                        new HealthAnalyzerDoAfterEvent(),
                        agent,
                        target: activeTarget)
                    {
                        BreakOnMove = false,
                        RequireCanInteract = false,
                        BlockDuplicate = false,
                    },
                    out var activePatientDoAfter),
                Is.True);
            Assert.That(activePatientDoAfter, Is.Not.Null);

            htn.PlanningToken?.Cancel();
            htn.PlanningToken?.Dispose();
            htn.PlanningJob = null;
            var activePlanningToken = new System.Threading.CancellationTokenSource();
            htn.PlanningToken = activePlanningToken;

            entities.DeleteEntity(passiveTarget);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(activeMission.Generation));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(activeTarget));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(activeTarget));
                Assert.That(rescue.EvacuatingTarget, Is.EqualTo(activeTarget));
                Assert.That(rescue.AssignedPatientStrap, Is.EqualTo(bed));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(activeTarget));
                Assert.That(rescue.TaskSupplyTarget, Is.EqualTo(bed));
                Assert.That(rescue.ProgressTarget, Is.EqualTo(activeTarget));
                Assert.That(rescue.ProgressGoal, Is.EqualTo(bed));
                Assert.That(rescue.RouteBlockHoldTarget, Is.EqualTo(activeTarget));
                Assert.That(rescue.RouteBlockHoldGoal, Is.EqualTo(bed));
                Assert.That(rescue.RequiredOnboardHandoffPatients.Contains(activeTarget), Is.True);
                Assert.That(rescue.RequiredOnboardHandoffPatients.Contains(passiveTarget), Is.False);
                Assert.That(rescue.ManualOverrideTarget, Is.Null);
                Assert.That(rescue.ManualOverrideGeneration, Is.Not.EqualTo(passiveManualGeneration));
                Assert.That(rescue.DeathSignalTarget, Is.Null);
                Assert.That(rescue.DormantRouteTarget, Is.Null);
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.SkippedTargets.ContainsKey(passiveTarget), Is.False);
                Assert.That(rescue.DeferredPatientTargets.ContainsKey(passiveTarget), Is.False);
                Assert.That(rescue.TerminalTreatmentFailures.ContainsKey(passiveTarget), Is.False);
                Assert.That(
                    rescue.TerminalPatientBuckleFailures.Keys.Any(pair => pair.Patient == passiveTarget),
                    Is.False);
                Assert.That(htn.PlanningToken, Is.SameAs(activePlanningToken));
                Assert.That(activePlanningToken.IsCancellationRequested, Is.False);
                Assert.That(
                    entities.GetComponent<DoAfterComponent>(agent)
                        .DoAfters[activePatientDoAfter!.Value.Index].Cancelled,
                    Is.False,
                    "Purging passive patient A must not cancel a controlled medical action on active patient B.");
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var activeFollow,
                        entities) && activeFollow.EntityId == activeTarget,
                    Is.True);
            });

            // The inverse stale-mirror case is just as important: a real action
            // on lifecycle identity A must be cancelled even when the mirror has
            // already advanced to active patient B. B's action and tracking stay.
            var staleActionTarget = entities.SpawnEntity("MobHuman", map.MapCoords);
            rescue.PendingMedicalDoAfterTarget = activeTarget;
            rescue.PendingMedicalDoAfterKind = "active tracked action";
            rescue.PendingMedicalIntentGeneration = activeMission.Generation;
            Assert.That(
                doAfter.TryStartDoAfter(
                    new DoAfterArgs(
                        entities,
                        agent,
                        TimeSpan.FromMinutes(1),
                        new HealthAnalyzerDoAfterEvent(),
                        agent,
                        target: staleActionTarget)
                    {
                        BreakOnMove = false,
                        RequireCanInteract = false,
                        BlockDuplicate = false,
                    },
                    out var stalePatientDoAfter),
                Is.True);
            Assert.That(stalePatientDoAfter, Is.Not.Null);

            entities.DeleteEntity(staleActionTarget);
            Assert.Multiple(() =>
            {
                var currentDoAfters = entities.GetComponent<DoAfterComponent>(agent).DoAfters;
                Assert.That(
                    currentDoAfters[stalePatientDoAfter!.Value.Index].Cancelled,
                    Is.True,
                    "Lifecycle purge must cancel the authoritative medical action on patient A.");
                Assert.That(
                    currentDoAfters[activePatientDoAfter!.Value.Index].Cancelled,
                    Is.False,
                    "Cancelling patient A must not cancel the active medical action on patient B.");
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(activeTarget));
                Assert.That(rescue.PendingMedicalDoAfterKind, Is.EqualTo("active tracked action"));
                Assert.That(rescue.PendingMedicalIntentGeneration, Is.EqualTo(activeMission.Generation));
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(activeMission.Generation));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(activeTarget));
                Assert.That(htn.PlanningToken, Is.SameAs(activePlanningToken));
                Assert.That(activePlanningToken.IsCancellationRequested, Is.False);
            });

            doAfter.Cancel(activePatientDoAfter!.Value);
            entities.DeleteEntity(activeTarget);
            entities.DeleteEntity(bed);
            entities.DeleteEntity(agent);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeadAibolitDoesNotBlockSpawningNextActiveSingleton()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var deadAgent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            mobState.ChangeMobState(deadAgent, MobState.Dead);

            Assert.That(agentSystem.TryFindActiveAgent(out _, out _), Is.False);
            Assert.That(
                agentSystem.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out var replacement,
                    out var status),
                Is.True,
                status);

            Assert.Multiple(() =>
            {
                Assert.That(replacement, Is.Not.EqualTo(deadAgent));
                Assert.That(agentSystem.TryFindActiveAgent(out var active, out _), Is.True);
                Assert.That(active, Is.EqualTo(replacement));
                Assert.That(entities.GetComponent<MobStateComponent>(active).CurrentState,
                    Is.EqualTo(MobState.Alive));
                Assert.That(entities.HasComponent<LuaMRescuePersonnelComponent>(deadAgent), Is.True);
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        replacement,
                        deadAgent,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var failure),
                    Is.False);
                Assert.That(failure, Is.EqualTo(LuaMRescueFailureReason.InvalidPatient));
                Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(deadAgent), Is.False,
                    "Direct spawn must retire the old active-role component before replacement.");
            });

            mobState.ChangeMobState(deadAgent, MobState.Alive);
            Assert.Multiple(() =>
            {
                Assert.That(agentSystem.TryFindActiveAgent(out var activeAfterRevival, out _), Is.True);
                Assert.That(activeAfterRevival, Is.EqualTo(replacement));
                Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(deadAgent), Is.False,
                    "Reviving retired personnel must not create a second active Aibolit.");
                Assert.That(entities.EntityQuery<LuaMRescueAgentComponent>().Count(), Is.EqualTo(1));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CriticalAibolitStillOwnsSingletonUntilStabilizedOrRetired()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var criticalAgent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            mobState.ChangeMobState(criticalAgent, MobState.Critical);

            Assert.That(agentSystem.TryFindActiveAgent(out var active, out _), Is.True);
            Assert.That(active, Is.EqualTo(criticalAgent));
            Assert.That(
                agentSystem.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out var blockedReplacement,
                    out var status),
                Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(blockedReplacement, Is.EqualTo(criticalAgent));
                Assert.That(status, Does.Contain("Only one Aibolit"));
                Assert.That(
                    entities.EntityQuery<LuaMRescueAgentComponent>().Count(),
                    Is.EqualTo(1));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CriticalAibolitResumesDeferredCrossGridPatientAfterStabilization()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(map.Grid, 0.5f, 0.5f));
            patient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(100f, 100f), map.MapId));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 20);
            Assert.That(
                damageable.TryChangeDamage(patient, damage, ignoreResistances: true),
                Is.Not.Null);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out var active),
                Is.True);
            Assert.That(active.Target, Is.EqualTo(patient));
            Assert.That(entities.GetComponent<TransformComponent>(agent).GridUid,
                Is.Not.EqualTo(entities.GetComponent<TransformComponent>(patient).GridUid));

            mobState.ChangeMobState(agent, MobState.Critical);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DeferredPatientTargets.ContainsKey(patient), Is.True);
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.LastDeferredPatientStatus, Does.Contain("while rescue agent was Critical"));
            });
            mobState.ChangeMobState(agent, MobState.Alive);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var resumed), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DeferredPatientTargets.ContainsKey(patient), Is.False);
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(resumed.Target, Is.EqualTo(patient));
                Assert.That(resumed.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.LastDeferredPatientStatus, Does.Contain("resuming deferred"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeadActiveOwnerTransfersAutomaticPatientToReplacementQueue()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var deadAgent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            var patient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(20f, 0f), map.MapId));
            entities.RemoveComponent<BarotraumaComponent>(patient);
            mobState.ChangeMobState(patient, MobState.Critical);

            var deadRescue = entities.GetComponent<LuaMRescueAgentComponent>(deadAgent);
            deadRescue.AssignedTarget = patient;
            deadRescue.TaskPatientTarget = patient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    deadAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True);
            mobState.ChangeMobState(deadAgent, MobState.Dead);

            Assert.That(
                agentSystem.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out var replacement,
                    out var spawnStatus),
                Is.True,
                spawnStatus);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(deadAgent), Is.False);
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.True,
                    "Retirement must transfer the consumed automatic mission before removing its owner.");
            });

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            var replacementRescue = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            Assert.That(coordinator.GetSnapshot(replacement, out var transferred), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
                Assert.That(replacementRescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(replacementRescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(transferred.Target, Is.EqualTo(patient));
                Assert.That(transferred.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeletedActiveOwnerTransfersAutomaticPatientToReplacementQueue()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var deletedAgent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            var patient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(20f, 0f), map.MapId));
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<NpcFactionMemberComponent>(patient);
            mobState.ChangeMobState(patient, MobState.Critical);

            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    deletedAgent,
                    patient,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var concreteFailure),
                Is.True,
                concreteFailure.ToString());
            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    EntityUid.Invalid,
                    patient,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var stationWideFailure),
                Is.False);
            Assert.That(stationWideFailure, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh));

            var deletedRescue = entities.GetComponent<LuaMRescueAgentComponent>(deletedAgent);
            deletedRescue.AssignedTarget = patient;
            deletedRescue.TaskPatientTarget = patient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    deletedAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True);

            entities.DeleteEntity(deletedAgent);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Count(entry => entry.Target == patient),
                Is.EqualTo(1),
                "Entity termination must transfer the consumed mission exactly once.");

            Assert.That(
                agentSystem.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out var replacement,
                    out var spawnStatus),
                Is.True,
                spawnStatus);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);

            var replacementRescue = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            Assert.That(coordinator.GetSnapshot(replacement, out var transferred), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
                Assert.That(replacementRescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(replacementRescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(transferred.Target, Is.EqualTo(patient));
                Assert.That(transferred.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertRejectedWithoutPull(
        IEntityManager entities,
        LuaMRescueActivityCoordinatorSystem coordinator,
        EntityUid agent,
        EntityUid target,
        LuaMRescueFailureReason expectedReason)
    {
        var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
        Assert.That(coordinator.GetSnapshot(agent, out var activity), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(rescue.AssignedTarget, Is.Null);
            Assert.That(rescue.EvacuatingTarget, Is.Null);
            Assert.That(rescue.TaskPatientTarget, Is.Null);
            Assert.That(activity.Target, Is.EqualTo(target));
            Assert.That(activity.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
            Assert.That(activity.Blocked, Is.True);
            Assert.That(activity.FailureReason, Is.EqualTo(expectedReason));
            Assert.That(activity.Fallback, Is.EqualTo(LuaMRescueActivity.Handoff));
            Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.Null);
            Assert.That(entities.GetComponent<PullableComponent>(target).Puller, Is.Null);
        });
    }

    private static EntityUid SpawnAgent(IEntityManager entities, MapId mapId, Vector2 position)
    {
        var agent = entities.SpawnEntity("LuaMRescueAgent", new MapCoordinates(position, mapId));
        entities.GetComponent<LuaMRescueAgentComponent>(agent).AutoAcquireTargets = false;
        return agent;
    }

    private static void BuildHorizontalFloor(
        SharedMapSystem mapSystem,
        TestMapData map,
        int minimumX,
        int maximumX)
    {
        for (var x = minimumX; x <= maximumX; x++)
            mapSystem.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);
    }

    private static EntityCoordinates GridCoordinates(Entity<MapGridComponent> grid, float x, float y)
    {
        return new EntityCoordinates(grid.Owner, new Vector2(x, y));
    }

    private static async Task AwaitReachableRoute(
        TestPair pair,
        LuaMRescueNavigationSystem navigation,
        EntityUid agent,
        EntityUid target)
    {
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            LuaMRescuePathProbeSnapshot snapshot = default;
            await pair.Server.WaitAssertion(() =>
            {
                snapshot = navigation.ProbeRoute(agent, target, RescueActionRange);
            });

            if (snapshot.State == LuaMRescuePathProbeState.Reachable)
                return;

            Assert.That(snapshot.State, Is.EqualTo(LuaMRescuePathProbeState.Pending));
            await pair.RunTicksSync(1);
        }

        Assert.Fail($"Route {agent} -> {target} remained Pending for {AsyncTickLimit} server ticks.");
    }

    private static async Task<LuaMRescuePathProbeSnapshot> AwaitRouteState(
        TestPair pair,
        LuaMRescueNavigationSystem navigation,
        EntityUid agent,
        EntityUid target,
        LuaMRescuePathProbeState expected)
    {
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            LuaMRescuePathProbeSnapshot snapshot = default;
            await pair.Server.WaitAssertion(() =>
            {
                snapshot = navigation.ProbeRoute(agent, target, RescueActionRange);
            });

            if (snapshot.State == expected)
                return snapshot;

            Assert.That(snapshot.State, Is.EqualTo(LuaMRescuePathProbeState.Pending));
            await pair.RunTicksSync(1);
        }

        Assert.Fail($"Route {agent} -> {target} did not become {expected} in {AsyncTickLimit} server ticks.");
        return default;
    }

    private static async Task<bool> AwaitSteeringTarget(
        TestPair pair,
        IEntityManager entities,
        EntityUid agent,
        EntityUid target)
    {
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            var matches = false;
            await pair.Server.WaitAssertion(() =>
            {
                matches = entities.TryGetComponent<NPCSteeringComponent>(agent, out var steering) &&
                          steering.Coordinates.EntityId == target;
            });

            if (matches)
                return true;

            await pair.RunTicksSync(1);
        }

        return false;
    }
}
