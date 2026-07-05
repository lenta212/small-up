using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server._LuaM.Sector;
using Content.Server._NF.BountyContracts;
using Content.Server._NF.Bank;
using Content.Server._NF.SectorServices;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared._LuaM.Sector;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.BountyContracts;
using Content.Shared._NF.Bank.Components;
using Content.Shared.GameTicking;
using Content.Shared.Interaction.Events;
using Content.Shared.MassMedia.Components;
using Content.Shared.Paper;
using Content.Shared.Pinpointer;
using Content.Shared.Radiation.Components;
using Content.Shared.Storage;
using Robust.Server.Console;
using Robust.Server.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMSectorStoryTest
{
    private static readonly ResPath SectorMemoryDirectory = new("/luam");
    private static readonly ResPath SectorMemoryPath = SectorMemoryDirectory / "sector_memory.json";
    private static readonly ResPath SectorMemoryBackupPath = SectorMemoryDirectory / "admin-test-backup.json";

    [Test]
    public async Task LuaMQuestPapersDoNotCreateGhostRolesAndInactiveMarkerMessagesAreClear()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var locMan = server.ResolveDependency<ILocalizationManager>();

        IReadOnlyList<string> offenders = [];
        var paperCount = 0;
        var closedText = string.Empty;
        var notRuntimeText = string.Empty;
        var differentActiveText = string.Empty;

        await server.WaitPost(() =>
        {
            var forbiddenComponents = new[]
            {
                "GhostRole",
                "GhostRoleMobSpawner",
                "GhostTakeoverAvailable",
                "TransferMindOnDespawn",
            };

            var luamPapers = prototypeManager
                .EnumeratePrototypes<EntityPrototype>()
                .Where(prototype =>
                    prototype.Components.ContainsKey("Paper") &&
                    (prototype.ID.StartsWith("PaperLuaM", StringComparison.Ordinal) ||
                     prototype.Components.ContainsKey("LuaMSectorEvidence")))
                .ToList();

            paperCount = luamPapers.Count;
            offenders = luamPapers
                .SelectMany(prototype => forbiddenComponents
                    .Where(prototype.Components.ContainsKey)
                    .Select(component => $"{prototype.ID}:{component}"))
                .ToList();

            closedText = locMan.GetString("luam-sector-terminal-marker-submit-closed", ("title", "Test task"));
            notRuntimeText = locMan.GetString("luam-sector-terminal-marker-submit-not-runtime", ("title", "Test task"));
            differentActiveText = locMan.GetString("luam-sector-terminal-marker-submit-different-active", ("title", "Test task"));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(paperCount, Is.GreaterThan(0));
            Assert.That(offenders, Is.Empty);
            Assert.That(closedText, Does.Contain("Test task"));
            Assert.That(notRuntimeText, Does.Contain("Test task"));
            Assert.That(differentActiveText, Does.Contain("Test task"));
            Assert.That(closedText, Does.Not.Contain("luam-sector-terminal-marker-submit-closed"));
            Assert.That(notRuntimeText, Does.Not.Contain("luam-sector-terminal-marker-submit-not-runtime"));
            Assert.That(differentActiveText, Does.Not.Contain("luam-sector-terminal-marker-submit-different-active"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AdminShipBuildCatalogIncludesShipyardVesselsAndRawShuttleMaps()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var director = entManager.System<LuaMSectorAiDirectorSystem>();

        await server.WaitAssertion(() =>
        {
            var presets = director.BuildAdminShipBuildPresets();

            Assert.That(presets.Any(entry => entry.GameMapId == "Baeg"), Is.True);
            Assert.That(presets.Any(entry => entry.GameMapId == "emergency_box"), Is.True);

            Assert.That(director.TryResolveAdminShipBuildId(
                "emergency_box",
                out var rawShipBuildId,
                out var rawDisplayName,
                out var rawError), Is.True, rawError);
            Assert.That(rawShipBuildId, Is.EqualTo("emergency_box"));
            Assert.That(rawDisplayName, Does.Contain("Emergency Box"));

            Assert.That(director.TryResolveAdminShipSpawnRequest(
                "создай шатл emergency_box рядом со мной",
                out var rawRequestedId,
                out _,
                out var rawRequestError), Is.True, rawRequestError);
            Assert.That(rawRequestError, Is.Empty);
            Assert.That(rawRequestedId, Is.EqualTo("emergency_box"));

            Assert.That(director.TryResolveAdminShipSpawnRequest(
                "создай шатл baeg рядом со мной",
                out var vesselRequestedId,
                out _,
                out var vesselRequestError), Is.True, vesselRequestError);
            Assert.That(vesselRequestError, Is.Empty);
            Assert.That(vesselRequestedId, Is.EqualTo("Baeg"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiBaseStatePersistsThroughJsonAndSnapshots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var director = entManager.System<LuaMSectorAiDirectorSystem>();

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            var aiBaseBeacon = entManager.SpawnEntity("LuaMAiBaseBeacon", MapCoordinates.Nullspace);
            Assert.That(entManager.HasComponent<LuaMAiBaseAnchorComponent>(aiBaseBeacon), Is.True);

            var aiShip = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(aiShip);
            logistics.Role = "hauler";
            logistics.VesselId = "Baeg";
            logistics.DisplayName = "Z-22 Baeg [Baeg]";
            Assert.That(entManager.HasComponent<LuaMAiLogisticsShipComponent>(aiShip), Is.True);

            var supplyDrop = entManager.SpawnEntity("LuaMAiSupplyDrop", MapCoordinates.Nullspace);
            Assert.That(entManager.HasComponent<LuaMAiSupplyDropComponent>(supplyDrop), Is.True);

            var miningDrone = entManager.SpawnEntity("LuaMAiMiningDrone", MapCoordinates.Nullspace);
            Assert.That(entManager.HasComponent<LuaMAiMiningDroneComponent>(miningDrone), Is.True);
        });

        await pair.RunTicksSync(10);

        LuaMSectorMemorySnapshot? snapshot = null;
        await server.WaitAssertion(() =>
        {
            var create = storySystem.EnsureAiBase("integration-test");
            Assert.That(create, Does.Contain("AI base deployed"));

            var hauler = storySystem.RecordAiBaseShipVisit(
                "integration-test",
                "hauler",
                "Baeg",
                "Z-22 Baeg [Baeg]");
            Assert.That(hauler, Does.Contain("AI base logistics updated"));
            Assert.That(hauler, Does.Contain("supply score"));

            var state = storySystem.GetAiBaseState();
            Assert.That(state.Created, Is.True);
            Assert.That(state.Inventory.Single(entry => entry.Resource == "ore").Amount, Is.GreaterThan(0));
            Assert.That(state.TradeLog, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(state.TradeCycles, Is.EqualTo(1));

            var status = storySystem.BuildAiBaseStatusText();
            Assert.That(status, Does.Contain("LuaM autonomous supply base"));
            Assert.That(status, Does.Contain("Needs:"));
            Assert.That(status, Does.Contain("Compensation plan:"));
            Assert.That(status, Does.Contain("Recent logistics:"));

            var compensationPlan = storySystem.BuildAiBaseCompensationPlan();
            Assert.That(compensationPlan, Is.Not.Empty);
            var topCompensation = compensationPlan.First(entry => entry.WeaknessId.StartsWith("resource-deficit:", StringComparison.Ordinal));
            Assert.That(topCompensation.Resource, Is.EqualTo("ore"));
            Assert.That(topCompensation.SuggestedRole, Is.EqualTo("miner"));
            Assert.That(topCompensation.SuggestedZone, Is.EqualTo("mining"));
            Assert.That(topCompensation.Compensation, Does.Contain("dispatch miner"));

            var adminState = director.BuildAdminState(string.Empty, string.Empty);
            Assert.That(adminState.AiBaseSummary, Does.Contain("physical beacons 1"));
            Assert.That(adminState.AiBaseSummary, Does.Contain("logistics ships 1"));
            Assert.That(adminState.AiBaseSummary, Does.Contain("drones 1"));
            Assert.That(adminState.AiBaseSummary, Does.Contain("supply drops 1"));
            Assert.That(adminState.AiBaseSummary, Does.Contain("compensate"));
            Assert.That(adminState.AiBaseDiagnostics, Does.Contain("AI base diagnostics"));
            Assert.That(adminState.AiBaseDiagnostics, Does.Contain("Suggested command:"));

            Assert.That(storySystem.TryExportMemoryJson(out var exportedJson), Is.True);
            Assert.That(exportedJson, Does.Contain("AiBase"));
            Assert.That(exportedJson, Does.Contain("fuel"));
            Assert.That(exportedJson, Does.Contain("TradeLog"));

            Assert.That(storySystem.TryExportMemorySnapshot(out snapshot), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            var trader = storySystem.RecordAiBaseShipVisit(
                "integration-test",
                "trader",
                "Hammerhead",
                "Hammerhead [Hammerhead]");
            Assert.That(trader, Does.Contain("trader ship"));
            Assert.That(storySystem.GetAiBaseState().TradeCycles, Is.EqualTo(2));

            Assert.That(storySystem.TryImportMemorySnapshot(snapshot!), Is.True);
            var restored = storySystem.GetAiBaseState();
            Assert.That(restored.Created, Is.True);
            Assert.That(restored.TradeCycles, Is.EqualTo(1));
            Assert.That(restored.TradeLog.Any(entry => entry.Vessel.Contains("Baeg", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(restored.TradeLog.Any(entry => entry.Vessel.Contains("Hammerhead", StringComparison.OrdinalIgnoreCase)), Is.False);

            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
            Assert.That(storySystem.GetAiBaseState().Created, Is.False);
            Assert.That(storySystem.BuildAiBaseStatusText(), Does.Contain("AI base is not deployed"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiLogisticsShipSystemCreatesVisibleSupplyDrop()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            var aiBaseBeacon = entManager.SpawnEntity("LuaMAiBaseBeacon", MapCoordinates.Nullspace);
            Assert.That(entManager.HasComponent<LuaMAiBaseAnchorComponent>(aiBaseBeacon), Is.True);

            var aiShip = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(aiShip);
            logistics.Role = "hauler";
            logistics.VesselId = "Baeg";
            logistics.DisplayName = "Z-22 Baeg [Baeg]";
            logistics.NextCycle = TimeSpan.FromTicks(1);

            Assert.That(storySystem.EnsureAiBase("integration-test"), Does.Contain("AI base deployed"));
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var state = storySystem.GetAiBaseState();
            Assert.That(state.TradeCycles, Is.GreaterThanOrEqualTo(1));

            var drops = 0;
            var deliveredAmount = 0;
            var deliveredResource = string.Empty;
            var query = entManager.EntityQueryEnumerator<LuaMAiSupplyDropComponent>();
            while (query.MoveNext(out _, out var drop))
            {
                drops++;
                deliveredAmount = Math.Max(deliveredAmount, drop.Amount);
                if (string.IsNullOrWhiteSpace(deliveredResource))
                    deliveredResource = drop.Resource;
            }

            Assert.That(drops, Is.GreaterThanOrEqualTo(1));
            Assert.That(deliveredResource, Is.Not.Empty);
            Assert.That(deliveredAmount, Is.GreaterThan(0));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiLogisticsShipSpawnsRoleManifestCrew()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();

        EntityUid aiShip = EntityUid.Invalid;
        EntityUid examinerUid = EntityUid.Invalid;
        EntityUid firstCrewDrone = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);

            aiShip = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            examinerUid = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(0.5f, 0.5f), mapId));
            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(aiShip);
            logistics.Role = "builder";
            logistics.VesselId = "Baeg";
            logistics.DisplayName = "Z-22 Baeg [Baeg]";
            logistics.NextCycle = TimeSpan.FromDays(1);

            Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("builder", "Baeg"),
                Is.EqualTo(new[] { "repair", "repair", "logistics", "guard" }));
            Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("miner", "Baeg"),
                Is.EqualTo(new[] { "miner", "miner", "logistics", "repair", "scout" }));
            var baegProfile = LuaMAiLogisticsShipSystem.BuildCrewProfile("builder", "Baeg");
            Assert.That(baegProfile.ProfileId, Is.EqualTo("builder:light-shuttle"));
            Assert.That(baegProfile.ManifestSource, Is.EqualTo("profile:builder:light-shuttle"));
            Assert.That(baegProfile.Summary, Does.Contain("repair-first"));
            Assert.That(baegProfile.StationPlan, Has.Count.EqualTo(4));
            Assert.That(baegProfile.StationPlan[0], Does.Contain("engineering/hull access"));
            var qjProfile = LuaMAiLogisticsShipSystem.BuildCrewProfile("miner", "QJ490");
            Assert.That(qjProfile.ProfileId, Is.EqualTo("miner:carrier"));
            Assert.That(qjProfile.Manifest, Is.EqualTo(new[] { "miner", "miner", "miner", "logistics", "repair", "guard" }));
            Assert.That(qjProfile.StationPlan.Any(station => station.Contains("hangar", StringComparison.OrdinalIgnoreCase)), Is.True);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var logistics = entManager.GetComponent<LuaMAiLogisticsShipComponent>(aiShip);
            Assert.That(logistics.CrewRoleManifest, Is.EqualTo(new[] { "repair", "repair", "logistics", "guard" }));
            Assert.That(logistics.CrewManifestSource, Is.EqualTo("profile:builder:light-shuttle"));
            Assert.That(logistics.CrewProfileId, Is.EqualTo("builder:light-shuttle"));
            Assert.That(logistics.CrewProfileSummary, Does.Contain("repair-first"));
            Assert.That(logistics.CrewStationPlan, Has.Count.EqualTo(4));
            Assert.That(logistics.CrewStationPlan[0], Does.Contain("repair:"));
            Assert.That(logistics.LastCrewReport, Does.Contain("AI ship crew active 4/4"));
            Assert.That(logistics.LastCrewReport, Does.Contain("profile builder:light-shuttle"));
            Assert.That(logistics.CrewSpawnAttempts, Is.EqualTo(4));

            var roleCounts = new Dictionary<string, int>();
            var prototypeCounts = new Dictionary<string, int>();
            var droneCount = 0;
            firstCrewDrone = EntityUid.Invalid;
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out var droneUid, out var drone))
            {
                if (drone.ParentShip != aiShip)
                    continue;

                if (!firstCrewDrone.IsValid())
                    firstCrewDrone = droneUid;

                droneCount++;
                roleCounts[drone.DroneRole] = roleCounts.GetValueOrDefault(drone.DroneRole) + 1;
                var dronePrototype = entManager.GetComponent<MetaDataComponent>(droneUid).EntityPrototype?.ID ?? string.Empty;
                prototypeCounts[dronePrototype] = prototypeCounts.GetValueOrDefault(dronePrototype) + 1;
                Assert.That(drone.VesselId, Is.EqualTo("Baeg"));
                Assert.That(drone.DisplayName, Is.EqualTo("Z-22 Baeg [Baeg]"));
                Assert.That(drone.CrewAssignment, Does.Contain("builder ship crew"));
                Assert.That(drone.CrewStation, Is.Not.Empty);
                Assert.That(dronePrototype, Is.EqualTo(drone.DroneRole switch
                {
                    "repair" => "LuaMAiRepairDrone",
                    "logistics" => "LuaMAiLogisticsDrone",
                    "guard" => "LuaMAiGuardDrone",
                    _ => "LuaMAiMiningDrone",
                }));
                if (drone.DroneRole == "repair")
                    Assert.That(drone.CrewStation, Does.Contain("engineering/hull access"));
                Assert.That(drone.CrewDirective, Does.Contain("Z-22 Baeg [Baeg]"));
                Assert.That(drone.CrewPriority, Is.GreaterThan(0));
                Assert.That(drone.HasCrewHome, Is.True);
                Assert.That(drone.LastCrewHomeAction, Is.EqualTo("home_station_assigned"));
                Assert.That(drone.OreCycles, Is.EqualTo(0));
                Assert.That(entManager.GetComponent<MetaDataComponent>(droneUid).EntityName, Does.Contain(drone.DroneRole));

                var droneTransform = entManager.GetComponent<TransformComponent>(droneUid);
                var distance = Vector2.Distance(
                    entManager.GetComponent<TransformComponent>(aiShip).MapPosition.Position,
                    droneTransform.MapPosition.Position);
                Assert.That(Vector2.Distance(drone.CrewHomeLocalPosition, droneTransform.LocalPosition), Is.LessThan(0.05f));
                Assert.That(distance, Is.LessThan(6f));
            }

            Assert.That(droneCount, Is.EqualTo(4));
            Assert.That(roleCounts.GetValueOrDefault("repair"), Is.EqualTo(2));
            Assert.That(roleCounts.GetValueOrDefault("logistics"), Is.EqualTo(1));
            Assert.That(roleCounts.GetValueOrDefault("guard"), Is.EqualTo(1));
            Assert.That(roleCounts.ContainsKey("miner"), Is.False);
            Assert.That(prototypeCounts.GetValueOrDefault("LuaMAiRepairDrone"), Is.EqualTo(2));
            Assert.That(prototypeCounts.GetValueOrDefault("LuaMAiLogisticsDrone"), Is.EqualTo(1));
            Assert.That(prototypeCounts.GetValueOrDefault("LuaMAiGuardDrone"), Is.EqualTo(1));
            Assert.That(prototypeCounts.ContainsKey("LuaMAiMiningDrone"), Is.False);

            var crewText = Examine(firstCrewDrone);
            Assert.That(crewText, Does.Contain("Ship crew:").Or.Contain("Экипаж корабля"));
            Assert.That(crewText, Does.Contain("Z-22 Baeg"));
            Assert.That(crewText, Does.Contain("builder ship crew"));
            Assert.That(crewText, Does.Contain("station").Or.Contain("пост"));
            Assert.That(crewText, Does.Contain("order").Or.Contain("приказ"));

            var shipText = Examine(aiShip);
            Assert.That(shipText, Does.Contain("AI ship:").Or.Contain("Корабль ИИ"));
            Assert.That(shipText, Does.Contain("Z-22 Baeg"));
            Assert.That(shipText, Does.Contain("Crew manifest").Or.Contain("Манифест экипажа"));
            Assert.That(shipText, Does.Contain("Crew profile").Or.Contain("Профиль экипажа"));
            Assert.That(shipText, Does.Contain("builder:light-shuttle"));
            Assert.That(shipText, Does.Contain("engineering/hull access"));
            Assert.That(shipText, Does.Contain("4/4"));
            Assert.That(shipText, Does.Contain("repair").Or.Contain("ремонт"));
            Assert.That(shipText, Does.Contain("logistics").Or.Contain("логистика"));
            Assert.That(shipText, Does.Contain("guard").Or.Contain("охрана"));
            Assert.That(shipText, Does.Contain("AI ship crew active 4/4"));
        });

        await server.WaitPost(() =>
        {
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out _, out var drone))
            {
                if (drone.ParentShip == aiShip)
                    drone.NextMine = TimeSpan.FromTicks(1);
            }
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out _, out var drone))
            {
                if (drone.ParentShip == aiShip)
                    Assert.That(drone.OreCycles, Is.EqualTo(0));
            }
        });

        await server.WaitPost(() =>
        {
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out _, out var drone))
            {
                if (drone.ParentShip != aiShip)
                    continue;

                drone.NextCrewDuty = TimeSpan.FromTicks(1);
                drone.NextSocialScan = TimeSpan.FromDays(1);
            }
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var logistics = entManager.GetComponent<LuaMAiLogisticsShipComponent>(aiShip);
            Assert.That(logistics.CrewDutyCycles, Is.EqualTo(4));
            Assert.That(logistics.CrewDutyRoleCycles.GetValueOrDefault("repair"), Is.EqualTo(2));
            Assert.That(logistics.CrewDutyRoleCycles.GetValueOrDefault("logistics"), Is.EqualTo(1));
            Assert.That(logistics.CrewDutyRoleCycles.GetValueOrDefault("guard"), Is.EqualTo(1));
            Assert.That(logistics.LastCrewDutyReport, Does.Contain("cycle 4"));
            Assert.That(logistics.LastCrewDutyReport, Does.Contain("Z-22 Baeg"));

            var droneCount = 0;
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out _, out var drone))
            {
                if (drone.ParentShip != aiShip)
                    continue;

                droneCount++;
                Assert.That(drone.CrewDutyCycles, Is.EqualTo(1));
                Assert.That(drone.LastCrewDutyEffect, Is.Not.Empty);
                Assert.That(drone.LastCrewDutyReport, Does.Contain(drone.DroneId));
                Assert.That(drone.LastCrewDutyReport, Does.Contain(drone.CrewStation));
                Assert.That(drone.LastCrewDutyReport, Does.Contain("Z-22 Baeg"));
                Assert.That(drone.LastCrewDutyTraceUid.IsValid(), Is.True);
                Assert.That(entManager.EntityExists(drone.LastCrewDutyTraceUid), Is.True);

                if (drone.DroneRole == "repair")
                    Assert.That(drone.LastCrewDutyReport, Does.Contain("hull access stable"));
                else if (drone.DroneRole == "logistics")
                    Assert.That(drone.LastCrewDutyReport, Does.Contain("supply route balanced"));
                else if (drone.DroneRole == "guard")
                    Assert.That(drone.LastCrewDutyReport, Does.Contain("perimeter clear"));
            }

            Assert.That(droneCount, Is.EqualTo(4));

            var dutyTraceCount = 0;
            EntityUid firstDutyTrace = EntityUid.Invalid;
            var traceQuery = entManager.EntityQueryEnumerator<LuaMAiDroneTraceComponent>();
            while (traceQuery.MoveNext(out var traceUid, out var trace))
            {
                if (!trace.Summary.Contains("duty #1", StringComparison.Ordinal))
                    continue;

                dutyTraceCount++;
                if (!firstDutyTrace.IsValid())
                    firstDutyTrace = traceUid;

                Assert.That(trace.TargetName, Is.EqualTo("Z-22 Baeg [Baeg]"));
                Assert.That(trace.TraceKind, Is.EqualTo("ship_duty"));
                Assert.That(trace.WorkStation, Is.Not.Empty);
                Assert.That(trace.WorkEffect, Is.EqualTo(trace.ContactTone));
                Assert.That(trace.ContactCount, Is.EqualTo(1));
                Assert.That(trace.ContactTone, Is.Not.Empty);
                Assert.That(trace.Summary, Does.Contain(trace.DroneId));
                Assert.That(trace.Summary, Does.Contain("Z-22 Baeg"));

                var traceXform = entManager.GetComponent<TransformComponent>(traceUid);
                Assert.That(traceXform.ParentUid == aiShip || traceXform.GridUid == aiShip, Is.True);
            }

            Assert.That(dutyTraceCount, Is.EqualTo(4));

            var crewText = Examine(firstCrewDrone);
            Assert.That(crewText, Does.Contain("Ship duty").Or.Contain("Вахта корабля"));
            Assert.That(crewText, Does.Contain("duty #1"));

            var traceText = Examine(firstDutyTrace);
            Assert.That(traceText, Does.Contain("AI ship duty trace").Or.Contain("Вахтовый след ИИ"));
            Assert.That(traceText, Does.Contain("station").Or.Contain("пост"));
            Assert.That(traceText, Does.Contain("duty #1"));
            Assert.That(traceText, Does.Contain("Z-22 Baeg"));

            var shipText = Examine(aiShip);
            Assert.That(shipText, Does.Contain("Last crew duty").Or.Contain("Последняя вахта"));
            Assert.That(shipText, Does.Contain("cycle 4"));
        });

        await pair.CleanReturnAsync();

        string Examine(EntityUid target)
        {
            var message = new FormattedMessage();
            var ev = new ExaminedEvent(message, target, examinerUid, isInDetailsRange: true, hasDescription: false);
            entManager.EventBus.RaiseLocalEvent(target, ev);
            return ev.GetTotalMessage().ToMarkup();
        }
    }

    [Test]
    public async Task AiLogisticsShipUsesMappedCrewMarkers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entManager.System<MapSystem>();

        EntityUid aiShip = EntityUid.Invalid;
        var markerUids = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);
            var grid = mapManager.CreateGridEntity(mapId);
            aiShip = grid.Owner;
            mapSystem.SetTiles(grid.Owner, grid.Comp, new List<(Vector2i Index, Tile Tile)>
            {
                new(new Vector2i(0, 0), new Tile(1)),
                new(new Vector2i(1, 0), new Tile(1)),
                new(new Vector2i(0, 1), new Tile(1)),
                new(new Vector2i(1, 1), new Tile(1)),
            });
            markerUids.Add(SpawnCrewMarker(aiShip, "repair", "repair-a", new Vector2(0.5f, 0.5f)));
            markerUids.Add(SpawnCrewMarker(aiShip, "repair", "repair-b", new Vector2(1.5f, 0.5f)));
            markerUids.Add(SpawnCrewMarker(aiShip, "logistics", "cargo-a", new Vector2(0.5f, 1.5f)));
            markerUids.Add(SpawnCrewMarker(aiShip, "guard", "guard-a", new Vector2(1.5f, 1.5f)));
            foreach (var markerUid in markerUids)
            {
                var markerXform = entManager.GetComponent<TransformComponent>(markerUid);
                Assert.That(
                    markerXform.GridUid == aiShip || markerXform.ParentUid == aiShip,
                    Is.True,
                    $"marker {markerUid} parent={markerXform.ParentUid} grid={markerXform.GridUid}");
            }

            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(aiShip);
            logistics.Role = "builder";
            logistics.VesselId = "Baeg";
            logistics.DisplayName = "Z-22 Baeg [Baeg]";
            logistics.NextCycle = TimeSpan.FromDays(1);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var logistics = entManager.GetComponent<LuaMAiLogisticsShipComponent>(aiShip);
            Assert.That(logistics.LastCrewReport, Does.Contain("markers 4/4"));
            Assert.That(logistics.MarkerCrewSpawns, Is.EqualTo(4));
            Assert.That(logistics.CrewSpawnAttempts, Is.EqualTo(4));

            foreach (var markerUid in markerUids)
            {
                var marker = entManager.GetComponent<LuaMAiShipCrewMarkerComponent>(markerUid);
                Assert.That(marker.LastSpawnedDrone.IsValid(), Is.True);
                Assert.That(entManager.EntityExists(marker.LastSpawnedDrone), Is.True);

                var markerXform = entManager.GetComponent<TransformComponent>(markerUid);
                var droneXform = entManager.GetComponent<TransformComponent>(marker.LastSpawnedDrone);
                var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(marker.LastSpawnedDrone);

                Assert.That(drone.ParentShip, Is.EqualTo(aiShip));
                Assert.That(drone.DroneRole, Is.EqualTo(marker.Role));
                Assert.That(droneXform.GridUid, Is.EqualTo(aiShip));
                Assert.That(Vector2.Distance(markerXform.LocalPosition, droneXform.LocalPosition), Is.LessThan(0.05f));
            }
        });

        await pair.CleanReturnAsync();

        EntityUid SpawnCrewMarker(EntityUid gridUid, string role, string label, Vector2 position)
        {
            var uid = entManager.SpawnEntity("LuaMAiShipCrewMarker", new EntityCoordinates(gridUid, position));
            var marker = entManager.EnsureComponent<LuaMAiShipCrewMarkerComponent>(uid);
            marker.Role = role;
            marker.Label = label;
            marker.Priority = 10;
            return uid;
        }
    }

    [Test]
    public async Task AiLogisticsShipWithoutMarkersUsesInternalGridTiles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entManager.System<MapSystem>();

        EntityUid aiShip = EntityUid.Invalid;
        var homePositions = new Dictionary<EntityUid, Vector2>();

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);
            var grid = mapManager.CreateGridEntity(mapId);
            aiShip = grid.Owner;
            mapSystem.SetTiles(grid.Owner, grid.Comp, new List<(Vector2i Index, Tile Tile)>
            {
                new(new Vector2i(0, 0), new Tile(1)),
                new(new Vector2i(1, 0), new Tile(1)),
                new(new Vector2i(0, 1), new Tile(1)),
                new(new Vector2i(1, 1), new Tile(1)),
                new(new Vector2i(2, 1), new Tile(1)),
            });

            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(aiShip);
            logistics.Role = "miner";
            logistics.VesselId = "Baeg";
            logistics.DisplayName = "Z-22 Baeg [Baeg]";
            logistics.NextCycle = TimeSpan.FromDays(1);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var logistics = entManager.GetComponent<LuaMAiLogisticsShipComponent>(aiShip);
            Assert.That(logistics.LastCrewReport, Does.Contain("grid fallback active 5/5"));
            Assert.That(logistics.GridCrewSpawns, Is.EqualTo(5));
            Assert.That(logistics.MarkerCrewSpawns, Is.EqualTo(0));

            var droneCount = 0;
            var localPositions = new HashSet<Vector2>();
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out var droneUid, out var drone))
            {
                if (drone.ParentShip != aiShip)
                    continue;

                droneCount++;
                var xform = entManager.GetComponent<TransformComponent>(droneUid);
                Assert.That(xform.GridUid, Is.EqualTo(aiShip));
                Assert.That(localPositions.Add(xform.LocalPosition), Is.True);
                Assert.That(drone.HasCrewHome, Is.True);
                Assert.That(Vector2.Distance(drone.CrewHomeLocalPosition, xform.LocalPosition), Is.LessThan(0.05f));
                homePositions[droneUid] = drone.CrewHomeLocalPosition;
            }

            Assert.That(droneCount, Is.EqualTo(5));
        });

        await server.WaitPost(() =>
        {
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out _, out var drone))
            {
                if (drone.ParentShip != aiShip)
                    continue;

                drone.NextMove = TimeSpan.FromTicks(1);
                drone.NextSocialScan = TimeSpan.FromDays(1);
            }
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var droneCount = 0;
            var query = entManager.EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
            while (query.MoveNext(out var droneUid, out var drone))
            {
                if (drone.ParentShip != aiShip)
                    continue;

                droneCount++;
                var xform = entManager.GetComponent<TransformComponent>(droneUid);
                Assert.That(xform.GridUid, Is.EqualTo(aiShip));
                Assert.That(homePositions.TryGetValue(droneUid, out var homePosition), Is.True);
                Assert.That(drone.HasCrewHome, Is.True);
                Assert.That(drone.CrewHomeLocalPosition, Is.EqualTo(homePosition));
                Assert.That(Vector2.Distance(xform.LocalPosition, homePosition), Is.LessThan(1.5f));
                Assert.That(drone.LastCrewHomeAction, Is.EqualTo("station_patrol"));
                Assert.That(drone.State, Is.EqualTo("ship_station_patrol"));
            }

            Assert.That(droneCount, Is.EqualTo(5));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiMiningDroneMovesAndDeliversOreToAiBase()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<MapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();

        EntityUid droneUid = EntityUid.Invalid;
        Vector2 startPosition = Vector2.Zero;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out var mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            var aiBaseBeacon = entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(new Vector2(2, 0), mapId));
            Assert.That(entManager.HasComponent<LuaMAiBaseAnchorComponent>(aiBaseBeacon), Is.True);

            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(new Vector2(4, 0), mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.VesselId = "Baeg";
            drone.DisplayName = "Z-22 Baeg [Baeg]";
            drone.DroneId = "test-miner";
            drone.NextMove = TimeSpan.FromTicks(1);
            drone.NextMine = TimeSpan.FromTicks(1);
            startPosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;

            Assert.That(storySystem.EnsureAiBase("integration-test"), Does.Contain("AI base deployed"));
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var state = storySystem.GetAiBaseState();
            Assert.That(state.Inventory.Single(entry => entry.Resource == "ore").Amount, Is.GreaterThan(0));
            Assert.That(state.TradeLog.Any(entry => entry.Role == "miner" && entry.Resource == "ore"), Is.True);

            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.OreCycles, Is.GreaterThanOrEqualTo(1));
            Assert.That(drone.LastOreAmount, Is.GreaterThan(0));
            Assert.That(drone.State, Is.EqualTo("mining"));

            var currentPosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;
            Assert.That(Vector2.Distance(startPosition, currentPosition), Is.GreaterThan(0.01f));

            var drops = 0;
            var query = entManager.EntityQueryEnumerator<LuaMAiSupplyDropComponent>();
            while (query.MoveNext(out _, out var drop))
            {
                if (drop.Resource == "ore")
                    drops++;
            }

            Assert.That(drops, Is.GreaterThanOrEqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiMiningDroneReactsToNearbyPeople()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var metaData = entManager.System<MetaDataSystem>();

        EntityUid droneUid = EntityUid.Invalid;
        Vector2 startPosition = Vector2.Zero;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);

            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(Vector2.Zero, mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneId = "social-test";
            drone.NextMove = TimeSpan.FromDays(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromTicks(1);
            drone.NextSpeech = TimeSpan.FromTicks(1);
            startPosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;

            var crew = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(2.4f, 0), mapId));
            entManager.AddComponent<MobStateComponent>(crew);
            metaData.SetEntityName(crew, "LuaM test technician");
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.SocialScans, Is.GreaterThanOrEqualTo(1));
            Assert.That(drone.PeopleSeen, Is.GreaterThanOrEqualTo(1));
            Assert.That(drone.SocialPings, Is.GreaterThanOrEqualTo(1));
            Assert.That(drone.State, Is.EqualTo("observing"));
            Assert.That(drone.LastSeenName, Does.Contain("LuaM test technician"));
            Assert.That(drone.LastLine, Does.Contain("LuaM test technician"));

            var currentPosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;
            Assert.That(Vector2.Distance(startPosition, currentPosition), Is.GreaterThan(0.01f));

            var light = entManager.GetComponent<PointLightComponent>(droneUid);
            Assert.That(light.Radius, Is.GreaterThan(1.4f));
            Assert.That(light.Energy, Is.GreaterThan(0.75f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiMiningDroneWarnsAndBacksOffWhenPeopleAreTooClose()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var metaData = entManager.System<MetaDataSystem>();

        EntityUid droneUid = EntityUid.Invalid;
        EntityUid crewUid = EntityUid.Invalid;
        var startDistance = 0f;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);

            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(Vector2.Zero, mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneId = "warning-test";
            drone.NextMove = TimeSpan.FromDays(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromTicks(1);
            drone.NextSpeech = TimeSpan.FromTicks(1);

            crewUid = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(0.7f, 0), mapId));
            entManager.AddComponent<MobStateComponent>(crewUid);
            metaData.SetEntityName(crewUid, "LuaM close technician");

            var dronePosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;
            var crewPosition = entManager.GetComponent<TransformComponent>(crewUid).MapPosition.Position;
            startDistance = Vector2.Distance(dronePosition, crewPosition);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.State, Is.EqualTo("warning"));
            Assert.That(drone.SocialPings, Is.GreaterThanOrEqualTo(1));
            Assert.That(drone.LastSeenName, Does.Contain("LuaM close technician"));
            Assert.That(drone.LastLine, Does.Contain("LuaM close technician"));

            var dronePosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;
            var crewPosition = entManager.GetComponent<TransformComponent>(crewUid).MapPosition.Position;
            Assert.That(Vector2.Distance(dronePosition, crewPosition), Is.GreaterThan(startDistance));

            var light = entManager.GetComponent<PointLightComponent>(droneUid);
            Assert.That(light.Radius, Is.GreaterThanOrEqualTo(3.0f));
            Assert.That(light.Energy, Is.GreaterThanOrEqualTo(1.65f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiMiningDroneRolesProduceDifferentSocialStates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var metaData = entManager.System<MetaDataSystem>();

        EntityUid guardDrone = EntityUid.Invalid;
        EntityUid scoutDrone = EntityUid.Invalid;
        EntityUid logisticsDrone = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);

            var crew = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            entManager.AddComponent<MobStateComponent>(crew);
            metaData.SetEntityName(crew, "LuaM role technician");

            guardDrone = SpawnRoleDrone("guard", new Vector2(-2.5f, 0), mapId);
            scoutDrone = SpawnRoleDrone("scout", new Vector2(0, 2.5f), mapId);
            logisticsDrone = SpawnRoleDrone("logistics", new Vector2(2.5f, 0), mapId);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            AssertRoleState(guardDrone, "guard", "screening", "security_screen", "guard:contact");
            AssertRoleState(scoutDrone, "scout", "scanning", "route_mapping", "scout:contact");
            AssertRoleState(logisticsDrone, "logistics", "guiding", "supply_route_hint", "logistics:contact");
        });

        await pair.CleanReturnAsync();

        EntityUid SpawnRoleDrone(string role, Vector2 position, MapId mapId)
        {
            var uid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(position, mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(uid);
            drone.DroneRole = role;
            drone.DroneId = $"{role}-test";
            drone.NextMove = TimeSpan.FromDays(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromTicks(1);
            drone.NextSpeech = TimeSpan.FromTicks(1);
            return uid;
        }

        void AssertRoleState(EntityUid uid, string role, string state, string action, string lightMode)
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(uid);
            Assert.That(drone.DroneRole, Is.EqualTo(role));
            Assert.That(drone.State, Is.EqualTo(state));
            Assert.That(drone.LastSocialAction, Is.EqualTo(action));
            Assert.That(drone.LastLightMode, Is.EqualTo(lightMode));
            Assert.That(drone.LastSeenName, Does.Contain("LuaM role technician"));
            Assert.That(drone.LastLine, Does.Contain("LuaM role technician"));
        }
    }

    [Test]
    public async Task AiMiningDroneLeavesWorldTraceWithoutSpam()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var metaData = entManager.System<MetaDataSystem>();

        EntityUid droneUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);

            var crew = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(2.4f, 0), mapId));
            entManager.AddComponent<MobStateComponent>(crew);
            metaData.SetEntityName(crew, "LuaM trace technician");

            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(Vector2.Zero, mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneRole = "scout";
            drone.DroneId = "trace-test";
            drone.NextMove = TimeSpan.FromDays(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromTicks(1);
            drone.NextSpeech = TimeSpan.FromDays(1);
            drone.NextWorldTrace = TimeSpan.FromTicks(1);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.WorldTraces, Is.EqualTo(1));
            Assert.That(drone.LastWorldTraceUid.IsValid(), Is.True);
            Assert.That(drone.LastWorldTraceSummary, Does.Contain("LuaM trace technician"));
            Assert.That(drone.LastContactCount, Is.EqualTo(1));
            Assert.That(drone.LastContactTone, Is.EqualTo("first_contact"));
            Assert.That(drone.UniquePeopleSeen, Is.EqualTo(1));
            Assert.That(drone.ContactMemory[drone.LastContactKey], Is.EqualTo(1));

            var traces = 0;
            var traceQuery = entManager.EntityQueryEnumerator<LuaMAiDroneTraceComponent>();
            while (traceQuery.MoveNext(out var uid, out var trace))
            {
                traces++;
                Assert.That(uid, Is.EqualTo(drone.LastWorldTraceUid));
                Assert.That(trace.DroneId, Is.EqualTo("trace-test"));
                Assert.That(trace.DroneRole, Is.EqualTo("scout"));
                Assert.That(trace.TraceKind, Is.EqualTo("social_contact"));
                Assert.That(trace.TargetName, Does.Contain("LuaM trace technician"));
                Assert.That(trace.WorkEffect, Is.EqualTo("first_contact"));
                Assert.That(trace.Summary, Does.Contain("LuaM trace technician"));
                Assert.That(trace.CloseContact, Is.False);
                Assert.That(trace.ContactCount, Is.EqualTo(1));
                Assert.That(trace.ContactTone, Is.EqualTo("first_contact"));

                var light = entManager.GetComponent<PointLightComponent>(uid);
                Assert.That(light.Radius, Is.GreaterThan(1.55f));
                Assert.That(light.Energy, Is.GreaterThan(0.85f));
            }

            Assert.That(traces, Is.EqualTo(1));
        });

        await pair.RunTicksSync(20);

        await server.WaitAssertion(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.WorldTraces, Is.EqualTo(1));

            var traces = 0;
            var traceQuery = entManager.EntityQueryEnumerator<LuaMAiDroneTraceComponent>();
            while (traceQuery.MoveNext(out _, out _))
            {
                traces++;
            }

            Assert.That(traces, Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiMiningDroneRemembersRepeatedHumanContacts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var metaData = entManager.System<MetaDataSystem>();

        EntityUid droneUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);

            var crew = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(2.4f, 0), mapId));
            entManager.AddComponent<MobStateComponent>(crew);
            metaData.SetEntityName(crew, "LuaM remembered technician");

            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(Vector2.Zero, mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneRole = "logistics";
            drone.DroneId = "memory-test";
            drone.NextMove = TimeSpan.FromDays(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromTicks(1);
            drone.NextSpeech = TimeSpan.FromTicks(1);
            drone.NextWorldTrace = TimeSpan.FromDays(1);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.LastSeenName, Does.Contain("LuaM remembered technician"));
            Assert.That(drone.LastLine, Does.Contain("LuaM remembered technician"));
            Assert.That(drone.LastContactCount, Is.EqualTo(1));
            Assert.That(drone.LastContactTone, Is.EqualTo("first_contact"));
            Assert.That(drone.UniquePeopleSeen, Is.EqualTo(1));
            Assert.That(drone.ContactMemory[drone.LastContactKey], Is.EqualTo(1));
        });

        await server.WaitPost(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.NextSocialScan = TimeSpan.FromTicks(1);
            drone.NextSpeech = TimeSpan.FromTicks(1);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var drone = entManager.GetComponent<LuaMAiMiningDroneComponent>(droneUid);
            Assert.That(drone.LastSeenName, Does.Contain("LuaM remembered technician"));
            Assert.That(drone.LastLine, Does.Contain("LuaM remembered technician"));
            Assert.That(drone.LastContactCount, Is.EqualTo(2));
            Assert.That(drone.LastContactTone, Is.EqualTo("repeat_contact"));
            Assert.That(drone.UniquePeopleSeen, Is.EqualTo(1));
            Assert.That(drone.ContactMemory[drone.LastContactKey], Is.EqualTo(2));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiBaseCreatesWorkZonesAndAssignsDroneTasks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<MapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();

        EntityUid droneUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out var mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(-8f, -8f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            Assert.That(storySystem.EnsureAiBase("zone-task-test"),
                Does.Contain("AI base deployed").Or.Contain("AI base already exists"));

            var anchor = entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(Vector2.Zero, mapId));
            Assert.That(entManager.HasComponent<LuaMAiBaseAnchorComponent>(anchor), Is.True);

            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(new Vector2(7f, 0f), mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneRole = "logistics";
            drone.DroneId = "zone-task-test";
            drone.NextMove = TimeSpan.FromTicks(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromDays(1);
        });

        await pair.RunTicksSync(20);

        await server.WaitAssertion(() =>
        {
            var zones = new Dictionary<string, EntityUid>();
            var zoneQuery = entManager.EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
            while (zoneQuery.MoveNext(out var uid, out var zone))
            {
                zones[zone.ZoneType] = uid;
            }

            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneDock));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneStorage));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneMining));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZonePatrol));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneContact));

            var miningZone = entManager.GetComponent<LuaMAiBaseZoneComponent>(zones[LuaMAiBaseEcologySystem.ZoneMining]);
            Assert.That(miningZone.ActiveWeaknessId, Is.EqualTo("resource-deficit:ore"));
            Assert.That(miningZone.SuggestedRole, Is.EqualTo("miner"));
            Assert.That(miningZone.SuggestedResource, Is.EqualTo("ore"));
            Assert.That(miningZone.ActiveCompensation, Does.Contain("dispatch miner"));

            var storageZone = entManager.GetComponent<LuaMAiBaseZoneComponent>(zones[LuaMAiBaseEcologySystem.ZoneStorage]);
            Assert.That(storageZone.ActiveWeaknessId, Is.Not.Empty);
            Assert.That(storageZone.ActiveWeaknessTitle, Is.Not.Empty);
            Assert.That(storageZone.ActiveCompensation, Is.Not.Empty);
            Assert.That(storageZone.SuggestedRole, Is.Not.Empty);
            Assert.That(storageZone.ActiveCompensationSeverity, Is.GreaterThan(0));

            var task = entManager.GetComponent<LuaMAiDroneTaskComponent>(droneUid);
            Assert.That(task.TaskType, Is.EqualTo("supply_run"));
            Assert.That(task.ZoneType, Is.EqualTo(LuaMAiBaseEcologySystem.ZoneStorage));
            Assert.That(task.TargetZone, Is.EqualTo(zones[LuaMAiBaseEcologySystem.ZoneStorage]));
            Assert.That(task.TaskStage, Is.AnyOf("moving", "working", "reporting"));
            Assert.That(task.WeaknessId, Is.Not.Empty);
            Assert.That(task.WeaknessTitle, Is.Not.Empty);
            Assert.That(task.Compensation, Is.Not.Empty);
            Assert.That(task.CompensationRole, Is.Not.Empty);
            Assert.That(task.CompensationSeverity, Is.GreaterThan(0));

            var dronePosition = entManager.GetComponent<TransformComponent>(droneUid).MapPosition.Position;
            var zonePosition = entManager.GetComponent<TransformComponent>(task.TargetZone).MapPosition.Position;
            Assert.That(Vector2.Distance(dronePosition, zonePosition), Is.LessThan(9.8f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiBaseRoleDronesProduceDifferentTaskContributions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<MapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();

        var droneUids = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out var mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(-8f, -8f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            Assert.That(storySystem.EnsureAiBase("role-task-test"),
                Does.Contain("AI base deployed").Or.Contain("AI base already exists"));

            entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(Vector2.Zero, mapId));
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var zones = CollectZones();
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneDock));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneStorage));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZonePatrol));
            Assert.That(zones.Keys, Does.Contain(LuaMAiBaseEcologySystem.ZoneContact));
        });

        await server.WaitPost(() =>
        {
            var zones = CollectZones();
            SpawnRoleDrone("repair", "repair-task-test", zones[LuaMAiBaseEcologySystem.ZoneDock]);
            SpawnRoleDrone("logistics", "logistics-task-test", zones[LuaMAiBaseEcologySystem.ZoneStorage]);
            SpawnRoleDrone("guard", "guard-task-test", zones[LuaMAiBaseEcologySystem.ZonePatrol]);
            SpawnRoleDrone("scout", "scout-task-test", zones[LuaMAiBaseEcologySystem.ZoneContact]);
            SpawnRoleDrone("medic", "medic-task-test", zones[LuaMAiBaseEcologySystem.ZoneContact]);
            SpawnRoleDrone("service", "service-task-test", zones[LuaMAiBaseEcologySystem.ZoneContact]);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            foreach (var droneUid in droneUids)
            {
                var task = entManager.GetComponent<LuaMAiDroneTaskComponent>(droneUid);
                Assert.That(task.CompletedCycles, Is.GreaterThanOrEqualTo(1));
                Assert.That(task.BaseContributionCycles, Is.EqualTo(1));
                Assert.That(task.LastContributionReport, Does.Contain("AI base drone task updated"));
                Assert.That(task.LastReport, Does.Contain(task.LastContributionReport));
            }

            var state = storySystem.GetAiBaseState();
            Assert.That(state.TradeLog.Any(entry => entry.Role == "repair" && entry.Resource == "hull-parts"), Is.True);
            Assert.That(state.TradeLog.Any(entry => entry.Role == "logistics"), Is.True);
            Assert.That(state.TradeLog.Any(entry => entry.Role == "guard" && entry.Resource == "security"), Is.True);
            Assert.That(state.TradeLog.Any(entry => entry.Role == "scout" && entry.Resource == "route-data"), Is.True);
            Assert.That(state.TradeLog.Any(entry => entry.Role == "medic" && entry.Resource == "medicine"), Is.True);
            Assert.That(state.TradeLog.Any(entry => entry.Role == "service" && entry.Resource == "food"), Is.True);
            Assert.That(state.Inventory.Single(entry => entry.Resource == "hull-parts").Amount, Is.GreaterThan(10));
            Assert.That(state.Inventory.Single(entry => entry.Resource == "medicine").Amount, Is.GreaterThan(5));
            Assert.That(state.Inventory.Single(entry => entry.Resource == "food").Amount, Is.GreaterThan(12));
            Assert.That(state.Inventory.Any(entry => entry.Resource == "security" && entry.Amount > 0), Is.True);
            Assert.That(state.Inventory.Any(entry => entry.Resource == "route-data" && entry.Amount > 0), Is.True);
            Assert.That(state.TradeCycles, Is.GreaterThanOrEqualTo(6));
        });

        await pair.CleanReturnAsync();

        Dictionary<string, EntityUid> CollectZones()
        {
            var zones = new Dictionary<string, EntityUid>();
            var zoneQuery = entManager.EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
            while (zoneQuery.MoveNext(out var uid, out var zone))
            {
                zones[zone.ZoneType] = uid;
            }

            return zones;
        }

        void SpawnRoleDrone(string role, string id, EntityUid zoneUid)
        {
            var zoneCoordinates = entManager.GetComponent<TransformComponent>(zoneUid).MapPosition;
            var uid = entManager.SpawnEntity("LuaMAiMiningDrone", zoneCoordinates);
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(uid);
            drone.DroneRole = role;
            drone.DroneId = id;
            drone.VesselId = "RoleTaskTest";
            drone.DisplayName = "Role Task Test Ship";
            drone.NextMove = TimeSpan.FromDays(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromDays(1);
            droneUids.Add(uid);
        }
    }

    [Test]
    public async Task AiBaseDroneAndTraceExamineExplainCurrentWork()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<MapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();

        EntityUid droneUid = EntityUid.Invalid;
        EntityUid traceUid = EntityUid.Invalid;
        EntityUid examinerUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out var mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(-8f, -8f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            Assert.That(storySystem.EnsureAiBase("examine-test"),
                Does.Contain("AI base deployed").Or.Contain("AI base already exists"));

            entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(Vector2.Zero, mapId));

            examinerUid = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(1f, 1f), mapId));
            droneUid = entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(new Vector2(7f, 0f), mapId));
            var drone = entManager.EnsureComponent<LuaMAiMiningDroneComponent>(droneUid);
            drone.DroneRole = "logistics";
            drone.DroneId = "examine-test";
            drone.LastSeenName = "LuaM examine technician";
            drone.LastContactCount = 2;
            drone.LastContactTone = "repeat_contact";
            drone.UniquePeopleSeen = 1;
            drone.LastWorldTraceSummary = "supply corridor hint #2 near LuaM examine technician";
            drone.NextMove = TimeSpan.FromTicks(1);
            drone.NextMine = TimeSpan.FromDays(1);
            drone.NextSocialScan = TimeSpan.FromDays(1);

            traceUid = entManager.SpawnEntity("LuaMAiDroneTrace", new MapCoordinates(new Vector2(1f, 0f), mapId));
            var trace = entManager.EnsureComponent<LuaMAiDroneTraceComponent>(traceUid);
            trace.DroneRole = "logistics";
            trace.TargetName = "LuaM examine technician";
            trace.ContactCount = 2;
            trace.ContactTone = "repeat_contact";
            trace.Summary = "supply corridor hint #2 near LuaM examine technician";
        });

        await pair.RunTicksSync(20);

        await server.WaitAssertion(() =>
        {
            EntityUid storageZone = EntityUid.Invalid;
            var zoneQuery = entManager.EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
            while (zoneQuery.MoveNext(out var uid, out var zone))
            {
                if (zone.ZoneType == LuaMAiBaseEcologySystem.ZoneStorage)
                {
                    storageZone = uid;
                    break;
                }
            }

            Assert.That(storageZone.IsValid(), Is.True);
            Assert.That(entManager.HasComponent<LuaMAiDroneTaskComponent>(droneUid), Is.True);

            var zoneText = Examine(storageZone);
            Assert.That(zoneText, Does.Contain("AI storage"));
            Assert.That(zoneText, Does.Contain("LuaM-AI-Base"));
            Assert.That(zoneText, Does.Contain("critical supply score"));
            Assert.That(zoneText, Does.Contain("prioritize"));

            var droneText = Examine(droneUid);
            Assert.That(droneText, Does.Contain("examine-test"));
            Assert.That(droneText, Does.Contain("storage"));
            Assert.That(droneText, Does.Contain("moving to storage"));
            Assert.That(droneText, Does.Contain("compensating"));
            Assert.That(droneText, Does.Contain("critical supply score"));
            Assert.That(droneText, Does.Contain("prioritize"));
            Assert.That(droneText, Does.Contain("supply corridor hint #2"));
            Assert.That(droneText, Does.Contain("LuaM examine technician"));

            var traceText = Examine(traceUid);
            Assert.That(traceText, Does.Contain("#2"));
            Assert.That(traceText, Does.Contain("supply corridor hint #2"));
            Assert.That(traceText, Does.Contain("LuaM examine technician"));
        });

        await pair.CleanReturnAsync();

        string Examine(EntityUid target)
        {
            var message = new FormattedMessage();
            var ev = new ExaminedEvent(message, target, examinerUid, isInDetailsRange: true, hasDescription: false);
            entManager.EventBus.RaiseLocalEvent(target, ev);
            return ev.GetTotalMessage().ToMarkup();
        }
    }

    [Test]
    public async Task SectorStoriesSeedNewsContractsAndMemory()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var sectorService = entManager.System<SectorServiceSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var bankSystem = entManager.System<BankSystem>();
        var bountyContracts = entManager.System<BountyContractSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        var stories = prototypeManager.EnumeratePrototypes<LuaMSectorStoryPrototype>().ToList();
        var initialStories = stories.Where(story => story.RequiredReputation <= 0).ToList();
        var expectedDistress = initialStories.Count(story => story.ContractCollection == "Distress");
        var expectedPublic = initialStories.Count(story => story.ContractCollection == "Public");

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            Assert.That(bankSystem.TrySectorDeposit(SectorBankAccount.Frontier, 50000, LedgerEntryType.TickingIncome), Is.True);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(sectorService.GetServiceEntity().IsValid(), Is.True);
            Assert.That(storySystem.TryGetSectorMemory(out var records, out var newsSeeded, out var contractsSeeded), Is.True);
            Assert.That(records, Has.Count.EqualTo(initialStories.Count));
            Assert.That(newsSeeded, Is.True);
            Assert.That(contractsSeeded, Is.True);
            Assert.That(storySystem.GetActiveHazards(), Has.Count.EqualTo(initialStories.Count));
            Assert.That(storySystem.GetInsuranceCases(), Has.Count.EqualTo(initialStories.Count));
            Assert.That(storySystem.GetBlackBoxCases(), Has.Count.EqualTo(initialStories.Count));
            Assert.That(storySystem.GetCompanyRecords(), Has.Count.EqualTo(initialStories.Count));
            Assert.That(storySystem.GetShipRecords(), Has.Count.EqualTo(initialStories.Count));

            var initialStatus = storySystem.GetStatusSnapshot();
            var lockedStories = stories.Where(story => story.RequiredReputation > 0).ToList();
            var trustedStory = stories.Single(story => story.ID == "LuaMSectorStoryTrustedSalvageCache");
            var courierStory = stories.Single(story => story.ID == "LuaMSectorStoryPreferredCourierRoute");
            var ledgerStory = stories.Single(story => story.ID == "LuaMSectorStoryGhostStationLedger");
            var priorityRescueStory = stories.Single(story => story.ID == "LuaMSectorStoryPriorityRescueLane");
            Assert.That(initialStatus.LockedStories, Is.EqualTo(stories.Count - initialStories.Count));
            Assert.That(initialStatus.LockedLeads.Select(lead => lead.Story),
                Is.EquivalentTo(lockedStories.Select(story => story.ID)));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == trustedStory.ID).RequiredTarget,
                Is.EqualTo(trustedStory.RequiredReputationTarget));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == trustedStory.ID).RequiredValue,
                Is.EqualTo(trustedStory.RequiredReputation));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == trustedStory.ID).CurrentValue, Is.EqualTo(0));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == courierStory.ID).RequiredTarget,
                Is.EqualTo(courierStory.RequiredReputationTarget));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == courierStory.ID).RequiredValue,
                Is.EqualTo(courierStory.RequiredReputation));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == courierStory.ID).CurrentValue, Is.EqualTo(0));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == ledgerStory.ID).RequiredTarget,
                Is.EqualTo(ledgerStory.RequiredReputationTarget));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == ledgerStory.ID).RequiredValue,
                Is.EqualTo(ledgerStory.RequiredReputation));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == ledgerStory.ID).CurrentValue, Is.EqualTo(0));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == priorityRescueStory.ID).RequiredTarget,
                Is.EqualTo(priorityRescueStory.RequiredReputationTarget));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == priorityRescueStory.ID).RequiredValue,
                Is.EqualTo(priorityRescueStory.RequiredReputation));
            Assert.That(initialStatus.LockedLeads.Single(lead => lead.Story == priorityRescueStory.ID).CurrentValue, Is.EqualTo(0));

            Assert.That(SectorNewsComponent.Articles, Has.Count.GreaterThanOrEqualTo(initialStories.Count));
            Assert.That(SectorNewsComponent.Articles.All(article => !string.IsNullOrWhiteSpace(article.Title)), Is.True);

            var distressContracts = bountyContracts.GetContracts("Distress")
                .Where(contract => contract.Author == "Доска слухов LuaM")
                .ToList();
            var publicContracts = bountyContracts.GetContracts("Public")
                .Where(contract => contract.Author == "Доска слухов LuaM")
                .ToList();

            Assert.That(distressContracts, Has.Count.EqualTo(expectedDistress));
            Assert.That(publicContracts, Has.Count.EqualTo(expectedPublic));
            Assert.That(distressContracts.Concat(publicContracts).Select(contract => contract.Name),
                Is.SupersetOf(initialStories.Select(story => story.ContractName)));

            foreach (var story in initialStories)
            {
                var contract = distressContracts.Concat(publicContracts).Single(candidate => candidate.Name == story.ContractName);
                Assert.That(contract.Reward, Is.EqualTo(story.ContractReward + story.HazardRewardBonus));
                Assert.That(contract.Description, Does.Contain($"[HZ-{story.HazardSeverity}]"));
            }

            var hazardStory = stories.First(story => story.ID == "LuaMSectorStorySilentTow");
            Assert.That(storySystem.TryAcknowledgeHazard(hazardStory.ID,
                "ops-console",
                "Marked as active hazard.",
                out var hazardReport), Is.True);
            Assert.That(hazardReport, Is.Not.Null);
            Assert.That(hazardReport!.Hazard, Is.EqualTo(hazardStory.Hazard));
            Assert.That(storySystem.TryAcknowledgeHazard(hazardStory.ID,
                "ops-console",
                "Duplicate hazard acknowledgement.",
                out _), Is.False);
            Assert.That(storySystem.GetHazardReports(), Has.Count.EqualTo(1));
            Assert.That(storySystem.GetActiveHazards().Select(record => record.Story), Does.Contain(hazardStory.ID));

            var paidStory = stories.First(story => story.ID == "LuaMSectorStorySealedCargo");
            Assert.That(storySystem.TryClaimInsurance(paidStory.ID,
                "insurance-desk",
                "Claim filed before final story close.",
                out var paidClaim), Is.True);
            Assert.That(paidClaim, Is.Not.Null);
            Assert.That(paidClaim!.Amount, Is.EqualTo(paidStory.ContractReward));
            Assert.That(paidClaim.Paid, Is.True);
            Assert.That(storySystem.TryClaimInsurance(paidStory.ID,
                "insurance-desk",
                "Duplicate insurance claim.",
                out _), Is.False);

            Assert.That(storySystem.TryRegisterCompanyRecord(paidStory.ID,
                "registry-desk",
                "Company record logged before final story close.",
                out var companyRecord), Is.True);
            Assert.That(companyRecord, Is.Not.Null);
            Assert.That(companyRecord!.Record, Is.EqualTo(paidStory.CompanyRecord));
            Assert.That(storySystem.TryRegisterCompanyRecord(paidStory.ID,
                "registry-desk",
                "Duplicate company registration.",
                out _), Is.False);

            Assert.That(storySystem.TryRegisterShipRecord(paidStory.ID,
                "registry-desk",
                "Ship record logged before final story close.",
                out var shipRecord), Is.True);
            Assert.That(shipRecord, Is.Not.Null);
            Assert.That(shipRecord!.Record, Is.EqualTo(paidStory.ShipRecord));
            Assert.That(storySystem.TryRegisterShipRecord(paidStory.ID,
                "registry-desk",
                "Duplicate ship registration.",
                out _), Is.False);

            Assert.That(storySystem.TryResolveStory(paidStory.ID, "integration-test", "Delivered sealed cargo."), Is.True);
            Assert.That(storySystem.TryResolveStory(paidStory.ID, "integration-test", "Duplicate close."), Is.False);
            Assert.That(storySystem.GetStatusSnapshot().LockedLeads.Select(lead => lead.Story),
                Does.Not.Contain(courierStory.ID));

            var blackBoxStory = stories.First(story => story.ID == "LuaMSectorStoryBlackBoxPaid");
            Assert.That(storySystem.TryRecoverBlackBox(blackBoxStory.ID,
                "black-box-desk",
                "Recovered recorder before final story close.",
                out var blackBoxRecovery), Is.True);
            Assert.That(blackBoxRecovery, Is.Not.Null);
            Assert.That(blackBoxRecovery!.Recovery, Is.EqualTo(blackBoxStory.BlackBox));
            Assert.That(storySystem.TryRecoverBlackBox(blackBoxStory.ID,
                "black-box-desk",
                "Duplicate recorder recovery.",
                out _), Is.False);

            Assert.That(storySystem.TryResolveStory(blackBoxStory.ID, "integration-test", "Recovered black box report."), Is.True);

            var reputation = storySystem.GetReputationLedger();
            Assert.That(reputation, Contains.Key(paidStory.ReputationTarget));
            Assert.That(reputation, Contains.Key(blackBoxStory.ReputationTarget));
            Assert.That(reputation[paidStory.ReputationTarget], Is.EqualTo(paidStory.ReputationDelta));
            Assert.That(reputation[blackBoxStory.ReputationTarget], Is.EqualTo(blackBoxStory.ReputationDelta));

            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.LockedStories, Is.EqualTo(2));
            Assert.That(status.TotalStories + status.LockedStories, Is.EqualTo(stories.Count));
            Assert.That(status.LockedLeads.Select(lead => lead.Story),
                Is.EquivalentTo(new[] { ledgerStory.ID, priorityRescueStory.ID }));
            Assert.That(status.LockedLeads.All(lead => lead.CurrentValue == 0), Is.True);
            Assert.That(status.ActiveHazards, Is.EqualTo(status.TotalStories - 2));
            Assert.That(status.AcknowledgedHazards, Is.EqualTo(3));
            Assert.That(status.Hazards.Single(hazard => hazard.Story == blackBoxStory.ID).Resolved, Is.True);
            Assert.That(status.Hazards.Single(hazard => hazard.Story == blackBoxStory.ID).Severity, Is.EqualTo(blackBoxStory.HazardSeverity));
            Assert.That(status.Hazards.Single(hazard => hazard.Story == blackBoxStory.ID).RewardBonus, Is.EqualTo(blackBoxStory.HazardRewardBonus));
            Assert.That(status.Reputation.Single(entry => entry.Target == paidStory.ReputationTarget).RewardBonus,
                Is.EqualTo(paidStory.ReputationDelta * LuaMSectorStorySystem.ReputationRewardStep));
            Assert.That(status.Reputation.Single(entry => entry.Target == blackBoxStory.ReputationTarget).RewardBonus,
                Is.EqualTo(blackBoxStory.ReputationDelta * LuaMSectorStorySystem.ReputationRewardStep));
            Assert.That(status.Reputation.Single(entry => entry.Target == blackBoxStory.ReputationTarget).Tier, Is.EqualTo("known"));
            Assert.That(status.RecentHistory, Has.Count.EqualTo(LuaMSectorStorySystem.RecentHistoryLimit));
            Assert.That(status.RecentHistory.Select(entry => entry.Category),
                Is.SupersetOf(new[] { "Reputation", "Hazard", "Insurance", "Company", "Ship" }));
            Assert.That(status.RecentHistory.Select(entry => entry.Title),
                Does.Contain(blackBoxStory.Title));
            Assert.That(status.RecentHistory.Select(entry => entry.Actor),
                Does.Contain("integration-test"));

            var entries = storySystem.GetReputationEntries();
            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(entries.Select(entry => entry.Story), Is.SupersetOf(new[] { paidStory.ID, blackBoxStory.ID }));
            Assert.That(entries.All(entry => entry.Actor == "integration-test"), Is.True);

            var hazardReports = storySystem.GetHazardReports();
            Assert.That(hazardReports.Select(report => report.Story),
                Is.SupersetOf(new[] { hazardStory.ID, paidStory.ID, blackBoxStory.ID }));
            Assert.That(eventSystem.HazardsAcknowledged, Is.EqualTo(3));
            Assert.That(eventSystem.InsuranceClaimed, Is.EqualTo(2));
            Assert.That(eventSystem.BlackBoxesRecovered, Is.EqualTo(2));
            Assert.That(eventSystem.CompaniesRegistered, Is.EqualTo(2));
            Assert.That(eventSystem.ShipsRegistered, Is.EqualTo(2));
            Assert.That(eventSystem.ReputationChanged, Is.EqualTo(2));
            Assert.That(eventSystem.StoriesResolved, Is.EqualTo(2));
            Assert.That(eventSystem.StoriesUnlocked, Is.EqualTo(2));

            var payouts = storySystem.GetInsurancePayouts();
            Assert.That(payouts, Has.Count.EqualTo(2));
            var paidPayout = payouts.Single(payout => payout.Story == paidStory.ID);
            var unpaidPayout = payouts.Single(payout => payout.Story == blackBoxStory.ID);
            Assert.That(paidPayout.Amount, Is.EqualTo(paidStory.ContractReward));
            Assert.That(paidPayout.Paid, Is.True);
            Assert.That(paidPayout.Actor, Is.EqualTo("insurance-desk"));
            Assert.That(unpaidPayout.Amount,
                Is.EqualTo(blackBoxStory.ContractReward + blackBoxStory.ReputationDelta * LuaMSectorStorySystem.ReputationRewardStep));
            Assert.That(unpaidPayout.Paid, Is.False);

            var recoveries = storySystem.GetBlackBoxRecoveries();
            Assert.That(recoveries.Select(recovery => recovery.Story), Is.SupersetOf(new[] { paidStory.ID, blackBoxStory.ID }));
            Assert.That(storySystem.GetCompanyRegistry().Select(entry => entry.Story), Is.SupersetOf(new[] { paidStory.ID, blackBoxStory.ID }));
            Assert.That(storySystem.GetShipRegistry().Select(entry => entry.Story), Is.SupersetOf(new[] { paidStory.ID, blackBoxStory.ID }));
            Assert.That(storySystem.GetActiveHazards().Select(record => record.Story), Does.Not.Contain(paidStory.ID));
            Assert.That(storySystem.GetActiveHazards().Select(record => record.Story), Does.Not.Contain(blackBoxStory.ID));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorMemorySnapshotRestoresPersistentStoryRecords()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        EntityUid user = default;
        EntityUid insurance = default;
        EntityUid blackBoxReport = default;
        EntityUid trustedManifest = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            insurance = entManager.SpawnEntity("PaperLuaMSalvageInsuranceForm", MapCoordinates.Nullspace);
            blackBoxReport = entManager.SpawnEntity("PaperLuaMBlackBoxReport", MapCoordinates.Nullspace);
            trustedManifest = entManager.SpawnEntity("PaperLuaMTrustedSalvageCacheManifest", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(evidenceSystem.TryFileEvidence(trustedManifest, user), Is.False);
            Assert.That(evidenceSystem.TryFileEvidence(blackBoxReport, user), Is.True);
            Assert.That(evidenceSystem.TryFileEvidence(insurance, user), Is.True);

            var persisted = storySystem.GetStatusSnapshot();
            Assert.That(persisted.InsurancePayouts, Is.EqualTo(2));
            Assert.That(persisted.BlackBoxRecoveries, Is.EqualTo(1));
            Assert.That(persisted.CompanyRecords, Is.EqualTo(2));
            Assert.That(persisted.ShipRecords, Is.EqualTo(2));
            Assert.That(persisted.ReputationLedger, Contains.Key("Salvage"));
            Assert.That(persisted.LockedStories, Is.EqualTo(3));
            Assert.That(persisted.LockedLeads.Select(lead => lead.Story),
                Is.EquivalentTo(new[]
                {
                    "LuaMSectorStoryPreferredCourierRoute",
                    "LuaMSectorStoryGhostStationLedger",
                    "LuaMSectorStoryPriorityRescueLane",
                }));

            Assert.That(evidenceSystem.TryFileEvidence(trustedManifest, user), Is.True);
            var trustedResolved = storySystem.GetStatusSnapshot();
            Assert.That(trustedResolved.ReputationLedger["Salvage"], Is.EqualTo(3));
            Assert.That(trustedResolved.Hazards.Single(hazard => hazard.Story == "LuaMSectorStoryTrustedSalvageCache").Resolved, Is.True);
            persisted = trustedResolved;

            Assert.That(storySystem.TryExportMemorySnapshot(out var snapshot), Is.True);

            Assert.That(storySystem.TryResolveStory("LuaMSectorStoryFalseQuiet", "later-run", "Temporary mutation."), Is.True);
            Assert.That(storySystem.GetStatusSnapshot().ReputationLedger, Contains.Key("Station Records"));

            Assert.That(storySystem.TryImportMemorySnapshot(snapshot), Is.True);

            var restored = storySystem.GetStatusSnapshot();
            Assert.That(restored.InsurancePayouts, Is.EqualTo(persisted.InsurancePayouts));
            Assert.That(restored.BlackBoxRecoveries, Is.EqualTo(persisted.BlackBoxRecoveries));
            Assert.That(restored.CompanyRecords, Is.EqualTo(persisted.CompanyRecords));
            Assert.That(restored.ShipRecords, Is.EqualTo(persisted.ShipRecords));
            Assert.That(restored.ActiveHazards, Is.EqualTo(persisted.ActiveHazards));
            Assert.That(restored.ReputationLedger, Contains.Key("Salvage"));
            Assert.That(restored.ReputationLedger, Does.Not.ContainKey("Station Records"));
            Assert.That(storySystem.GetCompanyRegistry().Select(entry => entry.Story),
                Is.SupersetOf(new[] { "LuaMSectorStoryBlackBoxPaid", "LuaMSectorStorySealedCargo" }));
            Assert.That(storySystem.GetShipRegistry().Select(entry => entry.Story),
                Is.SupersetOf(new[] { "LuaMSectorStoryBlackBoxPaid", "LuaMSectorStorySealedCargo" }));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorEvidenceItemsFileStoryProgress()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var bankSystem = entManager.System<BankSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        EntityUid user = default;
        EntityUid beacon = default;
        EntityUid insurance = default;
        EntityUid blackBoxReport = default;
        EntityUid blackBoxRecorder = default;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            Assert.That(bankSystem.TrySectorDeposit(SectorBankAccount.Frontier, 50000, LedgerEntryType.TickingIncome), Is.True);

            mapSystem.CreateMap(out mapId);
            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            beacon = entManager.SpawnEntity("LuaMDistressBeacon", MapCoordinates.Nullspace);
            insurance = entManager.SpawnEntity("PaperLuaMSalvageInsuranceForm", MapCoordinates.Nullspace);
            blackBoxReport = entManager.SpawnEntity("PaperLuaMBlackBoxReport", new MapCoordinates(new Vector2(8.25f, -4.5f), mapId));
            blackBoxRecorder = entManager.SpawnEntity("LuaMBlackBoxRecorder", MapCoordinates.Nullspace);

            var crew = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(8.75f, -4.25f), mapId));
            entManager.AddComponent<MobStateComponent>(crew);
            entManager.AddComponent<PhysicsComponent>(crew);

            var cargo = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(7.75f, -4.75f), mapId));
            entManager.AddComponent<ItemComponent>(cargo);
            entManager.AddComponent<StorageComponent>(cargo);
            entManager.AddComponent<PhysicsComponent>(cargo);

            var damagedMachine = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(8.50f, -5.00f), mapId));
            entManager.AddComponent<DamageableComponent>(damagedMachine);
            entManager.AddComponent<ApcPowerReceiverComponent>(damagedMachine);
            entManager.AddComponent<PhysicsComponent>(damagedMachine);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryGetSectorMemory(out _, out _, out _), Is.True);

            Assert.That(evidenceSystem.TryFileEvidence(beacon, user), Is.True);
            Assert.That(evidenceSystem.TryFileEvidence(beacon, user), Is.False);

            Assert.That(evidenceSystem.TryFileEvidence(blackBoxReport, user), Is.True);
            Assert.That(evidenceSystem.TryFileEvidence(blackBoxRecorder, user), Is.False);

            Assert.That(evidenceSystem.TryFileEvidence(insurance, user), Is.True);
            Assert.That(evidenceSystem.TryFileEvidence(insurance, user), Is.False);

            var hazardStories = storySystem.GetHazardReports().Select(report => report.Story).ToList();
            Assert.That(hazardStories, Is.SupersetOf(new[]
            {
                "LuaMSectorStorySilentTow",
                "LuaMSectorStoryBlackBoxPaid",
                "LuaMSectorStorySealedCargo",
            }));

            var payouts = storySystem.GetInsurancePayouts();
            Assert.That(payouts, Has.Count.EqualTo(2));
            Assert.That(payouts.Single(payout => payout.Story == "LuaMSectorStoryBlackBoxPaid").Paid, Is.False);
            Assert.That(payouts.Single(payout => payout.Story == "LuaMSectorStorySealedCargo").Paid, Is.True);

            var blackBoxRecovery = storySystem.GetBlackBoxRecoveries().Single();
            Assert.That(blackBoxRecovery.Story, Is.EqualTo("LuaMSectorStoryBlackBoxPaid"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.StartWith("Source "));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("GPS map"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("x 8.3"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("y -4.5"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("grid unavailable"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("grid physical unavailable"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("nearby 8m"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("mobs 1"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("items"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("storage 1"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("damageable 1 (0 damaged)"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("power 0 on/1 off"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("physics"));
            Assert.That(blackBoxRecovery.SourceSnapshot, Does.Contain("nearby names"));
            Assert.That(storySystem.TryExportMemoryJson(out var exportedJson), Is.True);
            Assert.That(exportedJson, Does.Contain("SourceSnapshot"));
            Assert.That(exportedJson, Does.Contain("x 8.3"));
            Assert.That(exportedJson, Does.Contain("nearby 8m"));
            Assert.That(storySystem.GetCompanyRegistry().Select(entry => entry.Story),
                Is.SupersetOf(new[] { "LuaMSectorStoryBlackBoxPaid", "LuaMSectorStorySealedCargo" }));
            Assert.That(storySystem.GetShipRegistry().Select(entry => entry.Story),
                Is.SupersetOf(new[] { "LuaMSectorStoryBlackBoxPaid", "LuaMSectorStorySealedCargo" }));

            Assert.That(storySystem.GetActiveHazards().Select(record => record.Story),
                Does.Not.Contain("LuaMSectorStoryBlackBoxPaid"));
            Assert.That(storySystem.GetActiveHazards().Select(record => record.Story),
                Does.Contain("LuaMSectorStorySilentTow"));
            Assert.That(storySystem.GetActiveHazards().Select(record => record.Story),
                Does.Contain("LuaMSectorStorySealedCargo"));

            var reputation = storySystem.GetReputationLedger();
            Assert.That(reputation, Contains.Key("Salvage"));
            Assert.That(reputation, Does.Not.ContainKey("Trade"));
            Assert.That(eventSystem.HazardsAcknowledged, Is.EqualTo(3));
            Assert.That(eventSystem.InsuranceClaimed, Is.EqualTo(2));
            Assert.That(eventSystem.BlackBoxesRecovered, Is.EqualTo(1));
            Assert.That(eventSystem.StoriesResolved, Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorInsuranceTerminalPrintsClaimVoucherAndFilesPayout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var insuranceTerminal = entManager.System<LuaMSectorInsuranceTerminalSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        EntityUid user = default;
        EntityUid terminal = default;
        EntityUid docket = default;
        EntityUid voucher = default;
        LuaMSectorInsuranceUiEntry selectedClaim = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            terminal = entManager.SpawnEntity("ComputerLuaMSectorInsuranceTerminal", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitPost(() =>
        {
            Assert.That(insuranceTerminal.TryPrintInsuranceDocket(terminal, user, out docket), Is.True);
            var claims = insuranceTerminal.BuildInsuranceUiEntries();
            Assert.That(claims, Is.Not.Empty);
            Assert.That(claims.Any(claim => !claim.Claimed), Is.True);
            selectedClaim = claims.Last(claim => !claim.Claimed);
            Assert.That(selectedClaim.State, Is.EqualTo("unclaimed"));
            Assert.That(selectedClaim.ServiceLine, Does.Contain("Service tier: standard"));
            Assert.That(insuranceTerminal.TryPrintInsuranceClaimVoucherForStory(terminal, user, out voucher, selectedClaim.StoryId), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(docket, out var docketPaper), Is.True);
            Assert.That(docketPaper!.Content, Does.Contain("# LuaM salvage insurance docket"));
            Assert.That(docketPaper.Content, Does.Contain("unclaimed"));
            Assert.That(docketPaper.Content, Does.Contain("Service tier: standard"));

            Assert.That(entManager.TryGetComponent<PaperComponent>(voucher, out var voucherPaper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(voucher, out var evidence), Is.True);
            Assert.That(evidence!.ClaimInsurance, Is.True);
            Assert.That(evidence.AcknowledgeHazard, Is.False);
            Assert.That($"{evidence.Story}", Is.EqualTo(selectedClaim.StoryId));
            Assert.That(voucherPaper!.Content, Does.Contain("# LuaM salvage insurance claim voucher"));
            Assert.That(voucherPaper.Content, Does.Contain($"Story: {evidence.Story}"));
            Assert.That(voucherPaper.Content, Does.Contain("## Policy"));
            Assert.That(voucherPaper.Content, Does.Contain("Service tier: standard"));

            Assert.That(storySystem.GetInsurancePayouts(), Is.Empty);
            Assert.That(evidenceSystem.TryFileEvidence(voucher, user), Is.True);

            var payout = storySystem.GetInsurancePayouts().Single();
            Assert.That(payout.Story, Is.EqualTo(evidence.Story));
            Assert.That(payout.Policy, Is.Not.Empty);
            Assert.That(payout.Note, Does.Contain("insurance claim voucher filed"));
            Assert.That(eventSystem.InsuranceClaimed, Is.EqualTo(1));
            var updatedClaim = insuranceTerminal.BuildInsuranceUiEntries()
                .Single(claim => claim.StoryId == selectedClaim.StoryId);
            Assert.That(updatedClaim.Claimed, Is.True);
            Assert.That(updatedClaim.State.StartsWith("paid", StringComparison.Ordinal) ||
                        updatedClaim.State.StartsWith("unpaid", StringComparison.Ordinal), Is.True);

            Assert.That(evidenceSystem.TryFileEvidence(voucher, user), Is.False);
            Assert.That(storySystem.GetInsurancePayouts(), Has.Count.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorRegistryTerminalPrintsCharterVoucherAndFilesRecords()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var registryTerminal = entManager.System<LuaMSectorRegistryTerminalSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        EntityUid user = default;
        EntityUid terminal = default;
        EntityUid docket = default;
        EntityUid voucher = default;
        LuaMSectorRegistryUiEntry selectedRecord = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            terminal = entManager.SpawnEntity("ComputerLuaMSectorRegistryTerminal", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitPost(() =>
        {
            Assert.That(registryTerminal.TryPrintRegistryDocket(terminal, user, out docket), Is.True);
            var records = registryTerminal.BuildRegistryUiEntries();
            Assert.That(records, Is.Not.Empty);
            Assert.That(records.Any(record => record.CanRegisterCompany || record.CanRegisterShip), Is.True);
            selectedRecord = records.Last(record => record.CanRegisterCompany || record.CanRegisterShip);
            Assert.That(selectedRecord.ServiceLine, Does.Contain("Service tier: standard"));
            Assert.That(registryTerminal.TryPrintCharterVoucherForStory(terminal, user, out voucher, selectedRecord.StoryId), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(docket, out var docketPaper), Is.True);
            Assert.That(docketPaper!.Content, Does.Contain("# LuaM company and ship registry docket"));
            Assert.That(docketPaper.Content, Does.Contain("company open"));
            Assert.That(docketPaper.Content, Does.Contain("ship open"));
            Assert.That(docketPaper.Content, Does.Contain("Service tier: standard"));

            Assert.That(entManager.TryGetComponent<PaperComponent>(voucher, out var voucherPaper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(voucher, out var evidence), Is.True);
            Assert.That(evidence!.RegisterCompany, Is.True);
            Assert.That(evidence.RegisterShip, Is.True);
            Assert.That(evidence.ClaimInsurance, Is.False);
            Assert.That($"{evidence.Story}", Is.EqualTo(selectedRecord.StoryId));
            Assert.That(voucherPaper!.Content, Does.Contain("# LuaM company and ship charter voucher"));
            Assert.That(voucherPaper.Content, Does.Contain($"Story: {evidence.Story}"));
            Assert.That(voucherPaper.Content, Does.Contain("Register company: True"));
            Assert.That(voucherPaper.Content, Does.Contain("Register ship: True"));
            Assert.That(voucherPaper.Content, Does.Contain("Service tier: standard"));

            Assert.That(storySystem.GetCompanyRegistry(), Is.Empty);
            Assert.That(storySystem.GetShipRegistry(), Is.Empty);
            Assert.That(evidenceSystem.TryFileEvidence(voucher, user), Is.True);

            var company = storySystem.GetCompanyRegistry().Single();
            var ship = storySystem.GetShipRegistry().Single();
            Assert.That(company.Story, Is.EqualTo(evidence.Story));
            Assert.That(ship.Story, Is.EqualTo(evidence.Story));
            Assert.That(company.Note, Does.Contain("registry charter voucher filed"));
            Assert.That(ship.Note, Does.Contain("registry charter voucher filed"));
            Assert.That(eventSystem.CompaniesRegistered, Is.EqualTo(1));
            Assert.That(eventSystem.ShipsRegistered, Is.EqualTo(1));
            var updatedRecord = registryTerminal.BuildRegistryUiEntries()
                .Single(record => record.StoryId == selectedRecord.StoryId);
            Assert.That(updatedRecord.CanRegisterCompany, Is.False);
            Assert.That(updatedRecord.CanRegisterShip, Is.False);
            Assert.That(updatedRecord.CompanyState, Is.EqualTo("filed"));
            Assert.That(updatedRecord.ShipState, Is.EqualTo("filed"));

            Assert.That(evidenceSystem.TryFileEvidence(voucher, user), Is.False);
            Assert.That(storySystem.GetCompanyRegistry(), Has.Count.EqualTo(1));
            Assert.That(storySystem.GetShipRegistry(), Has.Count.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorConditionsPersistAndPrintInLeadReport()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var leadReportSystem = entManager.System<LuaMSectorLeadReportSystem>();

        EntityUid user = default;
        EntityUid board = default;
        EntityUid report = default;
        LuaMSectorConditionEntry? condition = null;
        var conditionError = string.Empty;
        bool seeded = false;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            board = entManager.SpawnEntity("ComputerLuaMSectorRumorBoard", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            seeded = storySystem.TrySeedSectorCondition(
                "comms-blackout",
                "Comms blackout",
                4,
                "Long-range radio is unreliable near the east salvage lane.",
                "integration-test",
                out condition,
                out conditionError);
            Assert.That(leadReportSystem.TryPrintLeadReport(board, user, out report), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(seeded, Is.True, conditionError);
            Assert.That(condition, Is.Not.Null);
            Assert.That(condition!.Severity, Is.EqualTo(4));

            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.ActiveConditions, Is.EqualTo(1));
            var activeCondition = status.Conditions.Single();
            Assert.That(activeCondition.ConditionId, Is.EqualTo("comms-blackout"));
            Assert.That(activeCondition.Title, Is.EqualTo("Comms blackout"));
            Assert.That(activeCondition.Summary, Does.Contain("east salvage lane"));
            Assert.That(activeCondition.Active, Is.True);

            Assert.That(entManager.TryGetComponent<PaperComponent>(report, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Sector conditions"));
            Assert.That(paper.Content, Does.Contain("SC-4 Comms blackout [comms-blackout]"));
            Assert.That(paper.Content, Does.Contain("Long-range radio is unreliable"));

            Assert.That(storySystem.TryExportMemoryJson(out var exportedJson), Is.True);
            Assert.That(exportedJson, Does.Contain("SectorConditions"));
            Assert.That(exportedJson, Does.Contain("comms-blackout"));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryClearSectorCondition("comms-blackout", "integration-test", out var error), Is.True, error);
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.ActiveConditions, Is.EqualTo(0));
            Assert.That(status.Conditions.Single().Active, Is.False);
            Assert.That(storySystem.TryClearSectorCondition("comms-blackout", "integration-test", out _), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorStatusCartridgeUiStateIncludesActiveConditions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var cartridgeSystem = entManager.System<LuaMSectorStatusCartridgeSystem>();
        var leadReportSystem = entManager.System<LuaMSectorLeadReportSystem>();

        LuaMSectorStatusUiState? conditionState = null;
        LuaMSectorStatusUiState? clearedState = null;
        LuaMSectorStatusUiState? terminalState = null;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TrySeedSectorCondition(
                "dust-lane",
                "Dust lane",
                3,
                "Sensor drift is likely near the western trade route.",
                "integration-test",
                out _,
                out var error), Is.True, error);

            conditionState = cartridgeSystem.BuildStatusUiState();
            Assert.That(conditionState.ActiveConditions, Is.EqualTo(1));
            Assert.That(conditionState.Conditions, Has.Length.EqualTo(1));
            Assert.That(conditionState.Conditions.Single().ConditionId, Is.EqualTo("dust-lane"));
            Assert.That(conditionState.Conditions.Single().Title, Is.EqualTo("Dust lane"));
            Assert.That(conditionState.Conditions.Single().Severity, Is.EqualTo(3));
            Assert.That(conditionState.Conditions.Single().Summary, Does.Contain("western trade route"));
            Assert.That(conditionState.Conditions.Single().Actor, Is.EqualTo("integration-test"));
            Assert.That(conditionState.Conditions.Single().Active, Is.True);
            Assert.That(conditionState.DigestLines, Is.Not.Empty);
            Assert.That(conditionState.DigestLines.Any(line => line.Contains("Сводка дня")), Is.True);
            Assert.That(conditionState.BriefingSteps, Is.Not.Empty);
            Assert.That(conditionState.BriefingSteps.Any(step => step.Contains("Dust lane")), Is.True);
            Assert.That(conditionState.Automation.State, Is.Not.Empty);
            Assert.That(conditionState.Automation.NextAutomaticEvent, Is.Not.Empty);
            Assert.That(conditionState.Automation.DispatchTier, Is.EqualTo("standard"));
            Assert.That(conditionState.Automation.DispatchReputationScore, Is.EqualTo(0));
            Assert.That(conditionState.Automation.DispatchCooldownReductionPercent, Is.EqualTo(0));
            Assert.That(conditionState.Automation.DispatchCooldownMinSeconds, Is.EqualTo(LuaMSectorDynamicEventSystem.MinCooldownSeconds));
            Assert.That(conditionState.Automation.DispatchCooldownMaxSeconds, Is.EqualTo(LuaMSectorDynamicEventSystem.MaxCooldownSeconds));
            Assert.That(conditionState.Automation.TemplateIds, Does.Contain("quiet-distress"));
            Assert.That(conditionState.Automation.TemplateIds, Does.Contain("field-repair"));
            Assert.That(conditionState.SectorMapNodes, Has.Length.EqualTo(1));
            var conditionMapNode = conditionState.SectorMapNodes.Single();
            Assert.That(conditionMapNode.Kind, Is.EqualTo("условие"));
            Assert.That(conditionMapNode.Title, Is.EqualTo("Dust lane"));
            Assert.That(conditionMapNode.State, Is.EqualTo("SC-3 активно"));
            Assert.That(conditionMapNode.Location, Is.EqualTo("весь сектор"));
            Assert.That(conditionMapNode.TemplateId, Is.EqualTo("dust-lane"));
            Assert.That(conditionMapNode.Risk, Does.Contain("дрейф сенсоров"));
            Assert.That(conditionState.PreferredProcesses, Is.Not.Empty);
            var lockedRepairRequest = conditionState.PreferredProcesses.Single(process => process.TemplateId == "field-repair");
            Assert.That(lockedRepairRequest.Unlocked, Is.False);
            Assert.That(lockedRepairRequest.CurrentReputation, Is.EqualTo(0));
            Assert.That(lockedRepairRequest.RequiredReputation, Is.EqualTo(LuaMSectorDynamicEventSystem.PreferredProcessRequiredReputation));
            Assert.That(lockedRepairRequest.BlockReason, Does.Contain("Нужна репутация Salvage"));

            terminalState = leadReportSystem.BuildTerminalUiState("integration status refreshed.");
            Assert.That(terminalState.LastActionResult, Is.EqualTo("integration status refreshed."));
            Assert.That(terminalState.DigestLines, Is.Not.Empty);
            Assert.That(terminalState.BriefingSteps, Is.Not.Empty);
            Assert.That(terminalState.Automation.TemplateIds, Does.Contain("black-box-echo"));
            Assert.That(terminalState.SectorMapNodes.Single().NodeId, Is.EqualTo("condition:dust-lane"));
            Assert.That(terminalState.PreferredProcesses.Select(process => process.TemplateId), Does.Contain("black-box-echo"));

            Assert.That(storySystem.TryClearSectorCondition("dust-lane", "integration-test", out error), Is.True, error);
            clearedState = cartridgeSystem.BuildStatusUiState();
            Assert.That(clearedState.ActiveConditions, Is.EqualTo(0));
            Assert.That(clearedState.Conditions, Is.Empty);
            Assert.That(clearedState.SectorMapNodes, Is.Empty);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorConditionPresetCommandSeedsCondition()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var cartridgeSystem = entManager.System<LuaMSectorStatusCartridgeSystem>();

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            console.ExecuteCommand("luam_sector_condition_preset dust-cloud");
            console.ExecuteCommand("luam_sector_status");
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.ActiveConditions, Is.EqualTo(1));
            var condition = status.Conditions.Single();
            Assert.That(condition.ConditionId, Is.EqualTo("dust-cloud"));
            Assert.That(condition.Title, Is.EqualTo("Dust cloud"));
            Assert.That(condition.Severity, Is.EqualTo(3));
            Assert.That(condition.Summary, Does.Contain("Sensor drift"));
            Assert.That(condition.Actor, Is.EqualTo("console"));
            Assert.That(condition.Active, Is.True);

            var uiState = cartridgeSystem.BuildStatusUiState();
            Assert.That(uiState.ActiveConditions, Is.EqualTo(1));
            Assert.That(uiState.Conditions.Single().ConditionId, Is.EqualTo("dust-cloud"));
            Assert.That(uiState.Conditions.Single().Severity, Is.EqualTo(3));

            Assert.That(storySystem.TryExportMemoryJson(out var exportedJson), Is.True);
            Assert.That(exportedJson, Does.Contain("dust-cloud"));
            Assert.That(exportedJson, Does.Contain("Dust cloud"));
        });

        await server.WaitPost(() =>
        {
            console.ExecuteCommand("luam_sector_condition_clear dust-cloud");
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.ActiveConditions, Is.EqualTo(0));
            Assert.That(status.Conditions.Single(condition => condition.ConditionId == "dust-cloud").Active, Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorTradeReputationUnlocksCourierRouteEvidence()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        EntityUid user = default;
        EntityUid courierReceipt = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            courierReceipt = entManager.SpawnEntity("PaperLuaMPreferredCourierRouteReceipt", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(evidenceSystem.TryFileEvidence(courierReceipt, user), Is.False);

            var initialStatus = storySystem.GetStatusSnapshot();
            var courierLead = initialStatus.LockedLeads.Single(lead => lead.Story == "LuaMSectorStoryPreferredCourierRoute");
            Assert.That(courierLead.RequiredTarget, Is.EqualTo("Trade"));
            Assert.That(courierLead.RequiredValue, Is.EqualTo(1));
            Assert.That(courierLead.CurrentValue, Is.EqualTo(0));

            Assert.That(storySystem.TryResolveStory("LuaMSectorStorySealedCargo", "integration-test", "Delivered sealed cargo."), Is.True);

            var unlockedStatus = storySystem.GetStatusSnapshot();
            Assert.That(unlockedStatus.ReputationLedger["Trade"], Is.EqualTo(1));
            Assert.That(unlockedStatus.LockedLeads.Select(lead => lead.Story),
                Does.Not.Contain("LuaMSectorStoryPreferredCourierRoute"));

            Assert.That(evidenceSystem.TryFileEvidence(courierReceipt, user), Is.True);

            var resolvedStatus = storySystem.GetStatusSnapshot();
            Assert.That(resolvedStatus.ReputationLedger["Trade"], Is.EqualTo(2));
            Assert.That(resolvedStatus.Hazards.Single(hazard => hazard.Story == "LuaMSectorStoryPreferredCourierRoute").Resolved, Is.True);
            Assert.That(resolvedStatus.RecentHistory.Select(entry => entry.Title),
                Does.Contain("Приоритетный курьерский маршрут"));
            Assert.That(eventSystem.StoriesUnlocked, Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorStationRecordsReputationUnlocksLedgerEvidence()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var eventSystem = entManager.System<LuaMSectorStoryTestEventSystem>();
        eventSystem.Reset();

        EntityUid user = default;
        EntityUid ledgerExtract = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            ledgerExtract = entManager.SpawnEntity("PaperLuaMGhostStationLedgerExtract", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(evidenceSystem.TryFileEvidence(ledgerExtract, user), Is.False);

            var initialStatus = storySystem.GetStatusSnapshot();
            var ledgerLead = initialStatus.LockedLeads.Single(lead => lead.Story == "LuaMSectorStoryGhostStationLedger");
            Assert.That(ledgerLead.RequiredTarget, Is.EqualTo("Station Records"));
            Assert.That(ledgerLead.RequiredValue, Is.EqualTo(1));
            Assert.That(ledgerLead.CurrentValue, Is.EqualTo(0));

            Assert.That(storySystem.TryResolveStory("LuaMSectorStoryFalseQuiet", "integration-test", "Filed station record."), Is.True);

            var unlockedStatus = storySystem.GetStatusSnapshot();
            Assert.That(unlockedStatus.ReputationLedger["Station Records"], Is.EqualTo(1));
            Assert.That(unlockedStatus.LockedLeads.Select(lead => lead.Story),
                Does.Not.Contain("LuaMSectorStoryGhostStationLedger"));

            Assert.That(evidenceSystem.TryFileEvidence(ledgerExtract, user), Is.True);

            var resolvedStatus = storySystem.GetStatusSnapshot();
            Assert.That(resolvedStatus.ReputationLedger["Station Records"], Is.EqualTo(2));
            Assert.That(resolvedStatus.Hazards.Single(hazard => hazard.Story == "LuaMSectorStoryGhostStationLedger").Resolved, Is.True);
            Assert.That(resolvedStatus.RecentHistory.Select(entry => entry.Title),
                Does.Contain("Реестр станции-призрака"));
            Assert.That(eventSystem.StoriesUnlocked, Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorMemoryPersistsAcrossServiceRecreation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var sectorService = entManager.System<SectorServiceSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var bountyContracts = entManager.System<BountyContractSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var blackBoxStory = prototypeManager.Index<LuaMSectorStoryPrototype>("LuaMSectorStoryBlackBoxPaid");
        var trustedStory = prototypeManager.Index<LuaMSectorStoryPrototype>("LuaMSectorStoryTrustedSalvageCache");

        EntityUid host = default;
        EntityUid user = default;
        EntityUid insurance = default;
        EntityUid blackBoxReport = default;
        LuaMSectorStatusSnapshot? persisted = null;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            insurance = entManager.SpawnEntity("PaperLuaMSalvageInsuranceForm", MapCoordinates.Nullspace);
            blackBoxReport = entManager.SpawnEntity("PaperLuaMBlackBoxReport", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await server.WaitPost(() => SectorNewsComponent.Articles.Clear());
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(evidenceSystem.TryFileEvidence(blackBoxReport, user), Is.True);
            Assert.That(evidenceSystem.TryFileEvidence(insurance, user), Is.True);
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);

            persisted = storySystem.GetStatusSnapshot();
            Assert.That(persisted.InsurancePayouts, Is.EqualTo(2));
            Assert.That(persisted.BlackBoxRecoveries, Is.EqualTo(1));
            Assert.That(persisted.ReputationLedger, Contains.Key("Salvage"));
        });

        await server.WaitPost(() => entManager.DeleteEntity(host));
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(sectorService.GetServiceEntity().IsValid(), Is.False);

            host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(persisted, Is.Not.Null);
            Assert.That(sectorService.GetServiceEntity().IsValid(), Is.True);

            var restored = storySystem.GetStatusSnapshot();
            Assert.That(restored.InsurancePayouts, Is.EqualTo(persisted!.InsurancePayouts));
            Assert.That(restored.BlackBoxRecoveries, Is.EqualTo(persisted.BlackBoxRecoveries));
            Assert.That(restored.CompanyRecords, Is.EqualTo(persisted.CompanyRecords));
            Assert.That(restored.ShipRecords, Is.EqualTo(persisted.ShipRecords));
            Assert.That(restored.ActiveHazards, Is.EqualTo(persisted.ActiveHazards));
            Assert.That(restored.ReputationLedger, Contains.Key("Salvage"));
            Assert.That(restored.Hazards.Single(hazard => hazard.Story == "LuaMSectorStoryBlackBoxPaid").Resolved, Is.True);
            Assert.That(restored.Reputation.Single(entry => entry.Target == "Salvage").RewardBonus,
                Is.EqualTo(blackBoxStory.ReputationDelta * LuaMSectorStorySystem.ReputationRewardStep));

            var restoredContract = bountyContracts.GetContracts("Distress")
                .Single(contract => contract.Name == blackBoxStory.ContractName);
            Assert.That(restoredContract.Reward,
                Is.EqualTo(blackBoxStory.ContractReward + blackBoxStory.HazardRewardBonus + blackBoxStory.ReputationDelta * LuaMSectorStorySystem.ReputationRewardStep));
            Assert.That(restoredContract.Description, Does.Contain("[REP +"));

            var unlockedContract = bountyContracts.GetContracts("Distress")
                .Single(contract => contract.Name == trustedStory.ContractName);
            Assert.That(unlockedContract.Reward,
                Is.EqualTo(trustedStory.ContractReward + trustedStory.HazardRewardBonus + blackBoxStory.ReputationDelta * LuaMSectorStorySystem.ReputationRewardStep));
            Assert.That(unlockedContract.Description, Does.Contain("[REP +"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorMemoryAdminCommandsReportExportAndReset()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var bountyContracts = entManager.System<BountyContractSystem>();

        EntityUid user = default;
        EntityUid insurance = default;
        EntityUid blackBoxReport = default;
        string exportedJson = string.Empty;
        LuaMSectorStatusSnapshot? exportedStatus = null;
        var donationShopBackup = new ResPath("/luam/donation-shop.json");

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            insurance = entManager.SpawnEntity("PaperLuaMSalvageInsuranceForm", MapCoordinates.Nullspace);
            blackBoxReport = entManager.SpawnEntity("PaperLuaMBlackBoxReport", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await server.WaitPost(() => SectorNewsComponent.Articles.Clear());
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(evidenceSystem.TryFileEvidence(blackBoxReport, user), Is.True);
            Assert.That(evidenceSystem.TryFileEvidence(insurance, user), Is.True);
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);
            Assert.That(storySystem.GetStatusSnapshot().ReputationLedger, Contains.Key("Salvage"));
            Assert.That(storySystem.GetMemoryBackups().Where(path => path != donationShopBackup), Is.Empty);
        });

        await server.WaitPost(() =>
        {
            console.ExecuteCommand("luam_sector_resolve LuaMSectorStoryFalseQuiet Manual console close");
            console.ExecuteCommand("luam_sector_status");
            console.ExecuteCommand("luam_sector_export");
            console.ExecuteCommand("luam_sector_export_file admin-test-backup");
            console.ExecuteCommand("luam_sector_backups");
            console.ExecuteCommand("luam_sector_history 5");
            console.ExecuteCommand("luam_sector_seed_distress Runtime tow lead|DSV Console|2400|Tow a disabled shuttle from the outer beacon.|Comms blackout near the tow lane.|Distress|1");
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            exportedStatus = storySystem.GetStatusSnapshot();
            Assert.That(exportedStatus.ReputationLedger, Contains.Key("Salvage"));
            Assert.That(exportedStatus.ReputationLedger, Contains.Key("Station Records"));
            Assert.That(exportedStatus.Hazards.Select(hazard => hazard.Story), Does.Contain("LuaMSectorRuntimeDistress001"));
            Assert.That(SectorNewsComponent.Articles.Any(article => article.Title == "Runtime tow lead"), Is.True);
            Assert.That(bountyContracts.GetContracts("Distress")
                    .Any(contract => contract.Name == "Runtime tow lead" &&
                                     contract.Vessel == "DSV Console" &&
                                     contract.Description.Contains("Comms blackout near the tow lane.")),
                Is.True);
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);
            Assert.That(storySystem.TryExportMemoryJson(out exportedJson), Is.True);
            Assert.That(exportedJson, Does.Contain("Station Records"));
            Assert.That(exportedJson, Does.Contain("LuaMSectorRuntimeDistress001"));
            Assert.That(exportedJson, Does.Contain("Runtime tow lead"));
            Assert.That(resources.UserData.Exists(SectorMemoryBackupPath), Is.True);
            Assert.That(storySystem.GetMemoryBackups().Where(path => path != donationShopBackup), Is.EquivalentTo(new[] { SectorMemoryBackupPath }));
        });

        await server.WaitPost(() => console.ExecuteCommand("luam_sector_reset confirm"));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var reset = storySystem.GetStatusSnapshot();
            Assert.That(reset.InsurancePayouts, Is.EqualTo(0));
            Assert.That(reset.BlackBoxRecoveries, Is.EqualTo(0));
            Assert.That(reset.CompanyRecords, Is.EqualTo(0));
            Assert.That(reset.ShipRecords, Is.EqualTo(0));
            Assert.That(reset.ActiveHazards, Is.EqualTo(reset.TotalStories));
            Assert.That(reset.ReputationLedger, Is.Empty);
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.False);
        });

        await server.WaitPost(() => console.ExecuteCommand("luam_sector_import_file admin-test-backup"));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(exportedStatus, Is.Not.Null);

            var imported = storySystem.GetStatusSnapshot();
            Assert.That(imported.ReputationLedger, Contains.Key("Salvage"));
            Assert.That(imported.ReputationLedger, Contains.Key("Station Records"));
            Assert.That(imported.InsurancePayouts, Is.EqualTo(exportedStatus!.InsurancePayouts));
            Assert.That(imported.BlackBoxRecoveries, Is.EqualTo(exportedStatus.BlackBoxRecoveries));
            Assert.That(imported.CompanyRecords, Is.EqualTo(exportedStatus.CompanyRecords));
            Assert.That(imported.ShipRecords, Is.EqualTo(exportedStatus.ShipRecords));
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);
        });

        await server.WaitPost(() => console.ExecuteCommand("luam_sector_delete_backup admin-test-backup"));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);
            Assert.That(resources.UserData.Exists(SectorMemoryBackupPath), Is.False);
            Assert.That(storySystem.GetMemoryBackups().Where(path => path != donationShopBackup), Is.Empty);
        });

        await server.WaitPost(() => console.ExecuteCommand("luam_sector_reset confirm"));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var reset = storySystem.GetStatusSnapshot();
            Assert.That(reset.ReputationLedger, Is.Empty);
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.False);
            Assert.That(resources.UserData.Exists(SectorMemoryBackupPath), Is.False);
        });

        await server.WaitPost(() => console.ExecuteCommand($"luam_sector_import {exportedJson}"));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(exportedStatus, Is.Not.Null);

            var imported = storySystem.GetStatusSnapshot();
            Assert.That(imported.ReputationLedger, Contains.Key("Salvage"));
            Assert.That(imported.ReputationLedger, Contains.Key("Station Records"));
            Assert.That(imported.InsurancePayouts, Is.EqualTo(exportedStatus!.InsurancePayouts));
            Assert.That(imported.BlackBoxRecoveries, Is.EqualTo(exportedStatus.BlackBoxRecoveries));
            Assert.That(imported.CompanyRecords, Is.EqualTo(exportedStatus.CompanyRecords));
            Assert.That(imported.ShipRecords, Is.EqualTo(exportedStatus.ShipRecords));
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DistressBeaconUseSeedsRuntimeSectorStory()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var beaconSystem = entManager.System<LuaMDistressBeaconSystem>();
        var leadReportSystem = entManager.System<LuaMSectorLeadReportSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var bountyContracts = entManager.System<BountyContractSystem>();

        EntityUid user = default;
        EntityUid beacon = default;
        EntityUid board = default;
        EntityUid report = default;
        EntityUid coordinatePacket = default;
        EntityUid closureReport = default;
        MapId mapId = default;
        var initialArticleCount = 0;
        var initialDistressContractCount = 0;
        var priorityRescueStory = prototypeManager.Index<LuaMSectorStoryPrototype>("LuaMSectorStoryPriorityRescueLane");
        string expectedBeaconLocation = string.Empty;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var host = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);

            mapSystem.CreateMap(out mapId);
            var beaconCoordinates = new MapCoordinates(new Vector2(12.5f, -7.0f), mapId);

            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            beacon = entManager.SpawnEntity("LuaMDistressBeacon", beaconCoordinates);
            expectedBeaconLocation = beaconSystem.FormatBeaconLocation(
                entManager.GetComponent<TransformComponent>(beacon).Coordinates);
            board = entManager.SpawnEntity("ComputerLuaMSectorRumorBoard", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(expectedBeaconLocation, Does.StartWith("GPS карта "));
            Assert.That(expectedBeaconLocation, Does.Contain("x 12.5"));
            Assert.That(expectedBeaconLocation, Does.Contain("y -7.0"));
            initialArticleCount = SectorNewsComponent.Articles.Count;
            initialDistressContractCount = bountyContracts.GetContracts("Distress").Count();
        });

        await server.WaitPost(() =>
        {
            var use = new UseInHandEvent(user);
            entManager.EventBus.RaiseLocalEvent(beacon, use);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.Hazards.Select(hazard => hazard.Story), Does.Contain("LuaMSectorRuntimeDistress001"));
            Assert.That(SectorNewsComponent.Articles.Count, Is.GreaterThan(initialArticleCount));
            Assert.That(SectorNewsComponent.Articles.Any(article => article.Content.Contains("Координаты маяка:")), Is.True);
            Assert.That(SectorNewsComponent.Articles.Any(article => article.Content.Contains(expectedBeaconLocation)), Is.True);
            Assert.That(bountyContracts.GetContracts("Distress").Count(), Is.GreaterThan(initialDistressContractCount));
            Assert.That(bountyContracts.GetContracts("Distress")
                    .Any(contract => contract.Description.Contains("Координаты маяка:") &&
                                     contract.Description.Contains(expectedBeaconLocation)),
                Is.True);
            Assert.That(storySystem.TryExportMemoryJson(out var json), Is.True);
            Assert.That(json, Does.Contain("LuaMSectorRuntimeDistress001"));
            Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);
        });

        await server.WaitPost(() =>
        {
            var use = new UseInHandEvent(user);
            entManager.EventBus.RaiseLocalEvent(beacon, use);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var runtimeStories = storySystem.GetStatusSnapshot().Hazards
                .Count(hazard => hazard.Story.ToString().StartsWith("LuaMSectorRuntimeDistress", StringComparison.Ordinal));
            Assert.That(runtimeStories, Is.EqualTo(1));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintLeadReport(board, user, out report), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(report, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("# LuaM sector lead report"));
            Assert.That(paper.Content, Does.Contain("## Active leads"));
            Assert.That(paper.Content, Does.Contain("LuaMSectorRuntimeDistress001").Or.Contain("Distress"));
            Assert.That(paper.Content, Does.Contain("Координаты маяка:"));
            Assert.That(paper.Content, Does.Contain(expectedBeaconLocation));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeCoordinatePacket(board, user, out coordinatePacket), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(coordinatePacket, out var paper), Is.True);
            Assert.That(entManager.HasComponent<LuaMSectorEvidenceComponent>(coordinatePacket), Is.False);
            Assert.That(paper!.Content, Does.Contain("# Координатный пакет аварийного сигнала"));
            Assert.That(paper.Content, Does.Contain("LuaMSectorRuntimeDistress001"));
            Assert.That(paper.Content, Does.Contain("## Активная метка"));
            Assert.That(paper.Content, Does.Contain("нет активной динамической метки"));
            Assert.That(paper.Content, Does.Contain("## Маршрут"));
            Assert.That(paper.Content, Does.Contain("## Шаги маршрута"));
            Assert.That(paper.Content, Does.Contain(expectedBeaconLocation));
            Assert.That(paper.Content, Does.Contain("[ ] перенести активную метку на навигационную карту"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeClosureReport(board, user, out closureReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(closureReport, out var paper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(closureReport, out var evidence), Is.True);
            Assert.That(evidence!.Note, Does.Contain("no dynamic route marker"));
            Assert.That(paper!.Content, Does.Contain("# Итоговый отчет по аварийному сигналу"));
            Assert.That(paper.Content, Does.Contain("LuaMSectorRuntimeDistress001"));
            Assert.That(paper.Content, Does.Contain(expectedBeaconLocation));
            Assert.That(paper.Content, Does.Contain("## Active route status"));
            Assert.That(paper.Content, Does.Contain("No dynamic route marker found."));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(evidenceSystem.TryFileEvidence(closureReport, user), Is.True);
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.Hazards.Single(hazard => hazard.Story == "LuaMSectorRuntimeDistress001").Resolved, Is.True);
            Assert.That(status.ReputationLedger, Contains.Key("Distress"));
            Assert.That(status.LockedLeads.Select(lead => lead.Story), Does.Not.Contain(priorityRescueStory.ID));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.Hazards.Select(hazard => hazard.Story), Does.Contain(priorityRescueStory.ID));

            var priorityContract = bountyContracts.GetContracts("Distress")
                .Single(contract => contract.Name == priorityRescueStory.ContractName);
            Assert.That(priorityContract.Reward,
                Is.EqualTo(priorityRescueStory.ContractReward +
                           priorityRescueStory.HazardRewardBonus +
                           LuaMSectorStorySystem.ReputationRewardStep));
            Assert.That(priorityContract.Description, Does.Contain("[REP +"));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeClosureReport(board, user, out _), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DynamicEventGeneratorSeedsRuntimeLead()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var leadReportSystem = entManager.System<LuaMSectorLeadReportSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var bountyContracts = entManager.System<BountyContractSystem>();
        LuaMSectorStoryRecord? firstRecord = null;
        LuaMSectorStoryRecord? secondRecord = null;
        EntityUid board = default;
        EntityUid user = default;
        EntityUid coordinatePacket = default;
        EntityUid runtimeClosureReport = default;
        EntityUid markerFieldPacket = default;
        EntityUid firstMarkerUid = default;
        var firstError = string.Empty;
        var blockedError = string.Empty;
        var secondError = string.Empty;
        var routePingResult = string.Empty;
        bool firstGenerated = false;
        bool runtimeClosureFiled = false;
        bool blockedGenerated = true;
        bool secondGenerated = false;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out var marker, out _))
            {
                entManager.DeleteEntity(marker);
            }

            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out var site, out _))
            {
                entManager.DeleteEntity(site);
            }

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(3.5f, -9.0f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            board = entManager.SpawnEntity("ComputerLuaMSectorRumorBoard", MapCoordinates.Nullspace);
            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitPost(() =>
        {
            firstGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out firstRecord,
                out firstError,
                templateId: "field-repair",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(3.5f, -9.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(firstGenerated, Is.True, firstError);
            Assert.That(firstRecord, Is.Not.Null);
            Assert.That(firstRecord!.Story, Is.EqualTo("LuaMSectorRuntimeDistress001"));
            Assert.That(firstRecord.Title, Is.EqualTo("Запрос полевого ремонта"));
            Assert.That(firstRecord.ContractDescription, Does.Contain("Координаты маркера: GPS карта"));
            Assert.That(firstRecord.ContractDescription, Does.Contain("x 3.5"));
            Assert.That(firstRecord.ContractDescription, Does.Contain("y -9.0"));
            Assert.That(firstRecord.ContractDescription, Does.Contain("Сгенерировано для активных операторов: 0"));
            Assert.That(firstRecord.ContractDescription, Does.Contain("Репутация сектора Salvage: 0"));
            Assert.That(firstRecord.Hazard, Does.Contain("Класс динамического события: Salvage"));
            Assert.That(firstRecord.ReputationTarget, Is.EqualTo("Salvage"));
            Assert.That(SectorNewsComponent.Articles.Any(article => article.Title == "Запрос полевого ремонта"), Is.True);
            Assert.That(bountyContracts.GetContracts("Distress")
                    .Any(contract => contract.Name == "Запрос полевого ремонта" &&
                                     contract.Description.Contains("Координаты маркера: GPS карта") &&
                                     contract.Description.Contains("x 3.5") &&
                                     contract.Description.Contains("y -9.0")),
                Is.True);
        Assert.That(storySystem.TryExportMemoryJson(out var json), Is.True);
        Assert.That(json, Does.Contain("LuaMSectorRuntimeDistress001"));
        Assert.That(resources.UserData.Exists(SectorMemoryPath), Is.True);

            var markers = new List<(EntityUid Uid, LuaMDynamicEventMarkerComponent Marker, NavMapBeaconComponent Nav, TransformComponent Xform)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent, TransformComponent>();
            while (markerQuery.MoveNext(out var uid, out var marker, out var nav, out var xform))
            {
                markers.Add((uid, marker, nav, xform));
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            var generatedMarker = markers.Single();
            firstMarkerUid = generatedMarker.Uid;
            Assert.That(generatedMarker.Marker.Story, Is.EqualTo("LuaMSectorRuntimeDistress001"));
            Assert.That(generatedMarker.Marker.TemplateId, Is.EqualTo("field-repair"));
            Assert.That(generatedMarker.Marker.CreatedBy, Is.EqualTo("integration-test"));
            Assert.That(generatedMarker.Marker.MarkerLocation, Does.StartWith("GPS карта "));
            Assert.That(generatedMarker.Marker.MarkerLocation, Does.Contain("x 3.5"));
            Assert.That(generatedMarker.Marker.MarkerLocation, Does.Contain("y -9.0"));
            Assert.That(generatedMarker.Marker.PaperPrototype, Is.EqualTo("Paper"));
            Assert.That(generatedMarker.Marker.RoutePingCount, Is.EqualTo(0));
            Assert.That(generatedMarker.Nav.DefaultText, Is.EqualTo("station-beacon-luam-dynamic-repair"));
            Assert.That(generatedMarker.Nav.Color, Is.EqualTo(Color.FromHex("#FFD16696")));
            Assert.That(generatedMarker.Nav.Text, Is.Not.Null.And.Not.Empty);
            Assert.That(generatedMarker.Nav.Enabled, Is.True);
            Assert.That(generatedMarker.Xform.MapID, Is.EqualTo(mapId));
            Assert.That(entManager.HasComponent<LuaMDistressBeaconComponent>(generatedMarker.Uid), Is.False);

            var automationState = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automationState.CanPingRoute, Is.True);
            Assert.That(automationState.ActiveRouteStory, Is.EqualTo("LuaMSectorRuntimeDistress001"));
            Assert.That(automationState.ActiveRouteMarker, Does.Contain("x 3.5"));
            Assert.That(automationState.DispatchTier, Is.EqualTo("standard"));
            Assert.That(automationState.DispatchReputationScore, Is.EqualTo(0));
            Assert.That(automationState.DispatchCooldownReductionPercent, Is.EqualTo(0));
            Assert.That(automationState.DispatchCooldownMinSeconds, Is.EqualTo(LuaMSectorDynamicEventSystem.MinCooldownSeconds));
            Assert.That(automationState.DispatchCooldownMaxSeconds, Is.EqualTo(LuaMSectorDynamicEventSystem.MaxCooldownSeconds));

            var preferredState = dynamicEvents.BuildPreferredProcessUiEntries();
            var lockedBlackBox = preferredState.Single(process => process.TemplateId == "black-box-echo");
            Assert.That(lockedBlackBox.Unlocked, Is.False);
            Assert.That(lockedBlackBox.CurrentReputation, Is.EqualTo(0));
            Assert.That(lockedBlackBox.RequiredReputation, Is.EqualTo(LuaMSectorDynamicEventSystem.PreferredProcessRequiredReputation));
            Assert.That(lockedBlackBox.BlockReason, Does.Contain("Нужна репутация Salvage: 0/1."));

            var terminalBlackBox = leadReportSystem.BuildTerminalUiState().PreferredProcesses.Single(process => process.TemplateId == "black-box-echo");
            Assert.That(terminalBlackBox.Unlocked, Is.False);

            var mapNodes = dynamicEvents.BuildSectorMapUiEntries();
            Assert.That(mapNodes.Select(node => node.Kind), Does.Contain("маршрут"));
            Assert.That(mapNodes.Select(node => node.Kind), Does.Contain("точка"));
            var routeNode = mapNodes.Single(node => node.Kind == "маршрут");
            Assert.That(routeNode.Title, Is.EqualTo("Запрос полевого ремонта"));
            Assert.That(routeNode.State, Is.EqualTo("активный маршрут"));
            Assert.That(routeNode.StoryId, Is.EqualTo("LuaMSectorRuntimeDistress001"));
            Assert.That(routeNode.TemplateId, Is.EqualTo("field-repair"));
            Assert.That(routeNode.Location, Does.Contain("x 3.5"));
            Assert.That(routeNode.RoutePingCount, Is.EqualTo(0));
            Assert.That(routeNode.Active, Is.True);
            var terminalRouteNode = leadReportSystem.BuildTerminalUiState().SectorMapNodes.Single(node => node.Kind == "маршрут");
            Assert.That(terminalRouteNode.NodeId, Is.EqualTo("route:LuaMSectorRuntimeDistress001:field-repair"));

            var siteNotes = new List<(EntityUid Uid, LuaMDynamicEventSiteObjectComponent Site, PaperComponent Paper, TransformComponent Xform)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, PaperComponent, TransformComponent>();
            while (siteQuery.MoveNext(out var uid, out var site, out var paper, out var xform))
            {
                siteNotes.Add((uid, site, paper, xform));
            }

            Assert.That(siteNotes, Has.Count.EqualTo(1));
            var siteNote = siteNotes.Single();
            Assert.That(siteNote.Site.Story, Is.EqualTo("LuaMSectorRuntimeDistress001"));
            Assert.That(siteNote.Site.TemplateId, Is.EqualTo("field-repair"));
            Assert.That(siteNote.Site.CreatedBy, Is.EqualTo("integration-test"));
            Assert.That(siteNote.Site.MarkerLocation, Does.Contain("x 3.5"));
            Assert.That(siteNote.Site.SiteObjectKind, Is.EqualTo("field-note"));
            Assert.That(siteNote.Xform.MapID, Is.EqualTo(mapId));
            Assert.That(entManager.HasComponent<LuaMSectorEvidenceComponent>(siteNote.Uid), Is.True);
            Assert.That(siteNote.Paper.Content, Does.Contain("# Заметка места динамического события"));
            Assert.That(siteNote.Paper.Content, Does.Contain("Сюжет: LuaMSectorRuntimeDistress001"));
            Assert.That(siteNote.Paper.Content, Does.Contain("Шаблон: field-repair"));
            Assert.That(siteNote.Paper.Content, Does.Contain("Точка: GPS карта"));
            Assert.That(siteNote.Paper.Content, Does.Contain("Зацепка: Запрос полевого ремонта"));
            Assert.That(siteNote.Paper.Content, Does.Contain("Класс динамического события: Salvage"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out routePingResult), Is.True, routePingResult);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(routePingResult, Does.Contain("Пинг маршрута 1 отправлен"));
            Assert.That(entManager.TryGetComponent<LuaMDynamicEventMarkerComponent>(firstMarkerUid, out var marker), Is.True);
            Assert.That(entManager.TryGetComponent<NavMapBeaconComponent>(firstMarkerUid, out var nav), Is.True);
            Assert.That(marker!.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("Пинг 1"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("GPS карта"));
            Assert.That(nav!.Enabled, Is.True);
            Assert.That(nav.Text, Does.Contain("[PING 1]"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));

            var automationState = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automationState.CanPingRoute, Is.True);
            Assert.That(automationState.RoutePingCount, Is.EqualTo(1));
            Assert.That(automationState.LastRoutePing, Does.Contain("Пинг 1"));

            var routeNode = dynamicEvents.BuildSectorMapUiEntries().Single(node => node.Kind == "маршрут");
            Assert.That(routeNode.RoutePingCount, Is.EqualTo(1));
            Assert.That(routeNode.Detail, Does.Contain("Пинг 1"));
            Assert.That(routeNode.State, Is.EqualTo("активный маршрут"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(firstMarkerUid, user, out markerFieldPacket), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(markerFieldPacket, out var paper), Is.True);
            Assert.That(entManager.HasComponent<LuaMSectorEvidenceComponent>(markerFieldPacket), Is.False);
            Assert.That(paper!.Content, Does.Contain("# Полевой пакет динамического события"));
            Assert.That(paper.Content, Does.Contain("Сюжет: LuaMSectorRuntimeDistress001"));
            Assert.That(paper.Content, Does.Contain("Шаблон: field-repair"));
            Assert.That(paper.Content, Does.Contain("Маркер: GPS карта"));
            Assert.That(paper.Content, Does.Contain("x 3.5"));
            Assert.That(paper.Content, Does.Contain("y -9.0"));
            Assert.That(paper.Content, Does.Contain("Зацепка: Запрос полевого ремонта"));
            Assert.That(paper.Content, Does.Contain("Судно: Поврежденный отвод ретранслятора"));
            Assert.That(paper.Content, Does.Contain("## Полевые проверки"));
            Assert.That(paper.Content, Does.Contain("[ ] позиция маркера достигнута"));
            Assert.That(paper.Content, Does.Contain("Класс динамического события: Salvage"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeCoordinatePacket(board, user, out coordinatePacket), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(coordinatePacket, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("# Координатный пакет аварийного сигнала"));
            Assert.That(paper.Content, Does.Contain("## Активная метка"));
            Assert.That(paper.Content, Does.Contain("Координаты: GPS карта"));
            Assert.That(paper.Content, Does.Contain("x 3.5"));
            Assert.That(paper.Content, Does.Contain("y -9.0"));
            Assert.That(paper.Content, Does.Contain("Шаблон: field-repair"));
            Assert.That(paper.Content, Does.Contain("Оформил: integration-test"));
            Assert.That(paper.Content, Does.Contain("## Шаги маршрута"));
            Assert.That(paper.Content, Does.Contain("[ ] подойти вручную и проверить локальные угрозы"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeClosureReport(board, user, out runtimeClosureReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(runtimeClosureReport, out var paper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(runtimeClosureReport, out var evidence), Is.True);
            Assert.That(evidence!.Note, Does.Contain("marker field-repair"));
            Assert.That(evidence.Note, Does.Contain("route pings 1/2"));
            Assert.That(evidence.Note, Does.Contain("field packet pending"));
            Assert.That(paper!.Content, Does.Contain("## Active route status"));
            Assert.That(paper.Content, Does.Contain("Route pings: 1/2"));
            Assert.That(paper.Content, Does.Contain("Last route ping:"));
            Assert.That(paper.Content, Does.Contain("Field report evidence requires more route pings."));
        });

        await server.WaitPost(() =>
        {
            blockedGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out _,
                out blockedError,
                templateId: "black-box-echo",
                ignorePlayerGate: true);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(blockedGenerated, Is.False);
            Assert.That(blockedError, Does.Contain("Открытая активная зацепка сектора LuaM уже существует"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out routePingResult), Is.True, routePingResult);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<LuaMDynamicEventMarkerComponent>(firstMarkerUid, out var marker), Is.True);
            Assert.That(marker!.RoutePingCount, Is.EqualTo(2));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("GPS"));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingCount, Is.EqualTo(2));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().CanPingRoute, Is.False);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingBlockReason, Does.Contain("стабилизирован"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeClosureReport(board, user, out runtimeClosureReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(runtimeClosureReport, out var paper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(runtimeClosureReport, out var evidence), Is.True);
            Assert.That(evidence!.Note, Does.Contain("route pings 2/2"));
            Assert.That(evidence.Note, Does.Contain("field packet ready"));
            Assert.That(evidence.Note, Does.Contain("stabilized route field packet filed"));
            Assert.That(evidence.Note, Does.Contain("field-repair"));
            Assert.That(evidence.Note, Does.Contain("x 3.5"));
            Assert.That(paper!.Content, Does.Contain("Route pings: 2/2"));
            Assert.That(paper.Content, Does.Contain("Field report: optional; click the LuaM marker to close the task, or print a report as evidence."));
            Assert.That(paper.Content, Does.Contain("Route calibration handoff:"));
            Assert.That(paper.Content, Does.Contain("stabilized route field packet filed"));
            Assert.That(paper.Content, Does.Contain("field-repair"));
            Assert.That(paper.Content, Does.Contain("x 3.5"));
        });

        await server.WaitPost(() =>
        {
            runtimeClosureFiled = evidenceSystem.TryFileEvidence(runtimeClosureReport, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(runtimeClosureFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == "LuaMSectorRuntimeDistress001").Resolved, Is.True);
            var automationState = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automationState.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automationState.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(automationState.RouteCalibrationSource, Does.Contain("x 3.5"));
        });

        await server.WaitPost(() =>
        {

            var preferredState = dynamicEvents.BuildPreferredProcessUiEntries();
            var unlockedBlackBox = preferredState.Single(process => process.TemplateId == "black-box-echo");
            Assert.That(unlockedBlackBox.Unlocked, Is.True);
            Assert.That(unlockedBlackBox.CurrentReputation, Is.EqualTo(1));
            Assert.That(unlockedBlackBox.Tier, Is.EqualTo("known"));
            Assert.That(unlockedBlackBox.ReputationBonus, Is.EqualTo(LuaMSectorStorySystem.ReputationRewardStep));
            var dispatchState = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(dispatchState.DispatchTier, Is.EqualTo("known"));
            Assert.That(dispatchState.DispatchReputationScore, Is.EqualTo(1));
            Assert.That(dispatchState.DispatchCooldownReductionPercent, Is.EqualTo(LuaMSectorDynamicEventSystem.DispatchCooldownReductionPerReputation));
            Assert.That(dispatchState.DispatchCooldownMinSeconds, Is.EqualTo(3528));
            Assert.That(dispatchState.DispatchCooldownMaxSeconds, Is.EqualTo(7056));
            Assert.That(leadReportSystem.BuildTerminalUiState().PreferredProcesses
                    .Single(process => process.TemplateId == "black-box-echo")
                    .Unlocked,
                Is.True);

            secondGenerated = dynamicEvents.TryGeneratePreferredDynamicEvent(
                "integration-test",
                "black-box-echo",
                out secondRecord,
                out secondError,
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(-2.0f, 6.25f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(secondGenerated, Is.True, secondError);
            Assert.That(secondRecord, Is.Not.Null);
            Assert.That(secondRecord!.Story, Is.EqualTo("LuaMSectorRuntimeDistress002"));
            Assert.That(secondRecord.Title, Is.EqualTo("Эхо черного ящика"));
            Assert.That(secondRecord.ContractDescription, Does.Contain("Координаты маркера: GPS карта"));
            Assert.That(secondRecord.ContractDescription, Does.Contain("x -2.0"));
            Assert.That(secondRecord.ContractDescription, Does.Contain("y 6.3"));
            Assert.That(secondRecord.ContractDescription, Does.Contain("Репутация сектора Salvage: 1"));
            Assert.That(storySystem.GetStatusSnapshot().ReputationLedger, Contains.Key("Salvage"));

            var markers = new List<(LuaMDynamicEventMarkerComponent Marker, NavMapBeaconComponent Nav)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            while (markerQuery.MoveNext(out _, out var marker, out var nav))
            {
                markers.Add((marker, nav));
            }

            Assert.That(markers.Select(marker => marker.Marker.Story), Is.EquivalentTo(new[]
            {
                "LuaMSectorRuntimeDistress002",
            }));
            var remainingMarker = markers.Single();
            Assert.That(remainingMarker.Marker.TemplateId, Is.EqualTo("black-box-echo"));
            Assert.That(remainingMarker.Marker.MarkerLocation, Does.Contain("x -2.0"));
            Assert.That(remainingMarker.Marker.MarkerLocation, Does.Contain("y 6.3"));
            Assert.That(remainingMarker.Marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(remainingMarker.Marker.LastRoutePingActor, Is.EqualTo("route calibration"));
            Assert.That(remainingMarker.Marker.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(remainingMarker.Marker.RouteCalibrationSource, Does.Contain("x 3.5"));
            Assert.That(remainingMarker.Nav.DefaultText, Is.EqualTo("station-beacon-luam-dynamic-salvage"));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationCredits, Is.EqualTo(0));

            var siteNotes = new List<LuaMDynamicEventSiteObjectComponent>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out _, out var site))
            {
                siteNotes.Add(site);
            }

            Assert.That(siteNotes.Select(site => site.Story), Is.EquivalentTo(new[]
            {
                "LuaMSectorRuntimeDistress002",
            }));
            var remainingSiteNote = siteNotes.Single();
            Assert.That(remainingSiteNote.TemplateId, Is.EqualTo("black-box-echo"));
            Assert.That(remainingSiteNote.MarkerLocation, Does.Contain("x -2.0"));
            Assert.That(remainingSiteNote.MarkerLocation, Does.Contain("y 6.3"));

            var mapNodes = dynamicEvents.BuildSectorMapUiEntries();
            Assert.That(mapNodes.Select(node => node.StoryId), Is.All.EqualTo("LuaMSectorRuntimeDistress002"));
            Assert.That(mapNodes.Single(node => node.Kind == "маршрут").TemplateId, Is.EqualTo("black-box-echo"));
            Assert.That(mapNodes.Single(node => node.Kind == "точка").Location, Does.Contain("x -2.0"));
            Assert.That(leadReportSystem.BuildTerminalUiState().SectorMapNodes.Select(node => node.TemplateId),
                Does.Contain("black-box-echo"));
        });

        await server.WaitPost(() =>
        {
            entManager.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var markers = new List<LuaMDynamicEventMarkerComponent>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out _, out var marker))
            {
                markers.Add(marker);
            }

            Assert.That(markers, Is.Empty);

            var siteNotes = new List<LuaMDynamicEventSiteObjectComponent>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out _, out var site))
            {
                siteNotes.Add(site);
            }

            Assert.That(siteNotes, Is.Empty);
            Assert.That(dynamicEvents.BuildSectorMapUiEntries(), Is.Empty);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DynamicEventGeneratorSeedsMonolithArtifactEvent()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();

        LuaMSectorStoryRecord? record = null;
        EntityUid user = default;
        EntityUid researchReport = default;
        var error = string.Empty;
        var generated = false;
        var filed = false;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out var marker, out _))
            {
                entManager.DeleteEntity(marker);
            }

            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out var site, out _))
            {
                entManager.DeleteEntity(site);
            }

            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            while (hazardQuery.MoveNext(out var hazard, out _))
            {
                entManager.DeleteEntity(hazard);
            }

            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
            while (driftQuery.MoveNext(out var drift, out _))
            {
                entManager.DeleteEntity(drift);
            }

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(8.0f, -4.0f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitPost(() =>
        {
            generated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out record,
                out error,
                templateId: LuaMSectorDynamicEventSystem.MonolithArtifactTemplateId,
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(8.0f, -4.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(generated, Is.True, error);
            Assert.That(record, Is.Not.Null);
            Assert.That(record!.Title, Is.EqualTo("Обзор артефакта Монолита"));
            Assert.That(record.ReputationTarget, Is.EqualTo("Research"));
            Assert.That(record.ContractDescription, Does.Contain("Репутация сектора Research: 0"));
            Assert.That(record.ContractDescription, Does.Contain("Фиолетовый фрагмент Монолита"));
            Assert.That(record.Hazard, Does.Contain("Класс динамического события: Research"));
            Assert.That(record.Hazard, Does.Contain("Неизвестное резонансное поле"));
            Assert.That(SectorNewsComponent.Articles.Any(article => article.Title == "Обзор артефакта Монолита"), Is.True);

            var preferred = dynamicEvents.BuildPreferredProcessUiEntries()
                .Single(process => process.TemplateId == LuaMSectorDynamicEventSystem.MonolithArtifactTemplateId);
            Assert.That(preferred.ReputationTarget, Is.EqualTo("Research"));
            Assert.That(preferred.Unlocked, Is.False);
            Assert.That(preferred.BlockReason, Does.Contain("Нужна репутация Research: 0/1."));

            var markers = new List<(LuaMDynamicEventMarkerComponent Marker, NavMapBeaconComponent Nav)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            while (markerQuery.MoveNext(out _, out var marker, out var nav))
            {
                markers.Add((marker, nav));
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            var generatedMarker = markers.Single();
            Assert.That(generatedMarker.Marker.TemplateId, Is.EqualTo(LuaMSectorDynamicEventSystem.MonolithArtifactTemplateId));
            Assert.That(generatedMarker.Marker.MarkerLocation, Does.Contain("x 8.0"));
            Assert.That(generatedMarker.Marker.MarkerLocation, Does.Contain("y -4.0"));
            Assert.That(generatedMarker.Nav.DefaultText, Is.EqualTo("station-beacon-luam-dynamic-monolith"));
            Assert.That(generatedMarker.Nav.Color, Is.EqualTo(Color.FromHex("#A35CFFCC")));

            var siteObjects = new List<(EntityUid Uid, LuaMDynamicEventSiteObjectComponent Site, TransformComponent Xform)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, TransformComponent>();
            while (siteQuery.MoveNext(out var uid, out var site, out var xform))
            {
                siteObjects.Add((uid, site, xform));
            }

            Assert.That(siteObjects, Has.Count.EqualTo(6));
            Assert.That(siteObjects.Select(site => site.Site.TemplateId), Is.All.EqualTo(LuaMSectorDynamicEventSystem.MonolithArtifactTemplateId));
            Assert.That(siteObjects.Select(site => site.Site.SiteObjectKind), Does.Contain("field-note"));
            Assert.That(siteObjects.Select(site => site.Site.SiteObjectKind), Does.Contain("осколок Монолита"));
            Assert.That(siteObjects.Select(site => site.Site.SiteObjectKind), Does.Contain("контейнер"));
            Assert.That(siteObjects.Select(site => site.Site.SiteObjectKind), Does.Contain("сканер аномалий"));
            Assert.That(siteObjects.Select(site => site.Site.SiteObjectKind), Does.Contain("резонатор Монолита"));
            Assert.That(siteObjects.Select(site => site.Site.SiteObjectKind), Does.Contain("исследовательский отчет"));
            Assert.That(siteObjects.Select(site => site.Xform.MapID), Is.All.EqualTo(mapId));

            var reportSite = siteObjects.Single(site => site.Site.SiteObjectKind == "исследовательский отчет");
            researchReport = reportSite.Uid;
            Assert.That(entManager.TryGetComponent<PaperComponent>(researchReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("# Исследовательский отчет LuaM по Монолиту"));
            Assert.That(paper.Content, Does.Contain("Шаблон: monolith-artifact"));
            Assert.That(paper.Content, Does.Contain("[ ] отклик резонатора проверен"));
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(researchReport, out var evidence), Is.True);
            Assert.That(evidence!.Story, Is.EqualTo(record.Story));
            Assert.That(evidence.AcknowledgeHazard, Is.True);
            Assert.That(evidence.ResolveStory, Is.True);

            var mapNodes = dynamicEvents.BuildSectorMapUiEntries()
                .Where(node => node.TemplateId == LuaMSectorDynamicEventSystem.MonolithArtifactTemplateId)
                .ToList();
            Assert.That(mapNodes.Select(node => node.Kind), Does.Contain("маршрут"));
            Assert.That(mapNodes.Select(node => node.State), Does.Contain("осколок Монолита"));
            Assert.That(mapNodes.Select(node => node.State), Does.Contain("исследовательский отчет"));
            Assert.That(mapNodes.Single(node => node.Kind == "маршрут").Location, Does.Contain("x 8.0"));
        });

        await server.WaitPost(() =>
        {
            filed = evidenceSystem.TryFileEvidence(researchReport, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(filed, Is.True);
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.Hazards.Single(hazard => hazard.Story == record!.Story).Resolved, Is.True);
            Assert.That(status.ReputationLedger, Contains.Key("Research"));
            Assert.That(status.ReputationLedger["Research"], Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildSectorMapUiEntries()
                    .Any(node => node.TemplateId == LuaMSectorDynamicEventSystem.MonolithArtifactTemplateId),
                Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorConditionsModifyDynamicEventRiskAndReward()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var bountyContracts = entManager.System<BountyContractSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var leadReportSystem = entManager.System<LuaMSectorLeadReportSystem>();
        var locMan = server.ResolveDependency<ILocalizationManager>();
        var printedFieldPacketBlockReason = locMan.GetString("luam-sector-terminal-route-block-stabilized-file-printed");
        var printedFieldPacketPingReason = locMan.GetString("luam-sector-terminal-route-ping-stabilized-file-printed");

        LuaMSectorStoryRecord? record = null;
        LuaMSectorStoryRecord? calibratedRecord = null;
        LuaMSectorStoryRecord? surveyCalibratedRecord = null;
        LuaMSectorConditionEntry? condition = null;
        var conditionError = string.Empty;
        var eventError = string.Empty;
        var calibratedEventError = string.Empty;
        var surveyCalibratedEventError = string.Empty;
        bool conditionSeeded = false;
        bool generated = false;
        bool calibratedGenerated = false;
        bool surveyCalibratedGenerated = false;
        bool terminalPingBlocked = false;
        bool siteSurveyed = false;
        bool duplicateSiteSurveyBlocked = false;
        bool relayPingSent = false;
        bool inheritedRelayPingSent = false;
        bool inheritedRelaySiteSurveyed = false;
        bool printedFieldPacketPingBlocked = false;
        bool fieldPacketFiled = false;
        bool calibratedFieldPacketFiled = false;
        bool surveyCalibratedFieldPacketFiled = false;
        EntityUid markerUid = default;
        EntityUid calibratedMarkerUid = default;
        EntityUid surveyCalibratedMarkerUid = default;
        EntityUid siteNoteUid = default;
        EntityUid surveyCalibratedSiteNoteUid = default;
        EntityUid markerFieldPacket = default;
        EntityUid calibratedFieldPacket = default;
        EntityUid surveyCalibratedFieldPacket = default;
        EntityUid user = default;
        EntityUid board = default;
        EntityUid sectorMapReport = default;
        EntityUid runtimeCoordinateReport = default;
        EntityUid routeCalibrationReport = default;
        EntityUid calibratedRouteMapReport = default;
        EntityUid calibratedRouteCoordinateReport = default;
        EntityUid chainedRouteMapReport = default;
        EntityUid chainedRouteCoordinateReport = default;
        EntityUid chainedRouteClosureReport = default;
        MapId mapId = default;
        var terminalPingResult = string.Empty;
        var siteSurveyResult = string.Empty;
        var duplicateSiteSurveyResult = string.Empty;
        var relayPingResult = string.Empty;
        var inheritedRelayPingResult = string.Empty;
        var inheritedRelaySiteSurveyResult = string.Empty;
        var printedFieldPacketPingResult = string.Empty;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out var marker, out _))
            {
                entManager.DeleteEntity(marker);
            }

            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out var site, out _))
            {
                entManager.DeleteEntity(site);
            }

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(8.0f, 4.0f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
            board = entManager.SpawnEntity("ComputerLuaMSectorRumorBoard", MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            conditionSeeded = storySystem.TrySeedSectorCondition(
                "comms-blackout",
                "Comms blackout",
                4,
                "Long-range radio is unreliable near the east salvage lane.",
                "integration-test",
                out condition,
                out conditionError);

            generated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out record,
                out eventError,
                templateId: "field-repair",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(8.0f, 4.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(conditionSeeded, Is.True, conditionError);
            Assert.That(condition, Is.Not.Null);
            Assert.That(generated, Is.True, eventError);
            Assert.That(record, Is.Not.Null);

            var expectedConditionBonus = condition!.Severity * LuaMSectorDynamicEventSystem.ConditionRewardStep;
            Assert.That(record!.ContractReward, Is.EqualTo(30400 + expectedConditionBonus));
            Assert.That(record.HazardRewardBonus, Is.EqualTo(3000));
            Assert.That(record.ContractDescription, Does.Contain("Активные условия сектора:"));
            Assert.That(record.ContractDescription, Does.Contain("SC-4 Comms blackout [comms-blackout]"));
            Assert.That(record.ContractDescription, Does.Contain("Модификатор награды за условия: +1000"));
            Assert.That(record.Hazard, Does.Contain("активные условия сектора: 1"));
            Assert.That(record.Hazard, Does.Contain("активные условия сектора: 1"));
            Assert.That(record.Hazard, Does.Contain("удаленные обновления ненадежны"));
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.ActiveConditions, Is.EqualTo(1));
            Assert.That(status.Hazards.Single(hazard => hazard.Story == record.Story).RewardBonus, Is.EqualTo(3000));

            var markers = new List<(EntityUid Uid, LuaMDynamicEventMarkerComponent Marker, NavMapBeaconComponent Nav)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            while (markerQuery.MoveNext(out var uid, out var marker, out var nav))
            {
                markers.Add((uid, marker, nav));
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            markerUid = markers.Single().Uid;
            Assert.That(markers.Single().Marker.ConditionRiskSummary, Does.Contain("SC-4 Comms blackout [comms-blackout]"));
            Assert.That(markers.Single().Marker.ConditionRiskSummary, Does.Contain("+1000"));
            Assert.That(markers.Single().Marker.ConditionRiskSummary, Does.Contain("удаленные обновления ненадежны"));
            Assert.That(markers.Single().Nav.Text, Does.Contain("[SC-4]"));
            Assert.That(markers.Single().Nav.Color, Is.EqualTo(Color.FromHex("#FF4F4FCC")));
            Assert.That(markers.Single().Nav.Enabled, Is.False);

            var siteNotes = new List<(EntityUid Uid, LuaMDynamicEventSiteObjectComponent Site, PaperComponent Paper)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, PaperComponent>();
            while (siteQuery.MoveNext(out var uid, out var site, out var paper))
            {
                siteNotes.Add((uid, site, paper));
            }

            Assert.That(siteNotes, Has.Count.EqualTo(1));
            siteNoteUid = siteNotes.Single().Uid;
            Assert.That(siteNotes.Single().Site.ConditionRiskSummary, Does.Contain("SC-4 Comms blackout [comms-blackout]"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("Риск условия: SC-4 Comms blackout [comms-blackout]"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("удаленные обновления ненадежны"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("модификатор награды за условия: +1000"));
            Assert.That(bountyContracts.GetContracts("Distress")
                    .Any(contract => contract.Name == "Запрос полевого ремонта" &&
                                     contract.Reward == record.ContractReward + record.HazardRewardBonus &&
                                     contract.Description.Contains("модификатор награды за условия: +1000") &&
                                     contract.Description.Contains("удаленные обновления ненадежны")),
                Is.True);

            });

        await server.WaitPost(() =>
        {
            terminalPingBlocked = !dynamicEvents.TryPingActiveRouteMarker(user, out terminalPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(terminalPingBlocked, Is.True, terminalPingResult);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.RoutePingCount, Is.EqualTo(0));
            Assert.That(nav.Enabled, Is.False);
        });

        await server.WaitPost(() =>
        {
            siteSurveyed = dynamicEvents.TrySurveySiteObject(siteNoteUid, user, out siteSurveyResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(siteSurveyed, Is.True, siteSurveyResult);
            Assert.That(siteSurveyResult, Does.Contain("Site survey recorded"));

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.SiteSurveyApplied, Is.True);
            Assert.That(marker.SiteSurveyCommsRelayAvailable, Is.True);
            Assert.That(marker.SiteSurveySummary, Does.Contain("site survey"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("site survey"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("local survey bypassed comms blackout"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("site survey field relay armed"));
            Assert.That(nav.Text, Does.Contain("[PING 1]"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));
            Assert.That(nav.Enabled, Is.True);

            Assert.That(entManager.TryGetComponent<LuaMDynamicEventSiteObjectComponent>(siteNoteUid, out var site), Is.True);
            Assert.That(site!.RouteSurveyApplied, Is.True);
            Assert.That(site.RouteSurveySummary, Does.Contain("site survey"));
            Assert.That(entManager.TryGetComponent<PaperComponent>(siteNoteUid, out var sitePaper), Is.True);
            Assert.That(sitePaper!.Content, Does.Contain("Site survey:"));
            Assert.That(sitePaper.Content, Does.Contain("local survey bypassed comms blackout"));
            Assert.That(sitePaper.Content, Does.Contain("site survey field relay armed"));

            var siteNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("site:", StringComparison.Ordinal) &&
                                node.StoryId == record!.Story);
            Assert.That(siteNode.Detail, Does.Contain("site survey"));
            Assert.That(siteNode.RoutePingCount, Is.EqualTo(1));

            var routeNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("route:", StringComparison.Ordinal));
            Assert.That(routeNode.Detail, Does.Contain("site survey"));
            Assert.That(routeNode.RoutePingCount, Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingCount, Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().CanPingRoute, Is.True);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingBlockReason, Is.Empty);
        });

        await server.WaitPost(() =>
        {
            duplicateSiteSurveyBlocked = !dynamicEvents.TrySurveySiteObject(siteNoteUid, user, out duplicateSiteSurveyResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(duplicateSiteSurveyBlocked, Is.True, duplicateSiteSurveyResult);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker), Is.True);
            Assert.That(marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.SiteSurveyCommsRelayAvailable, Is.True);
        });

        await server.WaitPost(() =>
        {
            relayPingSent = dynamicEvents.TryPingActiveRouteMarker(user, out relayPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(relayPingSent, Is.True, relayPingResult);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.RoutePingCount, Is.EqualTo(2));
            Assert.That(marker.SiteSurveyCommsRelayAvailable, Is.False);
            Assert.That(marker.LastRoutePingSummary, Does.Contain("site survey field relay used"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("стабилизирован"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#7CFF6BFF")));

            var routeNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("route:", StringComparison.Ordinal));
            Assert.That(routeNode.Detail, Does.Contain("site survey field relay used"));
            Assert.That(routeNode.RoutePingCount, Is.EqualTo(2));

            var automationState = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automationState.RoutePingCount, Is.EqualTo(2));
            Assert.That(automationState.ConditionHazards, Is.EqualTo(0));
            Assert.That(automationState.CanPingRoute, Is.False);
            Assert.That(automationState.RoutePingBlockReason, Does.Contain("стабилизирован"));
            Assert.That(automationState.RouteCalibrationHandoffReady, Is.True);
            Assert.That(automationState.RouteCalibrationHandoffSource, Does.Contain("stabilized route field packet filed"));
            Assert.That(automationState.RouteCalibrationHandoffSource, Does.Contain("site survey field relay used"));
            Assert.That(automationState.RouteCalibrationHandoffSource, Does.Contain("handoff route calibration chain depth: 1"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(markerUid, user, out markerFieldPacket), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(markerFieldPacket, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("Риск условия: SC-4 Comms blackout [comms-blackout]"));
            Assert.That(paper.Content, Does.Contain("удаленные обновления ненадежны"));
            Assert.That(paper.Content, Does.Contain("site survey field relay used"));
            Assert.That(paper.Content, Does.Contain("Evidence filing note: stabilized route field packet filed"));
            Assert.That(paper.Content, Does.Contain("handoff route calibration chain depth: 1"));
            Assert.That(entManager.HasComponent<LuaMSectorEvidenceComponent>(markerFieldPacket), Is.True);
            var automationState = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automationState.RoutePingBlockReason, Is.EqualTo(printedFieldPacketBlockReason));
            Assert.That(automationState.RouteCalibrationHandoffReady, Is.True);
            Assert.That(automationState.RouteCalibrationHandoffSource, Does.Contain("stabilized route field packet filed"));
            Assert.That(automationState.RouteCalibrationHandoffSource, Does.Contain("site survey field relay used"));
            Assert.That(automationState.RouteCalibrationHandoffSource, Does.Contain("handoff route calibration chain depth: 1"));
            var routeNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("route:", StringComparison.Ordinal) &&
                                node.StoryId == record!.Story);
            Assert.That(routeNode.State, Is.EqualTo("полевой акт распечатан"));
            Assert.That(routeNode.Detail, Does.Contain("полевой акт распечатан"));
            Assert.That(routeNode.Detail, Does.Contain("подайте акт как доказательство"));
            Assert.That(routeNode.Detail, Does.Contain("route calibration handoff"));
            Assert.That(routeNode.Detail, Does.Contain("stabilized route field packet filed"));
        });

        await server.WaitPost(() =>
        {
            printedFieldPacketPingBlocked = !dynamicEvents.TryPingActiveRouteMarker(user, out printedFieldPacketPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(printedFieldPacketPingBlocked, Is.True, printedFieldPacketPingResult);
            Assert.That(printedFieldPacketPingResult, Is.EqualTo(printedFieldPacketPingReason));

            var markers = new List<LuaMDynamicEventMarkerComponent>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out _, out var marker))
            {
                if (marker.Story == record!.Story)
                    markers.Add(marker);
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            Assert.That(markers.Single().RoutePingCount, Is.EqualTo(2));
            Assert.That(markers.Single().StabilizedFieldPacketPrinted, Is.True);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingBlockReason, Is.EqualTo(printedFieldPacketBlockReason));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintLeadReport(board, user, out sectorMapReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(sectorMapReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Sector map"));
            Assert.That(paper.Content, Does.Contain("полевой акт распечатан"));
            Assert.That(paper.Content, Does.Contain("подайте акт как доказательство"));
            Assert.That(paper.Content, Does.Contain("template: field-repair"));
            Assert.That(paper.Content, Does.Contain($"story: {record!.Story}"));
            Assert.That(paper.Content, Does.Contain("pings 2"));
            Assert.That(paper.Content, Does.Contain("site survey field relay used"));
            Assert.That(paper.Content, Does.Contain("Route calibration handoff ready"));
            Assert.That(paper.Content, Does.Contain("route calibration handoff"));
            Assert.That(paper.Content, Does.Contain("stabilized route field packet filed"));
            Assert.That(paper.Content, Does.Contain("handoff route calibration chain depth: 1"));
            Assert.That(paper.Content, Does.Contain("SC-4 Comms blackout [comms-blackout]"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeCoordinatePacket(board, user, out runtimeCoordinateReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(runtimeCoordinateReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Active route status"));
            Assert.That(paper.Content, Does.Contain("Route pings: 2/2"));
            Assert.That(paper.Content, Does.Contain("Last route ping:"));
            Assert.That(paper.Content, Does.Contain("site survey field relay used"));
            Assert.That(paper.Content, Does.Contain("Condition risk: SC-4 Comms blackout [comms-blackout]"));
            Assert.That(paper.Content, Does.Contain("Field report: printed; click the LuaM marker to close the task, or file the report as evidence."));
        });

        await server.WaitPost(() =>
        {
            fieldPacketFiled = evidenceSystem.TryFileEvidence(markerFieldPacket, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(fieldPacketFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == record!.Story).Resolved, Is.True);
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("site survey field relay used"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("handoff route calibration chain depth: 1"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("x 8.0"));
            Assert.That(automation.RouteCalibrationSourceChainDepth, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationRewardBonus, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationRewardStep));
            Assert.That(automation.RouteCalibrationClosureRewardBonus, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationClosureRewardStep));
            Assert.That(automation.RouteCalibrationSources, Has.Length.EqualTo(1));
            Assert.That(automation.RouteCalibrationSources.Single(), Is.EqualTo(automation.RouteCalibrationSource));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintLeadReport(board, user, out routeCalibrationReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(routeCalibrationReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Process automation"));
            Assert.That(paper.Content, Does.Contain("Route calibration reserve: 1"));
            Assert.That(paper.Content, Does.Contain("Queued route calibration sources:"));
            Assert.That(paper.Content, Does.Contain("Route calibration chain depth: 1"));
            Assert.That(paper.Content, Does.Contain("Route calibration reward bonus: +500"));
            Assert.That(paper.Content, Does.Contain("Route calibration closure bonus: +250"));
            Assert.That(paper.Content, Does.Contain("field-repair"));
            Assert.That(paper.Content, Does.Contain("site survey field relay used"));
            Assert.That(paper.Content, Does.Contain("handoff route calibration chain depth: 1"));
            Assert.That(paper.Content, Does.Contain("x 8.0"));
        });

        await server.WaitPost(() =>
        {
            calibratedGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test-relay-calibrated",
                out calibratedRecord,
                out calibratedEventError,
                templateId: "black-box-echo",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(10.0f, 6.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(calibratedGenerated, Is.True, calibratedEventError);
            Assert.That(calibratedRecord, Is.Not.Null);

            var expectedConditionBonus = condition!.Severity * LuaMSectorDynamicEventSystem.ConditionRewardStep;
            var expectedRouteCalibrationBonus = LuaMSectorDynamicEventSystem.RouteCalibrationRewardStep;
            var expectedRouteCalibrationClosureBonus = LuaMSectorDynamicEventSystem.RouteCalibrationClosureRewardStep;
            Assert.That(calibratedRecord!.ContractReward, Is.EqualTo(32200 + expectedConditionBonus + expectedRouteCalibrationBonus));
            Assert.That(calibratedRecord.HazardRewardBonus, Is.EqualTo(3000 + expectedRouteCalibrationClosureBonus));
            Assert.That(calibratedRecord.ContractDescription, Does.Contain("Route calibration handoff bonus: +500"));
            Assert.That(calibratedRecord.ContractDescription, Does.Contain("Route calibration closure bonus: +250"));
            Assert.That(calibratedRecord.ContractDescription, Does.Contain("chain depth 1"));
            Assert.That(calibratedRecord.Hazard, Does.Contain("Route calibration handoff bonus: +500"));
            Assert.That(calibratedRecord.Hazard, Does.Contain("Route calibration closure bonus: +250"));
            Assert.That(calibratedRecord.Hazard, Does.Contain("chain depth 1"));
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == calibratedRecord.Story).RewardBonus,
                Is.EqualTo(calibratedRecord.HazardRewardBonus));
            var expectedReputationBonus = LuaMSectorStorySystem.ReputationRewardStep;
            Assert.That(bountyContracts.GetContracts("Distress")
                    .Any(contract => contract.Reward == calibratedRecord.ContractReward + calibratedRecord.HazardRewardBonus + expectedReputationBonus &&
                                     contract.Description.Contains("Route calibration closure bonus: +250")),
                Is.True);

            var markers = new List<(EntityUid Uid, LuaMDynamicEventMarkerComponent Marker, NavMapBeaconComponent Nav)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            while (markerQuery.MoveNext(out var uid, out var marker, out var nav))
            {
                if (marker.Story == calibratedRecord!.Story)
                    markers.Add((uid, marker, nav));
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            calibratedMarkerUid = markers.Single().Uid;
            Assert.That(markers.Single().Marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(markers.Single().Marker.LastRoutePingActor, Is.EqualTo("route calibration"));
            Assert.That(markers.Single().Marker.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(markers.Single().Marker.RouteCalibrationSource, Does.Contain("site survey field relay used"));
            Assert.That(markers.Single().Marker.LastRoutePingSummary, Does.Contain("source packet"));
            Assert.That(markers.Single().Marker.LastRoutePingSummary, Does.Contain("site survey field relay used"));
            Assert.That(markers.Single().Marker.LastRoutePingSummary, Does.Contain("inherited field relay armed"));
            Assert.That(markers.Single().Marker.SiteSurveyCommsRelayAvailable, Is.True);
            Assert.That(markers.Single().Marker.RouteCalibrationRelayInherited, Is.True);
            Assert.That(markers.Single().Nav.Enabled, Is.True);
            Assert.That(markers.Single().Nav.Text, Does.Contain("[PING 1]"));
            Assert.That(markers.Single().Nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));
            var routeNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("route:", StringComparison.Ordinal) &&
                                node.StoryId == calibratedRecord!.Story);
            Assert.That(routeNode.State, Is.EqualTo("active calibrated route"));
            Assert.That(routeNode.Detail, Does.Contain("inherited field relay armed"));
            Assert.That(routeNode.Detail, Does.Contain("route calibration source"));
            Assert.That(routeNode.Detail, Does.Contain("field-repair"));
            Assert.That(routeNode.RoutePingCount, Is.EqualTo(1));
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(0));
            Assert.That(automation.RouteCalibrationSource, Is.Empty);
            Assert.That(automation.RouteCalibrationSources, Is.Empty);
            Assert.That(automation.ActiveRouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(automation.ActiveRouteCalibrationSource, Does.Contain("site survey field relay used"));
            Assert.That(automation.ActiveRouteCalibrationChainDepth, Is.EqualTo(1));
            Assert.That(automation.ActiveRouteCalibrationRelayInherited, Is.True);
            Assert.That(automation.CanPingRoute, Is.True);
            Assert.That(automation.RoutePingBlockReason, Is.Empty);
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintLeadReport(board, user, out calibratedRouteMapReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(calibratedRouteMapReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Process automation"));
            Assert.That(paper.Content, Does.Contain("Active route calibration source"));
            Assert.That(paper.Content, Does.Contain("Active route calibration chain depth: 1"));
            Assert.That(paper!.Content, Does.Contain("## Sector map"));
            Assert.That(paper.Content, Does.Contain("active calibrated route"));
            Assert.That(paper.Content, Does.Contain("route calibration source"));
            Assert.That(paper.Content, Does.Contain("field-repair"));
            Assert.That(paper.Content, Does.Contain("inherited field relay armed"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeCoordinatePacket(board, user, out calibratedRouteCoordinateReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(calibratedRouteCoordinateReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Active route status"));
            Assert.That(paper.Content, Does.Contain("Route pings: 1/2"));
            Assert.That(paper.Content, Does.Contain("Route calibration source:"));
            Assert.That(paper.Content, Does.Contain("Route calibration chain depth: 1"));
            Assert.That(paper.Content, Does.Contain("field-repair"));
            Assert.That(paper.Content, Does.Contain("site survey field relay used"));
            Assert.That(paper.Content, Does.Contain("Calibration relay: inherited relay armed"));
            Assert.That(paper.Content, Does.Contain("Field report evidence requires more route pings."));
        });

        await server.WaitPost(() =>
        {
            inheritedRelayPingSent = dynamicEvents.TryPingActiveRouteMarker(user, out inheritedRelayPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(inheritedRelayPingSent, Is.True, inheritedRelayPingResult);
            Assert.That(inheritedRelayPingResult, Does.Contain("Источник калибровки перенесен дальше"));
            Assert.That(inheritedRelayPingResult, Does.Contain("field-repair"));

            var markers = new List<LuaMDynamicEventMarkerComponent>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out _, out var marker))
            {
                if (marker.Story == calibratedRecord!.Story)
                    markers.Add(marker);
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            Assert.That(markers.Single().RoutePingCount, Is.EqualTo(2));
            Assert.That(markers.Single().SiteSurveyCommsRelayAvailable, Is.False);
            Assert.That(markers.Single().RouteCalibrationRelayInherited, Is.True);
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("site survey field relay used"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("inherited field relay used"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("route calibration source carried forward"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("field-repair"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("стабилизирован"));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingCount, Is.EqualTo(2));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().CanPingRoute, Is.False);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingBlockReason, Does.Contain("стабилизирован"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(calibratedMarkerUid, user, out calibratedFieldPacket), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(calibratedFieldPacket, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("site survey field relay used"));
            Assert.That(paper.Content, Does.Contain("inherited field relay used"));
            Assert.That(paper.Content, Does.Contain("Evidence filing note: stabilized route field packet filed"));
            Assert.That(paper.Content, Does.Contain("Route calibration chain depth: 1"));
            Assert.That(paper.Content, Does.Contain("handoff route calibration chain depth: 2"));
            Assert.That(paper.Content, Does.Contain("calibrated from"));
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(calibratedFieldPacket, out var evidence), Is.True);
            Assert.That(evidence!.Note, Does.Contain("inherited field relay used"));
            Assert.That(evidence.Note, Does.Contain("handoff route calibration chain depth: 2"));
            Assert.That(evidence.Note, Does.Contain("calibrated from"));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingBlockReason, Is.EqualTo(printedFieldPacketBlockReason));
            var routeNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("route:", StringComparison.Ordinal) &&
                                node.StoryId == calibratedRecord!.Story);
            Assert.That(routeNode.State, Is.EqualTo("полевой акт распечатан"));
            Assert.That(routeNode.Detail, Does.Contain("полевой акт распечатан"));
            Assert.That(routeNode.Detail, Does.Contain("подайте акт как доказательство"));
        });

        await server.WaitPost(() =>
        {
            calibratedFieldPacketFiled = evidenceSystem.TryFileEvidence(calibratedFieldPacket, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(calibratedFieldPacketFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == calibratedRecord!.Story).Resolved, Is.True);
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("inherited field relay used"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("handoff route calibration chain depth: 2"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("calibrated from"));
            Assert.That(automation.RouteCalibrationSourceChainDepth, Is.EqualTo(2));
            Assert.That(automation.RouteCalibrationRewardBonus, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationRewardStep * 2));
            Assert.That(automation.RouteCalibrationClosureRewardBonus, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationClosureRewardStep * 2));
            Assert.That(automation.RouteCalibrationSources, Has.Length.EqualTo(1));
            Assert.That(automation.RouteCalibrationSources.Single(), Is.EqualTo(automation.RouteCalibrationSource));
            Assert.That(automation.ActiveRouteCalibrationSource, Is.Empty);
            Assert.That(automation.ActiveRouteCalibrationChainDepth, Is.EqualTo(0));
            Assert.That(automation.ActiveRouteCalibrationRelayInherited, Is.False);
        });

        await server.WaitPost(() =>
        {
            surveyCalibratedGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test-relay-survey-calibrated",
                out surveyCalibratedRecord,
                out surveyCalibratedEventError,
                templateId: "field-repair",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(12.0f, 8.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(surveyCalibratedGenerated, Is.True, surveyCalibratedEventError);
            Assert.That(surveyCalibratedRecord, Is.Not.Null);

            var expectedConditionBonus = condition!.Severity * LuaMSectorDynamicEventSystem.ConditionRewardStep;
            var expectedRouteCalibrationBonus = LuaMSectorDynamicEventSystem.RouteCalibrationRewardStep * 2;
            var expectedRouteCalibrationClosureBonus = LuaMSectorDynamicEventSystem.RouteCalibrationClosureRewardStep * 2;
            Assert.That(surveyCalibratedRecord!.ContractReward, Is.EqualTo(30400 + expectedConditionBonus + expectedRouteCalibrationBonus));
            Assert.That(surveyCalibratedRecord.HazardRewardBonus, Is.EqualTo(3000 + expectedRouteCalibrationClosureBonus));
            Assert.That(surveyCalibratedRecord.ContractDescription, Does.Contain("Route calibration handoff bonus: +1000"));
            Assert.That(surveyCalibratedRecord.ContractDescription, Does.Contain("Route calibration closure bonus: +500"));
            Assert.That(surveyCalibratedRecord.ContractDescription, Does.Contain("chain depth 2"));
            Assert.That(surveyCalibratedRecord.Hazard, Does.Contain("Route calibration handoff bonus: +1000"));
            Assert.That(surveyCalibratedRecord.Hazard, Does.Contain("Route calibration closure bonus: +500"));
            Assert.That(surveyCalibratedRecord.Hazard, Does.Contain("chain depth 2"));
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == surveyCalibratedRecord.Story).RewardBonus,
                Is.EqualTo(surveyCalibratedRecord.HazardRewardBonus));
            var expectedReputationBonus = LuaMSectorStorySystem.ReputationRewardStep * 2;
            Assert.That(bountyContracts.GetContracts("Distress")
                    .Any(contract => contract.Reward == surveyCalibratedRecord.ContractReward + surveyCalibratedRecord.HazardRewardBonus + expectedReputationBonus &&
                                     contract.Description.Contains("Route calibration closure bonus: +500")),
                Is.True);

            var markers = new List<(EntityUid Uid, LuaMDynamicEventMarkerComponent Marker, NavMapBeaconComponent Nav)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            while (markerQuery.MoveNext(out var uid, out var marker, out var nav))
            {
                if (marker.Story == surveyCalibratedRecord!.Story)
                    markers.Add((uid, marker, nav));
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            surveyCalibratedMarkerUid = markers.Single().Uid;
            Assert.That(markers.Single().Marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(markers.Single().Marker.SiteSurveyCommsRelayAvailable, Is.True);
            Assert.That(markers.Single().Marker.RouteCalibrationRelayInherited, Is.True);
            Assert.That(markers.Single().Marker.LastRoutePingSummary, Does.Contain("inherited field relay armed"));
            Assert.That(markers.Single().Nav.Enabled, Is.True);
            Assert.That(markers.Single().Nav.Text, Does.Contain("[PING 1]"));
            Assert.That(markers.Single().Nav.Color, Is.EqualTo(Color.FromHex("#B985FFFF")));

            var routeNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("route:", StringComparison.Ordinal) &&
                                node.StoryId == surveyCalibratedRecord!.Story);
            Assert.That(routeNode.State, Is.EqualTo("active chained route"));
            Assert.That(routeNode.Detail, Does.Contain("route calibration chain depth: 2"));
            Assert.That(routeNode.Detail, Does.Contain("calibrated from"));
            Assert.That(routeNode.Detail, Does.Contain("inherited field relay armed"));

            var siteNotes = new List<(EntityUid Uid, LuaMDynamicEventSiteObjectComponent Site, PaperComponent Paper)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, PaperComponent>();
            while (siteQuery.MoveNext(out var uid, out var site, out var paper))
            {
                if (site.Story == surveyCalibratedRecord!.Story)
                    siteNotes.Add((uid, site, paper));
            }

            Assert.That(siteNotes, Has.Count.EqualTo(1));
            surveyCalibratedSiteNoteUid = siteNotes.Single().Uid;
            Assert.That(siteNotes.Single().Site.RouteCalibrationSource, Does.Contain("inherited field relay used"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("Route calibration source"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("Route calibration chain depth: 2"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("inherited field relay used"));

            var siteNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("site:", StringComparison.Ordinal) &&
                                node.StoryId == surveyCalibratedRecord!.Story);
            Assert.That(siteNode.Risk, Does.Contain("source packet"));
            Assert.That(siteNode.Risk, Does.Contain("route calibration chain depth: 2"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintLeadReport(board, user, out chainedRouteMapReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(chainedRouteMapReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Sector map"));
            Assert.That(paper.Content, Does.Contain("active chained route"));
            Assert.That(paper.Content, Does.Contain("route calibration chain depth: 2"));
            Assert.That(paper.Content, Does.Contain("calibrated from"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(leadReportSystem.TryPrintRuntimeCoordinatePacket(board, user, out chainedRouteCoordinateReport), Is.True);
            Assert.That(leadReportSystem.TryPrintRuntimeClosureReport(board, user, out chainedRouteClosureReport), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(chainedRouteCoordinateReport, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("## Active route status"));
            Assert.That(paper.Content, Does.Contain("Route pings: 1/2"));
            Assert.That(paper.Content, Does.Contain("Route calibration source:"));
            Assert.That(paper.Content, Does.Contain("Route calibration chain depth: 2"));
            Assert.That(paper.Content, Does.Contain("calibrated from"));
            Assert.That(paper.Content, Does.Contain("Calibration relay: inherited relay armed"));

            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(chainedRouteClosureReport, out var evidence), Is.True);
            Assert.That(evidence!.Note, Does.Contain("active route calibration source:"));
            Assert.That(evidence.Note, Does.Contain("active route calibration chain depth: 2"));
            Assert.That(evidence.Note, Does.Contain("inherited calibration relay"));
        });

        await server.WaitPost(() =>
        {
            inheritedRelaySiteSurveyed = dynamicEvents.TrySurveySiteObject(
                surveyCalibratedSiteNoteUid,
                user,
                out inheritedRelaySiteSurveyResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(inheritedRelaySiteSurveyed, Is.True, inheritedRelaySiteSurveyResult);
            Assert.That(inheritedRelaySiteSurveyResult, Does.Contain("Calibration chain depth: 2"));

            var markers = new List<LuaMDynamicEventMarkerComponent>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out _, out var marker))
            {
                if (marker.Story == surveyCalibratedRecord!.Story)
                    markers.Add(marker);
            }

            Assert.That(markers, Has.Count.EqualTo(1));
            Assert.That(markers.Single().RoutePingCount, Is.EqualTo(2));
            Assert.That(markers.Single().SiteSurveyApplied, Is.True);
            Assert.That(markers.Single().SiteSurveyCommsRelayAvailable, Is.False);
            Assert.That(markers.Single().RouteCalibrationRelayInherited, Is.True);
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("site survey"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("inherited field relay used by site survey"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("route calibration source carried forward by site survey"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("route calibration chain depth: 2"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Contain("calibrated from"));
            Assert.That(markers.Single().LastRoutePingSummary, Does.Not.Contain("site survey field relay armed"));

            Assert.That(entManager.TryGetComponent<LuaMDynamicEventSiteObjectComponent>(surveyCalibratedSiteNoteUid, out var site), Is.True);
            Assert.That(site!.RouteSurveyApplied, Is.True);
            Assert.That(site.RouteSurveySummary, Does.Contain("inherited field relay used by site survey"));
            Assert.That(site.RouteSurveySummary, Does.Contain("route calibration source carried forward by site survey"));
            Assert.That(site.RouteSurveySummary, Does.Contain("route calibration chain depth: 2"));
            Assert.That(entManager.TryGetComponent<PaperComponent>(surveyCalibratedSiteNoteUid, out var sitePaper), Is.True);
            Assert.That(sitePaper!.Content, Does.Contain("inherited field relay used by site survey"));
            Assert.That(sitePaper.Content, Does.Contain("route calibration source carried forward by site survey"));
            Assert.That(sitePaper.Content, Does.Contain("route calibration chain depth: 2"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(surveyCalibratedMarkerUid, user, out surveyCalibratedFieldPacket), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(surveyCalibratedFieldPacket, out var paper), Is.True);
            Assert.That(paper!.Content, Does.Contain("inherited field relay used by site survey"));
            Assert.That(paper.Content, Does.Contain("route calibration source carried forward by site survey"));
            Assert.That(paper.Content, Does.Contain("Route calibration chain depth: 2"));
            Assert.That(paper.Content, Does.Contain("route calibration chain depth: 2"));
            Assert.That(paper.Content, Does.Contain("handoff route calibration chain depth: 3"));
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(surveyCalibratedFieldPacket, out var evidence), Is.True);
            Assert.That(evidence!.Note, Does.Contain("site survey"));
            Assert.That(evidence.Note, Does.Contain("inherited field relay used"));
            Assert.That(evidence.Note, Does.Contain("handoff route calibration chain depth: 3"));
            Assert.That(evidence.Note, Does.Contain("calibrated from"));
        });

        await server.WaitPost(() =>
        {
            surveyCalibratedFieldPacketFiled = evidenceSystem.TryFileEvidence(surveyCalibratedFieldPacket, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(surveyCalibratedFieldPacketFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == surveyCalibratedRecord!.Story).Resolved, Is.True);
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("site survey"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("inherited field relay used"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("handoff route calibration chain depth: 3"));
            Assert.That(automation.RouteCalibrationSourceChainDepth, Is.EqualTo(3));
            Assert.That(automation.RouteCalibrationSources, Has.Length.EqualTo(1));
            Assert.That(automation.RouteCalibrationSources.Single(), Is.EqualTo(automation.RouteCalibrationSource));
        });

        await pair.RunTicksSync(5);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RadiationSectorConditionSpawnsDynamicEventRadiationHazard()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();

        LuaMSectorStoryRecord? record = null;
        LuaMSectorStoryRecord? calibratedRecord = null;
        LuaMSectorStoryRecord? chainedRecord = null;
        var conditionError = string.Empty;
        var eventError = string.Empty;
        var calibratedEventError = string.Empty;
        var chainedEventError = string.Empty;
        bool conditionSeeded = false;
        bool generated = false;
        bool calibratedGenerated = false;
        bool chainedGenerated = false;
        bool stabilizedEvidenceFiled = false;
        bool chainedEvidenceFiled = false;
        bool redundantPingBlocked = false;
        bool duplicateStabilizedPrintBlocked = false;
        EntityUid user = default;
        EntityUid stabilizedFieldPacket = default;
        EntityUid chainedFieldPacket = default;
        EntityUid duplicateStabilizedFieldPacket = default;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out var marker, out _))
            {
                entManager.DeleteEntity(marker);
            }

            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out var site, out _))
            {
                entManager.DeleteEntity(site);
            }

            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            while (hazardQuery.MoveNext(out var hazard, out _))
            {
                entManager.DeleteEntity(hazard);
            }

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(14.0f, -3.0f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            conditionSeeded = storySystem.TrySeedSectorCondition(
                "radiation-lane",
                "Radiation lane",
                4,
                "A salvage lane is reporting elevated radiation.",
                "integration-test",
                out _,
                out conditionError);

            generated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out record,
                out eventError,
                templateId: "black-box-echo",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(14.0f, -3.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(conditionSeeded, Is.True, conditionError);
            Assert.That(generated, Is.True, eventError);
            Assert.That(record, Is.Not.Null);
            Assert.That(record!.Hazard, Does.Contain("радиационная защита"));

            var hazards = new List<(EntityUid Uid, LuaMDynamicEventConditionHazardComponent Hazard, RadiationSourceComponent Radiation, NavMapBeaconComponent Nav, TransformComponent Xform)>();
            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent, RadiationSourceComponent, NavMapBeaconComponent, TransformComponent>();
            while (hazardQuery.MoveNext(out var uid, out var hazard, out var radiation, out var nav, out var xform))
            {
                hazards.Add((uid, hazard, radiation, nav, xform));
            }

            Assert.That(hazards, Has.Count.EqualTo(1));
            var generatedHazard = hazards.Single();
            Assert.That(generatedHazard.Hazard.Story, Is.EqualTo(record.Story));
            Assert.That(generatedHazard.Hazard.TemplateId, Is.EqualTo("black-box-echo"));
            Assert.That(generatedHazard.Hazard.ConditionId, Is.EqualTo("radiation-lane"));
            Assert.That(generatedHazard.Hazard.CreatedBy, Is.EqualTo("integration-test"));
            Assert.That(generatedHazard.Hazard.MarkerLocation, Does.Contain("x 14.0"));
            Assert.That(generatedHazard.Hazard.MarkerLocation, Does.Contain("y -3.0"));
            Assert.That(generatedHazard.Hazard.RouteCalibrationSource, Is.Empty);
            Assert.That(generatedHazard.Hazard.RouteCalibrationChainDepth, Is.EqualTo(0));
            Assert.That(generatedHazard.Hazard.RouteCalibrationRadiationDamping, Is.EqualTo(0));
            Assert.That(generatedHazard.Radiation.Intensity, Is.EqualTo(4));
            Assert.That(generatedHazard.Radiation.Enabled, Is.True);
            Assert.That(generatedHazard.Nav.Text, Does.Contain("radiation hazard [SC-4]"));
            Assert.That(generatedHazard.Nav.Color, Is.EqualTo(Color.FromHex("#FF4F4FCC")));
            Assert.That(generatedHazard.Nav.Enabled, Is.True);
            Assert.That(generatedHazard.Xform.MapID, Is.EqualTo(mapId));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out var firstPingResult), Is.True, firstPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var hazards = new List<LuaMDynamicEventConditionHazardComponent>();
            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            while (hazardQuery.MoveNext(out _, out var hazard))
            {
                hazards.Add(hazard);
            }

            Assert.That(hazards, Has.Count.EqualTo(1));

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.Story, Is.EqualTo(record!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("Пинг 1"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().ConditionHazards, Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildSectorMapUiEntries().Count(node => node.NodeId.StartsWith("hazard:", StringComparison.Ordinal)), Is.EqualTo(1));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out var secondPingResult), Is.True, secondPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            Assert.That(hazardQuery.MoveNext(out _, out _), Is.False);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.Story, Is.EqualTo(record!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(2));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("Пинг 2"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("стабилизирован"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#7CFF6BFF")));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().ConditionHazards, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().CanPingRoute, Is.False);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingBlockReason, Does.Contain("стабилизирован"));
            Assert.That(dynamicEvents.BuildSectorMapUiEntries().Any(node => node.NodeId.StartsWith("hazard:", StringComparison.Ordinal)), Is.False);
        });

        await server.WaitPost(() =>
        {
            redundantPingBlocked = !dynamicEvents.TryPingActiveRouteMarker(user, out var redundantPingResult);
            Assert.That(redundantPingResult, Does.Contain("стабилизирован"));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(redundantPingBlocked, Is.True);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker), Is.True);
            Assert.That(marker.Story, Is.EqualTo(record!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(2));
        });

        await server.WaitPost(() =>
        {
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out var markerUid, out _), Is.True);
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(markerUid, user, out stabilizedFieldPacket), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(stabilizedFieldPacket, out var paper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(stabilizedFieldPacket, out var evidence), Is.True);
            Assert.That(evidence!.Story, Is.EqualTo(record!.Story));
            Assert.That(evidence.AcknowledgeHazard, Is.True);
            Assert.That(evidence.ResolveStory, Is.True);
            Assert.That(paper!.Content, Does.Contain("Навигационная стабилизация"));
            Assert.That(paper.Content, Does.Contain("Коридор маршрута стабилизирован"));

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker), Is.True);
            Assert.That(marker.StabilizedFieldPacketPrinted, Is.True);
        });

        await server.WaitPost(() =>
        {
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out var markerUid, out _), Is.True);
            duplicateStabilizedPrintBlocked = !dynamicEvents.TryPrintMarkerFieldPacket(markerUid, user, out duplicateStabilizedFieldPacket);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(duplicateStabilizedPrintBlocked, Is.True);
            Assert.That(duplicateStabilizedFieldPacket, Is.EqualTo(default(EntityUid)));

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker), Is.True);
            Assert.That(marker.RoutePingCount, Is.EqualTo(2));
            Assert.That(marker.StabilizedFieldPacketPrinted, Is.True);
        });

        await server.WaitPost(() =>
        {
            stabilizedEvidenceFiled = evidenceSystem.TryFileEvidence(stabilizedFieldPacket, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(stabilizedEvidenceFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == record!.Story).Resolved, Is.True);
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("x 14.0"));
            Assert.That(automation.RouteCalibrationRadiationDampingPreview, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationRadiationDampingStep));

            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            Assert.That(hazardQuery.MoveNext(out _, out _), Is.False);
        });

        await server.WaitPost(() =>
        {
            calibratedGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test-calibrated",
                out calibratedRecord,
                out calibratedEventError,
                templateId: "field-repair",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(17.0f, -1.5f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(calibratedGenerated, Is.True, calibratedEventError);
            Assert.That(calibratedRecord, Is.Not.Null);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.Story, Is.EqualTo(calibratedRecord!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.LastRoutePingActor, Is.EqualTo("route calibration"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("Калибровка маршрута"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("source packet"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("black-box-echo"));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("x 14.0"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("x 17.0"));
            Assert.That(nav.Text, Does.Contain("[PING 1]"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingCount, Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationCredits, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationSource, Is.Empty);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationRadiationDampingPreview, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().ConditionHazards, Is.EqualTo(1));

            var calibratedHazards = new List<(LuaMDynamicEventConditionHazardComponent Hazard, RadiationSourceComponent Radiation, NavMapBeaconComponent Nav)>();
            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent, RadiationSourceComponent, NavMapBeaconComponent>();
            while (hazardQuery.MoveNext(out _, out var hazard, out var radiation, out var hazardNav))
            {
                if (hazard.Story == calibratedRecord.Story)
                    calibratedHazards.Add((hazard, radiation, hazardNav));
            }

            Assert.That(calibratedHazards, Has.Count.EqualTo(1));
            var calibratedHazard = calibratedHazards.Single();
            Assert.That(calibratedHazard.Hazard.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(calibratedHazard.Hazard.RouteCalibrationSource, Does.Contain("x 14.0"));
            Assert.That(calibratedHazard.Hazard.RouteCalibrationChainDepth, Is.EqualTo(1));
            Assert.That(calibratedHazard.Hazard.RouteCalibrationRadiationDamping, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationRadiationDampingStep));
            Assert.That(calibratedHazard.Radiation.Intensity, Is.EqualTo(3));
            Assert.That(calibratedHazard.Nav.Text, Does.Contain("radiation hazard [SC-4->RAD-3]"));
            Assert.That(calibratedHazard.Nav.Text, Does.Contain("[DAMPED -1]"));
            Assert.That(calibratedHazard.Nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));

            var hazardNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("hazard:", StringComparison.Ordinal) &&
                                node.StoryId == calibratedRecord.Story);
            Assert.That(hazardNode.Detail, Does.Contain("route calibration radiation damping: -1"));
            Assert.That(hazardNode.Detail, Does.Contain("chain depth 1"));

            var siteNotes = new List<(LuaMDynamicEventSiteObjectComponent Site, PaperComponent Paper)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, PaperComponent>();
            while (siteQuery.MoveNext(out _, out var site, out var paper))
            {
                if (site.Story == calibratedRecord.Story)
                    siteNotes.Add((site, paper));
            }

            Assert.That(siteNotes, Has.Count.EqualTo(1));
            Assert.That(siteNotes.Single().Site.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(siteNotes.Single().Site.RouteCalibrationSource, Does.Contain("x 14.0"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("Route calibration source"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("black-box-echo"));

            var siteNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("site:", StringComparison.Ordinal) &&
                                node.StoryId == calibratedRecord.Story);
            Assert.That(siteNode.Risk, Does.Contain("source packet"));
            Assert.That(siteNode.Risk, Does.Contain("black-box-echo"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out var calibratedPingResult), Is.True, calibratedPingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            Assert.That(hazardQuery.MoveNext(out _, out _), Is.False);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.Story, Is.EqualTo(calibratedRecord!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(2));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("стабилизирован"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#7CFF6BFF")));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().ConditionHazards, Is.EqualTo(0));
        });

        await server.WaitPost(() =>
        {
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out var markerUid, out _), Is.True);
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(markerUid, user, out chainedFieldPacket), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.TryGetComponent<PaperComponent>(chainedFieldPacket, out var paper), Is.True);
            Assert.That(entManager.TryGetComponent<LuaMSectorEvidenceComponent>(chainedFieldPacket, out var evidence), Is.True);
            Assert.That(evidence!.Story, Is.EqualTo(calibratedRecord!.Story));
            Assert.That(evidence.Note, Does.Contain("field-repair"));
            Assert.That(evidence.Note, Does.Contain("calibrated from"));
            Assert.That(evidence.Note, Does.Contain("black-box-echo"));
            Assert.That(paper!.Content, Does.Contain("Route calibration source"));
            Assert.That(paper.Content, Does.Contain("black-box-echo"));
        });

        await server.WaitPost(() =>
        {
            chainedEvidenceFiled = evidenceSystem.TryFileEvidence(chainedFieldPacket, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(chainedEvidenceFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == calibratedRecord!.Story).Resolved, Is.True);
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("calibrated from"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(automation.RouteCalibrationRadiationDampingPreview, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationRadiationDampingStep * 2));
        });

        await server.WaitPost(() =>
        {
            chainedGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test-chained",
                out chainedRecord,
                out chainedEventError,
                templateId: "quiet-distress",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(19.0f, 2.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(chainedGenerated, Is.True, chainedEventError);
            Assert.That(chainedRecord, Is.Not.Null);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker), Is.True);
            Assert.That(marker.Story, Is.EqualTo(chainedRecord!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("calibrated from"));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("source packet"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("field-repair"));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationCredits, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationSource, Is.Empty);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationRadiationDampingPreview, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().ConditionHazards, Is.EqualTo(1));

            var chainedHazards = new List<(LuaMDynamicEventConditionHazardComponent Hazard, RadiationSourceComponent Radiation, NavMapBeaconComponent Nav)>();
            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent, RadiationSourceComponent, NavMapBeaconComponent>();
            while (hazardQuery.MoveNext(out _, out var hazard, out var radiation, out var hazardNav))
            {
                if (hazard.Story == chainedRecord.Story)
                    chainedHazards.Add((hazard, radiation, hazardNav));
            }

            Assert.That(chainedHazards, Has.Count.EqualTo(1));
            var chainedHazard = chainedHazards.Single();
            Assert.That(chainedHazard.Hazard.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(chainedHazard.Hazard.RouteCalibrationSource, Does.Contain("calibrated from"));
            Assert.That(chainedHazard.Hazard.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(chainedHazard.Hazard.RouteCalibrationChainDepth, Is.EqualTo(2));
            Assert.That(chainedHazard.Hazard.RouteCalibrationRadiationDamping, Is.EqualTo(LuaMSectorDynamicEventSystem.RouteCalibrationRadiationDampingStep * 2));
            Assert.That(chainedHazard.Radiation.Intensity, Is.EqualTo(2));
            Assert.That(chainedHazard.Nav.Text, Does.Contain("radiation hazard [SC-4->RAD-2]"));
            Assert.That(chainedHazard.Nav.Text, Does.Contain("[DAMPED -2]"));
            Assert.That(chainedHazard.Nav.Color, Is.EqualTo(Color.FromHex("#B985FFFF")));

            var hazardNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("hazard:", StringComparison.Ordinal) &&
                                node.StoryId == chainedRecord.Story);
            Assert.That(hazardNode.Detail, Does.Contain("route calibration radiation damping: -2"));
            Assert.That(hazardNode.Detail, Does.Contain("chain depth 2"));

            var siteNotes = new List<(LuaMDynamicEventSiteObjectComponent Site, PaperComponent Paper)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, PaperComponent>();
            while (siteQuery.MoveNext(out _, out var site, out var paper))
            {
                if (site.Story == chainedRecord.Story)
                    siteNotes.Add((site, paper));
            }

            Assert.That(siteNotes, Has.Count.EqualTo(1));
            Assert.That(siteNotes.Single().Site.RouteCalibrationSource, Does.Contain("field-repair"));
            Assert.That(siteNotes.Single().Site.RouteCalibrationSource, Does.Contain("calibrated from"));
            Assert.That(siteNotes.Single().Site.RouteCalibrationSource, Does.Contain("black-box-echo"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("Route calibration source"));
            Assert.That(siteNotes.Single().Paper.Content, Does.Contain("field-repair"));

            var siteNode = dynamicEvents.BuildSectorMapUiEntries()
                .Single(node => node.NodeId.StartsWith("site:", StringComparison.Ordinal) &&
                                node.StoryId == chainedRecord.Story);
            Assert.That(siteNode.Risk, Does.Contain("source packet"));
            Assert.That(siteNode.Risk, Does.Contain("field-repair"));
            Assert.That(siteNode.Risk, Does.Contain("calibrated from"));
        });

        await server.WaitPost(() =>
        {
            Assert.That(storySystem.TryResolveStory(chainedRecord!.Story, "integration-test", "Chained calibration route checked."), Is.True);
        });

        await pair.RunTicksSync(5);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DustSectorConditionSpawnsSensorDriftEchoMarker()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var transformSystem = entManager.System<SharedTransformSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();

        LuaMSectorStoryRecord? record = null;
        LuaMSectorStoryRecord? calibratedRecord = null;
        var conditionError = string.Empty;
        var eventError = string.Empty;
        var calibratedEventError = string.Empty;
        bool conditionSeeded = false;
        bool generated = false;
        bool calibratedGenerated = false;
        bool stabilizedEvidenceFiled = false;
        EntityUid user = default;
        EntityUid stabilizedFieldPacket = default;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (markerQuery.MoveNext(out var marker, out _))
            {
                entManager.DeleteEntity(marker);
            }

            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
            while (siteQuery.MoveNext(out var site, out _))
            {
                entManager.DeleteEntity(site);
            }

            var hazardQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
            while (hazardQuery.MoveNext(out var hazard, out _))
            {
                entManager.DeleteEntity(hazard);
            }

            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
            while (driftQuery.MoveNext(out var drift, out _))
            {
                entManager.DeleteEntity(drift);
            }

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(5.0f, 5.0f), mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            user = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            conditionSeeded = storySystem.TrySeedSectorCondition(
                "dust-cloud",
                "Dust cloud",
                3,
                "Sensor drift and poor visual fixes are likely near active route markers.",
                "integration-test",
                out _,
                out conditionError);

            generated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out record,
                out eventError,
                templateId: "navigation-drift",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(5.0f, 5.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(conditionSeeded, Is.True, conditionError);
            Assert.That(generated, Is.True, eventError);
            Assert.That(record, Is.Not.Null);
            Assert.That(record!.Hazard, Does.Contain("дрейф сенсоров"));

            var primaryMarkers = new List<LuaMDynamicEventMarkerComponent>();
            var primaryQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (primaryQuery.MoveNext(out _, out var primary))
            {
                primaryMarkers.Add(primary);
            }

            Assert.That(primaryMarkers, Has.Count.EqualTo(1));
            Assert.That(primaryMarkers.Single().Story, Is.EqualTo(record.Story));
            Assert.That(primaryMarkers.Single().TemplateId, Is.EqualTo("navigation-drift"));

            var driftMarkers = new List<(EntityUid Uid, LuaMDynamicEventSensorDriftComponent Drift, NavMapBeaconComponent Nav, TransformComponent Xform)>();
            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent, NavMapBeaconComponent, TransformComponent>();
            while (driftQuery.MoveNext(out var uid, out var drift, out var nav, out var xform))
            {
                driftMarkers.Add((uid, drift, nav, xform));
            }

            Assert.That(driftMarkers, Has.Count.EqualTo(1));
            var driftMarker = driftMarkers.Single();
            Assert.That(driftMarker.Drift.Story, Is.EqualTo(record.Story));
            Assert.That(driftMarker.Drift.TemplateId, Is.EqualTo("navigation-drift"));
            Assert.That(driftMarker.Drift.ConditionId, Is.EqualTo("dust-cloud"));
            Assert.That(driftMarker.Drift.CreatedBy, Is.EqualTo("integration-test"));
            Assert.That(driftMarker.Drift.DriftLocation, Does.Contain("x 9.5"));
            Assert.That(driftMarker.Drift.DriftLocation, Does.Contain("y 2.0"));
            Assert.That(driftMarker.Nav.Text, Does.Contain("эхо дрейфа сенсоров"));
            Assert.That(driftMarker.Nav.Text, Does.Contain("[SC-3]"));
            Assert.That(driftMarker.Nav.Color, Is.EqualTo(Color.FromHex("#FF9F1C99")));
            Assert.That(driftMarker.Nav.Enabled, Is.True);
            Assert.That(driftMarker.Xform.MapID, Is.EqualTo(mapId));
            var driftMapCoordinates = transformSystem.ToMapCoordinates(driftMarker.Xform.Coordinates);
            Assert.That(driftMapCoordinates.Position.X, Is.EqualTo(9.5f).Within(0.01f));
            Assert.That(driftMapCoordinates.Position.Y, Is.EqualTo(2.0f).Within(0.01f));
            Assert.That(entManager.HasComponent<LuaMDynamicEventMarkerComponent>(driftMarker.Uid), Is.False);
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out var routePingResult), Is.True, routePingResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
            Assert.That(driftQuery.MoveNext(out _, out _), Is.False);

            var primaryMarkers = new List<LuaMDynamicEventMarkerComponent>();
            var primaryQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (primaryQuery.MoveNext(out _, out var primary))
            {
                primaryMarkers.Add(primary);
            }

            Assert.That(primaryMarkers, Has.Count.EqualTo(1));
            Assert.That(primaryMarkers.Single().Story, Is.EqualTo(record!.Story));
            Assert.That(primaryMarkers.Single().RoutePingCount, Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().SensorDriftMarkers, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildSectorMapUiEntries().Any(node => node.NodeId.StartsWith("drift:", StringComparison.Ordinal)), Is.False);
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out var stabilizeRouteResult), Is.True, stabilizeRouteResult);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
            Assert.That(driftQuery.MoveNext(out _, out _), Is.False);

            var primaryMarkers = new List<LuaMDynamicEventMarkerComponent>();
            var primaryQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            while (primaryQuery.MoveNext(out _, out var primary))
            {
                primaryMarkers.Add(primary);
            }

            Assert.That(primaryMarkers, Has.Count.EqualTo(1));
            Assert.That(primaryMarkers.Single().Story, Is.EqualTo(record!.Story));
            Assert.That(primaryMarkers.Single().RoutePingCount, Is.EqualTo(2));
            Assert.That(primaryMarkers.Single().LastRoutePingSummary, Does.Contain("стабилизирован"));
        });

        await server.WaitPost(() =>
        {
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out var markerUid, out _), Is.True);
            Assert.That(dynamicEvents.TryPrintMarkerFieldPacket(markerUid, user, out stabilizedFieldPacket), Is.True);
            stabilizedEvidenceFiled = evidenceSystem.TryFileEvidence(stabilizedFieldPacket, user);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(stabilizedEvidenceFiled, Is.True);
            Assert.That(storySystem.GetStatusSnapshot().Hazards.Single(hazard => hazard.Story == record!.Story).Resolved, Is.True);
            var automation = dynamicEvents.BuildAutomationUiEntry();
            Assert.That(automation.RouteCalibrationCredits, Is.EqualTo(1));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("navigation-drift"));
            Assert.That(automation.RouteCalibrationSource, Does.Contain("x 5.0"));
            Assert.That(automation.RouteCalibrationSensorDriftSuppressionPreview, Is.True);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
            Assert.That(markerQuery.MoveNext(out _, out _), Is.False);
        });

        await server.WaitPost(() =>
        {
            calibratedGenerated = dynamicEvents.TryGenerateDynamicEvent(
                "integration-test-calibrated-dust",
                out calibratedRecord,
                out calibratedEventError,
                templateId: "quiet-distress",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(7.0f, 4.0f), mapId));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(calibratedGenerated, Is.True, calibratedEventError);
            Assert.That(calibratedRecord, Is.Not.Null);

            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
            Assert.That(driftQuery.MoveNext(out _, out _), Is.False);

            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
            Assert.That(markerQuery.MoveNext(out _, out var marker, out var nav), Is.True);
            Assert.That(marker.Story, Is.EqualTo(calibratedRecord!.Story));
            Assert.That(marker.RoutePingCount, Is.EqualTo(1));
            Assert.That(marker.LastRoutePingActor, Is.EqualTo("route calibration"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("Калибровка маршрута"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("source packet"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("navigation-drift"));
            Assert.That(marker.LastRoutePingSummary, Does.Contain("эхо дрейфа сенсоров подавлено"));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("navigation-drift"));
            Assert.That(marker.RouteCalibrationSource, Does.Contain("x 5.0"));
            Assert.That(nav.Text, Does.Contain("[PING 1]"));
            Assert.That(nav.Color, Is.EqualTo(Color.FromHex("#00E5FFFF")));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RoutePingCount, Is.EqualTo(1));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationCredits, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationSource, Is.Empty);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().RouteCalibrationSensorDriftSuppressionPreview, Is.False);
            Assert.That(dynamicEvents.BuildAutomationUiEntry().SensorDriftMarkers, Is.EqualTo(0));
            Assert.That(dynamicEvents.BuildSectorMapUiEntries().Any(node => node.NodeId.StartsWith("drift:", StringComparison.Ordinal)), Is.False);
        });

        await server.WaitPost(() =>
        {
            Assert.That(storySystem.TryResolveStory(calibratedRecord!.Story, "integration-test", "Calibrated dust route checked."), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var driftQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
            Assert.That(driftQuery.MoveNext(out _, out _), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    private static void ClearPersistedSectorMemory(IResourceManager resources)
    {
        resources.UserData.Delete(SectorMemoryPath);
        resources.UserData.Delete(SectorMemoryBackupPath);
    }
}

[Reflect(false)]
public sealed class LuaMSectorStoryTestEventSystem : EntitySystem
{
    public int StoriesUnlocked;
    public int StoriesResolved;
    public int ReputationChanged;
    public int HazardsAcknowledged;
    public int InsuranceClaimed;
    public int BlackBoxesRecovered;
    public int CompaniesRegistered;
    public int ShipsRegistered;

    public void Reset()
    {
        StoriesUnlocked = 0;
        StoriesResolved = 0;
        ReputationChanged = 0;
        HazardsAcknowledged = 0;
        InsuranceClaimed = 0;
        BlackBoxesRecovered = 0;
        CompaniesRegistered = 0;
        ShipsRegistered = 0;
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorStoryUnlockedEvent>(OnStoryUnlocked);
        SubscribeLocalEvent<LuaMSectorStoryResolvedEvent>(OnStoryResolved);
        SubscribeLocalEvent<LuaMSectorReputationChangedEvent>(OnReputationChanged);
        SubscribeLocalEvent<LuaMSectorHazardAcknowledgedEvent>(OnHazardAcknowledged);
        SubscribeLocalEvent<LuaMSectorInsuranceClaimedEvent>(OnInsuranceClaimed);
        SubscribeLocalEvent<LuaMSectorBlackBoxRecoveredEvent>(OnBlackBoxRecovered);
        SubscribeLocalEvent<LuaMSectorCompanyRegisteredEvent>(OnCompanyRegistered);
        SubscribeLocalEvent<LuaMSectorShipRegisteredEvent>(OnShipRegistered);
    }

    private void OnStoryUnlocked(LuaMSectorStoryUnlockedEvent ev)
    {
        StoriesUnlocked++;
    }

    private void OnStoryResolved(LuaMSectorStoryResolvedEvent ev)
    {
        StoriesResolved++;
    }

    private void OnReputationChanged(LuaMSectorReputationChangedEvent ev)
    {
        ReputationChanged++;
    }

    private void OnHazardAcknowledged(LuaMSectorHazardAcknowledgedEvent ev)
    {
        HazardsAcknowledged++;
    }

    private void OnInsuranceClaimed(LuaMSectorInsuranceClaimedEvent ev)
    {
        InsuranceClaimed++;
    }

    private void OnBlackBoxRecovered(LuaMSectorBlackBoxRecoveredEvent ev)
    {
        BlackBoxesRecovered++;
    }

    private void OnCompanyRegistered(LuaMSectorCompanyRegisteredEvent ev)
    {
        CompaniesRegistered++;
    }

    private void OnShipRegistered(LuaMSectorShipRegisteredEvent ev)
    {
        ShipsRegistered++;
    }
}
