using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.Mind;
using Content.Server._LuaM.Rescue;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Traits.Assorted;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueAgentSystem))]
public sealed class LuaMRescueOnboardRuntimeTest
{
    [Test]
    public async Task StableOnboardPatientIsReleasedAndBedBecomesAvailable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            // The minimal test grid has no atmosphere. Vacuum exposure is not
            // part of this contract and can otherwise turn the stable patient
            // into an onboard-treatment candidate before the buckle delay ends.
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(agent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            entities.EnsureComponent<NpcFactionMemberComponent>(patient);
            factions.RemoveFaction(patient, "NanoTrasen");
            factions.AddFaction(patient, "Syndicate");
            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    agent,
                    patient,
                    LuaMRescuePatientRequestKind.AutomaticTreatment,
                    manualOverride: false,
                    out var automaticFailure),
                Is.False);
            Assert.That(automaticFailure, Is.EqualTo(LuaMRescueFailureReason.ThreatTooHigh));
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, agent, bed, buckleComp: buckle), Is.True);
            Assert.That(buckle.BuckledTo, Is.EqualTo(bed));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.ManualOverrideTarget = patient;
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;
        });

        await pair.RunTicksSync(3);
        uint onboardGeneration = 0;
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            onboardGeneration = rescue.ActivityContext.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.EqualTo(bed));
                Assert.That(
                    entities.GetComponent<DamageableComponent>(patient).TotalDamage.Float(),
                    Is.LessThanOrEqualTo(rescue.AutoReleaseMaxDamage),
                    "The fixture patient must remain stable while the buckle safety delay is tested.");
                Assert.That(rescue.OnboardHandoffAttempts.GetValueOrDefault(patient), Is.Zero,
                    "The normal buckle safety delay must not consume the handoff retry budget.");
                Assert.That(rescue.IgnoredOnboardPatients, Does.Not.Contain(patient));
                Assert.That(rescue.AssignedTarget, Is.Null,
                    "A physically boarded assignment must leave follow ownership immediately.");
                Assert.That(rescue.OnboardCareTarget, Is.EqualTo(patient),
                    "A physically boarded assignment must normalize into onboard care.");
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.TerminalOnboardCareFailures, Does.Not.ContainKey(patient),
                    "Manual patient provenance must survive the onboard eligibility scan.");
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Handoff));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
            });
        });

        await server.WaitAssertion(() =>
        {
            var htn = entities.GetComponent<HTNComponent>(agent);
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(bed, Vector2.Zero));
        });

        for (var tick = 0; tick < 3; tick++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                Assert.Multiple(() =>
                {
                    Assert.That(rescue.AssignedTarget, Is.Null,
                        "Onboard reconciliation must not recreate follow ownership.");
                    Assert.That(rescue.OnboardCareTarget, Is.EqualTo(patient));
                    Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                    Assert.That(rescue.TerminalOnboardCareFailures, Does.Not.ContainKey(patient));
                    Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Handoff));
                    Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
                    Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(onboardGeneration),
                        "Waiting for the buckle deadline must not churn the onboard generation.");
                });

                var htn = entities.GetComponent<HTNComponent>(agent);
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var onboardFollow,
                        entities),
                    Is.True,
                    "Onboard reconciliation must preserve executor movement to the patient strap.");
                Assert.That(onboardFollow.EntityId, Is.EqualTo(bed));
            });
        }

        await RunPastBuckleDelay(pair, unbuckleAt);

        // The agent system can update immediately before the buckle deadline on
        // the boundary tick. Give the next refresh a small bounded window while
        // keeping a persistent lifecycle/release regression observable below.
        var physicallyReleased = false;
        for (var tick = 0; tick < 10 && !physicallyReleased; tick++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                physicallyReleased = entities.GetComponent<BuckleComponent>(patient).BuckledTo == null;
            });
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            var strap = entities.GetComponent<StrapComponent>(bed);
            Assert.Multiple(() =>
            {
                Assert.That(buckle.BuckledTo, Is.Null);
                Assert.That(strap.BuckledEntities, Does.Not.Contain(patient));
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.ManualOverrideTarget, Is.Null,
                    "Manual provenance must be released with the physical patient handoff.");
                Assert.That(rescue.AssignedPatientStrap, Is.Null);
            });

            var nextPatient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var nextBuckle = entities.GetComponent<BuckleComponent>(nextPatient);
            Assert.That(buckleSystem.TryBuckle(nextPatient, agent, bed, buckleComp: nextBuckle), Is.True,
                "A physically released bed must accept the next patient.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UntreatableOnboardPatientReachesBoundedSpecialistHandoff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoReleaseMaxDamage = 0f;
            rescue.AutoTreatMinDamage = 1f;
            rescue.AutoTreatCooldown = 0.01f;
            rescue.ActivityRoleProfile.MaxAttempts = 2;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Radiation", 20);
            Assert.That(damageable.TryChangeDamage(patient, damage, ignoreResistances: true), Is.Not.Null);

            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, agent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;
        });

        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.EqualTo(bed));
                Assert.That(rescue.OnboardHandoffAttempts.GetValueOrDefault(patient), Is.Zero,
                    "Terminal-care handoff must wait for the buckle delay without recording failures.");
                Assert.That(rescue.IgnoredOnboardPatients, Does.Not.Contain(patient));
            });
        });

        await RunPastBuckleDelay(pair, unbuckleAt);

        // A carried medical item can have a multi-second DoAfter. Let the real
        // action complete and exhaust the configured bounded attempt budget
        // instead of assuming that twenty simulation ticks are sufficient.
        var physicallyReleased = false;
        for (var tick = 0; tick < 600 && !physicallyReleased; tick++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                physicallyReleased = entities.GetComponent<BuckleComponent>(patient).BuckledTo == null;
            });
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var strap = entities.GetComponent<StrapComponent>(bed);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.Null,
                    "An untreatable patient must leave the active bed loop through bounded handoff.");
                Assert.That(strap.BuckledEntities, Does.Not.Contain(patient));
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.LastOnboardCareStatus, Does.Contain("released").IgnoreCase);
                Assert.That(rescue.IgnoredOnboardPatients, Does.Not.Contain(patient),
                    "A successful physical handoff must not use the logical-only ignored fallback.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExternalUnbucklePrunesOnboardOwnershipAndAllowsNextDispatch()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var shuttleSystem = entities.System<LuaMRescueShuttleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid stalePatient = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoReleaseStabilizedPatients = false;
            stalePatient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(stalePatient);
            Assert.That(buckleSystem.TryBuckle(stalePatient, agent, bed, buckleComp: buckle), Is.True);
            rescue.OnboardCareTarget = stalePatient;
            rescue.TaskPatientTarget = stalePatient;
            rescue.AssignedPatientStrap = bed;
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;
        });

        await RunPastBuckleDelay(pair, unbuckleAt);
        await server.WaitAssertion(() =>
        {
            var buckle = entities.GetComponent<BuckleComponent>(stalePatient);
            Assert.That(buckleSystem.TryUnbuckle(stalePatient, agent, buckle, popup: false), Is.True);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.AssignedPatientStrap, Is.Null);
                Assert.That(rescue.LastOnboardCareStatus, Does.Contain("TargetLost"));
            });

            var nextPatient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map, new Vector2(1f, 0f)));
            mobState.ChangeMobState(nextPatient, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(nextPatient);
            Assert.That(
                shuttleSystem.TryQueueAutomaticMedicalSignal(
                    nextPatient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var status),
                Is.True,
                status);
            Assert.That(rescue.AssignedTarget, Is.EqualTo(nextPatient));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StillBuckledUnrevivablePatientUsesHandoffAndFreesBed()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoDefibDeadPatients = true;
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, agent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            rescue.OnboardCareTarget = patient;
            rescue.TaskPatientTarget = patient;
            rescue.AssignedPatientStrap = bed;
            entities.EnsureComponent<UnrevivableComponent>(patient);
            mobState.ChangeMobState(patient, MobState.Dead);
        });

        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.EqualTo(bed));
                Assert.That(rescue.OnboardHandoffAttempts.GetValueOrDefault(patient), Is.Zero);
                Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.Zero,
                    "An explicitly unrevivable patient must bypass defibrillation retries.");
                Assert.That(rescue.IgnoredOnboardPatients, Does.Not.Contain(patient));
            });
        });

        await RunPastBuckleDelay(pair, unbuckleAt);
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            var strap = entities.GetComponent<StrapComponent>(bed);
            Assert.Multiple(() =>
            {
                Assert.That(buckle.BuckledTo, Is.Null,
                    "An ineligible body must be unbuckled by the bounded handoff policy.");
                Assert.That(strap.BuckledEntities, Does.Not.Contain(patient));
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.AssignedPatientStrap, Is.Null);
                Assert.That(rescue.LastOnboardCareStatus, Does.Contain("released").IgnoreCase);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CriticalSignalQueuesUntilCurrentOnboardPatientIsSafelyHandedOff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoReleaseMaxDamage = 0f;
            var onboard = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(onboard);
            Assert.That(buckleSystem.TryBuckle(onboard, agent, bed, buckleComp: buckle), Is.True);
            var injury = new DamageSpecifier();
            injury.DamageDict.Add("Blunt", 10);
            Assert.That(damageable.TryChangeDamage(onboard, injury, ignoreResistances: true), Is.Not.Null);

            rescue.OnboardCareTarget = onboard;
            rescue.TaskPatientTarget = onboard;
            rescue.AssignedPatientStrap = bed;

            var critical = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(1f, 0f)));
            mobState.ChangeMobState(critical, MobState.Critical);
            entities.EnsureComponent<ActorComponent>(critical);

            Assert.That(
                shuttle.TryQueueAutomaticMedicalSignal(
                    critical,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var status),
                Is.True,
                status);

            Assert.Multiple(() =>
            {
                Assert.That(rescue.OnboardCareTarget, Is.EqualTo(onboard));
                Assert.That(rescue.AssignedPatientStrap, Is.EqualTo(bed));
                Assert.That(buckle.BuckledTo, Is.EqualTo(bed));
                Assert.That(rescue.AssignedTarget, Is.Not.EqualTo(critical));
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == critical),
                    Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TerminalIgnoredOnboardCustodyCannotBePreemptedUntilPhysicalRelease()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid onboard = default;
        EntityUid critical = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoReleaseStabilizedPatients = false;
            rescue.ActivityRoleProfile.MaxAttempts = 1;

            onboard = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            critical = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(1f, 0f)));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(onboard);
            entities.RemoveComponent<BarotraumaComponent>(critical);
            entities.RemoveComponent<RespiratorComponent>(agent);
            entities.RemoveComponent<RespiratorComponent>(onboard);
            entities.RemoveComponent<RespiratorComponent>(critical);

            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(onboard);
            Assert.That(buckleSystem.TryBuckle(onboard, agent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            rescue.OnboardCareTarget = onboard;
            rescue.AssignedPatientStrap = bed;
            rescue.TaskStage = LuaMRescueTaskStage.DeliveringPatient;
            rescue.TaskPatientTarget = onboard;
            rescue.TaskSupplyTarget = bed;
            rescue.OnboardHandoffAttempts[onboard] = rescue.ActivityRoleProfile.MaxAttempts;
            rescue.TerminalOnboardCareFailures[onboard] =
                "BedUnavailable: terminal unbuckle budget exhausted";
            rescue.IgnoredOnboardPatients.Add(onboard);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    onboard,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out _),
                Is.True);
            Assert.That(
                coordinator.Block(
                    agent,
                    LuaMRescueFailureReason.BedUnavailable,
                    LuaMRescueActivity.Standby,
                    out _),
                Is.True);

            mobState.ChangeMobState(critical, MobState.Critical);
        });

        await RunPastBuckleDelay(pair, unbuckleAt);

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            for (var update = 0; update < 3; update++)
            {
                rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
                agentSystem.Update(rescue.TargetRefreshInterval);
            }

            var buckle = entities.GetComponent<BuckleComponent>(onboard);
            var strap = entities.GetComponent<StrapComponent>(bed);
            Assert.Multiple(() =>
            {
                Assert.That(buckle.BuckledTo, Is.EqualTo(bed));
                Assert.That(strap.BuckledEntities, Does.Contain(onboard));
                Assert.That(rescue.OnboardCareTarget, Is.EqualTo(onboard));
                Assert.That(rescue.AssignedPatientStrap, Is.EqualTo(bed));
                Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.DeliveringPatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(onboard));
                Assert.That(rescue.TaskSupplyTarget, Is.EqualTo(bed));
                Assert.That(rescue.AssignedTarget, Is.Not.EqualTo(critical));
                Assert.That(rescue.EvacuatingTarget, Is.Not.EqualTo(critical));
                Assert.That(rescue.TaskPatientTarget, Is.Not.EqualTo(critical));
                Assert.That(rescue.IgnoredOnboardPatients, Does.Contain(onboard));
                Assert.That(rescue.TerminalOnboardCareFailures, Does.ContainKey(onboard));
            });

            Assert.That(
                buckleSystem.TryUnbuckle(onboard, agent, buckle, popup: false),
                Is.True,
                "External/manual release must remain able to resolve terminal bed custody.");
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            agentSystem.Update(rescue.TargetRefreshInterval);
            var strap = entities.GetComponent<StrapComponent>(bed);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(onboard).BuckledTo, Is.Null);
                Assert.That(strap.BuckledEntities, Does.Not.Contain(onboard));
                Assert.That(rescue.OnboardCareTarget, Is.Not.EqualTo(onboard));
                Assert.That(rescue.AssignedPatientStrap, Is.Not.EqualTo(bed));
                Assert.That(rescue.TaskPatientTarget, Is.Not.EqualTo(onboard));
                Assert.That(rescue.IgnoredOnboardPatients, Does.Not.Contain(onboard));
                Assert.That(rescue.TerminalOnboardCareFailures, Does.Not.ContainKey(onboard));
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(true, false)]
    public async Task RetiredOnboardOwnerEitherSafelyReleasesOrTransfersExactOwnership(
        bool deleteOwner,
        bool releaseDuringRetirement)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            retiredAgent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            // Keep the patient aboard while the engine buckle delay elapses. The
            // requested policy is enabled immediately before retirement below,
            // so only the retirement handoff can perform the tested release.
            retiredRescue.AutoReleaseStabilizedPatients = false;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            if (releaseDuringRetirement)
            {
                var minorDamage = new DamageSpecifier();
                minorDamage.DamageDict.Add("Blunt", 2);
                Assert.That(
                    damageable.TryChangeDamage(patient, minorDamage, ignoreResistances: true),
                    Is.Not.Null);
            }
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(retiredAgent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, retiredAgent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            retiredRescue.AssignedTarget = patient;
            retiredRescue.TaskPatientTarget = patient;
            retiredRescue.OnboardCareTarget = patient;
            retiredRescue.AssignedPatientStrap = bed;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out var onboardIntent),
                Is.True);
            Assert.That(onboardIntent.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
        });

        await RunPastBuckleDelay(pair, unbuckleAt);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.EqualTo(bed));
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoReleaseStabilizedPatients = releaseDuringRetirement;

            if (deleteOwner)
            {
                entities.DeleteEntity(retiredAgent);
            }
            else
            {
                mobState.ChangeMobState(retiredAgent, MobState.Dead);
                shuttle.RetireDeadRescueAgents();
            }

            Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);
        });

        if (releaseDuringRetirement)
        {
            await server.WaitAssertion(() =>
            {
                var buckle = entities.GetComponent<BuckleComponent>(patient);
                var strap = entities.GetComponent<StrapComponent>(bed);
                Assert.Multiple(() =>
                {
                    Assert.That(buckle.BuckledTo, Is.Null,
                        "An eligible stable patient must be released synchronously with its retiring owner.");
                    Assert.That(strap.BuckledEntities, Does.Not.Contain(patient));
                    Assert.That(
                        entities.EntityQuery<LuaMRescueAgentComponent>().Count(rescue =>
                            rescue.OnboardCareTarget == patient ||
                            rescue.AssignedTarget == patient ||
                            rescue.TaskPatientTarget == patient),
                        Is.Zero,
                        "A physical release must not leave a second logical owner behind.");
                    Assert.That(
                        shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient),
                        Is.False,
                        "A safely released mildly injured patient must not be requeued from stale activity lineage.");
                });

                AssertBedAcceptsNextPatient(entities, buckleSystem, map, bed);
            });
        }
        else
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.EqualTo(bed));
                Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
                Assert.That(agentSystem.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True);
                Assert.That(replacement, Is.Not.EqualTo(retiredAgent));
                Assert.That(coordinator.GetSnapshot(replacement, out var adoptedIntent), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityQuery<LuaMRescueAgentComponent>().Count(), Is.EqualTo(1));
                    Assert.That(
                        entities.EntityQuery<LuaMRescueAgentComponent>().Count(rescue =>
                            rescue.OnboardCareTarget == patient),
                        Is.EqualTo(1),
                        "The still-buckled patient must have exactly one replacement owner.");
                    Assert.That(replacementRescue.AssignedShuttle, Is.EqualTo((EntityUid?) map.Grid));
                    Assert.That(replacementRescue.AssignedPatientStrap, Is.EqualTo(bed));
                    Assert.That(replacementRescue.OnboardCareTarget, Is.EqualTo(patient));
                    Assert.That(replacementRescue.TaskPatientTarget, Is.EqualTo(patient));
                    Assert.That(adoptedIntent.Activity, Is.EqualTo(LuaMRescueActivity.OnboardCare));
                    Assert.That(adoptedIntent.Target, Is.EqualTo(patient));
                    Assert.That(adoptedIntent.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                });

                // Complete the lifecycle as well: after adoption the inherited
                // policy can be enabled and the original bed must become usable.
                replacementRescue.AutoReleaseStabilizedPatients = true;
                replacementRescue.AssignedReturnTarget = null;
            });

            var physicallyReleased = false;
            for (var tick = 0; tick < 10 && !physicallyReleased; tick++)
            {
                await server.WaitAssertion(() =>
                {
                    Assert.That(agentSystem.TryFindActiveAgent(out _, out var releaseOwner), Is.True);
                    releaseOwner.TargetRefreshAccumulator = releaseOwner.TargetRefreshInterval;
                    agentSystem.Update(releaseOwner.TargetRefreshInterval);
                    physicallyReleased = entities.GetComponent<BuckleComponent>(patient).BuckledTo == null;
                });
                if (!physicallyReleased)
                    await pair.RunTicksSync(1);
            }

            await server.WaitAssertion(() =>
            {
                Assert.That(entities.GetComponent<BuckleComponent>(patient).BuckledTo, Is.Null,
                    "Replacement ownership must still reach a real physical release.");
                Assert.That(entities.GetComponent<StrapComponent>(bed).BuckledEntities, Does.Not.Contain(patient));
                AssertBedAcceptsNextPatient(entities, buckleSystem, map, bed);
            });
        }

        await pair.CleanReturnAsync();
    }

    [TestCase(MobState.Critical)]
    [TestCase(MobState.Dead)]
    public async Task ExternalUnbuckleAfterOwnerRetirementPreservesAcceptedAutomaticMission(MobState patientState)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            retiredAgent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoReleaseStabilizedPatients = false;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var patientMind = minds.CreateMind(null, "LuaM onboard transfer patient");
            minds.TransferTo(patientMind, patient, createGhost: false, mind: patientMind.Comp);
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(retiredAgent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, retiredAgent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            retiredRescue.AssignedTarget = patient;
            retiredRescue.TaskPatientTarget = patient;
            retiredRescue.OnboardCareTarget = patient;
            retiredRescue.AssignedPatientStrap = bed;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out _),
                Is.True);
        });

        // Let only the physical buckle safety delay elapse while the patient is
        // stable. Deterioration and retirement then happen in one assertion, so
        // onboard treatment cannot consume or rewrite the accepted mission.
        await RunPastBuckleDelay(pair, unbuckleAt);
        await server.WaitAssertion(() =>
        {
            mobState.ChangeMobState(patient, patientState);
            var signalKind = patientState == MobState.Dead
                ? LuaMRescueMedicalSignalKind.Death
                : LuaMRescueMedicalSignalKind.Critical;
            Assert.That(
                shuttle.TryGetAutomaticMedicalSignalEligibility(
                    patient,
                    signalKind,
                    requireAttachedPlayer: false,
                    out var eligibilityStatus),
                Is.True,
                eligibilityStatus);

            mobState.ChangeMobState(retiredAgent, MobState.Dead);
            shuttle.RetireDeadRescueAgents();
            Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);

            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(
                buckleSystem.TryUnbuckle(patient, user: null, buckleComp: buckle, popup: false),
                Is.True,
                "The external handoff must race after ownership was captured but before it was reconciled.");
            Assert.That(buckle.BuckledTo, Is.Null);

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);

            var pendingCount = shuttle.GetPendingAutomaticDispatches().Count(entry => entry.Target == patient);
            var ownerCount = entities.EntityQuery<LuaMRescueAgentComponent>().Count(rescue =>
                rescue.AssignedTarget == patient ||
                rescue.TaskPatientTarget == patient ||
                rescue.OnboardCareTarget == patient ||
                rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
                rescue.ActivityContext.Target == patient);
            Assert.That(ownerCount + pendingCount, Is.EqualTo(1),
                "Losing the physical strap must not erase or duplicate the accepted automatic patient mission.");

            Assert.That(agentSystem.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True,
                "Processing the preserved lineage must provide a replacement owner.");
            Assert.That(replacement, Is.Not.EqualTo(retiredAgent));
            Assert.That(
                replacementRescue.AssignedTarget == patient ||
                replacementRescue.TaskPatientTarget == patient ||
                replacementRescue.OnboardCareTarget == patient ||
                replacementRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
                replacementRescue.ActivityContext.Target == patient,
                Is.True,
                "The replacement must actively own the unbuckled critical/dead patient after reconciliation.");
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task InvalidOnboardTransferKeepsRequiredCustodyAcrossMedicalIneligibility(bool unrevivable)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            retiredAgent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoReleaseStabilizedPatients = false;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            if (unrevivable)
            {
                var patientMind = minds.CreateMind(null, "LuaM unrevivable custody patient");
                minds.TransferTo(patientMind, patient, createGhost: false, mind: patientMind.Comp);
                entities.EnsureComponent<UnrevivableComponent>(patient);
            }

            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(retiredAgent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, retiredAgent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            retiredRescue.AssignedTarget = patient;
            retiredRescue.TaskPatientTarget = patient;
            retiredRescue.OnboardCareTarget = patient;
            retiredRescue.AssignedPatientStrap = bed;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out _),
                Is.True);

        });

        await RunPastBuckleDelay(pair, unbuckleAt);

        await server.WaitAssertion(() =>
        {
            var buckle = entities.GetComponent<BuckleComponent>(patient);

            mobState.ChangeMobState(patient, MobState.Dead);
            Assert.That(
                coordinator.IsEligibleRescuePatient(
                    retiredAgent,
                    patient,
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                    manualOverride: false,
                    out var failure),
                Is.False);
            Assert.That(
                failure,
                Is.EqualTo(unrevivable
                    ? LuaMRescueFailureReason.Unrevivable
                    : LuaMRescueFailureReason.TargetHasNoMind));

            mobState.ChangeMobState(retiredAgent, MobState.Dead);
            shuttle.RetireDeadRescueAgents();
            Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);

            // Invalidate the captured physical transfer after retirement. The
            // accepted custody mission must now be reconstructed as evacuation,
            // even though the body is not medically recoverable.
            Assert.That(
                buckleSystem.TryUnbuckle(patient, user: null, buckleComp: buckle, popup: false),
                Is.True);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(agentSystem.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True);
            Assert.That(replacement, Is.Not.EqualTo(retiredAgent));
            Assert.That(replacementRescue.RequiredOnboardHandoffPatients, Does.Contain(patient));
            Assert.That(
                replacementRescue.AssignedTarget == patient ||
                replacementRescue.EvacuatingTarget == patient ||
                replacementRescue.TaskPatientTarget == patient ||
                replacementRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
                replacementRescue.ActivityContext.Target == patient,
                Is.True,
                "The replacement must adopt the medically ineligible body instead of orphaning custody.");

            replacementRescue.TargetRefreshAccumulator = replacementRescue.TargetRefreshInterval;
            agentSystem.Update(replacementRescue.TargetRefreshInterval);

            Assert.Multiple(() =>
            {
                Assert.That(replacementRescue.RequiredOnboardHandoffPatients, Does.Contain(patient),
                    "The generic target gate must preserve required physical custody.");
                Assert.That(
                    replacementRescue.AssignedTarget == patient ||
                    replacementRescue.EvacuatingTarget == patient ||
                    replacementRescue.TaskPatientTarget == patient ||
                    replacementRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
                    replacementRescue.ActivityContext.Target == patient,
                    Is.True,
                    "A normal agent update must not clear the adopted handoff mission.");
                Assert.That(shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualRequiredRetirementAfterExternalUnbuckleKeepsAutomaticCustodyWithoutManualLineage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        uint retiredManualGeneration = 0;
        await server.WaitAssertion(() =>
        {
            retiredAgent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoAcquireTargets = false;
            retiredRescue.AutoReleaseStabilizedPatients = false;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(retiredAgent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            retiredManualGeneration = agentSystem.EstablishManualOverrideTarget(retiredAgent, retiredRescue, patient);
            Assert.That(retiredManualGeneration, Is.GreaterThan(0));
            Assert.That(retiredRescue.ManualOverrideTarget, Is.EqualTo(patient));

            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, retiredAgent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            retiredRescue.AssignedTarget = patient;
            retiredRescue.TaskPatientTarget = patient;
            retiredRescue.OnboardCareTarget = patient;
            retiredRescue.AssignedPatientStrap = bed;
            retiredRescue.RequiredOnboardHandoffPatients.Add(patient);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out _),
                Is.True);
        });

        await RunPastBuckleDelay(pair, unbuckleAt);

        await server.WaitAssertion(() =>
        {
            mobState.ChangeMobState(retiredAgent, MobState.Dead);
            shuttle.RetireDeadRescueAgents();
            Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);

            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(
                buckleSystem.TryUnbuckle(patient, user: null, buckleComp: buckle, popup: false),
                Is.True);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(agentSystem.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(replacement, Is.Not.EqualTo(retiredAgent));
                Assert.That(replacementRescue.RequiredOnboardHandoffPatients, Does.Contain(patient));
                Assert.That(replacementRescue.ManualOverrideTarget, Is.Null,
                    "Manual authority belongs to the retired agent, not to replacement custody.");
                Assert.That(replacementRescue.ManualOverrideGeneration, Is.Zero);
                Assert.That(retiredManualGeneration, Is.GreaterThan(replacementRescue.ManualOverrideGeneration));
                Assert.That(
                    replacementRescue.AssignedTarget == patient ||
                    replacementRescue.EvacuatingTarget == patient ||
                    replacementRescue.TaskPatientTarget == patient ||
                    replacementRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
                    replacementRescue.ActivityContext.Target == patient,
                    Is.True,
                    "Required custody must survive as automatic ownership after external unbuckle.");
                Assert.That(
                    shuttle.GetPendingAutomaticDispatches().Where(entry => entry.Target == patient)
                        .All(entry => !entry.ManualOverride && entry.ManualOverrideOwner == null &&
                                      entry.ManualOverrideGeneration == 0),
                    Is.True);
                Assert.That(shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StructurallyInvalidRequiredTransferTerminalizesWithoutBareActiveCustody(bool removePullable)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            retiredAgent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoAcquireTargets = false;
            retiredRescue.AutoReleaseStabilizedPatients = false;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(retiredAgent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, retiredAgent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            retiredRescue.AssignedTarget = patient;
            retiredRescue.TaskPatientTarget = patient;
            retiredRescue.OnboardCareTarget = patient;
            retiredRescue.AssignedPatientStrap = bed;
            retiredRescue.RequiredOnboardHandoffPatients.Add(patient);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out _),
                Is.True);
            mobState.ChangeMobState(patient, MobState.Critical);
        });

        await RunPastBuckleDelay(pair, unbuckleAt);

        await server.WaitAssertion(() =>
        {
            mobState.ChangeMobState(retiredAgent, MobState.Dead);
            shuttle.RetireDeadRescueAgents();
            Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);

            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(
                buckleSystem.TryUnbuckle(patient, user: null, buckleComp: buckle, popup: false),
                Is.True);
            if (removePullable)
                entities.RemoveComponent<PullableComponent>(patient);
            else
                entities.RemoveComponent<BuckleComponent>(patient);

            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            Assert.That(agentSystem.TryFindActiveAgent(out var replacement, out var replacementRescue), Is.True);
            var terminal = shuttle.GetTerminalAutomaticDispatches().Single(entry => entry.Target == patient);

            Assert.Multiple(() =>
            {
                Assert.That(replacement, Is.Not.EqualTo(retiredAgent));
                Assert.That(replacementRescue.RequiredOnboardHandoffPatients, Does.Not.Contain(patient),
                    "A structural rejection must not leave Required custody without an active mission.");
                Assert.That(
                    replacementRescue.AssignedTarget == patient ||
                    replacementRescue.EvacuatingTarget == patient ||
                    replacementRescue.TaskPatientTarget == patient ||
                    replacementRescue.OnboardCareTarget == patient,
                    Is.False);
                Assert.That(shuttle.GetPendingAutomaticDispatches().Any(entry => entry.Target == patient), Is.False);
                Assert.That(terminal.Terminal, Is.True);
                Assert.That(terminal.ManualOverride, Is.False);
                Assert.That(terminal.LastStatus, Does.Contain("structurally ineligible").IgnoreCase);
                Assert.That(
                    terminal.LastStatus,
                    removePullable
                        ? Does.Contain("TargetNotPullable").IgnoreCase
                        : Does.Contain("buckle").IgnoreCase);
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(MobState.Critical)]
    [TestCase(MobState.Dead)]
    public async Task RecoveredBeforeAdoptionKeepsRequiredQueueAsFollowUp(MobState initialState)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttle = entities.System<LuaMRescueShuttleSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        EntityUid retiredAgent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;
        await server.WaitAssertion(() =>
        {
            retiredAgent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var retiredRescue = entities.GetComponent<LuaMRescueAgentComponent>(retiredAgent);
            retiredRescue.AutoAcquireTargets = false;
            retiredRescue.AutoReleaseStabilizedPatients = false;

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var patientMind = minds.CreateMind(null, "LuaM recovered required-custody patient");
            minds.TransferTo(patientMind, patient, createGhost: false, mind: patientMind.Comp);
            entities.RemoveComponent<BarotraumaComponent>(retiredAgent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<RespiratorComponent>(retiredAgent);
            entities.RemoveComponent<RespiratorComponent>(patient);
            bed = entities.SpawnEntity("MedicalBed", GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, retiredAgent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            retiredRescue.AssignedTarget = patient;
            retiredRescue.TaskPatientTarget = patient;
            retiredRescue.OnboardCareTarget = patient;
            retiredRescue.AssignedPatientStrap = bed;
            retiredRescue.RequiredOnboardHandoffPatients.Add(patient);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    retiredAgent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out _),
                Is.True);
            mobState.ChangeMobState(patient, initialState);
        });

        await RunPastBuckleDelay(pair, unbuckleAt);

        await server.WaitAssertion(() =>
        {
            mobState.ChangeMobState(retiredAgent, MobState.Dead);
            shuttle.RetireDeadRescueAgents();
            Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(retiredAgent), Is.False);

            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(
                buckleSystem.TryUnbuckle(patient, user: null, buckleComp: buckle, popup: false),
                Is.True);

            var busyAgent = SpawnOnboardAgent(entities, map, new Vector2(1f, 0f));
            var busyRescue = entities.GetComponent<LuaMRescueAgentComponent>(busyAgent);
            busyRescue.AutoAcquireTargets = false;
            busyRescue.EvacuateTargetsToShuttle = false;
            var busyPatient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(1.5f, 0f)));
            entities.RemoveComponent<BarotraumaComponent>(busyAgent);
            entities.RemoveComponent<BarotraumaComponent>(busyPatient);
            entities.RemoveComponent<RespiratorComponent>(busyAgent);
            entities.RemoveComponent<RespiratorComponent>(busyPatient);
            Assert.That(
                agentSystem.TryOrderAgent(busyAgent, busyPatient, out var busyStatus),
                Is.True,
                busyStatus);

            mobState.ChangeMobState(patient, MobState.Alive);
            Assert.That(shuttle.ProcessPendingAutomaticDispatchesNow(), Is.True);
            var pending = shuttle.GetPendingAutomaticDispatches().Single(entry => entry.Target == patient);

            Assert.Multiple(() =>
            {
                Assert.That(pending.Kind, Is.EqualTo(LuaMRescueMedicalSignalKind.FollowUp));
                Assert.That(pending.ManualOverride, Is.False);
                Assert.That(pending.Attempts, Is.Zero,
                    "Waiting on the singleton must not consume the required-custody retry budget.");
                Assert.That(pending.Terminal, Is.False);
                Assert.That(pending.LastStatus, Does.Contain("busy").IgnoreCase);
                Assert.That(shuttle.GetTerminalAutomaticDispatches().Any(entry => entry.Target == patient), Is.False);
                Assert.That(busyRescue.ManualOverrideTarget, Is.EqualTo(busyPatient));
                Assert.That(busyRescue.RequiredOnboardHandoffPatients, Does.Not.Contain(patient),
                    "Required is queue provenance until dispatch commits; it must not become bare agent state.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExplicitOrderCannotStrandDifferentPatientStillBuckledOnboard()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.AutoReleaseStabilizedPatients = false;
            var onboard = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map, new Vector2(0.5f, 0f)));
            var replacementOrder = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map, new Vector2(0.75f, 0f)));
            var bed = entities.SpawnEntity(
                "MedicalBed",
                GridCoordinates(map, new Vector2(0.5f, 0f)));
            var buckle = entities.GetComponent<BuckleComponent>(onboard);
            Assert.That(buckleSystem.TryBuckle(onboard, agent, bed, buckleComp: buckle), Is.True);
            rescue.AssignedTarget = onboard;
            rescue.TaskPatientTarget = onboard;
            rescue.OnboardCareTarget = onboard;
            rescue.AssignedPatientStrap = bed;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    onboard,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out var onboardIntent),
                Is.True);

            Assert.That(
                agents.TryOrderAgent(agent, replacementOrder, out var status),
                Is.False,
                status);
            Assert.Multiple(() =>
            {
                Assert.That(status, Does.Contain("onboard handoff").IgnoreCase);
                Assert.That(buckle.BuckledTo, Is.EqualTo(bed));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(onboard));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(onboard));
                Assert.That(rescue.OnboardCareTarget, Is.EqualTo(onboard));
                Assert.That(rescue.AssignedPatientStrap, Is.EqualTo(bed));
                Assert.That(rescue.ActivityContext.Generation, Is.EqualTo(onboardIntent.Generation));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(onboard));
                Assert.That(rescue.ManualOverrideTarget, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeletedRequiredPatientReleasesBlockedOwnershipAndAllowsExplicitOrder()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = SpawnOnboardAgent(entities, map, Vector2.Zero);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;

            var required = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map, new Vector2(0.5f, 0f)));
            var replacement = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(map, new Vector2(0.75f, 0f)));

            rescue.AssignedTarget = required;
            rescue.TaskStage = LuaMRescueTaskStage.DeliveringPatient;
            rescue.TaskPatientTarget = required;
            rescue.RequiredOnboardHandoffPatients.Add(required);
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.PreparingEvacuation,
                    required,
                    new EntityCoordinates(required, Vector2.Zero),
                    out _),
                Is.True);
            Assert.That(
                coordinator.Block(
                    agent,
                    LuaMRescueFailureReason.TargetNotPullable,
                    LuaMRescueActivity.Handoff,
                    out _),
                Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(rescue.RequiredOnboardHandoffPatients, Does.Contain(required));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(required));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(required));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(rescue.ActivityContext.FailureReason, Is.EqualTo(LuaMRescueFailureReason.TargetNotPullable));
            });

            entities.DeleteEntity(required);
            agents.Update(rescue.TargetRefreshInterval);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.RequiredOnboardHandoffPatients, Does.Not.Contain(required));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.EvacuatingTarget, Is.Null);
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Null);
                Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.None));
                Assert.That(rescue.AssignedPatientStrap, Is.Null);
            });

            Assert.That(
                agents.TryOrderAgent(agent, replacement, out var status),
                Is.True,
                status);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacement));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacement));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(replacement));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ConfiguredHomeHandoffRequiresReciprocalPhysicalDockBeforeRelease()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var buckleSystem = entities.System<SharedBuckleSystem>();
        var agentSystem = entities.System<LuaMRescueAgentSystem>();
        var activity = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var docking = entities.System<DockingSystem>();
        var shuttleSystem = entities.System<ShuttleSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        EntityUid home = default;
        EntityUid shuttleDock = default;
        EntityUid homeDock = default;
        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid bed = default;
        TimeSpan unbuckleAt = default;

        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(map.Grid);

            var shuttleGrid = mapManager.CreateGridEntity(map.MapId);
            var homeGrid = mapManager.CreateGridEntity(map.MapId);
            shuttle = shuttleGrid.Owner;
            home = homeGrid.Owner;
            transform.SetLocalPosition(home, new Vector2(20f, 0f));

            mapSystem.SetTiles(
                shuttle,
                shuttleGrid.Comp,
                new List<(Vector2i Index, Tile Tile)>
                {
                    new(new Vector2i(0, 0), new Tile(1)),
                    new(new Vector2i(0, 1), new Tile(1)),
                    new(new Vector2i(0, 2), new Tile(1)),
                });
            mapSystem.SetTiles(
                home,
                homeGrid.Comp,
                new List<(Vector2i Index, Tile Tile)>
                {
                    new(new Vector2i(0, 0), new Tile(1)),
                    new(new Vector2i(0, 1), new Tile(1)),
                    new(new Vector2i(-1, 1), new Tile(1)),
                    new(new Vector2i(1, 1), new Tile(1)),
                });

            shuttleDock = entities.SpawnEntity(
                "AirlockShuttle",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)));
            homeDock = entities.SpawnEntity(
                "AirlockShuttle",
                new EntityCoordinates(home, new Vector2(0.5f, 0.5f)));
            bed = entities.SpawnEntity(
                "MedicalBed",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 1.5f)));
            patient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 1.5f)));
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 2.5f)));

            // These deliberately minimal grids have no atmosphere. Vacuum damage
            // is unrelated to the home-handoff contract and would make a stable
            // patient enter onboard treatment while the docking phases are tested.
            entities.RemoveComponent<BarotraumaComponent>(patient);
            entities.RemoveComponent<BarotraumaComponent>(agent);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AssignedShuttle = shuttle;
            rescue.AssignedReturnTarget = home;
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.EvacuateTargetsToShuttle = true;
            rescue.AutoRouteShuttleToTargets = false;
            rescue.AutoReturnShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AutoPickupNearbyMedicalSupplies = false;
            rescue.AutoTakeNearbyStoredMedicalSupplies = false;
            rescue.AutoResupplyFromVending = false;
            rescue.AutoReleaseStabilizedPatients = true;
            rescue.AutoReleaseRange = 2f;

            var buckle = entities.GetComponent<BuckleComponent>(patient);
            Assert.That(buckleSystem.TryBuckle(patient, agent, bed, buckleComp: buckle), Is.True);
            unbuckleAt = buckle.BuckleTime!.Value + buckle.Delay;

            rescue.OnboardCareTarget = patient;
            rescue.AssignedPatientStrap = bed;
            Assert.That(
                activity.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(bed, Vector2.Zero),
                    out var onboardIntent),
                Is.True);
            Assert.That(onboardIntent.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));

            var lifecycle = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.Target = home;
            lifecycle.RouteActivity = LuaMRescueActivity.Returning;
            lifecycle.State = LuaMRescueShuttleRouteState.None;
            lifecycle.SafeExitConfirmed = false;

            Assert.That(docking.GetDockingConfig(shuttle, home), Is.Not.Null);
        });

        // The handoff guard, rather than the normal buckle delay, must be what
        // keeps the patient onboard throughout the following route states.
        await RunPastBuckleDelay(pair, unbuckleAt);

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var lifecycle = entities.GetComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Routing;
            lifecycle.SafeExitConfirmed = false;
            agentSystem.Update(rescue.TargetRefreshInterval);

            AssertHomeHandoffPatientHeld(
                entities,
                rescue,
                lifecycle,
                patient,
                bed,
                LuaMRescueShuttleRouteState.Routing);
        });

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var lifecycle = entities.GetComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Arrived;
            lifecycle.SafeExitConfirmed = false;
            agentSystem.Update(rescue.TargetRefreshInterval);

            AssertHomeHandoffPatientHeld(
                entities,
                rescue,
                lifecycle,
                patient,
                bed,
                LuaMRescueShuttleRouteState.Arrived);
        });

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var lifecycle = entities.GetComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Docked;
            lifecycle.SafeExitConfirmed = true;
            agentSystem.Update(rescue.TargetRefreshInterval);

            AssertHomeHandoffPatientHeld(
                entities,
                rescue,
                lifecycle,
                patient,
                bed,
                LuaMRescueShuttleRouteState.Docked);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<DockingComponent>(shuttleDock).DockedWith, Is.Null);
                Assert.That(entities.GetComponent<DockingComponent>(homeDock).DockedWith, Is.Null);
            });
        });

        await server.WaitAssertion(() =>
        {
            var config = docking.GetDockingConfig(shuttle, home);
            Assert.That(config, Is.Not.Null);
            shuttleSystem.FTLDock(
                (shuttle, entities.GetComponent<TransformComponent>(shuttle)),
                config!);

            var shuttleDocking = entities.GetComponent<DockingComponent>(shuttleDock);
            var homeDocking = entities.GetComponent<DockingComponent>(homeDock);
            Assert.Multiple(() =>
            {
                Assert.That(shuttleDocking.DockedWith, Is.EqualTo(homeDock));
                Assert.That(homeDocking.DockedWith, Is.EqualTo(shuttleDock));
                Assert.That(shuttleDocking.PathfindHandle, Is.GreaterThanOrEqualTo(0));
                Assert.That(homeDocking.PathfindHandle, Is.EqualTo(shuttleDocking.PathfindHandle));
            });

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var lifecycle = entities.GetComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Docked;
            lifecycle.RouteActivity = LuaMRescueActivity.Returning;
            lifecycle.Target = home;
            lifecycle.SafeExitConfirmed = true;
            agentSystem.Update(rescue.TargetRefreshInterval);

            var buckle = entities.GetComponent<BuckleComponent>(patient);
            var strap = entities.GetComponent<StrapComponent>(bed);
            Assert.Multiple(() =>
            {
                Assert.That(buckle.BuckledTo, Is.Null,
                    "Only reciprocal docking at the configured home grid may release the patient.");
                Assert.That(strap.BuckledEntities, Does.Not.Contain(patient));
                Assert.That(rescue.OnboardCareTarget, Is.Null);
                Assert.That(rescue.AssignedPatientStrap, Is.Null);
                Assert.That(rescue.LastOnboardCareStatus, Does.Contain("released").IgnoreCase);
            });
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnOnboardAgent(
        IEntityManager entities,
        TestMapData map,
        Vector2 position)
    {
        var agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map, position));
        var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
        rescue.AssignedShuttle = map.Grid;
        rescue.AutoAcquireTargets = true;
        rescue.TargetRefreshInterval = 0.001f;
        rescue.EvacuateTargetsToShuttle = true;
        rescue.AutoRouteShuttleToTargets = false;
        rescue.AutoReturnShuttle = false;
        rescue.AutoAnalyzeBeforeTreatment = false;
        rescue.AutoDefibDeadPatients = false;
        rescue.AutoPickupNearbyMedicalSupplies = false;
        rescue.AutoTakeNearbyStoredMedicalSupplies = false;
        rescue.AutoResupplyFromVending = false;
        rescue.AutoReleaseStabilizedPatients = true;
        rescue.AutoReleaseRange = 2f;
        return agent;
    }

    private static void AssertHomeHandoffPatientHeld(
        IEntityManager entities,
        LuaMRescueAgentComponent rescue,
        LuaMRescueShuttleLifecycleComponent lifecycle,
        EntityUid patient,
        EntityUid bed,
        LuaMRescueShuttleRouteState expectedState)
    {
        var buckle = entities.GetComponent<BuckleComponent>(patient);
        var strap = entities.GetComponent<StrapComponent>(bed);
        Assert.Multiple(() =>
        {
            Assert.That(lifecycle.State, Is.EqualTo(expectedState));
            Assert.That(lifecycle.RouteActivity, Is.EqualTo(LuaMRescueActivity.Returning));
            Assert.That(lifecycle.Target, Is.EqualTo(rescue.AssignedReturnTarget));
            Assert.That(buckle.BuckledTo, Is.EqualTo(bed));
            Assert.That(strap.BuckledEntities, Does.Contain(patient));
            Assert.That(rescue.OnboardCareTarget, Is.EqualTo(patient));
            Assert.That(rescue.AssignedPatientStrap, Is.EqualTo(bed));
            Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.OnboardCare));
            Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
            Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            Assert.That(rescue.OnboardHandoffAttempts.GetValueOrDefault(patient), Is.Zero);
            Assert.That(rescue.IgnoredOnboardPatients, Does.Not.Contain(patient));
        });
    }

    private static EntityCoordinates GridCoordinates(TestMapData map, Vector2 position)
    {
        return new EntityCoordinates(map.Grid, position);
    }

    private static void AssertBedAcceptsNextPatient(
        IEntityManager entities,
        SharedBuckleSystem buckleSystem,
        TestMapData map,
        EntityUid bed)
    {
        var nextPatient = entities.SpawnEntity("MobHuman", GridCoordinates(map, new Vector2(0.5f, 0f)));
        var nextBuckle = entities.GetComponent<BuckleComponent>(nextPatient);
        Assert.That(
            buckleSystem.TryBuckle(nextPatient, user: null, strap: bed, buckleComp: nextBuckle, popup: false),
            Is.True,
            "The released medical bed must accept the next patient.");
    }

    private static async Task RunPastBuckleDelay(TestPair pair, TimeSpan readyAt)
    {
        var tickPeriod = pair.Server.Timing.TickPeriod;
        var remaining = readyAt - pair.Server.Timing.CurTime;
        var ticks = Math.Max(
            1,
            (int) Math.Ceiling(Math.Max(0d, remaining.TotalSeconds) / tickPeriod.TotalSeconds) + 1);
        await pair.RunTicksSync(ticks);
    }
}
