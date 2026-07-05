namespace Content.Server._LuaM.Rescue;

[RegisterComponent]
public sealed partial class LuaMRescueAgentComponent : Component
{
    [DataField]
    public EntityUid? AssignedTarget;

    [DataField]
    public EntityUid? AssignedShuttle;

    [DataField]
    public EntityUid? AssignedShuttleAnchor;

    [DataField]
    public EntityUid? EvacuatingTarget;

    [DataField]
    public bool AutoAcquireTargets = true;

    [DataField]
    public bool EvacuateTargetsToShuttle = true;

    [DataField]
    public float SearchRange = 32f;

    [DataField]
    public float TargetRefreshInterval = 2f;

    [DataField]
    public float FollowCloseRange = 1.25f;

    [DataField]
    public float FollowRange = 4f;

    [DataField]
    public float EvacuationStartRange = 1.5f;

    [DataField]
    public float EvacuationArrivalRange = 3f;

    [DataField]
    public float EvacuationMinDamage = 50f;

    public float TargetRefreshAccumulator;

    [DataField]
    public string Role = "rescue";
}
