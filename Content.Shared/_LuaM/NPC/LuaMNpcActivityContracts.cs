using System;
using System.Collections.Generic;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Shared._LuaM.NPC;

/// <summary>
/// Stable identifiers used by the sector NPC activity kernel. Role packages are
/// free to add their own identifiers; the kernel only reserves the empty value.
/// </summary>
public static class LuaMNpcActivityIds
{
    public const string None = "";
    public const string Standby = "standby";
}

public enum LuaMNpcActivityTerminalStatus : byte
{
    None,
    Active,
    Blocked,
    Succeeded,
    Failed,
    Cancelled,
}

public enum LuaMNpcActivityRouteStatus : byte
{
    None,
    Pending,
    Planning,
    Moving,
    Arrived,
    Blocked,
    NoPath,
    InvalidDestination,
}

public enum LuaMNpcActivityActionStatus : byte
{
    None,
    Pending,
    Running,
    Succeeded,
    Cancelled,
    Failed,
    TimedOut,
}

/// <summary>
/// Why a lifecycle command itself was rejected. Domain failures such as
/// "NoMedicine" or "CargoMissing" remain data-driven strings in the state.
/// </summary>
public enum LuaMNpcActivityRejection : byte
{
    None,
    MissingComponent,
    MissingRolePolicy,
    ActivityNotAllowed,
    InvalidActivityPolicy,
    InvalidTransition,
    StaleGeneration,
    ActivityNotActive,
    RetryBackoffActive,
    AttemptLimitReached,
    InvalidFailure,
    InvalidFallback,
}

/// <summary>
/// Serializable activity state shared by arbitrary sector NPC roles. It owns
/// intent lifecycle only; movement, interactions and target selection remain in
/// role executors and report their progress back to this state machine.
/// </summary>
[Serializable, DataDefinition]
public sealed partial class LuaMNpcActivityState
{
    [DataField]
    public string Activity = LuaMNpcActivityIds.None;

    [DataField]
    public LuaMNpcActivityTerminalStatus TerminalStatus;

    [DataField]
    public EntityUid? Target;

    [DataField]
    public EntityCoordinates? Destination;

    [DataField]
    public TimeSpan StartedAt;

    [DataField]
    public TimeSpan LastProgressAt;

    [DataField]
    public float? LastProgressDistance;

    [DataField]
    public int Attempts;

    [DataField]
    public uint Generation;

    [DataField]
    public bool Blocked;

    [DataField]
    public string Fallback = LuaMNpcActivityIds.None;

    [DataField]
    public string Failure = LuaMNpcActivityIds.None;

    [DataField]
    public TimeSpan RetryNotBefore;

    [DataField]
    public LuaMNpcActivityRouteStatus RouteStatus;

    [DataField]
    public LuaMNpcActivityActionStatus ActionStatus;

    [DataField]
    public TimeSpan LastTransitionAt;

    [DataField]
    public TimeSpan Deadline;
}

public readonly record struct LuaMNpcActivityPolicyEntry(
    string Activity,
    TimeSpan Timeout,
    string TimeoutFallback);

/// <summary>
/// Minimal policy contract consumed by the lifecycle kernel. Existing roles can
/// adapt their current policy object; new roles normally use a prototype below.
/// </summary>
public interface ILuaMNpcRoleActivityPolicy
{
    string Role { get; }
    string NoneActivity { get; }
    string DefaultFallbackActivity { get; }
    int MaxAttempts { get; }
    TimeSpan BaseRetryBackoff { get; }
    TimeSpan MaxRetryBackoff { get; }
    float ProgressTolerance { get; }

    bool Allows(string activity);
    bool TryGetActivityPolicy(string activity, out LuaMNpcActivityPolicyEntry policy);
    bool IsTransitionAllowed(string from, string to);
}

[Serializable, DataDefinition]
public sealed partial class LuaMNpcActivityPrototypePolicy
{
    [DataField(required: true)]
    public string Activity = LuaMNpcActivityIds.None;

    [DataField]
    public float TimeoutSeconds = 30f;

    [DataField]
    public string TimeoutFallback = LuaMNpcActivityIds.Standby;
}

[Serializable, DataDefinition]
public sealed partial class LuaMNpcActivityTransition
{
    [DataField(required: true)]
    public string From = LuaMNpcActivityIds.None;

    [DataField(required: true)]
    public string To = LuaMNpcActivityIds.None;
}

/// <summary>
/// Data-driven role lifecycle. This is deliberately independent from rescue,
/// cargo, maintenance or any other executor vocabulary.
/// </summary>
[Prototype("luamNpcRoleActivity")]
public sealed partial class LuaMNpcRoleActivityPrototype : IPrototype, ILuaMNpcRoleActivityPolicy
{
    [ViewVariables]
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string RoleId = string.Empty;

    [DataField(required: true)]
    public List<string> AllowedActivities = new();

    [DataField(required: true)]
    public List<LuaMNpcActivityPrototypePolicy> ActivityPolicies = new();

    [DataField]
    public List<LuaMNpcActivityTransition> Transitions = new();

    /// <summary>
    /// Activities that may be entered from every activity in the role. This is
    /// useful for universal standby/cancellation/return paths without duplicating
    /// a complete transition matrix in YAML.
    /// </summary>
    [DataField]
    public List<string> UniversalTransitionTargets = new();

    [DataField]
    public string DefaultFallback = LuaMNpcActivityIds.Standby;

    [DataField]
    public int AttemptLimit = 3;

    [DataField]
    public float BaseRetryBackoffSeconds = 1f;

    [DataField]
    public float MaxRetryBackoffSeconds = 30f;

    [DataField]
    public float DistanceProgressTolerance = 0.25f;

    string ILuaMNpcRoleActivityPolicy.Role => RoleId;
    string ILuaMNpcRoleActivityPolicy.NoneActivity => LuaMNpcActivityIds.None;
    string ILuaMNpcRoleActivityPolicy.DefaultFallbackActivity => DefaultFallback;
    int ILuaMNpcRoleActivityPolicy.MaxAttempts => AttemptLimit;
    TimeSpan ILuaMNpcRoleActivityPolicy.BaseRetryBackoff =>
        TimeSpan.FromSeconds(Math.Max(0f, BaseRetryBackoffSeconds));
    TimeSpan ILuaMNpcRoleActivityPolicy.MaxRetryBackoff =>
        TimeSpan.FromSeconds(Math.Max(BaseRetryBackoffSeconds, MaxRetryBackoffSeconds));
    float ILuaMNpcRoleActivityPolicy.ProgressTolerance => DistanceProgressTolerance;

    public bool Allows(string activity)
    {
        return !string.IsNullOrWhiteSpace(activity) && AllowedActivities.Contains(activity);
    }

    public bool TryGetActivityPolicy(string activity, out LuaMNpcActivityPolicyEntry policy)
    {
        foreach (var candidate in ActivityPolicies)
        {
            if (!string.Equals(candidate.Activity, activity, StringComparison.Ordinal))
                continue;

            policy = new LuaMNpcActivityPolicyEntry(
                candidate.Activity,
                TimeSpan.FromSeconds(Math.Max(0f, candidate.TimeoutSeconds)),
                candidate.TimeoutFallback);
            return true;
        }

        policy = default;
        return false;
    }

    public bool IsTransitionAllowed(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal) ||
            string.Equals(from, LuaMNpcActivityIds.None, StringComparison.Ordinal) ||
            UniversalTransitionTargets.Contains(to))
        {
            return true;
        }

        foreach (var transition in Transitions)
        {
            if (string.Equals(transition.From, from, StringComparison.Ordinal) &&
                string.Equals(transition.To, to, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

[Serializable]
public readonly record struct LuaMNpcActivitySnapshot(
    EntityUid Actor,
    string Role,
    string Activity,
    LuaMNpcActivityTerminalStatus TerminalStatus,
    EntityUid? Target,
    EntityCoordinates? Destination,
    TimeSpan StartedAt,
    TimeSpan LastProgressAt,
    float? LastProgressDistance,
    int Attempts,
    uint Generation,
    bool Blocked,
    string Fallback,
    string Failure,
    TimeSpan RetryNotBefore,
    LuaMNpcActivityRouteStatus RouteStatus,
    LuaMNpcActivityActionStatus ActionStatus,
    TimeSpan LastTransitionAt,
    TimeSpan Deadline)
{
    public bool IsTerminal => TerminalStatus is LuaMNpcActivityTerminalStatus.Blocked
        or LuaMNpcActivityTerminalStatus.Succeeded
        or LuaMNpcActivityTerminalStatus.Failed
        or LuaMNpcActivityTerminalStatus.Cancelled;
}
