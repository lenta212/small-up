using Robust.Shared.Prototypes;

namespace Content.Shared._Goobstation.Xenobiology.Components;

[RegisterComponent]
public sealed partial class ClothingCoatingComponent : Component
{
    [DataField]
    public string CoatingName = string.Empty;

    [DataField]
    [AlwaysPushInheritance]
    public ComponentRegistry Components = new();
}
