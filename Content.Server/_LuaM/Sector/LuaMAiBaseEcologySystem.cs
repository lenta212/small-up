using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using DiagnosticsStopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMAiBaseEcologySystem : EntitySystem
{
    private const string ZoneMarkerPrototype = "LuaMAiBaseZoneMarker";
    public const string ZoneDock = "dock";
    public const string ZoneStorage = "storage";
    public const string ZoneMining = "mining";
    public const string ZonePatrol = "patrol";
    public const string ZoneContact = "contact";

    private const float ZoneArrivalDistance = 1.35f;
    private const int WorkTicksPerCycle = 2;
    private const double StuckCheckIntervalSeconds = 5;
    private const float StuckMovementEpsilon = 0.2f;
    private const int StuckChecksBeforeReport = 2;
    private const double UpdateTimeBudgetMilliseconds = 1.0;
    private static readonly TimeSpan DisabledCleanupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);

    private static readonly (string ZoneType, string Label, Vector2 Offset)[] RequiredZones =
    {
        (ZoneDock, "AI dock", new Vector2(2.8f, 0f)),
        (ZoneStorage, "AI storage", new Vector2(-2.8f, 0f)),
        (ZoneMining, "AI mining route", new Vector2(0f, 4.2f)),
        (ZonePatrol, "AI patrol ring", new Vector2(0f, -3.4f)),
        (ZoneContact, "AI contact point", new Vector2(3.2f, 3.2f)),
    };

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private LuaMAiPhysicalBaseBudgetSystem _physical = default!;

    private TimeSpan _nextDisabledCleanup;
    private TimeSpan _nextUpdate;
    private int _anchorCursor;
    private int _zoneCursor;
    private int _assignmentCursor;
    private int _progressCursor;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMAiBaseAnchorComponent, ComponentStartup>(OnAnchorStartup);
        SubscribeLocalEvent<LuaMAiBaseZoneComponent, ComponentStartup>(OnZoneStartup);
        SubscribeLocalEvent<LuaMAiSupplyDropComponent, ComponentStartup>(OnSupplyDropStartup);
        SubscribeLocalEvent<LuaMAiDroneTaskComponent, ComponentStartup>(OnDisabledDroneTaskStartup);
        SubscribeLocalEvent<LuaMAiBaseAnchorComponent, EntityTerminatingEvent>(OnAnchorTerminating);
        SubscribeLocalEvent<LuaMAiBaseAnchorComponent, ComponentShutdown>(OnAnchorShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_physical.Enabled)
        {
            _nextUpdate = TimeSpan.Zero;
            if (_nextDisabledCleanup == TimeSpan.Zero || _timing.CurTime >= _nextDisabledCleanup)
            {
                CleanupDisabledAiBase();
                _nextDisabledCleanup = _timing.CurTime + DisabledCleanupInterval;
            }

            return;
        }

        var now = _timing.CurTime;
        if (_nextUpdate != TimeSpan.Zero && now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;
        var started = DiagnosticsStopwatch.GetTimestamp();
        var processed = 0;
        var exhausted = false;

        var anchors = _physical.GetEntitySlice(
            LuaMAiPhysicalEntityKind.Anchor,
            ref _anchorCursor,
            LuaMAiPhysicalBaseBudgetSystem.EcologyAnchorsPerSlice);
        EnsureZonesForAnchors(anchors);
        processed += anchors.Count;

        if (IsTimeBudgetExhausted(started))
        {
            exhausted = true;
        }
        else
        {
            var zones = _physical.GetEntitySlice(
                LuaMAiPhysicalEntityKind.Zone,
                ref _zoneCursor,
                LuaMAiPhysicalBaseBudgetSystem.EcologyZonesPerSlice);
            UpdateZoneCompensationPlans(zones);
            processed += zones.Count;
            exhausted = IsTimeBudgetExhausted(started);
        }

        if (!exhausted)
        {
            var drones = _physical.GetEntitySlice(
                LuaMAiPhysicalEntityKind.Drone,
                ref _assignmentCursor,
                LuaMAiPhysicalBaseBudgetSystem.EcologyDronesPerSlice);
            AssignDroneTasks(drones);
            processed += drones.Count;
            exhausted = IsTimeBudgetExhausted(started);
        }

        if (!exhausted)
        {
            var drones = _physical.GetEntitySlice(
                LuaMAiPhysicalEntityKind.Drone,
                ref _progressCursor,
                LuaMAiPhysicalBaseBudgetSystem.EcologyDronesPerSlice);
            UpdateDroneTaskProgress(drones);
            processed += drones.Count;
            exhausted = IsTimeBudgetExhausted(started);
        }

        _physical.RecordSlice("ecology", processed, exhausted);
    }

    private void OnAnchorStartup(EntityUid uid, LuaMAiBaseAnchorComponent component, ComponentStartup args)
    {
        _physical.AdmitOrReject(uid, LuaMAiPhysicalEntityKind.Anchor);
    }

    private void OnZoneStartup(EntityUid uid, LuaMAiBaseZoneComponent component, ComponentStartup args)
    {
        _physical.AdmitOrReject(uid, LuaMAiPhysicalEntityKind.Zone);
    }

    private void OnSupplyDropStartup(EntityUid uid, LuaMAiSupplyDropComponent component, ComponentStartup args)
    {
        _physical.AdmitOrReject(uid, LuaMAiPhysicalEntityKind.Drop);
    }

    private void OnDisabledDroneTaskStartup(EntityUid uid, LuaMAiDroneTaskComponent component, ComponentStartup args)
    {
        if (_physical.Enabled || TerminatingOrDeleted(uid))
            return;

        RemCompDeferred<LuaMAiDroneTaskComponent>(uid);
    }

    private void OnAnchorTerminating(
        EntityUid uid,
        LuaMAiBaseAnchorComponent anchor,
        ref EntityTerminatingEvent args)
    {
        CleanupAnchor(uid, anchor);
    }

    private void OnAnchorShutdown(EntityUid uid, LuaMAiBaseAnchorComponent anchor, ComponentShutdown args)
    {
        CleanupAnchor(uid, anchor);
    }

    private void CleanupAnchor(EntityUid uid, LuaMAiBaseAnchorComponent anchor)
    {
        var mapId = MapId.Nullspace;
        if (!_physical.TryGetAdmissionMap(uid, LuaMAiPhysicalEntityKind.Anchor, out mapId) &&
            TryComp<TransformComponent>(uid, out var fallbackTransform))
        {
            mapId = fallbackTransform.MapID;
        }

        _physical.ReleaseAdmission(uid, LuaMAiPhysicalEntityKind.Anchor);

        if (mapId == MapId.Nullspace)
            return;

        var anchorQuery = EntityQueryEnumerator<LuaMAiBaseAnchorComponent, TransformComponent>();
        while (anchorQuery.MoveNext(out var otherUid, out var otherAnchor, out var otherTransform))
        {
            if (otherUid != uid &&
                !TerminatingOrDeleted(otherUid) &&
                otherTransform.MapID == mapId &&
                otherAnchor.BaseId.Equals(anchor.BaseId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var query = EntityQueryEnumerator<LuaMAiBaseZoneComponent, TransformComponent>();
        while (query.MoveNext(out var zoneUid, out var zone, out var zoneTransform))
        {
            if (TerminatingOrDeleted(zoneUid) ||
                zoneTransform.MapID != mapId ||
                !zone.BaseId.Equals(anchor.BaseId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            QueueDel(zoneUid);
        }
    }

    private void CleanupDisabledAiBase()
    {
        var anchorQuery = EntityQueryEnumerator<LuaMAiBaseAnchorComponent>();
        while (anchorQuery.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        var zoneQuery = EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
        while (zoneQuery.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        var dropQuery = EntityQueryEnumerator<LuaMAiSupplyDropComponent>();
        while (dropQuery.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        var taskQuery = EntityQueryEnumerator<LuaMAiDroneTaskComponent>();
        while (taskQuery.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                RemCompDeferred<LuaMAiDroneTaskComponent>(uid);
        }
    }

    private void EnsureZonesForAnchors(IReadOnlyList<EntityUid> anchors)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(ZoneMarkerPrototype))
            return;

        foreach (var anchorUid in anchors)
        {
            if (TerminatingOrDeleted(anchorUid) || !TryComp<LuaMAiBaseAnchorComponent>(anchorUid, out var anchor))
                continue;

            var anchorCoordinates = _transform.ToMapCoordinates(Transform(anchorUid).Coordinates, logError: false);
            if (anchorCoordinates == MapCoordinates.Nullspace)
                continue;

            foreach (var zone in RequiredZones)
            {
                if (TryFindZone(anchor.BaseId, zone.ZoneType, anchorCoordinates.MapId, out _))
                    continue;

                if (!_physical.CanSpawn(LuaMAiPhysicalEntityKind.Zone, anchorCoordinates.MapId, out _))
                    continue;

                var coordinates = new MapCoordinates(anchorCoordinates.Position + zone.Offset, anchorCoordinates.MapId);
                var zoneUid = Spawn(ZoneMarkerPrototype, coordinates);
                var zoneComponent = EnsureComp<LuaMAiBaseZoneComponent>(zoneUid);
                zoneComponent.BaseId = anchor.BaseId;
                zoneComponent.ZoneType = zone.ZoneType;
                zoneComponent.Label = zone.Label;
                zoneComponent.HomeCoordinates = coordinates;

                _metaData.SetEntityName(zoneUid, $"{zone.Label} [{anchor.BaseId}]");
            }
        }
    }

    private void AssignDroneTasks(IReadOnlyList<EntityUid> drones)
    {
        foreach (var droneUid in drones)
        {
            if (TerminatingOrDeleted(droneUid) || !TryComp<LuaMAiMiningDroneComponent>(droneUid, out var drone))
                continue;

            if (TryComp<LuaMAiDroneTaskComponent>(droneUid, out var existing) &&
                existing.TargetZone.IsValid() &&
                !TerminatingOrDeleted(existing.TargetZone))
            {
                CopyZoneCompensationToTask(existing, existing.TargetZone);
                continue;
            }

            var droneCoordinates = _transform.ToMapCoordinates(Transform(droneUid).Coordinates, logError: false);
            if (droneCoordinates == MapCoordinates.Nullspace)
                continue;

            var zoneType = PickZoneForRole(drone.DroneRole);
            if (!TryFindZone(drone.BaseId, zoneType, droneCoordinates.MapId, out var zoneUid))
                continue;

            var task = EnsureComp<LuaMAiDroneTaskComponent>(droneUid);
            task.BaseId = drone.BaseId;
            task.TaskType = PickTaskForRole(drone.DroneRole);
            task.ZoneType = zoneType;
            task.TaskStage = "assigned";
            task.TargetZone = zoneUid;
            task.ProgressTicks = 0;
            task.IsStuck = false;
            task.StuckChecks = 0;
            task.StuckReport = string.Empty;
            task.LastObservedCoordinates = droneCoordinates;
            task.NextStuckCheck = _timing.CurTime + TimeSpan.FromSeconds(StuckCheckIntervalSeconds);
            task.LastTargetCoordinates = _transform.ToMapCoordinates(Transform(zoneUid).Coordinates, logError: false);
            CopyZoneCompensationToTask(task, zoneUid);
            task.LastReport = string.IsNullOrWhiteSpace(task.Compensation)
                ? $"assigned {task.TaskType} at {zoneType}"
                : $"assigned {task.TaskType} at {zoneType}; compensates {task.WeaknessTitle}";

            if (drone.State is "idle" or "flying" or "launching" or "patrol")
                drone.State = $"assigned_{zoneType}";
        }
    }

    private void UpdateDroneTaskProgress(IReadOnlyList<EntityUid> drones)
    {
        foreach (var droneUid in drones)
        {
            if (TerminatingOrDeleted(droneUid) ||
                !TryComp<LuaMAiMiningDroneComponent>(droneUid, out var drone) ||
                !TryComp<LuaMAiDroneTaskComponent>(droneUid, out var task))
            {
                continue;
            }

            if (!task.TargetZone.IsValid() || TerminatingOrDeleted(task.TargetZone))
            {
                RemCompDeferred<LuaMAiDroneTaskComponent>(droneUid);
                continue;
            }

            var droneCoordinates = _transform.ToMapCoordinates(Transform(droneUid).Coordinates, logError: false);
            var zoneCoordinates = _transform.ToMapCoordinates(Transform(task.TargetZone).Coordinates, logError: false);
            if (droneCoordinates == MapCoordinates.Nullspace ||
                zoneCoordinates == MapCoordinates.Nullspace ||
                droneCoordinates.MapId != zoneCoordinates.MapId)
                continue;

            task.LastTargetCoordinates = zoneCoordinates;
            var distance = Vector2.Distance(droneCoordinates.Position, zoneCoordinates.Position);
            if (distance > ZoneArrivalDistance)
            {
                task.TaskStage = "moving";
                UpdateDroneStuckState(drone, task, droneCoordinates, distance);
                CopyZoneCompensationToTask(task, task.TargetZone);
                if (task.IsStuck)
                {
                    task.TaskStage = "stuck";
                    task.LastReport = task.StuckReport;
                    drone.State = $"stuck_{task.ZoneType}";
                }
                else
                {
                    task.LastReport = string.IsNullOrWhiteSpace(task.Compensation)
                        ? $"moving to {task.ZoneType}"
                        : $"moving to {task.ZoneType}; compensating {task.WeaknessTitle}";
                }

                continue;
            }

            task.TaskStage = "working";
            task.ProgressTicks++;
            ResetDroneStuckState(task, droneCoordinates);
            drone.State = $"working_{task.ZoneType}";

            if (task.ProgressTicks < WorkTicksPerCycle)
                continue;

            task.ProgressTicks = 0;
            task.CompletedCycles++;
            task.TaskStage = "reporting";
            task.LastReport = BuildTaskReport(drone, task);
            if (task.BaseContributionCycles == 0)
            {
                task.LastContributionReport = _stories.RecordAiBaseDroneTaskContribution(
                    "LuaM AI base drone task",
                    drone.DroneId,
                    drone.DroneRole,
                    task.TaskType,
                    task.ZoneType,
                    drone.VesselId,
                    drone.DisplayName,
                    task.CompletedCycles);
                task.BaseContributionCycles++;
                task.LastReport = $"{task.LastReport}; {task.LastContributionReport}";
            }
            else if (!string.IsNullOrWhiteSpace(task.LastContributionReport))
            {
                task.LastReport = $"{task.LastReport}; {task.LastContributionReport}";
            }

            drone.State = $"reporting_{task.ZoneType}";
        }
    }

    private void UpdateZoneCompensationPlans(IReadOnlyList<EntityUid> zones)
    {
        var plan = _stories.BuildAiBaseCompensationPlan();
        foreach (var zoneUid in zones)
        {
            if (TerminatingOrDeleted(zoneUid) || !TryComp<LuaMAiBaseZoneComponent>(zoneUid, out var zone))
                continue;

            var entry = plan
                .Where(item => item.SuggestedZone.Equals(zone.ZoneType, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Severity)
                .ThenBy(item => item.WeaknessId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (entry == null)
            {
                ClearZoneCompensation(zone);
                continue;
            }

            zone.ActiveWeaknessId = entry.WeaknessId;
            zone.ActiveWeaknessTitle = entry.Title;
            zone.ActiveCompensation = entry.Compensation;
            zone.SuggestedRole = entry.SuggestedRole;
            zone.SuggestedResource = entry.Resource;
            zone.ActiveCompensationSeverity = entry.Severity;
        }
    }

    private static bool IsTimeBudgetExhausted(long started)
    {
        return DiagnosticsStopwatch.GetElapsedTime(started).TotalMilliseconds >= UpdateTimeBudgetMilliseconds;
    }

    private static void ClearZoneCompensation(LuaMAiBaseZoneComponent zone)
    {
        zone.ActiveWeaknessId = string.Empty;
        zone.ActiveWeaknessTitle = string.Empty;
        zone.ActiveCompensation = string.Empty;
        zone.SuggestedRole = string.Empty;
        zone.SuggestedResource = string.Empty;
        zone.ActiveCompensationSeverity = 0;
    }

    private void CopyZoneCompensationToTask(LuaMAiDroneTaskComponent task, EntityUid zoneUid)
    {
        if (!TryComp<LuaMAiBaseZoneComponent>(zoneUid, out var zone) ||
            string.IsNullOrWhiteSpace(zone.ActiveWeaknessId))
        {
            task.WeaknessId = string.Empty;
            task.WeaknessTitle = string.Empty;
            task.Compensation = string.Empty;
            task.CompensationRole = string.Empty;
            task.CompensationResource = string.Empty;
            task.CompensationSeverity = 0;
            return;
        }

        task.WeaknessId = zone.ActiveWeaknessId;
        task.WeaknessTitle = zone.ActiveWeaknessTitle;
        task.Compensation = zone.ActiveCompensation;
        task.CompensationRole = zone.SuggestedRole;
        task.CompensationResource = zone.SuggestedResource;
        task.CompensationSeverity = zone.ActiveCompensationSeverity;
    }

    private void UpdateDroneStuckState(
        LuaMAiMiningDroneComponent drone,
        LuaMAiDroneTaskComponent task,
        MapCoordinates droneCoordinates,
        float distanceToZone)
    {
        if (_timing.CurTime < task.NextStuckCheck)
            return;

        task.NextStuckCheck = _timing.CurTime + TimeSpan.FromSeconds(StuckCheckIntervalSeconds);

        if (task.LastObservedCoordinates == MapCoordinates.Nullspace ||
            task.LastObservedCoordinates.MapId != droneCoordinates.MapId)
        {
            ResetDroneStuckState(task, droneCoordinates);
            return;
        }

        var moved = Vector2.Distance(task.LastObservedCoordinates.Position, droneCoordinates.Position);
        task.LastObservedCoordinates = droneCoordinates;
        if (moved > StuckMovementEpsilon)
        {
            task.IsStuck = false;
            task.StuckChecks = 0;
            task.StuckReport = string.Empty;
            return;
        }

        task.StuckChecks++;
        if (task.StuckChecks < StuckChecksBeforeReport)
            return;

        task.IsStuck = true;
        var droneId = string.IsNullOrWhiteSpace(drone.DroneId)
            ? "unmarked-drone"
            : drone.DroneId;
        task.StuckReport = $"{droneId} stuck while moving to {task.ZoneType}; no movement for {task.StuckChecks} checks; distance {distanceToZone:0.0}";
    }

    private static void ResetDroneStuckState(LuaMAiDroneTaskComponent task, MapCoordinates coordinates)
    {
        task.IsStuck = false;
        task.StuckChecks = 0;
        task.StuckReport = string.Empty;
        task.LastObservedCoordinates = coordinates;
    }

    private bool TryFindZone(string baseId, string zoneType, MapId mapId, out EntityUid zoneUid)
    {
        zoneUid = EntityUid.Invalid;
        var query = EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
        while (query.MoveNext(out var uid, out var zone))
        {
            if (TerminatingOrDeleted(uid) ||
                !zone.BaseId.Equals(baseId, StringComparison.OrdinalIgnoreCase) ||
                !zone.ZoneType.Equals(zoneType, StringComparison.OrdinalIgnoreCase))
                continue;

            var coordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
            if (coordinates == MapCoordinates.Nullspace || coordinates.MapId != mapId)
                continue;

            zoneUid = uid;
            return true;
        }

        return false;
    }

    private static string PickZoneForRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "guard" => ZonePatrol,
            "scout" => ZoneContact,
            "logistics" or "hauler" or "trader" => ZoneStorage,
            "repair" or "builder" or "engineer" => ZoneDock,
            "medic" or "medical" or "service" or "janitor" => ZoneContact,
            "miner" or "prospector" => ZoneMining,
            _ => ZoneDock,
        };
    }

    private static string PickTaskForRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "guard" => "patrol_ring",
            "scout" => "route_scan",
            "logistics" or "hauler" or "trader" => "supply_run",
            "repair" or "builder" or "engineer" => "repair_watch",
            "medic" or "medical" => "medical_watch",
            "service" or "janitor" => "service_watch",
            "miner" or "prospector" => "mine_route",
            _ => "dock_wait",
        };
    }

    private static string BuildTaskReport(LuaMAiMiningDroneComponent drone, LuaMAiDroneTaskComponent task)
    {
        var droneId = string.IsNullOrWhiteSpace(drone.DroneId)
            ? "unmarked-drone"
            : drone.DroneId;

        return string.IsNullOrWhiteSpace(task.Compensation)
            ? $"{droneId} completed {task.TaskType} cycle {task.CompletedCycles} at {task.ZoneType}"
            : $"{droneId} completed {task.TaskType} cycle {task.CompletedCycles} at {task.ZoneType}; compensation {task.WeaknessTitle}";
    }
}
