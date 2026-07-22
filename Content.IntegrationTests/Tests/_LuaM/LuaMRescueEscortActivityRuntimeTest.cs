using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using Content.Server.Atmos.Components;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server._LuaM.Rescue;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueTeamSystem))]
public sealed class LuaMRescueEscortActivityRuntimeTest
{
    [Test]
    public async Task ObserverFactionOwnsHostilityDecisionInsteadOfNanoTrasenDefault()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var factions = entities.System<NpcFactionSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var observer = entities.SpawnEntity(null, map.MapCoords);
            var candidate = entities.SpawnEntity(null, map.MapCoords);
            entities.EnsureComponent<NpcFactionMemberComponent>(observer);
            entities.EnsureComponent<NpcFactionMemberComponent>(candidate);
            factions.AddFaction(observer, "Syndicate");
            factions.AddFaction(candidate, "NanoTrasen");

            Assert.Multiple(() =>
            {
                Assert.That(factions.IsEntityFriendly(observer, candidate), Is.False);
                Assert.That(factions.IsEntityHostile(observer, candidate), Is.True,
                    "A Syndicate observer must evaluate its own hostility toward NanoTrasen.");
                Assert.That(factions.IsEntityHostile(candidate, observer), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NoProgressDeadlineIsTerminalAndSameDutyDoesNotChurnGeneration()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var pulling = entities.System<PullingSystem>();
        var mobState = entities.System<MobStateSystem>();
        var teamSystem = entities.System<LuaMRescueTeamSystem>();
        var escortMap = await pair.CreateTestMap();
        var patientMap = await pair.CreateTestMap();

        EntityUid escort = default;
        EntityUid shuttleAnchor = default;
        CancellationTokenSource stalePlanning = null!;

        await server.WaitAssertion(() =>
        {
            var leader = entities.SpawnEntity("MobHuman", escortMap.MapCoords);
            shuttleAnchor = entities.SpawnEntity(null, new MapCoordinates(new Vector2(1f, 0f), escortMap.MapId));
            var patient = entities.SpawnEntity("MobHuman", patientMap.MapCoords);
            MakePatientCritical(entities, mobState, patient);
            var oldPullTarget = entities.SpawnEntity("MobHuman", escortMap.MapCoords);
            var team = entities.EnsureComponent<LuaMRescueTeamComponent>(leader);
            ConfigureTeam(team, leader, patient, shuttleAnchor);

            escort = entities.SpawnEntity("LuaMRescueEscort", escortMap.MapCoords);
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            ConfigureEscort(escortComp, leader);

            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Kostyl;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
            carrier.ActivityRoleProfile.MaxAttempts = 10;
            Assert.That(
                carrier.ActivityRoleProfile.TryGetPolicy(
                    LuaMRescueActivity.PreparingEvacuation,
                    out var policy),
                Is.True);
            policy.Timeout = TimeSpan.FromSeconds(0.15);

            var htn = entities.GetComponent<HTNComponent>(escort);
            stalePlanning = new CancellationTokenSource();
            htn.PlanningToken = stalePlanning;
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(oldPullTarget, Vector2.Zero));
            htn.Blackboard.SetValue(NPCBlackboard.CurrentOrderedTarget, oldPullTarget);

            Assert.That(
                pulling.TryStartPull(
                    escort,
                    oldPullTarget,
                    entities.GetComponent<PullerComponent>(escort),
                    entities.GetComponent<PullableComponent>(oldPullTarget)),
                Is.True);
        });

        await pair.RunSeconds(0.5f);

        uint generation = 0;
        TimeSpan deadline = default;
        TimeSpan startedAt = default;
        int transitions = 0;
        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            var htn = entities.GetComponent<HTNComponent>(escort);
            generation = context.Generation;
            deadline = context.Deadline;
            startedAt = context.StartedAt;
            transitions = carrier.IntentTransitions;

            Assert.Multiple(() =>
            {
                Assert.That(stalePlanning, Is.Not.Null);
                Assert.That(stalePlanning!.IsCancellationRequested, Is.True);
                Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.CurrentOrderedTarget), Is.False);
                Assert.That(entities.GetComponent<PullerComponent>(escort).Pulling, Is.Null);
                Assert.That(context.Activity, Is.EqualTo(LuaMRescueActivity.PreparingEvacuation));
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(context.FailureReason, Is.EqualTo(LuaMRescueFailureReason.DeadlineExceeded));
                Assert.That(context.RouteStatus, Is.EqualTo(LuaMRescueRouteStatus.NoPath));
                Assert.That(context.Fallback, Is.EqualTo(LuaMRescueActivity.Returning));
                Assert.That(context.Attempts, Is.EqualTo(1));
                Assert.That(context.Deadline, Is.GreaterThan(context.StartedAt));
                Assert.That(escortComp.CurrentFollowTarget, Is.EqualTo(shuttleAnchor));
            });

            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var fallbackTarget,
                    entities),
                Is.True);
            Assert.That(fallbackTarget.EntityId, Is.EqualTo(shuttleAnchor));

            var status = teamSystem.BuildRescueTeamStatusLines()
                .Single(line => line.Contains($"escort={entities.GetNetEntity(escort)}", StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(status, Does.Contain("activityDeadline="));
                Assert.That(status, Does.Contain("activityAttempts=1/10"));
                Assert.That(status, Does.Contain("activityFailure=DeadlineExceeded"));
                Assert.That(status, Does.Contain("activityFallback=return"));
            });
        });

        await pair.RunSeconds(1.25f);

        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            Assert.Multiple(() =>
            {
                Assert.That(context.Generation, Is.EqualTo(generation));
                Assert.That(context.Deadline, Is.EqualTo(deadline));
                Assert.That(context.StartedAt, Is.EqualTo(startedAt));
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(carrier.IntentTransitions, Is.EqualTo(transitions));
            });
        });

        stalePlanning.Dispose();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InvalidRouteAttemptsAreBackedOffAndBecomeTerminalBlocked()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mobState = entities.System<MobStateSystem>();
        var escortMap = await pair.CreateTestMap();
        var patientMap = await pair.CreateTestMap();

        EntityUid escort = default;
        await server.WaitAssertion(() =>
        {
            var leader = entities.SpawnEntity("MobHuman", escortMap.MapCoords);
            var patient = entities.SpawnEntity("MobHuman", patientMap.MapCoords);
            MakePatientCritical(entities, mobState, patient);
            var team = entities.EnsureComponent<LuaMRescueTeamComponent>(leader);
            ConfigureTeam(team, leader, patient, shuttleAnchor: null);

            escort = entities.SpawnEntity("LuaMRescueEscort", escortMap.MapCoords);
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            ConfigureEscort(escortComp, leader);

            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Kostyl;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
            carrier.ActivityRoleProfile.MaxAttempts = 2;
            carrier.ActivityRoleProfile.BaseRetryBackoff = TimeSpan.FromSeconds(0.05);
            carrier.ActivityRoleProfile.MaxRetryBackoff = TimeSpan.FromSeconds(0.1);
        });

        await pair.RunSeconds(0.35f);

        uint generation = 0;
        TimeSpan deadline = default;
        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            generation = context.Generation;
            deadline = context.Deadline;

            Assert.Multiple(() =>
            {
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(context.Blocked, Is.True);
                Assert.That(context.FailureReason, Is.EqualTo(LuaMRescueFailureReason.NoPath));
                Assert.That(context.RouteStatus, Is.EqualTo(LuaMRescueRouteStatus.NoPath));
                Assert.That(context.Attempts, Is.EqualTo(2));
                Assert.That(context.RetryNotBefore, Is.EqualTo(TimeSpan.Zero));
                Assert.That(context.Fallback, Is.EqualTo(LuaMRescueActivity.Observing));
            });
        });

        await pair.RunSeconds(0.5f);

        await server.WaitAssertion(() =>
        {
            var context = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort).ActivityContext;
            Assert.That(context.Generation, Is.EqualTo(generation));
            Assert.That(context.Deadline, Is.EqualTo(deadline));
            Assert.That(context.Attempts, Is.EqualTo(2));
            Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PatientPullSurvivesApproachToShuttleDestinationChange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid escort = default;
        EntityUid patient = default;
        EntityUid shuttleAnchor = default;
        await server.WaitAssertion(() =>
        {
            var leader = entities.SpawnEntity("MobHuman", map.MapCoords);
            patient = entities.SpawnEntity("MobHuman", map.MapCoords);
            MakePatientCritical(entities, mobState, patient);
            shuttleAnchor = entities.SpawnEntity(null, map.MapCoords);
            var team = entities.EnsureComponent<LuaMRescueTeamComponent>(leader);
            ConfigureTeam(
                team,
                leader,
                patient,
                shuttleAnchor,
                LuaMRescueSortiePlan.EvacuatePatient);

            escort = entities.SpawnEntity("LuaMRescueEscort", map.MapCoords);
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            ConfigureEscort(escortComp, leader);
            escortComp.NextDutyActionAt = TimeSpan.Zero;

            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Kostyl;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
        });

        await pair.RunSeconds(0.4f);

        uint generation = 0;
        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            generation = context.Generation;

            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<PullerComponent>(escort).Pulling, Is.EqualTo(patient));
                Assert.That(entities.GetComponent<PullableComponent>(patient).Puller, Is.EqualTo(escort));
                Assert.That(context.Activity, Is.EqualTo(LuaMRescueActivity.PreparingEvacuation));
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(context.Target, Is.EqualTo(patient), "The primary patient owns the intent.");
                Assert.That(context.Destination?.EntityId, Is.EqualTo(shuttleAnchor),
                    "The route destination may change without replacing the patient intent.");
                Assert.That(context.Destination?.Position, Is.EqualTo(Vector2.Zero),
                    "Direct PatientSupport movement must not retain a formation offset outside pull range.");
                Assert.That(entities.GetComponent<LuaMRescueEscortComponent>(escort).CurrentFollowTarget,
                    Is.EqualTo(shuttleAnchor));
            });
        });

        await pair.RunSeconds(0.4f);

        await server.WaitAssertion(() =>
        {
            var context = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort).ActivityContext;
            Assert.Multiple(() =>
            {
                Assert.That(context.Generation, Is.EqualTo(generation));
                Assert.That(context.Target, Is.EqualTo(patient));
                Assert.That(context.Destination?.EntityId, Is.EqualTo(shuttleAnchor));
                Assert.That(entities.GetComponent<PullerComponent>(escort).Pulling, Is.EqualTo(patient));
            });
        });

        await pair.RunSeconds(1.5f);

        await server.WaitAssertion(() =>
        {
            var context = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort).ActivityContext;
            Assert.Multiple(() =>
            {
                Assert.That(context.Generation, Is.EqualTo(generation));
                Assert.That(context.Target, Is.EqualTo(patient));
                Assert.That(context.Destination?.EntityId, Is.EqualTo(shuttleAnchor));
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Succeeded));
                Assert.That(context.FailureReason, Is.EqualTo(LuaMRescueFailureReason.None));
                Assert.That(entities.GetComponent<PullerComponent>(escort).Pulling, Is.Null);
                Assert.That(entities.GetComponent<PullableComponent>(patient).Puller, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DormantObservationReactivatesExhaustedIntentOnceWithoutGenerationChurn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mobState = entities.System<MobStateSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var escortMap = await pair.CreateTestMap();
        var patientMap = await pair.CreateTestMap();

        EntityUid escort = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            var leader = entities.SpawnEntity("MobHuman", escortMap.MapCoords);
            patient = entities.SpawnEntity("MobHuman", patientMap.MapCoords);
            MakePatientCritical(entities, mobState, patient);
            var team = entities.EnsureComponent<LuaMRescueTeamComponent>(leader);
            ConfigureTeam(team, leader, patient, shuttleAnchor: null);

            escort = entities.SpawnEntity("LuaMRescueEscort", escortMap.MapCoords);
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            ConfigureEscort(escortComp, leader);

            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Kostyl;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
            carrier.ActivityRoleProfile.MaxAttempts = 1;
            carrier.ActivityRoleProfile.BaseRetryBackoff = TimeSpan.FromSeconds(0.05);
            carrier.ActivityRoleProfile.MaxRetryBackoff = TimeSpan.FromSeconds(0.05);
        });

        // Different maps fail synchronously. Wait until both the activity attempt and
        // its one configured active recovery attempt are exhausted.
        await pair.RunSeconds(0.45f);

        uint terminalGeneration = 0;
        int terminalTransitions = 0;
        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            terminalGeneration = context.Generation;
            terminalTransitions = carrier.IntentTransitions;
            Assert.Multiple(() =>
            {
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(context.FailureReason, Is.EqualTo(LuaMRescueFailureReason.NoPath));
                Assert.That(carrier.TerminalRecoveryArmed, Is.True);
                Assert.That(carrier.TerminalRecoveryDormant, Is.True);
                Assert.That(carrier.TerminalRecoveryAttempts, Is.EqualTo(1));
                Assert.That(carrier.NextTerminalRecoveryAt, Is.GreaterThan(server.Timing.CurTime));
            });

            // This models a temporary graph/door obstruction disappearing without
            // changing the duty, target entity, or logical destination.
            transform.SetMapCoordinates(patient, escortMap.MapCoords);
        });

        // Dormant observation is deliberately low-frequency: clearing the obstacle
        // cannot churn the terminal generation before the scheduled route observation.
        await pair.RunSeconds(14f);

        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            Assert.Multiple(() =>
            {
                Assert.That(carrier.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(carrier.ActivityContext.Generation, Is.EqualTo(terminalGeneration));
                Assert.That(carrier.IntentTransitions, Is.EqualTo(terminalTransitions));
                Assert.That(carrier.TerminalRecoveryAttempts, Is.EqualTo(1));
                Assert.That(carrier.TerminalRecoveryDormant, Is.True);
            });
        });

        await pair.RunSeconds(2f);

        uint recoveredGeneration = 0;
        TimeSpan recoveredStartedAt = default;
        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            recoveredGeneration = context.Generation;
            recoveredStartedAt = context.StartedAt;
            Assert.Multiple(() =>
            {
                Assert.That(context.Activity, Is.EqualTo(LuaMRescueActivity.PreparingEvacuation));
                Assert.That(context.Target, Is.EqualTo(patient));
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(context.RouteStatus, Is.EqualTo(LuaMRescueRouteStatus.Arrived));
                Assert.That(context.FailureReason, Is.EqualTo(LuaMRescueFailureReason.None));
                Assert.That(context.Generation, Is.EqualTo(terminalGeneration + 1));
                Assert.That(carrier.IntentTransitions, Is.EqualTo(terminalTransitions + 1));
                Assert.That(carrier.TerminalRecoveryArmed, Is.False);
                Assert.That(carrier.TerminalRecoveryDormant, Is.False);
                Assert.That(carrier.TerminalRecoveryAttempts, Is.Zero);
                Assert.That(carrier.LastTerminalRecoveryStatus, Does.StartWith("recovered"));
                Assert.That(entities.GetComponent<LuaMRescueEscortComponent>(escort).CurrentFollowTarget,
                    Is.EqualTo(patient));
            });
        });

        await pair.RunSeconds(0.5f);

        await server.WaitAssertion(() =>
        {
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            Assert.Multiple(() =>
            {
                Assert.That(carrier.ActivityContext.Generation, Is.EqualTo(recoveredGeneration));
                Assert.That(carrier.ActivityContext.StartedAt, Is.EqualTo(recoveredStartedAt));
                Assert.That(carrier.IntentTransitions, Is.EqualTo(terminalTransitions + 1));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PatientLifecyclePurgesEscortIdentityWithoutCancellingAnotherActivePatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var pulling = entities.System<PullingSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var leader = entities.SpawnEntity("MobHuman", map.MapCoords);
            var activePatient = entities.SpawnEntity("MobHuman", map.MapCoords);
            var firstTarget = entities.SpawnEntity("MobHuman", map.MapCoords);
            var team = entities.EnsureComponent<LuaMRescueTeamComponent>(leader);
            ConfigureTeam(team, leader, firstTarget, shuttleAnchor: null);
            team.Leader = firstTarget;
            team.Shuttle = firstTarget;
            team.ShuttleAnchor = firstTarget;
            team.TriageCoverConfirmedPatient = firstTarget;
            team.LastHandoffPatient = firstTarget;
            team.LastThreatNeutralizedTarget = firstTarget;
            team.LastThreatNeutralizedBy = firstTarget;
            team.SceneAnchor = firstTarget;
            team.ThreatTarget = firstTarget;
            team.CrowdTarget = firstTarget;
            team.RouteBlockerTarget = firstTarget;
            team.SceneMemory.Add(new LuaMRescueSceneMemoryEntry
            {
                Anchor = firstTarget,
                ThreatTarget = firstTarget,
                ExpiresAt = TimeSpan.MaxValue,
            });

            var escortUid = entities.SpawnEntity("LuaMRescueEscort", map.MapCoords);
            team.Escorts.Add(firstTarget);
            team.Escorts.Add(escortUid);
            var escort = entities.GetComponent<LuaMRescueEscortComponent>(escortUid);
            ConfigureEscort(escort, leader);
            escort.Leader = firstTarget;
            escort.Patient = firstTarget;
            escort.Shuttle = firstTarget;
            escort.ShuttleAnchor = firstTarget;
            escort.CurrentFollowTarget = firstTarget;
            escort.SceneAnchor = firstTarget;
            escort.ThreatTarget = firstTarget;
            escort.CrowdTarget = firstTarget;
            escort.RouteBlockerTarget = firstTarget;
            escort.PatientAssistAttemptTarget = firstTarget;
            escort.PatientAssistAttempts = 3;
            escort.NextPatientAssistAttemptAt = TimeSpan.MaxValue;
            escort.PatientHandoffAttemptTarget = firstTarget;
            escort.PatientHandoffAttempts = 3;
            escort.NextPatientHandoffAttemptAt = TimeSpan.MaxValue;
            escort.ClearRoutePullAttemptTarget = firstTarget;
            escort.ClearRoutePullAttempts = 3;
            escort.NextClearRoutePullAttemptAt = TimeSpan.MaxValue;
            escort.ClearRouteReleaseAttemptTarget = firstTarget;
            escort.ClearRouteReleaseAttempts = 3;
            escort.NextClearRouteReleaseAttemptAt = TimeSpan.MaxValue;

            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escortUid);
            carrier.ActivityRole = LuaMRescueRole.Kostyl;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
            carrier.SourceDuty = LuaMRescueEscortDuty.PatientSupport;
            carrier.ActivityContext = new LuaMRescueActivityContext
            {
                Activity = LuaMRescueActivity.PreparingEvacuation,
                TerminalStatus = LuaMRescueTerminalStatus.Active,
                Target = firstTarget,
                Destination = new EntityCoordinates(firstTarget, Vector2.Zero),
                Generation = 10,
            };
            carrier.TerminalRecoveryArmed = true;
            carrier.TerminalRecoveryAttempts = 3;

            var htn = entities.GetComponent<HTNComponent>(escortUid);
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(firstTarget, Vector2.Zero));
            htn.Blackboard.SetValue(NPCBlackboard.CurrentOrderedTarget, firstTarget);
            var firstPlanningToken = new CancellationTokenSource();
            htn.PlanningToken = firstPlanningToken;
            Assert.That(pulling.TryStartPull(escortUid, firstTarget), Is.True);

            entities.DeleteEntity(firstTarget);
            Assert.Multiple(() =>
            {
                Assert.That(team.Patient, Is.Null);
                Assert.That(team.Leader, Is.Null);
                Assert.That(team.Shuttle, Is.Null);
                Assert.That(team.ShuttleAnchor, Is.Null);
                Assert.That(team.TriageCoverConfirmedPatient, Is.Null);
                Assert.That(team.LastHandoffPatient, Is.Null);
                Assert.That(team.LastThreatNeutralizedTarget, Is.Null);
                Assert.That(team.LastThreatNeutralizedBy, Is.Null);
                Assert.That(team.SceneAnchor, Is.Null);
                Assert.That(team.ThreatTarget, Is.Null);
                Assert.That(team.CrowdTarget, Is.Null);
                Assert.That(team.RouteBlockerTarget, Is.Null);
                Assert.That(team.SceneMemory, Is.Empty);
                Assert.That(team.Escorts, Is.EqualTo(new[] { escortUid }));
                Assert.That(escort.Leader, Is.Null);
                Assert.That(escort.Patient, Is.Null);
                Assert.That(escort.Shuttle, Is.Null);
                Assert.That(escort.ShuttleAnchor, Is.Null);
                Assert.That(escort.CurrentFollowTarget, Is.Null);
                Assert.That(escort.SceneAnchor, Is.Null);
                Assert.That(escort.ThreatTarget, Is.Null);
                Assert.That(escort.CrowdTarget, Is.Null);
                Assert.That(escort.RouteBlockerTarget, Is.Null);
                Assert.That(escort.PatientAssistAttemptTarget, Is.Null);
                Assert.That(escort.PatientAssistAttempts, Is.Zero);
                Assert.That(escort.NextPatientAssistAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(escort.PatientHandoffAttemptTarget, Is.Null);
                Assert.That(escort.PatientHandoffAttempts, Is.Zero);
                Assert.That(escort.NextPatientHandoffAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(escort.ClearRoutePullAttemptTarget, Is.Null);
                Assert.That(escort.ClearRoutePullAttempts, Is.Zero);
                Assert.That(escort.NextClearRoutePullAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(escort.ClearRouteReleaseAttemptTarget, Is.Null);
                Assert.That(escort.ClearRouteReleaseAttempts, Is.Zero);
                Assert.That(escort.NextClearRouteReleaseAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(carrier.ActivityContext.Generation, Is.EqualTo(11));
                Assert.That(carrier.ActivityContext.Target, Is.Not.EqualTo(firstTarget));
                Assert.That(carrier.ActivityContext.Destination?.EntityId, Is.Not.EqualTo(firstTarget));
                Assert.That(carrier.TerminalRecoveryAttempts, Is.Zero);
                Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget), Is.False);
                Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.CurrentOrderedTarget), Is.False);
                Assert.That(firstPlanningToken.IsCancellationRequested, Is.True);
                Assert.That(entities.GetComponent<PullerComponent>(escortUid).Pulling, Is.Null);
            });

            var passiveTarget = entities.SpawnEntity("MobHuman", map.MapCoords);
            team.Leader = leader;
            team.Patient = activePatient;
            team.Shuttle = activePatient;
            team.ShuttleAnchor = activePatient;
            team.SceneAnchor = activePatient;
            team.ThreatTarget = activePatient;
            team.TriageCoverConfirmedPatient = passiveTarget;
            team.LastHandoffPatient = passiveTarget;
            team.LastThreatNeutralizedTarget = passiveTarget;
            team.LastThreatNeutralizedBy = passiveTarget;
            team.Escorts.Add(passiveTarget);
            team.SceneMemory.Add(new LuaMRescueSceneMemoryEntry
            {
                Anchor = passiveTarget,
                ExpiresAt = TimeSpan.MaxValue,
            });
            team.SceneMemory.Add(new LuaMRescueSceneMemoryEntry
            {
                Anchor = activePatient,
                ExpiresAt = TimeSpan.MaxValue,
            });

            escort.Leader = leader;
            escort.Patient = activePatient;
            escort.Shuttle = activePatient;
            escort.ShuttleAnchor = activePatient;
            escort.CurrentFollowTarget = activePatient;
            escort.SceneAnchor = activePatient;
            escort.ThreatTarget = activePatient;
            escort.CurrentDuty = LuaMRescueEscortDuty.PatientSupport;
            escort.PendingDuty = LuaMRescueEscortDuty.PatientSupport;
            escort.PatientAssistAttemptTarget = passiveTarget;
            escort.PatientAssistAttempts = 2;
            escort.NextPatientAssistAttemptAt = TimeSpan.MaxValue;
            escort.PatientHandoffAttemptTarget = passiveTarget;
            escort.PatientHandoffAttempts = 2;
            escort.NextPatientHandoffAttemptAt = TimeSpan.MaxValue;
            escort.ClearRoutePullAttemptTarget = passiveTarget;
            escort.ClearRoutePullAttempts = 2;
            escort.NextClearRoutePullAttemptAt = TimeSpan.MaxValue;
            escort.ClearRouteReleaseAttemptTarget = passiveTarget;
            escort.ClearRouteReleaseAttempts = 2;
            escort.NextClearRouteReleaseAttemptAt = TimeSpan.MaxValue;

            carrier.SourceDuty = LuaMRescueEscortDuty.PatientSupport;
            carrier.ActivityContext = new LuaMRescueActivityContext
            {
                Activity = LuaMRescueActivity.PreparingEvacuation,
                TerminalStatus = LuaMRescueTerminalStatus.Active,
                Target = activePatient,
                Destination = new EntityCoordinates(activePatient, Vector2.Zero),
                Generation = 50,
            };
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(activePatient, Vector2.Zero));
            htn.Blackboard.SetValue(NPCBlackboard.CurrentOrderedTarget, activePatient);
            var activePlanningToken = new CancellationTokenSource();
            htn.PlanningToken = activePlanningToken;
            Assert.That(pulling.TryStartPull(escortUid, activePatient), Is.True);

            entities.DeleteEntity(passiveTarget);
            Assert.Multiple(() =>
            {
                Assert.That(team.Patient, Is.EqualTo(activePatient));
                Assert.That(team.Leader, Is.EqualTo(leader));
                Assert.That(team.Shuttle, Is.EqualTo(activePatient));
                Assert.That(team.ShuttleAnchor, Is.EqualTo(activePatient));
                Assert.That(team.SceneAnchor, Is.EqualTo(activePatient));
                Assert.That(team.ThreatTarget, Is.EqualTo(activePatient));
                Assert.That(team.TriageCoverConfirmedPatient, Is.Null);
                Assert.That(team.LastHandoffPatient, Is.Null);
                Assert.That(team.LastThreatNeutralizedTarget, Is.Null);
                Assert.That(team.LastThreatNeutralizedBy, Is.Null);
                Assert.That(team.Escorts.Contains(passiveTarget), Is.False);
                Assert.That(team.Escorts.Contains(escortUid), Is.True);
                Assert.That(team.SceneMemory.Any(memory => memory.Anchor == passiveTarget), Is.False);
                Assert.That(team.SceneMemory.Any(memory => memory.Anchor == activePatient), Is.True);
                Assert.That(escort.Leader, Is.EqualTo(leader));
                Assert.That(escort.Patient, Is.EqualTo(activePatient));
                Assert.That(escort.Shuttle, Is.EqualTo(activePatient));
                Assert.That(escort.ShuttleAnchor, Is.EqualTo(activePatient));
                Assert.That(escort.CurrentFollowTarget, Is.EqualTo(activePatient));
                Assert.That(escort.SceneAnchor, Is.EqualTo(activePatient));
                Assert.That(escort.ThreatTarget, Is.EqualTo(activePatient));
                Assert.That(escort.PatientAssistAttemptTarget, Is.Null);
                Assert.That(escort.PatientAssistAttempts, Is.Zero);
                Assert.That(escort.NextPatientAssistAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(escort.PatientHandoffAttemptTarget, Is.Null);
                Assert.That(escort.PatientHandoffAttempts, Is.Zero);
                Assert.That(escort.NextPatientHandoffAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(escort.ClearRoutePullAttemptTarget, Is.Null);
                Assert.That(escort.ClearRoutePullAttempts, Is.Zero);
                Assert.That(escort.NextClearRoutePullAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(escort.ClearRouteReleaseAttemptTarget, Is.Null);
                Assert.That(escort.ClearRouteReleaseAttempts, Is.Zero);
                Assert.That(escort.NextClearRouteReleaseAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(carrier.ActivityContext.Generation, Is.EqualTo(50));
                Assert.That(carrier.ActivityContext.Target, Is.EqualTo(activePatient));
                Assert.That(carrier.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(htn.PlanningToken, Is.SameAs(activePlanningToken));
                Assert.That(activePlanningToken.IsCancellationRequested, Is.False);
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var activeFollow,
                        entities) && activeFollow.EntityId == activePatient,
                    Is.True);
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityUid>(
                        NPCBlackboard.CurrentOrderedTarget,
                        out var activeOrderedTarget,
                        entities) && activeOrderedTarget == activePatient,
                    Is.True);
                Assert.That(entities.GetComponent<PullerComponent>(escortUid).Pulling,
                    Is.EqualTo(activePatient));
            });

            entities.DeleteEntity(activePatient);
            entities.DeleteEntity(escortUid);
            entities.DeleteEntity(leader);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FormationStopRangeMatchesCarrierArrivalAndDoesNotFalseTimeout()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mobState = entities.System<MobStateSystem>();
        var mapSystem = server.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();

        EntityUid escort = default;
        await server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 5; x++)
                mapSystem.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);

            var leader = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(4.5f, 0.5f)));
            var patient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(4.5f, 1.5f)));
            MakePatientCritical(entities, mobState, patient);
            var team = entities.EnsureComponent<LuaMRescueTeamComponent>(leader);
            ConfigureTeam(team, leader, patient, shuttleAnchor: null);

            escort = entities.SpawnEntity(
                "LuaMRescueEscort",
                new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            ConfigureEscort(escortComp, leader, LuaMRescueEscortRole.Tourniquet);

            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Tourniquet;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Tourniquet);
            Assert.That(
                carrier.ActivityRoleProfile.TryGetPolicy(LuaMRescueActivity.Protecting, out var policy),
                Is.True);
            policy.Timeout = TimeSpan.FromSeconds(0.15);
        });

        await pair.RunSeconds(0.5f);

        await server.WaitAssertion(() =>
        {
            var escortComp = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var context = carrier.ActivityContext;
            var htn = entities.GetComponent<HTNComponent>(escort);
            Assert.That(
                htn.Blackboard.TryGetValue<float>("FollowRange", out var followRange, entities),
                Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(escortComp.CurrentDuty, Is.EqualTo(LuaMRescueEscortDuty.SecureScene));
                Assert.That(context.Activity, Is.EqualTo(LuaMRescueActivity.Protecting));
                Assert.That(context.RouteStatus, Is.EqualTo(LuaMRescueRouteStatus.Arrived));
                Assert.That(context.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(context.FailureReason, Is.EqualTo(LuaMRescueFailureReason.None));
                Assert.That(context.Deadline, Is.LessThan(server.Timing.CurTime),
                    "An ongoing formation duty may remain active after its route deadline once actually in formation.");
                Assert.That(followRange, Is.EqualTo(5.5f));
                Assert.That(context.LastProgressDistance, Is.LessThanOrEqualTo(followRange));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void ConfigureTeam(
        LuaMRescueTeamComponent team,
        EntityUid leader,
        EntityUid patient,
        EntityUid? shuttleAnchor,
        LuaMRescueSortiePlan sortiePlan = LuaMRescueSortiePlan.ApproachPatient)
    {
        team.TeamId = 9001;
        team.Leader = leader;
        team.Patient = patient;
        team.SceneAnchor = leader;
        team.ShuttleAnchor = shuttleAnchor;
        team.SortiePlan = sortiePlan;
    }

    private static void MakePatientCritical(
        IEntityManager entities,
        MobStateSystem mobState,
        EntityUid patient)
    {
        // A forced Critical state with zero damage is reconciled back to Alive by
        // MobThresholds on a later tick. Give the fixture a real critical injury
        // so long-running route and formation assertions retain a medical target.
        entities.RemoveComponent<BarotraumaComponent>(patient);
        var injury = new DamageSpecifier();
        injury.DamageDict.Add("Blunt", 100);
        Assert.That(
            entities.System<DamageableSystem>().TryChangeDamage(patient, injury, ignoreResistances: true),
            Is.Not.Null);
        mobState.ChangeMobState(patient, MobState.Critical);
        Assert.That(
            entities.GetComponent<MobStateComponent>(patient).CurrentState,
            Is.EqualTo(MobState.Critical));
    }

    private static void ConfigureEscort(
        LuaMRescueEscortComponent escort,
        EntityUid leader,
        LuaMRescueEscortRole role = LuaMRescueEscortRole.Kostyl)
    {
        escort.TeamId = 9001;
        escort.Role = role;
        escort.Leader = leader;
        escort.CurrentDuty = LuaMRescueEscortDuty.Standby;
        escort.PendingDuty = LuaMRescueEscortDuty.Standby;
        escort.DutyRefreshInterval = 0.01f;
        escort.DutyRefreshAccumulator = escort.DutyRefreshInterval;
        escort.NextDutyActionAt = TimeSpan.MaxValue;
        escort.NextSpeechTime = TimeSpan.MaxValue;
    }
}
