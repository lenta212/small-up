using System;
using System.Numerics;
using Content.Server._LuaM.Rescue;
using Content.Server._Mono.CorticalBorer;
using Content.Server.Atmos.Components;
using Content.Shared._Mono.CorticalBorer;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueAgentSystem))]
public sealed class LuaMRescueActivityRuntimeTest
{
    [Test]
    public async Task InfestedBorerStaleIntentIsRejectedByServerTickBeforePulling()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var borers = entities.System<CorticalBorerSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid borer = default;
        EntityUid host = default;
        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            agent = fixture.SpawnAgent(Vector2.Zero);
            borer = fixture.SpawnBorer(new Vector2(0.5f, 0f), Content.Shared.Mobs.MobState.Critical);
            host = fixture.SpawnPatient(new Vector2(0.5f, 0f));
            borers.InfestTarget((borer, entities.GetComponent<CorticalBorerComponent>(borer)), host);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AssignedTarget = borer;
            rescue.EvacuatingTarget = borer;
            rescue.TaskPatientTarget = borer;
            rescue.DeathSignalTarget = borer;
            rescue.TaskStage = LuaMRescueTaskStage.EvacuatingPatient;

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.Pulling,
                    borer,
                    new EntityCoordinates(borer, Vector2.Zero),
                    out var staleIntent),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(staleIntent.Target, Is.EqualTo(borer));
                Assert.That(containers.IsEntityOrParentInContainer(borer), Is.True);
                Assert.That(
                    entities.GetComponent<CorticalBorerInfestedComponent>(host)
                        .InfestationContainer.Contains(borer),
                    Is.True,
                    "The stale candidate must be inside the real host InfestationContainer.");
                Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.Null);
                Assert.That(entities.GetComponent<PullableComponent>(borer).Puller, Is.Null);
            });
        });

        // The production UpdateAssignedTarget path must revalidate stale ownership
        // before evacuation can call PullingSystem.
        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.EvacuatingTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.DeathSignalTarget, Is.Null);
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Standby));
                Assert.That(rescue.ActivityContext.Target, Is.Null);
                Assert.That(rescue.LastTargetTrackingStatus,
                    Does.Contain(nameof(LuaMRescueFailureReason.ContainedTarget)));
                Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.Null);
                Assert.That(entities.GetComponent<PullableComponent>(borer).Puller, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AutomaticSignalsRejectLiveDeadAndContainedBorersBeforePulling()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var live = fixture.SpawnBorer(new Vector2(1f, 0f), Content.Shared.Mobs.MobState.Critical);
            var dead = fixture.SpawnBorer(new Vector2(2f, 0f), Content.Shared.Mobs.MobState.Dead);
            var contained = fixture.SpawnBorer(new Vector2(3f, 0f), Content.Shared.Mobs.MobState.Critical);
            fixture.PutInContainer(contained, new Vector2(3f, 0f));
            var eligibleCritical = fixture.SpawnPatient(
                new Vector2(4f, 0f),
                Content.Shared.Mobs.MobState.Critical);
            var eligibleDead = fixture.SpawnPatient(new Vector2(5f, 0f));
            fixture.GiveMind(eligibleDead);
            fixture.SetMobState(eligibleDead, Content.Shared.Mobs.MobState.Dead);

            Assert.Multiple(() =>
            {
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        live,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var liveReason),
                    Is.False);
                Assert.That(liveReason, Is.EqualTo(LuaMRescueFailureReason.ExcludedSpecies));

                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        dead,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var deadReason),
                    Is.False);
                Assert.That(deadReason, Is.EqualTo(LuaMRescueFailureReason.ExcludedSpecies));

                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        contained,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: true,
                        out var containedReason),
                    Is.False);
                Assert.That(containedReason, Is.EqualTo(LuaMRescueFailureReason.TargetContained));

                Assert.That(
                    shuttle.TryGetAutomaticMedicalSignalEligibility(
                        live,
                        LuaMRescueMedicalSignalKind.Critical,
                        requireAttachedPlayer: false,
                        out var liveSignalReason),
                    Is.False);
                Assert.That(
                    liveSignalReason,
                    Is.EqualTo(LuaMRescueFailureReason.ExcludedSpecies.ToString()));

                Assert.That(
                    shuttle.TryGetAutomaticMedicalSignalEligibility(
                        dead,
                        LuaMRescueMedicalSignalKind.Death,
                        requireAttachedPlayer: false,
                        out var deadSignalReason),
                    Is.False);
                Assert.That(
                    deadSignalReason,
                    Is.EqualTo(LuaMRescueFailureReason.ExcludedSpecies.ToString()));

                Assert.That(
                    shuttle.TryGetAutomaticMedicalSignalEligibility(
                        contained,
                        LuaMRescueMedicalSignalKind.Critical,
                        requireAttachedPlayer: false,
                        out var containedSignalReason),
                    Is.False);
                Assert.That(
                    containedSignalReason,
                    Is.EqualTo(LuaMRescueFailureReason.TargetContained.ToString()));

                Assert.That(
                    shuttle.TryGetAutomaticMedicalSignalEligibility(
                        eligibleCritical,
                        LuaMRescueMedicalSignalKind.Critical,
                        requireAttachedPlayer: false,
                        out var criticalSignalReason),
                    Is.True,
                    criticalSignalReason);
                Assert.That(criticalSignalReason, Is.EqualTo("eligible"));

                Assert.That(
                    shuttle.TryGetAutomaticMedicalSignalEligibility(
                        eligibleDead,
                        LuaMRescueMedicalSignalKind.Death,
                        requireAttachedPlayer: false,
                        out var deathSignalReason),
                    Is.True,
                    deathSignalReason);
                Assert.That(deathSignalReason, Is.EqualTo("eligible"));
            });

            Assert.That(
                coordinator.SelectBestEligiblePatient(
                    agent,
                    new[] { contained },
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: true,
                    out var selected,
                    out var selectionFailure),
                Is.False);
            Assert.That(selected.Valid, Is.False);
            Assert.That(selectionFailure, Is.EqualTo(LuaMRescueFailureReason.NoEligibleTarget));
            Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.Null);
            Assert.That(entities.GetComponent<PullableComponent>(contained).Puller, Is.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HostilesAndRescuePersonnelAreExcludedFromAutomaticRescue()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var otherAgent = fixture.SpawnAgent(new Vector2(1f, 0f));
            var escort = fixture.SpawnEscort(new Vector2(2f, 0f));
            var hostile = fixture.SpawnHostilePatient(
                new Vector2(3f, 0f),
                Content.Shared.Mobs.MobState.Critical);

            Assert.Multiple(() =>
            {
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        otherAgent,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var agentReason),
                    Is.False);
                Assert.That(agentReason, Is.EqualTo(LuaMRescueFailureReason.InvalidPatient));

                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        escort,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var escortReason),
                    Is.False);
                Assert.That(escortReason, Is.EqualTo(LuaMRescueFailureReason.InvalidPatient));

                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        agent,
                        hostile,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var hostileReason),
                    Is.False);
                Assert.That(hostileReason, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh));
            });

            Assert.That(
                coordinator.SelectBestEligiblePatient(
                    agent,
                    new[] { otherAgent, escort, hostile },
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var selected,
                    out var selectionFailure),
                Is.False);
            Assert.That(selected.Valid, Is.False);
            Assert.That(selectionFailure, Is.EqualTo(LuaMRescueFailureReason.NoEligibleTarget));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LiveCriticalPatientPreemptsRecoverableDeadPatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var critical = fixture.SpawnPatient(new Vector2(1f, 0f), Content.Shared.Mobs.MobState.Critical);
            var dead = fixture.SpawnPatient(new Vector2(2f, 0f));
            fixture.GiveMind(dead);
            fixture.SetMobState(dead, Content.Shared.Mobs.MobState.Dead);

            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    agent,
                    critical,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var criticalFailure),
                Is.True,
                criticalFailure.ToString());
            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    agent,
                    dead,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var deadFailure),
                Is.True,
                deadFailure.ToString());

            Assert.That(coordinator.GetPatientUrgency(critical), Is.EqualTo(LuaMRescuePatientUrgency.Critical));
            Assert.That(coordinator.GetPatientUrgency(dead), Is.EqualTo(LuaMRescuePatientUrgency.RecoverableDead));
            Assert.That(
                coordinator.GetPatientPriority(
                    agent,
                    critical,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out _),
                Is.GreaterThan(coordinator.GetPatientPriority(
                    agent,
                    dead,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out _)));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    dead,
                    destination: null,
                    out var existingIntent),
                Is.True);

            Assert.That(
                coordinator.SelectBestEligiblePatient(
                    agent,
                    new[] { dead, critical },
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var selected,
                    out var selectionFailure),
                Is.True,
                selectionFailure.ToString());
            Assert.That(selected, Is.EqualTo(critical));
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    selected,
                    destination: null,
                    out var preemptedIntent),
                Is.True);
            Assert.That(preemptedIntent.Generation, Is.EqualTo(existingIntent.Generation + 1));
            Assert.That(preemptedIntent.Target, Is.EqualTo(critical));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AgentRuntimeSwitchesFromRecoverableDeadToVisibleCriticalPatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid critical = default;
        EntityUid dead = default;
        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            agent = entities.SpawnEntity(
                LuaMRescueActivityRuntimeFixture.AgentPrototype,
                new EntityCoordinates(map.Grid, new Vector2(0.25f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.1f;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.EvacuateTargetsToShuttle = false;

            dead = entities.SpawnEntity(
                LuaMRescueActivityRuntimeFixture.PatientPrototype,
                new EntityCoordinates(map.Grid, new Vector2(0.9f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(dead);
            fixture.GiveMind(dead);
            fixture.ApplyDamage(dead, "Asphyxiation", 200);
            critical = entities.SpawnEntity(
                LuaMRescueActivityRuntimeFixture.PatientPrototype,
                new EntityCoordinates(map.Grid, new Vector2(0.65f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(critical);
            fixture.ApplyDamage(critical, "Blunt", 100);

            Assert.Multiple(() =>
            {
                Assert.That(
                    entities.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(dead).CurrentState,
                    Is.EqualTo(Content.Shared.Mobs.MobState.Dead));
                Assert.That(
                    entities.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(critical).CurrentState,
                    Is.EqualTo(Content.Shared.Mobs.MobState.Critical));
            });

            Assert.That(rescueSystem.TryOrderAgent(agent, dead, out var status), Is.True, status);
            Assert.That(rescue.AssignedTarget, Is.EqualTo(dead));
        });

        await pair.RunSeconds(0.5f);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var activity = entities.System<LuaMRescueActivityCoordinatorSystem>();
            var deadState = entities.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(dead).CurrentState;
            var criticalState = entities.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(critical).CurrentState;
            var deadEligible = activity.IsEligibleRescuePatient(
                agent,
                dead,
                LuaMRescuePatientRequestKind.AutomaticEvacuation,
                manualOverride: false,
                out var deadFailure);
            var criticalEligible = activity.IsEligibleRescuePatient(
                agent,
                critical,
                LuaMRescuePatientRequestKind.AutomaticTreatment,
                manualOverride: false,
                out var criticalFailure);
            var diagnostics =
                $"deadState={deadState}; deadEligible={deadEligible}:{deadFailure}; " +
                $"criticalState={criticalState}; criticalEligible={criticalEligible}:{criticalFailure}; " +
                $"activity={rescue.ActivityContext.Activity}:{rescue.ActivityContext.TerminalStatus}:" +
                $"{rescue.ActivityContext.FailureReason}; task={rescue.LastTaskStatus}; " +
                $"tracking={rescue.LastTargetTrackingStatus}; deferred={rescue.LastDeferredPatientStatus}; " +
                $"treat={rescue.LastAutoTreatmentStatus}; evacuation={rescue.LastAutoEvacuationStatus}";
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(critical), diagnostics);
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(critical), diagnostics);
                Assert.That(rescue.LastDeferredPatientStatus, Does.Contain("higher urgency"), diagnostics);
                Assert.That(rescue.DeferredPatientTargets.ContainsKey(dead), Is.True,
                    $"The recoverable patient displaced by local urgency must remain durable mission memory. {diagnostics}");
            });
        });

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            fixture.ApplyDamage(critical, "Blunt", -100);
            entities.GetComponent<LuaMRescueAgentComponent>(agent).AutoDefibDeadPatients = true;
            Assert.That(
                entities.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(critical).CurrentState,
                Is.EqualTo(Content.Shared.Mobs.MobState.Alive));
        });
        await pair.RunSeconds(0.5f);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var deadState = entities.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(dead).CurrentState;
            var terminalDefib = rescue.TerminalDefibrillationFailures.TryGetValue(dead, out var terminalStatus)
                ? terminalStatus
                : "none";
            var diagnostics =
                $"deadState={deadState}; activity={rescue.ActivityContext.Activity}:" +
                $"{rescue.ActivityContext.TerminalStatus}:{rescue.ActivityContext.FailureReason}; " +
                $"task={rescue.LastTaskStatus}; deferred={rescue.LastDeferredPatientStatus}; " +
                $"defib={rescue.LastAutoDefibStatus}; " +
                $"terminalDefib={terminalDefib}";
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DeferredPatientTargets.ContainsKey(dead), Is.False);
                Assert.That(rescue.AssignedTarget, Is.EqualTo(dead), diagnostics);
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(dead), diagnostics);
                Assert.That(rescue.LastDeferredPatientStatus, Does.Contain("resuming deferred"), diagnostics);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CoordinatorUsesConcreteRescuerFactionForPatientEligibility()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var rescuer = fixture.SpawnAgent(Vector2.Zero);
            var nanoTrasenPatient = fixture.SpawnPatient(
                new Vector2(1f, 0f),
                Content.Shared.Mobs.MobState.Critical);

            factions.ClearFactions(rescuer);
            factions.AddFaction(rescuer, "Syndicate");

            Assert.Multiple(() =>
            {
                Assert.That(factions.IsEntityHostile(rescuer, nanoTrasenPatient), Is.True);
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        rescuer,
                        nanoTrasenPatient,
                        LuaMRescuePatientRequestKind.AutomaticTreatment,
                        manualOverride: false,
                        out var failureReason),
                    Is.False);
                Assert.That(failureReason, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ProtectionProfileValidatesAssignedPatientsButCannotSelectMedicalTargets()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var escort = fixture.SpawnEscort(Vector2.Zero);
            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Zaslon;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Zaslon);

            var critical = fixture.SpawnPatient(new Vector2(1f, 0f), Content.Shared.Mobs.MobState.Critical);
            var dead = fixture.SpawnPatient(new Vector2(2f, 0f));
            fixture.GiveMind(dead);
            fixture.SetMobState(dead, Content.Shared.Mobs.MobState.Dead);

            Assert.Multiple(() =>
            {
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        escort,
                        critical,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var criticalFailure),
                    Is.True,
                    criticalFailure.ToString());
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        escort,
                        dead,
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out var deadFailure),
                    Is.True,
                    deadFailure.ToString());
                Assert.That(
                    coordinator.SelectBestEligiblePatient(
                        escort,
                        new[] { critical, dead },
                        LuaMRescuePatientRequestKind.AutomaticEvacuation,
                        manualOverride: false,
                        out _,
                        out var selectionFailure),
                    Is.False);
                Assert.That(selectionFailure, Is.EqualTo(LuaMRescueFailureReason.RoleDisallowed));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReplacingTargetAdvancesGenerationAndRejectsStaleProgress()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var firstTarget = fixture.SpawnPatient(new Vector2(1f, 0f));
            var secondTarget = fixture.SpawnPatient(new Vector2(2f, 0f));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    firstTarget,
                    destination: null,
                    out var first),
                Is.True);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    firstTarget,
                    destination: null,
                    out var same),
                Is.True);
            Assert.That(same.Generation, Is.EqualTo(first.Generation));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    secondTarget,
                    destination: null,
                    out var replacement),
                Is.True);
            Assert.That(replacement.Generation, Is.EqualTo(first.Generation + 1));
            Assert.That(replacement.Target, Is.EqualTo(secondTarget));

            Assert.That(
                coordinator.RecordProgress(
                    agent,
                    first.Generation,
                    destination: null,
                    remainingDistance: 2f,
                    LuaMRescueRouteStatus.Moving,
                    LuaMRescueDoAfterStatus.None,
                    out var stale),
                Is.False);
            Assert.That(stale.Generation, Is.EqualTo(replacement.Generation));
            Assert.That(stale.Target, Is.EqualTo(replacement.Target));
            Assert.That(stale.FailureReason, Is.EqualTo(LuaMRescueFailureReason.StaleGeneration));
            Assert.That(coordinator.GetSnapshot(agent, out var afterStale), Is.True);
            Assert.That(afterStale, Is.EqualTo(replacement));

            Assert.That(
                coordinator.RecordProgress(
                    agent,
                    replacement.Generation,
                    destination: null,
                    remainingDistance: 1f,
                    LuaMRescueRouteStatus.Moving,
                    LuaMRescueDoAfterStatus.None,
                    out var progressed),
                Is.True);
            Assert.That(progressed.Generation, Is.EqualTo(replacement.Generation));
            Assert.That(coordinator.Complete(agent, progressed.Generation, out var completed), Is.True);
            Assert.That(completed.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Succeeded));
            Assert.That(completed.Generation, Is.EqualTo(replacement.Generation));

            Assert.That(
                coordinator.RecordProgress(
                    agent,
                    completed.Generation,
                    destination: null,
                    remainingDistance: 0f,
                    LuaMRescueRouteStatus.Arrived,
                    LuaMRescueDoAfterStatus.Succeeded,
                    out var afterTerminal),
                Is.False);
            Assert.That(afterTerminal, Is.EqualTo(completed));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CancellationUsesCancelledTerminalAndRejectsStaleGeneration()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var patient = fixture.SpawnPatient(new Vector2(1f, 0f));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    destination: null,
                    out var active),
                Is.True);

            Assert.That(
                coordinator.Cancel(
                    agent,
                    active.Generation + 1,
                    LuaMRescueFailureReason.Cancelled,
                    out var stale),
                Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(stale.Generation, Is.EqualTo(active.Generation));
                Assert.That(stale.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(stale.FailureReason, Is.EqualTo(LuaMRescueFailureReason.StaleGeneration));
            });
            Assert.That(coordinator.GetSnapshot(agent, out var afterStale), Is.True);
            Assert.That(afterStale, Is.EqualTo(active));

            Assert.That(
                coordinator.Cancel(
                    agent,
                    active.Generation,
                    LuaMRescueFailureReason.Cancelled,
                    out var cancelled),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(cancelled.Generation, Is.EqualTo(active.Generation));
                Assert.That(cancelled.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Cancelled));
                Assert.That(cancelled.FailureReason, Is.EqualTo(LuaMRescueFailureReason.Cancelled));
                Assert.That(cancelled.IsTerminal, Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExpiredDeadlineBecomesImmutableTerminalFailure()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();
        EntityUid agent = default;
        uint generation = 0;
        LuaMRescueActivitySnapshot forcedDeadline = default;

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            agent = fixture.SpawnAgent(Vector2.Zero);
            var patient = fixture.SpawnPatient(new Vector2(1f, 0f));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    patient,
                    destination: null,
                    out var active),
                Is.True);
            generation = active.Generation;

            entities.GetComponent<LuaMRescueAgentComponent>(agent).ActivityContext.Deadline =
                TimeSpan.FromTicks(1);
            Assert.That(coordinator.GetSnapshot(agent, out forcedDeadline), Is.True);
            Assert.That(forcedDeadline.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            Assert.That(forcedDeadline.Deadline, Is.EqualTo(TimeSpan.FromTicks(1)));
        });

        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(coordinator.GetSnapshot(agent, out var timedOut), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(timedOut.Generation, Is.EqualTo(generation));
                Assert.That(timedOut.IsTerminal, Is.True);
                Assert.That(timedOut.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(timedOut.FailureReason, Is.EqualTo(LuaMRescueFailureReason.DeadlineExceeded));
                Assert.That(timedOut.Fallback, Is.EqualTo(LuaMRescueActivity.Standby));
                Assert.That(timedOut.Blocked, Is.False);
                Assert.That(timedOut.LastTransitionAt, Is.GreaterThanOrEqualTo(forcedDeadline.LastTransitionAt));
            });

            Assert.That(coordinator.RecordAttempt(agent, generation, out var rejectedAttempt), Is.False);
            Assert.That(rejectedAttempt, Is.EqualTo(timedOut));
            Assert.That(
                coordinator.RecordProgress(
                    agent,
                    generation,
                    destination: null,
                    remainingDistance: 0f,
                    LuaMRescueRouteStatus.Arrived,
                    LuaMRescueDoAfterStatus.Succeeded,
                    out var rejectedProgress),
                Is.False);
            Assert.That(rejectedProgress, Is.EqualTo(timedOut));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AttemptLimitAndBlockedActivitiesAreTerminalAndImmutable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var patient = fixture.SpawnPatient(new Vector2(1f, 0f));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    patient,
                    destination: null,
                    out var active),
                Is.True);

            var maxAttempts = entities.GetComponent<LuaMRescueAgentComponent>(agent).ActivityRoleProfile.MaxAttempts;
            var attempt = active;
            for (var i = 1; i <= maxAttempts; i++)
            {
                Assert.That(
                    coordinator.RecordAttempt(agent, active.Generation, out attempt),
                    Is.EqualTo(i < maxAttempts));
                Assert.That(attempt.Attempts, Is.EqualTo(i));
            }

            Assert.That(attempt.IsTerminal, Is.True);
            Assert.That(attempt.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
            Assert.That(attempt.FailureReason, Is.EqualTo(LuaMRescueFailureReason.AttemptLimitReached));

            Assert.That(coordinator.RecordAttempt(agent, attempt.Generation, out var rejectedAttempt), Is.False);
            Assert.That(rejectedAttempt, Is.EqualTo(attempt));
            Assert.That(
                coordinator.RecordProgress(
                    agent,
                    attempt.Generation,
                    destination: null,
                    remainingDistance: 0f,
                    LuaMRescueRouteStatus.Arrived,
                    LuaMRescueDoAfterStatus.Succeeded,
                    out var rejectedProgress),
                Is.False);
            Assert.That(rejectedProgress, Is.EqualTo(attempt));

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    patient,
                    destination: null,
                    out var restarted),
                Is.True);
            Assert.That(restarted.Generation, Is.EqualTo(attempt.Generation + 1));
            Assert.That(restarted.Attempts, Is.Zero);

            Assert.That(
                coordinator.Block(
                    agent,
                    restarted.Generation,
                    LuaMRescueFailureReason.RouteBlocked,
                    LuaMRescueActivity.Resupply,
                    out var blocked),
                Is.True);
            Assert.That(blocked.IsTerminal, Is.True);
            Assert.That(blocked.Blocked, Is.True);
            Assert.That(blocked.FailureReason, Is.EqualTo(LuaMRescueFailureReason.RouteBlocked));
            Assert.That(blocked.Fallback, Is.EqualTo(LuaMRescueActivity.Resupply));

            Assert.That(coordinator.RecordAttempt(agent, blocked.Generation, out var blockedAttempt), Is.False);
            Assert.That(blockedAttempt, Is.EqualTo(blocked));
            Assert.That(
                coordinator.RecordProgress(
                    agent,
                    blocked.Generation,
                    destination: null,
                    remainingDistance: 1f,
                    LuaMRescueRouteStatus.Blocked,
                    LuaMRescueDoAfterStatus.None,
                    out var blockedProgress),
                Is.False);
            Assert.That(blockedProgress, Is.EqualTo(blocked));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BlockedRouteCanRecoverAfterBackoffButKeepsBoundedAttemptBudget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var agent = fixture.SpawnAgent(Vector2.Zero);
            var anchor = fixture.SpawnPatient(new Vector2(2f, 0f));
            var destination = new Robust.Shared.Map.EntityCoordinates(anchor, Vector2.Zero);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ReturnToShuttle,
                    anchor,
                    destination,
                    out var active),
                Is.True);

            var generation = active.Generation;
            var maxAttempts = entities.GetComponent<LuaMRescueAgentComponent>(agent)
                .ActivityRoleProfile.MaxAttempts;
            for (var failedExecution = 1; failedExecution <= maxAttempts; failedExecution++)
            {
                Assert.That(
                    coordinator.Block(
                        agent,
                        LuaMRescueFailureReason.NoPath,
                        LuaMRescueActivity.Standby,
                        out var blocked),
                    Is.True);
                Assert.That(blocked.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));

                if (failedExecution == 1)
                {
                    Assert.That(
                        coordinator.TryRecoverBlockedIntent(
                            agent,
                            blocked.Generation + 1,
                            LuaMRescueActivity.ReturnToShuttle,
                            anchor,
                            destination,
                            out var staleRetryAfter,
                            out var staleRecovery),
                        Is.False);
                    Assert.Multiple(() =>
                    {
                        Assert.That(staleRetryAfter, Is.EqualTo(TimeSpan.Zero));
                        Assert.That(staleRecovery.FailureReason, Is.EqualTo(LuaMRescueFailureReason.StaleGeneration));
                        Assert.That(staleRecovery.Generation, Is.EqualTo(blocked.Generation));
                        Assert.That(staleRecovery.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                    });
                    Assert.That(coordinator.GetSnapshot(agent, out var afterStaleRecovery), Is.True);
                    Assert.That(afterStaleRecovery, Is.EqualTo(blocked));
                }

                // Simulate expiry of the deterministic backoff without wall-clock sleeps.
                entities.GetComponent<LuaMRescueAgentComponent>(agent)
                    .ActivityContext.RetryNotBefore = TimeSpan.Zero;
                var recovered = coordinator.TryRecoverBlockedIntent(
                    agent,
                    blocked.Generation,
                    LuaMRescueActivity.ReturnToShuttle,
                    anchor,
                    destination,
                    out var retryAfter,
                    out var snapshot);

                Assert.That(retryAfter, Is.EqualTo(TimeSpan.Zero));
                if (failedExecution < maxAttempts)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(recovered, Is.True);
                        Assert.That(snapshot.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                        Assert.That(snapshot.Attempts, Is.EqualTo(failedExecution));
                        Assert.That(snapshot.Generation, Is.GreaterThan(generation));
                    });
                    generation = snapshot.Generation;
                }
                else
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(recovered, Is.False);
                        Assert.That(snapshot.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                        Assert.That(snapshot.Attempts, Is.EqualTo(maxAttempts));
                        Assert.That(snapshot.Generation, Is.EqualTo(generation));
                    });
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RescuePlannerMatchesTopicalMedicineAndRejectsEmptyMedipen()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var rescue = entities.System<LuaMRescueAgentSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var fixture = new LuaMRescueActivityRuntimeFixture(entities, map.MapId);
            var brutePatient = fixture.SpawnPatient(Vector2.Zero);
            fixture.ApplyDamage(brutePatient, "Blunt", 20);
            var burnPatient = fixture.SpawnPatient(new Vector2(1f, 0f));
            fixture.ApplyDamage(burnPatient, "Heat", 20);
            var brutepack = entities.SpawnEntity("Brutepack", map.MapCoords);
            var ointment = entities.SpawnEntity("Ointment", map.MapCoords);
            var medipen = entities.SpawnEntity("EmergencyMedipen", map.MapCoords);

            Assert.That(
                solutions.TryGetSolution(medipen, "pen", out var solutionEntity, out var solution),
                Is.True);
            solutions.SplitSolution(solutionEntity.Value, solution.Volume);

            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.IsEffectiveTreatmentItem(brutepack, brutePatient, out var bruteMatchReason),
                    Is.True,
                    bruteMatchReason);
                Assert.That(bruteMatchReason, Is.EqualTo("None"));
                Assert.That(
                    rescue.IsEffectiveTreatmentItem(ointment, brutePatient, out var ointmentRejectReason),
                    Is.False);
                Assert.That(ointmentRejectReason, Is.EqualTo("NoEffectiveMedicine"));

                Assert.That(
                    rescue.IsEffectiveTreatmentItem(ointment, burnPatient, out var burnMatchReason),
                    Is.True,
                    burnMatchReason);
                Assert.That(burnMatchReason, Is.EqualTo("None"));
                Assert.That(
                    rescue.IsEffectiveTreatmentItem(brutepack, burnPatient, out var brutepackRejectReason),
                    Is.False);
                Assert.That(brutepackRejectReason, Is.EqualTo("NoEffectiveMedicine"));

                Assert.That(
                    rescue.IsEffectiveTreatmentItem(medipen, brutePatient, out var emptyReason),
                    Is.False);
                Assert.That(emptyReason, Is.EqualTo("ItemEmpty"));
            });
        });

        await pair.CleanReturnAsync();
    }
}
