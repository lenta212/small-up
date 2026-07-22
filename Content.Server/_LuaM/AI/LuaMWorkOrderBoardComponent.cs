using System;
using System.Collections.Generic;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;

namespace Content.Server._LuaM.AI;

public enum LuaMWorkOrderState : byte
{
    Open,
    Claimed,
    Completed,
    Cancelled,
    Failed,
}

[Serializable, DataDefinition]
public sealed partial class LuaMWorkOrderLease
{
    [DataField]
    public EntityUid Agent;

    [DataField]
    public TimeSpan ExpiresAt;
}

[Serializable, DataDefinition]
public sealed partial class LuaMWorkOrderAttempt
{
    [DataField]
    public EntityUid Agent;

    [DataField]
    public int Failures;

    [DataField]
    public TimeSpan RetryAt;

    [DataField]
    public string LastFailure = string.Empty;
}

[Serializable, DataDefinition]
public sealed partial class LuaMWorkOrder
{
    [DataField]
    public uint Id;

    [DataField]
    public LuaMBehaviorIntent Intent = LuaMBehaviorIntent.ExecuteWorkOrder;

    [DataField]
    public int Priority = 500;

    [DataField]
    public float Severity = 0.5f;

    [DataField]
    public EntityUid? Target;

    [DataField]
    public EntityCoordinates? Destination;

    [DataField]
    public List<LuaMBehaviorCapability> RequiredCapabilities = new();

    [DataField]
    public TimeSpan CreatedAt;

    [DataField]
    public TimeSpan ExpiresAt;

    [DataField]
    public int MaxAssignees = 1;

    [DataField]
    public LuaMWorkOrderState State;

    [DataField]
    public int Failures;

    [DataField]
    public string LastFailure = string.Empty;

    [DataField]
    public List<LuaMWorkOrderLease> Leases = new();

    [DataField]
    public List<LuaMWorkOrderAttempt> Attempts = new();
}

[RegisterComponent]
public sealed partial class LuaMWorkOrderBoardComponent : Component
{
    [DataField]
    public List<LuaMWorkOrder> Orders = new();

    [DataField]
    public uint NextOrderId = 1;

    [DataField]
    public int MaximumOrders = 128;
}

public readonly record struct LuaMWorkOrderRequest(
    LuaMBehaviorIntent Intent,
    int Priority,
    float Severity,
    EntityUid? Target,
    EntityCoordinates? Destination,
    IReadOnlyList<LuaMBehaviorCapability> RequiredCapabilities,
    TimeSpan Lifetime,
    int MaxAssignees = 1);
