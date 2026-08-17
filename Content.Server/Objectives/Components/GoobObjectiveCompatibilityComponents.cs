using Content.Server.Objectives.Systems;

namespace Content.Server.Objectives.Components;

[RegisterComponent, Access(typeof(KillPersonConditionSystem))]
public sealed partial class PickRandomTraitorComponent : Component;

[RegisterComponent]
public sealed partial class RoleplayObjectiveComponent : Component;

[RegisterComponent, Access(typeof(GoobObjectiveCompatibilitySystem))]
public sealed partial class BlobCaptureConditionComponent : Component
{
    [DataField]
    public int Target;
}

[RegisterComponent, Access(typeof(GoobObjectiveCompatibilitySystem))]
public sealed partial class SignContractConditionComponent : Component;

[RegisterComponent, Access(typeof(GoobObjectiveCompatibilitySystem))]
public sealed partial class MeetContractWeightConditionComponent : Component;

[RegisterComponent, Access(typeof(GoobObjectiveCompatibilitySystem))]
public sealed partial class DetonateNukeConditionComponent : Component;

[RegisterComponent]
public sealed partial class RandomTraitorTargetComponent : Component;
