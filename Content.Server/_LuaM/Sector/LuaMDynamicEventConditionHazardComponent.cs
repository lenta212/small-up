using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMDynamicEventConditionHazardComponent : Component
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string TemplateId = string.Empty;

    [DataField]
    public string ConditionId = string.Empty;

    [DataField]
    public string CreatedBy = string.Empty;

    [DataField]
    public string MarkerLocation = string.Empty;

    [DataField]
    public string RouteCalibrationSource = string.Empty;

    [DataField]
    public int RouteCalibrationChainDepth;

    [DataField]
    public int RouteCalibrationRadiationDamping;
}
