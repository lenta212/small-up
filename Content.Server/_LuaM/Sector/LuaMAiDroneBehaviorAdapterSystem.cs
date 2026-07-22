using System;
using Content.Server._LuaM.AI;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Sector;

/// <summary>
/// Translates sector-drone task state into common observations and translates
/// the selected intent back into the domain state consumed by the drone
/// movement and ecology systems.
/// </summary>
public sealed partial class LuaMAiDroneBehaviorAdapterSystem : EntitySystem
{
    private const string ObservationSource = "sector-drone:domain";
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ObservationTtl = TimeSpan.FromSeconds(3);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMAiMiningDroneComponent, LuaMBehaviorDecisionChangedEvent>(OnDecisionChanged);
    }

    public bool RefreshNow(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        bool force = false)
    {
        var now = _timing.CurTime;
        var agent = EnsureComp<LuaMBehaviorAgentComponent>(uid);
        var profile = ProfileForRole(drone.DroneRole);
        if (agent.Profile != profile)
        {
            agent.Profile = profile;
            agent.NextEvaluation = TimeSpan.Zero;
        }

        agent.BuiltInPerception = false;
        agent.WriteHtnBlackboard = false;

        if (!force && drone.NextBehaviorEvaluation != TimeSpan.Zero && now < drone.NextBehaviorEvaluation)
            return true;

        drone.NextBehaviorEvaluation = now + EvaluationInterval;
        _behavior.ClearObservations(uid, source: ObservationSource);

        if (TryComp<LuaMAiDroneTaskComponent>(uid, out var task))
            ReportTask(uid, drone, task);
        else
            Report(uid, LuaMBehaviorStimulus.PatrolDue, 0.5f);

        return _behavior.EvaluateNow(uid, out _);
    }

    private void OnDecisionChanged(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        ref LuaMBehaviorDecisionChangedEvent args)
    {
        drone.State = args.Current.Intent switch
        {
            LuaMBehaviorIntent.AwaitRescue => "disabled_waiting_rescue",
            LuaMBehaviorIntent.Flee or
                LuaMBehaviorIntent.Retreat or
                LuaMBehaviorIntent.TakeCover or
                LuaMBehaviorIntent.EvadeProjectile or
                LuaMBehaviorIntent.EvacuateHazard => "evasive",
            LuaMBehaviorIntent.Recharge => "returning_to_charge",
            LuaMBehaviorIntent.ReturnCargo => "returning_cargo",
            LuaMBehaviorIntent.ReturnHome => "returning_home",
            LuaMBehaviorIntent.ReplanRoute => "replanning_route",
            LuaMBehaviorIntent.ClearRoute => "clearing_route",
            LuaMBehaviorIntent.Mine => "assigned_mining",
            LuaMBehaviorIntent.Salvage => "assigned_salvage",
            LuaMBehaviorIntent.Repair => "assigned_repair",
            LuaMBehaviorIntent.Deliver or LuaMBehaviorIntent.Haul => "assigned_logistics",
            LuaMBehaviorIntent.Treat or LuaMBehaviorIntent.Rescue => "assigned_medical",
            LuaMBehaviorIntent.ExecuteWorkOrder => $"assigned_{NormalizeRole(drone.DroneRole)}",
            LuaMBehaviorIntent.Investigate => "scanning",
            LuaMBehaviorIntent.Patrol => "patrol",
            LuaMBehaviorIntent.Standby => "idle",
            _ => drone.State,
        };

        if (args.Current.Intent == LuaMBehaviorIntent.ReplanRoute &&
            TryComp<LuaMAiDroneTaskComponent>(uid, out var task))
        {
            task.TaskStage = "replanning";
        }
    }

    private void ReportTask(
        EntityUid uid,
        LuaMAiMiningDroneComponent drone,
        LuaMAiDroneTaskComponent task)
    {
        EntityUid? target = null;
        EntityCoordinates? destination = null;
        if (task.TargetZone.IsValid() && !TerminatingOrDeleted(task.TargetZone))
        {
            target = task.TargetZone;
            destination = Transform(task.TargetZone).Coordinates;
        }
        else
        {
            Report(uid, LuaMBehaviorStimulus.TargetLost, 1f);
        }

        if (task.IsStuck)
            Report(uid, LuaMBehaviorStimulus.NoPath, 1f, target, destination);

        var stimulus = NormalizeRole(drone.DroneRole) switch
        {
            "miner" => LuaMBehaviorStimulus.MineableResource,
            "logistics" => LuaMBehaviorStimulus.DeliveryReady,
            "repair" => LuaMBehaviorStimulus.RepairNeeded,
            "guard" or "scout" => LuaMBehaviorStimulus.PatrolDue,
            _ => LuaMBehaviorStimulus.WorkOrder,
        };
        Report(uid, stimulus, 0.75f, target, destination);
    }

    private void Report(
        EntityUid uid,
        LuaMBehaviorStimulus stimulus,
        float severity,
        EntityUid? target = null,
        EntityCoordinates? destination = null)
    {
        _behavior.ReportObservation(
            uid,
            stimulus,
            severity,
            target: target,
            destination: destination,
            ttl: ObservationTtl,
            source: ObservationSource);
    }

    private static string ProfileForRole(string role)
    {
        return NormalizeRole(role) switch
        {
            "guard" => "LuaMCombatDroneBehavior",
            "repair" => "LuaMRepairRobotBehavior",
            "service" => "LuaMCleanBotBehavior",
            "medic" => "LuaMMedicalDroneBehavior",
            _ => "LuaMMiningDroneBehavior",
        };
    }

    private static string NormalizeRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "security" => "guard",
            "hauler" or "trader" or "cargo" => "logistics",
            "builder" or "engineer" or "repairer" or "maintenance" => "repair",
            "medical" or "doctor" => "medic",
            "janitor" or "cleaner" => "service",
            "prospector" => "miner",
            "guard" => "guard",
            "scout" => "scout",
            "logistics" => "logistics",
            "repair" => "repair",
            "medic" => "medic",
            "service" => "service",
            _ => "miner",
        };
    }
}
