using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server._LuaM.ShipPersistence;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._NF.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.Maps;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMSalvageExpeditionConsoleBindingTest
{
    [Test]
    public async Task SalvageExpeditionConsoleOnOrdinaryShuttleCreatesLocalExpeditionData()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
        });

        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var stations = entities.System<StationSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var testMap = await pair.CreateTestMap();

        EntityUid station = EntityUid.Invalid;
        EntityUid console = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                entities.DeleteEntity(testMap.Grid);

                station = entities.SpawnEntity("StandardFrontierVessel", MapCoordinates.Nullspace);
                var shuttle = mapManager.CreateGridEntity(testMap.MapId);
                maps.SetTile(shuttle.Owner, shuttle.Comp, Vector2i.Zero, new Tile(1));
                transform.SetLocalPosition(shuttle.Owner, Vector2.Zero);

                if (!entities.HasComponent<ShuttleComponent>(shuttle.Owner))
                    entities.AddComponent<ShuttleComponent>(shuttle.Owner);

                stations.AddGridToStation(station, shuttle.Owner);

                Assert.That(entities.HasComponent<SalvageExpeditionDataComponent>(station), Is.False,
                    "Ordinary vessels should start without expedition data in this test.");

                console = entities.SpawnEntity(
                    "ComputerSalvageExpedition",
                    new EntityCoordinates(shuttle.Owner, new Vector2(0.5f, 0.5f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entities.TryGetComponent<SalvageExpeditionDataComponent>(station, out var data), Is.True,
                    "Installing an expedition console on a shuttle station should bind it to that shuttle's own station data.");
                Assert.That(data!.Missions, Is.Not.Empty,
                    "Newly enabled expedition data should get an initial local mission offer list.");
                Assert.That(entities.GetComponent<SalvageExpeditionConsoleComponent>(console).LegacyLaunchingEnabled,
                    Is.True,
                    "Player ship consoles must offer shuttle-launched expeditions again.");
                entities.EventBus.RaiseLocalEvent(
                    console,
                    new ClaimSalvageMessage { Index = data.Missions.Keys.First() });
                Assert.That(data.ActiveMission, Is.EqualTo((int) data.Missions.Keys.First()),
                    "A claim message on the shuttle console must start the selected expedition.");
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task RestoredPersistentVesselCommitsStationPreservesCooldownAndValidatesClaims()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
        });

        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var maps = entities.System<SharedMapSystem>();
        var stations = entities.System<StationSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var ui = entities.System<SharedUserInterfaceSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var shipyard = entities.System<ShipyardSystem>();
        var testMap = await pair.CreateTestMap();

        try
        {
            await server.WaitPost(() =>
            {
                entities.DeleteEntity(testMap.Grid);

                var shuttle = mapManager.CreateGridEntity(testMap.MapId);
                maps.SetTile(shuttle.Owner, shuttle.Comp, Vector2i.Zero, new Tile(1));
                transform.SetLocalPosition(shuttle.Owner, Vector2.Zero);
                entities.EnsureComponent<ShuttleComponent>(shuttle.Owner);
                var vessel = entities.EnsureComponent<VesselComponent>(shuttle.Owner);
                vessel.VesselId = "Phoenix";

                var gameMap = prototypes.Index<GameMapPrototype>("Phoenix");
                var sourceStation = stations.InitializeNewStation(
                    gameMap.Stations["Phoenix"],
                    [shuttle.Owner],
                    "LuaM restored expedition vessel");
                entities.SpawnEntity(
                    "ComputerSalvageExpedition",
                    new EntityCoordinates(shuttle.Owner, new Vector2(0.5f, 0.5f)));

                var sourceData = entities.EnsureComponent<SalvageExpeditionDataComponent>(sourceStation);
                sourceData.Cooldown = true;
                sourceData.NextOffer = timing.CurTime + TimeSpan.FromSeconds(90);
                Assert.That(
                    persistence.TryCaptureSnapshot(
                        shuttle.Owner,
                        1,
                        out var cooldownSnapshot,
                        out var cooldownCaptureReason),
                    Is.True,
                    cooldownCaptureReason);
                Assert.That(persistence.TryCommitSnapshotRevision(shuttle.Owner, cooldownSnapshot), Is.True);
                Assert.That(
                    entities.GetComponent<LuaMSalvageExpeditionCooldownComponent>(shuttle.Owner).RemainingCooldown,
                    Is.EqualTo(TimeSpan.FromSeconds(90)),
                    "Capture must store a relative cooldown on the portable grid.");

                sourceData.Cooldown = false;
                sourceData.ActiveMission = 0;
                sourceData.NextOffer = TimeSpan.Zero;
                Assert.That(
                    persistence.TryCaptureSnapshot(
                        shuttle.Owner,
                        2,
                        out var noCooldownSnapshot,
                        out var noCooldownCaptureReason),
                    Is.True,
                    noCooldownCaptureReason);
                Assert.That(persistence.TryCommitSnapshotRevision(shuttle.Owner, noCooldownSnapshot), Is.True);
                Assert.That(
                    entities.HasComponent<LuaMSalvageExpeditionCooldownComponent>(shuttle.Owner),
                    Is.False,
                    "A ship without an active expedition or cooldown must not persist stale cooldown state.");

                vessel.VesselId = "LuaMInvalidVesselForRestoreTest";
                Assert.That(
                    persistence.TryCaptureSnapshot(
                        shuttle.Owner,
                        3,
                        out var invalidVesselSnapshot,
                        out var invalidCaptureReason),
                    Is.True,
                    invalidCaptureReason);

                entities.DeleteEntity(sourceStation);
                entities.DeleteEntity(shuttle.Owner);
                var entityCountBeforeRestore = entities.GetEntities().Count();

                Assert.That(
                    persistence.TryBeginRestoreSnapshot(
                        invalidVesselSnapshot,
                        testMap.MapId,
                        out _,
                        out var invalidVesselReason),
                    Is.False);
                Assert.Multiple(() =>
                {
                    Assert.That(
                        invalidVesselReason,
                        Is.EqualTo(
                            "restored-vessel-game-map-prototype-not-found:LuaMInvalidVesselForRestoreTest"));
                    Assert.That(entities.GetEntities().Count(), Is.EqualTo(entityCountBeforeRestore),
                        "Invalid vessel metadata must fail and clean the deserialized graph before commit.");
                });

                Assert.That(
                    persistence.TryBeginRestoreSnapshot(
                        cooldownSnapshot,
                        testMap.MapId,
                        out var rollbackScope,
                        out var beginReason),
                    Is.True,
                    beginReason);
                var rollbackStation = stations.GetOwningStation(rollbackScope.Grid);
                Assert.Multiple(() =>
                {
                    Assert.That(rollbackStation, Is.Null,
                        "TryBegin must not expose a vessel station before database completion.");
                    Assert.That(entities.HasComponent<StationMemberComponent>(rollbackScope.Grid), Is.False,
                        "The pre-commit grid must not acquire station membership as a side effect.");
                });
                Assert.That(persistence.RollbackRestore(rollbackScope), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(rollbackScope.Grid), Is.False);
                    Assert.That(entities.GetEntities().Count(), Is.EqualTo(entityCountBeforeRestore),
                        "Rolling back a pre-commit restore must remove only the deserialized graph and leak no station helpers.");
                });

                Assert.That(
                    persistence.TryRestoreSnapshot(
                        cooldownSnapshot,
                        testMap.MapId,
                        out var cooldownGrid,
                        out var cooldownRestoreReason),
                    Is.True,
                    cooldownRestoreReason);

                var cooldownStation = stations.GetOwningStation(cooldownGrid);
                Assert.That(cooldownStation, Is.Not.Null,
                    "Direct restore must commit the deferred vessel station synchronously.");
                var cooldownData = entities.GetComponent<SalvageExpeditionDataComponent>(cooldownStation!.Value);
                var restoredRemaining = cooldownData.NextOffer - timing.CurTime;
                Assert.Multiple(() =>
                {
                    Assert.That(cooldownData.Cooldown, Is.True,
                        "A parked expedition cooldown must resume on the committed vessel station.");
                    Assert.That(
                        restoredRemaining,
                        Is.InRange(TimeSpan.FromSeconds(89), TimeSpan.FromSeconds(91)),
                        "Cooldown persistence must use remaining duration instead of the old round's absolute CurTime.");
                });

                var (cooldownConsole, cooldownConsoleComponent) = FindConsoleOnGrid(entities, cooldownGrid);
                Assert.That(
                    ui.TryGetUiState<SalvageExpeditionConsoleState>(
                        cooldownConsole,
                        SalvageConsoleUiKey.Expedition,
                        out var cooldownUiState),
                    Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(cooldownUiState!.Cooldown, Is.True);
                    Assert.That(cooldownUiState.NextOffer, Is.EqualTo(cooldownData.NextOffer),
                        "Deferred station commit must replace the console's pre-station disabled state with the restored cooldown.");
                });
                var cooldownMission = EnsureMission(cooldownData);
                cooldownConsoleComponent.Debug = true;
                entities.EventBus.RaiseLocalEvent(
                    cooldownConsole,
                    new ClaimSalvageMessage { Index = cooldownMission });
                Assert.That(cooldownData.ActiveMission, Is.Zero,
                    "A forged claim message must not bypass the server-side cooldown guard.");

                entities.DeleteEntity(cooldownStation.Value);
                entities.DeleteEntity(cooldownGrid);

                Assert.That(
                    persistence.TryRestoreSnapshot(
                        noCooldownSnapshot,
                        testMap.MapId,
                        out var restoredGrid,
                        out var restoreReason),
                    Is.True,
                    restoreReason);

                var (restoredConsole, restoredConsoleComponent) = FindConsoleOnGrid(entities, restoredGrid);
                var restoredStation = stations.GetOwningStation(restoredGrid);
                var deletableStationMethod = typeof(ShipyardSystem).GetMethod(
                    "TryGetDeletablePersistentVesselStation",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(deletableStationMethod, Is.Not.Null,
                    "The parking path must retain its guarded vessel-station cleanup predicate.");
                var deletableStationArgs = new object[] { restoredGrid, EntityUid.Invalid };
                Assert.Multiple(() =>
                {
                    Assert.That(restoredStation, Is.Not.Null,
                        "The restored grid must own a new station before station-bound console messages are handled.");
                    Assert.That(stations.GetOwningStation(restoredConsole), Is.EqualTo(restoredStation),
                        "The expedition console must resolve the restored vessel's new station.");
                    Assert.That(
                        entities.GetComponent<ExtraShuttleInformationComponent>(restoredStation!.Value).Vessel?.Id,
                        Is.EqualTo("Phoenix"),
                        "The recreated station must retain a vessel marker that distinguishes it from a main station.");
                    Assert.That(
                        (bool) deletableStationMethod!.Invoke(shipyard, deletableStationArgs)! &&
                        (EntityUid) deletableStationArgs[1] == restoredStation,
                        Is.True,
                        "Parking cleanup may delete only the proven one-grid vessel station recreated for this hull.");
                });

                var restoredVesselInfo =
                    entities.GetComponent<ExtraShuttleInformationComponent>(restoredStation!.Value);
                var restoredStationData =
                    entities.GetComponent<StationDataComponent>(restoredStation.Value);
                var stationGrids = (HashSet<EntityUid>) typeof(StationDataComponent)
                    .GetField(nameof(StationDataComponent.Grids))!
                    .GetValue(restoredStationData)!;
                var unrelatedGrid = mapManager.CreateGridEntity(testMap.MapId);
                stationGrids.Add(unrelatedGrid.Owner);
                var inconsistentStationArgs = new object[] { restoredGrid, EntityUid.Invalid };
                Assert.That(
                    (bool) deletableStationMethod!.Invoke(shipyard, inconsistentStationArgs)!,
                    Is.False,
                    "Station deletion must fail closed when StationData still records another grid, even if its membership marker is missing.");
                stationGrids.Remove(unrelatedGrid.Owner);
                entities.DeleteEntity(unrelatedGrid.Owner);

                restoredVesselInfo.Vessel = null;
                var unprovenStationArgs = new object[] { restoredGrid, EntityUid.Invalid };
                Assert.That(
                    (bool) deletableStationMethod!.Invoke(shipyard, unprovenStationArgs)!,
                    Is.False,
                    "A station without a matching vessel marker must never be deleted with a parked grid.");
                restoredVesselInfo.Vessel = "Phoenix";

                var data = entities.GetComponent<SalvageExpeditionDataComponent>(restoredStation!.Value);
                Assert.Multiple(() =>
                {
                    Assert.That(data.Cooldown, Is.False,
                        "A vessel captured outside cooldown must restore ready for a new expedition.");
                    Assert.That(data.NextOffer, Is.EqualTo(TimeSpan.Zero),
                        "No-cooldown restore must leave NextOffer zero for normal mission generation.");
                    Assert.That(
                        entities.HasComponent<LuaMSalvageExpeditionCooldownComponent>(restoredGrid),
                        Is.False);
                });

                restoredConsoleComponent.Debug = true;
                var missionIndex = EnsureMission(data);
                entities.EventBus.RaiseLocalEvent(
                    restoredConsole,
                    new ClaimSalvageMessage { Index = missionIndex });

                Assert.That(data.ActiveMission, Is.EqualTo(missionIndex),
                    "A claim message sent by the restored console BUI must reach the station-bound server handler.");
            });
        }
        finally
        {
            pair.Kill();
        }
    }

    private static (EntityUid Uid, SalvageExpeditionConsoleComponent Component) FindConsoleOnGrid(
        IEntityManager entities,
        EntityUid grid)
    {
        var query = entities.EntityQueryEnumerator<SalvageExpeditionConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var component, out var xform))
        {
            if (xform.GridUid == grid)
                return (uid, component);
        }

        Assert.Fail("The expedition console must remain in the restored ship graph.");
        return default;
    }

    private static ushort EnsureMission(SalvageExpeditionDataComponent data)
    {
        if (data.Missions.Count == 0)
        {
            // The normal update fills a newly spawned expedition station. This explicit fixture keeps
            // message assertions focused on server binding and cooldown authorization after restore.
            data.Missions[1] = new SalvageMissionParams
            {
                Index = 1,
                MissionType = SalvageMissionType.Elimination,
                Difficulty = DifficultyRating.Moderate,
                Seed = 1,
            };
        }

        return data.Missions.Keys.First();
    }
}
