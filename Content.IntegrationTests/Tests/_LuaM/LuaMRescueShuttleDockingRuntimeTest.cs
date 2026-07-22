using System.Collections.Generic;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server._LuaM.Rescue;
using Content.Server._Mono.NPC.HTN;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueShuttleSystem))]
public sealed class LuaMRescueShuttleDockingRuntimeTest
{
    private const int AsyncTickLimit = 240;
    private const int PostDockingAsyncTickLimit = 600;

    [Test]
    public async Task ArrivalRangeIsNotSuccessAndLifecyclePerformsConfirmedSafeDocking()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var agents = entities.System<LuaMRescueAgentSystem>();
        var docking = entities.System<DockingSystem>();
        var damageable = entities.System<DamageableSystem>();
        var mobState = entities.System<MobStateSystem>();
        var power = entities.System<PowerReceiverSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        EntityUid targetGrid = default;
        EntityUid shuttleDock = default;
        EntityUid targetDock = default;
        EntityUid agent = default;
        EntityUid patient = default;

        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(map.Grid);

            var shuttleGrid = mapManager.CreateGridEntity(map.MapId);
            var stationGrid = mapManager.CreateGridEntity(map.MapId);
            shuttle = shuttleGrid.Owner;
            targetGrid = stationGrid.Owner;
            transform.SetLocalPosition(targetGrid, new Vector2(20f, 0f));

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
                targetGrid,
                stationGrid.Comp,
                new List<(Vector2i Index, Tile Tile)>
                {
                    new(new Vector2i(0, 0), new Tile(1)),
                    new(new Vector2i(0, 1), new Tile(1)),
                    new(new Vector2i(0, 2), new Tile(1)),
                    new(new Vector2i(-1, 2), new Tile(1)),
                    new(new Vector2i(1, 2), new Tile(1)),
                });

            shuttleDock = entities.SpawnEntity(
                "AirlockShuttle",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)));
            targetDock = entities.SpawnEntity(
                "AirlockShuttle",
                new EntityCoordinates(targetGrid, new Vector2(0.5f, 0.5f)));
            foreach (var dock in new[] { shuttleDock, targetDock })
            {
                // The synthetic grids have no APC. Keep the real shuttle
                // airlocks, but make them self-powered so DockingSystem can
                // perform its normal open-and-bolt safe-exit sequence.
                var receiver = entities.GetComponent<ApcPowerReceiverComponent>(dock);
                power.SetNeedsPower(dock, false, receiver);
                receiver.Powered = true;
            }
            agent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 1.5f)));
            patient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(targetGrid, new Vector2(0.5f, 1.5f)));
            entities.RemoveComponent<BarotraumaComponent>(agent);
            entities.RemoveComponent<BarotraumaComponent>(patient);
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 100);
            Assert.That(
                damageable.TryChangeDamage(patient, damage, ignoreResistances: true),
                Is.Not.Null);
            Assert.That(mobState.IsCritical(patient), Is.True,
                "The docking fixture patient must be physically critical, not only state-forced.");

            var agentComponent = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            agentComponent.AssignedShuttle = shuttle;
            agentComponent.AutoAcquireTargets = true;
            agentComponent.TargetRefreshInterval = 0.05f;
            agentComponent.EvacuateTargetsToShuttle = false;
            agentComponent.AutoAnalyzeBeforeTreatment = false;
            agentComponent.AutoTreatWithCarriedItems = false;
            agentComponent.AutoDefibDeadPatients = false;
            agentComponent.AutoRouteShuttleToTargets = false;
            agentComponent.AutoReturnShuttle = false;

            Assert.That(docking.GetDockingConfig(shuttle, targetGrid), Is.Not.Null);

            var lifecycle = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Routing;
            lifecycle.Target = patient;
            lifecycle.RouteStartedAt = timing.CurTime;
            lifecycle.RouteDeadline = timing.CurTime + TimeSpan.FromSeconds(10);
            lifecycle.StateChangedAt = timing.CurTime;
            lifecycle.ArrivalRange = 25f;
            lifecycle.RouteGeneration = 1;
            lifecycle.DockingRetryDelaySeconds = 0.1f;
            lifecycle.MaxDockingAttempts = 2;

            Assert.That(agents.TryOrderAgent(agent, patient, out var orderStatus), Is.True, orderStatus);
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var before), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(before.State, Is.EqualTo(LuaMRescueShuttleRouteState.Routing));
                Assert.That(before.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(before.RouteActivity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(before.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(before.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(before.ActivityGeneration, Is.EqualTo((uint) before.RouteGeneration));
                Assert.That(before.SafeExitConfirmed, Is.False);
                Assert.That(entities.GetComponent<DockingComponent>(shuttleDock).DockedWith, Is.Null);
                Assert.That(entities.GetComponent<DockingComponent>(targetDock).DockedWith, Is.Null);
                Assert.That(entities.GetComponent<TransformComponent>(agent).GridUid, Is.EqualTo(shuttle));
                Assert.That(HasMovementIntentFor(entities, agent, patient), Is.False,
                    "Aibolit must not receive a cross-grid patient MoveTo while the shuttle is only Routing.");
            });
        });

        // The first lifecycle pass only acknowledges proximity. In particular,
        // it must not release the onboard medic towards a different nav graph.
        var reachedArrivalGate = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var route), Is.True);
                reachedArrivalGate = route.State == LuaMRescueShuttleRouteState.Arrived;
            });

            if (reachedArrivalGate)
                break;

            await pair.RunTicksSync(1);
        }

        Assert.That(reachedArrivalGate, Is.True,
            "The shuttle lifecycle never reached the explicit Arrived gate.");

        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var arrived), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(arrived.State, Is.EqualTo(LuaMRescueShuttleRouteState.Arrived));
                Assert.That(arrived.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(arrived.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(arrived.ActivityGeneration, Is.EqualTo((uint) arrived.RouteGeneration));
                Assert.That(arrived.SafeExitConfirmed, Is.False);
                Assert.That(entities.GetComponent<DockingComponent>(shuttleDock).DockedWith, Is.Null);
                Assert.That(entities.GetComponent<DockingComponent>(targetDock).DockedWith, Is.Null);
                Assert.That(entities.GetComponent<TransformComponent>(agent).GridUid, Is.EqualTo(shuttle));
                Assert.That(HasMovementIntentFor(entities, agent, patient), Is.False,
                    "Arrival radius alone must not publish FollowTarget or steering across grids.");
            });
        });

        // A later pass must obtain a valid dock configuration and receive
        // reciprocal confirmation before cross-grid navigation is released.
        await pair.RunSeconds(0.7f);

        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var after), Is.True);
            var shuttleDocking = entities.GetComponent<DockingComponent>(shuttleDock);
            var targetDocking = entities.GetComponent<DockingComponent>(targetDock);

            Assert.Multiple(() =>
            {
                Assert.That(after.State, Is.EqualTo(LuaMRescueShuttleRouteState.Docked));
                Assert.That(after.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(after.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(after.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Succeeded));
                Assert.That(after.ActivityGeneration, Is.EqualTo((uint) after.RouteGeneration));
                Assert.That(after.SafeExitConfirmed, Is.True);
                Assert.That(after.DockingAttemptCount, Is.Zero);
                Assert.That(after.LastStatus, Does.Contain("safe exit confirmed"));
                Assert.That(shuttleDocking.DockedWith, Is.EqualTo(targetDock));
                Assert.That(targetDocking.DockedWith, Is.EqualTo(shuttleDock));
                Assert.That(shuttleDocking.PathfindHandle, Is.GreaterThanOrEqualTo(0));
                Assert.That(targetDocking.PathfindHandle, Is.EqualTo(shuttleDocking.PathfindHandle));
            });
        });

        var releasedAfterDocking = false;
        for (var i = 0; i < PostDockingAsyncTickLimit; i++)
        {
            await server.WaitAssertion(() =>
            {
                releasedAfterDocking = HasMovementIntentFor(entities, agent, patient) ||
                                       entities.GetComponent<TransformComponent>(agent).GridUid == targetGrid;
            });

            if (releasedAfterDocking)
                break;

            await pair.RunTicksSync(1);
            // Route probes complete asynchronously. Under a loaded grouped run,
            // advancing virtual time alone can consume the whole tick budget
            // before the pathfinding continuation gets a scheduler turn.
            await Task.Delay(5);
        }

        Assert.That(releasedAfterDocking, Is.True,
            "Aibolit never resumed the assigned patient approach after Docked and SafeExitConfirmed.");

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingCompatibleDockHasBoundedTerminalFailure()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(map.Grid);

            var shuttleGrid = mapManager.CreateGridEntity(map.MapId);
            var targetGrid = mapManager.CreateGridEntity(map.MapId);
            shuttle = shuttleGrid.Owner;
            transform.SetLocalPosition(targetGrid.Owner, new Vector2(5f, 0f));
            mapSystem.SetTile(shuttleGrid.Owner, shuttleGrid.Comp, Vector2i.Zero, new Tile(1));
            mapSystem.SetTile(targetGrid.Owner, targetGrid.Comp, Vector2i.Zero, new Tile(1));

            var lifecycle = entities.EnsureComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.State = LuaMRescueShuttleRouteState.Arrived;
            lifecycle.Target = targetGrid.Owner;
            lifecycle.RouteStartedAt = timing.CurTime;
            lifecycle.RouteDeadline = timing.CurTime + TimeSpan.FromSeconds(10);
            lifecycle.StateChangedAt = timing.CurTime;
            lifecycle.ArrivalRange = 10f;
            lifecycle.DockingRetryDelaySeconds = 0.1f;
            lifecycle.MaxDockingAttempts = 2;
            lifecycle.MaxRetries = 0;
        });

        await pair.RunSeconds(1.2f);

        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var failed), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(failed.State, Is.EqualTo(LuaMRescueShuttleRouteState.Failed));
                Assert.That(failed.Terminal, Is.True);
                Assert.That(failed.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(failed.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
                Assert.That(failed.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(failed.ActivityFailureReason, Is.EqualTo(LuaMRescueFailureReason.ShuttleRouteFailed));
                Assert.That(failed.DockingAttemptCount, Is.EqualTo(2));
                Assert.That(failed.SafeExitConfirmed, Is.False);
                Assert.That(failed.LastStatus, Does.Contain("safe docking failed after 2/2 attempts"));
                Assert.That(failed.LastStatus, Does.Contain("no compatible free airlock"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NewRouteSafelyUndocksInitiallyDockedShuttleBeforeWakingAutopilot()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var docking = entities.System<DockingSystem>();
        var shuttleSystem = entities.System<ShuttleSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(map.Grid);

            var shuttleGrid = mapManager.CreateGridEntity(map.MapId);
            var stationGrid = mapManager.CreateGridEntity(map.MapId);
            var shuttle = shuttleGrid.Owner;
            var station = stationGrid.Owner;
            transform.SetLocalPosition(station, new Vector2(20f, 0f));

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
                station,
                stationGrid.Comp,
                new List<(Vector2i Index, Tile Tile)>
                {
                    new(new Vector2i(0, 0), new Tile(1)),
                    new(new Vector2i(0, 1), new Tile(1)),
                    new(new Vector2i(0, 2), new Tile(1)),
                    new(new Vector2i(-1, 2), new Tile(1)),
                    new(new Vector2i(1, 2), new Tile(1)),
                });

            var shuttleDock = entities.SpawnEntity(
                "AirlockShuttle",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)));
            var stationDock = entities.SpawnEntity(
                "AirlockShuttle",
                new EntityCoordinates(station, new Vector2(0.5f, 0.5f)));
            var autopilot = entities.SpawnEntity(
                "ComputerShuttle",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 1.5f)));

            var config = docking.GetDockingConfig(shuttle, station);
            Assert.That(config, Is.Not.Null);
            shuttleSystem.FTLDock((shuttle, entities.GetComponent<TransformComponent>(shuttle)), config!);
            Assert.That(entities.GetComponent<DockingComponent>(shuttleDock).DockedWith, Is.EqualTo(stationDock));

            var target = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(100f, 0f), map.MapId));
            Assert.That(rescue.TrySetAutopilotTarget(shuttle, target, out var selectedAutopilot), Is.True);
            Assert.That(selectedAutopilot, Is.EqualTo(autopilot));
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var route), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(route.State, Is.EqualTo(LuaMRescueShuttleRouteState.Routing));
                Assert.That(route.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(route.RouteActivity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(route.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(route.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(route.ActivityGeneration, Is.EqualTo((uint) route.RouteGeneration));
                Assert.That(route.SafeExitConfirmed, Is.False);
                Assert.That(route.LastStatus, Does.Contain("safely undocked 1 port pair"));
                Assert.That(entities.GetComponent<DockingComponent>(shuttleDock).DockedWith, Is.Null);
                Assert.That(entities.GetComponent<DockingComponent>(stationDock).DockedWith, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TimedOutRouteCanBeReissuedThroughTheSameRealAutopilotConsole()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var npc = entities.System<NPCSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        EntityUid autopilot = default;
        EntityUid target = default;
        var originalGeneration = 0;

        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(map.Grid);

            var shuttleGrid = mapManager.CreateGridEntity(map.MapId);
            shuttle = shuttleGrid.Owner;
            mapSystem.SetTiles(
                shuttle,
                shuttleGrid.Comp,
                new List<(Vector2i Index, Tile Tile)>
                {
                    new(new Vector2i(0, 0), new Tile(1)),
                    new(new Vector2i(0, 1), new Tile(1)),
                });

            autopilot = entities.SpawnEntity(
                "ComputerShuttle",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)));
            target = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(100f, 0f), map.MapId));

            Assert.That(rescue.TrySetAutopilotTarget(shuttle, target, out var selectedAutopilot), Is.True);
            Assert.That(selectedAutopilot, Is.EqualTo(autopilot));
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var routing), Is.True);

            var lifecycle = entities.GetComponent<LuaMRescueShuttleLifecycleComponent>(shuttle);
            lifecycle.MaxRetries = 0;
            lifecycle.RouteDeadline = timing.CurTime;
            originalGeneration = routing.RouteGeneration;

            Assert.Multiple(() =>
            {
                Assert.That(routing.State, Is.EqualTo(LuaMRescueShuttleRouteState.Routing));
                Assert.That(routing.Role, Is.EqualTo(LuaMRescueRole.Autopilot));
                Assert.That(routing.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(routing.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(routing.ActivityGeneration, Is.EqualTo((uint) routing.RouteGeneration));
                Assert.That(routing.Target, Is.EqualTo(target));
                Assert.That(routing.AutopilotConsole, Is.EqualTo(autopilot));
                Assert.That(entities.HasComponent<Content.Shared.NPC.ActiveNPCComponent>(autopilot), Is.True);
            });
        });

        // Let the real lifecycle, not the test, produce its terminal timeout.
        await pair.RunSeconds(0.7f);

        await server.WaitAssertion(() =>
        {
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var timedOut), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(timedOut.State, Is.EqualTo(LuaMRescueShuttleRouteState.TimedOut));
                Assert.That(timedOut.Terminal, Is.True);
                Assert.That(timedOut.Activity, Is.EqualTo(LuaMRescueActivity.Recovering));
                Assert.That(timedOut.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(timedOut.ActivityFailureReason, Is.EqualTo(LuaMRescueFailureReason.ShuttleRouteFailed));
                Assert.That(timedOut.ActivityGeneration, Is.EqualTo((uint) timedOut.RouteGeneration));
                Assert.That(timedOut.Target, Is.EqualTo(target));
                Assert.That(timedOut.RouteGeneration, Is.EqualTo(originalGeneration));
            });

            // Make wake-up observable. The retry must republish the same target
            // into the real shuttle HTN controller and activate it again.
            var htn = entities.GetComponent<HTNComponent>(autopilot);
            var console = entities.GetComponent<ShuttleConsoleComponent>(autopilot);
            npc.SleepNPC(autopilot, htn);
            htn.Blackboard.Remove<EntityCoordinates>(console.AutopilotTargetKey);
            Assert.That(entities.HasComponent<Content.Shared.NPC.ActiveNPCComponent>(autopilot), Is.False);

            Assert.That(rescue.TryRetryAutopilotRoute(shuttle, out var retryStatus), Is.True, retryStatus);
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var retried), Is.True);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    console.AutopilotTargetKey,
                    out var republishedTarget,
                    entities),
                Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(retried.State, Is.EqualTo(LuaMRescueShuttleRouteState.Routing));
                Assert.That(retried.Terminal, Is.False);
                Assert.That(retried.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(retried.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(retried.ActivityGeneration, Is.EqualTo((uint) retried.RouteGeneration));
                Assert.That(retried.Target, Is.EqualTo(target));
                Assert.That(retried.AutopilotConsole, Is.EqualTo(autopilot));
                Assert.That(retried.RouteGeneration, Is.EqualTo(originalGeneration + 1));
                Assert.That(retried.RetryCount, Is.Zero,
                    "An explicit retry after an exhausted budget starts a fresh bounded budget.");
                Assert.That(retried.RouteDeadline, Is.GreaterThan(timing.CurTime));
                Assert.That(republishedTarget.EntityId, Is.EqualTo(target));
                Assert.That(entities.HasComponent<Content.Shared.NPC.ActiveNPCComponent>(autopilot), Is.True);
                Assert.That(retryStatus, Does.Contain("attempt 1/1 issued"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReplacingActiveAutopilotIntentCancelsOldPlanAndPublishesReturningGeneration()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var rescue = entities.System<LuaMRescueShuttleSystem>();
        var mapSystem = entities.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();

        EntityUid shuttle = default;
        EntityUid autopilot = default;
        EntityUid outboundTarget = default;
        EntityUid returnTarget = default;
        var originalGeneration = 0;

        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(map.Grid);
            var shuttleGrid = mapManager.CreateGridEntity(map.MapId);
            shuttle = shuttleGrid.Owner;
            entities.EnsureComponent<ShuttleComponent>(shuttle);
            mapSystem.SetTile(shuttle, shuttleGrid.Comp, Vector2i.Zero, new Tile(1));
            autopilot = entities.SpawnEntity(
                "ComputerShuttle",
                new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)));
            // This deliberately minimal shuttle has no APC. Model the console
            // as self-powered so ShipMoveToOperator remains alive long enough
            // for this test to exercise replacement of a real running plan.
            var receiver = entities.GetComponent<ApcPowerReceiverComponent>(autopilot);
            entities.System<PowerReceiverSystem>().SetNeedsPower(autopilot, false, receiver);
            receiver.Powered = true;
            outboundTarget = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(100f, 0f), map.MapId));
            returnTarget = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(-100f, 0f), map.MapId));

            Assert.That(
                rescue.TrySetAutopilotTarget(
                    shuttle,
                    outboundTarget,
                    LuaMRescueActivity.Delivering,
                    out var selectedAutopilot),
                Is.True);
            Assert.That(selectedAutopilot, Is.EqualTo(autopilot));
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var outbound), Is.True);
            originalGeneration = outbound.RouteGeneration;
            Assert.Multiple(() =>
            {
                Assert.That(outbound.RouteActivity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(outbound.Activity, Is.EqualTo(LuaMRescueActivity.Delivering));
                Assert.That(outbound.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        var oldPlanStarted = false;
        for (var i = 0; i < AsyncTickLimit; i++)
        {
            await server.WaitAssertion(() =>
            {
                oldPlanStarted = entities.GetComponent<HTNComponent>(autopilot).Plan != null &&
                                 entities.HasComponent<ShipSteererComponent>(autopilot);
            });
            if (oldPlanStarted)
                break;
            await pair.RunTicksSync(1);
        }
        Assert.That(oldPlanStarted, Is.True, "The outbound HTN plan never started.");

        await server.WaitAssertion(() =>
        {
            var htn = entities.GetComponent<HTNComponent>(autopilot);
            var console = entities.GetComponent<ShuttleConsoleComponent>(autopilot);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    console.AutopilotTargetKey,
                    out var oldTarget,
                    entities),
                Is.True);
            Assert.That(oldTarget.EntityId, Is.EqualTo(outboundTarget));

            Assert.That(
                rescue.TrySetAutopilotTarget(
                    shuttle,
                    returnTarget,
                    LuaMRescueActivity.Returning,
                    out var selectedAutopilot),
                Is.True);
            Assert.That(selectedAutopilot, Is.EqualTo(autopilot));
            Assert.That(rescue.TryGetRouteSnapshot(shuttle, out var returning), Is.True);
            Assert.That(
                htn.Blackboard.TryGetValue<EntityCoordinates>(
                    console.AutopilotTargetKey,
                    out var newTarget,
                    entities),
                Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(htn.Plan, Is.Null,
                    "Replacing the intent must synchronously shut down the old HTN operator before wake-up.");
                Assert.That(entities.HasComponent<ShipSteererComponent>(autopilot), Is.False,
                    "Replacing the intent must cancel the old ShipMoveTo operator's live steering component.");
                Assert.That(newTarget.EntityId, Is.EqualTo(returnTarget));
                Assert.That(returning.Target, Is.EqualTo(returnTarget));
                Assert.That(returning.RouteActivity, Is.EqualTo(LuaMRescueActivity.Returning));
                Assert.That(returning.Activity, Is.EqualTo(LuaMRescueActivity.Returning));
                Assert.That(returning.ActivityTerminal, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(returning.RouteGeneration, Is.EqualTo(originalGeneration + 1));
                Assert.That(returning.ActivityGeneration, Is.EqualTo((uint) returning.RouteGeneration));
                Assert.That(
                    string.Join("\n", rescue.BuildAutopilotStatusLines()),
                    Does.Contain("role=Autopilot; activity=Returning"));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static bool HasMovementIntentFor(
        IEntityManager entities,
        EntityUid agent,
        EntityUid target)
    {
        var htn = entities.GetComponent<HTNComponent>(agent);
        if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var followTarget,
                entities) &&
            followTarget.EntityId == target)
        {
            return true;
        }

        return entities.TryGetComponent<NPCSteeringComponent>(agent, out var steering) &&
               steering.Coordinates.EntityId == target;
    }
}
