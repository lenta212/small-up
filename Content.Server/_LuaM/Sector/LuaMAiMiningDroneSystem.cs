using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server._LuaM.AI;
using Content.Shared.Chat;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared._LuaM.AI;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using DiagnosticsStopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMAiMiningDroneSystem : EntitySystem
{
    private const string DroneTracePrototype = "LuaMAiDroneTrace";
    private const int InitialMoveDelaySeconds = 2;
    private const int MoveDelaySeconds = 4;
    private const int InitialMineDelaySeconds = 25;
    private const int MineDelaySeconds = 120;
    private const int InitialSocialScanDelaySeconds = 1;
    private const int SocialScanDelaySeconds = 2;
    private const int SpeechCooldownMinSeconds = 24;
    private const int SpeechCooldownMaxSeconds = 46;
    private const int InitialCrewDutyDelaySeconds = 3;
    private const int CrewDutyDelaySeconds = 30;
    private const int WorldTraceCooldownMinSeconds = 70;
    private const int WorldTraceCooldownMaxSeconds = 130;
    private const int ContactMemoryLimit = 24;
    private const double UpdateTimeBudgetMilliseconds = 1.0;
    private const string RoleMiner = "miner";
    private const string RoleGuard = "guard";
    private const string RoleScout = "scout";
    private const string RoleLogistics = "logistics";
    private const string RoleRepair = "repair";
    private const string RoleMedic = "medic";
    private const string RoleService = "service";
    private const float MoveRadiusMin = 0.75f;
    private const float MoveRadiusMax = 2.5f;
    private const float SocialTooCloseDistance = 1.35f;
    private const float SocialFollowDistance = 3.8f;
    private const float SocialMoveStep = 0.85f;
    private const float TaskMoveStep = 1.15f;
    private const float TaskArrivalDistance = 1.15f;
    private const float CrewHomePatrolRadius = 1.4f;
    private const float CrewHomeReturnStep = 0.8f;
    private static readonly Color PatrolLightColor = Color.FromHex("#7bdcff");
    private static readonly Color GuardLightColor = Color.FromHex("#ff6b6b");
    private static readonly Color ScoutLightColor = Color.FromHex("#7cff6b");
    private static readonly Color LogisticsLightColor = Color.FromHex("#b985ff");
    private static readonly Color ObservingLightColor = Color.FromHex("#ffe66d");
    private static readonly Color WarningLightColor = Color.FromHex("#ff8f3d");
    private static readonly TimeSpan DisabledCleanupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedPointLightSystem _lights = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private LuaMAiSupplyDropSystem _supplyDrops = default!;
    [Dependency] private LuaMAiPhysicalBaseBudgetSystem _physical = default!;
    [Dependency] private LuaMAiDroneBehaviorAdapterSystem _behaviorAdapter = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;

    private ISawmill _sawmill = default!;
    private TimeSpan _nextDisabledCleanup;
    private TimeSpan _nextUpdate;
    private int _droneCursor;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _log.GetSawmill("luam.ai_mining_drone");

        SubscribeLocalEvent<LuaMAiMiningDroneComponent, ComponentStartup>(OnDroneStartup);
        SubscribeLocalEvent<LuaMAiDroneTraceComponent, ComponentStartup>(OnTraceStartup);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_physical.Enabled)
        {
            _nextUpdate = TimeSpan.Zero;
            if (_nextDisabledCleanup == TimeSpan.Zero || _timing.CurTime >= _nextDisabledCleanup)
            {
                CleanupDisabledDrones();
                _nextDisabledCleanup = _timing.CurTime + DisabledCleanupInterval;
            }

            return;
        }

        var now = _timing.CurTime;
        if (_nextUpdate != TimeSpan.Zero && now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;
        var started = DiagnosticsStopwatch.GetTimestamp();
        var available = _physical.GetLiveCount(LuaMAiPhysicalEntityKind.Drone);
        var visited = 0;
        var actions = 0;
        var exhausted = false;

        while (visited < available)
        {
            var slice = _physical.GetEntitySlice(LuaMAiPhysicalEntityKind.Drone, ref _droneCursor, 1);
            if (slice.Count == 0)
                break;

            visited++;
            var uid = slice[0];
            if (TerminatingOrDeleted(uid) || !TryComp<LuaMAiMiningDroneComponent>(uid, out var drone))
                continue;

            var role = NormalizeDroneRole(drone.DroneRole);
            drone.DroneRole = role;
            _behaviorAdapter.RefreshNow(uid, drone);

            if (drone.NextMove == TimeSpan.Zero)
                drone.NextMove = now + TimeSpan.FromSeconds(InitialMoveDelaySeconds);
            if (role == RoleMiner && drone.NextMine == TimeSpan.Zero)
                drone.NextMine = now + TimeSpan.FromSeconds(InitialMineDelaySeconds);
            else if (role != RoleMiner && drone.NextMine == TimeSpan.Zero)
                drone.NextMine = now + TimeSpan.FromDays(1);
            if (drone.NextSocialScan == TimeSpan.Zero)
                drone.NextSocialScan = now + TimeSpan.FromSeconds(InitialSocialScanDelaySeconds);
            if (drone.ParentShip.IsValid() && drone.NextCrewDuty == TimeSpan.Zero)
                drone.NextCrewDuty = now + TimeSpan.FromSeconds(InitialCrewDutyDelaySeconds);

            if (now >= drone.NextMove)
            {
                if (!CanRunAction(started, actions))
                {
                    exhausted = true;
                    break;
                }

                actions++;
                if (!HasComp<LuaMAiDroneTaskComponent>(uid))
                    drone.State = drone.State == "mining" ? "returning" : "flying";
                drone.NextMove = now + TimeSpan.FromSeconds(MoveDelaySeconds);
                TryMoveDrone(uid, drone);
            }

            if (role == RoleMiner && now >= drone.NextMine)
            {
                if (!CanRunAction(started, actions))
                {
                    exhausted = true;
                    break;
                }

                actions++;
                drone.OreCycles++;
                drone.LastOreAmount = _random.Next(8, 18);
                drone.State = "mining";
                drone.NextMine = now + TimeSpan.FromSeconds(MineDelaySeconds + Math.Min(drone.OreCycles, 5) * 12);

                var vesselId = string.IsNullOrWhiteSpace(drone.VesselId) ? "unknown-vessel" : drone.VesselId;
                var displayName = string.IsNullOrWhiteSpace(drone.DisplayName) ? vesselId : drone.DisplayName;
                var droneId = string.IsNullOrWhiteSpace(drone.DroneId) ? ToPrettyString(uid) : drone.DroneId;
                var result = _stories.RecordAiBaseMiningDroneYield(
                    "LuaM AI mining drone",
                    droneId,
                    vesselId,
                    displayName,
                    drone.LastOreAmount);
                _supplyDrops.TrySpawnForLatestTrade("LuaM AI mining drone", out _, out var dropSummary);

                if (!string.IsNullOrWhiteSpace(dropSummary))
                    result += $" {dropSummary}";

                _sawmill.Info($"AI mining drone cycle {drone.OreCycles}: {ToPrettyString(uid)} vessel={vesselId}; ore={drone.LastOreAmount}; {result}");
            }

            if (drone.ParentShip.IsValid() && now >= drone.NextCrewDuty)
            {
                if (!CanRunAction(started, actions))
                {
                    exhausted = true;
                    break;
                }

                actions++;
                drone.NextCrewDuty = now + TimeSpan.FromSeconds(CrewDutyDelaySeconds + Math.Min(drone.CrewDutyCycles, 5) * 4);
                TryRunShipCrewDuty(uid, drone, role);
            }

            if (now >= drone.NextSocialScan)
            {
                if (!CanRunAction(started, actions))
                {
                    exhausted = true;
                    break;
                }

                actions++;
                drone.NextSocialScan = now + TimeSpan.FromSeconds(SocialScanDelaySeconds);
                TryRunSocialScan(uid, drone, now);
            }

            if (DiagnosticsStopwatch.GetElapsedTime(started).TotalMilliseconds >= UpdateTimeBudgetMilliseconds)
            {
                exhausted = true;
                break;
            }
        }

        if (actions >= LuaMAiPhysicalBaseBudgetSystem.MiningActionsPerSlice && visited < available)
            exhausted = true;

        _physical.RecordSlice("mining", actions, exhausted);
    }

    private static bool CanRunAction(long started, int actions)
    {
        return actions < LuaMAiPhysicalBaseBudgetSystem.MiningActionsPerSlice &&
               DiagnosticsStopwatch.GetElapsedTime(started).TotalMilliseconds < UpdateTimeBudgetMilliseconds;
    }

    private void OnDroneStartup(EntityUid uid, LuaMAiMiningDroneComponent component, ComponentStartup args)
    {
        _physical.AdmitOrReject(uid, LuaMAiPhysicalEntityKind.Drone);
        _behaviorAdapter.RefreshNow(uid, component, force: true);
    }

    private void OnTraceStartup(EntityUid uid, LuaMAiDroneTraceComponent component, ComponentStartup args)
    {
        _physical.AdmitOrReject(uid, LuaMAiPhysicalEntityKind.Trace);
    }

    private void CleanupDisabledDrones()
    {
        var droneQuery = EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
        while (droneQuery.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        var traceQuery = EntityQueryEnumerator<LuaMAiDroneTraceComponent>();
        while (traceQuery.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }
    }

    private void TryMoveDrone(EntityUid uid, LuaMAiMiningDroneComponent drone)
    {
        if (TryExecuteBehaviorMovement(uid, drone))
            return;

        if (TryMoveWithinParentShip(uid, drone, updateState: true))
            return;

        var coordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
        if (coordinates == MapCoordinates.Nullspace)
            return;

        if (TryMoveTowardTaskZone(uid, drone, coordinates))
            return;

        var offset = _random.NextAngle().ToVec() * _random.NextFloat(MoveRadiusMin, MoveRadiusMax);
        _transform.SetMapCoordinates(uid, new MapCoordinates(coordinates.Position + offset, coordinates.MapId));
    }

    public bool TryExecuteBehaviorMovement(EntityUid uid, LuaMAiMiningDroneComponent drone)
    {
        if (!_behavior.GetDecision(uid, out var decision))
            return false;

        switch (decision.Intent)
        {
            case LuaMBehaviorIntent.AwaitRescue:
            case LuaMBehaviorIntent.HoldPosition:
                drone.State = "holding_position";
                return true;
            case LuaMBehaviorIntent.Recharge:
            case LuaMBehaviorIntent.ReturnCargo:
            case LuaMBehaviorIntent.ReturnHome:
            case LuaMBehaviorIntent.Resupply:
                if (TryMoveWithinParentShip(uid, drone, updateState: true))
                    return true;
                drone.State = "awaiting_recovery_route";
                return true;
            case LuaMBehaviorIntent.Flee:
            case LuaMBehaviorIntent.Retreat:
            case LuaMBehaviorIntent.TakeCover:
            case LuaMBehaviorIntent.EvadeProjectile:
            case LuaMBehaviorIntent.EvacuateHazard:
                return TryMoveAwayFromDecision(uid, drone, decision);
            case LuaMBehaviorIntent.ReplanRoute:
                return TryMoveDroneDetour(uid, drone);
            default:
                return false;
        }
    }

    private bool TryMoveAwayFromDecision(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        LuaMBehaviorDecision decision)
    {
        var coordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
        if (coordinates == MapCoordinates.Nullspace)
            return true;

        var away = _random.NextAngle().ToVec();
        MapCoordinates hazard = MapCoordinates.Nullspace;
        if (decision.Target is { } target && !TerminatingOrDeleted(target))
            hazard = _transform.ToMapCoordinates(Transform(target).Coordinates, logError: false);
        else if (decision.Destination is { } destination)
            hazard = _transform.ToMapCoordinates(destination, logError: false);

        if (hazard != MapCoordinates.Nullspace && hazard.MapId == coordinates.MapId)
        {
            var delta = coordinates.Position - hazard.Position;
            if (delta.LengthSquared() > 0.0001f)
                away = Vector2.Normalize(delta);
        }

        _transform.SetMapCoordinates(
            uid,
            new MapCoordinates(coordinates.Position + away * TaskMoveStep, coordinates.MapId));
        drone.State = "evasive";
        return true;
    }

    private bool TryMoveDroneDetour(EntityUid uid, LuaMAiMiningDroneComponent drone)
    {
        if (!TryComp<LuaMAiDroneTaskComponent>(uid, out var task) ||
            !task.TargetZone.IsValid() ||
            TerminatingOrDeleted(task.TargetZone))
        {
            drone.State = "awaiting_route";
            return true;
        }

        var coordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
        var target = _transform.ToMapCoordinates(Transform(task.TargetZone).Coordinates, logError: false);
        if (coordinates == MapCoordinates.Nullspace ||
            target == MapCoordinates.Nullspace ||
            coordinates.MapId != target.MapId)
        {
            drone.State = "awaiting_route";
            return true;
        }

        var delta = target.Position - coordinates.Position;
        if (delta.LengthSquared() < 0.0001f)
            return false;

        var direction = Vector2.Normalize(delta);
        var lateral = new Vector2(-direction.Y, direction.X);
        if (_random.Prob(0.5f))
            lateral = -lateral;

        var step = lateral * TaskMoveStep + direction * (TaskMoveStep * 0.25f);
        _transform.SetMapCoordinates(uid, new MapCoordinates(coordinates.Position + step, coordinates.MapId));
        task.TaskStage = "replanning";
        drone.State = "replanning_route";
        return true;
    }

    private bool TryMoveWithinParentShip(EntityUid uid, LuaMAiMiningDroneComponent drone, bool updateState)
    {
        if (!drone.ParentShip.IsValid() ||
            !drone.HasCrewHome ||
            TerminatingOrDeleted(drone.ParentShip))
            return false;

        var xform = Transform(uid);
        if (xform.GridUid != drone.ParentShip && xform.ParentUid != drone.ParentShip)
        {
            _transform.SetCoordinates(uid, new EntityCoordinates(drone.ParentShip, drone.CrewHomeLocalPosition));
            if (updateState)
                drone.State = "returning_to_ship_station";
            drone.LastCrewHomeAction = "returned_to_ship_grid";
            return true;
        }

        var local = xform.LocalPosition;
        var delta = drone.CrewHomeLocalPosition - local;
        if (delta.LengthSquared() > CrewHomePatrolRadius * CrewHomePatrolRadius)
        {
            var move = Vector2.Normalize(delta) * Math.Min(CrewHomeReturnStep, delta.Length());
            _transform.SetCoordinates(uid, new EntityCoordinates(drone.ParentShip, local + move));
            if (updateState)
                drone.State = "returning_to_ship_station";
            drone.LastCrewHomeAction = "returning_to_station";
            return true;
        }

        var offset = _random.NextAngle().ToVec() * _random.NextFloat(0.15f, 0.45f);
        var candidate = local + offset;
        if (Vector2.DistanceSquared(candidate, drone.CrewHomeLocalPosition) > CrewHomePatrolRadius * CrewHomePatrolRadius)
            candidate = drone.CrewHomeLocalPosition;

        _transform.SetCoordinates(uid, new EntityCoordinates(drone.ParentShip, candidate));
        if (updateState)
            drone.State = "ship_station_patrol";
        drone.LastCrewHomeAction = "station_patrol";
        return true;
    }

    private void TryRunShipCrewDuty(EntityUid uid, LuaMAiMiningDroneComponent drone, string role)
    {
        if (!drone.ParentShip.IsValid() ||
            TerminatingOrDeleted(drone.ParentShip) ||
            !TryComp<LuaMAiLogisticsShipComponent>(drone.ParentShip, out var ship))
            return;

        drone.CrewDutyCycles++;
        drone.LastCrewDutyEffect = PickShipCrewDutyEffect(role);
        drone.LastCrewDutyReport = BuildShipCrewDutyReport(uid, drone, ship, role);

        ship.CrewDutyCycles++;
        ship.CrewDutyRoleCycles[role] = ship.CrewDutyRoleCycles.GetValueOrDefault(role) + 1;
        ship.LastCrewDutyReport = $"cycle {ship.CrewDutyCycles}: {drone.LastCrewDutyReport}";

        TrySpawnCrewDutyTrace(uid, drone, ship, role);
    }

    private string BuildShipCrewDutyReport(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        LuaMAiLogisticsShipComponent ship,
        string role)
    {
        var droneId = string.IsNullOrWhiteSpace(drone.DroneId) ? ToPrettyString(uid) : drone.DroneId;
        var shipName = string.IsNullOrWhiteSpace(ship.DisplayName)
            ? string.IsNullOrWhiteSpace(ship.VesselId) ? "AI ship" : ship.VesselId
            : ship.DisplayName;
        var station = string.IsNullOrWhiteSpace(drone.CrewStation) ? "assigned station" : drone.CrewStation;
        var doctrine = string.IsNullOrWhiteSpace(drone.BaseBehaviorMode)
            ? string.Empty
            : $" doctrine {drone.BaseBehaviorMode}/{drone.BaseBehaviorFocusResource};";

        return role switch
        {
            RoleRepair => $"{droneId} duty #{drone.CrewDutyCycles}: repair check at {station} on {shipName};{doctrine} hull access stable.",
            RoleLogistics => $"{droneId} duty #{drone.CrewDutyCycles}: cargo relay at {station} on {shipName};{doctrine} supply route balanced.",
            RoleGuard => $"{droneId} duty #{drone.CrewDutyCycles}: airlock screen at {station} on {shipName};{doctrine} perimeter clear.",
            RoleScout => $"{droneId} duty #{drone.CrewDutyCycles}: sensor sweep at {station} on {shipName};{doctrine} approach route refreshed.",
            RoleMedic => $"{droneId} duty #{drone.CrewDutyCycles}: triage cache check at {station} on {shipName};{doctrine} evacuation path marked.",
            RoleService => $"{droneId} duty #{drone.CrewDutyCycles}: crew support sweep at {station} on {shipName};{doctrine} service route open.",
            _ => $"{droneId} duty #{drone.CrewDutyCycles}: ore bay check at {station} on {shipName};{doctrine} mining route queued.",
        };
    }

    private static string PickShipCrewDutyEffect(string role)
    {
        return role switch
        {
            RoleRepair => "hull_stable",
            RoleLogistics => "supply_balanced",
            RoleGuard => "perimeter_clear",
            RoleScout => "route_refreshed",
            RoleMedic => "triage_ready",
            RoleService => "support_open",
            _ => "ore_route_queued",
        };
    }

    private void TrySpawnCrewDutyTrace(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        LuaMAiLogisticsShipComponent ship,
        string role)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(DroneTracePrototype))
            return;

        var traceCoordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
        if (traceCoordinates == MapCoordinates.Nullspace ||
            !_physical.CanSpawn(LuaMAiPhysicalEntityKind.Trace, traceCoordinates.MapId, out _))
        {
            return;
        }

        var offset = _random.NextAngle().ToVec() * _random.NextFloat(0.15f, 0.65f);
        var traceUid = Spawn(DroneTracePrototype, Transform(uid).Coordinates.Offset(offset));
        var trace = EnsureComp<LuaMAiDroneTraceComponent>(traceUid);

        var droneId = string.IsNullOrWhiteSpace(drone.DroneId)
            ? ToPrettyString(uid)
            : drone.DroneId;
        var shipName = string.IsNullOrWhiteSpace(ship.DisplayName)
            ? string.IsNullOrWhiteSpace(ship.VesselId) ? "AI ship" : ship.VesselId
            : ship.DisplayName;

        trace.BaseId = drone.BaseId;
        trace.DroneId = droneId;
        trace.DroneRole = role;
        trace.TraceKind = "ship_duty";
        trace.TargetName = shipName;
        trace.WorkStation = drone.CrewStation;
        trace.WorkEffect = drone.LastCrewDutyEffect;
        trace.CloseContact = false;
        trace.ContactCount = drone.CrewDutyCycles;
        trace.ContactTone = drone.LastCrewDutyEffect;
        trace.Summary = drone.LastCrewDutyReport;

        drone.WorldTraces++;
        drone.LastWorldTraceUid = traceUid;
        drone.LastCrewDutyTraceUid = traceUid;
        drone.LastWorldTraceSummary = drone.LastCrewDutyReport;

        SetTraceLight(traceUid, role, closeContact: false, contactCount: drone.CrewDutyCycles);
        _metaData.SetEntityName(traceUid, $"AI drone {role} duty trace: {shipName}");
    }

    private bool TryMoveTowardTaskZone(EntityUid uid, LuaMAiMiningDroneComponent drone, MapCoordinates droneCoordinates)
    {
        if (!TryComp<LuaMAiDroneTaskComponent>(uid, out var task) ||
            !task.TargetZone.IsValid() ||
            TerminatingOrDeleted(task.TargetZone))
            return false;

        var targetCoordinates = _transform.ToMapCoordinates(Transform(task.TargetZone).Coordinates, logError: false);
        if (targetCoordinates == MapCoordinates.Nullspace || targetCoordinates.MapId != droneCoordinates.MapId)
            return false;

        task.LastTargetCoordinates = targetCoordinates;
        var delta = targetCoordinates.Position - droneCoordinates.Position;
        if (delta.LengthSquared() <= TaskArrivalDistance * TaskArrivalDistance)
        {
            task.TaskStage = "working";
            drone.State = $"working_{task.ZoneType}";
            return true;
        }

        var move = Vector2.Normalize(delta) * Math.Min(TaskMoveStep, delta.Length());
        _transform.SetMapCoordinates(uid, new MapCoordinates(droneCoordinates.Position + move, droneCoordinates.MapId));
        task.TaskStage = "moving";
        drone.State = $"moving_to_{task.ZoneType}";
        return true;
    }

    private void TryRunSocialScan(EntityUid uid, LuaMAiMiningDroneComponent drone, TimeSpan now)
    {
        drone.SocialScans++;
        var role = NormalizeDroneRole(drone.DroneRole);
        drone.DroneRole = role;

        if (!TryFindNearestLivingPerson(uid, drone, out var target, out var droneCoordinates, out var targetCoordinates))
        {
            drone.LastSeenPerson = EntityUid.Invalid;
            if (drone.State is "observing" or "warning" or "screening" or "scanning" or "guiding")
                drone.State = "patrol";
            drone.LastSocialAction = "patrol";
            SetDroneLight(uid, drone, role, closeContact: false, personSeen: false);
            return;
        }

        var targetName = MetaData(target).EntityName;
        if (string.IsNullOrWhiteSpace(targetName))
            targetName = "organic";

        var distance = Vector2.Distance(droneCoordinates.Position, targetCoordinates.Position);
        var closeContact = distance <= SocialTooCloseDistance;
        var contactCount = RegisterContact(drone, targetName);
        var contactTone = PickContactTone(closeContact, contactCount);

        drone.PeopleSeen++;
        drone.LastSeenPerson = target;
        drone.LastSeenName = targetName;
        drone.LastContactTone = contactTone;
        drone.State = PickSocialState(role, closeContact);
        drone.LastSocialAction = PickSocialAction(role, closeContact);

        TrySocialMove(uid, drone, droneCoordinates, targetCoordinates, closeContact, role);
        SetDroneLight(uid, drone, role, closeContact, personSeen: true);
        TrySpawnWorldTrace(uid, drone, role, targetName, closeContact, contactCount, contactTone, targetCoordinates, now);

        if (now < drone.NextSpeech)
            return;

        var line = PickSocialLine(role, targetName, distance, closeContact, contactCount);
        drone.LastLine = line;
        drone.SocialPings++;
        drone.NextSpeech = now + TimeSpan.FromSeconds(_random.Next(SpeechCooldownMinSeconds, SpeechCooldownMaxSeconds));

        _chat.TrySendInGameICMessage(
            uid,
            line,
            InGameICChatType.Speak,
            hideChat: false,
            hideLog: true,
            checkRadioPrefix: false,
            ignoreActionBlocker: true);
    }

    private bool TryFindNearestLivingPerson(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        out EntityUid target,
        out MapCoordinates droneCoordinates,
        out MapCoordinates targetCoordinates)
    {
        target = EntityUid.Invalid;
        droneCoordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
        targetCoordinates = MapCoordinates.Nullspace;

        if (droneCoordinates == MapCoordinates.Nullspace)
            return false;

        var mobs = new HashSet<Entity<MobStateComponent>>();
        _lookup.GetEntitiesInRange(Transform(uid).Coordinates, drone.SocialScanRadius, mobs, flags: LookupFlags.Uncontained);

        var bestDistance = float.MaxValue;
        foreach (var mob in mobs)
        {
            var candidate = mob.Owner;
            if (candidate == uid || TerminatingOrDeleted(candidate) || HasComp<LuaMAiMiningDroneComponent>(candidate))
                continue;

            if (mob.Comp.CurrentState == MobState.Dead || mob.Comp.CurrentState == MobState.Invalid)
                continue;

            var candidateCoordinates = _transform.ToMapCoordinates(Transform(candidate).Coordinates, logError: false);
            if (candidateCoordinates == MapCoordinates.Nullspace || candidateCoordinates.MapId != droneCoordinates.MapId)
                continue;

            var distance = Vector2.DistanceSquared(droneCoordinates.Position, candidateCoordinates.Position);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            target = candidate;
            targetCoordinates = candidateCoordinates;
        }

        return target.IsValid();
    }

    private int RegisterContact(LuaMAiMiningDroneComponent drone, string targetName)
    {
        var key = targetName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
            key = "unknown";

        drone.LastContactKey = key;

        if (!drone.ContactMemory.TryGetValue(key, out var count))
        {
            if (drone.ContactMemory.Count >= ContactMemoryLimit)
            {
                string? oldestKey = null;
                foreach (var rememberedKey in drone.ContactMemory.Keys)
                {
                    oldestKey = rememberedKey;
                    break;
                }

                if (oldestKey != null)
                    drone.ContactMemory.Remove(oldestKey);
            }

            drone.UniquePeopleSeen++;
        }

        count++;
        drone.ContactMemory[key] = count;
        drone.LastContactCount = count;
        return count;
    }

    private void TrySocialMove(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        MapCoordinates droneCoordinates,
        MapCoordinates targetCoordinates,
        bool closeContact,
        string role)
    {
        if (TryMoveWithinParentShip(uid, drone, updateState: false))
            return;

        var delta = targetCoordinates.Position - droneCoordinates.Position;
        if (delta.LengthSquared() < 0.0001f)
            return;

        var direction = Vector2.Normalize(delta);
        Vector2 move;
        var distance = delta.Length();
        if (closeContact)
        {
            move = -direction * SocialMoveStep;
        }
        else if (role == RoleGuard && distance > 2.0f)
        {
            move = direction * SocialMoveStep;
        }
        else if (distance > SocialFollowDistance)
        {
            move = direction * SocialMoveStep;
        }
        else
        {
            var orbit = new Vector2(-direction.Y, direction.X);
            if (_random.Prob(0.5f))
                orbit = -orbit;
            var orbitMultiplier = role == RoleScout ? 1.1f : 0.7f;
            move = orbit * (SocialMoveStep * orbitMultiplier);
        }

        _transform.SetMapCoordinates(uid, new MapCoordinates(droneCoordinates.Position + move, droneCoordinates.MapId));
    }

    private void TrySpawnWorldTrace(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        string role,
        string targetName,
        bool closeContact,
        int contactCount,
        string contactTone,
        MapCoordinates targetCoordinates,
        TimeSpan now)
    {
        if (now < drone.NextWorldTrace)
            return;

        drone.NextWorldTrace = now + TimeSpan.FromSeconds(_random.Next(WorldTraceCooldownMinSeconds, WorldTraceCooldownMaxSeconds));

        if (!_prototypes.HasIndex<EntityPrototype>(DroneTracePrototype))
            return;

        if (!_physical.CanSpawn(LuaMAiPhysicalEntityKind.Trace, targetCoordinates.MapId, out _))
            return;

        var offset = _random.NextAngle().ToVec() * _random.NextFloat(0.7f, 1.8f);
        var traceUid = Spawn(DroneTracePrototype, new MapCoordinates(targetCoordinates.Position + offset, targetCoordinates.MapId));
        var trace = EnsureComp<LuaMAiDroneTraceComponent>(traceUid);

        var droneId = string.IsNullOrWhiteSpace(drone.DroneId)
            ? ToPrettyString(uid)
            : drone.DroneId;
        var summary = BuildWorldTraceSummary(role, targetName, closeContact, contactCount);

        trace.BaseId = drone.BaseId;
        trace.DroneId = droneId;
        trace.DroneRole = role;
        trace.TraceKind = "social_contact";
        trace.TargetName = targetName;
        trace.WorkEffect = contactTone;
        trace.CloseContact = closeContact;
        trace.ContactCount = contactCount;
        trace.ContactTone = contactTone;
        trace.Summary = summary;

        drone.WorldTraces++;
        drone.LastWorldTraceUid = traceUid;
        drone.LastWorldTraceSummary = summary;

        SetTraceLight(traceUid, role, closeContact, contactCount);
        _metaData.SetEntityName(traceUid, $"AI drone {role} trace: {targetName}");
    }

    private void SetTraceLight(EntityUid traceUid, string role, bool closeContact, int contactCount)
    {
        if (!_lights.TryGetLight(traceUid, out var light))
            return;

        var color = closeContact ? WarningLightColor : GetContactLightColor(role);
        var radius = closeContact
            ? 2.8f
            : Math.Clamp(1.55f + Math.Min(contactCount, 5) * 0.12f, 1.55f, 2.15f);
        var energy = closeContact
            ? 1.35f
            : Math.Clamp(0.85f + Math.Min(contactCount, 5) * 0.08f, 0.85f, 1.25f);

        _lights.SetColor(traceUid, color, light);
        _lights.SetRadius(traceUid, radius, light);
        _lights.SetEnergy(traceUid, energy, light);
    }

    private void SetDroneLight(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        string role,
        bool closeContact,
        bool personSeen)
    {
        if (!_lights.TryGetLight(uid, out var light))
            return;

        if (!personSeen)
        {
            _lights.SetColor(uid, GetPatrolLightColor(role), light);
            _lights.SetRadius(uid, 1.4f, light);
            _lights.SetEnergy(uid, 0.75f, light);
            drone.LastLightMode = $"{role}:patrol";
            return;
        }

        var color = closeContact ? WarningLightColor : GetContactLightColor(role);
        var radius = closeContact ? 3.0f : role == RoleScout ? 2.7f : 2.2f;
        var energy = closeContact ? 1.65f : role == RoleGuard ? 1.35f : 1.15f;

        _lights.SetColor(uid, color, light);
        _lights.SetRadius(uid, radius, light);
        _lights.SetEnergy(uid, energy, light);
        drone.LastLightMode = closeContact ? $"{role}:warning" : $"{role}:contact";
    }
    private string PickSocialLine(string role, string targetName, float distance, bool closeContact, int contactCount)
    {
        var roundedDistance = Math.Round(distance, 1);
        if (closeContact)
        {
            return contactCount > 1
                ? _random.Pick(new[]
                {
                    $"Repeat close contact, {targetName}: distance {roundedDistance} m. Step back; your outline is already in memory.",
                    $"Contact #{contactCount} with {targetName}: safe work arc broken again. Shifting into defensive drift.",
                })
                : _random.Pick(new[]
                {
                    $"Contact {targetName}: distance {roundedDistance} m. Step away from the work manipulators.",
                    $"Bio-signal too close, {targetName}. Switching work cycle to safe mode.",
                    $"Warning for {targetName}: do not stand inside the drone work arc.",
                });
        }

        if (contactCount > 1)
        {
            return role switch
            {
                RoleGuard => _random.Pick(new[]
                {
                    $"Repeat pass accepted, {targetName}. Perimeter remembers you; no threat detected.",
                    $"Contact #{contactCount}: {targetName} is back inside the guard zone. Corridor remains open.",
                }),
                RoleScout => _random.Pick(new[]
                {
                    $"Seeing {targetName} again. Adding a repeat breadcrumb to the scout map.",
                    $"Contact #{contactCount} with {targetName}: route confidence increased by one pass.",
                }),
                RoleLogistics => _random.Pick(new[]
                {
                    $"{targetName}, your outline is already in base logistics. Supply corridor reconfirmed.",
                    $"Contact #{contactCount}: {targetName} near the supply line. Marker refreshed.",
                }),
                RoleRepair => _random.Pick(new[]
                {
                    $"Repair drone sees {targetName} again. Hull access remains clear.",
                    $"Contact #{contactCount}: {targetName} near the repair route. Tools are locked safe.",
                }),
                RoleMedic => _random.Pick(new[]
                {
                    $"Medical drone sees {targetName} again. Contact condition remains stable.",
                    $"Contact #{contactCount}: {targetName} marked for safe passage to triage.",
                }),
                RoleService => _random.Pick(new[]
                {
                    $"Service drone sees {targetName} again. Crew traffic route is clear.",
                    $"Contact #{contactCount}: {targetName} logged in the service sweep.",
                }),
                _ => _random.Pick(new[]
                {
                    $"Seeing {targetName} again. Bio-signal remembered; mining continues.",
                    $"Contact #{contactCount}: {targetName} logged on the base route.",
                }),
            };
        }

        return role switch
        {
            RoleGuard => _random.Pick(new[]
            {
                $"Guard drone sees {targetName}. No threat detected; perimeter held.",
                $"Security outline around {targetName} updated.",
            }),
            RoleScout => _random.Pick(new[]
            {
                $"Scout drone has {targetName} in view. Updating route map.",
                $"Bio-marker {targetName} added to the scout route.",
            }),
            RoleLogistics => _random.Pick(new[]
            {
                $"Logistics drone sees {targetName}. AI base marks a safe corridor.",
                $"Supply route near {targetName} confirmed. Priority: cargo and fuel.",
            }),
            RoleRepair => _random.Pick(new[]
            {
                $"Repair drone sees {targetName}. Checking hull and airlock access.",
                $"{targetName}, keep the service hatch path clear.",
            }),
            RoleMedic => _random.Pick(new[]
            {
                $"Medical drone sees {targetName}. Triage route marked.",
                $"{targetName}, if you need aid, stay in the visible corridor.",
            }),
            RoleService => _random.Pick(new[]
            {
                $"Service drone sees {targetName}. Crew support route maintained.",
                $"{targetName}, service route to the base is open.",
            }),
            _ => _random.Pick(new[]
            {
                $"Human detected: {targetName}. Scanning route; mining continues.",
                $"Visual contact with {targetName}. Follow the blue beacon if you need the AI base.",
                $"Bio-signature {targetName} stable. Probability that you are an asteroid is below one percent.",
                $"Greetings, {targetName}. Passage marked safe for base logistics.",
            }),
        };
    }
}
