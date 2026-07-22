using System.Collections.Generic;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.SectorServices;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.MassMedia.Components;
using Robust.Server.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMAiPhysicalBaseGrowthLimitTest
{
    private static readonly ResPath SectorMemoryPath = new("/luam/sector_memory.json");

    [Test]
    public async Task DirectSpawnsRespectMapLiveAndRoundAdmissionCaps()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var budget = entManager.System<LuaMAiPhysicalBaseBudgetSystem>();

        MapId firstMap = default;
        MapId secondMap = default;
        MapId thirdMap = default;

        await server.WaitPost(() =>
        {
            ApplyEnabledPreset(server);
            mapSystem.CreateMap(out firstMap);
            mapSystem.CreateMap(out secondMap);
            mapSystem.CreateMap(out thirdMap);

            SpawnMany("LuaMAiBaseBeacon", firstMap, 2);
            SpawnMany("LuaMAiBaseBeacon", secondMap, 1);
            SpawnMany("LuaMAiBaseBeacon", thirdMap, 1);
            entManager.SpawnEntity("LuaMAiBaseBeacon", MapCoordinates.Nullspace);

            SpawnMany("LuaMAiBaseZoneMarker", firstMap, 6);
            SpawnMany("LuaMAiBaseZoneMarker", secondMap, 5);
            SpawnMany("LuaMAiBaseZoneMarker", thirdMap, 1);

            AddLogisticsShips(firstMap, 2);
            AddLogisticsShips(secondMap, 1);
            AddLogisticsShips(thirdMap, 1);

            SpawnMany("LuaMAiMiningDrone", firstMap, 7);
            SpawnMany("LuaMAiMiningDrone", secondMap, 6);
            SpawnMany("LuaMAiMiningDrone", thirdMap, 1);

            SpawnMany("LuaMAiDroneTrace", firstMap, 33);
            SpawnMany("LuaMAiDroneTrace", secondMap, 32);
            SpawnMany("LuaMAiDroneTrace", thirdMap, 1);

            SpawnMany("LuaMAiSupplyDrop", firstMap, 5);
            SpawnMany("LuaMAiSupplyDrop", secondMap, 4);
            SpawnMany("LuaMAiSupplyDrop", thirdMap, 1);
        });

        await pair.RunSeconds(1f);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                AssertLimit(LuaMAiPhysicalEntityKind.Anchor, firstMap, 1, 2);
                AssertLimit(LuaMAiPhysicalEntityKind.Zone, firstMap, 5, 10);
                AssertLimit(LuaMAiPhysicalEntityKind.Ship, firstMap, 1, 2);
                AssertLimit(LuaMAiPhysicalEntityKind.Drone, firstMap, 6, 12);
                AssertLimit(LuaMAiPhysicalEntityKind.Trace, firstMap, 32, 64);
                AssertLimit(LuaMAiPhysicalEntityKind.Drop, firstMap, 4, 8);

                Assert.That(budget.GetRejectionCount(LuaMAiPhysicalEntityKind.Anchor, "nullspace"), Is.GreaterThan(0));
                Assert.That(budget.GetRejectionCount(LuaMAiPhysicalEntityKind.Drone, "map_cap"), Is.GreaterThan(0));
                Assert.That(budget.GetRejectionCount(LuaMAiPhysicalEntityKind.Trace, "round_live_cap"), Is.GreaterThan(0));
            });
        });

        await DeleteAllDrops();
        await server.WaitPost(() =>
        {
            SpawnMany("LuaMAiSupplyDrop", firstMap, 4);
            SpawnMany("LuaMAiSupplyDrop", secondMap, 4);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(budget.GetRoundSpawnCount(LuaMAiPhysicalEntityKind.Drop), Is.EqualTo(16));
            Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Drop), Is.EqualTo(8));
        });

        await DeleteAllDrops();
        await server.WaitPost(() => entManager.SpawnEntity(
            "LuaMAiSupplyDrop",
            new MapCoordinates(Vector2.Zero, firstMap)));
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Drop), Is.Zero);
            Assert.That(budget.GetRejectionCount(LuaMAiPhysicalEntityKind.Drop, "round_spawn_cap"), Is.GreaterThan(0));
            entManager.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
            Assert.That(budget.GetRoundSpawnCount(LuaMAiPhysicalEntityKind.Drop), Is.Zero);
        });

        await pair.CleanReturnAsync();

        void SpawnMany(string prototype, MapId mapId, int count)
        {
            for (var i = 0; i < count; i++)
                entManager.SpawnEntity(prototype, new MapCoordinates(new Vector2(i * 0.1f, 0f), mapId));
        }

        void AddLogisticsShips(MapId mapId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var uid = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(i, 2f), mapId));
                entManager.AddComponent<LuaMAiLogisticsShipComponent>(uid).NextCycle = TimeSpan.FromDays(1);
            }
        }

        void AssertLimit(LuaMAiPhysicalEntityKind kind, MapId mapId, int expectedMap, int expectedRound)
        {
            Assert.That(budget.GetLiveCount(kind, mapId), Is.EqualTo(expectedMap), $"{kind} map cap");
            Assert.That(budget.GetLiveCount(kind), Is.EqualTo(expectedRound), $"{kind} round live cap");
        }

        async Task DeleteAllDrops()
        {
            await server.WaitPost(() =>
            {
                var query = entManager.EntityQueryEnumerator<LuaMAiSupplyDropComponent>();
                var drops = new List<EntityUid>();
                while (query.MoveNext(out var uid, out _))
                    drops.Add(uid);

                foreach (var uid in drops)
                    entManager.DeleteEntity(uid);
            });
            await pair.RunTicksSync(2);
        }
    }

    [Test]
    public async Task DropDedupAndParentCleanupPreventOrphans()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<MapSystem>();
        var story = entManager.System<LuaMSectorStorySystem>();
        var drops = entManager.System<LuaMAiSupplyDropSystem>();
        var budget = entManager.System<LuaMAiPhysicalBaseBudgetSystem>();

        EntityUid anchor = default;
        EntityUid ship = default;
        MapId mapId = default;
        var zoneEntities = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            ApplyEnabledPreset(server);
            resources.UserData.Delete(SectorMemoryPath);
            SectorNewsComponent.Articles.Clear();
            mapSystem.CreateMap(out mapId);

            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(-4f, 0f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            anchor = entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(Vector2.Zero, mapId));
            ship = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(3f, 0f), mapId));
            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(ship);
            logistics.CrewRoleManifest = new List<string> { "repair", "guard" };
            logistics.NextCycle = TimeSpan.FromDays(1);

            story.EnsureAiBase("physical-growth-test");
            story.RecordAiBaseShipVisit("physical-growth-test", "hauler", "Baeg", "Baeg test ship");
        });

        await pair.RunSeconds(1f);

        EntityUid firstDrop = default;
        EntityUid secondDrop = default;
        await server.WaitPost(() =>
        {
            Assert.That(drops.TrySpawnForLatestTrade("dedup-first", out firstDrop, out _), Is.True);
            Assert.That(drops.TrySpawnForLatestTrade("dedup-second", out secondDrop, out _), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(secondDrop, Is.EqualTo(firstDrop));
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Drop, mapId), Is.EqualTo(1));
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Anchor, mapId), Is.EqualTo(1));
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Zone, mapId), Is.EqualTo(5));
                Assert.That(CountChildDrones(ship), Is.EqualTo(2));
            });
        });

        await server.WaitPost(() =>
        {
            var query = entManager.EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
            while (query.MoveNext(out var uid, out _))
            {
                if (entManager.GetComponent<TransformComponent>(uid).MapID == mapId)
                    zoneEntities.Add(uid);
            }

            Assert.That(zoneEntities, Has.Count.EqualTo(5));
        });

        await server.WaitPost(() => entManager.DeleteEntity(ship));
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountChildDrones(ship), Is.Zero);
            Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Drone, mapId), Is.Zero);
        });

        await server.WaitPost(() => entManager.DeleteEntity(anchor));
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Anchor, mapId), Is.Zero);
                Assert.That(budget.TryGetAdmissionMap(anchor, LuaMAiPhysicalEntityKind.Anchor, out _), Is.False);
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Zone, mapId), Is.Zero);

                foreach (var zone in zoneEntities)
                {
                    Assert.That(entManager.EntityExists(zone), Is.False, $"zone {zone} survived its last anchor");
                    Assert.That(
                        budget.TryGetAdmissionMap(zone, LuaMAiPhysicalEntityKind.Zone, out _),
                        Is.False,
                        $"zone {zone} left a stale budget admission");
                }
            });
        });

        await pair.CleanReturnAsync();

        int CountChildDrones(EntityUid parent)
        {
            var count = 0;
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out _, out var drone))
            {
                if (drone.ParentShip == parent)
                    count++;
            }

            return count;
        }
    }

    [Test]
    public async Task MiningUpdatesYieldAtActionBudgetAndResumeWithCursor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var budget = entManager.System<LuaMAiPhysicalBaseBudgetSystem>();

        var drones = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            ApplyEnabledPreset(server);
            mapSystem.CreateMap(out var mapId);
            for (var i = 0; i < 6; i++)
            {
                var uid = entManager.SpawnEntity(
                    "LuaMAiScoutDrone",
                    new MapCoordinates(new Vector2(i, 0f), mapId));
                var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(uid);
                drone.NextMove = TimeSpan.FromTicks(1);
                drone.NextMine = TimeSpan.FromDays(1);
                drone.NextSocialScan = TimeSpan.FromTicks(1);
                drones.Add(uid);
            }
        });

        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var diagnostics = budget.GetSliceDiagnostics("mining");
            Assert.Multiple(() =>
            {
                Assert.That(diagnostics.LastProcessed, Is.LessThanOrEqualTo(LuaMAiPhysicalBaseBudgetSystem.MiningActionsPerSlice));
                Assert.That(diagnostics.Exhaustions, Is.GreaterThan(0));
            });
        });

        await pair.RunSeconds(1f);
        await server.WaitAssertion(() =>
        {
            foreach (var uid in drones)
            {
                var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(uid);
                Assert.That(drone.NextMove, Is.GreaterThan(TimeSpan.FromTicks(1)));
                Assert.That(drone.NextSocialScan, Is.GreaterThan(TimeSpan.FromTicks(1)));
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DisableTransitionDeletesAllAdmittedKindsIncludingShipGrid()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entManager.System<MapSystem>();
        var budget = entManager.System<LuaMAiPhysicalBaseBudgetSystem>();

        var entities = new List<EntityUid>();
        EntityUid shipGrid = default;
        await server.WaitPost(() =>
        {
            ApplyEnabledPreset(server);
            mapSystem.CreateMap(out var mapId);
            entities.Add(entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(Vector2.Zero, mapId)));
            entities.Add(entManager.SpawnEntity("LuaMAiBaseZoneMarker", new MapCoordinates(new Vector2(1f, 0f), mapId)));
            entities.Add(entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(new Vector2(2f, 0f), mapId)));
            entities.Add(entManager.SpawnEntity("LuaMAiDroneTrace", new MapCoordinates(new Vector2(3f, 0f), mapId)));
            entities.Add(entManager.SpawnEntity("LuaMAiSupplyDrop", new MapCoordinates(new Vector2(4f, 0f), mapId)));

            shipGrid = mapManager.CreateGridEntity(mapId).Owner;
            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(shipGrid);
            logistics.CrewRoleManifest = new List<string> { "guard" };
            logistics.NextCycle = TimeSpan.FromDays(1);
            entities.Add(shipGrid);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            foreach (var kind in Enum.GetValues<LuaMAiPhysicalEntityKind>())
                Assert.That(budget.GetLiveCount(kind), Is.GreaterThan(0), $"{kind} was admitted before disable");
        });

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.LuaMAiPhysicalBaseEnabled, false));
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var uid in entities)
                    Assert.That(entManager.EntityExists(uid), Is.False, $"{uid} survived the disable transition");

                foreach (var kind in Enum.GetValues<LuaMAiPhysicalEntityKind>())
                    Assert.That(budget.GetLiveCount(kind), Is.Zero, $"{kind} admission survived disable");

                Assert.That(entManager.EntityExists(shipGrid), Is.False, "the admitted ship grid survived disable");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StartupBypassDeletesRejectedShipGridAndConflictingPhysicalEntity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entManager.System<MapSystem>();
        var budget = entManager.System<LuaMAiPhysicalBaseBudgetSystem>();

        EntityUid admittedShip = default;
        EntityUid rejectedShip = default;
        EntityUid conflictingEntity = default;
        MapId mapId = default;
        await server.WaitPost(() =>
        {
            ApplyEnabledPreset(server);
            mapSystem.CreateMap(out mapId);

            admittedShip = mapManager.CreateGridEntity(mapId).Owner;
            entManager.AddComponent<LuaMAiLogisticsShipComponent>(admittedShip).NextCycle = TimeSpan.FromDays(1);

            rejectedShip = mapManager.CreateGridEntity(mapId).Owner;
            entManager.AddComponent<LuaMAiLogisticsShipComponent>(rejectedShip).NextCycle = TimeSpan.FromDays(1);

            conflictingEntity = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(5f, 0f), mapId));
            entManager.AddComponent<LuaMAiBaseAnchorComponent>(conflictingEntity);
            entManager.AddComponent<LuaMAiSupplyDropComponent>(conflictingEntity);
        });

        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entManager.EntityExists(admittedShip), Is.True);
                Assert.That(entManager.EntityExists(rejectedShip), Is.False, "over-cap startup bypass left a ship grid behind");
                Assert.That(entManager.EntityExists(conflictingEntity), Is.False, "conflicting physical components survived admission");
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Ship, mapId), Is.EqualTo(1));
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Anchor, mapId), Is.Zero);
                Assert.That(budget.GetLiveCount(LuaMAiPhysicalEntityKind.Drop, mapId), Is.Zero);
                Assert.That(budget.GetRejectionCount(LuaMAiPhysicalEntityKind.Ship, "map_cap"), Is.GreaterThan(0));
                Assert.That(budget.GetRejectionCount(LuaMAiPhysicalEntityKind.Drop, "component_conflict"), Is.GreaterThan(0));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void ApplyEnabledPreset(RobustIntegrationTest.ServerIntegrationInstance server)
    {
        // Pool recycling flushes entities but does not represent a new game round. Reset the
        // deliberately persistent per-round spawn budget so every test starts in its own round;
        // toggling the feature CVar must not reset this budget in production.
        var entManager = server.ResolveDependency<IEntityManager>();
        entManager.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
        server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, false);
        server.CfgMan.SetCVar(CCVars.LuaMAiDirectorWorldPulseEnabled, false);
        server.CfgMan.SetCVar(CCVars.LuaMAiPhysicalBaseEnabled, true);
    }
}
