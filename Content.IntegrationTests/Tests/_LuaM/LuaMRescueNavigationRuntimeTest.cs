using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.NPC;
using Content.Server._LuaM.Rescue;
using Content.Server.Gravity;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Gravity;
using Content.Shared.NPC;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueNavigationSystem))]
public sealed class LuaMRescueNavigationRuntimeTest
{
    private const float RescueActionRange = 1.5f;
    private const int ProbeTickLimit = 180;

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
