using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Sector;

[Serializable, NetSerializable]
public sealed class LuaMSectorStatusUiState : BoundUserInterfaceState
{
    public readonly int TotalStories;
    public readonly int ActiveHazards;
    public readonly int AcknowledgedHazards;
    public readonly int InsurancePayouts;
    public readonly int BlackBoxRecoveries;
    public readonly int CompanyRecords;
    public readonly int ShipRecords;
    public readonly int ActiveConditions;
    public readonly int LockedStories;
    public readonly string LastActionResult;
    public readonly string[] DigestLines;
    public readonly string[] BriefingSteps;
    public readonly LuaMSectorQuestTaskUiEntry[] QuestTasks;
    public readonly LuaMSectorAutomationUiEntry Automation;
    public readonly LuaMSectorMapNodeUiEntry[] SectorMapNodes;
    public readonly LuaMSectorPreferredProcessUiEntry[] PreferredProcesses;
    public readonly LuaMSectorInsuranceUiEntry[] InsuranceCases;
    public readonly LuaMSectorRegistryUiEntry[] RegistryRecords;
    public readonly LuaMSectorHazardUiEntry[] Hazards;
    public readonly LuaMSectorConditionUiEntry[] Conditions;
    public readonly LuaMSectorReputationUiEntry[] Reputation;
    public readonly LuaMSectorLockedLeadUiEntry[] LockedLeads;
    public readonly LuaMSectorHistoryUiEntry[] RecentHistory;

    public LuaMSectorStatusUiState(
        int totalStories,
        int activeHazards,
        int acknowledgedHazards,
        int insurancePayouts,
        int blackBoxRecoveries,
        int companyRecords,
        int shipRecords,
        int activeConditions,
        int lockedStories,
        string lastActionResult,
        string[] digestLines,
        string[] briefingSteps,
        LuaMSectorQuestTaskUiEntry[] questTasks,
        LuaMSectorAutomationUiEntry automation,
        LuaMSectorMapNodeUiEntry[] sectorMapNodes,
        LuaMSectorPreferredProcessUiEntry[] preferredProcesses,
        LuaMSectorInsuranceUiEntry[] insuranceCases,
        LuaMSectorRegistryUiEntry[] registryRecords,
        LuaMSectorHazardUiEntry[] hazards,
        LuaMSectorConditionUiEntry[] conditions,
        LuaMSectorReputationUiEntry[] reputation,
        LuaMSectorLockedLeadUiEntry[] lockedLeads,
        LuaMSectorHistoryUiEntry[] recentHistory)
    {
        TotalStories = totalStories;
        ActiveHazards = activeHazards;
        AcknowledgedHazards = acknowledgedHazards;
        InsurancePayouts = insurancePayouts;
        BlackBoxRecoveries = blackBoxRecoveries;
        CompanyRecords = companyRecords;
        ShipRecords = shipRecords;
        ActiveConditions = activeConditions;
        LockedStories = lockedStories;
        LastActionResult = lastActionResult;
        DigestLines = digestLines;
        BriefingSteps = briefingSteps;
        QuestTasks = questTasks;
        Automation = automation;
        SectorMapNodes = sectorMapNodes;
        PreferredProcesses = preferredProcesses;
        InsuranceCases = insuranceCases;
        RegistryRecords = registryRecords;
        Hazards = hazards;
        Conditions = conditions;
        Reputation = reputation;
        LockedLeads = lockedLeads;
        RecentHistory = recentHistory;
    }
}

[Serializable, NetSerializable]
public struct LuaMSectorQuestTaskUiEntry
{
    public string TaskId;
    public string Status;
    public string Title;
    public string Objective;
    public string Location;
    public string TurnIn;
    public string Reward;
    public int Priority;
    public bool Active;
}

[Serializable, NetSerializable]
public struct LuaMSectorMapNodeUiEntry
{
    public string NodeId;
    public string Kind;
    public string Title;
    public string State;
    public string StoryId;
    public string TemplateId;
    public string Location;
    public string Detail;
    public string Risk;
    public bool Active;
    public int RoutePingCount;
    public int SortOrder;
}

[Serializable, NetSerializable]
public struct LuaMSectorPreferredProcessUiEntry
{
    public string TemplateId;
    public string Title;
    public string Vessel;
    public string Description;
    public string ReputationTarget;
    public int CurrentReputation;
    public int RequiredReputation;
    public string Tier;
    public int BaseReward;
    public int ReputationBonus;
    public bool Unlocked;
    public bool CanRequestNow;
    public string BlockReason;
}

[Serializable, NetSerializable]
public struct LuaMSectorInsuranceUiEntry
{
    public string StoryId;
    public string Title;
    public string Vessel;
    public string Policy;
    public string State;
    public bool Claimed;
    public bool Paid;
    public int RequestedAmount;
    public int PaidAmount;
    public string ServiceLine;
}

[Serializable, NetSerializable]
public struct LuaMSectorRegistryUiEntry
{
    public string StoryId;
    public string Title;
    public string Vessel;
    public string CompanyState;
    public string ShipState;
    public bool CanRegisterCompany;
    public bool CanRegisterShip;
    public string ServiceLine;
    public string CompanyRecord;
    public string ShipRecord;
}

[Serializable, NetSerializable]
public struct LuaMSectorAutomationUiEntry
{
    public string State;
    public string NextAutomaticEvent;
    public int ActivePlayers;
    public string DispatchTier;
    public int DispatchReputationScore;
    public int DispatchCooldownReductionPercent;
    public int DispatchCooldownMinSeconds;
    public int DispatchCooldownMaxSeconds;
    public bool HasOpenRuntimeLead;
    public string OpenRuntimeLead;
    public bool CanRequestDynamicEvent;
    public string RequestBlockReason;
    public bool CanPingRoute;
    public string RoutePingBlockReason;
    public string ActiveRouteStory;
    public string ActiveRouteMarker;
    public string LastRoutePing;
    public int RoutePingCount;
    public int RouteCalibrationCredits;
    public string RouteCalibrationSource;
    public int RouteCalibrationSourceChainDepth;
    public int RouteCalibrationRewardBonus;
    public int RouteCalibrationClosureRewardBonus;
    public int RouteCalibrationRadiationDampingPreview;
    public bool RouteCalibrationSensorDriftSuppressionPreview;
    public string[] RouteCalibrationSources;
    public bool RouteCalibrationHandoffReady;
    public string RouteCalibrationHandoffSource;
    public string ActiveRouteCalibrationSource;
    public int ActiveRouteCalibrationChainDepth;
    public bool ActiveRouteCalibrationRelayInherited;
    public int ActiveMarkers;
    public int SiteNotes;
    public int ConditionHazards;
    public int SensorDriftMarkers;
    public string[] TemplateIds;
}

[Serializable, NetSerializable]
public struct LuaMSectorHistoryUiEntry
{
    public string Category;
    public string Title;
    public string Actor;
    public string Summary;
}

[Serializable, NetSerializable]
public struct LuaMSectorHazardUiEntry
{
    public string Title;
    public string Hazard;
    public string Description;
    public int Severity;
    public int RewardBonus;
    public bool Acknowledged;
    public bool Resolved;
}

[Serializable, NetSerializable]
public struct LuaMSectorConditionUiEntry
{
    public string ConditionId;
    public string Title;
    public int Severity;
    public string Summary;
    public string Actor;
    public bool Active;
}

[Serializable, NetSerializable]
public struct LuaMSectorReputationUiEntry
{
    public string Target;
    public int Value;
    public string Tier;
    public int RewardBonus;
}

[Serializable, NetSerializable]
public struct LuaMSectorLockedLeadUiEntry
{
    public string Title;
    public string RequiredTarget;
    public int RequiredValue;
    public int CurrentValue;
}
