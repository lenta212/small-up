using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

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
    private static readonly TimeSpan DisabledCleanupInterval = TimeSpan.FromSeconds(30);

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

    private TimeSpan _nextDisabledCleanup;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMAiBaseAnchorComponent, ComponentStartup>(OnDisabledAnchorStartup);
        SubscribeLocalEvent<LuaMAiBaseZoneComponent, ComponentStartup>(OnDisabledZoneStartup);
        SubscribeLocalEvent<LuaMAiSupplyDropComponent, ComponentStartup>(OnDisabledSupplyDropStartup);
        SubscribeLocalEvent<LuaMAiDroneTaskComponent, ComponentStartup>(OnDisabledDroneTaskStartup);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!LuaMAiPhysicalBaseFeature.Enabled)
        {
            if (_nextDisabledCleanup == TimeSpan.Zero || _timing.CurTime >= _nextDisabledCleanup)
            {
                CleanupDisabledAiBase();
                _nextDisabledCleanup = _timing.CurTime + DisabledCleanupInterval;
            }

            return;
        }

        EnsureZonesForAnchors();
        UpdateZoneCompensationPlans();
        AssignDroneTasks();
        UpdateDroneTaskProgress();
    }

    private void OnDisabledAnchorStartup(EntityUid uid, LuaMAiBaseAnchorComponent component, ComponentStartup args)
    {
        QueueDisabledPhysicalEntity(uid);
    }

    private void OnDisabledZoneStartup(EntityUid uid, LuaMAiBaseZoneComponent component, ComponentStartup args)
    {
        QueueDisabledPhysicalEntity(uid);
    }

    private void OnDisabledSupplyDropStartup(EntityUid uid, LuaMAiSupplyDropComponent component, ComponentStartup args)
    {
        QueueDisabledPhysicalEntity(uid);
    }

    private void OnDisabledDroneTaskStartup(EntityUid uid, LuaMAiDroneTaskComponent component, ComponentStartup args)
    {
        if (LuaMAiPhysicalBaseFeature.Enabled || TerminatingOrDeleted(uid))
            return;

        RemCompDeferred<LuaMAiDroneTaskComponent>(uid);
    }

    private void QueueDisabledPhysicalEntity(EntityUid uid)
    {
        if (LuaMAiPhysicalBaseFeature.Enabled || TerminatingOrDeleted(uid))
            return;

        QueueDel(uid);
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

    private void EnsureZonesForAnchors()
    {
        if (!_prototypes.HasIndex<EntityPrototype>(ZoneMarkerPrototype))
            return;

        var anchorQuery = EntityQueryEnumerator<LuaMAiBaseAnchorComponent>();
        while (anchorQuery.MoveNext(out var anchorUid, out var anchor))
        {
            if (TerminatingOrDeleted(anchorUid))
                continue;

            var anchorCoordinates = _transform.ToMapCoordinates(Transform(anchorUid).Coordinates, logError: false);
            if (anchorCoordinates == MapCoordinates.Nullspace)
                continue;

            foreach (var zone in RequiredZones)
            {
                if (TryFindZone(anchor.BaseId, zone.ZoneType, anchorCoordinates.MapId, out _))
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

    private void AssignDroneTasks()
    {
        var droneQuery = EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
        while (droneQuery.MoveNext(out var droneUid, out var drone))
        {
            if (TerminatingOrDeleted(droneUid))
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

    private void UpdateDroneTaskProgress()
    {
        var query = EntityQueryEnumerator<LuaMAiMiningDroneComponent, LuaMAiDroneTaskComponent>();
        while (query.MoveNext(out var droneUid, out var drone, out var task))
        {
            if (TerminatingOrDeleted(droneUid) ||
                !task.TargetZone.IsValid() ||
                TerminatingOrDeleted(task.TargetZone))
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

    private void UpdateZoneCompensationPlans()
    {
        var plan = _stories.BuildAiBaseCompensationPlan();
        var query = EntityQueryEnumerator<LuaMAiBaseZoneComponent>();
        while (query.MoveNext(out var zoneUid, out var zone))
        {
            if (TerminatingOrDeleted(zoneUid))
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
