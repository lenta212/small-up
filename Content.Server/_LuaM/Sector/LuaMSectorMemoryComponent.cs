using Content.Shared._LuaM.Sector;
using Content.Shared._NF.BountyContracts;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorStorySystem))]
public sealed partial class LuaMSectorMemoryComponent : Component
{
    [DataField]
    public bool StoriesLoaded;

    [DataField]
    public bool NewsSeeded;

    [DataField]
    public bool ContractsSeeded;

    [DataField]
    public bool PersistenceLoaded;

    [DataField]
    public List<LuaMSectorStoryRecord> Records = new();

    [DataField]
    public Dictionary<string, int> ReputationLedger = new();

    [DataField]
    public List<LuaMSectorReputationLedgerEntry> ReputationEntries = new();

    [DataField]
    public List<LuaMSectorHazardReportEntry> HazardReports = new();

    [DataField]
    public List<LuaMSectorInsurancePayoutEntry> InsurancePayouts = new();

    [DataField]
    public List<LuaMSectorBlackBoxRecoveryEntry> BlackBoxRecoveries = new();

    [DataField]
    public List<LuaMSectorRegistryEntry> CompanyRegistry = new();

    [DataField]
    public List<LuaMSectorRegistryEntry> ShipRegistry = new();

    [DataField]
    public List<LuaMSectorConditionEntry> SectorConditions = new();

    [DataField]
    public LuaMAiBaseState AiBase = new();
}

[DataDefinition]
public sealed partial class LuaMSectorMemorySnapshot
{
    [DataField]
    public bool StoriesLoaded;

    [DataField]
    public bool NewsSeeded;

    [DataField]
    public bool ContractsSeeded;

    [DataField]
    public List<LuaMSectorStoryRecord> Records = new();

    [DataField]
    public Dictionary<string, int> ReputationLedger = new();

    [DataField]
    public List<LuaMSectorReputationLedgerEntry> ReputationEntries = new();

    [DataField]
    public List<LuaMSectorHazardReportEntry> HazardReports = new();

    [DataField]
    public List<LuaMSectorInsurancePayoutEntry> InsurancePayouts = new();

    [DataField]
    public List<LuaMSectorBlackBoxRecoveryEntry> BlackBoxRecoveries = new();

    [DataField]
    public List<LuaMSectorRegistryEntry> CompanyRegistry = new();

    [DataField]
    public List<LuaMSectorRegistryEntry> ShipRegistry = new();

    [DataField]
    public List<LuaMSectorConditionEntry> SectorConditions = new();

    [DataField]
    public LuaMAiBaseState AiBase = new();
}

[DataDefinition]
public sealed partial class LuaMSectorStoryRecord
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string Title = string.Empty;

    [DataField]
    public string Author = "LuaM sector board";

    [DataField]
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
    public int ContractReward;

    [DataField]
    public string Insurance = string.Empty;

    [DataField]
    public string BlackBox = string.Empty;

    [DataField]
    public string CompanyRecord = string.Empty;

    [DataField]
    public string ShipRecord = string.Empty;

    [DataField]
    public bool Resolved;

    [DataField]
    public string ResolvedBy = string.Empty;

    [DataField]
    public string ResolutionNote = string.Empty;

    [DataField]
    public bool NewsSeeded;

    [DataField]
    public bool ContractSeeded;
}

[DataDefinition]
public sealed partial class LuaMSectorReputationLedgerEntry
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string Target = string.Empty;

    [DataField]
    public int Delta;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Note = string.Empty;
}

[DataDefinition]
public sealed partial class LuaMSectorHazardReportEntry
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string Hazard = string.Empty;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Note = string.Empty;
}

[DataDefinition]
public sealed partial class LuaMSectorInsurancePayoutEntry
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public int Amount;

    [DataField]
    public bool Paid;

    [DataField]
    public string Policy = string.Empty;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Note = string.Empty;
}

[DataDefinition]
public sealed partial class LuaMSectorBlackBoxRecoveryEntry
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string Recovery = string.Empty;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Note = string.Empty;

    [DataField]
    public string SourceSnapshot = string.Empty;
}

[DataDefinition]
public sealed partial class LuaMSectorRegistryEntry
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string Record = string.Empty;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Note = string.Empty;
}

[DataDefinition]
public sealed partial class LuaMSectorConditionEntry
{
    [DataField]
    public string ConditionId = string.Empty;

    [DataField]
    public string Title = string.Empty;

    [DataField]
    public int Severity = 1;

    [DataField]
    public string Summary = string.Empty;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public bool Active = true;
}

[DataDefinition]
public sealed partial class LuaMAiBaseState
{
    [DataField]
    public bool Created;

    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public string Name = "LuaM autonomous supply base";

    [DataField]
    public string Location = "hidden sector anchorage";

    [DataField]
    public int SupplyScore;

    [DataField]
    public int TradeCycles;

    [DataField]
    public List<LuaMAiBaseInventoryEntry> Inventory = new();

    [DataField]
    public List<LuaMAiBaseNeedEntry> Needs = new();

    [DataField]
    public List<LuaMAiBaseTradeEntry> TradeLog = new();

    [DataField]
    public List<LuaMAiBaseAutofixEntry> AutofixLog = new();
}

[DataDefinition]
public sealed partial class LuaMAiBaseInventoryEntry
{
    [DataField]
    public string Resource = string.Empty;

    [DataField]
    public int Amount;
}

[DataDefinition]
public sealed partial class LuaMAiBaseNeedEntry
{
    [DataField]
    public string Resource = string.Empty;

    [DataField]
    public int Target;

    [DataField]
    public int Priority = 1;
}

[DataDefinition]
public sealed partial class LuaMAiBaseTradeEntry
{
    [DataField]
    public int Cycle;

    [DataField]
    public string Role = string.Empty;

    [DataField]
    public string Vessel = string.Empty;

    [DataField]
    public string Resource = string.Empty;

    [DataField]
    public int Amount;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Summary = string.Empty;
}

[DataDefinition]
public sealed partial class LuaMAiBaseAutofixEntry
{
    [DataField]
    public int Attempt;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Issue = string.Empty;

    [DataField]
    public string CommandId = string.Empty;

    [DataField]
    public string BeforeSummary = string.Empty;

    [DataField]
    public string ResultSummary = string.Empty;

    [DataField]
    public string AfterSummary = string.Empty;

    [DataField]
    public bool Success;
}
