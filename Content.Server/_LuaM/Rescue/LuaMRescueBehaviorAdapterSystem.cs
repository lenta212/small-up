using System;
using Content.Server._LuaM.AI;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

/// <summary>
/// Runtime state shared by the rescue perception and executor adapters. The
/// rescue systems remain authoritative for treatment, movement, and combat.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMRescueBehaviorAdapterComponent : Component
{
    [DataField]
    public TimeSpan NextEvaluation;

    [DataField]
    public LuaMBehaviorIntent Intent = LuaMBehaviorIntent.Standby;

    [DataField]
    public uint DecisionGeneration;

    [DataField]
    public LuaMRescueEscortDuty? EscortDutyOverride;

    [DataField]
    public string LastStatus = "not evaluated";
}

/// <summary>
/// Converts rescue-domain state into common behavior observations, then hands
/// the selected intent back to the existing rescue activity and escort-duty
/// executors.
/// </summary>
public sealed partial class LuaMRescueBehaviorAdapterSystem : EntitySystem
{
    private const string ObservationSource = "rescue:domain";
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(0.75);
    private static readonly TimeSpan ObservationTtl = TimeSpan.FromSeconds(3);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;
    [Dependency] private LuaMRescueTeamSystem _team = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMRescueAgentComponent, LuaMBehaviorDecisionChangedEvent>(OnRescueDecisionChanged);
        SubscribeLocalEvent<LuaMRescueEscortComponent, LuaMBehaviorDecisionChangedEvent>(OnEscortDecisionChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var rescueQuery = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (rescueQuery.MoveNext(out var uid, out var rescue))
        {
            var adapter = EnsureComp<LuaMRescueBehaviorAdapterComponent>(uid);
            if (adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
                continue;

            RefreshNow(uid, rescue, adapter);
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent>();
        while (escortQuery.MoveNext(out var uid, out var escort))
        {
            var adapter = EnsureComp<LuaMRescueBehaviorAdapterComponent>(uid);
            if (adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
                continue;

            RefreshNow(uid, escort, adapter);
        }
    }

    public bool RefreshNow(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        LuaMRescueBehaviorAdapterComponent? adapter = null,
        bool force = false)
    {
        adapter ??= EnsureComp<LuaMRescueBehaviorAdapterComponent>(uid);
        if (!PrepareEvaluation(uid, adapter, "LuaMRescueMedicBehavior", force))
            return true;

        var patient = GetRescuePatient(rescue);
        var routeFailed = ReportRescueRoute(uid, rescue, patient);
        ReportRescueThreat(uid, rescue);

        if (!routeFailed && patient is { } patientUid)
            ReportPatient(uid, patientUid);

        if (rescue.ActivityContext.Activity == LuaMRescueActivity.Resupplying ||
            rescue.TaskStage is LuaMRescueTaskStage.PickingUpSupply or LuaMRescueTaskStage.VendingSupply)
        {
            Report(uid, LuaMBehaviorStimulus.ResourceLow, 0.8f, rescue.TaskSupplyTarget);
        }

        return _behavior.EvaluateNow(uid, out _);
    }

    public bool RefreshNow(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueBehaviorAdapterComponent? adapter = null,
        bool force = false)
    {
        adapter ??= EnsureComp<LuaMRescueBehaviorAdapterComponent>(uid);
        var profile = escort.Role == LuaMRescueEscortRole.Kostyl
            ? "LuaMRescueTransportBehavior"
            : "LuaMRescueCrewBehavior";
        if (!PrepareEvaluation(uid, adapter, profile, force))
            return true;

        ReportEscortThreat(uid, escort);

        var routeTarget = ValidTarget(escort.RouteBlockerTarget);
        if (routeTarget != null || escort.NearbyBlockers > 0)
        {
            Report(
                uid,
                LuaMBehaviorStimulus.RouteBlocked,
                Math.Clamp(Math.Max(1, escort.NearbyBlockers) / 4f, 0.25f, 1f),
                routeTarget,
                Coordinates(routeTarget));
        }
        else if (ValidTarget(escort.Patient) is { } patient)
        {
            Report(uid, LuaMBehaviorStimulus.EscortRequired, 0.85f, patient, Transform(patient).Coordinates);
        }

        return _behavior.EvaluateNow(uid, out _);
    }

    private bool PrepareEvaluation(
        EntityUid uid,
        LuaMRescueBehaviorAdapterComponent adapter,
        string profile,
        bool force)
    {
        var now = _timing.CurTime;
        var agent = EnsureComp<LuaMBehaviorAgentComponent>(uid);
        if (agent.Profile != profile)
        {
            agent.Profile = profile;
            agent.NextEvaluation = TimeSpan.Zero;
        }

        // Rescue domain executors remain the sole owners of ActivityContext and
        // HTN movement. The common arbiter is advisory for Aibolit/escort state
        // and is evaluated only after this adapter has published a complete
        // observation snapshot.
        agent.WriteHtnBlackboard = false;
        agent.DriveActivityLifecycle = false;
        agent.ExternalEvaluationOnly = true;

        if (!force && adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
            return false;

        adapter.NextEvaluation = now + EvaluationInterval;
        _behavior.ClearObservations(uid, source: ObservationSource);
        return true;
    }

    private bool ReportRescueRoute(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid? patient)
    {
        var routeTarget = ValidTarget(rescue.RouteBlockHoldGoal) ??
                          ValidTarget(rescue.RouteBlockHoldTarget) ??
                          ValidTarget(patient);
        var destination = Coordinates(routeTarget) ?? rescue.ActivityContext.Destination;
        switch (rescue.ActivityContext.RouteStatus)
        {
            case LuaMRescueRouteStatus.NoPath:
            case LuaMRescueRouteStatus.InvalidDestination:
                Report(uid, LuaMBehaviorStimulus.NoPath, 1f, routeTarget, destination);
                return true;
            case LuaMRescueRouteStatus.Blocked:
                Report(uid, LuaMBehaviorStimulus.RouteBlocked, 1f, routeTarget, destination);
                return true;
        }

        if (rescue.RouteBlockHoldTarget is not { Valid: true })
            return false;

        Report(uid, LuaMBehaviorStimulus.RouteBlocked, 0.9f, routeTarget, destination);
        return true;
    }

    private void ReportRescueThreat(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (!TryComp<LuaMRescueTeamComponent>(uid, out var team))
            return;

        var threat = ValidTarget(team.ThreatTarget);
        if (threat == null && team.NearbyHostiles <= 0 && team.NearbyCombatants <= 0)
            return;

        var limit = Math.Max(1, rescue.ActivityRoleProfile.ThreatPolicy.SelfPreservationHostileLimit);
        var pressure = Math.Max(team.NearbyHostiles, team.NearbyCombatants);
        var severity = Math.Clamp(Math.Max(1, pressure) / (float) limit, 0.25f, 1f);
        Report(uid, LuaMBehaviorStimulus.HostileThreat, severity, threat, Coordinates(threat));

        if (pressure > limit)
            Report(uid, LuaMBehaviorStimulus.Overwhelmed, Math.Clamp(pressure / (float) (limit + 1), 0.5f, 1f), threat);
    }

    private void ReportEscortThreat(EntityUid uid, LuaMRescueEscortComponent escort)
    {
        var threat = ValidTarget(escort.ThreatTarget);
        if (threat == null && escort.NearbyHostiles <= 0 && escort.NearbyCombatants <= 0)
            return;

        var limit = 5;
        if (TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier))
            limit = Math.Max(1, carrier.ActivityRoleProfile.ThreatPolicy.SelfPreservationHostileLimit);

        var pressure = Math.Max(escort.NearbyHostiles, escort.NearbyCombatants);
        var severity = Math.Clamp(Math.Max(1, pressure) / (float) limit, 0.25f, 1f);
        Report(uid, LuaMBehaviorStimulus.HostileThreat, severity, threat, Coordinates(threat));

        if (pressure > limit)
            Report(uid, LuaMBehaviorStimulus.Overwhelmed, Math.Clamp(pressure / (float) (limit + 1), 0.5f, 1f), threat);
    }

    private void ReportPatient(EntityUid uid, EntityUid patient)
    {
        var destination = Transform(patient).Coordinates;
        if (TryComp<MobStateComponent>(patient, out var mobState))
        {
            if (mobState.CurrentState == MobState.Dead)
            {
                Report(uid, LuaMBehaviorStimulus.RecoverableDeadAlly, 1f, patient, destination);
                return;
            }

            if (mobState.CurrentState == MobState.Critical)
            {
                Report(uid, LuaMBehaviorStimulus.AllyCritical, 1f, patient, destination);
                return;
            }
        }

        if (TryComp<DamageableComponent>(patient, out var damage) && damage.TotalDamage.Float() > 0f)
        {
            Report(
                uid,
                LuaMBehaviorStimulus.AllyInjured,
                Math.Clamp(damage.TotalDamage.Float() / 100f, 0.1f, 1f),
                patient,
                destination);
            return;
        }

        Report(uid, LuaMBehaviorStimulus.PatientWaiting, 0.6f, patient, destination);
    }

    private void OnRescueDecisionChanged(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        ref LuaMBehaviorDecisionChangedEvent args)
    {
        var adapter = EnsureComp<LuaMRescueBehaviorAdapterComponent>(uid);
        adapter.Intent = args.Current.Intent;
        adapter.DecisionGeneration = args.Current.Generation;

        if (HasComp<ActorComponent>(uid))
        {
            adapter.LastStatus = $"{args.Current.Intent}: player control keeps executor authority";
            return;
        }

        if (!TryMapRescueActivity(rescue, args.Current, out var activity, out var target))
        {
            adapter.LastStatus = $"{args.Current.Intent}: observed; rescue executor keeps current activity";
            return;
        }

        var current = rescue.ActivityContext;
        adapter.LastStatus =
            $"{args.Current.Intent}: advisory {activity} target={target?.ToString() ?? "none"}; " +
            $"rescue executor keeps {current.Activity}@g{current.Generation}";
    }

    private void OnEscortDecisionChanged(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        ref LuaMBehaviorDecisionChangedEvent args)
    {
        var adapter = EnsureComp<LuaMRescueBehaviorAdapterComponent>(uid);
        adapter.Intent = args.Current.Intent;
        adapter.DecisionGeneration = args.Current.Generation;

        if (HasComp<ActorComponent>(uid))
        {
            adapter.EscortDutyOverride = null;
            adapter.LastStatus = $"{args.Current.Intent}: player control keeps executor authority";
            return;
        }

        var dutyOverride = MapEscortDuty(escort, args.Current.Intent);
        if (dutyOverride == LuaMRescueEscortDuty.Standby &&
            IsRetreatIntent(args.Current.Intent) &&
            HasActiveEscortMissionContract(uid, escort))
        {
            // A safety intent without a usable retreat anchor has no executable
            // replacement duty. Keep the explicit rescue sortie authoritative
            // instead of converting that missing fallback into Standby.
            dutyOverride = null;
        }

        adapter.EscortDutyOverride = dutyOverride;
        var refreshed = _team.RefreshEscortBehaviorExecution(uid);
        adapter.LastStatus = adapter.EscortDutyOverride is { } duty
            ? $"{args.Current.Intent}: escort duty {duty}; executor refreshed={refreshed}"
            : $"{args.Current.Intent}: sortie plan keeps escort duty; executor refreshed={refreshed}";
    }

    private bool TryMapRescueActivity(
        LuaMRescueAgentComponent rescue,
        LuaMBehaviorDecision decision,
        out LuaMRescueActivity activity,
        out EntityUid? target)
    {
        // Safety decisions often target the threat that caused them. Preserve
        // the rescue-owned patient instead of accidentally treating or pulling
        // that hostile entity as the casualty.
        var patient = GetRescuePatient(rescue) ?? ValidTarget(decision.Target);
        var returnTarget = ValidTarget(rescue.AssignedReturnTarget) ??
                           ValidTarget(rescue.AssignedShuttleAnchor) ??
                           ValidTarget(rescue.AssignedShuttle);

        switch (decision.Intent)
        {
            case LuaMBehaviorIntent.AwaitRescue:
            case LuaMBehaviorIntent.HoldPosition:
                activity = LuaMRescueActivity.Standby;
                target = null;
                return true;
            case LuaMBehaviorIntent.Treat:
                activity = LuaMRescueActivity.Treating;
                target = patient;
                return target != null;
            case LuaMBehaviorIntent.Revive:
                activity = LuaMRescueActivity.Recovering;
                target = patient;
                return target != null;
            case LuaMBehaviorIntent.EvacuateCasualty:
                activity = LuaMRescueActivity.PreparingEvacuation;
                target = patient;
                return target != null;
            case LuaMBehaviorIntent.Rescue:
            case LuaMBehaviorIntent.Escort:
            case LuaMBehaviorIntent.FollowOrder:
            case LuaMBehaviorIntent.ExecuteWorkOrder:
                activity = LuaMRescueActivity.Approaching;
                target = patient;
                return target != null;
            case LuaMBehaviorIntent.ReplanRoute:
            case LuaMBehaviorIntent.ClearRoute:
            case LuaMBehaviorIntent.SearchTarget:
            case LuaMBehaviorIntent.Navigate:
                activity = LuaMRescueActivity.PlanningRoute;
                target = patient;
                return target != null;
            case LuaMBehaviorIntent.Resupply:
                activity = LuaMRescueActivity.Resupplying;
                target = ValidTarget(rescue.TaskSupplyTarget) ?? patient;
                return true;
            case LuaMBehaviorIntent.ReturnHome:
            case LuaMBehaviorIntent.Dock:
            case LuaMBehaviorIntent.SeekSafeAtmosphere:
            case LuaMBehaviorIntent.TakeCover:
            case LuaMBehaviorIntent.EvadeProjectile:
                activity = LuaMRescueActivity.Returning;
                target = returnTarget;
                return target != null;
            case LuaMBehaviorIntent.Flee:
                if (decision.RuleId == "safety-flee-unarmed-threat" && patient != null)
                {
                    activity = LuaMRescueActivity.PreparingEvacuation;
                    target = patient;
                    return true;
                }

                activity = LuaMRescueActivity.Returning;
                target = returnTarget;
                return target != null;
            case LuaMBehaviorIntent.Retreat:
            case LuaMBehaviorIntent.EvacuateHazard:
                if (patient != null)
                {
                    activity = LuaMRescueActivity.PreparingEvacuation;
                    target = patient;
                    return true;
                }

                activity = LuaMRescueActivity.Returning;
                target = returnTarget;
                return target != null;
            case LuaMBehaviorIntent.Standby:
                if (patient != null)
                    break;
                activity = LuaMRescueActivity.Standby;
                target = null;
                return true;
        }

        activity = LuaMRescueActivity.None;
        target = null;
        return false;
    }

    private LuaMRescueEscortDuty? MapEscortDuty(
        LuaMRescueEscortComponent escort,
        LuaMBehaviorIntent intent)
    {
        switch (intent)
        {
            case LuaMBehaviorIntent.AwaitRescue:
            case LuaMBehaviorIntent.HoldPosition:
                return LuaMRescueEscortDuty.Standby;
            case LuaMBehaviorIntent.Flee:
            case LuaMBehaviorIntent.Retreat:
            case LuaMBehaviorIntent.TakeCover:
            case LuaMBehaviorIntent.EvadeProjectile:
            case LuaMBehaviorIntent.EvacuateHazard:
            case LuaMBehaviorIntent.SeekSafeAtmosphere:
                return HasRetreatAnchor(escort)
                    ? LuaMRescueEscortDuty.ReturnToShuttle
                    : LuaMRescueEscortDuty.Standby;
            case LuaMBehaviorIntent.DefendSelf:
            case LuaMBehaviorIntent.DefendArea:
            case LuaMBehaviorIntent.ProtectTarget:
                return escort.Role is LuaMRescueEscortRole.Tourniquet or LuaMRescueEscortRole.Zaslon
                    ? LuaMRescueEscortDuty.ThreatScreen
                    : LuaMRescueEscortDuty.PatientSupport;
            case LuaMBehaviorIntent.ClearRoute:
            case LuaMBehaviorIntent.ReplanRoute:
                return escort.Role == LuaMRescueEscortRole.Kostyl
                    ? LuaMRescueEscortDuty.PatientSupport
                    : LuaMRescueEscortDuty.ClearRoute;
            case LuaMBehaviorIntent.Escort:
            case LuaMBehaviorIntent.Rescue:
            case LuaMBehaviorIntent.EvacuateCasualty:
                return escort.Role == LuaMRescueEscortRole.Kostyl
                    ? LuaMRescueEscortDuty.PatientSupport
                    : LuaMRescueEscortDuty.EvacuationCorridor;
            case LuaMBehaviorIntent.Treat:
            case LuaMBehaviorIntent.Revive:
                return LuaMRescueEscortDuty.PatientSupport;
            case LuaMBehaviorIntent.Patrol:
            case LuaMBehaviorIntent.Investigate:
                return LuaMRescueEscortDuty.SecureScene;
            default:
                return null;
        }
    }

    private bool HasActiveEscortMissionContract(
        EntityUid uid,
        LuaMRescueEscortComponent escort)
    {
        var sortiePlan = escort.SortiePlan;
        var patient = ValidTarget(escort.Patient);
        var routeBlocker = ValidTarget(escort.RouteBlockerTarget);
        var nearbyBlockers = escort.NearbyBlockers;

        // The behavior decision event can run before the escort executor's
        // first context sync. Read the leader team as the authoritative mission
        // source so that a stale default mirror cannot persist a Standby
        // override over an already-issued sortie.
        if (escort.Leader is { Valid: true } leader &&
            TryComp<LuaMRescueTeamComponent>(leader, out var team))
        {
            sortiePlan = team.SortiePlan;
            patient = ValidTarget(team.Patient);
            routeBlocker = ValidTarget(team.RouteBlockerTarget);
            nearbyBlockers = team.NearbyBlockers;
        }

        if (sortiePlan == LuaMRescueSortiePlan.Standby ||
            !TryComp<LuaMBehaviorAgentComponent>(uid, out var agent) ||
            !_prototypes.TryIndex(agent.Profile, out LuaMBehaviorProfilePrototype? profile))
        {
            return false;
        }

        if (routeBlocker != null || nearbyBlockers > 0)
        {
            return profile.HasCapability(LuaMBehaviorCapability.Interact) &&
                   profile.HasCapability(LuaMBehaviorCapability.Navigate);
        }

        return patient != null &&
               profile.HasCapability(LuaMBehaviorCapability.Escort) &&
               profile.HasCapability(LuaMBehaviorCapability.Navigate);
    }

    private static bool IsRetreatIntent(LuaMBehaviorIntent intent)
    {
        return intent is LuaMBehaviorIntent.Flee or
            LuaMBehaviorIntent.Retreat or
            LuaMBehaviorIntent.TakeCover or
            LuaMBehaviorIntent.EvadeProjectile or
            LuaMBehaviorIntent.EvacuateHazard or
            LuaMBehaviorIntent.SeekSafeAtmosphere;
    }

    private EntityUid? GetRescuePatient(LuaMRescueAgentComponent rescue)
    {
        return ValidTarget(rescue.TaskPatientTarget) ??
               ValidTarget(rescue.EvacuatingTarget) ??
               ValidTarget(rescue.OnboardCareTarget) ??
               ValidTarget(rescue.AssignedTarget) ??
               ValidTarget(rescue.ActivityContext.Target);
    }

    private EntityUid? ValidTarget(EntityUid? target)
    {
        return target is { Valid: true } uid && !TerminatingOrDeleted(uid)
            ? uid
            : null;
    }

    private EntityCoordinates? Coordinates(EntityUid? target)
    {
        return ValidTarget(target) is { } uid
            ? Transform(uid).Coordinates
            : null;
    }

    private static bool HasRetreatAnchor(LuaMRescueEscortComponent escort)
    {
        return escort.ShuttleAnchor is { Valid: true } || escort.Shuttle is { Valid: true };
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
}
