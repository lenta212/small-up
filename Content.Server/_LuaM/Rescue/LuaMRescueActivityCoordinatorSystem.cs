using System;
using System.Collections.Generic;
using Content.Server._LuaM.NPC;
using Content.Server.Atmos.Rotting;
using Content.Server.Body.Components;
using Content.Shared._LuaM.NPC;
using Content.Shared._Mono.CorticalBorer;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Traits.Assorted;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

/// <summary>
/// Rescue compatibility facade over the generic sector NPC lifecycle. Rescue
/// target eligibility and urgency remain here; generation, deadlines, retries
/// and terminal transitions are owned by <see cref="LuaMNpcActivityLifecycleSystem"/>.
/// Movement and interactions remain in role executors and report outcomes here.
/// </summary>
public sealed class LuaMRescueActivityCoordinatorSystem : EntitySystem
{
    private readonly LuaMRescueRoleProfile _automaticDispatchProfile =
        LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Aibolit);
    // Rescue activity state is server-authoritative and intentionally not networked;
    // admin telemetry reads it directly. Keep call sites expressive without invoking
    // EntityManager.Dirty, which is only valid for networked components.
    private static void Dirty(EntityUid _, LuaMRescueAgentComponent __) { }

    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly RottingSystem _rotting = default!;
    [Dependency] private readonly LuaMNpcActivityLifecycleSystem _npcLifecycle = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var agent, out var rescue))
        {
            var state = ToNpcState(rescue.ActivityContext);
            var policy = new RescueActivityPolicyAdapter(rescue.ActivityRoleProfile);
            if (!_npcLifecycle.ExpireIfDue(
                    state,
                    policy,
                    now,
                    FailureId(LuaMRescueFailureReason.DeadlineExceeded)))
                continue;

            ApplyNpcState(rescue.ActivityContext, state);
            Dirty(agent, rescue);
        }
    }

    public bool BeginOrReplaceIntent(
        EntityUid agent,
        LuaMRescueRole role,
        LuaMRescueActivity activity,
        EntityUid? target,
        EntityCoordinates? destination,
        out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var profile = GetOrResetRoleProfile(rescue, role);
        var now = _timing.CurTime;
        var state = ToNpcState(rescue.ActivityContext);
        var policy = new RescueActivityPolicyAdapter(profile);
        if (!_npcLifecycle.BeginOrReplaceIntent(
                state,
                policy,
                ActivityId(activity),
                target,
                destination,
                now,
                out var rejection))
        {
            snapshot = Snapshot(agent, rescue) with
            {
                FailureReason = RejectionFailure(rejection, activity),
            };
            return false;
        }

        rescue.ActivityRole = role;
        ApplyNpcState(rescue.ActivityContext, state);

        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        return true;
    }

    public bool RecordAttempt(EntityUid agent, out LuaMRescueActivitySnapshot snapshot)
    {
        return RecordAttempt(agent, expectedGeneration: null, out snapshot);
    }

    public bool RecordAttempt(EntityUid agent, uint expectedGeneration, out LuaMRescueActivitySnapshot snapshot)
    {
        return RecordAttempt(agent, (uint?) expectedGeneration, out snapshot);
    }

    private bool RecordAttempt(EntityUid agent, uint? expectedGeneration, out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var profile = rescue.ActivityRoleProfile;
        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.RecordAttempt(
            state,
            new RescueActivityPolicyAdapter(profile),
            expectedGeneration,
            _timing.CurTime,
            FailureId(LuaMRescueFailureReason.AttemptLimitReached),
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);

        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool CanAttempt(
        EntityUid agent,
        out TimeSpan retryAfter,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return CanAttempt(agent, expectedGeneration: null, out retryAfter, out snapshot);
    }

    public bool CanAttempt(
        EntityUid agent,
        uint expectedGeneration,
        out TimeSpan retryAfter,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return CanAttempt(agent, (uint?) expectedGeneration, out retryAfter, out snapshot);
    }

    private bool CanAttempt(
        EntityUid agent,
        uint? expectedGeneration,
        out TimeSpan retryAfter,
        out LuaMRescueActivitySnapshot snapshot)
    {
        retryAfter = TimeSpan.Zero;
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.CanAttempt(
            state,
            new RescueActivityPolicyAdapter(rescue.ActivityRoleProfile),
            expectedGeneration,
            _timing.CurTime,
            out retryAfter,
            out var rejection);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    /// <summary>
    /// Re-arms a retryable blocked intent after its backoff without losing the
    /// overall bounded-attempt budget. Failed/deadline/succeeded terminals remain
    /// immutable; only an explicit new order may replace those.
    /// </summary>
    public bool TryRecoverBlockedIntent(
        EntityUid agent,
        uint expectedGeneration,
        LuaMRescueActivity activity,
        EntityUid? target,
        EntityCoordinates? destination,
        out TimeSpan retryAfter,
        out LuaMRescueActivitySnapshot snapshot)
    {
        retryAfter = TimeSpan.Zero;
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.TryRecoverBlockedIntent(
            state,
            new RescueActivityPolicyAdapter(rescue.ActivityRoleProfile),
            expectedGeneration,
            ActivityId(activity),
            target,
            destination,
            _timing.CurTime,
            out retryAfter,
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);
        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool RecordProgress(
        EntityUid agent,
        EntityCoordinates? destination,
        float? remainingDistance,
        LuaMRescueRouteStatus routeStatus,
        LuaMRescueDoAfterStatus doAfterStatus,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return RecordProgress(
            agent,
            expectedGeneration: null,
            destination,
            remainingDistance,
            routeStatus,
            doAfterStatus,
            out snapshot);
    }

    public bool RecordProgress(
        EntityUid agent,
        uint expectedGeneration,
        EntityCoordinates? destination,
        float? remainingDistance,
        LuaMRescueRouteStatus routeStatus,
        LuaMRescueDoAfterStatus doAfterStatus,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return RecordProgress(
            agent,
            (uint?) expectedGeneration,
            destination,
            remainingDistance,
            routeStatus,
            doAfterStatus,
            out snapshot);
    }

    private bool RecordProgress(
        EntityUid agent,
        uint? expectedGeneration,
        EntityCoordinates? destination,
        float? remainingDistance,
        LuaMRescueRouteStatus routeStatus,
        LuaMRescueDoAfterStatus doAfterStatus,
        out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.RecordProgress(
            state,
            new RescueActivityPolicyAdapter(rescue.ActivityRoleProfile),
            expectedGeneration,
            destination,
            remainingDistance,
            ToNpcRouteStatus(routeStatus),
            ToNpcActionStatus(doAfterStatus),
            _timing.CurTime,
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);

        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool Complete(EntityUid agent, out LuaMRescueActivitySnapshot snapshot)
    {
        return Complete(agent, expectedGeneration: null, out snapshot);
    }

    public bool Complete(EntityUid agent, uint expectedGeneration, out LuaMRescueActivitySnapshot snapshot)
    {
        return Complete(agent, (uint?) expectedGeneration, out snapshot);
    }

    private bool Complete(EntityUid agent, uint? expectedGeneration, out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.Complete(
            state,
            expectedGeneration,
            _timing.CurTime,
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);
        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool Cancel(
        EntityUid agent,
        LuaMRescueFailureReason reason,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return Cancel(agent, expectedGeneration: null, reason, out snapshot);
    }

    public bool Cancel(
        EntityUid agent,
        uint expectedGeneration,
        LuaMRescueFailureReason reason,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return Cancel(agent, (uint?) expectedGeneration, reason, out snapshot);
    }

    private bool Cancel(
        EntityUid agent,
        uint? expectedGeneration,
        LuaMRescueFailureReason reason,
        out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.Cancel(
            state,
            expectedGeneration,
            FailureId(reason),
            _timing.CurTime,
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);
        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool Block(
        EntityUid agent,
        LuaMRescueFailureReason reason,
        LuaMRescueActivity fallback,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return Block(agent, expectedGeneration: null, reason, fallback, out snapshot);
    }

    public bool Block(
        EntityUid agent,
        uint expectedGeneration,
        LuaMRescueFailureReason reason,
        LuaMRescueActivity fallback,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return Block(agent, (uint?) expectedGeneration, reason, fallback, out snapshot);
    }

    private bool Block(
        EntityUid agent,
        uint? expectedGeneration,
        LuaMRescueFailureReason reason,
        LuaMRescueActivity fallback,
        out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var routeStatus = reason switch
        {
            LuaMRescueFailureReason.NoPath => LuaMNpcActivityRouteStatus.NoPath,
            LuaMRescueFailureReason.RouteBlocked => LuaMNpcActivityRouteStatus.Blocked,
            _ => (LuaMNpcActivityRouteStatus?) null,
        };
        var result = _npcLifecycle.Block(
            state,
            new RescueActivityPolicyAdapter(rescue.ActivityRoleProfile),
            expectedGeneration,
            FailureId(reason),
            ActivityId(fallback),
            routeStatus,
            _timing.CurTime,
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);

        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool Fail(
        EntityUid agent,
        LuaMRescueFailureReason reason,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return Fail(agent, expectedGeneration: null, reason, out snapshot);
    }

    public bool Fail(
        EntityUid agent,
        uint expectedGeneration,
        LuaMRescueFailureReason reason,
        out LuaMRescueActivitySnapshot snapshot)
    {
        return Fail(agent, (uint?) expectedGeneration, reason, out snapshot);
    }

    private bool Fail(
        EntityUid agent,
        uint? expectedGeneration,
        LuaMRescueFailureReason reason,
        out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        var state = ToNpcState(rescue.ActivityContext);
        var result = _npcLifecycle.Fail(
            state,
            expectedGeneration,
            FailureId(reason),
            _timing.CurTime,
            out var rejection);
        ApplyNpcState(rescue.ActivityContext, state);
        Dirty(agent, rescue);
        snapshot = Snapshot(agent, rescue);
        if (rejection == LuaMNpcActivityRejection.StaleGeneration)
            snapshot = snapshot with { FailureReason = LuaMRescueFailureReason.StaleGeneration };
        return result;
    }

    public bool GetSnapshot(EntityUid agent, out LuaMRescueActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
        {
            snapshot = MissingSnapshot(agent, LuaMRescueFailureReason.MissingActivityComponent);
            return false;
        }

        snapshot = Snapshot(agent, rescue);
        return true;
    }

    public bool IsEligibleRescuePatient(
        EntityUid rescuer,
        EntityUid candidate,
        LuaMRescuePatientRequestKind kind,
        bool manualOverride,
        out LuaMRescueFailureReason failureReason)
    {
        failureReason = LuaMRescueFailureReason.None;
        var profile = ResolveRoleProfile(rescuer);
        var targetPolicy = profile.TargetPolicy;

        if (manualOverride || kind == LuaMRescuePatientRequestKind.Manual)
        {
            if (!targetPolicy.AllowManualOverride)
            {
                failureReason = LuaMRescueFailureReason.RoleDisallowed;
                return false;
            }
        }
        else if (!targetPolicy.Allows(kind))
        {
            failureReason = LuaMRescueFailureReason.RoleDisallowed;
            return false;
        }

        if (candidate == rescuer)
        {
            failureReason = LuaMRescueFailureReason.SelfTarget;
            return false;
        }

        if (!candidate.Valid || Deleted(candidate))
        {
            failureReason = LuaMRescueFailureReason.TargetMissing;
            return false;
        }

        // Containment must be checked before species so callers get the actionable blocker first.
        if (targetPolicy.RejectContained && _containers.IsEntityOrParentInContainer(candidate))
        {
            failureReason = LuaMRescueFailureReason.TargetContained;
            return false;
        }

        // Cortical borers are never patients, including manual overrides.
        if (targetPolicy.RejectCorticalBorer && HasComp<CorticalBorerComponent>(candidate))
        {
            failureReason = LuaMRescueFailureReason.ExcludedSpecies;
            return false;
        }

        // Rescue actors are coworkers, never patients. This also avoids rescue-agent feedback loops.
        if (targetPolicy.RejectRescuePersonnel &&
            (HasComp<LuaMRescuePersonnelComponent>(candidate) ||
             HasComp<LuaMRescueAgentComponent>(candidate) ||
             HasComp<LuaMRescueEscortComponent>(candidate)))
        {
            failureReason = LuaMRescueFailureReason.InvalidPatient;
            return false;
        }

        // Only apply hostility when both sides participate in the normal NPC faction model.
        if (!manualOverride &&
            targetPolicy.RejectHostileAutomatic &&
            IsHostileToRescuer(rescuer, candidate))
        {
            failureReason = LuaMRescueFailureReason.ThreatTooHigh;
            return false;
        }

        if (!TryComp<MobStateComponent>(candidate, out var mobState) ||
            !TryComp<DamageableComponent>(candidate, out var damageable))
        {
            failureReason = LuaMRescueFailureReason.UnsupportedPatient;
            return false;
        }

        if (!manualOverride &&
            targetPolicy.RequireHumanoidForAutomatic &&
            !HasComp<HumanoidAppearanceComponent>(candidate))
        {
            failureReason = LuaMRescueFailureReason.UnsupportedPatient;
            return false;
        }

        if (manualOverride || kind == LuaMRescuePatientRequestKind.Manual)
            return true;

        var isDead = mobState.CurrentState == MobState.Dead;
        if (kind == LuaMRescuePatientRequestKind.AutomaticDeathSignal && !isDead)
        {
            failureReason = LuaMRescueFailureReason.TargetNotDead;
            return false;
        }

        if (isDead)
        {
            if (!targetPolicy.AllowDeadRecovery)
            {
                failureReason = LuaMRescueFailureReason.Unrevivable;
                return false;
            }

            if (kind == LuaMRescuePatientRequestKind.AutomaticTreatment)
            {
                failureReason = LuaMRescueFailureReason.TargetDead;
                return false;
            }

            if (targetPolicy.RequirePullableForEvacuation &&
                kind == LuaMRescuePatientRequestKind.AutomaticEvacuation &&
                !HasComp<PullableComponent>(candidate))
            {
                failureReason = LuaMRescueFailureReason.TargetNotPullable;
                return false;
            }

            return IsRecoverableDeadPatient(candidate, out failureReason);
        }

        if (kind == LuaMRescuePatientRequestKind.AutomaticDeathSignal)
        {
            failureReason = LuaMRescueFailureReason.TargetNotDead;
            return false;
        }

        if (targetPolicy.RequirePullableForEvacuation &&
            kind == LuaMRescuePatientRequestKind.AutomaticEvacuation &&
            !HasComp<PullableComponent>(candidate))
        {
            failureReason = LuaMRescueFailureReason.TargetNotPullable;
            return false;
        }

        if (targetPolicy.RequireInjectableForTreatment &&
            kind == LuaMRescuePatientRequestKind.AutomaticTreatment &&
            !HasComp<InjectableSolutionComponent>(candidate))
        {
            failureReason = LuaMRescueFailureReason.TargetNotInjectable;
            return false;
        }

        if (mobState.CurrentState == MobState.Critical || damageable.TotalDamage.Float() > 0f)
            return true;

        failureReason = LuaMRescueFailureReason.TargetHealthy;
        return false;
    }

    public LuaMRescuePatientUrgency GetPatientUrgency(EntityUid candidate)
    {
        if (!candidate.Valid || Deleted(candidate) ||
            _containers.IsEntityOrParentInContainer(candidate) ||
            HasComp<CorticalBorerComponent>(candidate) ||
            !TryComp<MobStateComponent>(candidate, out var mobState) ||
            !TryComp<DamageableComponent>(candidate, out var damageable))
        {
            return LuaMRescuePatientUrgency.None;
        }

        if (mobState.CurrentState == MobState.Critical)
            return LuaMRescuePatientUrgency.Critical;

        if (mobState.CurrentState == MobState.Dead)
        {
            return IsRecoverableDeadPatient(candidate, out _)
                ? LuaMRescuePatientUrgency.RecoverableDead
                : LuaMRescuePatientUrgency.None;
        }

        // Active blood loss is observable evidence that a living patient is getting
        // worse. It must outrank a merely severe but currently stable injury and any
        // dead-recovery call, without relying on random score jitter.
        if (TryComp<BloodstreamComponent>(candidate, out var bloodstream) &&
            bloodstream.BleedAmount > 0f)
        {
            return LuaMRescuePatientUrgency.Deteriorating;
        }

        var damage = damageable.TotalDamage.Float();
        if (damage >= 50f)
            return LuaMRescuePatientUrgency.Severe;

        if (damage > 0f)
            return LuaMRescuePatientUrgency.Injured;

        return LuaMRescuePatientUrgency.Stable;
    }

    public int GetPatientPriority(
        EntityUid rescuer,
        EntityUid candidate,
        LuaMRescuePatientRequestKind kind,
        bool manualOverride,
        out LuaMRescueFailureReason failureReason)
    {
        if (!IsEligibleRescuePatient(rescuer, candidate, kind, manualOverride, out failureReason))
            return int.MinValue;

        var urgency = GetPatientUrgency(candidate);
        var urgencyScore = ResolveRoleProfile(rescuer).GetPatientPriority(urgency);
        var damageScore = TryComp<DamageableComponent>(candidate, out var damageable)
            ? Math.Clamp((int) damageable.TotalDamage.Float(), 0, 9_999)
            : 0;
        return urgencyScore + damageScore;
    }

    public bool SelectBestEligiblePatient(
        EntityUid rescuer,
        IEnumerable<EntityUid> candidates,
        LuaMRescuePatientRequestKind kind,
        bool manualOverride,
        out EntityUid selected,
        out LuaMRescueFailureReason failureReason)
    {
        selected = default;
        failureReason = LuaMRescueFailureReason.NoEligibleTarget;
        if (!ResolveRoleProfile(rescuer).TargetPolicy.CanSelectMedicalTargets)
        {
            failureReason = LuaMRescueFailureReason.RoleDisallowed;
            return false;
        }

        var bestPriority = int.MinValue;

        foreach (var candidate in candidates)
        {
            var priority = GetPatientPriority(rescuer, candidate, kind, manualOverride, out _);
            if (priority == int.MinValue ||
                priority < bestPriority ||
                priority == bestPriority && selected.Valid && candidate.Id >= selected.Id)
            {
                continue;
            }

            selected = candidate;
            bestPriority = priority;
        }

        if (!selected.Valid)
            return false;

        failureReason = LuaMRescueFailureReason.None;
        return true;
    }

    public static bool IsTransitionAllowed(LuaMRescueActivity from, LuaMRescueActivity to)
    {
        if (from == to)
            return true;

        if (to is LuaMRescueActivity.Standby
            or LuaMRescueActivity.Observing
            or LuaMRescueActivity.Returning
            or LuaMRescueActivity.Interacting)
            return true;

        return from switch
        {
            LuaMRescueActivity.None => true,
            LuaMRescueActivity.Standby => to is LuaMRescueActivity.Observing
                or LuaMRescueActivity.Dispatching
                or LuaMRescueActivity.Resupplying,
            LuaMRescueActivity.Observing => to is LuaMRescueActivity.Standby
                or LuaMRescueActivity.Dispatching
                or LuaMRescueActivity.Resupplying,
            LuaMRescueActivity.Dispatching => to is LuaMRescueActivity.PlanningRoute
                or LuaMRescueActivity.Approaching
                or LuaMRescueActivity.Protecting,
            LuaMRescueActivity.PlanningRoute => to is LuaMRescueActivity.Approaching
                or LuaMRescueActivity.Protecting,
            LuaMRescueActivity.Approaching => to is LuaMRescueActivity.Triage
                or LuaMRescueActivity.Treating
                or LuaMRescueActivity.Recovering
                or LuaMRescueActivity.PreparingEvacuation
                or LuaMRescueActivity.Protecting,
            LuaMRescueActivity.Triage => to is LuaMRescueActivity.Treating
                or LuaMRescueActivity.Recovering
                or LuaMRescueActivity.PreparingEvacuation
                or LuaMRescueActivity.Resupplying,
            LuaMRescueActivity.Treating => to is LuaMRescueActivity.Triage
                or LuaMRescueActivity.PreparingEvacuation
                or LuaMRescueActivity.OnboardCare
                or LuaMRescueActivity.Resupplying
                or LuaMRescueActivity.Handoff,
            LuaMRescueActivity.Recovering => to is LuaMRescueActivity.Treating
                or LuaMRescueActivity.PreparingEvacuation
                or LuaMRescueActivity.OnboardCare,
            LuaMRescueActivity.PreparingEvacuation => to is LuaMRescueActivity.Pulling
                or LuaMRescueActivity.Treating
                or LuaMRescueActivity.Recovering,
            LuaMRescueActivity.Pulling => to == LuaMRescueActivity.Delivering,
            LuaMRescueActivity.Delivering => to is LuaMRescueActivity.BucklePatient
                or LuaMRescueActivity.OnboardCare
                or LuaMRescueActivity.Handoff,
            LuaMRescueActivity.BucklePatient => to == LuaMRescueActivity.OnboardCare,
            LuaMRescueActivity.OnboardCare => to is LuaMRescueActivity.Treating
                or LuaMRescueActivity.Recovering
                or LuaMRescueActivity.Handoff
                or LuaMRescueActivity.Approaching,
            LuaMRescueActivity.Handoff => to is LuaMRescueActivity.Approaching
                or LuaMRescueActivity.Returning,
            LuaMRescueActivity.Resupplying => to is LuaMRescueActivity.PlanningRoute
                or LuaMRescueActivity.Approaching
                or LuaMRescueActivity.Triage
                or LuaMRescueActivity.Treating,
            LuaMRescueActivity.Protecting => to is LuaMRescueActivity.ThreatScreen
                or LuaMRescueActivity.CrowdControl
                or LuaMRescueActivity.ClearRoute
                or LuaMRescueActivity.Approaching,
            LuaMRescueActivity.ThreatScreen or LuaMRescueActivity.CrowdControl or LuaMRescueActivity.ClearRoute =>
                to is LuaMRescueActivity.Protecting or LuaMRescueActivity.Approaching,
            LuaMRescueActivity.Returning => to is LuaMRescueActivity.Standby or LuaMRescueActivity.Observing,
            _ => false,
        };
    }

    private bool IsHostileToRescuer(EntityUid rescuer, EntityUid candidate)
    {
        if (!TryComp<NpcFactionMemberComponent>(candidate, out var candidateFaction))
        {
            // Automatic station-wide signals have no concrete observer yet. A
            // humanoid without a crew faction must not become an omniscient
            // rescue dispatch merely because there is no rescuer to compare.
            return !rescuer.Valid || Deleted(rescuer);
        }

        if (!rescuer.Valid || Deleted(rescuer))
        {
            // Automatic dispatch is a NanoTrasen crew service. Registered
            // manual medical calls still use the manual-override path above.
            var canonicalCrew = _factions.IsMember((candidate, candidateFaction), "NanoTrasen") ||
                                 _factions.IsFactionFriendly("NanoTrasen", (candidate, candidateFaction));
            return !canonicalCrew;
        }

        if (!TryComp<NpcFactionMemberComponent>(rescuer, out var rescuerFaction) ||
            _factions.IsEntityFriendly((rescuer, rescuerFaction), (candidate, candidateFaction)))
        {
            return false;
        }

        // A concrete rescuer owns the decision. This keeps the coordinator
        // reusable by sector roles whose faction is not NanoTrasen.
        return _factions.IsEntityHostile(
            (rescuer, rescuerFaction),
            (candidate, candidateFaction));
    }

    private bool IsRecoverableDeadPatient(EntityUid candidate, out LuaMRescueFailureReason failureReason)
    {
        if (HasComp<UnrevivableComponent>(candidate) ||
            _rotting.IsRotten(candidate) ||
            !HasComp<MobThresholdsComponent>(candidate))
        {
            failureReason = LuaMRescueFailureReason.TargetUnrecoverable;
            return false;
        }

        if (!TryComp<MindContainerComponent>(candidate, out var mindContainer) ||
            !mindContainer.HasMind)
        {
            failureReason = LuaMRescueFailureReason.TargetHasNoMind;
            return false;
        }

        failureReason = LuaMRescueFailureReason.None;
        return true;
    }

    private LuaMRescueRoleProfile GetOrResetRoleProfile(
        LuaMRescueAgentComponent rescue,
        LuaMRescueRole role)
    {
        if (rescue.ActivityRoleProfile.Role == role)
            return rescue.ActivityRoleProfile;

        rescue.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(role);
        return rescue.ActivityRoleProfile;
    }

    private LuaMRescueRoleProfile ResolveRoleProfile(EntityUid actor)
    {
        if (actor.Valid && !Deleted(actor))
        {
            if (TryComp<LuaMRescueAgentComponent>(actor, out var rescue))
                return GetOrResetRoleProfile(rescue, rescue.ActivityRole);

            if (TryComp<LuaMRescueActivityCarrierComponent>(actor, out var carrier))
            {
                if (carrier.ActivityRoleProfile.Role != carrier.ActivityRole)
                    carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(carrier.ActivityRole);
                return carrier.ActivityRoleProfile;
            }
        }

        return _automaticDispatchProfile;
    }

    private static LuaMNpcActivityState ToNpcState(LuaMRescueActivityContext context)
    {
        return new LuaMNpcActivityState
        {
            Activity = ActivityId(context.Activity),
            TerminalStatus = ToNpcTerminalStatus(context.TerminalStatus),
            Target = context.Target,
            Destination = context.Destination,
            StartedAt = context.StartedAt,
            LastProgressAt = context.LastProgressAt,
            LastProgressDistance = context.LastProgressDistance,
            Attempts = context.Attempts,
            Generation = context.Generation,
            Blocked = context.Blocked,
            Fallback = ActivityId(context.Fallback),
            Failure = FailureId(context.FailureReason),
            RetryNotBefore = context.RetryNotBefore,
            RouteStatus = ToNpcRouteStatus(context.RouteStatus),
            ActionStatus = ToNpcActionStatus(context.DoAfterStatus),
            LastTransitionAt = context.LastTransitionAt,
            Deadline = context.Deadline,
        };
    }

    private static void ApplyNpcState(
        LuaMRescueActivityContext context,
        LuaMNpcActivityState state)
    {
        context.Activity = ParseActivity(state.Activity);
        context.TerminalStatus = ToRescueTerminalStatus(state.TerminalStatus);
        context.Target = state.Target;
        context.Destination = state.Destination;
        context.StartedAt = state.StartedAt;
        context.LastProgressAt = state.LastProgressAt;
        context.LastProgressDistance = state.LastProgressDistance;
        context.Attempts = state.Attempts;
        context.Generation = state.Generation;
        context.Blocked = state.Blocked;
        context.Fallback = ParseActivity(state.Fallback);
        context.FailureReason = ParseFailure(state.Failure);
        context.RetryNotBefore = state.RetryNotBefore;
        context.RouteStatus = ToRescueRouteStatus(state.RouteStatus);
        context.DoAfterStatus = ToRescueDoAfterStatus(state.ActionStatus);
        context.LastTransitionAt = state.LastTransitionAt;
        context.Deadline = state.Deadline;
    }

    private static LuaMNpcActivityTerminalStatus ToNpcTerminalStatus(LuaMRescueTerminalStatus status)
    {
        return status switch
        {
            LuaMRescueTerminalStatus.None => LuaMNpcActivityTerminalStatus.None,
            LuaMRescueTerminalStatus.Active => LuaMNpcActivityTerminalStatus.Active,
            LuaMRescueTerminalStatus.Blocked => LuaMNpcActivityTerminalStatus.Blocked,
            LuaMRescueTerminalStatus.Succeeded => LuaMNpcActivityTerminalStatus.Succeeded,
            LuaMRescueTerminalStatus.Failed => LuaMNpcActivityTerminalStatus.Failed,
            LuaMRescueTerminalStatus.Cancelled => LuaMNpcActivityTerminalStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static LuaMRescueTerminalStatus ToRescueTerminalStatus(LuaMNpcActivityTerminalStatus status)
    {
        return status switch
        {
            LuaMNpcActivityTerminalStatus.None => LuaMRescueTerminalStatus.None,
            LuaMNpcActivityTerminalStatus.Active => LuaMRescueTerminalStatus.Active,
            LuaMNpcActivityTerminalStatus.Blocked => LuaMRescueTerminalStatus.Blocked,
            LuaMNpcActivityTerminalStatus.Succeeded => LuaMRescueTerminalStatus.Succeeded,
            LuaMNpcActivityTerminalStatus.Failed => LuaMRescueTerminalStatus.Failed,
            LuaMNpcActivityTerminalStatus.Cancelled => LuaMRescueTerminalStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static LuaMNpcActivityRouteStatus ToNpcRouteStatus(LuaMRescueRouteStatus status)
    {
        return status switch
        {
            LuaMRescueRouteStatus.None => LuaMNpcActivityRouteStatus.None,
            LuaMRescueRouteStatus.Pending => LuaMNpcActivityRouteStatus.Pending,
            LuaMRescueRouteStatus.Planning => LuaMNpcActivityRouteStatus.Planning,
            LuaMRescueRouteStatus.Moving => LuaMNpcActivityRouteStatus.Moving,
            LuaMRescueRouteStatus.Arrived => LuaMNpcActivityRouteStatus.Arrived,
            LuaMRescueRouteStatus.Blocked => LuaMNpcActivityRouteStatus.Blocked,
            LuaMRescueRouteStatus.NoPath => LuaMNpcActivityRouteStatus.NoPath,
            LuaMRescueRouteStatus.InvalidDestination => LuaMNpcActivityRouteStatus.InvalidDestination,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static LuaMRescueRouteStatus ToRescueRouteStatus(LuaMNpcActivityRouteStatus status)
    {
        return status switch
        {
            LuaMNpcActivityRouteStatus.None => LuaMRescueRouteStatus.None,
            LuaMNpcActivityRouteStatus.Pending => LuaMRescueRouteStatus.Pending,
            LuaMNpcActivityRouteStatus.Planning => LuaMRescueRouteStatus.Planning,
            LuaMNpcActivityRouteStatus.Moving => LuaMRescueRouteStatus.Moving,
            LuaMNpcActivityRouteStatus.Arrived => LuaMRescueRouteStatus.Arrived,
            LuaMNpcActivityRouteStatus.Blocked => LuaMRescueRouteStatus.Blocked,
            LuaMNpcActivityRouteStatus.NoPath => LuaMRescueRouteStatus.NoPath,
            LuaMNpcActivityRouteStatus.InvalidDestination => LuaMRescueRouteStatus.InvalidDestination,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static LuaMNpcActivityActionStatus ToNpcActionStatus(LuaMRescueDoAfterStatus status)
    {
        return status switch
        {
            LuaMRescueDoAfterStatus.None => LuaMNpcActivityActionStatus.None,
            LuaMRescueDoAfterStatus.Pending => LuaMNpcActivityActionStatus.Pending,
            LuaMRescueDoAfterStatus.Running => LuaMNpcActivityActionStatus.Running,
            LuaMRescueDoAfterStatus.Succeeded => LuaMNpcActivityActionStatus.Succeeded,
            LuaMRescueDoAfterStatus.Cancelled => LuaMNpcActivityActionStatus.Cancelled,
            LuaMRescueDoAfterStatus.Failed => LuaMNpcActivityActionStatus.Failed,
            LuaMRescueDoAfterStatus.TimedOut => LuaMNpcActivityActionStatus.TimedOut,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static LuaMRescueDoAfterStatus ToRescueDoAfterStatus(LuaMNpcActivityActionStatus status)
    {
        return status switch
        {
            LuaMNpcActivityActionStatus.None => LuaMRescueDoAfterStatus.None,
            LuaMNpcActivityActionStatus.Pending => LuaMRescueDoAfterStatus.Pending,
            LuaMNpcActivityActionStatus.Running => LuaMRescueDoAfterStatus.Running,
            LuaMNpcActivityActionStatus.Succeeded => LuaMRescueDoAfterStatus.Succeeded,
            LuaMNpcActivityActionStatus.Cancelled => LuaMRescueDoAfterStatus.Cancelled,
            LuaMNpcActivityActionStatus.Failed => LuaMRescueDoAfterStatus.Failed,
            LuaMNpcActivityActionStatus.TimedOut => LuaMRescueDoAfterStatus.TimedOut,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static string ActivityId(LuaMRescueActivity activity)
    {
        return activity == LuaMRescueActivity.None
            ? LuaMNpcActivityIds.None
            : activity.ToString();
    }

    private static LuaMRescueActivity ParseActivity(string activity)
    {
        return !string.IsNullOrWhiteSpace(activity) &&
               Enum.TryParse<LuaMRescueActivity>(activity, out var parsed)
            ? parsed
            : LuaMRescueActivity.None;
    }

    private static string FailureId(LuaMRescueFailureReason failure)
    {
        return failure == LuaMRescueFailureReason.None
            ? LuaMNpcActivityIds.None
            : failure.ToString();
    }

    private static LuaMRescueFailureReason ParseFailure(string failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
            return LuaMRescueFailureReason.None;

        return Enum.TryParse<LuaMRescueFailureReason>(failure, out var parsed)
            ? parsed
            : LuaMRescueFailureReason.Unknown;
    }

    private static LuaMRescueFailureReason RejectionFailure(
        LuaMNpcActivityRejection rejection,
        LuaMRescueActivity requestedActivity)
    {
        return rejection switch
        {
            LuaMNpcActivityRejection.ActivityNotAllowed => requestedActivity == LuaMRescueActivity.None
                ? LuaMRescueFailureReason.InvalidActivity
                : LuaMRescueFailureReason.RoleDisallowed,
            LuaMNpcActivityRejection.InvalidActivityPolicy => LuaMRescueFailureReason.InvalidActivity,
            LuaMNpcActivityRejection.InvalidTransition => LuaMRescueFailureReason.InvalidTransition,
            LuaMNpcActivityRejection.StaleGeneration => LuaMRescueFailureReason.StaleGeneration,
            LuaMNpcActivityRejection.ActivityNotActive => LuaMRescueFailureReason.ActivityNotActive,
            LuaMNpcActivityRejection.RetryBackoffActive => LuaMRescueFailureReason.RetryBackoffActive,
            LuaMNpcActivityRejection.AttemptLimitReached => LuaMRescueFailureReason.AttemptLimitReached,
            _ => LuaMRescueFailureReason.Unknown,
        };
    }

    private readonly struct RescueActivityPolicyAdapter : ILuaMNpcRoleActivityPolicy
    {
        private readonly LuaMRescueRoleProfile _profile;

        public RescueActivityPolicyAdapter(LuaMRescueRoleProfile profile)
        {
            _profile = profile;
        }

        public string Role => _profile.Role == LuaMRescueRole.None
            ? string.Empty
            : _profile.Role.ToString();
        public string NoneActivity => LuaMNpcActivityIds.None;
        public string DefaultFallbackActivity => ActivityId(LuaMRescueActivity.Standby);
        public int MaxAttempts => _profile.MaxAttempts;
        public TimeSpan BaseRetryBackoff => _profile.BaseRetryBackoff;
        public TimeSpan MaxRetryBackoff => _profile.MaxRetryBackoff;
        public float ProgressTolerance => _profile.ProgressTolerance;

        public bool Allows(string activity)
        {
            return _profile.Allows(ParseActivity(activity));
        }

        public bool TryGetActivityPolicy(
            string activity,
            out LuaMNpcActivityPolicyEntry policy)
        {
            var parsed = ParseActivity(activity);
            if (!_profile.TryGetPolicy(parsed, out var rescuePolicy))
            {
                policy = default;
                return false;
            }

            policy = new LuaMNpcActivityPolicyEntry(
                ActivityId(rescuePolicy.Activity),
                rescuePolicy.Timeout,
                ActivityId(rescuePolicy.TimeoutFallback));
            return true;
        }

        public bool IsTransitionAllowed(string from, string to)
        {
            return LuaMRescueActivityCoordinatorSystem.IsTransitionAllowed(
                ParseActivity(from),
                ParseActivity(to));
        }
    }

    private static LuaMRescueActivitySnapshot Snapshot(EntityUid agent, LuaMRescueAgentComponent rescue)
    {
        var context = rescue.ActivityContext;
        return new LuaMRescueActivitySnapshot(
            agent,
            rescue.ActivityRole,
            context.Activity,
            context.TerminalStatus,
            context.Target,
            context.Destination,
            context.StartedAt,
            context.LastProgressAt,
            context.LastProgressDistance,
            context.Attempts,
            context.Generation,
            context.Blocked,
            context.Fallback,
            context.FailureReason,
            context.RetryNotBefore,
            context.RouteStatus,
            context.DoAfterStatus,
            context.LastTransitionAt,
            context.Deadline);
    }

    private static LuaMRescueActivitySnapshot MissingSnapshot(
        EntityUid agent,
        LuaMRescueFailureReason failureReason)
    {
        return new LuaMRescueActivitySnapshot(
            agent,
            LuaMRescueRole.None,
            LuaMRescueActivity.None,
            LuaMRescueTerminalStatus.None,
            null,
            null,
            TimeSpan.Zero,
            TimeSpan.Zero,
            null,
            0,
            0,
            false,
            LuaMRescueActivity.None,
            failureReason,
            TimeSpan.Zero,
            LuaMRescueRouteStatus.None,
            LuaMRescueDoAfterStatus.None,
            TimeSpan.Zero,
            TimeSpan.Zero);
    }
}
