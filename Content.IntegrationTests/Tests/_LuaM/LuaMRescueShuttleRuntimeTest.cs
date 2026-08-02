using System;
using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server._LuaM.Rescue;
using Content.Server._Mono.CorticalBorer;
using Content.Shared._Mono.CorticalBorer;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Server.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueShuttleSystem))]
public sealed class LuaMRescueShuttleRuntimeTest
{
    [Test]
    public async Task DispatcherManualAssignmentConsumesQueueAndRejectsDuplicateOwner()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var patient = entities.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(5f, 0f)));
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 110);
            damageable.TryChangeDamage(patient, damage, true);
            mobState.ChangeMobState(patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(patient);
            Assert.That(shuttle.TryQueueAutomaticMedicalSignal(
                patient,
                LuaMRescueMedicalSignalKind.Critical,
                out var queueStatus), Is.True, queueStatus);
            Assert.That(shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient), Is.True);

            var incapacitated = entities.SpawnEntity("LuaMRescueAgent", new EntityCoordinates(map.Grid, Vector2.Zero));
            mobState.ChangeMobState(incapacitated, MobState.Critical);
            Assert.That(shuttle.TryAssignQueuedDispatch(patient, incapacitated, out var incapacitatedStatus),
                Is.False, incapacitatedStatus);

            var assigned = entities.SpawnEntity("LuaMRescueAgent", new EntityCoordinates(map.Grid, Vector2.Zero));
            var assignedRescue = entities.GetComponent<LuaMRescueAgentComponent>(assigned);
            assignedRescue.AssignedShuttle = map.Grid;
            assignedRescue.AutoAcquireTargets = false;
            assignedRescue.EvacuateTargetsToShuttle = false;
            assignedRescue.AutoAnalyzeBeforeTreatment = false;
            assignedRescue.AutoTreatWithCarriedItems = false;
            assignedRescue.AutoDefibDeadPatients = false;
            Assert.That(shuttle.TryAssignQueuedDispatch(patient, assigned, out var assignedStatus),
                Is.True, assignedStatus);
            Assert.Multiple(() =>
            {
                Assert.That(
                    assignedRescue.AssignedTarget == patient ||
                    assignedRescue.TaskPatientTarget == patient ||
                    assignedRescue.ActivityContext.Target == patient ||
                    assignedRescue.ManualOverrideTarget == patient,
                    Is.True);
                Assert.That(shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient), Is.False);
                Assert.That(shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient), Is.False);
            });

            var second = entities.SpawnEntity("LuaMRescueAgent", new EntityCoordinates(map.Grid, Vector2.Zero));
            Assert.That(shuttle.TryAssignQueuedDispatch(patient, second, out var duplicateStatus),
                Is.False, duplicateStatus);
            Assert.That(entities.GetComponent<LuaMRescueAgentComponent>(second).AssignedTarget, Is.Null);
            Assert.That(entities.EntityQuery<LuaMRescueAgentComponent>().Count(rescue =>
                rescue.AssignedTarget == patient ||
                rescue.TaskPatientTarget == patient ||
                rescue.ActivityContext.Target == patient ||
                rescue.ManualOverrideTarget == patient), Is.EqualTo(1));

            entities.DeleteEntity(second);
            entities.DeleteEntity(assigned);
            entities.DeleteEntity(incapacitated);
            entities.DeleteEntity(patient);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MultipleAgentsSelectDeterministicDistinctPatientOwners()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var remoteMap = await pair.CreateTestMap();
        var localMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            // Spawn an incapacitated local agent and then the remote agent first
            // so raw entity-query order would choose the wrong owner.
            var incapacitatedAgent = entities.SpawnEntity("LuaMRescueAgent", localMap.MapCoords);
            mobState.ChangeMobState(incapacitatedAgent, MobState.Critical);
            var remoteAgent = entities.SpawnEntity("LuaMRescueAgent", remoteMap.MapCoords);
            var localAgent = entities.SpawnEntity("LuaMRescueAgent", localMap.MapCoords);
            var remoteRescue = entities.GetComponent<LuaMRescueAgentComponent>(remoteAgent);
            var localRescue = entities.GetComponent<LuaMRescueAgentComponent>(localAgent);
            remoteRescue.AutoAcquireTargets = false;
            localRescue.AutoAcquireTargets = false;
            remoteRescue.EvacuateTargetsToShuttle = false;
            localRescue.EvacuateTargetsToShuttle = false;

            var firstPatient = entities.SpawnEntity("MobHuman", localMap.MapCoords);
            mobState.ChangeMobState(firstPatient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(firstPatient);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    firstPatient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var firstStatus),
                Is.True,
                firstStatus);

            Assert.Multiple(() =>
            {
                Assert.That(localRescue.AssignedTarget, Is.EqualTo(firstPatient),
                    "An idle rescuer on the patient's map must outrank a remote first-enumerated rescuer.");
                Assert.That(remoteRescue.AssignedTarget, Is.Null);
                Assert.That(
                    entities.GetComponent<LuaMRescueAgentComponent>(incapacitatedAgent).AssignedTarget,
                    Is.Null,
                    "A medically incapacitated rescuer must never block an operational owner.");
            });

            // A repeated signal must reconcile with the existing exact owner,
            // never assigning the same patient to the second rescuer.
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    firstPatient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var repeatStatus),
                Is.True,
                repeatStatus);
            Assert.Multiple(() =>
            {
                Assert.That(localRescue.AssignedTarget, Is.EqualTo(firstPatient));
                Assert.That(remoteRescue.AssignedTarget, Is.Null);
                Assert.That(
                    entities.EntityQuery<LuaMRescueAgentComponent>().Count(rescue =>
                        rescue.AssignedTarget == firstPatient ||
                        rescue.TaskPatientTarget == firstPatient),
                    Is.EqualTo(1));
            });

            // A separate patient may use the remaining idle rescuer instead of
            // being incorrectly attached to the already busy owner.
            var secondPatient = entities.SpawnEntity("MobHuman", localMap.MapCoords);
            mobState.ChangeMobState(secondPatient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(secondPatient);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    secondPatient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var secondStatus),
                Is.True,
                secondStatus);
            Assert.Multiple(() =>
            {
                Assert.That(localRescue.AssignedTarget, Is.EqualTo(firstPatient));
                Assert.That(remoteRescue.AssignedTarget, Is.EqualTo(secondPatient));
                Assert.That(
                    entities.EntityQuery<LuaMRescueAgentComponent>().Count(rescue =>
                        rescue.AssignedTarget == secondPatient ||
                        rescue.TaskPatientTarget == secondPatient),
                    Is.EqualTo(1));
            });

            // This class uses pooled server instances. Explicitly retire every
            // test-created owner and target so the following randomized test
            // cannot observe a valid rescuer from this two-map fixture.
            entities.DeleteEntity(firstPatient);
            entities.DeleteEntity(secondPatient);
            entities.DeleteEntity(localAgent);
            entities.DeleteEntity(remoteAgent);
            entities.DeleteEntity(incapacitatedAgent);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CorticalBorerSignalsAreRejectedForCriticalDeadAndContainedTargets()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var borers = entities.System<CorticalBorerSystem>();
        var map = await pair.CreateTestMap();
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var session = pair.Server.ResolveDependency<IPlayerManager>()
            .GetSessionById(clientSession!.UserId);

        await server.WaitAssertion(() =>
        {
            var critical = SpawnBorer(entities, mobState, map.MapId, Vector2.Zero, MobState.Critical);
            var dead = SpawnBorer(entities, mobState, map.MapId, new Vector2(1f, 0f), MobState.Dead);
            var contained = SpawnBorer(entities, mobState, map.MapId, new Vector2(2f, 0f), MobState.Critical);
            var host = entities.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(2f, 0f), map.MapId));
            borers.InfestTarget((contained, entities.GetComponent<CorticalBorerComponent>(contained)), host);

            var playerControlled = SpawnBorer(
                entities,
                mobState,
                map.MapId,
                new Vector2(3f, 0f),
                MobState.Alive);
            pair.Server.PlayerMan.SetAttachedEntity(session, playerControlled, true);
            Assert.That(entities.HasComponent<ActorComponent>(playerControlled), Is.True);
            mobState.ChangeMobState(playerControlled, MobState.Dead);
            pair.Server.PlayerMan.SetAttachedEntity(session, null, true);

            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == playerControlled),
                    Is.False,
                    "The real MobState death event for a player-controlled borer must not enqueue rescue.");
                Assert.That(
                    rescue.TryGetAutomaticMedicalSignalEligibility(
                        critical,
                        LuaMRescueMedicalSignalKind.Critical,
                        requireAttachedPlayer: false,
                        out var criticalReason),
                    Is.False);
                Assert.That(
                    criticalReason,
                    Is.EqualTo(LuaMRescueFailureReason.UnsupportedSpecies.ToString()));

                Assert.That(
                    rescue.TryGetAutomaticMedicalSignalEligibility(
                        dead,
                        LuaMRescueMedicalSignalKind.Death,
                        requireAttachedPlayer: false,
                        out var deathReason),
                    Is.False);
                Assert.That(
                    deathReason,
                    Is.EqualTo(LuaMRescueFailureReason.UnsupportedSpecies.ToString()));

                Assert.That(containers.IsEntityOrParentInContainer(contained), Is.True);
                Assert.That(
                    entities.GetComponent<CorticalBorerInfestedComponent>(host)
                        .InfestationContainer.Contains(contained),
                    Is.True,
                    "The containment check must exercise the real InfestationContainer.");
                Assert.That(
                    rescue.TryGetAutomaticMedicalSignalEligibility(
                        contained,
                        LuaMRescueMedicalSignalKind.Critical,
                        requireAttachedPlayer: false,
                        out var containedReason),
                    Is.False);
                Assert.That(
                    containedReason,
                    Is.EqualTo(LuaMRescueFailureReason.ContainedTarget.ToString()));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HostilePlayerCriticalSignalDoesNotDispatchOrEnterQueue()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var hostile = entities.SpawnEntity("MobHumanSyndicateAgentBase", map.MapCoords);
            mobState.ChangeMobState(hostile, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(hostile);

            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.TryQueueAutomaticMedicalSignal(
                        hostile,
                        LuaMRescueMedicalSignalKind.Critical,
                        out var status),
                    Is.False,
                    status);
                Assert.That(status, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh.ToString()));
                Assert.That(
                    rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == hostile),
                    Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EqualUrgencyCriticalSignalQueuesWithoutStealingCurrentIntent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid, new Vector2(0.1f, 0.5f)));
            var current = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(0.4f, 0.5f)));
            var candidate = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(0.75f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(current);
            entities.RemoveComponent<BarotraumaComponent>(candidate);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            var currentDamage = new DamageSpecifier();
            currentDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(current, currentDamage, ignoreResistances: true),
                Is.Not.Null);
            var candidateDamage = new DamageSpecifier();
            candidateDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(candidate, candidateDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsCritical(current), Is.True);
                Assert.That(mobState.IsCritical(candidate), Is.True);
            });
            entities.EnsureComponent<ActorComponent>(candidate);

            Assert.That(agents.TryOrderAgent(agent, current, out var orderStatus), Is.True, orderStatus);
            Assert.Multiple(() =>
            {
                Assert.That(mobState.IsCritical(current), Is.True);
                Assert.That(mobState.IsCritical(candidate), Is.True);
                Assert.That(
                    coordinator.GetPatientUrgency(current),
                    Is.EqualTo(LuaMRescuePatientUrgency.Critical));
                Assert.That(
                    coordinator.GetPatientUrgency(candidate),
                    Is.EqualTo(LuaMRescuePatientUrgency.Critical));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(current));
            });
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    candidate,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);

            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(current));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(current));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == candidate),
                    Is.True,
                    "Same-category Critical patients must queue instead of causing distance/damage jitter.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CriticalPreemptionPreservesDisplacedMissionInfrastructureAcrossOwnerDeletion()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttleSystem = entities.System<LuaMRescueShuttleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var owner = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid, new Vector2(0.1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(owner);
            var ownerRescue = entities.GetComponent<LuaMRescueAgentComponent>(owner);
            ownerRescue.AutoAcquireTargets = false;
            ownerRescue.EvacuateTargetsToShuttle = false;
            ownerRescue.AutoAnalyzeBeforeTreatment = false;
            ownerRescue.AutoTreatWithCarriedItems = false;
            ownerRescue.AutoDefibDeadPatients = false;

            var assignedShuttle = entities.SpawnEntity(
                null,
                new EntityCoordinates(map.Grid, new Vector2(4f, 0.5f)));
            var assignedAnchor = entities.SpawnEntity(
                null,
                new EntityCoordinates(map.Grid, new Vector2(7f, 0.5f)));
            var assignedConsole = entities.SpawnEntity(
                null,
                new EntityCoordinates(map.Grid, new Vector2(4.2f, 0.5f)));
            var assignedReturnTarget = entities.SpawnEntity(
                null,
                new EntityCoordinates(map.Grid, new Vector2(9f, 0.5f)));
            ownerRescue.AssignedShuttle = assignedShuttle;
            ownerRescue.AssignedShuttleAnchor = assignedAnchor;
            ownerRescue.AssignedShuttleConsole = assignedConsole;
            ownerRescue.AssignedReturnTarget = assignedReturnTarget;

            var displacedPatient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(displacedPatient);
            var displacedDamage = new DamageSpecifier();
            displacedDamage.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(displacedPatient, displacedDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsAlive(displacedPatient), Is.True);

            ownerRescue.AssignedTarget = displacedPatient;
            ownerRescue.TaskPatientTarget = displacedPatient;
            ownerRescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    owner,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.Dispatching,
                    displacedPatient,
                    new EntityCoordinates(displacedPatient, Vector2.Zero),
                    out var initialIntent),
                Is.True,
                $"Initial automatic ownership intent was rejected: {initialIntent.FailureReason}");

            var criticalPatient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(2f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(criticalPatient);
            var criticalDamage = new DamageSpecifier();
            criticalDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(criticalPatient, criticalDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(criticalPatient), Is.True);
            entities.EnsureComponent<ActorComponent>(criticalPatient);

            Assert.That(
                shuttleSystem.TryQueueAutomaticMedicalSignal(
                    criticalPatient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var preemptionStatus),
                Is.True,
                preemptionStatus);
            Assert.Multiple(() =>
            {
                Assert.That(ownerRescue.AssignedTarget, Is.EqualTo(criticalPatient));
                Assert.That(ownerRescue.TaskPatientTarget, Is.EqualTo(criticalPatient));
                Assert.That(
                    shuttleSystem.GetPendingAutomaticDispatches()
                        .Any(entry => entry.Target == displacedPatient),
                    Is.True,
                    "Critical preemption must preserve the displaced automatic mission.");
            });

            entities.DeleteEntity(owner);
            Assert.That(
                agentSystem.TryFindActiveAgent(out _, out _),
                Is.False,
                "The replacement path must be exercised without a manually spawned agent.");

            Assert.That(shuttleSystem.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(agentSystem.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True);
            var replacementPosition = entities.GetComponent<TransformComponent>(replacement).MapPosition.Position;
            var anchorPosition = entities.GetComponent<TransformComponent>(assignedAnchor).MapPosition.Position;

            Assert.Multiple(() =>
            {
                Assert.That(replacement, Is.Not.EqualTo(owner));
                Assert.That(replacementRescue.AssignedTarget, Is.EqualTo(displacedPatient),
                    "The first queue owner after preemption must remain the displaced mission, not the newer Critical mission.");
                Assert.That(replacementRescue.TaskPatientTarget, Is.EqualTo(displacedPatient));
                Assert.That(replacementRescue.AssignedShuttle, Is.EqualTo(assignedShuttle));
                Assert.That(replacementRescue.AssignedShuttleAnchor, Is.EqualTo(assignedAnchor));
                Assert.That(replacementRescue.AssignedShuttleConsole, Is.EqualTo(assignedConsole));
                Assert.That(replacementRescue.AssignedReturnTarget, Is.EqualTo(assignedReturnTarget));
                Assert.That(replacementPosition, Is.EqualTo(anchorPosition),
                    "The replacement must spawn at the preserved shuttle anchor rather than at the displaced patient.");
                Assert.That(
                    shuttleSystem.GetPendingAutomaticDispatches()
                        .Any(entry => entry.Target == displacedPatient),
                    Is.False);
                Assert.That(
                    shuttleSystem.GetPendingAutomaticDispatches()
                        .Any(entry => entry.Target == criticalPatient),
                    Is.True,
                    "The later Critical mission must remain queued behind the restored displaced lineage.");
            });

            entities.DeleteEntity(criticalPatient);
            shuttleSystem.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualOverrideDisplacedByCriticalSignalRemainsPending()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid, new Vector2(0.1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;

            var hostile = entities.SpawnEntity(
                "MobHumanSyndicateAgentBase",
                new EntityCoordinates(map.Grid, new Vector2(0.4f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(hostile);
            var hostileDamage = new DamageSpecifier();
            hostileDamage.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(hostile, hostileDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(agents.TryOrderAgent(agent, hostile, out var orderStatus), Is.True, orderStatus);
            Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostile));

            var critical = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(0.75f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(critical);
            var criticalDamage = new DamageSpecifier();
            criticalDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(critical, criticalDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(critical), Is.True);
            entities.EnsureComponent<ActorComponent>(critical);

            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    critical,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var dispatchStatus),
                Is.True,
                dispatchStatus);

            var displaced = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == hostile);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(critical));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(critical));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostile));
                Assert.That(displaced.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.FollowUp));
                Assert.That(displaced.ManualOverride, Is.True);
                Assert.That(displaced.ManualOverrideOwner, Is.EqualTo(agent));
                Assert.That(displaced.ManualOverrideGeneration, Is.EqualTo(rescue.ManualOverrideGeneration));
                Assert.That(displaced.Terminal, Is.False);
            });

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            displaced = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == hostile);
            Assert.Multiple(() =>
            {
                Assert.That(displaced.ManualOverride, Is.True);
                Assert.That(displaced.Terminal, Is.False);
                Assert.That(displaced.Attempts, Is.Zero);
                Assert.That(
                    displaced.LastStatus,
                    Does.Contain("busy").Or.Contain("preempted by higher-priority critical signal"),
                    "The queued manual mission may retain its authoritative preemption reason until its retry clock is due.");
                Assert.That(displaced.LastStatus, Does.Not.Contain("discarded"));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostile));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(critical));
            });

            var replacement = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(1.1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(replacement);
            var replacementDamage = new DamageSpecifier();
            replacementDamage.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(replacement, replacementDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(
                agents.TryOrderAgent(agent, replacement, out var replacementStatus),
                Is.True,
                replacementStatus);

            displaced = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == hostile);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(replacement));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacement));
                Assert.That(displaced.ManualOverride, Is.False,
                    "An explicit replacement must revoke the queued target's elevated eligibility.");
                Assert.That(displaced.LastStatus, Does.Contain("manual override revoked"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MarkerOnlyManualPendingCannotResetLocalRouteRecovery()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid, new Vector2(0.1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;

            var hostile = entities.SpawnEntity(
                "MobHumanSyndicateAgentBase",
                new EntityCoordinates(map.Grid, new Vector2(0.4f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(hostile);
            var hostileDamage = new DamageSpecifier();
            hostileDamage.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(hostile, hostileDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(agents.TryOrderAgent(agent, hostile, out var orderStatus), Is.True, orderStatus);

            var critical = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(0.75f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(critical);
            var criticalDamage = new DamageSpecifier();
            criticalDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(critical, criticalDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(critical), Is.True);
            entities.EnsureComponent<ActorComponent>(critical);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    critical,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var dispatchStatus),
                Is.True,
                dispatchStatus);

            entities.RemoveComponent<ActorComponent>(critical);
            mobState.ChangeMobState(critical, MobState.Alive);
            var urgentDamage = new DamageSpecifier();
            urgentDamage.DamageDict.Add("Blunt", 100);
            Assert.That(
                damageable.TryChangeDamage(hostile, urgentDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(hostile), Is.True);

            var manualGeneration = rescue.ManualOverrideGeneration;
            var skipUntil = TimeSpan.MaxValue;
            rescue.SkippedTargets[hostile] = skipUntil;

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            var pending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == hostile);
            Assert.Multiple(() =>
            {
                Assert.That(pending.ManualOverride, Is.True);
                Assert.That(pending.ManualOverrideOwner, Is.EqualTo(agent));
                Assert.That(pending.ManualOverrideGeneration, Is.EqualTo(manualGeneration));
                Assert.That(pending.Attempts, Is.Zero);
                Assert.That(pending.LastStatus, Does.Contain("local route recovery"));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(hostile));
                Assert.That(rescue.ManualOverrideGeneration, Is.EqualTo(manualGeneration));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(critical),
                    "A more urgent skipped manual target must wait for its local recovery policy.");
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(critical));
                Assert.That(rescue.SkippedTargets[hostile], Is.EqualTo(skipUntil),
                    "Dispatch reconciliation must not remove the route skip owned by local recovery.");
            });

            entities.DeleteEntity(hostile);
            var (time, tick) = timing.TimeBase;
            timing.TimeBase = (time + TimeSpan.FromSeconds(11), tick);
            var cleanupBudget = shuttle.PendingAutomaticDispatchCount + 1;
            for (var i = 0; i < cleanupBudget &&
                            shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == hostile); i++)
            {
                shuttle.ProcessPendingAutomaticDispatchesNow();
            }
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == hostile),
                Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RetiredManualDispatchLineageCannotAcquireReplacementAgent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var retiredAgent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid, new Vector2(0.1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoAcquireTargets = false;
            retiredRescue.EvacuateTargetsToShuttle = false;
            retiredRescue.AutoAnalyzeBeforeTreatment = false;
            retiredRescue.AutoTreatWithCarriedItems = false;
            retiredRescue.AutoDefibDeadPatients = false;

            var staleTarget = entities.SpawnEntity(
                "MobHumanSyndicateAgentBase",
                new EntityCoordinates(map.Grid, new Vector2(0.4f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(staleTarget);
            var staleDamage = new DamageSpecifier();
            staleDamage.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(staleTarget, staleDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(
                agents.TryOrderAgent(retiredAgent, staleTarget, out var staleOrderStatus),
                Is.True,
                staleOrderStatus);

            var critical = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(0.75f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(critical);
            var criticalDamage = new DamageSpecifier();
            criticalDamage.DamageDict.Add("Blunt", 110);
            Assert.That(
                damageable.TryChangeDamage(critical, criticalDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(critical), Is.True);
            entities.EnsureComponent<ActorComponent>(critical);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    critical,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var dispatchStatus),
                Is.True,
                dispatchStatus);

            var stale = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == staleTarget);
            Assert.That(stale.ManualOverrideOwner, Is.EqualTo(retiredAgent));
            Assert.That(stale.ManualOverrideGeneration, Is.EqualTo(retiredRescue.ManualOverrideGeneration));
            mobState.ChangeMobState(retiredAgent, MobState.Dead);
            shuttle.RetireDeadRescueAgents();

            stale = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == staleTarget);
            Assert.Multiple(() =>
            {
                Assert.That(stale.ManualOverride, Is.False);
                Assert.That(stale.ManualOverrideOwner, Is.Null);
                Assert.That(stale.ManualOverrideGeneration, Is.Zero);
                Assert.That(stale.LastStatus, Does.Contain("owner retired"));
                Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);
                Assert.That(entities.HasComponent<LuaMRescuePersonnelComponent>(retiredAgent), Is.True);
            });

            Assert.That(
                agents.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out var replacementAgent,
                    out var spawnStatus),
                Is.True,
                spawnStatus);
            Assert.That(replacementAgent, Is.Not.EqualTo(retiredAgent));
            entities.RemoveComponent<BarotraumaComponent>(replacementAgent);
            var replacementRescue = entities.GetComponent<LuaMRescueAgentComponent>(replacementAgent);
            replacementRescue.AutoAcquireTargets = false;
            replacementRescue.EvacuateTargetsToShuttle = false;
            replacementRescue.AutoAnalyzeBeforeTreatment = false;
            replacementRescue.AutoTreatWithCarriedItems = false;
            replacementRescue.AutoDefibDeadPatients = false;

            var replacementTarget = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(1.1f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(replacementTarget);
            var replacementDamage = new DamageSpecifier();
            replacementDamage.DamageDict.Add("Blunt", 10);
            Assert.That(
                damageable.TryChangeDamage(replacementTarget, replacementDamage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(
                agents.TryOrderAgent(replacementAgent, replacementTarget, out var replacementStatus),
                Is.True,
                replacementStatus);

            var replacementGeneration = replacementRescue.ManualOverrideGeneration;
            var skipUntil = TimeSpan.MaxValue;
            replacementRescue.AssignedTarget = null;
            replacementRescue.DeathSignalTarget = null;
            replacementRescue.EvacuatingTarget = null;
            replacementRescue.OnboardCareTarget = null;
            replacementRescue.TaskPatientTarget = null;
            replacementRescue.SkippedTargets[replacementTarget] = skipUntil;

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == staleTarget),
                    Is.False,
                    "The hostile stale target must lose manual eligibility with its retired owner.");
                Assert.That(replacementRescue.ManualOverrideTarget, Is.EqualTo(replacementTarget));
                Assert.That(replacementRescue.ManualOverrideGeneration, Is.EqualTo(replacementGeneration));
                Assert.That(replacementRescue.AssignedTarget, Is.Null);
                Assert.That(replacementRescue.TaskPatientTarget, Is.Null);
                Assert.That(replacementRescue.SkippedTargets[replacementTarget], Is.EqualTo(skipUntil));
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AutomaticSignalCannotBypassDurableOrDormantLocalRecovery(bool dormant)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;

            var patient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));
            mobState.ChangeMobState(patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(patient);

            EntityUid? expectedDormant = dormant ? patient : null;
            rescue.SkippedTargets[patient] = TimeSpan.MaxValue;
            rescue.RouteFailureAttempts[patient] = rescue.ActivityRoleProfile.MaxAttempts;
            rescue.DormantRouteTarget = expectedDormant;
            rescue.AssignedTarget = null;
            rescue.DeathSignalTarget = null;
            rescue.EvacuatingTarget = null;
            rescue.OnboardCareTarget = null;
            rescue.TaskPatientTarget = null;

            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);

            var pending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == patient);
            Assert.Multiple(() =>
            {
                Assert.That(pending.Attempts, Is.Zero);
                Assert.That(pending.LastStatus, Does.Contain("local route recovery"));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.SkippedTargets[patient], Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(rescue.RouteFailureAttempts[patient],
                    Is.EqualTo(rescue.ActivityRoleProfile.MaxAttempts));
                Assert.That(rescue.DormantRouteTarget, Is.EqualTo(expectedDormant));
            });

            // Finish the test-owned pending record through the normal direct
            // reconciliation path so it cannot leak into the pooled server.
            rescue.SkippedTargets.Remove(patient);
            rescue.DormantRouteTarget = null;
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var reconcileStatus),
                Is.True,
                reconcileStatus);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedAutomaticSignalCannotReopenCanonicalRequiredHandoffHold(bool durableSkip)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var anchor = entities.SpawnEntity("MedicalBed", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.AssignedShuttle = map.Grid;
            rescue.AssignedShuttleAnchor = anchor;

            var patient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));
            entities.RemoveComponent<ActorComponent>(patient);
            mobState.ChangeMobState(patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(patient);

            rescue.AssignedTarget = patient;
            rescue.EvacuatingTarget = patient;
            rescue.TaskPatientTarget = patient;
            rescue.TaskSupplyTarget = anchor;
            rescue.TaskStage = LuaMRescueTaskStage.DeliveringPatient;
            rescue.RequiredOnboardHandoffPatients.Add(patient);
            if (durableSkip)
                rescue.SkippedTargets[patient] = TimeSpan.MaxValue;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.Handoff,
                    patient,
                    new EntityCoordinates(anchor, Vector2.Zero),
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

            for (var i = 0; i < 3; i++)
            {
                Assert.That(
                    shuttle.TryQueueAutomaticMedicalSignal(
                        patient,
                        LuaMRescueMedicalSignalKind.Critical,
                        out var queueStatus),
                    Is.True,
                    queueStatus);
                Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);

                var pending = shuttle.GetPendingAutomaticDispatches()
                    .Single(entry => entry.Target == patient);
                Assert.Multiple(() =>
                {
                    Assert.That(pending.Attempts, Is.Zero);
                    Assert.That(pending.LastStatus, Does.Contain("local route recovery"));
                    Assert.That(pending.RequiredOnboardHandoff, Is.True);
                    Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(blocked.Generation));
                    Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Handoff));
                    Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                    Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                    Assert.That(rescue.EvacuatingTarget, Is.EqualTo(patient));
                    Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                    Assert.That(rescue.TaskSupplyTarget, Is.EqualTo(anchor));
                    Assert.That(
                        rescue.SkippedTargets.TryGetValue(patient, out var skipUntil),
                        Is.EqualTo(durableSkip));
                    if (durableSkip)
                        Assert.That(skipUntil, Is.EqualTo(TimeSpan.MaxValue));
                });
            }

            entities.DeleteEntity(patient);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.False);
            entities.DeleteEntity(anchor);
            entities.DeleteEntity(agent);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task IgnoredOnboardPatientCannotBeReacquiredByAutomaticSignal()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;

            var patient = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));
            mobState.ChangeMobState(patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(patient);
            rescue.IgnoredOnboardPatients.Add(patient);

            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            var pending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == patient);
            Assert.Multiple(() =>
            {
                Assert.That(pending.Attempts, Is.Zero);
                Assert.That(pending.LastStatus, Does.Contain("local route recovery"));
                Assert.That(rescue.IgnoredOnboardPatients, Does.Contain(patient));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.OnboardCareTarget, Is.Null);
            });

            rescue.IgnoredOnboardPatients.Remove(patient);
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var reconcileStatus),
                Is.True,
                reconcileStatus);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecoveredNonRequiredCriticalSignalClosesPendingEpisode()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            patient = entities.SpawnEntity("MobHuman", map.MapCoords);
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 20);
            Assert.That(damageable.TryChangeDamage(patient, damage, ignoreResistances: true), Is.Not.Null);
            // Add Actor only after entering Critical so the test owns the queue event.
            mobState.ChangeMobState(patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(patient);

            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queuedStatus),
                Is.True,
                queuedStatus);
            Assert.That(
                rescue.GetPendingAutomaticDispatches().Single(entry => entry.Target == patient).Kind,
                Is.EqualTo(LuaMRescueMedicalSignalKind.Critical));

            // The direct queue gate needed an Actor marker, but later queue
            // processing does not. Remove the session-less marker before the
            // state transition so unrelated playtime tracking is not invoked
            // with a null PlayerSession.
            Assert.That(entities.RemoveComponent<ActorComponent>(patient), Is.True);
            mobState.ChangeMobState(patient, MobState.Alive);
            // Recovery closes the medical episode unless a separate Required
            // physical handoff still owns the patient. Drain the stale order
            // token and verify that it cannot recreate the obsolete dispatch.
            Assert.That(rescue.ProcessPendingAutomaticDispatchesNow(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
                Assert.That(
                    rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingConsoleRouteBecomesTerminalAndManualRetriesAdvanceExactlyOneGeneration()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        EntityUid target = default;
        var terminalGeneration = 0;

        await server.WaitAssertion(() =>
        {
            shuttle = entities.SpawnEntity(null, map.MapCoords);
            target = entities.SpawnEntity(null, new MapCoordinates(new Vector2(8f, 0f), map.MapId));
            var lifecycle = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.MaxRetries = 5;
            lifecycle.ActivityRoleProfile.MaxAttempts = 2;
            lifecycle.RetryDelaySeconds = 0.01f;

            Assert.That(rescue.TrySetAutopilotTarget(shuttle, target, out var console), Is.False);
            Assert.That(console, Is.Null);
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var initial), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(initial.State, Is.EqualTo(LuaMRescueShuttleRouteState.Failed));
                Assert.That(initial.RouteGeneration, Is.EqualTo(1));
                Assert.That(initial.RetryCount, Is.Zero);
                Assert.That(initial.MaxRetries, Is.EqualTo(1),
                    "The role profile must cap a looser per-shuttle retry configuration.");
                Assert.That(initial.Attempt, Is.EqualTo(1));
                Assert.That(initial.Terminal, Is.False);
                Assert.That(initial.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(initial.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
                Assert.That(initial.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(initial.ActivityGeneration, Is.EqualTo((uint) initial.RouteGeneration));
                Assert.That(initial.ActivityFailureReason, Is.EqualTo(LuaMRescueFailureReason.ShuttleRouteFailed));
            });
        });

        await pair.RunSeconds(1.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var exhausted), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(exhausted.State, Is.EqualTo(LuaMRescueShuttleRouteState.Failed));
                Assert.That(exhausted.RetryCount, Is.EqualTo(1));
                Assert.That(exhausted.Attempt, Is.EqualTo(2));
                Assert.That(exhausted.RouteGeneration, Is.EqualTo(2));
                Assert.That(exhausted.Terminal, Is.True);
                Assert.That(exhausted.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
                Assert.That(exhausted.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(exhausted.ActivityGeneration, Is.EqualTo((uint) exhausted.RouteGeneration));
            });

            terminalGeneration = exhausted.RouteGeneration;
            Assert.That(rescue.TryRetryAutopilotRoute(shuttle, out var retryStatus), Is.False);
            Assert.That(retryStatus, Does.Contain("console").IgnoreCase);
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var manualRetry), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(manualRetry.State, Is.EqualTo(LuaMRescueShuttleRouteState.Failed));
                Assert.That(manualRetry.RouteGeneration, Is.EqualTo(terminalGeneration + 1));
                Assert.That(manualRetry.RetryCount, Is.Zero);
                Assert.That(manualRetry.Attempt, Is.EqualTo(1));
                Assert.That(manualRetry.Terminal, Is.False);
                Assert.That(manualRetry.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
                Assert.That(manualRetry.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(manualRetry.ActivityGeneration, Is.EqualTo((uint) manualRetry.RouteGeneration));
                Assert.That(manualRetry.NextRetryAt, Is.GreaterThan(TimeSpan.Zero));
            });
        });

        await pair.RunSeconds(1.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var exhaustedAgain), Is.True);
            Assert.That(exhaustedAgain.RouteGeneration, Is.EqualTo(terminalGeneration + 2));
            Assert.That(exhaustedAgain.Terminal, Is.True);
            Assert.That(exhaustedAgain.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualRetryFromTimedOutIsBoundedAndArrivedIsNotDockedOrTerminal()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var map = await pair.CreateTestMap();

        EntityUid arrivedShuttle = default;

        await server.WaitAssertion(() =>
        {
            var timedOutShuttle = entities.SpawnEntity(null, map.MapCoords);
            var timedOutTarget = entities.SpawnEntity(null, new MapCoordinates(new Vector2(10f, 0f), map.MapId));
            var timedOut = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(timedOutShuttle);
            timedOut.State = LuaMRescueShuttleRouteState.TimedOut;
            timedOut.Target = timedOutTarget;
            timedOut.MaxRetries = 1;
            timedOut.RetryCount = 1;
            timedOut.RouteGeneration = 12;
            timedOut.RetryDelaySeconds = 60f;
            timedOut.RouteStartedAt = timing.CurTime;
            timedOut.RouteDeadline = timing.CurTime;
            timedOut.StateChangedAt = timing.CurTime;

            Assert.That(rescue.TryGetRouteSnapshot(timedOutShuttle, out var terminalTimeout), Is.True);
            Assert.That(terminalTimeout.Terminal, Is.True);
            Assert.That(terminalTimeout.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
            Assert.That(terminalTimeout.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
            Assert.That(terminalTimeout.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
            Assert.That(terminalTimeout.ActivityGeneration, Is.EqualTo((uint) terminalTimeout.RouteGeneration));
            Assert.That(rescue.TryRetryAutopilotRoute(timedOutShuttle, out var timedOutRetryStatus), Is.False);
            Assert.That(timedOutRetryStatus, Does.Contain("console").IgnoreCase);
            Assert.That(rescue.TryGetRouteSnapshot(timedOutShuttle, out var retriedTimeout), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(retriedTimeout.State, Is.EqualTo(LuaMRescueShuttleRouteState.Failed));
                Assert.That(retriedTimeout.RouteGeneration, Is.EqualTo(13));
                Assert.That(retriedTimeout.RetryCount, Is.Zero);
                Assert.That(retriedTimeout.Terminal, Is.False);
                Assert.That(retriedTimeout.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
                Assert.That(retriedTimeout.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(retriedTimeout.ActivityGeneration, Is.EqualTo((uint) retriedTimeout.RouteGeneration));
            });

            arrivedShuttle = entities.SpawnEntity(null, new MapCoordinates(new Vector2(20f, 0f), map.MapId));
            var arrivedTarget = entities.SpawnEntity(null, new MapCoordinates(new Vector2(20f, 0f), map.MapId));
            var arrived = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(arrivedShuttle);
            arrived.State = LuaMRescueShuttleRouteState.Routing;
            arrived.Target = arrivedTarget;
            arrived.ArrivalRange = 2f;
            arrived.RouteGeneration = 21;
            arrived.RouteStartedAt = timing.CurTime;
            arrived.RouteDeadline = timing.CurTime + TimeSpan.FromSeconds(30);
            arrived.StateChangedAt = timing.CurTime;
        });

        await pair.RunSeconds(1.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(arrivedShuttle, out var arrived), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(arrived.State, Is.EqualTo(LuaMRescueShuttleRouteState.Arrived));
                Assert.That(arrived.State, Is.Not.EqualTo(LuaMRescueShuttleRouteState.Docked));
                Assert.That(arrived.RouteGeneration, Is.EqualTo(21));
                Assert.That(arrived.Terminal, Is.False);
                Assert.That(arrived.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(arrived.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(arrived.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(arrived.ActivityGeneration, Is.EqualTo((uint) arrived.RouteGeneration));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnBorer(
        IEntityManager entities,
        MobStateSystem mobState,
        MapId mapId,
        Vector2 position,
        MobState state)
    {
        var borer = entities.SpawnEntity("MobCorticalBorer", new MapCoordinates(position, mapId));
        entities.EnsureComponent<HumanoidAppearanceComponent>(borer);
        mobState.ChangeMobState(borer, state);
        return borer;
    }

    private static void PutInContainer(
        IEntityManager entities,
        SharedContainerSystem containers,
        EntityUid entity,
        MapCoordinates coordinates)
    {
        var owner = entities.SpawnEntity(null, coordinates);
        var container = containers.EnsureContainer<Container>(owner, "LuaMRescueShuttleRuntimeContainer");
        Assert.That(containers.Insert(entity, container), Is.True);
    }
}
