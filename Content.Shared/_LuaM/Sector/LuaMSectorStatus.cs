using Robust.Shared.Prototypes;

namespace Content.Shared._LuaM.Sector;

public sealed class LuaMSectorStatusSnapshot
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
    public readonly IReadOnlyDictionary<string, int> ReputationLedger;
    public readonly IReadOnlyList<LuaMSectorReputationStatus> Reputation;
    public readonly IReadOnlyList<LuaMSectorHazardStatus> Hazards;
    public readonly IReadOnlyList<LuaMSectorConditionStatus> Conditions;
    public readonly IReadOnlyList<LuaMSectorLockedStoryStatus> LockedLeads;
    public readonly IReadOnlyList<LuaMSectorHistoryStatus> RecentHistory;

    public LuaMSectorStatusSnapshot(
        int totalStories,
        int activeHazards,
        int acknowledgedHazards,
        int insurancePayouts,
        int blackBoxRecoveries,
        int companyRecords,
        int shipRecords,
        int activeConditions,
        int lockedStories,
        IReadOnlyDictionary<string, int> reputationLedger,
        IReadOnlyList<LuaMSectorReputationStatus> reputation,
        IReadOnlyList<LuaMSectorHazardStatus> hazards,
        IReadOnlyList<LuaMSectorConditionStatus> conditions,
        IReadOnlyList<LuaMSectorLockedStoryStatus> lockedLeads,
        IReadOnlyList<LuaMSectorHistoryStatus> recentHistory)
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
        ReputationLedger = reputationLedger;
        Reputation = reputation;
        Hazards = hazards;
        Conditions = conditions;
        LockedLeads = lockedLeads;
        RecentHistory = recentHistory;
    }
}

public sealed class LuaMSectorConditionStatus
{
    public readonly string ConditionId;
    public readonly string Title;
    public readonly int Severity;
    public readonly string Summary;
    public readonly string Actor;
    public readonly bool Active;

    public LuaMSectorConditionStatus(
        string conditionId,
        string title,
        int severity,
        string summary,
        string actor,
        bool active)
    {
        ConditionId = conditionId;
        Title = title;
        Severity = severity;
        Summary = summary;
        Actor = actor;
        Active = active;
    }
}

public sealed class LuaMSectorHistoryStatus
{
    public readonly string Category;
    public readonly ProtoId<LuaMSectorStoryPrototype> Story;
    public readonly string Title;
    public readonly string Actor;
    public readonly string Summary;

    public LuaMSectorHistoryStatus(
        string category,
        ProtoId<LuaMSectorStoryPrototype> story,
        string title,
        string actor,
        string summary)
    {
        Category = category;
        Story = story;
        Title = title;
        Actor = actor;
        Summary = summary;
    }
}

public sealed class LuaMSectorLockedStoryStatus
{
    public readonly ProtoId<LuaMSectorStoryPrototype> Story;
    public readonly string Title;
    public readonly string RequiredTarget;
    public readonly int RequiredValue;
    public readonly int CurrentValue;

    public LuaMSectorLockedStoryStatus(
        ProtoId<LuaMSectorStoryPrototype> story,
        string title,
        string requiredTarget,
        int requiredValue,
        int currentValue)
    {
        Story = story;
        Title = title;
        RequiredTarget = requiredTarget;
        RequiredValue = requiredValue;
        CurrentValue = currentValue;
    }
}

public sealed class LuaMSectorReputationStatus
{
    public readonly string Target;
    public readonly int Value;
    public readonly string Tier;
    public readonly int RewardBonus;

    public LuaMSectorReputationStatus(string target, int value, string tier, int rewardBonus)
    {
        Target = target;
        Value = value;
        Tier = tier;
        RewardBonus = rewardBonus;
    }
}

public sealed class LuaMSectorHazardStatus
{
    public readonly ProtoId<LuaMSectorStoryPrototype> Story;
    public readonly string Title;
    public readonly string Hazard;
    public readonly string Description;
    public readonly int Severity;
    public readonly int RewardBonus;
    public readonly bool Acknowledged;
    public readonly bool Resolved;

    public LuaMSectorHazardStatus(
        ProtoId<LuaMSectorStoryPrototype> story,
        string title,
        string hazard,
        string description,
        int severity,
        int rewardBonus,
        bool acknowledged,
        bool resolved)
    {
        Story = story;
        Title = title;
        Hazard = hazard;
        Description = description;
        Severity = severity;
        RewardBonus = rewardBonus;
        Acknowledged = acknowledged;
        Resolved = resolved;
    }
}
