using System.Numerics;
using System.Linq;
using Content.IntegrationTests.Pair;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Body.Systems;
using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Server.NPC;
using Content.Server._LuaM.Rescue;
using Content.Server.Gravity;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Teleportation.Components;
using Content.Shared.Teleportation.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueNavigationSystem))]
public sealed class LuaMRescueNavigationRuntimeTest
{
    private const float RescueActionRange = 1.5f;
    private const int ProbeTickLimit = 180;

    [Test]
    public async Task AgentUsesOpenGatewayToReachPatientOnGeneratedMap()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var gatewaySystem = entities.System<GatewaySystem>();
        var transform = entities.System<SharedTransformSystem>();
        var linked = entities.System<LinkedEntitySystem>();
        var sourceMap = await pair.CreateTestMap();
        var destinationMap = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid sourceGateway = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, sourceMap, 0, 3);
            BuildHorizontalFloor(mapSystem, destinationMap, 0, 3);
            entities.System<GravitySystem>().EnableGravity(
                sourceMap.Grid,
                entities.EnsureComponent<GravityComponent>(sourceMap.Grid));
            entities.System<GravitySystem>().EnableGravity(
                destinationMap.Grid,
                entities.EnsureComponent<GravityComponent>(destinationMap.Grid));

            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(sourceMap.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;

            sourceGateway = entities.SpawnEntity("Gateway", GridCoordinates(sourceMap.Grid, 2.5f, 0.5f));
            var destinationGateway = entities.SpawnEntity("Gateway", GridCoordinates(destinationMap.Grid, 0.5f, 0.5f));
            gatewaySystem.SetEnabled(sourceGateway, true);
            gatewaySystem.SetEnabled(destinationGateway, true);
            var sourcePortal = entities.EnsureComponent<PortalComponent>(sourceGateway);
            var destinationPortal = entities.EnsureComponent<PortalComponent>(destinationGateway);
            sourcePortal.CanTeleportToOtherMaps = true;
            sourcePortal.RandomTeleport = false;
            destinationPortal.CanTeleportToOtherMaps = true;
            destinationPortal.RandomTeleport = false;
            Assert.That(linked.TryLink(sourceGateway, destinationGateway), Is.True);

            patient = entities.SpawnEntity("MobHuman", GridCoordinates(destinationMap.Grid, 2.5f, 0.5f));
            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
        });

        var crossedGateway = false;
        var reachedPatient = false;
        for (var i = 0; i < ProbeTickLimit * 4; i += 5)
        {
            await pair.RunTicksSync(5);
            await server.WaitAssertion(() =>
            {
                crossedGateway |= entities.GetComponent<TransformComponent>(agent).MapID ==
                                  entities.GetComponent<TransformComponent>(patient).MapID;
                reachedPatient = crossedGateway && Vector2.Distance(
                    transform.GetWorldPosition(agent),
                    transform.GetWorldPosition(patient)) <= RescueActionRange;
            });

            if (reachedPatient)
                break;
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(crossedGateway, Is.True,
                    $"Aibolit never crossed {sourceGateway}; tracking={rescue.LastTargetTrackingStatus}");
                Assert.That(reachedPatient, Is.True,
                    $"Aibolit crossed the gateway but did not resume approach; tracking={rescue.LastTargetTrackingStatus}");
            });
        });

        EntityUid shuttleAnchor = default;
        await server.WaitAssertion(() =>
        {
            shuttleAnchor = entities.SpawnEntity("MedicalBed", GridCoordinates(sourceMap.Grid, 0.5f, 0.5f));
            entities.System<MobStateSystem>().ChangeMobState(patient, MobState.Critical);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.TargetRefreshInterval = 0.01f;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            rescue.AutoAcquireTargets = true;
            rescue.AutoDefibDeadPatients = false;
            rescue.AutoUnbucklePatientsForEvacuation = false;
            rescue.AutoRouteShuttleToTargets = false;
            rescue.AutoReturnShuttle = false;
            rescue.EvacuateTargetsToShuttle = true;
            rescue.BucklePatientsOnShuttle = false;
            rescue.AssignedShuttle = sourceMap.Grid;
            rescue.AssignedShuttleAnchor = shuttleAnchor;
            rescue.AssignedTarget = patient;
        });

        var patientReturned = false;
        var rescuerReturned = false;
        for (var i = 0; i < ProbeTickLimit * 6; i += 5)
        {
            await pair.RunTicksSync(5);
            await server.WaitAssertion(() =>
            {
                var sourceMapId = entities.GetComponent<TransformComponent>(shuttleAnchor).MapID;
                patientReturned |= entities.GetComponent<TransformComponent>(patient).MapID == sourceMapId;
                rescuerReturned |= entities.GetComponent<TransformComponent>(agent).MapID == sourceMapId;
            });

            if (patientReturned && rescuerReturned)
                break;
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(patientReturned, Is.True,
                    $"The immobile patient was left in the expedition; return={rescue.LastShuttleReturnStatus}");
                Assert.That(rescuerReturned, Is.True,
                    $"Aibolit did not follow the patient through the return gateway; return={rescue.LastShuttleReturnStatus}");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TwentyThreeMeterPatientIsApproachedWithinActionRange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitPost(() =>
        {
            // Keep the global optimization enabled: the rescue prototype's
            // role-level opt-out is what this locomotion contract exercises.
            server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, true);
            var gravity = entities.EnsureComponent<GravityComponent>(map.Grid);
            entities.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });

        EntityUid agent = default;
        EntityUid target = default;
        LuaMRescuePathProbeSnapshot initial = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 23);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.That(htn.PauseWhenNoPlayersInRange, Is.False,
                "A sector rescue assignment must continue without a nearby living player.");
            target = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 23.5f, 0.5f));
            initial = navigation.ProbeRoute(agent, target, RescueActionRange);
        });

        Assert.Multiple(() =>
        {
            Assert.That(RescueActionRange, Is.LessThanOrEqualTo(1.5f));
            Assert.That(initial.State, Is.EqualTo(LuaMRescuePathProbeState.Pending));
            Assert.That(initial.Distance, Is.EqualTo(23f).Within(0.01f));
            Assert.That(initial.Distance, Is.GreaterThan(RescueActionRange));
        });

        var completed = await AwaitCompletedProbe(pair, navigation, agent, target, RescueActionRange);
        Assert.Multiple(() =>
        {
            Assert.That(completed.State, Is.EqualTo(LuaMRescuePathProbeState.Reachable));
            Assert.That(completed.Distance, Is.EqualTo(23f).Within(0.01f));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(rescueSystem.TryOrderAgent(agent, target, out var status), Is.True, status);
        });

        var steeringStart = await AwaitSteeringTarget(pair, entities, agent, target);
        Assert.That(
            steeringStart.Started,
            Is.True,
            "The movement budget must start only after HTN planning has installed steering for the patient. " +
            steeringStart.Diagnostics);

        var approached = false;
        var finalDistance = float.PositiveInfinity;
        for (var i = 0; i < ProbeTickLimit * 5; i += 5)
        {
            await pair.RunTicksSync(5);
            await server.WaitAssertion(() =>
            {
                finalDistance = Vector2.Distance(
                    transform.GetWorldPosition(agent),
                    transform.GetWorldPosition(target));
            });

            if (finalDistance <= RescueActionRange)
            {
                approached = true;
                break;
            }
        }

        Assert.That(
            approached,
            Is.True,
            $"Aibolit remained {finalDistance:0.00} m from a patient after bounded real movement ticks.");

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AgentProgressReprobesInBackgroundWithoutClearingFollowTarget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitPost(() =>
        {
            var gravity = entities.EnsureComponent<GravityComponent>(map.Grid);
            entities.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });

        EntityUid agent = default;
        EntityUid target = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 23);
            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            target = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 23.5f, 0.5f));
        });

        var completed = await AwaitCompletedProbe(pair, navigation, agent, target, RescueActionRange);
        Assert.That(completed.State, Is.EqualTo(LuaMRescuePathProbeState.Reachable));

        await server.WaitAssertion(() =>
        {
            Assert.That(rescueSystem.TryOrderAgent(agent, target, out var status), Is.True, status);
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var follow,
                    entities),
                Is.True);
            Assert.That(follow.EntityId, Is.EqualTo(target));

            // Ordinary route progress exceeds the old 0.5 m invalidation
            // threshold. Force the next coordinator refresh to exercise the
            // background re-probe immediately and deterministically.
            transform.SetLocalPosition(agent, new Vector2(1.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
        });

        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var follow,
                    entities),
                Is.True,
                "A background route refresh caused by rescuer progress must not stop FollowCompound.");
            Assert.That(follow.EntityId, Is.EqualTo(target));

            var visible = navigation.ProbeRoute(agent, target, RescueActionRange);
            Assert.That(
                visible.State,
                Is.EqualTo(LuaMRescuePathProbeState.Reachable),
                "The last authoritative route must remain usable while its background replacement is pending.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StockAibolitStartsInternalsInVacuumAndApproachesInZeroGravity()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var internals = entities.System<InternalsSystem>();
        var inventory = entities.System<InventorySystem>();
        var dispatchConsole = entities.System<LuaMRescueDispatchConsoleSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid target = default;
        EntityUid initialTank = default;
        uint manualGeneration = 0;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 8);
            var gravity = entities.EnsureComponent<GravityComponent>(map.Grid);
            Assert.Multiple(() =>
            {
                Assert.That(gravity.Enabled, Is.False,
                    "The fixture must remain a real zero-gravity grid.");
            });

            // Map-root coordinates are real space. Starting gear must connect
            // oxygen there before the rescuer enters the zero-gravity route.
            agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            Assert.That(inventory.TryGetSlotEntity(agent, "outerClothing", out var suit), Is.True);
            Assert.That(inventory.TryGetSlotEntity(agent, "head", out var helmet), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<MetaDataComponent>(suit.Value).EntityPrototype?.ID,
                    Is.EqualTo("ClothingOuterHardsuitDeathsquad"));
                Assert.That(entities.GetComponent<MetaDataComponent>(helmet.Value).EntityPrototype?.ID,
                    Is.EqualTo("ClothingHeadHelmetHardsuitDeathsquad"));
            });
            var escort = entities.SpawnEntity("LuaMRescueEscort", map.MapCoords);
            Assert.That(inventory.TryGetSlotEntity(escort, "outerClothing", out var escortSuit), Is.True);
            Assert.That(inventory.TryGetSlotEntity(escort, "head", out var escortHelmet), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<MetaDataComponent>(escortSuit.Value).EntityPrototype?.ID,
                    Is.EqualTo("ClothingOuterHardsuitDeathsquad"));
                Assert.That(entities.GetComponent<MetaDataComponent>(escortHelmet.Value).EntityPrototype?.ID,
                    Is.EqualTo("ClothingHeadHelmetHardsuitDeathsquad"));
            });
            entities.DeleteEntity(escort);
            Assert.That(internals.AreInternalsWorking(agent), Is.True,
                "Stock Aibolit must connect its carried oxygen when spawned into vacuum.");
            initialTank = entities.GetComponent<InternalsComponent>(agent).GasTankEntity!.Value;
            var telemetry = dispatchConsole.BuildState().Agents.Single(entry => entry.Agent == entities.GetNetEntity(agent));
            Assert.Multiple(() =>
            {
                Assert.That(telemetry.InternalsActive, Is.True);
                Assert.That(telemetry.OxygenPressureKpa, Is.GreaterThan(0f));
                Assert.That(telemetry.LifeSupportStatus, Does.Contain("internals active"));
                Assert.That(telemetry.ReserveTankCount, Is.EqualTo(1));
                Assert.That(telemetry.BestReservePressureKpa, Is.GreaterThan(30f));
            });

            entities.GetComponent<GasTankComponent>(initialTank).Air.Clear();
            var lifeSupport = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            lifeSupport.LifeSupportCheckAccumulator = lifeSupport.LifeSupportCheckInterval;
            lifeSupport.AutoAcquireTargets = false;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var connectedTank = entities.GetComponent<InternalsComponent>(agent).GasTankEntity;
            var telemetry = dispatchConsole.BuildState().Agents.Single(entry => entry.Agent == entities.GetNetEntity(agent));
            Assert.Multiple(() =>
            {
                Assert.That(connectedTank, Is.Not.Null.And.Not.EqualTo(initialTank),
                    "Aibolit must replace a depleted active tank with its accessible reserve.");
                Assert.That(internals.AreInternalsWorking(agent), Is.True);
                Assert.That(telemetry.AutomaticSwapCount, Is.EqualTo(1));
                Assert.That(telemetry.OxygenPressureKpa, Is.GreaterThan(30f));
                Assert.That(telemetry.ReserveTankCount, Is.EqualTo(1),
                    "The depleted primary remains carried and visible as a non-usable reserve.");
                Assert.That(telemetry.LifeSupportStatus, Does.Contain("automatic oxygen replacement connected"));
            });

            transform.SetCoordinates(agent, GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            target = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 8.5f, 0.5f));
            entities.EnsureComponent<Content.Shared._Shitmed.Body.Components.BreathingImmunityComponent>(target);
            entities.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
        });

        var route = await AwaitCompletedProbe(pair, navigation, agent, target, RescueActionRange);
        Assert.That(route.State, Is.EqualTo(LuaMRescuePathProbeState.Reachable));
        await server.WaitAssertion(() =>
        {
            Assert.That(rescueSystem.TryOrderAgent(agent, target, out var status), Is.True, status);
            manualGeneration = entities.GetComponent<LuaMRescueAgentComponent>(agent).ManualOverrideGeneration;
            Assert.That(manualGeneration, Is.Not.Zero);
        });

        var steering = await AwaitSteeringTarget(pair, entities, agent, target);
        Assert.That(steering.Started, Is.True, steering.Diagnostics);

        var approached = false;
        var distance = float.PositiveInfinity;
        for (var i = 0; i < ProbeTickLimit * 4; i += 4)
        {
            await pair.RunTicksSync(4);
            await server.WaitAssertion(() =>
            {
                distance = Vector2.Distance(
                    transform.GetWorldPosition(agent),
                    transform.GetWorldPosition(target));
            });

            if (distance <= RescueActionRange)
            {
                approached = true;
                break;
            }
        }

        Assert.That(approached, Is.True,
            $"Stock Aibolit remained {distance:0.00} m from the patient in vacuum zero gravity.");

        EntityUid exhaustedReserve = default;
        await server.WaitAssertion(() =>
        {
            exhaustedReserve = entities.GetComponent<InternalsComponent>(agent).GasTankEntity!.Value;
            entities.GetComponent<GasTankComponent>(exhaustedReserve).Air.Clear();
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
        });
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var telemetry = dispatchConsole.BuildState().Agents.Single(entry => entry.Agent == entities.GetNetEntity(agent));
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<InternalsComponent>(agent).GasTankEntity,
                    Is.EqualTo(exhaustedReserve),
                    "An empty carried tank must not replace the connected empty reserve.");
                Assert.That(telemetry.AutomaticSwapCount, Is.EqualTo(1));
                Assert.That(telemetry.OxygenPressureKpa, Is.EqualTo(0f));
                Assert.That(telemetry.LifeSupportStatus, Does.Contain("no accessible replacement"));
                Assert.That(telemetry.LifeSupportEmergency, Is.True);
                Assert.That(telemetry.LifeSupportEmergencyCount, Is.EqualTo(1));
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                Assert.That(rescue.DeferredPatientTargets, Does.Not.ContainKey(target),
                    "A manual patient must remain owned by the manual override, not be duplicated in the automatic queue. " +
                    $"emergencyPatient={rescue.LifeSupportEmergencyPatient}; assigned={rescue.AssignedTarget}; " +
                    $"task={rescue.TaskPatientTarget}; activity={rescue.ActivityContext.Activity}/" +
                    $"{rescue.ActivityContext.Target}/{rescue.ActivityContext.TerminalStatus}");
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(target));
                Assert.That(rescue.ManualOverrideGeneration, Is.EqualTo(manualGeneration));
                Assert.That(rescue.LifeSupportEmergencyPatient, Is.EqualTo(target));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.ReturnToShuttle));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Blocked));
                Assert.That(rescue.ActivityContext.FailureReason,
                    Is.EqualTo(LuaMRescueFailureReason.LifeSupportUnavailable));
                Assert.That(rescue.ActivityContext.Fallback, Is.EqualTo(LuaMRescueActivity.ReturnToShuttle));
                Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget), Is.False);
                Assert.That(rescue.LastAutoCommsKey, Does.StartWith("life-support-emergency:1"));
                Assert.That(rescue.LastShuttleReturnStatus, Does.Contain("no assigned rescue shuttle"));
            });
        });

        await server.WaitAssertion(() =>
        {
            var gas = entities.GetComponent<GasTankComponent>(exhaustedReserve);
            gas.Air.SetMoles(Gas.Oxygen, 1f);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
        });
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var telemetry = dispatchConsole.BuildState().Agents.Single(entry => entry.Agent == entities.GetNetEntity(agent));
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.LifeSupportEmergencyActive, Is.False);
                Assert.That(telemetry.LifeSupportEmergency, Is.False);
                Assert.That(telemetry.OxygenPressureKpa, Is.GreaterThan(30f));
                Assert.That(rescue.DeferredPatientTargets, Does.Not.ContainKey(target));
                Assert.That(rescue.ManualOverrideGeneration, Is.EqualTo(manualGeneration));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(target),
                    "The manual patient must resume after life support is restored.");
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(target));
                Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.FollowingPatient));
                Assert.That(
                    htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var follow,
                        entities) && follow.EntityId == target,
                    Is.True);
                Assert.That(rescue.LastDeferredPatientStatus, Does.Contain("resuming manual override"));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LowTankUsesBreathableAmbientBeforeTouchingReserve()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var atmos = entities.System<AtmosphereSystem>();
        var internals = entities.System<InternalsSystem>();
        var respirator = entities.System<RespiratorSystem>();
        var inventory = entities.System<InventorySystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid activeTank = default;
        EntityUid reserveTank = default;
        float reservePressure = 0f;
        await server.WaitAssertion(() =>
        {
            // Spawn into real space first so stock loadout startup establishes
            // internals before the ambient atmosphere becomes breathable.
            agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            Assert.That(internals.AreInternalsWorking(agent), Is.True);
            activeTank = entities.GetComponent<InternalsComponent>(agent).GasTankEntity!.Value;
            Assert.That(inventory.TryGetSlotEntity(agent, "pocket1", out var reserve), Is.True);
            reserveTank = reserve!.Value;
            reservePressure = entities.GetComponent<GasTankComponent>(reserveTank).Air.Pressure;
            Assert.That(reservePressure, Is.GreaterThan(30f));

            entities.GetComponent<GasTankComponent>(activeTank).Air.Clear();
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = Atmospherics.OxygenMolesStandard;
            moles[(int) Gas.Nitrogen] = Atmospherics.NitrogenMolesStandard;
            atmos.SetMapAtmosphere(map.MapUid, false, new GasMixture(moles, Atmospherics.T20C));

            var mixture = atmos.GetContainingMixture(agent);
            Assert.That(mixture, Is.Not.Null);
            Assert.That(respirator.CanMetabolizeGas(agent, mixture!), Is.True,
                "The fixture must expose breathable ambient air before the life-support tick.");

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
            rescue.AutoAcquireTargets = false;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<InternalsComponent>(agent).GasTankEntity, Is.Null,
                    "A depleted connection must be released when ambient air is breathable.");
                Assert.That(internals.AreInternalsWorking(agent), Is.False);
                Assert.That(respirator.CanMetabolizeInhaledAir(agent), Is.True);
                Assert.That(rescue.LifeSupportEmergencyActive, Is.False);
                Assert.That(rescue.LifeSupportSwapCount, Is.Zero,
                    "Breathable ambient air must be checked before consuming a reserve.");
                Assert.That(entities.GetComponent<GasTankComponent>(reserveTank).Air.Pressure,
                    Is.EqualTo(reservePressure).Within(0.01f));
                Assert.That(rescue.LastLifeSupportStatus, Does.Contain("ambient atmosphere"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LifeSupportSkipsHighPressureIncompatibleTankForBreathableReserve()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var internals = entities.System<InternalsSystem>();
        var respirator = entities.System<RespiratorSystem>();
        var inventory = entities.System<InventorySystem>();
        var hands = entities.System<SharedHandsSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid activeTank = default;
        EntityUid oxygenReserve = default;
        EntityUid incompatibleTank = default;
        await server.WaitAssertion(() =>
        {
            agent = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            Assert.That(internals.AreInternalsWorking(agent), Is.True);
            activeTank = entities.GetComponent<InternalsComponent>(agent).GasTankEntity!.Value;
            Assert.That(inventory.TryGetSlotEntity(agent, "pocket1", out var reserve), Is.True);
            oxygenReserve = reserve!.Value;

            var oxygen = entities.GetComponent<GasTankComponent>(oxygenReserve);
            oxygen.Air.Clear();
            oxygen.Air.SetMoles(Gas.Oxygen, 0.25f);

            incompatibleTank = entities.SpawnEntity("PlasmaTankFilled", map.MapCoords);
            Assert.That(hands.TryPickupAnyHand(agent, incompatibleTank), Is.True);
            var incompatible = entities.GetComponent<GasTankComponent>(incompatibleTank);
            Assert.Multiple(() =>
            {
                Assert.That(respirator.CanMetabolizeGas(agent, oxygen.Air), Is.True);
                Assert.That(respirator.CanMetabolizeGas(agent, incompatible.Air), Is.False);
                Assert.That(incompatible.Air.Pressure, Is.GreaterThan(oxygen.Air.Pressure),
                    "The incompatible hand tank must be the tempting higher-pressure candidate.");
                Assert.That(oxygen.Air.Pressure, Is.GreaterThan(30f));
            });

            entities.GetComponent<GasTankComponent>(activeTank).Air.Clear();
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
            rescue.AutoAcquireTargets = false;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<InternalsComponent>(agent).GasTankEntity,
                    Is.EqualTo(oxygenReserve),
                    "A higher-pressure plasma tank must never outrank breathable oxygen.");
                Assert.That(entities.GetComponent<InternalsComponent>(agent).GasTankEntity,
                    Is.Not.EqualTo(incompatibleTank));
                Assert.That(internals.AreInternalsWorking(agent), Is.True);
                Assert.That(rescue.LifeSupportSwapCount, Is.EqualTo(1));
                Assert.That(rescue.LifeSupportEmergencyActive, Is.False);
                Assert.That(rescue.LastLifeSupportStatus,
                    Does.Contain("automatic oxygen replacement connected"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EscortSwitchesToCompatiblePocketReserveWithoutBecomingAgent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var internals = entities.System<InternalsSystem>();
        var respirator = entities.System<RespiratorSystem>();
        var inventory = entities.System<InventorySystem>();
        var hands = entities.System<SharedHandsSystem>();
        var map = await pair.CreateTestMap();

        EntityUid escort = default;
        EntityUid activeTank = default;
        EntityUid oxygenReserve = default;
        EntityUid incompatibleTank = default;
        await server.WaitAssertion(() =>
        {
            escort = entities.SpawnEntity("LuaMRescueEscort", map.MapCoords);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(escort), Is.False,
                    "Escort life support must not make it visible to patient dispatch as another Aibolit.");
                Assert.That(internals.AreInternalsWorking(escort), Is.True);
            });

            activeTank = entities.GetComponent<InternalsComponent>(escort).GasTankEntity!.Value;
            Assert.That(inventory.TryGetSlotEntity(escort, "pocket1", out var reserve), Is.True);
            oxygenReserve = reserve!.Value;
            var oxygen = entities.GetComponent<GasTankComponent>(oxygenReserve);
            oxygen.Air.Clear();
            oxygen.Air.SetMoles(Gas.Oxygen, 0.25f);

            incompatibleTank = entities.SpawnEntity("PlasmaTankFilled", map.MapCoords);
            Assert.That(hands.TryPickupAnyHand(escort, incompatibleTank), Is.True);
            var incompatible = entities.GetComponent<GasTankComponent>(incompatibleTank);
            Assert.Multiple(() =>
            {
                Assert.That(respirator.CanMetabolizeGas(escort, oxygen.Air), Is.True);
                Assert.That(respirator.CanMetabolizeGas(escort, incompatible.Air), Is.False);
                Assert.That(incompatible.Air.Pressure, Is.GreaterThan(oxygen.Air.Pressure));
                Assert.That(oxygen.Air.Pressure, Is.GreaterThan(30f));
            });

            entities.GetComponent<GasTankComponent>(activeTank).Air.Clear();
            var escortState = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            escortState.LifeSupportCheckAccumulator = escortState.LifeSupportCheckInterval;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var escortState = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<InternalsComponent>(escort).GasTankEntity,
                    Is.EqualTo(oxygenReserve),
                    "Escort must prefer its breathable pocket reserve over higher-pressure plasma.");
                Assert.That(entities.GetComponent<InternalsComponent>(escort).GasTankEntity,
                    Is.Not.EqualTo(incompatibleTank));
                Assert.That(internals.AreInternalsWorking(escort), Is.True);
                Assert.That(escortState.LifeSupportSwapCount, Is.EqualTo(1));
                Assert.That(escortState.LastLifeSupportStatus,
                    Does.Contain("automatic compatible oxygen replacement connected"));

                escortState.LifeSupportCheckAccumulator = escortState.LifeSupportCheckInterval;
            });
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var escortState = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<InternalsComponent>(escort).GasTankEntity,
                    Is.EqualTo(oxygenReserve));
                Assert.That(escortState.LifeSupportSwapCount, Is.EqualTo(1),
                    "A nominal active reserve must not ping-pong back to the depleted primary tank.");
                Assert.That(entities.HasComponent<LuaMRescueAgentComponent>(escort), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OnboardLifeSupportEmergencyKeepsReturnIntentAndResumesManualPatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var inventory = entities.System<InventorySystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid shuttleAnchor = default;
        EntityUid activeTank = default;
        uint manualGeneration = 0;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 5);
            entities.System<GravitySystem>().EnableGravity(
                map.Grid,
                entities.EnsureComponent<GravityComponent>(map.Grid));

            agent = entities.SpawnEntity("LuaMRescueAgent", GridCoordinates(map.Grid, 0.5f, 0.5f));
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 4.5f, 0.5f));
            entities.EnsureComponent<Content.Shared._Shitmed.Body.Components.BreathingImmunityComponent>(patient);
            entities.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(patient);
            var injury = new DamageSpecifier();
            injury.DamageDict.Add("Blunt", 100);
            Assert.That(damageable.TryChangeDamage(patient, injury, ignoreResistances: true), Is.Not.Null);
            Assert.That(mobState.IsCritical(patient), Is.True);

            shuttleAnchor = entities.SpawnEntity(null, GridCoordinates(map.Grid, 0.5f, 0.5f));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AutoReturnShuttle = false;
            rescue.AssignedShuttle = map.Grid;
            rescue.AssignedShuttleAnchor = shuttleAnchor;
            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
            manualGeneration = rescue.ManualOverrideGeneration;
            Assert.That(manualGeneration, Is.Not.Zero);

            activeTank = entities.GetComponent<InternalsComponent>(agent).GasTankEntity!.Value;
            entities.GetComponent<GasTankComponent>(activeTank).Air.Clear();
            Assert.That(inventory.TryGetSlotEntity(agent, "pocket1", out var reserve), Is.True);
            entities.GetComponent<GasTankComponent>(reserve!.Value).Air.Clear();
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var htn = entities.GetComponent<HTNComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<TransformComponent>(agent).GridUid, Is.EqualTo(map.Grid.Owner));
                Assert.That(rescue.LifeSupportEmergencyActive, Is.True);
                Assert.That(rescue.LifeSupportEmergencyPatient, Is.EqualTo(patient));
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.ManualOverrideGeneration, Is.EqualTo(manualGeneration));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.ReturnToShuttle));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo((EntityUid?) map.Grid.Owner));
                Assert.That(
                    rescue.ActivityContext.Destination,
                    Is.EqualTo(entities.GetComponent<TransformComponent>(shuttleAnchor).Coordinates));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.ActivityContext.FailureReason, Is.EqualTo(LuaMRescueFailureReason.None));
                Assert.That(htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget), Is.False,
                    "An onboard emergency must hold movement instead of following the patient off the shuttle.");
                Assert.That(rescue.LastShuttleReturnStatus, Does.Contain("return-route disabled"));
            });
        });

        await server.WaitAssertion(() =>
        {
            entities.GetComponent<GasTankComponent>(activeTank).Air.SetMoles(Gas.Oxygen, 1f);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
        });
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.LifeSupportEmergencyActive, Is.False);
                Assert.That(rescue.ManualOverrideTarget, Is.EqualTo(patient));
                Assert.That(rescue.ManualOverrideGeneration, Is.EqualTo(manualGeneration));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.FollowingPatient));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LifeSupportEmergencyReturnsThroughGatewayAndResumesExactPatient()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var shuttleSystem = entities.System<LuaMRescueShuttleSystem>();
        var gatewaySystem = entities.System<GatewaySystem>();
        var transform = entities.System<SharedTransformSystem>();
        var linked = entities.System<LinkedEntitySystem>();
        var inventory = entities.System<InventorySystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var homeMap = await pair.CreateTestMap();
        var expeditionMap = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid competingPatient = default;
        EntityUid shuttleAnchor = default;
        EntityUid homeGateway = default;
        EntityUid expeditionGateway = default;
        EntityUid invalidExpeditionGateway = default;
        EntityUid activeTank = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, homeMap, 0, 4);
            BuildHorizontalFloor(mapSystem, expeditionMap, 0, 4);
            mapSystem.SetTile(homeMap.Grid, new Vector2i(2, 1), homeMap.Tile.Tile);
            mapSystem.SetTile(expeditionMap.Grid, new Vector2i(4, 1), expeditionMap.Tile.Tile);
            entities.System<GravitySystem>().EnableGravity(
                homeMap.Grid,
                entities.EnsureComponent<GravityComponent>(homeMap.Grid));
            entities.System<GravitySystem>().EnableGravity(
                expeditionMap.Grid,
                entities.EnsureComponent<GravityComponent>(expeditionMap.Grid));

            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                GridCoordinates(expeditionMap.Grid, 4.5f, 0.5f));
            Assert.That(entities.System<InternalsSystem>().AreInternalsWorking(agent), Is.True);
            patient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(expeditionMap.Grid, 2.5f, 0.5f));
            entities.EnsureComponent<Content.Shared._Shitmed.Body.Components.BreathingImmunityComponent>(patient);
            entities.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(patient);
            var injury = new DamageSpecifier();
            injury.DamageDict.Add("Blunt", 100);
            Assert.That(damageable.TryChangeDamage(patient, injury, ignoreResistances: true), Is.Not.Null);
            Assert.That(mobState.IsCritical(patient), Is.True,
                "The automatic recovery fixture needs a physically critical patient that will not self-normalize.");

            competingPatient = entities.SpawnEntity(
                "MobHuman",
                GridCoordinates(homeMap.Grid, 1.5f, 0.5f));
            entities.EnsureComponent<Content.Shared._Shitmed.Body.Components.BreathingImmunityComponent>(competingPatient);
            entities.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(competingPatient);
            var competingInjury = new DamageSpecifier();
            competingInjury.DamageDict.Add("Blunt", 100);
            Assert.That(
                damageable.TryChangeDamage(competingPatient, competingInjury, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(competingPatient), Is.True);

            homeGateway = entities.SpawnEntity("Gateway", GridCoordinates(homeMap.Grid, 3.5f, 0.5f));
            expeditionGateway = entities.SpawnEntity(
                "Gateway",
                GridCoordinates(expeditionMap.Grid, 0.5f, 0.5f));
            gatewaySystem.SetEnabled(homeGateway, true);
            gatewaySystem.SetEnabled(expeditionGateway, true);
            var homePortal = entities.EnsureComponent<PortalComponent>(homeGateway);
            var expeditionPortal = entities.EnsureComponent<PortalComponent>(expeditionGateway);
            homePortal.CanTeleportToOtherMaps = true;
            homePortal.RandomTeleport = false;
            expeditionPortal.CanTeleportToOtherMaps = true;
            expeditionPortal.RandomTeleport = false;
            Assert.That(linked.TryLink(homeGateway, expeditionGateway), Is.True);

            // This endpoint is closer to the expedition spawn, but its linked
            // destination is disabled. Neither Aibolit nor an escort may select
            // it as a reciprocal return route.
            invalidExpeditionGateway = entities.SpawnEntity(
                "Gateway",
                GridCoordinates(expeditionMap.Grid, 4.5f, 1.5f));
            var invalidHomeGateway = entities.SpawnEntity(
                "Gateway",
                GridCoordinates(homeMap.Grid, 2.5f, 1.5f));
            gatewaySystem.SetEnabled(invalidExpeditionGateway, true);
            gatewaySystem.SetEnabled(invalidHomeGateway, false);
            var invalidExpeditionPortal = entities.EnsureComponent<PortalComponent>(invalidExpeditionGateway);
            var invalidHomePortal = entities.EnsureComponent<PortalComponent>(invalidHomeGateway);
            invalidExpeditionPortal.CanTeleportToOtherMaps = true;
            invalidExpeditionPortal.RandomTeleport = false;
            invalidHomePortal.CanTeleportToOtherMaps = true;
            invalidHomePortal.RandomTeleport = false;
            Assert.That(linked.TryLink(invalidExpeditionGateway, invalidHomeGateway), Is.True);

            shuttleAnchor = entities.SpawnEntity(null, GridCoordinates(homeMap.Grid, 0.5f, 0.5f));
            entities.EnsureComponent<LuaMRescueTeamComponent>(agent);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = false;
            rescue.EvacuateTargetsToShuttle = false;
            rescue.AutoAnalyzeBeforeTreatment = false;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoDefibDeadPatients = false;
            rescue.AssignedShuttle = homeMap.Grid;
            rescue.AssignedShuttleAnchor = shuttleAnchor;
            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient), status);
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient), rescue.LastTaskStatus);
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient), rescue.LastTargetTrackingStatus);
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active),
                    rescue.LastIntentCancellationStatus);
            });
            // TryOrderAgent intentionally establishes manual ownership. Clear only
            // that marker so this fixture exercises automatic patient deferral.
            rescue.ManualOverrideTarget = null;
            // This older same-urgency candidate would win the generic deferred
            // selector. Recovery must nevertheless resume the exact interrupted
            // mission first.
            rescue.DeferredPatientTargets[competingPatient] = TimeSpan.Zero;

            activeTank = entities.GetComponent<InternalsComponent>(agent).GasTankEntity!.Value;
            entities.GetComponent<GasTankComponent>(activeTank).Air.Clear();
            Assert.That(inventory.TryGetSlotEntity(agent, "pocket1", out var reserve), Is.True);
            entities.GetComponent<GasTankComponent>(reserve!.Value).Air.Clear();
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
        });

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.LifeSupportEmergencyActive, Is.True);
                Assert.That(rescue.LifeSupportEmergencyPatient, Is.EqualTo(patient),
                    $"Emergency activation lost the automatic patient: assigned={rescue.AssignedTarget}; " +
                    $"task={rescue.TaskPatientTarget}; manual={rescue.ManualOverrideTarget}; " +
                    $"activity={rescue.ActivityContext.Activity}/{rescue.ActivityContext.Target}/" +
                    $"{rescue.ActivityContext.TerminalStatus}");
                Assert.That(rescue.DeferredPatientTargets, Does.ContainKey(patient));
            });
        });
        await server.WaitAssertion(() =>
        {
            entities.EnsureComponent<ActorComponent>(patient);
            Assert.That(
                shuttleSystem.TryQueueAutomaticMedicalSignal(
                    patient,
                    LuaMRescueMedicalSignalKind.Critical,
                    out var repeatedStatus),
                Is.True,
                repeatedStatus);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(repeatedStatus, Does.Contain("remains reserved"));
                Assert.That(rescue.LifeSupportEmergencyPatient, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.ReturnToShuttle));
                Assert.That(rescue.ActivityContext.Target, Is.Not.EqualTo(patient),
                    "A repeated signal must not replace emergency navigation with a patient intent.");
            });
            entities.RemoveComponent<ActorComponent>(patient);
        });
        var initialDistance = float.PositiveInfinity;
        await server.WaitAssertion(() =>
        {
            initialDistance = Vector2.Distance(
                transform.GetWorldPosition(agent),
                transform.GetWorldPosition(expeditionGateway));
        });

        var gatewayGoalSeen = false;
        var crossedHome = false;
        var minimumGatewayDistance = initialDistance;
        for (var i = 0; i < ProbeTickLimit * 4; i += 4)
        {
            await pair.RunTicksSync(4);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var htn = entities.GetComponent<HTNComponent>(agent);
                gatewayGoalSeen |= rescue.LifeSupportEmergencyActive &&
                                   htn.Blackboard.TryGetValue<EntityCoordinates>(
                                       NPCBlackboard.FollowTarget,
                                       out var follow,
                                       entities) &&
                                   follow.EntityId == expeditionGateway;
                crossedHome |= entities.GetComponent<TransformComponent>(agent).MapID ==
                               entities.GetComponent<TransformComponent>(shuttleAnchor).MapID;
                // Controlled transfer can complete between two four-tick samples.
                // Physical arrival on the reciprocal endpoint is authoritative
                // proof that the gateway route was owned during that interval.
                gatewayGoalSeen |= crossedHome;
                if (!crossedHome)
                {
                    minimumGatewayDistance = Math.Min(
                        minimumGatewayDistance,
                        Vector2.Distance(
                            transform.GetWorldPosition(agent),
                            transform.GetWorldPosition(expeditionGateway)));
                }
            });

            if (crossedHome)
                break;
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var team = entities.GetComponent<LuaMRescueTeamComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(gatewayGoalSeen, Is.True,
                    $"Emergency return never owned the expedition gateway: {rescue.LastShuttleReturnStatus}");
                Assert.That(minimumGatewayDistance, Is.LessThan(initialDistance),
                    "The authoritative emergency state must produce physical movement toward the gateway.");
                Assert.That(crossedHome, Is.True,
                    $"Aibolit never crossed the reciprocal gateway home: {rescue.LastShuttleReturnStatus}");
                Assert.That(rescue.LifeSupportEmergencyActive, Is.True);
                Assert.That(rescue.LifeSupportEmergencyPatient, Is.EqualTo(patient));
                Assert.That(rescue.DeferredPatientTargets, Does.ContainKey(patient));
                Assert.That(rescue.AssignedTarget, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.Not.EqualTo((EntityUid?) homeMap.Grid),
                    "Returning activity targets must never become patient ownership mirrors.");
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.ReturnToShuttle));
                Assert.That(team.Patient, Is.EqualTo(patient));
                Assert.That(team.Phase, Is.EqualTo(LuaMRescueTeamPhase.ReturnOrExtract));
                Assert.That(team.SortiePlan, Is.EqualTo(LuaMRescueSortiePlan.ReturnToShuttle));
                Assert.That(team.LastReturnOrExtractReasonStatus, Does.StartWith("reason=life-support"));
            });
        });

        EntityUid escort = default;
        var escortInitialDistance = float.PositiveInfinity;
        await server.WaitAssertion(() =>
        {
            escort = entities.SpawnEntity(
                "LuaMRescueEscort",
                GridCoordinates(expeditionMap.Grid, 4.5f, 0.5f));
            var team = entities.GetComponent<LuaMRescueTeamComponent>(agent);
            team.TeamId = 9001;
            team.Leader = agent;
            team.Patient = patient;
            team.Shuttle = homeMap.Grid;
            team.ShuttleAnchor = shuttleAnchor;
            team.Escorts.Add(escort);

            var escortState = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            escortState.TeamId = team.TeamId;
            escortState.Leader = agent;
            escortState.Patient = patient;
            escortState.Shuttle = homeMap.Grid;
            escortState.ShuttleAnchor = shuttleAnchor;
            escortState.DutyRefreshInterval = 0.01f;
            escortState.DutyRefreshAccumulator = escortState.DutyRefreshInterval;
            escortState.NextDutyActionAt = TimeSpan.MaxValue;
            escortState.NextSpeechTime = TimeSpan.MaxValue;
            escortInitialDistance = Vector2.Distance(
                transform.GetWorldPosition(escort),
                transform.GetWorldPosition(expeditionGateway));
            Assert.That(
                Vector2.Distance(
                    transform.GetWorldPosition(escort),
                    transform.GetWorldPosition(invalidExpeditionGateway)),
                Is.LessThan(escortInitialDistance));
        });

        var escortGatewayGoalSeen = false;
        var escortPortalRangesSeen = false;
        var escortCrossedHome = false;
        var escortMinimumDistance = escortInitialDistance;
        for (var i = 0; i < ProbeTickLimit * 4; i += 2)
        {
            await pair.RunTicksSync(2);
            await server.WaitAssertion(() =>
            {
                var escortState = entities.GetComponent<LuaMRescueEscortComponent>(escort);
                var escortHtn = entities.GetComponent<HTNComponent>(escort);
                var followsValidGateway =
                    escortState.CurrentFollowTarget == expeditionGateway &&
                    escortHtn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var follow,
                        entities) &&
                    follow.EntityId == expeditionGateway;
                escortGatewayGoalSeen |= escortState.CurrentDuty == LuaMRescueEscortDuty.ReturnToShuttle &&
                                         followsValidGateway;
                escortPortalRangesSeen |= followsValidGateway &&
                                          escortHtn.Blackboard.TryGetValue<float>(
                                              "FollowRange",
                                              out var followRange,
                                              entities) &&
                                          escortHtn.Blackboard.TryGetValue<float>(
                                              "FollowCloseRange",
                                              out var closeRange,
                                              entities) &&
                                          Math.Abs(followRange - 0.1f) < 0.001f &&
                                          Math.Abs(closeRange - 0.1f) < 0.001f;
                escortCrossedHome |= entities.GetComponent<TransformComponent>(escort).MapID ==
                                     entities.GetComponent<TransformComponent>(shuttleAnchor).MapID;
                if (!escortCrossedHome)
                {
                    escortMinimumDistance = Math.Min(
                        escortMinimumDistance,
                        Vector2.Distance(
                            transform.GetWorldPosition(escort),
                            transform.GetWorldPosition(expeditionGateway)));
                }
            });

            if (escortCrossedHome)
                break;
        }

        await server.WaitAssertion(() =>
        {
            var escortState = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            var escortXform = entities.GetComponent<TransformComponent>(escort);
            var leaderXform = entities.GetComponent<TransformComponent>(agent);
            var homeXform = entities.GetComponent<TransformComponent>(shuttleAnchor);
            var carrier = entities.GetComponent<LuaMRescueActivityCarrierComponent>(escort);
            var timeoutStatus = entities.TryGetComponent<PortalTimeoutComponent>(escort, out var timeout)
                ? $"portal-timeout={timeout.EnteredPortal}"
                : "portal-timeout=none";
            Assert.Multiple(() =>
            {
                Assert.That(escortGatewayGoalSeen, Is.True,
                    $"Escort never selected the valid reciprocal gateway: {escortState.LastDutyStatus}");
                Assert.That(escortPortalRangesSeen, Is.True,
                    "Gateway traversal must use a near-zero range so collision actually enters the portal.");
                Assert.That(escortMinimumDistance, Is.LessThan(escortInitialDistance),
                    "Escort ReturnToShuttle duty must produce physical movement toward the gateway.");
                Assert.That(escortCrossedHome, Is.True,
                    $"Escort never crossed home with Aibolit: {escortState.LastDutyStatus}; " +
                    $"action={escortState.LastDutyActionStatus}; initial={escortInitialDistance:0.000}; " +
                    $"minimum={escortMinimumDistance:0.000}; escort-map={escortXform.MapID}; " +
                    $"leader-map={leaderXform.MapID}; home-map={homeXform.MapID}; " +
                    $"escort-pos={escortXform.MapPosition.Position}; leader-pos={leaderXform.MapPosition.Position}; " +
                    $"carrier={carrier.ActivityContext.Activity}/{carrier.ActivityContext.TerminalStatus}/" +
                    $"{carrier.ActivityContext.FailureReason}; {timeoutStatus}");
                Assert.That(escortState.CurrentDuty, Is.EqualTo(LuaMRescueEscortDuty.ReturnToShuttle));
            });
        });

        await server.WaitAssertion(() =>
        {
            entities.GetComponent<GasTankComponent>(activeTank).Air.SetMoles(Gas.Oxygen, 1f);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.LifeSupportCheckAccumulator = rescue.LifeSupportCheckInterval;
        });
        await pair.RunTicksSync(2);
        var crossedBackToPatient = false;
        var resumedPatientGoal = false;
        for (var i = 0; i < ProbeTickLimit * 4; i += 4)
        {
            await pair.RunTicksSync(4);
            await server.WaitAssertion(() =>
            {
                var htn = entities.GetComponent<HTNComponent>(agent);
                crossedBackToPatient |= entities.GetComponent<TransformComponent>(agent).MapID ==
                                        entities.GetComponent<TransformComponent>(patient).MapID;
                resumedPatientGoal |= crossedBackToPatient &&
                                      htn.Blackboard.TryGetValue<EntityCoordinates>(
                                          NPCBlackboard.FollowTarget,
                                          out var follow,
                                          entities) &&
                                      follow.EntityId == patient;
            });

            if (resumedPatientGoal)
                break;
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.LifeSupportEmergencyActive, Is.False);
                Assert.That(rescue.LifeSupportEmergencyPatient, Is.Null);
                Assert.That(rescue.DeferredPatientTargets, Does.Not.ContainKey(patient));
                Assert.That(rescue.DeferredPatientTargets, Does.ContainKey(competingPatient),
                    "The older competing candidate must remain queued until the exact interrupted patient resumes.");
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(rescue.TaskStage, Is.EqualTo(LuaMRescueTaskStage.FollowingPatient));
                Assert.That(rescue.LastDeferredPatientStatus, Does.Contain("resuming deferred"));
                Assert.That(crossedBackToPatient, Is.True,
                    "The restored patient mission must route back through the home gateway.");
                Assert.That(resumedPatientGoal, Is.True,
                    "After crossing back, the exact interrupted patient must regain the movement goal.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DifferentMapAndDifferentGridAreRejectedBeforePathfinding()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var firstMap = await pair.CreateTestMap();
        var secondMap = await pair.CreateTestMap();

        LuaMRescuePathProbeSnapshot differentMap = default;
        LuaMRescuePathProbeSnapshot differentGrid = default;
        await server.WaitAssertion(() =>
        {
            var secondGrid = server.MapMan.CreateGridEntity(firstMap.MapId);
            transform.SetLocalPosition(secondGrid, new Vector2(1.1f, 0f));
            mapSystem.SetTile(secondGrid, Vector2i.Zero, firstMap.Tile.Tile);

            var agent = entities.SpawnEntity("MobHuman", GridCoordinates(firstMap.Grid, 0.5f, 0.5f));
            var otherMapTarget = entities.SpawnEntity(null, GridCoordinates(secondMap.Grid, 0.5f, 0.5f));
            var otherGridTarget = entities.SpawnEntity(null, GridCoordinates(secondGrid, 0.5f, 0.5f));

            Assert.That(
                entities.GetComponent<TransformComponent>(otherGridTarget).GridUid,
                Is.Not.EqualTo(entities.GetComponent<TransformComponent>(agent).GridUid),
                "The close-range fixture endpoints must remain parented to distinct grids.");

            differentMap = navigation.ProbeRoute(agent, otherMapTarget, RescueActionRange);
            differentGrid = navigation.ProbeRoute(agent, otherGridTarget, RescueActionRange);
        });

        Assert.Multiple(() =>
        {
            Assert.That(differentMap.State, Is.EqualTo(LuaMRescuePathProbeState.DifferentGrid));
            Assert.That(differentMap.Distance, Is.EqualTo(float.PositiveInfinity));
            Assert.That(differentGrid.State, Is.EqualTo(LuaMRescuePathProbeState.DifferentGrid));
            Assert.That(differentGrid.Distance, Is.LessThan(RescueActionRange),
                "Separate undocked grids must be rejected even inside direct action range.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PatientInsideActionRangeBehindWallHasNoLineOfSight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        LuaMRescuePathProbeSnapshot initial = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 2);
            entities.SpawnEntity("WallSolid", GridCoordinates(map.Grid, 1.5f, 0.5f));

            // Both actors stand immediately on opposite sides of the wall. Their
            // centres are ~1 m apart, comfortably inside the 1.5 m action range.
            agent = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 0.99f, 0.5f));
            patient = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 2.01f, 0.5f));
            initial = navigation.ProbeRoute(agent, patient, RescueActionRange);
        });

        Assert.Multiple(() =>
        {
            Assert.That(initial.Distance, Is.EqualTo(1.02f).Within(0.01f));
            Assert.That(initial.Distance, Is.LessThan(RescueActionRange));
            Assert.That(initial.State, Is.EqualTo(LuaMRescuePathProbeState.Pending));
            Assert.That(initial.RequiresCloserApproach, Is.True,
                "A close obstruction must start a real path query instead of terminalizing by distance alone.");
        });

        var result = await AwaitCompletedProbe(pair, navigation, agent, patient, RescueActionRange);

        Assert.Multiple(() =>
        {
            Assert.That(result.Distance, Is.EqualTo(1.02f).Within(0.01f));
            Assert.That(result.Distance, Is.LessThan(RescueActionRange));
            Assert.That(result.State, Is.EqualTo(LuaMRescuePathProbeState.NoLineOfSight));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SolidWallAcrossSingleTileCorridorCompletesAsNoPath()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid target = default;
        LuaMRescuePathProbeSnapshot initial = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 23);
            entities.SpawnEntity("WallSolid", GridCoordinates(map.Grid, 12.5f, 0.5f));
            agent = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 0.5f, 0.5f));
            target = entities.SpawnEntity(null, GridCoordinates(map.Grid, 23.5f, 0.5f));
            initial = navigation.ProbeRoute(agent, target, RescueActionRange);
        });

        Assert.That(initial.State, Is.EqualTo(LuaMRescuePathProbeState.Pending));
        var completed = await AwaitCompletedProbe(pair, navigation, agent, target, RescueActionRange);
        Assert.That(completed.State, Is.EqualTo(LuaMRescuePathProbeState.NoPath));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FailedRouteQueriesUseExponentialBackoff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid target = default;
        await server.WaitAssertion(() =>
        {
            BuildHorizontalFloor(mapSystem, map, 0, 23);
            entities.SpawnEntity("WallSolid", GridCoordinates(map.Grid, 12.5f, 0.5f));
            agent = entities.SpawnEntity("MobHuman", GridCoordinates(map.Grid, 0.5f, 0.5f));
            target = entities.SpawnEntity(null, GridCoordinates(map.Grid, 23.5f, 0.5f));
        });

        var firstFailure = await AwaitCompletedProbe(pair, navigation, agent, target, RescueActionRange);
        Assert.Multiple(() =>
        {
            Assert.That(firstFailure.State, Is.EqualTo(LuaMRescuePathProbeState.NoPath));
            Assert.That(firstFailure.AttemptCount, Is.EqualTo(1));
        });

        // Repeated selection checks during the first one-second backoff reuse the
        // terminal snapshot instead of starting another asynchronous path query.
        for (var i = 0; i < 8; i++)
        {
            await pair.RunTicksSync(Math.Max(1, server.Timing.TickRate / 16));
            await server.WaitAssertion(() =>
            {
                var cached = navigation.ProbeRoute(agent, target, RescueActionRange);
                Assert.That(cached.State, Is.EqualTo(LuaMRescuePathProbeState.NoPath));
                Assert.That(cached.AttemptCount, Is.EqualTo(1));
            });
        }

        // Once the first backoff expires the next query is identified as attempt 2.
        await pair.RunTicksSync(server.Timing.TickRate);
        LuaMRescuePathProbeSnapshot secondPending = default;
        await server.WaitAssertion(() =>
        {
            secondPending = navigation.ProbeRoute(agent, target, RescueActionRange);
        });
        Assert.Multiple(() =>
        {
            Assert.That(secondPending.State, Is.EqualTo(LuaMRescuePathProbeState.Pending));
            Assert.That(secondPending.AttemptCount, Is.EqualTo(2));
        });

        var secondFailure = await AwaitCompletedProbe(pair, navigation, agent, target, RescueActionRange);
        Assert.Multiple(() =>
        {
            Assert.That(secondFailure.State, Is.EqualTo(LuaMRescuePathProbeState.NoPath));
            Assert.That(secondFailure.AttemptCount, Is.EqualTo(2));
        });

        // Attempt 2 has a two-second backoff, so one second still returns the same
        // failure and cannot churn a third path request.
        await pair.RunTicksSync(server.Timing.TickRate);
        await server.WaitAssertion(() =>
        {
            var stillBackedOff = navigation.ProbeRoute(agent, target, RescueActionRange);
            Assert.That(stillBackedOff.State, Is.EqualTo(LuaMRescuePathProbeState.NoPath));
            Assert.That(stillBackedOff.AttemptCount, Is.EqualTo(2));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ActivityDeadlineExpiresThroughServerTicksOnlyOnce()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        LuaMRescueActivitySnapshot started = default;
        await server.WaitAssertion(() =>
        {
            agent = entities.SpawnEntity(null, map.GridCoords);
            var rescue = entities.AddComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(
                rescue.ActivityRoleProfile.TryGetPolicy(LuaMRescueActivity.Standby, out var policy),
                Is.True);
            policy.Timeout = TimeSpan.FromTicks(server.Timing.TickPeriod.Ticks * 2);

            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.Standby,
                    target: null,
                    destination: null,
                    out started),
                Is.True);
            Assert.That(started.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            Assert.That(started.Deadline, Is.GreaterThan(server.Timing.CurTime));
        });

        await pair.RunTicksSync(4);

        LuaMRescueActivitySnapshot expired = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(coordinator.GetSnapshot(agent, out expired), Is.True);
            Assert.That(server.Timing.CurTime, Is.GreaterThanOrEqualTo(expired.Deadline));
        });

        Assert.Multiple(() =>
        {
            Assert.That(expired.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
            Assert.That(expired.FailureReason, Is.EqualTo(LuaMRescueFailureReason.DeadlineExceeded));
            Assert.That(expired.Fallback, Is.EqualTo(LuaMRescueActivity.Standby));
            Assert.That(expired.Generation, Is.EqualTo(started.Generation));
        });

        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(coordinator.GetSnapshot(agent, out var afterMoreTicks), Is.True);
            Assert.That(afterMoreTicks, Is.EqualTo(expired));
        });

        await pair.CleanReturnAsync();
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

    private static async Task<LuaMRescuePathProbeSnapshot> AwaitCompletedProbe(
        TestPair pair,
        LuaMRescueNavigationSystem navigation,
        EntityUid agent,
        EntityUid target,
        float actionRange)
    {
        for (var i = 0; i < ProbeTickLimit; i++)
        {
            LuaMRescuePathProbeSnapshot snapshot = default;
            await pair.Server.WaitAssertion(() =>
            {
                snapshot = navigation.ProbeRoute(agent, target, actionRange);
            });

            if (snapshot.State != LuaMRescuePathProbeState.Pending)
                return snapshot;

            await pair.RunTicksSync(1);
        }

        Assert.Fail($"Route probe remained Pending for {ProbeTickLimit} server ticks.");
        return default;
    }

    private static async Task<(bool Started, string Diagnostics)> AwaitSteeringTarget(
        TestPair pair,
        IEntityManager entities,
        EntityUid agent,
        EntityUid target)
    {
        var diagnostics = "no planner observation";
        for (var i = 0; i < ProbeTickLimit; i++)
        {
            var matches = false;
            await pair.Server.WaitAssertion(() =>
            {
                matches = entities.TryGetComponent<NPCSteeringComponent>(agent, out var steering) &&
                          steering.Coordinates.EntityId == target;

                var htn = entities.GetComponent<HTNComponent>(agent);
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var follow = htn.Blackboard.TryGetValue<EntityCoordinates>(
                    NPCBlackboard.FollowTarget,
                    out var followCoordinates,
                    entities)
                    ? followCoordinates.EntityId.ToString()
                    : "none";
                diagnostics =
                    $"tick={i}; activeNpc={entities.HasComponent<ActiveNPCComponent>(agent)}; " +
                    $"pauseWithoutPlayers={htn.PauseWhenNoPlayersInRange}; plan={htn.Plan?.CurrentOperator?.GetType().Name ?? "none"}; " +
                    $"planningJob={htn.PlanningJob != null}; planningToken={htn.PlanningToken != null}; " +
                    $"followTarget={follow}; assigned={rescue.AssignedTarget}; " +
                    $"activity={rescue.ActivityContext.Activity}@g{rescue.ActivityContext.Generation}; " +
                    $"terminal={rescue.ActivityContext.TerminalStatus}; route={rescue.ActivityContext.RouteStatus}; " +
                    $"tracking={rescue.LastTargetTrackingStatus}";
            });

            if (matches)
                return (true, diagnostics);

            await pair.RunTicksSync(1);
        }

        return (false, diagnostics);
    }
}
