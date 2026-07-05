using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(
    typeof(LuaMSectorEvidenceSystem),
    typeof(LuaMSectorDynamicEventSystem),
    typeof(LuaMSectorLeadReportSystem),
    typeof(LuaMSectorInsuranceTerminalSystem),
    typeof(LuaMSectorRegistryTerminalSystem))]
public sealed partial class LuaMSectorEvidenceComponent : Component
{
    [DataField(required: true)]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public bool AcknowledgeHazard;

    [DataField]
    public bool ClaimInsurance;

    [DataField]
    public bool RecoverBlackBox;

    [DataField]
    public bool RegisterCompany;

    [DataField]
    public bool RegisterShip;

    [DataField]
    public bool ResolveStory;

    [DataField]
    public string Note = string.Empty;
}
