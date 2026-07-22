using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Atmos.Components;
using Content.Server.Mind;
using Content.Server._LuaM.Rescue;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueShuttleSystem))]
public sealed class LuaMRescueTerminalPolicyRuntimeTest
{
    [Test]
    public async Task ArrivedWithoutDockingTimesOutAndManualRetryStartsFreshBudget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        var initialGeneration = 0;

        await server.WaitAssertion(() =>
        {
            shuttle = entities.SpawnEntity(null, map.MapCoords);
            var target = entities.SpawnEntity(null, map.MapCoords);
            var lifecycle = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Arrived;
            lifecycle.Target = target;
            lifecycle.ArrivalRange = 2f;
            lifecycle.MaxRetries = 1;
            lifecycle.RetryCount = 1;
            lifecycle.RouteGeneration = 17;
            lifecycle.RouteStartedAt = timing.CurTime - TimeSpan.FromSeconds(1);
            lifecycle.RouteDeadline = timing.CurTime + TimeSpan.FromSeconds(0.1);
            lifecycle.StateChangedAt = timing.CurTime;
            initialGeneration = lifecycle.RouteGeneration;

            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var arrived), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(arrived.State, Is.EqualTo(LuaMRescueShuttleRouteState.Arrived));
                Assert.That(arrived.Terminal, Is.False);
                Assert.That(arrived.RetryCount, Is.EqualTo(arrived.MaxRetries));
            });
        });

        await pair.RunSeconds(0.7f);

        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var timedOut), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(timedOut.State, Is.EqualTo(LuaMRescueShuttleRouteState.TimedOut));
                Assert.That(timedOut.Terminal, Is.True);
                Assert.That(timedOut.RouteGeneration, Is.EqualTo(initialGeneration));
                Assert.That(timedOut.NextRetryAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(timedOut.LastStatus, Does.Contain("docking was not confirmed").IgnoreCase);
            });

            Assert.That(rescue.TryRetryAutopilotRoute(shuttle, out var retryStatus), Is.False);
            Assert.That(retryStatus, Does.Contain("console").IgnoreCase);
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var retried), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(retried.State, Is.EqualTo(LuaMRescueShuttleRouteState.Failed));
                Assert.That(retried.RouteGeneration, Is.EqualTo(initialGeneration + 1));
                Assert.That(retried.RetryCount, Is.Zero);
                Assert.That(retried.Terminal, Is.False);
                Assert.That(retried.NextRetryAt, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(retried.RouteDeadline, Is.GreaterThan(timedOut.RouteDeadline));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PendingDispatchStopsAfterThreeAttemptsAndRequiresExplicitRetry()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();
        EntityUid target = default;

        await server.WaitAssertion(() =>
        {
            target = SpawnCriticalPatient(entities, mobState, map.MapId, Vector2.Zero);

            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);
            Assert.That(queueStatus, Does.StartWith("queued:").IgnoreCase);

            var queued = rescue.GetPendingAutomaticDispatches().Single(entry => entry.Target == target);
            Assert.Multiple(() =>
            {
                Assert.That(queued.Attempts, Is.Zero);
                Assert.That(queued.Terminal, Is.False);
                Assert.That(queued.Deadline, Is.GreaterThan(queued.EnqueuedAt));
            });

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var processingBudget = rescue.PendingAutomaticDispatchCount + 1;
                for (var i = 0; i < processingBudget; i++)
                {
                    Assert.That(rescue.ProcessPendingAutomaticDispatchesNow(), Is.True);
                    if (rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target) ||
                        rescue.GetPendingAutomaticDispatches()
                            .Any(entry => entry.Target == target && entry.Attempts >= attempt))
                    {
                        break;
                    }
                }

                if (attempt < 3)
                {
                    var pending = rescue.GetPendingAutomaticDispatches().Single(entry => entry.Target == target);
                    Assert.Multiple(() =>
                    {
                        Assert.That(pending.Attempts, Is.EqualTo(attempt));
                        Assert.That(pending.Terminal, Is.False);
                        Assert.That(pending.NextAttemptAt, Is.GreaterThan(timing.CurTime));
                    });
                    AdvanceSimulationClock(timing, TimeSpan.FromSeconds(11));
                }
            }

            Assert.That(rescue.PendingAutomaticDispatchCount, Is.Zero);
            var terminal = rescue.GetTerminalAutomaticDispatches().Single(entry => entry.Target == target);
            Assert.Multiple(() =>
            {
                Assert.That(terminal.Attempts, Is.EqualTo(3));
                Assert.That(terminal.Terminal, Is.True);
                Assert.That(terminal.NextAttemptAt, Is.EqualTo(TimeSpan.Zero));
                Assert.That(terminal.LastStatus, Does.Contain("after 3 attempts").IgnoreCase);
            });

            var availableAgent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var availableRescue = entities.GetComponent<LuaMRescueAgentComponent>(availableAgent);
            availableRescue.AutoAcquireTargets = false;
            availableRescue.AssignedTarget = target;
            availableRescue.TaskPatientTarget = target;

            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var implicitRetryStatus),
                Is.False);
            Assert.That(implicitRetryStatus, Does.Contain("explicit retry").IgnoreCase);
            Assert.That(
                rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
                Is.True,
                "Even an available active owner must not bypass the explicit terminal retry gate.");

            Assert.That(rescue.TryRetryTerminalAutomaticDispatch(target, out var manualRetryStatus), Is.True);
            Assert.That(manualRetryStatus, Does.Contain("manual dispatch retry").IgnoreCase);
            Assert.That(rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target), Is.False);

            var retried = rescue.GetPendingAutomaticDispatches().Single(entry => entry.Target == target);
            Assert.Multiple(() =>
            {
                Assert.That(retried.Attempts, Is.Zero);
                Assert.That(retried.Terminal, Is.False);
                Assert.That(retried.EnqueuedAt, Is.GreaterThan(terminal.EnqueuedAt));
                Assert.That(retried.Deadline, Is.GreaterThan(terminal.Deadline));
                Assert.That(retried.NextAttemptAt, Is.EqualTo(retried.EnqueuedAt));
            });

            DiscardPendingTarget(entities, timing, rescue, target);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(LuaMRescueMedicalSignalKind.FollowUp)]
    [TestCase(LuaMRescueMedicalSignalKind.Critical)]
    public async Task DeathEscalationResetsPendingBudgetAndSupersedesTerminalGate(
        LuaMRescueMedicalSignalKind initialKind)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            EntityUid SpawnRecoverablePatient(string label, Vector2 position)
            {
                var patient = entities.SpawnEntity(
                    "MobHuman",
                    new MapCoordinates(position, map.MapId));
                // Attach the actor only after setting the initial state so the
                // test remains the sole producer of dispatch signals.
                mobState.ChangeMobState(patient, MobState.Critical);
                var mind = minds.CreateMind(null, label);
                minds.TransferTo(mind, patient, createGhost: false, mind: mind.Comp);
                entities.EnsureComponent<ActorComponent>(patient);
                return patient;
            }

            void EscalateToDeathWithoutMobStateSignal(EntityUid patient)
            {
                entities.RemoveComponent<ActorComponent>(patient);
                mobState.ChangeMobState(patient, MobState.Dead);
                entities.EnsureComponent<ActorComponent>(patient);
            }

            var pendingTarget = SpawnRecoverablePatient(
                $"LuaM pending escalation {initialKind}",
                Vector2.Zero);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(pendingTarget, initialKind, out var queueStatus),
                Is.True,
                queueStatus);

            for (var processingAttempt = 0; processingAttempt < 8; processingAttempt++)
            {
                var current = shuttle.GetPendingAutomaticDispatches()
                    .Single(entry => entry.Target == pendingTarget);
                if (current.Attempts >= 2)
                    break;

                if (current.NextAttemptAt > timing.CurTime)
                    AdvanceSimulationClock(timing, current.NextAttemptAt - timing.CurTime);
                Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            }
            var spentPending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == pendingTarget);
            Assert.That(spentPending.Attempts, Is.EqualTo(2));

            EscalateToDeathWithoutMobStateSignal(pendingTarget);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    pendingTarget,
                    LuaMRescueMedicalSignalKind.Death,
                    out var escalationStatus),
                Is.True,
                escalationStatus);
            var escalatedPending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == pendingTarget);
            Assert.Multiple(() =>
            {
                Assert.That(escalatedPending.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.Death));
                Assert.That(escalatedPending.Attempts, Is.Zero,
                    "A strict urgency escalation must receive a fresh bounded-attempt budget.");
                Assert.That(escalatedPending.EnqueuedAt, Is.GreaterThan(spentPending.EnqueuedAt));
                Assert.That(escalatedPending.Deadline, Is.GreaterThan(spentPending.Deadline));
                Assert.That(escalatedPending.NextAttemptAt, Is.EqualTo(escalatedPending.EnqueuedAt));
            });

            AdvanceSimulationClock(
                timing,
                escalatedPending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
            ProcessPendingUntilTerminal(shuttle, pendingTarget);
            var deathTerminal = shuttle.GetTerminalAutomaticDispatches()
                .Single(entry => entry.Target == pendingTarget);
            Assert.That(deathTerminal.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.Death));
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    pendingTarget,
                    LuaMRescueMedicalSignalKind.Death,
                    out var sameKindStatus),
                Is.False);
            Assert.That(sameKindStatus, Does.Contain("explicit retry").IgnoreCase,
                "An equal-urgency repeat must not bypass a terminal dispatch gate.");
            entities.DeleteEntity(pendingTarget);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.False,
                "Target termination must synchronously purge its dispatch state.");

            var terminalTarget = SpawnRecoverablePatient(
                $"LuaM terminal escalation {initialKind}",
                new Vector2(1f, 0f));
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(terminalTarget, initialKind, out queueStatus),
                Is.True,
                queueStatus);
            var initialPending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == terminalTarget);
            AdvanceSimulationClock(
                timing,
                initialPending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
            ProcessPendingUntilTerminal(shuttle, terminalTarget);
            var initialTerminal = shuttle.GetTerminalAutomaticDispatches()
                .Single(entry => entry.Target == terminalTarget);
            Assert.That(initialTerminal.Kind, Is.EqualTo(initialKind));

            EscalateToDeathWithoutMobStateSignal(terminalTarget);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    terminalTarget,
                    LuaMRescueMedicalSignalKind.Death,
                    out escalationStatus),
                Is.True,
                escalationStatus);
            var terminalEscalation = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == terminalTarget);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == terminalTarget),
                    Is.False,
                    "A strict escalation must supersede the obsolete lower-urgency terminal record.");
                Assert.That(terminalEscalation.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.Death));
                Assert.That(terminalEscalation.Attempts, Is.Zero);
                Assert.That(terminalEscalation.EnqueuedAt, Is.GreaterThan(initialTerminal.EnqueuedAt));
                Assert.That(terminalEscalation.Deadline, Is.GreaterThan(initialTerminal.Deadline));
            });

            entities.DeleteEntity(terminalTarget);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.False,
                "Target termination must synchronously purge its dispatch state.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RequiredFollowUpTerminalEscalationCreatesFreshOwnerWithInheritedInfrastructure()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var setup = CreateRequiredFollowUpTerminal(
                entities,
                timing,
                shuttle,
                damageable,
                map.MapId,
                Vector2.Zero);

            // Suppress the implicit MobState signal so the test is the sole
            // producer of the strict FollowUp -> Critical escalation.
            mobState.ChangeMobState(setup.Patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(setup.Patient);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    setup.Patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var escalationStatus),
                Is.True,
                escalationStatus);

            var escalated = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == setup.Patient);
            Assert.Multiple(() =>
            {
                Assert.That(escalated.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.Critical));
                Assert.That(escalated.Attempts, Is.Zero);
                Assert.That(escalated.Terminal, Is.False);
                Assert.That(escalated.EnqueuedAt, Is.GreaterThan(setup.Terminal.EnqueuedAt));
                Assert.That(escalated.Deadline, Is.GreaterThan(setup.Terminal.Deadline));
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches()
                        .Any(entry => entry.Target == setup.Patient),
                    Is.False);
            });

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(agents.TryFindActiveAgent(out var replacement, out var rescue), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(setup.Patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(setup.Patient));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(setup.Patient));
                Assert.That(
                    rescue.ActivityContext.TerminalStatus,
                    Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.RequiredOnboardHandoffPatients, Does.Contain(setup.Patient),
                    "Strict escalation must not shed the accepted physical-custody contract.");
                Assert.That(rescue.AssignedShuttle, Is.EqualTo(setup.AssignedShuttle));
                Assert.That(rescue.AssignedShuttleAnchor, Is.EqualTo(setup.AssignedAnchor));
                Assert.That(rescue.AssignedShuttleConsole, Is.EqualTo(setup.AssignedConsole));
                Assert.That(rescue.AssignedReturnTarget, Is.EqualTo(setup.ReturnTarget));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == setup.Patient),
                    Is.False);
            });

            entities.DeleteEntity(setup.Patient);
            entities.DeleteEntity(replacement);
            entities.DeleteEntity(setup.AssignedShuttle);
            entities.DeleteEntity(setup.AssignedAnchor);
            entities.DeleteEntity(setup.AssignedConsole);
            entities.DeleteEntity(setup.ReturnTarget);
            shuttle.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ActiveOrExplicitOwnerAdoptsRequiredEscalationProvenance(
        bool explicitOrder)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var setup = CreateRequiredFollowUpTerminal(
                entities,
                timing,
                shuttle,
                damageable,
                map.MapId,
                Vector2.Zero);

            mobState.ChangeMobState(setup.Patient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(setup.Patient);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    setup.Patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var escalationStatus),
                Is.True,
                escalationStatus);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches()
                    .Single(entry => entry.Target == setup.Patient)
                    .Attempts,
                Is.Zero);

            Assert.That(
                agents.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out var replacement,
                    out var spawnStatus),
                Is.True,
                spawnStatus);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            rescue.AutoAcquireTargets = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.EvacuateTargetsToShuttle = false;

            if (explicitOrder)
            {
                Assert.That(
                    agents.TryOrderAgent(replacement, setup.Patient, out var orderStatus),
                    Is.True,
                    orderStatus);
            }
            else
            {
                Assert.That(
                    coordinator.BeginOrReplaceIntent(
                        replacement,
                        LuaMRescueRole.Aibolit,
                        LuaMRescueActivity.ApproachPatient,
                        setup.Patient,
                        new EntityCoordinates(setup.Patient, Vector2.Zero),
                        out _),
                    Is.True);
                rescue.AssignedTarget = setup.Patient;
                rescue.TaskPatientTarget = setup.Patient;
                rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
                Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            }

            Assert.Multiple(() =>
            {
                Assert.That(rescue.RequiredOnboardHandoffPatients, Does.Contain(setup.Patient));
                Assert.That(rescue.AssignedShuttle, Is.EqualTo(setup.AssignedShuttle));
                Assert.That(rescue.AssignedShuttleAnchor, Is.EqualTo(setup.AssignedAnchor));
                Assert.That(rescue.AssignedShuttleConsole, Is.EqualTo(setup.AssignedConsole));
                Assert.That(rescue.AssignedReturnTarget, Is.EqualTo(setup.ReturnTarget));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == setup.Patient),
                    Is.False,
                    "Accepted active ownership must consume the obsolete queued diagnostic.");
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == setup.Patient),
                    Is.False);
            });

            entities.DeleteEntity(setup.Patient);
            entities.DeleteEntity(replacement);
            entities.DeleteEntity(setup.AssignedShuttle);
            entities.DeleteEntity(setup.AssignedAnchor);
            entities.DeleteEntity(setup.AssignedConsole);
            entities.DeleteEntity(setup.ReturnTarget);
            shuttle.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task QueuedFreshEpisodeReplacesTerminalContextDespiteRetainedAssignmentMirrors()
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
            var patient = SpawnCriticalPatient(entities, mobState, map.MapId, Vector2.Zero);
            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True);
            Assert.That(
                coordinator.Fail(agent, LuaMRescueFailureReason.Unknown, out var terminal),
                Is.True);
            Assert.That(terminal.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));

            // Preserve the compatibility mirrors on purpose. They describe the
            // intended patient, but a terminal ActivityContext is not ownership.
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;

            mobState.ChangeMobState(agent, MobState.Critical);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);
            Assert.That(queueStatus, Does.Contain("incapacitated").IgnoreCase);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                Is.True);
            Assert.That(coordinator.GetSnapshot(agent, out var stillTerminal), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(stillTerminal.Generation, Is.EqualTo(terminal.Generation));
                Assert.That(stillTerminal.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
            });

            mobState.ChangeMobState(agent, MobState.Alive);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(coordinator.GetSnapshot(agent, out var fresh), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False,
                    "Legacy mirrors must not consume a queued signal while the authoritative context is terminal.");
                Assert.That(fresh.Generation, Is.GreaterThan(terminal.Generation));
                Assert.That(fresh.Target, Is.EqualTo(patient));
                Assert.That(fresh.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
            });

            entities.DeleteEntity(patient);
            entities.DeleteEntity(agent);
            shuttle.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PendingDispatchDeadlineTerminalizesBeforeFirstRetry()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();
        EntityUid target = default;

        await server.WaitAssertion(() =>
        {
            target = SpawnCriticalPatient(entities, mobState, map.MapId, Vector2.Zero);
            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);

            var pending = rescue.GetPendingAutomaticDispatches().Single(entry => entry.Target == target);
            Assert.That(pending.Attempts, Is.Zero);

            AdvanceSimulationClock(timing, pending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
            var processingBudget = rescue.PendingAutomaticDispatchCount + 1;
            for (var i = 0; i < processingBudget &&
                            rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target); i++)
            {
                Assert.That(rescue.ProcessPendingAutomaticDispatchesNow(), Is.True);
            }

            Assert.That(
                rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                Is.False);
            var terminal = rescue.GetTerminalAutomaticDispatches().Single(entry => entry.Target == target);
            Assert.Multiple(() =>
            {
                Assert.That(terminal.Attempts, Is.Zero);
                Assert.That(terminal.Terminal, Is.True);
                Assert.That(terminal.LastStatus, Does.Contain("deadline exceeded").IgnoreCase);
            });

            Assert.That(rescue.TryRetryTerminalAutomaticDispatch(target, out var retryStatus), Is.True);
            Assert.That(retryStatus, Does.Contain("manual dispatch retry").IgnoreCase);
            var retried = rescue.GetPendingAutomaticDispatches().Single(entry => entry.Target == target);
            Assert.That(retried.Deadline, Is.GreaterThan(timing.CurTime));
            Assert.That(retried.Attempts, Is.Zero);
            Assert.That(retried.Terminal, Is.False);

            DiscardPendingTarget(entities, timing, rescue, target);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SuccessfulDirectRedispatchConsumesExistingPendingRecord()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var target = SpawnCriticalPatient(entities, mobState, map.MapId, Vector2.Zero);
            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);

            Assert.That(
                rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                Is.True);
            Assert.That(
                rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
                Is.False);

            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var active = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            active.AutoAcquireTargets = false;
            active.AssignedTarget = target;
            active.TaskPatientTarget = target;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    target,
                    new EntityCoordinates(target, Vector2.Zero),
                    out _),
                Is.True);

            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var redispatchStatus),
                Is.True,
                redispatchStatus);
            Assert.Multiple(() =>
            {
                Assert.That(redispatchStatus, Does.Contain("refreshed"));
                Assert.That(
                    rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
                    Is.False);
                Assert.That(
                    rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                    Is.False,
                    "A successful active owner must consume the older pending queue entry.");
                Assert.That(active.AssignedTarget, Is.EqualTo(target));
                Assert.That(active.TaskPatientTarget, Is.EqualTo(target));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExpiredPendingReconcilesWithActiveOwnerBeforeTerminalization()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var target = SpawnCriticalPatient(entities, mobState, map.MapId, Vector2.Zero);
            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);
            var pending = rescue.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == target);
            AdvanceSimulationClock(timing, pending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));

            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var active = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            active.AutoAcquireTargets = false;
            active.AssignedTarget = target;
            active.TaskPatientTarget = target;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    target,
                    new EntityCoordinates(target, Vector2.Zero),
                    out _),
                Is.True);

            Assert.That(rescue.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(
                    rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                    Is.False);
                Assert.That(
                    rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
                    Is.False,
                    "An already active mission must not inherit a stale queue deadline terminal.");
                Assert.That(active.AssignedTarget, Is.EqualTo(target));
                Assert.That(active.TaskPatientTarget, Is.EqualTo(target));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AuthoritativeActivityOwnershipConsumesStaleTerminalWithoutIntentChurn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var target = SpawnCriticalPatient(entities, mobState, map.MapId, Vector2.Zero);
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var queueStatus),
                Is.True,
                queueStatus);
            var pending = shuttle.GetPendingAutomaticDispatches().Single(entry => entry.Target == target);
            AdvanceSimulationClock(timing, pending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(
                shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
                Is.True);

            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var active = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            active.AutoAcquireTargets = false;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    target,
                    new EntityCoordinates(target, Vector2.Zero),
                    out var owned),
                Is.True);
            active.AssignedTarget = null;
            active.DeathSignalTarget = null;
            active.EvacuatingTarget = null;
            active.OnboardCareTarget = null;
            active.TaskPatientTarget = null;
            active.ManualOverrideTarget = null;

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(
                shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
                Is.False,
                "A dispatch terminal is obsolete once the generic lifecycle authoritatively owns the patient.");

            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    target,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var refreshStatus),
                Is.True,
                refreshStatus);
            Assert.That(refreshStatus, Does.Contain("refreshed"));
            Assert.That(coordinator.GetSnapshot(agent, out var refreshed), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(refreshed.Generation, Is.EqualTo(owned.Generation));
                Assert.That(refreshed.Activity, Is.EqualTo(LuaMRescueActivity.TreatPatient));
                Assert.That(refreshed.Target, Is.EqualTo(target));
                Assert.That(refreshed.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(active.AssignedTarget, Is.Null,
                    "Refreshing an ActivityContext-only owner must not rebuild legacy mirrors or replace its intent.");
            });

            var equalUrgency = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                new Vector2(1f, 0f));
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    equalUrgency,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var busyStatus),
                Is.True,
                busyStatus);
            Assert.That(busyStatus, Does.Contain("queued:").IgnoreCase);
            Assert.That(coordinator.GetSnapshot(agent, out var stillOwned), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(stillOwned.Generation, Is.EqualTo(owned.Generation));
                Assert.That(stillOwned.Target, Is.EqualTo(target));
                Assert.That(stillOwned.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == equalUrgency),
                    Is.True,
                    "An equal-urgency signal must wait instead of replacing the authoritative activity owner.");
            });
            DiscardPendingTarget(entities, timing, shuttle, equalUrgency);

            var deletedTerminal = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                new Vector2(2f, 0f));
            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    deletedTerminal,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var deletedQueueStatus),
                Is.True,
                deletedQueueStatus);
            var deletedPending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == deletedTerminal);
            AdvanceSimulationClock(
                timing,
                deletedPending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(
                shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == deletedTerminal),
                Is.True);
            entities.DeleteEntity(deletedTerminal);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.False,
                "Target termination must synchronously purge its dispatch state.");
            Assert.That(
                shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == deletedTerminal),
                Is.False,
                "Deleted patients must not leak terminal dispatch records indefinitely.");
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(MobState.Critical)]
    [TestCase(MobState.Dead)]
    public async Task DiscardedSignalReleasesCooldownForNewMedicalEpisode(MobState episodeState)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var session = server.ResolveDependency<IPlayerManager>()
            .GetSessionById(clientSession!.UserId);

        await server.WaitAssertion(() =>
        {
            var target = entities.SpawnEntity("MobHuman", map.MapCoords);
            var mind = minds.CreateMind(null, $"LuaM cooldown episode {episodeState}");
            minds.TransferTo(mind, target, createGhost: false, mind: mind.Comp);
            server.PlayerMan.SetAttachedEntity(session, target, true);
            Assert.That(entities.HasComponent<ActorComponent>(target), Is.True);

            mobState.ChangeMobState(target, episodeState);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                Is.True,
                "The first real MobState episode must be accepted and queued.");

            mobState.ChangeMobState(target, MobState.Alive);
            shuttle.ProcessPendingAutomaticDispatchesNow();
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                Is.False,
                "Recovery must discard the obsolete queued episode.");

            if (!entities.HasComponent<ActorComponent>(target))
                server.PlayerMan.SetAttachedEntity(session, target, true);
            mobState.ChangeMobState(target, episodeState);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                Is.True,
                "A distinct medical episode before the old cooldown window expires must not be lost.");

            server.PlayerMan.SetAttachedEntity(session, null, true);
            entities.DeleteEntity(target);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.False,
                "Target termination must synchronously purge its dispatch state.");
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
                Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecoveredTerminalEpisodeCannotPoisonFreshCriticalIntentOrBudget()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var session = server.ResolveDependency<IPlayerManager>()
            .GetSessionById(clientSession!.UserId);

        await server.WaitAssertion(() =>
        {
            var patient = entities.SpawnEntity("MobHuman", map.MapCoords);
            var mind = minds.CreateMind(null, "LuaM fresh critical episode regression");
            minds.TransferTo(mind, patient, createGhost: false, mind: mind.Comp);
            server.PlayerMan.SetAttachedEntity(session, patient, true);
            Assert.That(entities.HasComponent<ActorComponent>(patient), Is.True);

            // Episode A owns a real signal cooldown and first passes through the
            // pending queue before exhausting into a terminal dispatch record.
            mobState.ChangeMobState(patient, MobState.Critical);
            var episodeAPending = shuttle.GetPendingAutomaticDispatches()
                .Single(entry => entry.Target == patient);
            AdvanceSimulationClock(
                timing,
                episodeAPending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
            for (var attempt = 0;
                 attempt < 8 &&
                 !shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient);
                 attempt++)
            {
                shuttle.ProcessPendingAutomaticDispatchesNow();
            }
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.True);
            });

            var agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True);
            Assert.That(coordinator.RecordAttempt(agent, out _), Is.True);
            Assert.That(
                coordinator.Fail(
                    agent,
                    LuaMRescueFailureReason.DeadlineExceeded,
                    out var episodeAFailure),
                Is.True);
            Assert.That(episodeAFailure.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));

            // Model every stale gate that could otherwise make episode B wait
            // locally or inherit an exhausted treatment/route budget.
            rescue.SkippedTargets[patient] = TimeSpan.MaxValue;
            rescue.RouteFailureAttempts[patient] = rescue.ActivityRoleProfile.MaxAttempts;
            rescue.AnalysisAttempts[patient] = 3;
            rescue.TerminalAnalysisFailures[patient] = "episode A analyzer budget exhausted";
            rescue.TreatmentAttempts[patient] = 3;
            rescue.TerminalTreatmentFailures[patient] = "episode A treatment budget exhausted";
            rescue.TerminalTreatmentFailureDamage[patient] = 200f;
            rescue.PullAttempts[patient] = 3;
            rescue.NextPullAttemptAt[patient] = TimeSpan.MaxValue;
            rescue.TerminalPullFailures[patient] = "episode A pull budget exhausted";
            // Retain the legacy mirrors on purpose: the fresh episode must not
            // be mistaken for already-active ownership by those mirrors alone.
            rescue.AssignedTarget = patient;
            rescue.DeathSignalTarget = null;
            rescue.EvacuatingTarget = null;
            rescue.OnboardCareTarget = null;
            rescue.TaskPatientTarget = patient;
            rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
            rescue.ManualOverrideTarget = null;

            mobState.ChangeMobState(patient, MobState.Alive);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False,
                    "Recovery must close episode A's explicit terminal gate.");
            });

            // This deterioration occurs well inside episode A's cooldown window.
            // It must nevertheless create a generation with a clean budget.
            mobState.ChangeMobState(patient, MobState.Critical);
            Assert.That(coordinator.GetSnapshot(agent, out var episodeB), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(episodeB.Generation, Is.GreaterThan(episodeAFailure.Generation));
                Assert.That(episodeB.Target, Is.EqualTo(patient));
                Assert.That(episodeB.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(episodeB.Attempts, Is.Zero);
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.SkippedTargets.ContainsKey(patient), Is.False);
                Assert.That(rescue.RouteFailureAttempts.ContainsKey(patient), Is.False);
                Assert.That(rescue.AnalysisAttempts.ContainsKey(patient), Is.False);
                Assert.That(rescue.TerminalAnalysisFailures.ContainsKey(patient), Is.False);
                Assert.That(rescue.TreatmentAttempts.ContainsKey(patient), Is.False);
                Assert.That(rescue.TerminalTreatmentFailures.ContainsKey(patient), Is.False);
                Assert.That(rescue.TerminalTreatmentFailureDamage.ContainsKey(patient), Is.False);
                Assert.That(rescue.PullAttempts.ContainsKey(patient), Is.False);
                Assert.That(rescue.NextPullAttemptAt.ContainsKey(patient), Is.False);
                Assert.That(rescue.TerminalPullFailures.ContainsKey(patient), Is.False);
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False,
                    "Episode B must not wait behind episode A's durable local-recovery state.");
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False);
            });

            server.PlayerMan.SetAttachedEntity(session, null, true);
            entities.DeleteEntity(patient);
            entities.DeleteEntity(agent);
            shuttle.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExplicitOrderBeforeRecoveryReconciliationWinsButInheritsInfrastructure()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttleSystem = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var retiredAgent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var patient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                Vector2.Zero,
                attachActor: false);
            var assignedShuttle = entities.SpawnEntity(null, map.MapCoords);
            var assignedAnchor = entities.SpawnEntity(null, map.MapCoords);
            var assignedConsole = entities.SpawnEntity(null, map.MapCoords);
            var assignedReturnTarget = entities.SpawnEntity(null, map.MapCoords);
            var retired = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retired.AutoAcquireTargets = false;
            retired.AssignedShuttle = assignedShuttle;
            retired.AssignedShuttleAnchor = assignedAnchor;
            retired.AssignedShuttleConsole = assignedConsole;
            retired.AssignedReturnTarget = assignedReturnTarget;
            retired.AssignedTarget = patient;
            retired.TaskPatientTarget = patient;
            retired.TaskStage = LuaMRescueTaskStage.FollowingPatient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True);

            retired.SkippedTargets[patient] = TimeSpan.MaxValue;
            retired.RouteFailureAttempts[patient] = 4;
            retired.AnalysisAttempts[patient] = 3;
            retired.TerminalAnalysisFailures[patient] = "retired owner exhausted analyzer";
            retired.TreatmentAttempts[patient] = 3;
            retired.TerminalTreatmentFailures[patient] = "retired owner exhausted treatment";
            retired.TerminalTreatmentFailureDamage[patient] = 150f;

            entities.DeleteEntity(retiredAgent);
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
            Assert.That(
                agentSystem.TryOrderAgent(replacement, patient, out var orderStatus),
                Is.True,
                orderStatus);

            var replacementRescue = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            var manualGeneration = replacementRescue.ActivityContext.Generation;
            var manualLineage = replacementRescue.ManualOverrideGeneration;
            Assert.Multiple(() =>
            {
                Assert.That(replacementRescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(replacementRescue.SkippedTargets.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.RouteFailureAttempts.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.AnalysisAttempts.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TerminalAnalysisFailures.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TreatmentAttempts.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TerminalTreatmentFailures.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TerminalTreatmentFailureDamage.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.AssignedShuttle, Is.EqualTo(assignedShuttle));
                Assert.That(replacementRescue.AssignedShuttleAnchor, Is.EqualTo(assignedAnchor));
                Assert.That(replacementRescue.AssignedShuttleConsole, Is.EqualTo(assignedConsole));
                Assert.That(replacementRescue.AssignedReturnTarget, Is.EqualTo(assignedReturnTarget));
            });

            shuttleSystem.ProcessPendingAutomaticDispatchesNow();
            Assert.Multiple(() =>
            {
                Assert.That(replacementRescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(replacementRescue.ManualOverrideGeneration, Is.EqualTo(manualLineage));
                Assert.That(replacementRescue.ActivityContext.Generation, Is.EqualTo(manualGeneration));
                Assert.That(replacementRescue.SkippedTargets.ContainsKey(patient), Is.False,
                    "The retired owner's durable skip must not overwrite a newer explicit order.");
                Assert.That(replacementRescue.RouteFailureAttempts.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.AnalysisAttempts.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TerminalAnalysisFailures.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TreatmentAttempts.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TerminalTreatmentFailures.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.TerminalTreatmentFailureDamage.ContainsKey(patient), Is.False);
                Assert.That(replacementRescue.AssignedShuttle, Is.EqualTo(assignedShuttle));
                Assert.That(replacementRescue.AssignedShuttleAnchor, Is.EqualTo(assignedAnchor));
                Assert.That(replacementRescue.AssignedShuttleConsole, Is.EqualTo(assignedConsole));
                Assert.That(replacementRescue.AssignedReturnTarget, Is.EqualTo(assignedReturnTarget));
            });

            entities.DeleteEntity(patient);
            entities.DeleteEntity(replacement);
            entities.DeleteEntity(assignedShuttle);
            entities.DeleteEntity(assignedAnchor);
            entities.DeleteEntity(assignedConsole);
            entities.DeleteEntity(assignedReturnTarget);
            shuttleSystem.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HistoryOnlyRecoveryStateDoesNotSpawnReplacementWithoutAcceptedMission()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var baselineAgents = entities.EntityQuery<LuaMRescueAgentComponent>().Count();
            Assert.That(baselineAgents, Is.Zero);
            var retiredAgent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var historicalPatient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                Vector2.Zero,
                attachActor: false);
            var retired = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retired.AutoAcquireTargets = false;

            // These maps are retry/diagnostic history only: there is no active,
            // deferred, dormant, onboard or manually accepted mission.
            retired.TreatmentAttempts[historicalPatient] = 3;
            retired.TerminalTreatmentFailures[historicalPatient] = "historical treatment terminal";
            retired.PullAttempts[historicalPatient] = 2;
            retired.NextPullAttemptAt[historicalPatient] = timing.CurTime + TimeSpan.FromMinutes(5);
            retired.TerminalPullFailures[historicalPatient] = "historical pull terminal";
            Assert.Multiple(() =>
            {
                Assert.That(retired.AssignedTarget, Is.Null);
                Assert.That(retired.DeathSignalTarget, Is.Null);
                Assert.That(retired.EvacuatingTarget, Is.Null);
                Assert.That(retired.OnboardCareTarget, Is.Null);
                Assert.That(retired.TaskPatientTarget, Is.Null);
                Assert.That(retired.ManualOverrideTarget, Is.Null);
                Assert.That(retired.DormantRouteTarget, Is.Null);
                Assert.That(retired.DeferredPatientTargets.ContainsKey(historicalPatient), Is.False);
                Assert.That(retired.ActivityContext.TerminalStatus, Is.Not.EqualTo(LuaMRescueTerminalStatus.Active));
            });

            entities.DeleteEntity(retiredAgent);
            Assert.That(entities.EntityQuery<LuaMRescueAgentComponent>().Count(), Is.EqualTo(baselineAgents));
            shuttle.ProcessPendingAutomaticDispatchesNow();
            Assert.Multiple(() =>
            {
                Assert.That(
                    entities.EntityQuery<LuaMRescueAgentComponent>().Count(),
                    Is.EqualTo(baselineAgents),
                    "Retry/terminal history alone must not create a replacement rescue agent.");
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == historicalPatient),
                    Is.False);
                Assert.That(
                    shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == historicalPatient),
                    Is.False);
            });

            entities.DeleteEntity(historicalPatient);
            shuttle.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TerminalAnalyzerBudgetTransfersWithAcceptedAutomaticMission()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var retired = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var patient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                Vector2.Zero,
                attachActor: false);
            var old = entities.GetComponent<LuaMRescueAgentComponent>(retired);
            old.AutoAcquireTargets = false;
            old.AssignedTarget = patient;
            old.TaskPatientTarget = patient;
            old.TaskStage = LuaMRescueTaskStage.FollowingPatient;
            var expectedAttempts = old.ActivityRoleProfile.MaxAttempts;
            old.AnalysisAttempts[patient] = expectedAttempts;
            old.TerminalAnalysisFailures[patient] = "terminal analyzer timeout on retired owner";
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retired,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.Triage,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True);

            entities.DeleteEntity(retired);
            Assert.That(agents.TryFindActiveAgent(out _, out _), Is.False);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(agents.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(replacement, Is.Not.EqualTo(retired));
                Assert.That(
                    replacementRescue.AnalysisAttempts.GetValueOrDefault(patient),
                    Is.EqualTo(expectedAttempts));
                Assert.That(
                    replacementRescue.TerminalAnalysisFailures.GetValueOrDefault(patient),
                    Is.EqualTo("terminal analyzer timeout on retired owner"));
                Assert.That(replacementRescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(replacementRescue.TaskPatientTarget, Is.EqualTo(patient));
            });

            entities.DeleteEntity(patient);
            entities.DeleteEntity(replacement);
            shuttle.ProcessPendingAutomaticDispatchesNow();
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BusySingletonWaitDoesNotConsumeInfrastructureAttemptBudget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();
        EntityUid queuedPatient = default;

        await server.WaitAssertion(() =>
        {
            var activeAgent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var current = entities.SpawnEntity(
                "MobHuman",
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));
            mobState.ChangeMobState(current, MobState.Critical);
            var activeRescue = entities.GetComponent<LuaMRescueAgentComponent>(activeAgent);
            activeRescue.AutoAcquireTargets = false;
            activeRescue.AssignedTarget = current;

            queuedPatient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                new Vector2(2f, 0f));
            Assert.That(
                rescue.TryQueueAutomaticMedicalSignal(
                    queuedPatient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var status),
                Is.True,
                status);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                AdvanceSimulationClock(timing, TimeSpan.FromSeconds(11));
                Assert.That(rescue.ProcessPendingAutomaticDispatchesNow(), Is.True);
                var pending = rescue.GetPendingAutomaticDispatches()
                    .Single(entry => entry.Target == queuedPatient);
                Assert.Multiple(() =>
                {
                    Assert.That(pending.Attempts, Is.Zero);
                    Assert.That(pending.Terminal, Is.False);
                    Assert.That(pending.LastStatus, Does.Contain("busy"));
                });
            }

            Assert.That(
                rescue.GetTerminalAutomaticDispatches().Any(entry => entry.Target == queuedPatient),
                Is.False);

            DiscardPendingTarget(entities, timing, rescue, queuedPatient);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExhaustedDeadlineIsDurableUntilExplicitRedispatch()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            patient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                Vector2.Zero,
                attachActor: false);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureBoundedRoutePolicy(rescue);

            Assert.That(
                navigation.ProbeRoute(agent, patient, rescue.EvacuationStartRange).State,
                Is.EqualTo(LuaMRescuePathProbeState.Reachable));
            Assert.That(agentSystem.TryOrderAgent(agent, patient, out var orderStatus), Is.True, orderStatus);
            Assert.That(coordinator.GetSnapshot(agent, out var active), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(active.Activity, Is.EqualTo(LuaMRescueActivity.ApproachPatient));
                Assert.That(active.Target, Is.EqualTo(patient));
                Assert.That(active.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });

            Assert.That(
                coordinator.Fail(agent, LuaMRescueFailureReason.DeadlineExceeded, out var failed),
                Is.True);
            Assert.That(failed.FailureReason, Is.EqualTo(LuaMRescueFailureReason.DeadlineExceeded));
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.SkippedTargets.GetValueOrDefault(patient), Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(rescue.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(1));
                Assert.That(rescue.DormantRouteTarget, Is.Null);
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.ManualOverrideTarget, Is.Null);
                Assert.That(rescue.LastDormantRouteStatus, Does.Contain("durable").IgnoreCase);
                Assert.That(rescue.LastDormantRouteStatus, Does.Contain(nameof(LuaMRescueFailureReason.DeadlineExceeded)));
                Assert.That(rescue.LastDormantRouteStatus, Does.Contain("explicit redispatch required").IgnoreCase);
            });
        });

        // Normal acquisition keeps running, but a durable deadline failure must
        // not silently become a fresh intent just because the route is reachable.
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.SkippedTargets.GetValueOrDefault(patient), Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(rescue.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(1));
                Assert.That(rescue.AssignedTarget, Is.Not.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.Not.EqualTo(patient));
                Assert.That(rescue.ManualOverrideTarget, Is.Not.EqualTo(patient));
            });

            Assert.That(
                agentSystem.TryOrderAgent(agent, patient, out var redispatchStatus),
                Is.True,
                redispatchStatus);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.SkippedTargets.ContainsKey(patient), Is.False);
                Assert.That(rescue.RouteFailureAttempts.ContainsKey(patient), Is.False);
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task DormantRouteLineageTransfersToReplacementWithoutBudgetReset(
        bool deleteOwner,
        bool factionlessPatient)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid replacement = default;
        var inheritedProbeCount = 0;
        var inheritedResumeCount = 0;
        await server.WaitAssertion(() =>
        {
            retiredAgent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            patient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                Vector2.Zero,
                attachActor: false);
            // Route-lineage transfer is independent of environmental damage.
            // Isolate the fixture from the pooled Barotrauma timer so deleting
            // the old body cannot race a pressure-damage/body-part update.
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            if (factionlessPatient)
                entities.RemoveComponent<NpcFactionMemberComponent>(patient);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            ConfigureBoundedRoutePolicy(rescue);

            if (factionlessPatient)
            {
                Assert.That(
                    coordinator.IsEligibleRescuePatient(
                        retiredAgent,
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
            }

            Assert.That(
                navigation.ProbeRoute(retiredAgent, patient, rescue.EvacuationStartRange).State,
                Is.EqualTo(LuaMRescuePathProbeState.Reachable));
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.ApproachPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out var automaticIntent),
                Is.True,
                automaticIntent.FailureReason.ToString());
            Assert.That(rescue.ManualOverrideTarget, Is.Null);
            Assert.That(
                coordinator.Block(
                    retiredAgent,
                    LuaMRescueFailureReason.ShuttleUnavailable,
                    LuaMRescueActivity.Standby,
                    out _),
                Is.True);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DormantRouteTarget, Is.EqualTo(patient));
                Assert.That(rescue.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(1));
                Assert.That(rescue.SkippedTargets.GetValueOrDefault(patient), Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
            });
            inheritedProbeCount = rescue.DormantRouteProbeCount;
            inheritedResumeCount = rescue.DormantRouteResumeCount;

            if (deleteOwner)
                entities.DeleteEntity(retiredAgent);
            else
                mobState.ChangeMobState(retiredAgent, MobState.Dead);
            Assert.That(
                agentSystem.TrySpawnAgent(
                    map.Grid,
                    followTarget: null,
                    controller: null,
                    control: false,
                    out replacement,
                    out var spawnStatus),
                Is.True,
                spawnStatus);
            entities.RemoveComponent<BarotraumaComponent>(replacement);

            // A signal can arrive in the sub-tick window after the replacement
            // spawns but before the shuttle queue adopts the dormant lineage. It
            // must wait behind the transfer instead of creating generation zero.
            if (!factionlessPatient)
            {
                entities.EnsureComponent<ActorComponent>(patient);
                Assert.That(
                    shuttle.TryQueueAutomaticMedicalSignal(
                        patient,
                        LuaMRescueMedicalSignalKind.Critical,
                        out var preAdoptionStatus),
                    Is.True,
                    preAdoptionStatus);
                Assert.That(preAdoptionStatus, Does.Contain("awaiting dormant route lineage").IgnoreCase);
            }

            var beforeAdoption = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            Assert.Multiple(() =>
            {
                Assert.That(beforeAdoption.AssignedTarget, Is.Null);
                Assert.That(beforeAdoption.TaskPatientTarget, Is.Null);
                Assert.That(beforeAdoption.ActivityContext.Target, Is.Not.EqualTo(patient));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.EqualTo(!factionlessPatient));
            });
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);

            var adopted = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            Assert.Multiple(() =>
            {
                Assert.That(adopted.DormantRouteTarget, Is.EqualTo(patient));
                Assert.That(adopted.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(1),
                    "Replacement must inherit the exhausted route budget, not receive a fresh attempt.");
                Assert.That(adopted.SkippedTargets.GetValueOrDefault(patient), Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(adopted.DormantRouteProbeCount, Is.EqualTo(inheritedProbeCount));
                Assert.That(adopted.DormantRouteResumeCount, Is.EqualTo(inheritedResumeCount));
                Assert.That(adopted.AssignedTarget, Is.Null);
                Assert.That(adopted.TaskPatientTarget, Is.Null);
                Assert.That(adopted.ActivityContext.Target, Is.Not.EqualTo(patient),
                    "Dormant handoff must not bypass the low-frequency probe with a fresh dispatch intent.");
            });

            adopted.AutoAcquireTargets = true;
            adopted.TargetRefreshInterval = 0.001f;
            adopted.TargetRefreshAccumulator = adopted.TargetRefreshInterval;
            adopted.NextDormantRouteProbeAt = timing.CurTime;
        });

        await pair.RunTicksSync(1);
        uint resumedGeneration = 0;
        await server.WaitAssertion(() =>
        {
            var resumedRescue = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            Assert.That(coordinator.GetSnapshot(replacement, out var resumed), Is.True);
            resumedGeneration = resumed.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(resumedRescue.DormantRouteTarget, Is.Null);
                Assert.That(resumedRescue.RouteFailureAttempts.ContainsKey(patient), Is.False);
                Assert.That(resumedRescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(resumedRescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(resumedRescue.DormantRouteResumeCount, Is.EqualTo(inheritedResumeCount + 1));
                Assert.That(resumed.Target, Is.EqualTo(patient));
                Assert.That(resumed.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
            resumedRescue.AutoAcquireTargets = false;
            if (!factionlessPatient)
            {
                Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                    Is.False,
                    "The pre-adoption signal must reconcile against the resumed authoritative owner.");
            }
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var stable = entities.GetComponent<LuaMRescueAgentComponent>(replacement);
            Assert.That(coordinator.GetSnapshot(replacement, out var snapshot), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(stable.DormantRouteResumeCount, Is.EqualTo(inheritedResumeCount + 1));
                Assert.That(snapshot.Generation, Is.EqualTo(resumedGeneration));
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(LuaMRescueFailureReason.ShuttleRouteFailed)]
    [TestCase(LuaMRescueFailureReason.ShuttleUnavailable)]
    public async Task ExhaustedShuttleRouteFailureEntersDormantRecovery(LuaMRescueFailureReason reason)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            patient = SpawnCriticalPatient(
                entities,
                mobState,
                map.MapId,
                Vector2.Zero,
                attachActor: false);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureBoundedRoutePolicy(rescue);

            Assert.That(
                navigation.ProbeRoute(agent, patient, rescue.EvacuationStartRange).State,
                Is.EqualTo(LuaMRescuePathProbeState.Reachable));
            Assert.That(agentSystem.TryOrderAgent(agent, patient, out var orderStatus), Is.True, orderStatus);
            Assert.That(
                coordinator.Block(agent, reason, LuaMRescueActivity.Standby, out var blocked),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(blocked.Target, Is.EqualTo(patient));
                Assert.That(blocked.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(blocked.FailureReason, Is.EqualTo(reason));
            });

            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DormantRouteTarget, Is.EqualTo(patient));
                Assert.That(rescue.SkippedTargets.GetValueOrDefault(patient), Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(rescue.RouteFailureAttempts.GetValueOrDefault(patient), Is.EqualTo(1));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.LastDormantRouteStatus, Does.Contain("armed").IgnoreCase);
                Assert.That(rescue.LastDormantRouteStatus, Does.Contain(reason.ToString()));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void ConfigureBoundedRoutePolicy(LuaMRescueAgentComponent rescue)
    {
        rescue.AutoAcquireTargets = false;
        rescue.TargetRefreshInterval = 0.001f;
        rescue.TargetRefreshAccumulator = 0f;
        rescue.EvacuateTargetsToShuttle = false;
        rescue.AutoAnalyzeBeforeTreatment = false;
        rescue.AutoTreatWithCarriedItems = false;
        rescue.AutoDefibDeadPatients = false;
        rescue.TargetSkipSeconds = 0.01f;
        rescue.DormantRouteObservationSeconds = 1_000f;
        rescue.ActivityRoleProfile.MaxAttempts = 1;
    }

    private static EntityUid SpawnCriticalPatient(
        IEntityManager entities,
        MobStateSystem mobState,
        MapId mapId,
        Vector2 position,
        bool attachActor = true)
    {
        var patient = entities.SpawnEntity("MobHuman", new MapCoordinates(position, mapId));
        // Keep the forced state consistent with MobThresholds. A zero-damage
        // Critical can be reconciled back to Alive by any later damage event,
        // which would legitimately close the medical episode and erase the
        // durable route history this fixture is exercising.
        entities.RemoveComponent<BarotraumaComponent>(patient);
        var injury = new DamageSpecifier();
        injury.DamageDict.Add("Blunt", 100);
        Assert.That(
            entities.System<DamageableSystem>().TryChangeDamage(patient, injury, ignoreResistances: true),
            Is.Not.Null);
        // Add the actor only after the state change so this test controls the sole signal producer.
        mobState.ChangeMobState(patient, MobState.Critical);
        Assert.That(
            entities.GetComponent<MobStateComponent>(patient).CurrentState,
            Is.EqualTo(MobState.Critical));
        if (attachActor)
            entities.EnsureComponent<ActorComponent>(patient);
        return patient;
    }

    private static RequiredFollowUpTerminalSetup CreateRequiredFollowUpTerminal(
        IEntityManager entities,
        IGameTiming timing,
        LuaMRescueShuttleSystem shuttle,
        DamageableSystem damageable,
        MapId mapId,
        Vector2 position)
    {
        var patient = entities.SpawnEntity("MobHuman", new MapCoordinates(position, mapId));
        var injury = new DamageSpecifier();
        injury.DamageDict.Add("Blunt", 5);
        Assert.That(
            damageable.TryChangeDamage(patient, injury, ignoreResistances: true),
            Is.Not.Null);

        var owner = entities.SpawnEntity(
            "LuaMRescueAgent",
            new MapCoordinates(position, mapId));
        var assignedShuttle = entities.SpawnEntity(null, new MapCoordinates(position, mapId));
        var assignedAnchor = entities.SpawnEntity(null, new MapCoordinates(position, mapId));
        var assignedConsole = entities.SpawnEntity(null, new MapCoordinates(position, mapId));
        var returnTarget = entities.SpawnEntity(null, new MapCoordinates(position, mapId));
        var rescue = entities.GetComponent<LuaMRescueAgentComponent>(owner);
        rescue.AutoAcquireTargets = false;
        rescue.AssignedTarget = patient;
        rescue.TaskPatientTarget = patient;
        rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
        rescue.AssignedShuttle = assignedShuttle;
        rescue.AssignedShuttleAnchor = assignedAnchor;
        rescue.AssignedShuttleConsole = assignedConsole;
        rescue.AssignedReturnTarget = returnTarget;
        rescue.RequiredOnboardHandoffPatients.Add(patient);

        entities.DeleteEntity(owner);
        var pending = shuttle.GetPendingAutomaticDispatches()
            .Single(entry => entry.Target == patient);
        Assert.Multiple(() =>
        {
            Assert.That(pending.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.FollowUp));
            Assert.That(pending.Attempts, Is.Zero);
            Assert.That(pending.Terminal, Is.False);
        });

        AdvanceSimulationClock(
            timing,
            pending.Deadline - timing.CurTime + TimeSpan.FromSeconds(1));
        ProcessPendingUntilTerminal(shuttle, patient);
        var terminal = shuttle.GetTerminalAutomaticDispatches()
            .Single(entry => entry.Target == patient);
        Assert.Multiple(() =>
        {
            Assert.That(terminal.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.FollowUp));
            Assert.That(terminal.Terminal, Is.True);
            Assert.That(
                shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                Is.False);
        });

        return new RequiredFollowUpTerminalSetup(
            patient,
            assignedShuttle,
            assignedAnchor,
            assignedConsole,
            returnTarget,
            terminal);
    }

    private readonly record struct RequiredFollowUpTerminalSetup(
        EntityUid Patient,
        EntityUid AssignedShuttle,
        EntityUid AssignedAnchor,
        EntityUid AssignedConsole,
        EntityUid ReturnTarget,
        LuaMRescuePendingDispatchSnapshot Terminal);

    private static void AdvanceSimulationClock(IGameTiming timing, TimeSpan delta)
    {
        Assert.That(delta, Is.GreaterThan(TimeSpan.Zero));
        var (time, tick) = timing.TimeBase;
        timing.TimeBase = (time + delta, tick);
    }

    private static void ProcessPendingUntilTerminal(
        LuaMRescueShuttleSystem shuttle,
        EntityUid target)
    {
        for (var processingAttempt = 0; processingAttempt < 8; processingAttempt++)
        {
            if (shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target))
                return;
            if (!shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == target))
                break;

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
        }

        Assert.That(
            shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == target),
            Is.True,
            "An overdue pending diagnostic must be processed through any stale queue-order entries.");
    }

    private static void DiscardPendingTarget(
        IEntityManager entities,
        IGameTiming timing,
        LuaMRescueShuttleSystem rescue,
        EntityUid target)
    {
        entities.DeleteEntity(target);
        AdvanceSimulationClock(timing, TimeSpan.FromSeconds(11));

        var processingBudget = rescue.PendingAutomaticDispatchCount + 1;
        for (var i = 0; i < processingBudget &&
                        rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target); i++)
        {
            rescue.ProcessPendingAutomaticDispatchesNow();
        }

        Assert.That(
            rescue.GetPendingAutomaticDispatches().Any(entry => entry.Target == target),
            Is.False,
            "A test-owned pending dispatch must not leak into the pooled integration server.");
    }
}
