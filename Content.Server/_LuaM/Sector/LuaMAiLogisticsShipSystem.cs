using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using DiagnosticsStopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMAiLogisticsShipSystem : EntitySystem
{
    private const string AiMiningDronePrototype = "LuaMAiMiningDrone";
    private const string AiRepairDronePrototype = "LuaMAiRepairDrone";
    private const string AiLogisticsDronePrototype = "LuaMAiLogisticsDrone";
    private const string AiGuardDronePrototype = "LuaMAiGuardDrone";
    private const string AiScoutDronePrototype = "LuaMAiScoutDrone";
    private const string AiMedicDronePrototype = "LuaMAiMedicDrone";
    private const string AiServiceDronePrototype = "LuaMAiServiceDrone";
    private const int InitialCycleDelaySeconds = 60;
    private const int CycleDelaySeconds = 120;
    private const int MaxAiShipCrewDrones = 6;
    private const double UpdateTimeBudgetMilliseconds = 1.0;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CrewAuditInterval = TimeSpan.FromSeconds(1);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private LuaMAiSupplyDropSystem _supplyDrops = default!;
    [Dependency] private LuaMAiPhysicalBaseBudgetSystem _physical = default!;

    private ISawmill _sawmill = default!;
    private readonly Dictionary<EntityUid, TimeSpan> _nextCrewAudit = new();
    private TimeSpan _nextUpdate;
    private int _shipCursor;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _log.GetSawmill("luam.ai_logistics");
        SubscribeLocalEvent<LuaMAiLogisticsShipComponent, ComponentShutdown>(OnShipShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_physical.Enabled)
        {
            _nextUpdate = TimeSpan.Zero;
            return;
        }

        var now = _timing.CurTime;
        if (_nextUpdate != TimeSpan.Zero && now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;
        var started = DiagnosticsStopwatch.GetTimestamp();
        var ships = _physical.GetEntitySlice(
            LuaMAiPhysicalEntityKind.Ship,
            ref _shipCursor,
            LuaMAiPhysicalBaseBudgetSystem.LogisticsShipsPerSlice);
        var processed = 0;
        var exhausted = _physical.GetLiveCount(LuaMAiPhysicalEntityKind.Ship) > ships.Count;

        foreach (var uid in ships)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<LuaMAiLogisticsShipComponent>(uid, out var logistics))
                continue;

            processed++;
            if (!_nextCrewAudit.TryGetValue(uid, out var nextCrewAudit) || now >= nextCrewAudit)
            {
                EnsureCrewForShip(uid, logistics);
                _nextCrewAudit[uid] = now + CrewAuditInterval;
            }

            if (logistics.BehaviorState is "emergency_hold" or "disengaging" or "replanning_route" or "pilot_conflict" or "pilot_lost")
                continue;

            if (logistics.NextCycle == TimeSpan.Zero)
            {
                logistics.NextCycle = now + TimeSpan.FromSeconds(InitialCycleDelaySeconds);
                continue;
            }

            if (now < logistics.NextCycle)
                continue;

            logistics.Cycles++;
            logistics.NextCycle = now + TimeSpan.FromSeconds(CycleDelaySeconds + Math.Min(logistics.Cycles, 6) * 10);

            var role = string.IsNullOrWhiteSpace(logistics.Role) ? "hauler" : logistics.Role;
            var vesselId = string.IsNullOrWhiteSpace(logistics.VesselId) ? "unknown-vessel" : logistics.VesselId;
            var displayName = string.IsNullOrWhiteSpace(logistics.DisplayName) ? vesselId : logistics.DisplayName;
            var result = _stories.RecordAiBaseShipVisit(
                "LuaM physical AI logistics ship",
                role,
                vesselId,
                displayName);
            _supplyDrops.TrySpawnForLatestTrade("LuaM physical AI logistics ship", out _, out var dropSummary);

            if (!string.IsNullOrWhiteSpace(dropSummary))
                result += $" {dropSummary}";

            _sawmill.Info($"AI logistics ship cycle {logistics.Cycles}: {ToPrettyString(uid)} role={role} vessel={vesselId}; {result}");

            if (DiagnosticsStopwatch.GetElapsedTime(started).TotalMilliseconds >= UpdateTimeBudgetMilliseconds)
            {
                exhausted = true;
                break;
            }
        }

        if (DiagnosticsStopwatch.GetElapsedTime(started).TotalMilliseconds >= UpdateTimeBudgetMilliseconds)
            exhausted = true;

        _physical.RecordSlice("logistics", processed, exhausted);
    }

    public string EnsureCrewForShip(EntityUid shipUid, LuaMAiLogisticsShipComponent logistics)
    {
        if (!_physical.Enabled)
        {
            logistics.CrewRoleManifest.Clear();
            logistics.LastCrewReport = LuaMAiPhysicalBaseFeature.DisabledReason;
            return logistics.LastCrewReport;
        }

        EnsureCrewManifest(logistics);

        if (!_prototypes.HasIndex<EntityPrototype>(AiMiningDronePrototype))
        {
            logistics.LastCrewReport = $"AI ship crew skipped: prototype {AiMiningDronePrototype} is missing";
            return logistics.LastCrewReport;
        }

        var shipCoordinates = _transform.ToMapCoordinates(Transform(shipUid).Coordinates, logError: false);
        if (shipCoordinates == MapCoordinates.Nullspace)
        {
            logistics.LastCrewReport = "AI ship crew skipped: ship has no valid map position";
            return logistics.LastCrewReport;
        }

        var desiredRoles = logistics.CrewRoleManifest
            .Select(NormalizeCrewRole)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Take(MaxAiShipCrewDrones)
            .ToList();

        if (desiredRoles.Count == 0)
        {
            logistics.LastCrewReport = "AI ship crew skipped: empty manifest";
            return logistics.LastCrewReport;
        }

        logistics.CrewRoleManifest = desiredRoles;
        var desiredCounts = desiredRoles
            .GroupBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var existingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var existingTotal = 0;
        var crewMarkers = FindCrewMarkers(shipUid);
        var usedMarkers = new HashSet<EntityUid>();
        var usedGridTiles = new HashSet<Vector2i>();
        var markersUsed = 0;
        var gridTilesUsed = 0;

        var query = EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
        while (query.MoveNext(out var uid, out var drone))
        {
            if (TerminatingOrDeleted(uid) || drone.ParentShip != shipUid)
                continue;

            var role = NormalizeCrewRole(drone.DroneRole);
            drone.DroneRole = role;
            existingCounts[role] = existingCounts.GetValueOrDefault(role) + 1;
            existingTotal++;
        }

        var created = 0;
        foreach (var role in desiredRoles)
        {
            var existingForRole = existingCounts.GetValueOrDefault(role);
            if (existingForRole >= desiredCounts[role])
                continue;

            var spawned = false;
            if (TryPickCrewMarker(role, crewMarkers, usedMarkers, out var markerUid, out var marker))
            {
                spawned = SpawnCrewDrone(shipUid, logistics, Transform(markerUid).Coordinates, role, existingTotal + created, marker);
                if (spawned)
                {
                    usedMarkers.Add(markerUid);
                    markersUsed++;
                }
            }
            else if (TryPickFallbackCrewCoordinatesOnGrid(shipUid, role, existingTotal + created, usedGridTiles, out var gridCoordinates))
            {
                spawned = SpawnCrewDrone(shipUid, logistics, gridCoordinates, role, existingTotal + created, null);
                if (spawned)
                    gridTilesUsed++;
            }
            else
            {
                spawned = SpawnCrewDrone(shipUid, logistics, PickFallbackCrewCoordinates(shipCoordinates, role, existingTotal + created), role, existingTotal + created);
            }

            if (!spawned)
                break;

            existingCounts[role] = existingForRole + 1;
            created++;
        }

        logistics.MarkerCrewSpawns += markersUsed;
        logistics.GridCrewSpawns += gridTilesUsed;
        var activeMarkerSlots = CountActiveMarkerSlots(crewMarkers);
        var placementText = crewMarkers.Count > 0
            ? $"markers {activeMarkerSlots}/{crewMarkers.Count}"
            : TryComp<MapGridComponent>(shipUid, out _)
                ? $"grid fallback active {existingTotal + created}/{desiredRoles.Count}; grid created {gridTilesUsed}"
                : "radial fallback spawn";
        var profile = string.IsNullOrWhiteSpace(logistics.CrewProfileId)
            ? "unprofiled"
            : logistics.CrewProfileId;
        var behaviorText = string.IsNullOrWhiteSpace(logistics.BaseBehaviorMode)
            ? "doctrine pending"
            : $"doctrine {logistics.BaseBehaviorMode}/{logistics.BaseBehaviorFocusResource}";
        logistics.LastCrewReport = $"AI ship crew active {existingTotal + created}/{desiredRoles.Count}; created {created}; {placementText}; profile {profile}; {behaviorText}; manifest {string.Join(",", desiredRoles)}";
        return logistics.LastCrewReport;
    }

    private void OnShipShutdown(EntityUid uid, LuaMAiLogisticsShipComponent component, ComponentShutdown args)
    {
        _physical.ReleaseAdmission(uid, LuaMAiPhysicalEntityKind.Ship);
        _nextCrewAudit.Remove(uid);

        if (component.BehaviorCore is { Valid: true } core && !TerminatingOrDeleted(core))
            QueueDel(core);
        component.BehaviorCore = null;

        var query = EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
        while (query.MoveNext(out var droneUid, out var drone))
        {
            if (!TerminatingOrDeleted(droneUid) && drone.ParentShip == uid)
                QueueDel(droneUid);
        }
    }

    private void EnsureCrewManifest(LuaMAiLogisticsShipComponent logistics)
    {
        if (logistics.CrewRoleManifest.Count > 0)
        {
            logistics.CrewRoleManifest = logistics.CrewRoleManifest
                .Select(NormalizeCrewRole)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Take(MaxAiShipCrewDrones)
                .ToList();
            logistics.CrewManifestSource = string.IsNullOrWhiteSpace(logistics.CrewManifestSource)
                ? "custom"
                : logistics.CrewManifestSource;
            EnsureCustomCrewProfile(logistics);
            return;
        }

        var aiBase = _stories.GetAiBaseState();
        var profile = BuildCrewProfile(
            logistics.Role,
            logistics.VesselId,
            aiBase.BehaviorMode,
            aiBase.BehaviorFocusResource);
        logistics.CrewRoleManifest = profile.Manifest.ToList();
        logistics.CrewManifestSource = profile.ManifestSource;
        logistics.CrewProfileId = profile.ProfileId;
        logistics.CrewProfileSummary = profile.Summary;
        logistics.BaseBehaviorMode = aiBase.BehaviorMode;
        logistics.BaseBehaviorFocusResource = aiBase.BehaviorFocusResource;
        logistics.BaseBehaviorDirective = aiBase.BehaviorDirective;
        logistics.CrewStationPlan = profile.StationPlan.ToList();
    }

    public static List<string> BuildCrewManifest(
        string shipRole,
        string vesselId,
        string behaviorMode = "",
        string behaviorFocusResource = "")
    {
        return BuildCrewProfile(shipRole, vesselId, behaviorMode, behaviorFocusResource).Manifest.ToList();
    }

    public static LuaMAiShipCrewProfile BuildCrewProfile(
        string shipRole,
        string vesselId,
        string behaviorMode = "",
        string behaviorFocusResource = "")
    {
        var role = NormalizeShipRole(shipRole);
        var vesselClass = ClassifyCrewVessel(vesselId);
        var normalizedBehaviorMode = NormalizeBehaviorMode(behaviorMode);
        var normalizedFocus = NormalizeBehaviorFocus(behaviorFocusResource);
        var manifest = ApplyBehaviorToManifest(
            BuildCrewManifestForProfile(role, vesselClass),
            normalizedBehaviorMode,
            normalizedFocus);
        var profileId = string.IsNullOrWhiteSpace(normalizedBehaviorMode)
            ? $"{role}:{vesselClass}"
            : $"{role}:{vesselClass}:{normalizedBehaviorMode}:{normalizedFocus}";
        return new LuaMAiShipCrewProfile(
            profileId,
            $"profile:{profileId}",
            BuildCrewProfileSummary(role, vesselClass, normalizedBehaviorMode, normalizedFocus),
            manifest,
            BuildCrewStationPlan(manifest, vesselClass));
    }

    private static List<string> BuildCrewManifestForProfile(string role, string vesselClass)
    {
        return role switch
        {
            "builder" => vesselClass == "repair"
                ? ["repair", "repair", "service", "logistics", "guard"]
                : ["repair", "repair", "logistics", "guard"],
            "miner" => vesselClass == "carrier"
                ? ["miner", "miner", "miner", "logistics", "repair", "guard"]
                : ["miner", "miner", "logistics", "repair", "scout"],
            "scout" => ["scout", "scout", "logistics", "repair"],
            "guard" => vesselClass == "combat"
                ? ["guard", "guard", "scout", "repair", "logistics"]
                : ["guard", "guard", "repair", "logistics"],
            "trader" => vesselClass is "cargo" or "carrier"
                ? ["logistics", "logistics", "service", "repair", "scout"]
                : ["logistics", "logistics", "repair", "scout"],
            "medic" => vesselClass == "medical"
                ? ["medic", "medic", "logistics", "repair", "guard"]
                : ["medic", "logistics", "repair", "guard"],
            "service" => ["service", "logistics", "repair", "scout"],
            _ => ["logistics", "repair", "guard", "scout"],
        };
    }

    private static List<string> ApplyBehaviorToManifest(
        List<string> manifest,
        string behaviorMode,
        string focusResource)
    {
        manifest = manifest
            .Select(NormalizeCrewRole)
            .Where(role => role != "any")
            .Take(MaxAiShipCrewDrones)
            .ToList();

        void EnsureRole(string role)
        {
            role = NormalizeCrewRole(role);
            if (string.IsNullOrWhiteSpace(role) || role == "any" || manifest.Contains(role, StringComparer.OrdinalIgnoreCase))
                return;

            if (manifest.Count < MaxAiShipCrewDrones)
            {
                manifest.Add(role);
                return;
            }

            manifest[^1] = role;
        }

        switch (behaviorMode)
        {
            case "medical-followup":
                EnsureRole("medic");
                EnsureRole("logistics");
                EnsureRole("guard");
                break;
            case "critical-recovery":
            case "logistics-start":
            case "balanced-logistics":
                EnsureRole("logistics");
                EnsureRole(RoleForBehaviorFocus(focusResource));
                break;
            case "extraction":
                EnsureRole("miner");
                EnsureRole("logistics");
                break;
            case "construction":
                EnsureRole("repair");
                EnsureRole("logistics");
                break;
            case "medical-support":
                EnsureRole("medic");
                EnsureRole("logistics");
                break;
            case "crew-support":
                EnsureRole("service");
                EnsureRole("logistics");
                break;
            case "stable-watch":
                EnsureRole("scout");
                EnsureRole("guard");
                break;
        }

        return manifest
            .Take(MaxAiShipCrewDrones)
            .ToList();
    }

    private static string RoleForBehaviorFocus(string focusResource)
    {
        return NormalizeBehaviorFocus(focusResource) switch
        {
            "ore" => "miner",
            "hull-parts" or "electronics" => "repair",
            "medicine" => "medic",
            "food" => "service",
            "security" => "guard",
            "route-data" => "scout",
            _ => "logistics",
        };
    }

    private static void EnsureCustomCrewProfile(LuaMAiLogisticsShipComponent logistics)
    {
        var role = NormalizeShipRole(logistics.Role);
        var vesselClass = ClassifyCrewVessel(logistics.VesselId);
        if (string.IsNullOrWhiteSpace(logistics.CrewProfileId))
            logistics.CrewProfileId = $"custom:{role}:{vesselClass}";
        if (string.IsNullOrWhiteSpace(logistics.CrewProfileSummary))
            logistics.CrewProfileSummary = $"custom {role} crew profile for {vesselClass} vessel";
        if (logistics.CrewStationPlan.Count == 0)
            logistics.CrewStationPlan = BuildCrewStationPlan(logistics.CrewRoleManifest, vesselClass);
    }

    private bool SpawnCrewDrone(
        EntityUid shipUid,
        LuaMAiLogisticsShipComponent logistics,
        EntityCoordinates coordinates,
        string role,
        int index,
        LuaMAiShipCrewMarkerComponent? marker)
    {
        var mapCoordinates = _transform.ToMapCoordinates(coordinates, logError: false);
        if (mapCoordinates == MapCoordinates.Nullspace ||
            !_physical.CanSpawn(LuaMAiPhysicalEntityKind.Drone, mapCoordinates.MapId, out _))
        {
            return false;
        }

        var droneUid = Spawn(ResolveCrewDronePrototype(role), coordinates);
        ConfigureCrewDrone(shipUid, logistics, droneUid, role, index);
        if (marker != null)
            marker.LastSpawnedDrone = droneUid;
        return true;
    }

    private bool SpawnCrewDrone(
        EntityUid shipUid,
        LuaMAiLogisticsShipComponent logistics,
        MapCoordinates coordinates,
        string role,
        int index)
    {
        if (coordinates == MapCoordinates.Nullspace ||
            !_physical.CanSpawn(LuaMAiPhysicalEntityKind.Drone, coordinates.MapId, out _))
        {
            return false;
        }

        var droneUid = Spawn(ResolveCrewDronePrototype(role), coordinates);
        ConfigureCrewDrone(shipUid, logistics, droneUid, role, index);
        return true;
    }

    private string ResolveCrewDronePrototype(string role)
    {
        var prototype = NormalizeCrewRole(role) switch
        {
            "repair" => AiRepairDronePrototype,
            "logistics" => AiLogisticsDronePrototype,
            "guard" => AiGuardDronePrototype,
            "scout" => AiScoutDronePrototype,
            "medic" => AiMedicDronePrototype,
            "service" => AiServiceDronePrototype,
            _ => AiMiningDronePrototype,
        };

        return _prototypes.HasIndex<EntityPrototype>(prototype)
            ? prototype
            : AiMiningDronePrototype;
    }

    private void ConfigureCrewDrone(
        EntityUid shipUid,
        LuaMAiLogisticsShipComponent logistics,
        EntityUid droneUid,
        string role,
        int index)
    {
        var drone = EnsureComp<LuaMAiMiningDroneComponent>(droneUid);
        drone.BaseId = string.IsNullOrWhiteSpace(logistics.BaseId) ? "LuaM-AI-Base" : logistics.BaseId;
        drone.ParentShip = shipUid;
        drone.VesselId = string.IsNullOrWhiteSpace(logistics.VesselId) ? "unknown-vessel" : logistics.VesselId;
        drone.DisplayName = string.IsNullOrWhiteSpace(logistics.DisplayName) ? drone.VesselId : logistics.DisplayName;
        drone.DroneRole = role;
        drone.DroneId = $"{role}-{_random.Next(1000, 9999)}";
        drone.CrewAssignment = $"{NormalizeShipRole(logistics.Role)} ship crew #{index + 1}";
        drone.CrewStation = PickCrewStation(role, index, logistics.CrewStationPlan);
        drone.BaseBehaviorMode = logistics.BaseBehaviorMode;
        drone.BaseBehaviorFocusResource = logistics.BaseBehaviorFocusResource;
        drone.CrewDirective = BuildCrewDirective(role, drone.DisplayName, drone.BaseBehaviorMode, drone.BaseBehaviorFocusResource);
        drone.CrewPriority = PickCrewPriority(role);
        drone.State = "ship_crew_launch";

        var droneTransform = Transform(droneUid);
        if (droneTransform.GridUid == shipUid || droneTransform.ParentUid == shipUid)
        {
            drone.HasCrewHome = true;
            drone.CrewHomeLocalPosition = droneTransform.LocalPosition;
            drone.LastCrewHomeAction = "home_station_assigned";
        }
        else if (TryAttachCrewDroneToShipHome(shipUid, droneUid, drone, droneTransform))
        {
            drone.LastCrewHomeAction = "home_station_assigned";
        }
        else
        {
            drone.HasCrewHome = false;
            drone.CrewHomeLocalPosition = Vector2.Zero;
            drone.LastCrewHomeAction = "no_ship_grid_home";
        }

        drone.NextMove = _timing.CurTime + TimeSpan.FromSeconds(2 + index);
        drone.NextSocialScan = _timing.CurTime + TimeSpan.FromSeconds(1 + index % 3);
        drone.NextCrewDuty = _timing.CurTime + TimeSpan.FromSeconds(3 + index);
        drone.NextMine = role == "miner"
            ? _timing.CurTime + TimeSpan.FromSeconds(25 + index * 5)
            : _timing.CurTime + TimeSpan.FromDays(1);

        _metaData.SetEntityName(droneUid, $"LuaM AI {role} drone {drone.DroneId}");
        logistics.CrewSpawnAttempts++;
    }

    private bool TryAttachCrewDroneToShipHome(
        EntityUid shipUid,
        EntityUid droneUid,
        LuaMAiMiningDroneComponent drone,
        TransformComponent droneTransform)
    {
        var shipCoordinates = _transform.ToMapCoordinates(Transform(shipUid).Coordinates, logError: false);
        var droneCoordinates = _transform.ToMapCoordinates(droneTransform.Coordinates, logError: false);
        if (shipCoordinates == MapCoordinates.Nullspace ||
            droneCoordinates == MapCoordinates.Nullspace ||
            shipCoordinates.MapId != droneCoordinates.MapId)
            return false;

        var localCoordinates = _transform.ToCoordinates(shipUid, droneCoordinates);
        _transform.SetCoordinates(droneUid, localCoordinates);
        drone.HasCrewHome = true;
        drone.CrewHomeLocalPosition = localCoordinates.Position;
        return true;
    }

    private MapCoordinates PickFallbackCrewCoordinates(MapCoordinates shipCoordinates, string role, int index)
    {
        var baseAngle = role switch
        {
            "repair" => MathF.PI * 0.25f,
            "logistics" => MathF.PI * 0.75f,
            "guard" => MathF.PI * 1.25f,
            "scout" => MathF.PI * 1.75f,
            "medic" => MathF.PI * 0.5f,
            "service" => MathF.PI,
            _ => 0f,
        };
        var angle = baseAngle + index * 0.45f + _random.NextFloat(-0.12f, 0.12f);
        var distance = _random.NextFloat(1.5f, 4.5f);
        var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
        return new MapCoordinates(shipCoordinates.Position + offset, shipCoordinates.MapId);
    }

    private bool TryPickFallbackCrewCoordinatesOnGrid(
        EntityUid shipUid,
        string role,
        int index,
        HashSet<Vector2i> usedTiles,
        out EntityCoordinates coordinates)
    {
        coordinates = EntityCoordinates.Invalid;
        if (!TryComp<MapGridComponent>(shipUid, out var grid))
            return false;

        var tiles = _map.GetAllTiles(shipUid, grid)
            .Where(tile => !tile.Tile.IsEmpty && !usedTiles.Contains(tile.GridIndices))
            .ToList();
        if (tiles.Count == 0)
            return false;

        var target = PickFallbackCrewLocalTarget(grid, role, index);
        var selected = tiles
            .OrderBy(tile => Vector2.DistanceSquared(_map.GridTileToLocal(shipUid, grid, tile.GridIndices).Position, target))
            .ThenBy(tile => tile.GridIndices.X)
            .ThenBy(tile => tile.GridIndices.Y)
            .First();

        coordinates = _map.GridTileToLocal(shipUid, grid, selected.GridIndices);
        usedTiles.Add(selected.GridIndices);
        return true;
    }

    private static Vector2 PickFallbackCrewLocalTarget(MapGridComponent grid, string role, int index)
    {
        var bounds = grid.LocalAABB;
        var center = bounds.Center;
        var xOffset = MathF.Max(0.5f, bounds.Width * 0.25f);
        var yOffset = MathF.Max(0.5f, bounds.Height * 0.25f);
        var roleOffset = role switch
        {
            "repair" => new Vector2(-xOffset, -yOffset),
            "logistics" => new Vector2(xOffset, -yOffset),
            "guard" => new Vector2(xOffset, yOffset),
            "scout" => new Vector2(-xOffset, yOffset),
            "medic" => new Vector2(0f, yOffset),
            "service" => new Vector2(0f, -yOffset),
            _ => Vector2.Zero,
        };

        var spread = new Vector2((index % 2) * 0.35f, (index / 2 % 2) * 0.35f);
        return center + roleOffset + spread;
    }

    private List<Entity<LuaMAiShipCrewMarkerComponent>> FindCrewMarkers(EntityUid shipUid)
    {
        var markers = new List<Entity<LuaMAiShipCrewMarkerComponent>>();
        var query = EntityQueryEnumerator<LuaMAiShipCrewMarkerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var marker, out var xform))
        {
            if (TerminatingOrDeleted(uid) || (xform.GridUid != shipUid && xform.ParentUid != shipUid))
                continue;

            markers.Add((uid, marker));
        }

        return markers
            .OrderByDescending(marker => marker.Comp.Priority)
            .ThenBy(marker => marker.Comp.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(marker => marker.Comp.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryPickCrewMarker(
        string role,
        IReadOnlyList<Entity<LuaMAiShipCrewMarkerComponent>> markers,
        HashSet<EntityUid> usedMarkers,
        out EntityUid markerUid,
        out LuaMAiShipCrewMarkerComponent marker)
    {
        if (TryPickCrewMarker(role, exactRole: true, markers, usedMarkers, out markerUid, out marker))
            return true;

        return TryPickCrewMarker(role, exactRole: false, markers, usedMarkers, out markerUid, out marker);
    }

    private int CountActiveMarkerSlots(IReadOnlyList<Entity<LuaMAiShipCrewMarkerComponent>> markers)
    {
        var count = 0;
        foreach (var marker in markers)
        {
            if (marker.Comp.LastSpawnedDrone.IsValid() &&
                EntityManager.EntityExists(marker.Comp.LastSpawnedDrone) &&
                !TerminatingOrDeleted(marker.Comp.LastSpawnedDrone))
                count++;
        }

        return count;
    }

    private static bool TryPickCrewMarker(
        string role,
        bool exactRole,
        IReadOnlyList<Entity<LuaMAiShipCrewMarkerComponent>> markers,
        HashSet<EntityUid> usedMarkers,
        out EntityUid markerUid,
        out LuaMAiShipCrewMarkerComponent marker)
    {
        foreach (var candidate in markers)
        {
            if (usedMarkers.Contains(candidate.Owner))
                continue;

            var markerRole = NormalizeCrewRole(candidate.Comp.Role);
            var matches = exactRole
                ? markerRole.Equals(role, StringComparison.OrdinalIgnoreCase)
                : markerRole == "any";
            if (!matches)
                continue;

            markerUid = candidate.Owner;
            marker = candidate.Comp;
            return true;
        }

        markerUid = EntityUid.Invalid;
        marker = default!;
        return false;
    }

    private static string PickCrewStation(string role, int index, IReadOnlyList<string>? stationPlan = null)
    {
        if (stationPlan != null && index >= 0 && index < stationPlan.Count)
        {
            var planned = stationPlan[index];
            var separator = planned.IndexOf(':');
            if (separator > 0)
            {
                var plannedRole = NormalizeCrewRole(planned[..separator]);
                var station = planned[(separator + 1)..].Trim();
                if (plannedRole == role && !string.IsNullOrWhiteSpace(station))
                    return station;
            }
        }

        var suffix = index % 2 == 0 ? "primary" : "secondary";
        return role switch
        {
            "repair" => $"{suffix} engineering/hull access",
            "logistics" => $"{suffix} cargo relay",
            "guard" => $"{suffix} airlock screen",
            "scout" => $"{suffix} sensor path",
            "medic" => $"{suffix} medical triage point",
            "service" => $"{suffix} crew support point",
            _ => $"{suffix} ore handling bay",
        };
    }

    private static string BuildCrewDirective(
        string role,
        string displayName,
        string behaviorMode = "",
        string behaviorFocusResource = "")
    {
        var origin = string.IsNullOrWhiteSpace(displayName) ? "AI ship" : displayName;
        var directive = role switch
        {
            "repair" => $"inspect hull access and keep {origin} operational",
            "logistics" => $"move supplies between {origin} and the AI base",
            "guard" => $"screen close contacts around {origin}",
            "scout" => $"map approach routes around {origin}",
            "medic" => $"mark wounded crew paths near {origin}",
            "service" => $"support crew traffic and non-critical requests on {origin}",
            _ => $"run ore cycles for {origin}",
        };

        return string.IsNullOrWhiteSpace(behaviorMode)
            ? directive
            : $"{directive}; base doctrine {behaviorMode}/{behaviorFocusResource}";
    }

    private static int PickCrewPriority(string role)
    {
        return role switch
        {
            "guard" => 80,
            "repair" => 75,
            "medic" => 70,
            "logistics" => 65,
            "scout" => 55,
            "service" => 45,
            _ => 60,
        };
    }

    private static List<string> BuildCrewStationPlan(IReadOnlyList<string> manifest, string vesselClass)
    {
        var plan = new List<string>(manifest.Count);
        for (var i = 0; i < manifest.Count; i++)
        {
            var role = NormalizeCrewRole(manifest[i]);
            var station = PickCrewStationForProfile(role, vesselClass, i);
            plan.Add($"{role}: {station}");
        }

        return plan;
    }

    private static string PickCrewStationForProfile(string role, string vesselClass, int index)
    {
        var suffix = index % 2 == 0 ? "primary" : "secondary";
        return role switch
        {
            "repair" => vesselClass == "combat"
                ? $"{suffix} armor and weapons access"
                : vesselClass == "carrier"
                    ? $"{suffix} hangar/hull service lane"
                    : $"{suffix} engineering/hull access",
            "logistics" => vesselClass == "cargo"
                ? $"{suffix} cargo spine relay"
                : vesselClass == "carrier"
                    ? $"{suffix} hangar supply relay"
                    : $"{suffix} cargo relay",
            "guard" => vesselClass == "combat"
                ? $"{suffix} weapons corridor screen"
                : $"{suffix} airlock screen",
            "scout" => vesselClass == "carrier"
                ? $"{suffix} flight-deck sensor path"
                : $"{suffix} sensor path",
            "medic" => vesselClass == "medical"
                ? $"{suffix} triage bay"
                : $"{suffix} medical triage point",
            "service" => $"{suffix} crew support point",
            _ => vesselClass == "carrier"
                ? $"{suffix} drone ore handling bay"
                : $"{suffix} ore handling bay",
        };
    }

    private static string BuildCrewProfileSummary(
        string role,
        string vesselClass,
        string behaviorMode = "",
        string behaviorFocusResource = "")
    {
        var summary = role switch
        {
            "builder" => $"repair-first crew profile for {vesselClass} vessel: hull access, cargo relay, and airlock watch stay covered",
            "miner" => $"mining crew profile for {vesselClass} vessel: ore handling, repair cover, and route scouting stay active",
            "scout" => $"scout crew profile for {vesselClass} vessel: route sensors, cargo link, and repair cover stay active",
            "guard" => $"security crew profile for {vesselClass} vessel: perimeter screen, sensor pass, and repair cover stay active",
            "trader" => $"trade crew profile for {vesselClass} vessel: cargo relays, service support, and route scouting stay active",
            "medic" => $"medical crew profile for {vesselClass} vessel: triage, evacuation route, and escort cover stay active",
            "service" => $"service crew profile for {vesselClass} vessel: crew support, logistics, repair, and route hints stay active",
            _ => $"hauler crew profile for {vesselClass} vessel: supply relay, repair cover, perimeter watch, and route scouting stay active",
        };

        return string.IsNullOrWhiteSpace(behaviorMode)
            ? summary
            : $"{summary}; behavior doctrine {behaviorMode} focused on {behaviorFocusResource}";
    }

    private static string NormalizeBehaviorMode(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "bootstrap" or "logistics-start" or "medical-followup" or "critical-recovery" or "extraction" or "construction" or "medical-support" or "crew-support" or "balanced-logistics" or "stable-watch" => value,
            _ => string.Empty,
        };
    }

    private static string NormalizeBehaviorFocus(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "ore" or "hull-parts" or "electronics" or "medicine" or "food" or "fuel" or "security" or "route-data" or "base" => value,
            _ => string.IsNullOrWhiteSpace(value) ? "general" : value,
        };
    }

    private static string ClassifyCrewVessel(string vesselId)
    {
        var id = NormalizeCrewVesselId(vesselId);
        return id switch
        {
            "qj490" or "qj340" or "qj270" or "ample" or "antlion" or "bowman" or "hound" or "inertia" or "mock" or "rook" or "snakelet" or "stratorat" or "taser" or "veska" or "zephyr" => "carrier",
            "hammerhead" or "tokarev" or "tzipora" or "claymore" or "sellsword" or "ravager" or "praetorian" or "balor" or "sentinel" or "altair" or "andromeda" or "fenrir" or "vulture" => "combat",
            "triage" or "medicus" or "syringe" => "medical",
            "remontnik" or "welder" or "framework" => "repair",
            "gruznyk" or "takeaway" or "shareholder" or "rosetta" or "promise" or "bratan" or "canister" or "parcel" or "mailpod" => "cargo",
            "baeg" or "stubby" => "light-shuttle",
            "" => "standard",
            _ => "standard",
        };
    }

    private static string NormalizeCrewVesselId(string vesselId)
    {
        if (string.IsNullOrWhiteSpace(vesselId))
            return string.Empty;

        var builder = new System.Text.StringBuilder(vesselId.Length);
        foreach (var ch in vesselId.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string NormalizeCrewRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "" or "any" or "all" => "any",
            "builder" or "engineer" or "repairer" or "maintenance" => "repair",
            "hauler" or "trader" or "cargo" => "logistics",
            "medical" or "doctor" => "medic",
            "janitor" or "cleaner" => "service",
            "security" => "guard",
            "prospector" => "miner",
            "miner" or "guard" or "scout" or "logistics" or "repair" or "medic" or "service" => role.Trim().ToLowerInvariant(),
            _ => "miner",
        };
    }

    private static string NormalizeShipRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "builder" or "repair" or "engineer" => "builder",
            "miner" or "prospector" => "miner",
            "scout" => "scout",
            "guard" or "security" => "guard",
            "trader" => "trader",
            "medic" or "medical" => "medic",
            "service" => "service",
            _ => "hauler",
        };
    }

    public sealed record LuaMAiShipCrewProfile(
        string ProfileId,
        string ManifestSource,
        string Summary,
        List<string> Manifest,
        List<string> StationPlan);
}
