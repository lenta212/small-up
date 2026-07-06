using System.Linq;
using Content.Server.CartridgeLoader;
using Content.Shared._LuaM.Sector;
using Content.Shared.CartridgeLoader;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorStatusCartridgeSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private LuaMSectorStorySystem _sectorStories = default!;
    [Dependency] private LuaMSectorDynamicEventSystem _dynamicEvents = default!;
    [Dependency] private LuaMSectorInsuranceTerminalSystem _insurance = default!;
    [Dependency] private LuaMSectorRegistryTerminalSystem _registry = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorStatusCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<LuaMSectorStoryUnlockedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorStoryResolvedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorReputationChangedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorHazardAcknowledgedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorInsuranceClaimedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorBlackBoxRecoveredEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorCompanyRegisteredEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorShipRegisteredEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorConditionChangedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorRescueAfterActionRecordedEvent>(OnSectorStatusChanged);
        SubscribeLocalEvent<LuaMSectorRescueFollowUpClearedEvent>(OnSectorStatusChanged);
    }

    private void OnUiReady(Entity<LuaMSectorStatusCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateUi(args.Loader);
    }

    private void OnSectorStatusChanged<T>(T ev)
    {
        UpdateAllUis();
    }

    private void UpdateAllUis()
    {
        var query = EntityQueryEnumerator<LuaMSectorStatusCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out _, out _, out var cartridge))
        {
            if (cartridge.LoaderUid is { } loader)
                UpdateUi(loader);
        }
    }

    private void UpdateUi(EntityUid loader)
    {
        _cartridgeLoader.UpdateCartridgeUiState(loader, BuildStatusUiState());
    }

    public LuaMSectorStatusUiState BuildStatusUiState(string lastActionResult = "")
    {
        var snapshot = _sectorStories.GetStatusSnapshot();
        var openStory = _sectorStories.TryGetOpenRuntimeDistressStory(out var runtimeStory) && runtimeStory != null
            ? runtimeStory
            : null;
        var automation = _dynamicEvents.BuildAutomationUiEntry();
        var sectorMapNodes = _dynamicEvents.BuildSectorMapUiEntries();
        var preferredProcesses = _dynamicEvents.BuildPreferredProcessUiEntries();
        var insuranceCases = _insurance.BuildInsuranceUiEntries();
        var registryRecords = _registry.BuildRegistryUiEntries();

        return new LuaMSectorStatusUiState(
            snapshot.TotalStories,
            snapshot.ActiveHazards,
            snapshot.AcknowledgedHazards,
            snapshot.InsurancePayouts,
            snapshot.BlackBoxRecoveries,
            snapshot.CompanyRecords,
            snapshot.ShipRecords,
            snapshot.ActiveConditions,
            snapshot.LockedStories,
            lastActionResult,
            LuaMSectorPlayerBriefing.BuildDailyDigestLines(snapshot, openStory, automation.ActivePlayers, 4),
            LuaMSectorPlayerBriefing.BuildNextActions(snapshot, openStory, 4),
            LuaMSectorPlayerBriefing.BuildQuestTasks(
                snapshot,
                openStory,
                automation,
                sectorMapNodes,
                preferredProcesses,
                insuranceCases,
                registryRecords,
                6),
            automation,
            sectorMapNodes,
            preferredProcesses,
            insuranceCases,
            registryRecords,
            snapshot.Hazards
                .OrderByDescending(hazard => hazard.Severity)
                .ThenBy(hazard => hazard.Title)
                .Select(hazard => new LuaMSectorHazardUiEntry
                {
                    Title = hazard.Title,
                    Hazard = hazard.Hazard,
                    Description = hazard.Description,
                    Severity = hazard.Severity,
                    RewardBonus = hazard.RewardBonus,
                    Acknowledged = hazard.Acknowledged,
                    Resolved = hazard.Resolved,
                })
                .ToArray(),
            snapshot.Conditions
                .Where(condition => condition.Active)
                .OrderByDescending(condition => condition.Severity)
                .ThenBy(condition => condition.Title)
                .Select(condition => new LuaMSectorConditionUiEntry
                {
                    ConditionId = condition.ConditionId,
                    Title = condition.Title,
                    Severity = condition.Severity,
                    Summary = condition.Summary,
                    Actor = condition.Actor,
                    Active = condition.Active,
                })
                .ToArray(),
            snapshot.Reputation
                .Select(entry => new LuaMSectorReputationUiEntry
                {
                    Target = entry.Target,
                    Value = entry.Value,
                    Tier = entry.Tier,
                    RewardBonus = entry.RewardBonus,
                })
                .ToArray(),
            snapshot.LockedLeads
                .Select(lead => new LuaMSectorLockedLeadUiEntry
                {
                    Title = lead.Title,
                    RequiredTarget = lead.RequiredTarget,
                    RequiredValue = lead.RequiredValue,
                    CurrentValue = lead.CurrentValue,
                })
                .ToArray(),
            snapshot.RecentHistory
                .Select(entry => new LuaMSectorHistoryUiEntry
                {
                    Category = entry.Category,
                    Title = entry.Title,
                    Actor = entry.Actor,
                    Summary = entry.Summary,
                })
                .ToArray());
    }
}
