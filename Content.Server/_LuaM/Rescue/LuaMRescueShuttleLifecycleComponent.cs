using System;

namespace Content.Server._LuaM.Rescue;

[RegisterComponent]
public sealed partial class LuaMRescueShuttleLifecycleComponent : Component
{
    [DataField]
    public LuaMRescueShuttleRouteState State;

    [DataField]
    public EntityUid? Target;

    [DataField]
    public EntityUid? AutopilotConsole;

    /// <summary>
    /// The high-level intent owned by this lifecycle. The shuttle HTN only
    /// executes this intent and never chooses between outbound and return work.
    /// </summary>
    [DataField]
    public LuaMRescueActivity RouteActivity = LuaMRescueActivity.Delivering;

    [DataField]
    public LuaMRescueRoleProfile ActivityRoleProfile =
        LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Autopilot);

    [DataField]
    public LuaMRescueActivityContext ActivityContext = new();

    [DataField]
    public LuaMRescueFailureReason RouteFailureReason = LuaMRescueFailureReason.None;

    [DataField]
    public TimeSpan RouteStartedAt;

    [DataField]
    public TimeSpan RouteDeadline;

    [DataField]
    public TimeSpan StateChangedAt;

    [DataField]
    public TimeSpan NextRetryAt;

    [DataField]
    public TimeSpan NextDockingAttemptAt;

    [DataField]
    public float RouteTimeoutSeconds = 180f;

    [DataField]
    public float RetryDelaySeconds = 5f;

    [DataField]
    public float ArrivalRange = 45f;

    [DataField]
    public float DockingRetryDelaySeconds = 2f;

    [DataField]
    public int RetryCount;

    [DataField]
    public int MaxRetries = 2;

    [DataField]
    public int DockingAttemptCount;

    [DataField]
    public int MaxDockingAttempts = 3;

    [DataField]
    public int RouteGeneration;

    [DataField]
    public bool SafeExitConfirmed;

    [DataField]
    public string LastStatus = "none";

    /// <summary>
    /// Per-shuttle MaxRetries may make a route stricter, while the role profile
    /// remains the global upper bound for attempts.
    /// </summary>
    public int EffectiveMaxRetries => Math.Min(
        Math.Max(0, MaxRetries),
        Math.Max(0, ActivityRoleProfile.MaxAttempts - 1));
}

public enum LuaMRescueShuttleRouteState : byte
{
    None,
    Routing,
    Arrived,
    Docked,
    Failed,
    TimedOut,
}

public enum LuaMRescueMedicalSignalKind : byte
{
    Critical,
    Death,
    FollowUp,
}

public readonly record struct LuaMRescueShuttleRouteSnapshot(
    EntityUid Shuttle,
    EntityUid? Target,
    EntityUid? AutopilotConsole,
    LuaMRescueShuttleRouteState State,
    LuaMRescueActivity RouteActivity,
    LuaMRescueRole Role,
    LuaMRescueActivity Activity,
    LuaMRescueTerminalStatus ActivityTerminal,
    uint ActivityGeneration,
    LuaMRescueFailureReason ActivityFailureReason,
    LuaMRescueActivity ActivityFallback,
    float? LastProgressDistance,
    TimeSpan RouteStartedAt,
    TimeSpan RouteDeadline,
    TimeSpan StateChangedAt,
    int RetryCount,
    int MaxRetries,
    int RouteGeneration,
    TimeSpan NextRetryAt,
    int DockingAttemptCount,
    int MaxDockingAttempts,
    TimeSpan NextDockingAttemptAt,
    bool SafeExitConfirmed,
    bool Terminal,
    string LastStatus)
{
    public int Attempt => RetryCount + 1;
}

public readonly record struct LuaMRescuePendingDispatchSnapshot(
    EntityUid Target,
    LuaMRescueMedicalSignalKind Kind,
    bool ManualOverride,
    EntityUid? ManualOverrideOwner,
    uint ManualOverrideGeneration,
    TimeSpan EnqueuedAt,
    TimeSpan NextAttemptAt,
    int Attempts,
    TimeSpan Deadline,
    bool Terminal,
    string LastStatus,
    bool RequiredOnboardHandoff);

public sealed class LuaMRescueShuttleRouteStateChangedEvent : EntityEventArgs
{
    public readonly EntityUid Shuttle;
    public readonly EntityUid? Target;
    public readonly LuaMRescueShuttleRouteState OldState;
    public readonly LuaMRescueShuttleRouteState NewState;
    public readonly LuaMRescueActivity RouteActivity;
    public readonly int RouteGeneration;
    public readonly int Attempt;
    public readonly bool Terminal;
    public readonly string Status;

    public LuaMRescueShuttleRouteStateChangedEvent(
        EntityUid shuttle,
        EntityUid? target,
        LuaMRescueShuttleRouteState oldState,
        LuaMRescueShuttleRouteState newState,
        LuaMRescueActivity routeActivity,
        int routeGeneration,
        int attempt,
        bool terminal,
        string status)
    {
        Shuttle = shuttle;
        Target = target;
        OldState = oldState;
        NewState = newState;
        RouteActivity = routeActivity;
        RouteGeneration = routeGeneration;
        Attempt = attempt;
        Terminal = terminal;
        Status = status;
    }
}

public sealed class LuaMRescueDispatchQueuedEvent : EntityEventArgs
{
    public readonly EntityUid Target;
    public readonly LuaMRescueMedicalSignalKind Kind;
    public readonly bool ManualOverride;
    public readonly int PendingCount;

    public LuaMRescueDispatchQueuedEvent(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        bool manualOverride,
        int pendingCount)
    {
        Target = target;
        Kind = kind;
        ManualOverride = manualOverride;
        PendingCount = pendingCount;
    }
}

/// <summary>
/// Synchronous server-side request used by the activity owner without creating a
/// circular EntitySystem dependency on the shuttle dispatcher.
/// </summary>
[ByRefEvent]
public sealed class LuaMRescueShuttleRouteRequestEvent : EntityEventArgs
{
    public readonly EntityUid Target;
    public readonly LuaMRescueActivity RouteActivity;
    public bool Handled;
    public EntityUid? AutopilotConsole;
    public string Status = "route request was not handled";

    public LuaMRescueShuttleRouteRequestEvent(
        EntityUid target,
        LuaMRescueActivity routeActivity = LuaMRescueActivity.Delivering)
    {
        Target = target;
        RouteActivity = routeActivity;
    }
}
