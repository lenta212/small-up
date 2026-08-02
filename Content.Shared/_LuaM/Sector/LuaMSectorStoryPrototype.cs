using Content.Shared._NF.BountyContracts;
using Robust.Shared.Prototypes;

namespace Content.Shared._LuaM.Sector;

[Prototype("luamSectorStory")]
public sealed partial class LuaMSectorStoryPrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Title = string.Empty;

    [DataField]
    public string Author = "LuaM sector board";

    [DataField(required: true)]
    public string News = string.Empty;

    [DataField]
    public ProtoId<BountyContractCollectionPrototype>? ContractCollection;

    [DataField]
    public BountyContractCategory? ContractCategory;

    [DataField]
    public string ContractName = string.Empty;

    [DataField]
    public string ContractVessel = string.Empty;

    [DataField]
    public string ContractDescription = string.Empty;

    /// <summary>
    /// Physical item created when this contract is accepted. Its runtime evidence
    /// component is bound to the exact contract and accepting character.
    /// </summary>
    [DataField]
    public EntProtoId? ContractObjectivePrototype;

    [DataField]
    public int ContractReward;

    [DataField]
    public string Hazard = string.Empty;

    [DataField]
    public int HazardSeverity = 1;

    [DataField]
    public int HazardRewardBonus;

    [DataField]
    public string ReputationTarget = string.Empty;

    [DataField]
    public int ReputationDelta;

    [DataField]
    public string RequiredReputationTarget = string.Empty;

    [DataField]
    public int RequiredReputation;

    [DataField]
    public string Insurance = string.Empty;

    [DataField]
    public string BlackBox = string.Empty;

    [DataField]
    public string CompanyRecord = string.Empty;

    [DataField]
    public string ShipRecord = string.Empty;
}
