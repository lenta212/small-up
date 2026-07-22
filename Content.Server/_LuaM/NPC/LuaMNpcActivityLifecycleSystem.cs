using System;
using Content.Shared._LuaM.NPC;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.NPC;

/// <summary>
/// Reusable, deterministic intent lifecycle for sector NPC roles. The detached
/// methods are also used by legacy role coordinators during incremental migration.
/// </summary>
public sealed class LuaMNpcActivityLifecycleSystem : EntitySystem
{
    public const string DeadlineExceededFailure = "deadline-exceeded";
    public const string AttemptLimitReachedFailure = "attempt-limit-reached";

    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMNpcActivityComponent>();
        while (query.MoveNext(out _, out var carrier))
        {
            if (!TryResolvePolicy(carrier, out var policy))
            {
                carrier.LastLifecycleStatus = "missing role activity prototype";
                continue;
            }

            if (!ExpireIfDue(carrier.Context, policy, now, DeadlineExceededFailure))
                continue;

            carrier.LastLifecycleStatus =
                $"{carrier.Context.Activity} failed: {carrier.Context.Failure}";
        }
    }

    public bool BeginOrReplaceIntent(
        EntityUid actor,
        string activity,
        EntityUid? target,
        EntityCoordinates? destination,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = BeginOrReplaceIntent(
            carrier.Context,
            policy,
            activity,
            target,
            destination,
            _timing.CurTime,
            out rejection);
        carrier.LastLifecycleStatus = result
            ? $"intent {activity}@g{carrier.Context.Generation}"
            : $"intent rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool RecordAttempt(
        EntityUid actor,
        uint? expectedGeneration,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = RecordAttempt(
            carrier.Context,
            policy,
            expectedGeneration,
            _timing.CurTime,
            AttemptLimitReachedFailure,
            out rejection);
        carrier.LastLifecycleStatus = result
            ? $"attempt {carrier.Context.Attempts}/{Math.Max(1, policy.AttemptLimit)}"
            : $"attempt rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool CanAttempt(
        EntityUid actor,
        uint? expectedGeneration,
        out TimeSpan retryAfter,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        retryAfter = TimeSpan.Zero;
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = CanAttempt(
            carrier.Context,
            policy,
            expectedGeneration,
            _timing.CurTime,
            out retryAfter,
            out rejection);
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool TryRecoverBlockedIntent(
        EntityUid actor,
        uint expectedGeneration,
        string activity,
        EntityUid? target,
        EntityCoordinates? destination,
        out TimeSpan retryAfter,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        retryAfter = TimeSpan.Zero;
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = TryRecoverBlockedIntent(
            carrier.Context,
            policy,
            expectedGeneration,
            activity,
            target,
            destination,
            _timing.CurTime,
            out retryAfter,
            out rejection);
        carrier.LastLifecycleStatus = result
            ? $"recovered {activity}@g{carrier.Context.Generation}"
            : $"recovery rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool RecordProgress(
        EntityUid actor,
        uint? expectedGeneration,
        EntityCoordinates? destination,
        float? remainingDistance,
        LuaMNpcActivityRouteStatus routeStatus,
        LuaMNpcActivityActionStatus actionStatus,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = RecordProgress(
            carrier.Context,
            policy,
            expectedGeneration,
            destination,
            remainingDistance,
            routeStatus,
            actionStatus,
            _timing.CurTime,
            out rejection);
        carrier.LastLifecycleStatus = result ? "progress recorded" : $"progress rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool Complete(
        EntityUid actor,
        uint? expectedGeneration,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = Complete(carrier.Context, expectedGeneration, _timing.CurTime, out rejection);
        carrier.LastLifecycleStatus = result ? "intent succeeded" : $"completion rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool Cancel(
        EntityUid actor,
        uint? expectedGeneration,
        string failure,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = Cancel(
            carrier.Context,
            expectedGeneration,
            failure,
            _timing.CurTime,
            out rejection);
        carrier.LastLifecycleStatus = result ? "intent cancelled" : $"cancellation rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool Block(
        EntityUid actor,
        uint? expectedGeneration,
        string failure,
        string fallback,
        LuaMNpcActivityRouteStatus? routeStatus,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = Block(
            carrier.Context,
            policy,
            expectedGeneration,
            failure,
            fallback,
            routeStatus,
            _timing.CurTime,
            out rejection);
        carrier.LastLifecycleStatus = result ? $"blocked: {failure}" : $"block rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool Fail(
        EntityUid actor,
        uint? expectedGeneration,
        string failure,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryGetCarrier(actor, out var carrier, out var policy, out snapshot, out rejection))
            return false;

        var result = Fail(carrier.Context, expectedGeneration, failure, _timing.CurTime, out rejection);
        carrier.LastLifecycleStatus = result ? $"failed: {failure}" : $"failure rejected: {rejection}";
        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return result;
    }

    public bool GetSnapshot(EntityUid actor, out LuaMNpcActivitySnapshot snapshot)
    {
        if (!TryComp<LuaMNpcActivityComponent>(actor, out var carrier) ||
            !TryResolvePolicy(carrier, out var policy))
        {
            snapshot = MissingSnapshot(actor);
            return false;
        }

        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        return true;
    }

    /// <summary>
    /// Starts a new intent in detached state. Repeating the exact active intent
    /// is idempotent and does not increment its generation.
    /// </summary>
    public bool BeginOrReplaceIntent(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        string activity,
        EntityUid? target,
        EntityCoordinates? destination,
        TimeSpan now,
        out LuaMNpcActivityRejection rejection)
    {
        rejection = LuaMNpcActivityRejection.None;
        if (!policy.Allows(activity))
        {
            rejection = LuaMNpcActivityRejection.ActivityNotAllowed;
            return false;
        }

        if (!policy.TryGetActivityPolicy(activity, out var activityPolicy) ||
            activityPolicy.Timeout <= TimeSpan.Zero ||
            !policy.Allows(activityPolicy.TimeoutFallback))
        {
            rejection = LuaMNpcActivityRejection.InvalidActivityPolicy;
            return false;
        }

        var sameIntent = state.TerminalStatus == LuaMNpcActivityTerminalStatus.Active &&
                         string.Equals(state.Activity, activity, StringComparison.Ordinal) &&
                         state.Target == target &&
                         Nullable.Equals(state.Destination, destination);
        if (sameIntent)
            return true;

        // Activity policy owns the state-machine edge independently of which
        // entity happens to be the current target. Otherwise an executor could
        // bypass a forbidden transition simply by replacing the target at the
        // same time (for example Standby(A) -> Handoff(B)).
        if (state.TerminalStatus == LuaMNpcActivityTerminalStatus.Active &&
            !policy.IsTransitionAllowed(state.Activity, activity))
        {
            rejection = LuaMNpcActivityRejection.InvalidTransition;
            return false;
        }

        var generation = NextGeneration(state.Generation);
        state.Activity = activity;
        state.TerminalStatus = LuaMNpcActivityTerminalStatus.Active;
        state.Target = target;
        state.Destination = destination;
        state.StartedAt = now;
        state.LastProgressAt = now;
        state.LastProgressDistance = null;
        state.Attempts = 0;
        state.Generation = generation;
        state.Blocked = false;
        state.Fallback = activityPolicy.TimeoutFallback;
        state.Failure = LuaMNpcActivityIds.None;
        state.RetryNotBefore = TimeSpan.Zero;
        state.RouteStatus = destination == null
            ? LuaMNpcActivityRouteStatus.None
            : LuaMNpcActivityRouteStatus.Pending;
        state.ActionStatus = LuaMNpcActivityActionStatus.None;
        state.LastTransitionAt = now;
        state.Deadline = AddDuration(now, activityPolicy.Timeout);
        return true;
    }

    public bool ExpireIfDue(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        TimeSpan now,
        string failure)
    {
        if (state.TerminalStatus != LuaMNpcActivityTerminalStatus.Active ||
            state.Deadline == TimeSpan.Zero ||
            now < state.Deadline)
        {
            return false;
        }

        state.TerminalStatus = LuaMNpcActivityTerminalStatus.Failed;
        state.Failure = failure;
        state.Blocked = false;
        state.Fallback = ResolveAllowedFallback(policy, state.Fallback);
        state.RetryNotBefore = TimeSpan.Zero;
        state.LastTransitionAt = now;
        return true;
    }

    public bool RecordAttempt(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        uint? expectedGeneration,
        TimeSpan now,
        string attemptLimitFailure,
        out LuaMNpcActivityRejection rejection)
    {
        if (!ValidateActive(state, expectedGeneration, out rejection))
            return false;

        var maxAttempts = Math.Max(1, policy.MaxAttempts);
        if (state.Attempts >= maxAttempts)
        {
            SetTerminal(state, LuaMNpcActivityTerminalStatus.Failed, attemptLimitFailure);
            state.LastTransitionAt = now;
            rejection = LuaMNpcActivityRejection.AttemptLimitReached;
            return false;
        }

        if (state.Blocked && state.RetryNotBefore > now)
        {
            rejection = LuaMNpcActivityRejection.RetryBackoffActive;
            return false;
        }

        state.Attempts++;
        state.Blocked = false;
        state.Failure = LuaMNpcActivityIds.None;
        state.RetryNotBefore = TimeSpan.Zero;
        state.LastTransitionAt = now;

        if (state.Attempts < maxAttempts)
            return true;

        SetTerminal(state, LuaMNpcActivityTerminalStatus.Failed, attemptLimitFailure);
        rejection = LuaMNpcActivityRejection.AttemptLimitReached;
        return false;
    }

    public bool CanAttempt(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        uint? expectedGeneration,
        TimeSpan now,
        out TimeSpan retryAfter,
        out LuaMNpcActivityRejection rejection)
    {
        retryAfter = TimeSpan.Zero;
        if (expectedGeneration != null && state.Generation != expectedGeneration.Value)
        {
            rejection = LuaMNpcActivityRejection.StaleGeneration;
            return false;
        }

        var maxAttempts = Math.Max(1, policy.MaxAttempts);
        var exhausted = state.TerminalStatus == LuaMNpcActivityTerminalStatus.Blocked
            ? state.Attempts >= maxAttempts - 1
            : state.Attempts >= maxAttempts;
        if (exhausted)
        {
            rejection = LuaMNpcActivityRejection.AttemptLimitReached;
            return false;
        }

        // Blocked is a retryable lifecycle state, not an unobservable terminal.
        // Callers use CanAttempt to display the remaining backoff before they
        // invoke TryRecoverBlockedIntent to create the next generation.
        if (state.TerminalStatus == LuaMNpcActivityTerminalStatus.Blocked)
        {
            if (state.RetryNotBefore <= now)
            {
                rejection = LuaMNpcActivityRejection.None;
                return true;
            }

            retryAfter = state.RetryNotBefore - now;
            rejection = LuaMNpcActivityRejection.RetryBackoffActive;
            return false;
        }

        if (state.TerminalStatus != LuaMNpcActivityTerminalStatus.Active)
        {
            rejection = LuaMNpcActivityRejection.ActivityNotActive;
            return false;
        }

        if (!state.Blocked || state.RetryNotBefore <= now)
        {
            rejection = LuaMNpcActivityRejection.None;
            return true;
        }

        retryAfter = state.RetryNotBefore - now;
        rejection = LuaMNpcActivityRejection.RetryBackoffActive;
        return false;
    }

    public bool TryRecoverBlockedIntent(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        uint expectedGeneration,
        string activity,
        EntityUid? target,
        EntityCoordinates? destination,
        TimeSpan now,
        out TimeSpan retryAfter,
        out LuaMNpcActivityRejection rejection)
    {
        retryAfter = TimeSpan.Zero;
        if (state.Generation != expectedGeneration)
        {
            rejection = LuaMNpcActivityRejection.StaleGeneration;
            return false;
        }

        rejection = LuaMNpcActivityRejection.ActivityNotActive;
        if (state.TerminalStatus != LuaMNpcActivityTerminalStatus.Blocked ||
            !string.Equals(state.Activity, activity, StringComparison.Ordinal) ||
            state.Target != target ||
            !Nullable.Equals(state.Destination, destination))
        {
            return false;
        }

        if (!policy.TryGetActivityPolicy(activity, out var activityPolicy))
        {
            rejection = LuaMNpcActivityRejection.InvalidActivityPolicy;
            return false;
        }

        var maxAttempts = Math.Max(1, policy.MaxAttempts);
        if (state.Attempts >= maxAttempts - 1)
        {
            state.Attempts = Math.Max(state.Attempts, maxAttempts);
            state.RetryNotBefore = TimeSpan.Zero;
            rejection = LuaMNpcActivityRejection.AttemptLimitReached;
            return false;
        }

        if (state.RetryNotBefore > now)
        {
            retryAfter = state.RetryNotBefore - now;
            rejection = LuaMNpcActivityRejection.RetryBackoffActive;
            return false;
        }

        var failedExecutions = state.Attempts + 1;
        state.TerminalStatus = LuaMNpcActivityTerminalStatus.Active;
        state.Blocked = false;
        state.Failure = LuaMNpcActivityIds.None;
        state.Attempts = failedExecutions;
        state.Generation = NextGeneration(state.Generation);
        state.StartedAt = now;
        state.LastProgressAt = now;
        state.LastProgressDistance = null;
        state.LastTransitionAt = now;
        state.Deadline = AddDuration(now, activityPolicy.Timeout);
        state.RetryNotBefore = TimeSpan.Zero;
        state.RouteStatus = destination == null
            ? LuaMNpcActivityRouteStatus.None
            : LuaMNpcActivityRouteStatus.Pending;
        state.ActionStatus = LuaMNpcActivityActionStatus.None;
        rejection = LuaMNpcActivityRejection.None;
        return true;
    }

    public bool RecordProgress(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        uint? expectedGeneration,
        EntityCoordinates? destination,
        float? remainingDistance,
        LuaMNpcActivityRouteStatus routeStatus,
        LuaMNpcActivityActionStatus actionStatus,
        TimeSpan now,
        out LuaMNpcActivityRejection rejection)
    {
        if (!ValidateActive(state, expectedGeneration, out rejection))
            return false;

        if (remainingDistance is < 0f)
            remainingDistance = 0f;

        var madeProgress = remainingDistance is { } distance &&
                           (state.LastProgressDistance == null ||
                            distance + Math.Max(0f, policy.ProgressTolerance) < state.LastProgressDistance.Value);

        state.Destination = destination ?? state.Destination;
        state.RouteStatus = routeStatus;
        state.ActionStatus = actionStatus;
        state.LastTransitionAt = now;

        if (madeProgress ||
            routeStatus == LuaMNpcActivityRouteStatus.Arrived ||
            actionStatus == LuaMNpcActivityActionStatus.Succeeded)
        {
            state.LastProgressAt = now;
            state.LastProgressDistance = remainingDistance;
            state.Blocked = false;
            state.Failure = LuaMNpcActivityIds.None;
            state.RetryNotBefore = TimeSpan.Zero;
        }

        return true;
    }

    public bool Complete(
        LuaMNpcActivityState state,
        uint? expectedGeneration,
        TimeSpan now,
        out LuaMNpcActivityRejection rejection)
    {
        if (!ValidateActive(state, expectedGeneration, out rejection))
            return false;

        SetTerminal(state, LuaMNpcActivityTerminalStatus.Succeeded, LuaMNpcActivityIds.None);
        state.LastTransitionAt = now;
        return true;
    }

    public bool Cancel(
        LuaMNpcActivityState state,
        uint? expectedGeneration,
        string failure,
        TimeSpan now,
        out LuaMNpcActivityRejection rejection)
    {
        if (!ValidateActive(state, expectedGeneration, out rejection))
            return false;

        SetTerminal(state, LuaMNpcActivityTerminalStatus.Cancelled, failure);
        state.LastTransitionAt = now;
        return true;
    }

    public bool Block(
        LuaMNpcActivityState state,
        ILuaMNpcRoleActivityPolicy policy,
        uint? expectedGeneration,
        string failure,
        string fallback,
        LuaMNpcActivityRouteStatus? routeStatus,
        TimeSpan now,
        out LuaMNpcActivityRejection rejection)
    {
        if (!ValidateActive(state, expectedGeneration, out rejection))
            return false;

        if (string.IsNullOrWhiteSpace(failure))
        {
            rejection = LuaMNpcActivityRejection.InvalidFailure;
            return false;
        }

        if (!string.Equals(fallback, policy.NoneActivity, StringComparison.Ordinal) &&
            !policy.Allows(fallback))
        {
            rejection = LuaMNpcActivityRejection.InvalidFallback;
            return false;
        }

        state.TerminalStatus = LuaMNpcActivityTerminalStatus.Blocked;
        state.Blocked = true;
        state.Failure = failure;
        state.Fallback = fallback;
        state.RetryNotBefore = AddDuration(now, CalculateBackoff(policy, NextAttemptOrdinal(state.Attempts)));
        state.LastTransitionAt = now;
        if (routeStatus != null)
            state.RouteStatus = routeStatus.Value;
        return true;
    }

    public bool Fail(
        LuaMNpcActivityState state,
        uint? expectedGeneration,
        string failure,
        TimeSpan now,
        out LuaMNpcActivityRejection rejection)
    {
        if (!ValidateActive(state, expectedGeneration, out rejection))
            return false;

        if (string.IsNullOrWhiteSpace(failure))
        {
            rejection = LuaMNpcActivityRejection.InvalidFailure;
            return false;
        }

        SetTerminal(state, LuaMNpcActivityTerminalStatus.Failed, failure);
        state.LastTransitionAt = now;
        return true;
    }

    public bool ValidateActive(
        LuaMNpcActivityState state,
        uint? expectedGeneration,
        out LuaMNpcActivityRejection rejection)
    {
        if (expectedGeneration != null && state.Generation != expectedGeneration.Value)
        {
            rejection = LuaMNpcActivityRejection.StaleGeneration;
            return false;
        }

        if (state.TerminalStatus != LuaMNpcActivityTerminalStatus.Active)
        {
            rejection = LuaMNpcActivityRejection.ActivityNotActive;
            return false;
        }

        rejection = LuaMNpcActivityRejection.None;
        return true;
    }

    public LuaMNpcActivitySnapshot Snapshot(
        EntityUid actor,
        string role,
        LuaMNpcActivityState state)
    {
        return new LuaMNpcActivitySnapshot(
            actor,
            role,
            state.Activity,
            state.TerminalStatus,
            state.Target,
            state.Destination,
            state.StartedAt,
            state.LastProgressAt,
            state.LastProgressDistance,
            state.Attempts,
            state.Generation,
            state.Blocked,
            state.Fallback,
            state.Failure,
            state.RetryNotBefore,
            state.RouteStatus,
            state.ActionStatus,
            state.LastTransitionAt,
            state.Deadline);
    }

    private bool TryGetCarrier(
        EntityUid actor,
        out LuaMNpcActivityComponent carrier,
        out LuaMNpcRoleActivityPrototype policy,
        out LuaMNpcActivitySnapshot snapshot,
        out LuaMNpcActivityRejection rejection)
    {
        if (!TryComp<LuaMNpcActivityComponent>(actor, out var foundCarrier))
        {
            carrier = default!;
            policy = default!;
            snapshot = MissingSnapshot(actor);
            rejection = LuaMNpcActivityRejection.MissingComponent;
            return false;
        }

        carrier = foundCarrier;

        if (!TryResolvePolicy(carrier, out policy))
        {
            snapshot = MissingSnapshot(actor);
            rejection = LuaMNpcActivityRejection.MissingRolePolicy;
            return false;
        }

        snapshot = Snapshot(actor, policy.RoleId, carrier.Context);
        rejection = LuaMNpcActivityRejection.None;
        return true;
    }

    private bool TryResolvePolicy(
        LuaMNpcActivityComponent carrier,
        out LuaMNpcRoleActivityPrototype policy)
    {
        if (!_prototypes.TryIndex(carrier.RoleProfile, out LuaMNpcRoleActivityPrototype? foundPolicy))
        {
            policy = default!;
            return false;
        }

        policy = foundPolicy;
        return true;
    }

    private static TimeSpan CalculateBackoff(ILuaMNpcRoleActivityPolicy policy, int attempts)
    {
        var baseTicks = Math.Max(0L, policy.BaseRetryBackoff.Ticks);
        var maxTicks = Math.Max(baseTicks, policy.MaxRetryBackoff.Ticks);
        var ticks = baseTicks;
        for (var i = 1; i < Math.Max(1, attempts) && ticks < maxTicks; i++)
            ticks = Math.Min(maxTicks, ticks > maxTicks / 2 ? maxTicks : ticks * 2);

        return TimeSpan.FromTicks(ticks);
    }

    private static int NextAttemptOrdinal(int attempts)
    {
        return attempts >= int.MaxValue
            ? int.MaxValue
            : Math.Max(0, attempts) + 1;
    }

    private static string ResolveAllowedFallback(
        ILuaMNpcRoleActivityPolicy policy,
        string requested)
    {
        if (policy.Allows(requested))
            return requested;

        return policy.Allows(policy.DefaultFallbackActivity)
            ? policy.DefaultFallbackActivity
            : policy.NoneActivity;
    }

    private static TimeSpan AddDuration(TimeSpan now, TimeSpan duration)
    {
        var safeDurationTicks = Math.Max(0L, duration.Ticks);
        var safeNowTicks = Math.Max(0L, now.Ticks);
        var remainingTicks = TimeSpan.MaxValue.Ticks - safeNowTicks;
        return TimeSpan.FromTicks(safeNowTicks + Math.Min(safeDurationTicks, remainingTicks));
    }

    private static void SetTerminal(
        LuaMNpcActivityState state,
        LuaMNpcActivityTerminalStatus terminalStatus,
        string failure)
    {
        state.TerminalStatus = terminalStatus;
        state.Failure = failure;
        state.Blocked = false;
        state.Fallback = LuaMNpcActivityIds.None;
        state.RetryNotBefore = TimeSpan.Zero;
    }

    private static uint NextGeneration(uint generation)
    {
        var next = unchecked(generation + 1);
        return next == 0 ? 1u : next;
    }

    private static LuaMNpcActivitySnapshot MissingSnapshot(EntityUid actor)
    {
        return new LuaMNpcActivitySnapshot(
            actor,
            string.Empty,
            LuaMNpcActivityIds.None,
            LuaMNpcActivityTerminalStatus.None,
            null,
            null,
            TimeSpan.Zero,
            TimeSpan.Zero,
            null,
            0,
            0,
            false,
            LuaMNpcActivityIds.None,
            LuaMNpcActivityIds.None,
            TimeSpan.Zero,
            LuaMNpcActivityRouteStatus.None,
            LuaMNpcActivityActionStatus.None,
            TimeSpan.Zero,
            TimeSpan.Zero);
    }
}
