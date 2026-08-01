using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._LuaM.Rescue;
using Content.Server.Access.Systems;
using Content.Server.NPC;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.NPC;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueNavigationSystem))]
public sealed class LuaMRescueAccessRuntimeTest
{
    private const string TestNpc = "LuaMRescueAccessRuntimeNpc";
    private const float ActionRange = 1.5f;
    private const int ProbeTickLimit = 180;

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  parent: MobHuman
  id: {TestNpc}
  components:
  - type: InputMover
  - type: MobMover
  - type: HTN
    pauseWhenNoPlayersInRange: false
    rootTask:
      task: FollowCompound
    blackboard:
      FollowCloseRange: !type:Single
        1.0
      FollowRange: !type:Single
        1.5
      MinimumIdleTime: !type:Single
        0.1
      MaximumIdleTime: !type:Single
        0.2
      NavInteract: !type:Bool
        true
      NavAccess: !type:Bool
        true
";

    [Test]
    public async Task AccessControlledOnlyRouteUsesCarriedIdOrReportsAccessDenied()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var pathfinding = entities.System<PathfindingSystem>();
        var accessReader = entities.System<AccessReaderSystem>();
        var npc = entities.System<NPCSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid target = default;
        EntityUid door = default;
        await server.WaitAssertion(() =>
        {
            BuildFloorRectangle(mapSystem, map, 0, 8, 0, 0);
            door = SpawnAnchoredDoor(entities, map.Grid, 4.5f, 0.5f);
            ConfigureDoorAccess(entities, accessReader, door, "Medical");

            agent = SpawnSleepingAccessNpc(entities, map.Grid, 0.5f, 0.5f);
            GiveAccessId(entities, agent, map.Grid, "Medical");
            target = entities.SpawnEntity(null, GridCoordinates(map.Grid, 8.5f, 0.5f));

            Assert.Multiple(() =>
            {
                Assert.That(pathfinding.GetFlags(agent) & PathFlags.Access, Is.Not.EqualTo(PathFlags.None));
                Assert.That(accessReader.IsAllowed(agent, door), Is.True,
                    "The path requester must obtain Medical from its real carried ID card.");
                Assert.That(navigation.ProbeRoute(agent, target, ActionRange).State,
                    Is.EqualTo(LuaMRescuePathProbeState.Pending));
            });
        });

        var allowed = await AwaitCompletedProbe(pair, navigation, agent, target);
        Assert.Multiple(() =>
        {
            Assert.That(allowed.State, Is.EqualTo(LuaMRescuePathProbeState.Reachable));
            Assert.That(allowed.RequiresAccess, Is.True);
        });

        await server.WaitAssertion(() =>
        {
            ConfigureDoorAccess(entities, accessReader, door, "NuclearOperative");
            Assert.That(accessReader.IsAllowed(agent, door), Is.False,
                "The same carried Medical ID must not satisfy NuclearOperative access.");

            navigation.CancelRoute(agent, target);
            Assert.That(navigation.ProbeRoute(agent, target, ActionRange).State,
                Is.EqualTo(LuaMRescuePathProbeState.Pending));
        });

        var denied = await AwaitCompletedProbe(pair, navigation, agent, target);
        Assert.That(denied.State, Is.EqualTo(LuaMRescuePathProbeState.AccessDenied));

        await server.WaitAssertion(() =>
        {
            // Restore the carried ID's access and power the runtime fixture. The
            // door transition itself must still be produced by NPC interaction,
            // not by the test.
            ConfigureDoorAccess(entities, accessReader, door, "Medical");
            var receiver = entities.GetComponent<ApcPowerReceiverComponent>(door);
            entities.System<PowerReceiverSystem>().SetNeedsPower(door, false, receiver);
            receiver.Powered = true;
            Assert.That(accessReader.IsAllowed(agent, door), Is.True);
            Assert.That(entities.GetComponent<DoorComponent>(door).State, Is.EqualTo(DoorState.Closed));

            var htn = entities.GetComponent<Content.Server.NPC.HTN.HTNComponent>(agent);
            Assert.That(htn.PauseWhenNoPlayersInRange, Is.False,
                "The access fixture must remain awake without a nearby test player.");
            htn.Blackboard.SetValue(
                NPCBlackboard.FollowTarget,
                new EntityCoordinates(target, Vector2.Zero));
            npc.WakeNPC(agent, htn);
        });

        var observedOpen = false;
        for (var i = 0; i < ProbeTickLimit * 5; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var state = entities.GetComponent<DoorComponent>(door).State;
                observedOpen |= state is DoorState.Opening or DoorState.Open;
            });

            if (observedOpen)
                break;
        }

        Assert.That(observedOpen, Is.True,
            "The authorized NPC never caused the real access airlock to open.");

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeniedDoorDoesNotHideReachableAccessFreeDetour()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var accessReader = entities.System<AccessReaderSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid target = default;
        await server.WaitAssertion(() =>
        {
            // The middle row contains a denied door, while the rows above and below
            // are genuine floor routes around it.
            BuildFloorRectangle(mapSystem, map, 0, 8, 0, 2);
            var door = SpawnAnchoredDoor(entities, map.Grid, 4.5f, 1.5f);
            ConfigureDoorAccess(entities, accessReader, door, "NuclearOperative");

            agent = SpawnSleepingAccessNpc(entities, map.Grid, 0.5f, 1.5f);
            GiveAccessId(entities, agent, map.Grid, "Medical");
            target = entities.SpawnEntity(null, GridCoordinates(map.Grid, 8.5f, 1.5f));

            Assert.That(accessReader.IsAllowed(agent, door), Is.False);
            Assert.That(navigation.ProbeRoute(agent, target, ActionRange).State,
                Is.EqualTo(LuaMRescuePathProbeState.Pending));
        });

        var completed = await AwaitCompletedProbe(pair, navigation, agent, target);
        Assert.Multiple(() =>
        {
            Assert.That(completed.State, Is.EqualTo(LuaMRescuePathProbeState.Reachable));
            Assert.That(completed.RequiresAccess, Is.False,
                "The selected path must use the access-free detour, not the denied airlock.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RouteCacheInvalidatesWhenTargetChangesGridAtSameLocalCoordinates()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var navigation = entities.System<LuaMRescueNavigationSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid target = default;
        await server.WaitAssertion(() =>
        {
            BuildFloorRectangle(mapSystem, map, 0, 4, 0, 0);
            agent = SpawnSleepingAccessNpc(entities, map.Grid, 0.5f, 0.5f);
            target = entities.SpawnEntity(null, GridCoordinates(map.Grid, 4.5f, 0.5f));

            Assert.That(
                navigation.ProbeRoute(agent, target, ActionRange, allowConfirmedDockedCrossGrid: true).State,
                Is.EqualTo(LuaMRescuePathProbeState.Pending));
        });

        var initial = await AwaitCompletedProbe(
            pair,
            navigation,
            agent,
            target,
            allowConfirmedDockedCrossGrid: true);
        Assert.That(initial.State, Is.EqualTo(LuaMRescuePathProbeState.Reachable));

        await server.WaitAssertion(() =>
        {
            var movingGrid = maps.CreateGridEntity(map.MapId);
            for (var x = 0; x <= 4; x++)
                mapSystem.SetTile(movingGrid, new Vector2i(x, 0), map.Tile.Tile);

            // Preserve the exact local endpoint. Only its owning grid changes,
            // reproducing a patient carried by a shuttle relative to a rescuer
            // on the station grid.
            transform.SetCoordinates(target, new EntityCoordinates(movingGrid.Owner, new Vector2(4.5f, 0.5f)));
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<TransformComponent>(target).GridUid, Is.EqualTo(movingGrid.Owner));
                Assert.That(entities.GetComponent<TransformComponent>(target).Coordinates.Position,
                    Is.EqualTo(new Vector2(4.5f, 0.5f)));
                Assert.That(
                    navigation.ProbeRoute(agent, target, ActionRange, allowConfirmedDockedCrossGrid: true).State,
                    Is.EqualTo(LuaMRescuePathProbeState.Pending),
                    "A cached station route must be invalidated when the patient moves onto another grid.");
            });

            navigation.CancelRoute(agent, target);
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnSleepingAccessNpc(
        IEntityManager entities,
        Entity<MapGridComponent> grid,
        float x,
        float y)
    {
        var agent = entities.SpawnEntity(TestNpc, GridCoordinates(grid, x, y));
        entities.System<NPCSystem>().SleepNPC(agent);
        return agent;
    }

    private static void GiveAccessId(
        IEntityManager entities,
        EntityUid agent,
        Entity<MapGridComponent> grid,
        ProtoId<AccessLevelPrototype> access)
    {
        var id = entities.SpawnEntity("PassengerIDCard", GridCoordinates(grid, 0.5f, 0.5f));
        Assert.Multiple(() =>
        {
            Assert.That(entities.HasComponent<IdCardComponent>(id), Is.True);
            Assert.That(entities.HasComponent<AccessComponent>(id), Is.True);
            Assert.That(entities.System<AccessSystem>().TrySetTags(id, new[] { access }), Is.True);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(agent, id), Is.True);
        });
    }

    private static EntityUid SpawnAnchoredDoor(
        IEntityManager entities,
        Entity<MapGridComponent> grid,
        float x,
        float y)
    {
        var door = entities.SpawnEntity("Airlock", GridCoordinates(grid, x, y));
        var transform = entities.GetComponent<TransformComponent>(door);
        if (!transform.Anchored)
            entities.System<SharedTransformSystem>().AnchorEntity(door, transform);

        Assert.That(transform.Anchored, Is.True);
        Assert.That(entities.HasComponent<AccessReaderComponent>(door), Is.True);
        return door;
    }

    private static void ConfigureDoorAccess(
        IEntityManager entities,
        AccessReaderSystem accessReader,
        EntityUid door,
        ProtoId<AccessLevelPrototype> access)
    {
        var reader = entities.GetComponent<AccessReaderComponent>(door);
        // Airlocks normally delegate to their electronics container. Keeping the
        // real reader on the airlock makes this runtime fixture deterministic while
        // exercising the exact authorization API used by pathfinding and steering.
        reader.ContainerAccessProvider = null;
        accessReader.SetAccesses(door, reader, new List<ProtoId<AccessLevelPrototype>> { access });
    }

    private static void BuildFloorRectangle(
        SharedMapSystem mapSystem,
        TestMapData map,
        int minimumX,
        int maximumX,
        int minimumY,
        int maximumY)
    {
        for (var x = minimumX; x <= maximumX; x++)
        {
            for (var y = minimumY; y <= maximumY; y++)
                mapSystem.SetTile(map.Grid, new Vector2i(x, y), map.Tile.Tile);
        }
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
        bool allowConfirmedDockedCrossGrid = false)
    {
        for (var i = 0; i < ProbeTickLimit; i++)
        {
            LuaMRescuePathProbeSnapshot snapshot = default;
            await pair.Server.WaitAssertion(() =>
            {
                snapshot = navigation.ProbeRoute(
                    agent,
                    target,
                    ActionRange,
                    allowConfirmedDockedCrossGrid);
            });

            if (snapshot.State != LuaMRescuePathProbeState.Pending)
                return snapshot;

            await pair.RunTicksSync(1);
        }

        Assert.Fail($"Access route probe remained Pending for {ProbeTickLimit} server ticks.");
        return default;
    }
}
