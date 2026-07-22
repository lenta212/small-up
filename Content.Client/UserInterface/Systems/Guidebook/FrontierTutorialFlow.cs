using Content.Shared.Guidebook;
using Robust.Shared.Prototypes;

namespace Content.Client.UserInterface.Systems.Guidebook;

/// <summary>
/// A stable onboarding topic. Entries must only be appended: the index is persisted as a bit.
/// </summary>
internal readonly record struct FrontierTutorialTopic(
    ProtoId<GuideEntryPrototype> Guide,
    string NameLocId);

internal static class FrontierTutorialCatalog
{
    public const int ProgressVersion = 1;

    // Keep this list deliberately short and append-only. Reordering it would reinterpret archived progress.
    public static readonly IReadOnlyList<FrontierTutorialTopic> Topics =
    [
        new("NF14", "frontier-tutorial-topic-station-basics"),
        new("Bank", "frontier-tutorial-topic-banking"),
        new("Hiring", "frontier-tutorial-topic-work"),
        new("Shipyard", "frontier-tutorial-topic-shipyard"),
        new("PreflightChecklist", "frontier-tutorial-topic-preflight"),
        new("Piloting", "frontier-tutorial-topic-piloting"),
        new("SectorTopology", "frontier-tutorial-topic-coordinates"),
        new("Survival", "frontier-tutorial-topic-survival"),
        new("Expeditions", "frontier-tutorial-topic-expeditions"),
        new("CargoHauling", "frontier-tutorial-topic-cargo"),
    ];
}

/// <summary>
/// Pure state machine for the selectable Frontier onboarding checklist.
/// </summary>
internal sealed class FrontierTutorialFlow
{
    private readonly int _topicCount;

    public int CompletedMask { get; private set; }
    public int CurrentIndex { get; private set; }
    public bool IsComplete => CurrentIndex >= _topicCount;

    public FrontierTutorialFlow(int topicCount, int completedMask)
    {
        ValidateTopicCount(topicCount);
        _topicCount = topicCount;
        CompletedMask = NormalizeMask(completedMask, topicCount);
        CurrentIndex = FindNextIncomplete(0);
    }

    public bool CompleteCurrent()
    {
        if (IsComplete)
            return false;

        CompletedMask |= 1 << CurrentIndex;
        CurrentIndex = FindNextIncomplete(CurrentIndex + 1);
        return true;
    }

    public bool SelectTopic(int index)
    {
        if (index < 0 || index >= _topicCount || IsTopicComplete(index))
            return false;

        CurrentIndex = index;
        return true;
    }

    public bool IsTopicComplete(int index)
    {
        if (index < 0 || index >= _topicCount)
            return false;

        return (CompletedMask & 1 << index) != 0;
    }

    private int FindNextIncomplete(int start)
    {
        for (var offset = 0; offset < _topicCount; offset++)
        {
            var index = (start + offset) % _topicCount;
            if ((CompletedMask & 1 << index) == 0)
                return index;
        }

        return _topicCount;
    }

    public static int NormalizeMask(int mask, int topicCount)
    {
        ValidateTopicCount(topicCount);
        return mask & GetCompleteMask(topicCount);
    }

    public static bool ShouldOfferForPlaytime(
        int completedMask,
        int topicCount,
        TimeSpan overallPlaytime,
        TimeSpan newPlayerWindow)
    {
        ValidateTopicCount(topicCount);
        if (overallPlaytime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(overallPlaytime));
        if (newPlayerWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(newPlayerWindow));

        var normalized = NormalizeMask(completedMask, topicCount);
        if (normalized == GetCompleteMask(topicCount))
            return false;

        // A started checklist may always be resumed. A fresh one is reserved for genuinely new players.
        return normalized != 0 || overallPlaytime < newPlayerWindow;
    }

    /// <summary>
    /// Migrates the old one-shot 0/1/2 choice while respecting that an existing player already handled the prompt.
    /// The searchable guidebook remains available even when the sequential checklist is considered complete.
    /// </summary>
    public static FrontierTutorialMigration Migrate(
        int legacyChoice,
        int completedMask,
        int storedVersion,
        int topicCount)
    {
        ValidateTopicCount(topicCount);

        // Never let an older client rewrite progress created by a newer schema.
        if (storedVersion > FrontierTutorialCatalog.ProgressVersion)
        {
            return new FrontierTutorialMigration(
                legacyChoice,
                completedMask,
                storedVersion,
                false);
        }

        var normalized = NormalizeMask(completedMask, topicCount);
        if (storedVersion == 0 && legacyChoice is 1 or 2)
            normalized = GetCompleteMask(topicCount);

        return new FrontierTutorialMigration(
            0,
            normalized,
            FrontierTutorialCatalog.ProgressVersion,
            true);
    }

    private static void ValidateTopicCount(int topicCount)
    {
        if (topicCount is <= 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(topicCount), "Tutorial progress supports between 1 and 30 topics.");
    }

    private static int GetCompleteMask(int topicCount)
    {
        return (1 << topicCount) - 1;
    }
}

internal readonly record struct FrontierTutorialMigration(
    int LegacyChoice,
    int CompletedMask,
    int Version,
    bool Compatible);
