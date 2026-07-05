using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMDynamicEventSensorDriftComponent : Component
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
    public string DriftLocation = string.Empty;
}
