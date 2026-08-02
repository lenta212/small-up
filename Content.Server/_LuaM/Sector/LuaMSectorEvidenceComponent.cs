using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(
    typeof(LuaMSectorEvidenceSystem),
    typeof(LuaMSectorContractObjectiveSystem),
    typeof(LuaMSectorDynamicEventSystem),
    typeof(LuaMSectorTrafficSystem),
    typeof(LuaMSectorLeadReportSystem),
    typeof(LuaMSectorInsuranceTerminalSystem),
    typeof(LuaMSectorRegistryTerminalSystem))]
public sealed partial class LuaMSectorEvidenceComponent : Component
{
    [DataField(required: true)]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    /// <summary>
    /// Exact generated contract this physical evidence instance belongs to.
    /// Prototype/static paperwork intentionally leaves this unset.
    /// </summary>
    [DataField]
    public uint? ContractId;

    /// <summary>
    /// Exact character authorized to file this generated objective item.
    /// </summary>
    [DataField]
    public EntityUid AuthorizedActor = EntityUid.Invalid;

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
    public bool ClearRescueFollowUp;

    [DataField]
    public bool ResolveStory;

    /// <summary>
    /// Requires the operator to carry this evidence back to a physical LuaM
    /// sector terminal before filing it. Existing evidence remains unchanged.
    /// </summary>
    [DataField]
    public bool RequireSectorTerminal;

    [DataField]
    public string Note = string.Empty;
}
