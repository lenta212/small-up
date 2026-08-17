using Robust.Shared.Utility;
using Content.Shared.Interaction;

namespace Content.Shared.Actions;

[RegisterComponent]
public sealed partial class ActionComponent : Component
{
    [DataField]
    public TimeSpan? UseDelay;

    [DataField]
    public ItemActionIconStyle ItemIconStyle;

    [DataField]
    public bool CheckCanInteract = true;

    [DataField]
    public bool CheckConsciousness = true;

    [DataField]
    public SpriteSpecifier? Icon;
}

[RegisterComponent]
public sealed partial class TargetActionComponent : Component
{
    [DataField]
    public float Range = SharedInteractionSystem.InteractionRange;

    [DataField]
    public bool InteractOnMiss;

    [DataField]
    public bool CheckCanAccess = true;
}
