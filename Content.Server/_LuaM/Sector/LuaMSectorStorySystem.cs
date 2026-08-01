using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Content.Server._NF.Bank;
using Content.Server._NF.BountyContracts;
using Content.Server._NF.SectorServices;
using Content.Server.CartridgeLoader.Cartridges;
using Content.Server.GameTicking;
using Content.Shared._LuaM.Sector;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.BountyContracts;
using Content.Shared.GameTicking;
using Content.Shared.MassMedia.Components;
using Content.Shared.MassMedia.Systems;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorStorySystem : EntitySystem
{
    public const int ReputationRewardStep = 500;
    public const int ReputationRewardCap = 3000;
    public const int RecentHistoryLimit = 8;
    public const string RuntimeDistressStoryPrefix = "LuaMSectorRuntimeDistress";
    public const string RescueAfterActionStoryId = "LuaMSectorRescueAfterAction";
    private static readonly ProtoId<LuaMSectorStoryPrototype> RescueAfterActionStory = new(RescueAfterActionStoryId);
    private const int AiBaseTradeLogLimit = 12;
    private const int AiBaseAutofixLogLimit = 16;
    private const int RescueAfterActionLimit = 16;
    public const int RescueAfterActionCooldownSeconds = 90;
    public const int RescueAfterActionBlockedCooldownSeconds = 180;

    private static readonly ResPath PersistenceDirectory = new("/luam");
    private static readonly ResPath PersistencePath = PersistenceDirectory / "sector_memory.json";
    private static readonly ResPath PdaBankAccountRegistryPath = PersistenceDirectory / "pda-bank-accounts.json";
    private static readonly JsonSerializerOptions PersistenceJsonOptions = new()
    {
        WriteIndented = false,
    };
    private static readonly (string Resource, int Amount)[] AiBaseInitialInventory =
    [
        ("fuel", 20),
        ("hull-parts", 10),
        ("food", 12),
        ("medicine", 5),
        ("electronics", 8),
        ("ore", 0),
        ("credits", 1200),
    ];
    private static readonly (string Resource, int Target, int Priority)[] AiBaseDefaultNeeds =
    [
        ("fuel", 80, 5),
        ("hull-parts", 60, 5),
        ("electronics", 45, 4),
        ("ore", 100, 4),
        ("food", 40, 3),
        ("medicine", 30, 4),
    ];

    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private SectorServiceSystem _sectorService = default!;
    [Dependency] private BountyContractSystem _bountyContracts = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IResourceManager _resource = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var service = _sectorService.GetServiceEntity();
        if (!service.IsValid() ||
            !TryComp<LuaMSectorMemoryComponent>(service, out var memory))
        {
            return;
        }

        TryLoadPersistedMemory(memory);
        EnsureRecords(memory);
        TrySeedNews(memory);
        TrySeedContracts(service, memory);
    }

    public bool TryGetSectorMemory(
        out IReadOnlyList<LuaMSectorStoryRecord> records,
        out bool newsSeeded,
        out bool contractsSeeded)
    {
        records = [];
        newsSeeded = false;
        contractsSeeded = false;

        var service = _sectorService.GetServiceEntity();
        if (!service.IsValid() ||
            !TryComp<LuaMSectorMemoryComponent>(service, out var memory))
        {
            return false;
        }

        records = new List<LuaMSectorStoryRecord>(memory.Records);
        newsSeeded = memory.NewsSeeded;
        contractsSeeded = memory.ContractsSeeded;
        return true;
    }

    public IReadOnlyDictionary<string, int> GetReputationLedger()
    {
        return TryGetMemory(out var memory)
            ? new Dictionary<string, int>(memory.ReputationLedger)
            : new Dictionary<string, int>();
    }

    public IReadOnlyList<LuaMSectorReputationLedgerEntry> GetReputationEntries()
    {
        return TryGetMemory(out var memory)
            ? new List<LuaMSectorReputationLedgerEntry>(memory.ReputationEntries)
            : [];
    }

    public IReadOnlyList<LuaMSectorHazardReportEntry> GetHazardReports()
    {
        return TryGetMemory(out var memory)
            ? new List<LuaMSectorHazardReportEntry>(memory.HazardReports)
            : [];
    }

    public IReadOnlyList<LuaMSectorInsurancePayoutEntry> GetInsurancePayouts()
    {
        return TryGetMemory(out var memory)
            ? new List<LuaMSectorInsurancePayoutEntry>(memory.InsurancePayouts)
            : [];
    }

    public IReadOnlyList<LuaMSectorBlackBoxRecoveryEntry> GetBlackBoxRecoveries()
    {
        return TryGetMemory(out var memory)
            ? new List<LuaMSectorBlackBoxRecoveryEntry>(memory.BlackBoxRecoveries)
            : [];
    }

    public IReadOnlyList<LuaMSectorRegistryEntry> GetCompanyRegistry()
    {
        return TryGetMemory(out var memory)
            ? new List<LuaMSectorRegistryEntry>(memory.CompanyRegistry)
            : [];
    }

    public IReadOnlyList<LuaMSectorRegistryEntry> GetShipRegistry()
    {
        return TryGetMemory(out var memory)
            ? new List<LuaMSectorRegistryEntry>(memory.ShipRegistry)
            : [];
    }

    public IReadOnlyList<LuaMSectorConditionEntry> GetSectorConditions()
    {
        return TryGetMemory(out var memory)
            ? memory.SectorConditions.Select(CloneConditionEntry).ToList()
            : [];
    }

    public LuaMAiBaseState GetAiBaseState()
    {
        return TryGetMemory(out var memory)
            ? CloneAiBaseState(memory.AiBase)
            : new LuaMAiBaseState();
    }

    public string EnsureAiBase(string actor)
    {
        if (!TryGetMemory(out var memory))
            return "AI base memory is not available.";

        var state = EnsureAiBaseState(memory, actor, _timing.CurTime, out var createdNow);
        SaveMemory(memory);

        return createdNow
            ? $"AI base deployed. {BuildAiBaseStatusText(state)}"
            : $"AI base already exists. {BuildAiBaseStatusText(state)}";
    }

    public string BuildAiBaseStatusText()
    {
        if (!TryGetMemory(out var memory))
            return "AI base memory is not available.";

        return BuildAiBaseStatusText(memory.AiBase);
    }

    public bool TryGetActiveRescueCooldown(out int remainingSeconds, out string status)
    {
        remainingSeconds = 0;
        status = "none";

        if (!TryGetMemory(out var memory))
        {
            status = "rescue cooldown memory unavailable";
            return false;
        }

        var state = memory.AiBase;
        status = string.IsNullOrWhiteSpace(state.LastRescueCooldownStatus)
            ? "none"
            : state.LastRescueCooldownStatus;

        if (!IsRescueCooldownActive(state, _timing.CurTime))
            return false;

        remainingSeconds = Math.Max(
            1,
            (int) Math.Ceiling((state.NextRescueDispatchAllowedAt - _timing.CurTime).TotalSeconds));
        status = $"{status}; remaining={remainingSeconds}s";
        return true;
    }

    public IReadOnlyList<LuaMAiBaseCompensationEntry> BuildAiBaseCompensationPlan()
    {
        if (!TryGetMemory(out var memory))
        {
            return
            [
                new LuaMAiBaseCompensationEntry
                {
                    WeaknessId = "memory-unavailable",
                    Title = "AI base memory unavailable",
                    Severity = 5,
                    Evidence = "sector memory entity is not available",
                    Compensation = "restore sector memory before dispatching autonomous logistics",
                    SuggestedRole = "operator",
                    SuggestedZone = "admin",
                },
            ];
        }

        return BuildAiBaseCompensationPlan(memory.AiBase);
    }

    public static IReadOnlyList<LuaMAiBaseCompensationEntry> BuildAiBaseCompensationPlan(LuaMAiBaseState state)
    {
        var copy = CloneAiBaseState(state);
        EnsureAiBaseDefaults(copy);
        return BuildAiBaseCompensationPlanInternal(copy);
    }

    public static string BuildAiBaseCompensationSummary(LuaMAiBaseState state, int maxEntries = 2)
    {
        var entries = BuildAiBaseCompensationPlan(state)
            .Take(Math.Clamp(maxEntries, 1, 5))
            .ToList();

        return entries.Count == 0
            ? "stable; keep scout and trade cycles running"
            : string.Join(" | ", entries.Select(entry => $"S{entry.Severity} {entry.Title}: {entry.Compensation}"));
    }

    public string RecordAiBaseShipVisit(string actor, string role, string vesselId, string displayName)
    {
        if (!TryGetMemory(out var memory))
            return "AI base memory is not available.";

        var state = EnsureAiBaseState(memory, actor, _timing.CurTime, out _);
        var normalizedRole = NormalizeAiBaseShipRole(role);
        var delivery = SelectAiBaseDelivery(state, normalizedRole);
        AddAiBaseInventory(state, delivery.Resource, delivery.Amount);
        if (delivery.CreditDelta != 0)
            AddAiBaseInventory(state, "credits", delivery.CreditDelta);

        state.TradeCycles++;
        state.SupplyScore = CalculateAiBaseSupplyScore(state);
        RefreshAiBaseBehavior(state, _timing.CurTime, $"ship:{normalizedRole}");

        var resourceAmount = GetAiBaseInventoryAmount(state, delivery.Resource);
        var target = GetAiBaseNeedTarget(state, delivery.Resource);
        var targetText = target > 0 ? $"/{target}" : string.Empty;
        var vesselText = string.IsNullOrWhiteSpace(displayName)
            ? Trim(vesselId, 96)
            : Trim(displayName, 96);
        if (string.IsNullOrWhiteSpace(vesselText))
            vesselText = "unknown vessel";

        var summary = $"{GetAiBaseRoleLabel(normalizedRole)} {vesselText}: {delivery.Summary}";
        state.TradeLog.Add(new LuaMAiBaseTradeEntry
        {
            Cycle = state.TradeCycles,
            Role = normalizedRole,
            Vessel = vesselText,
            Resource = delivery.Resource,
            Amount = delivery.Amount,
            Actor = Trim(actor, 64),
            Summary = Trim(summary, 192),
        });

        if (state.TradeLog.Count > AiBaseTradeLogLimit)
            state.TradeLog.RemoveRange(0, state.TradeLog.Count - AiBaseTradeLogLimit);

        SaveMemory(memory);

        var creditText = delivery.CreditDelta == 0
            ? string.Empty
            : delivery.CreditDelta > 0
                ? $" credits +{delivery.CreditDelta};"
                : $" credits {delivery.CreditDelta};";
        return $"AI base logistics updated: {summary}; stock {delivery.Resource} {resourceAmount}{targetText};{creditText} supply score {state.SupplyScore}/100; behavior {state.BehaviorMode}/{state.BehaviorFocusResource}: {state.BehaviorDirective}.";
    }

    public string RecordAiBaseMiningDroneYield(string actor, string droneId, string vesselId, string displayName, int amount)
    {
        if (!TryGetMemory(out var memory))
            return "AI base memory is not available.";

        var state = EnsureAiBaseState(memory, actor, _timing.CurTime, out _);
        amount = Math.Clamp(amount, 1, 50);
        AddAiBaseInventory(state, "ore", amount);

        state.TradeCycles++;
        state.SupplyScore = CalculateAiBaseSupplyScore(state);
        RefreshAiBaseBehavior(state, _timing.CurTime, "mining-drone");

        var oreAmount = GetAiBaseInventoryAmount(state, "ore");
        var oreTarget = GetAiBaseNeedTarget(state, "ore");
        var vesselText = string.IsNullOrWhiteSpace(displayName)
            ? Trim(vesselId, 96)
            : Trim(displayName, 96);
        if (string.IsNullOrWhiteSpace(vesselText))
            vesselText = "unknown vessel";

        droneId = string.IsNullOrWhiteSpace(droneId)
            ? "mining-drone"
            : Trim(droneId, 64);

        var summary = $"mining drone {droneId} from {vesselText}: mined ore and returned it to the AI base";
        state.TradeLog.Add(new LuaMAiBaseTradeEntry
        {
            Cycle = state.TradeCycles,
            Role = "miner",
            Vessel = vesselText,
            Resource = "ore",
            Amount = amount,
            Actor = Trim(actor, 64),
            Summary = Trim(summary, 192),
        });

        if (state.TradeLog.Count > AiBaseTradeLogLimit)
            state.TradeLog.RemoveRange(0, state.TradeLog.Count - AiBaseTradeLogLimit);

        SaveMemory(memory);

        var targetText = oreTarget > 0 ? $"/{oreTarget}" : string.Empty;
        return $"AI base mining updated: {summary}; stock ore {oreAmount}{targetText}; supply score {state.SupplyScore}/100; behavior {state.BehaviorMode}/{state.BehaviorFocusResource}: {state.BehaviorDirective}.";
    }

    public string RecordAiBaseDroneTaskContribution(
        string actor,
        string droneId,
        string role,
        string taskType,
        string zoneType,
        string vesselId,
        string displayName,
        int taskCycle)
    {
        if (!TryGetMemory(out var memory))
            return "AI base memory is not available.";

        var state = EnsureAiBaseState(memory, actor, _timing.CurTime, out _);
        var normalizedRole = NormalizeAiBaseDroneRole(role);
        var delivery = SelectAiBaseDroneTaskDelivery(state, normalizedRole, taskType, zoneType);
        AddAiBaseInventory(state, delivery.Resource, delivery.Amount);

        state.TradeCycles++;
        state.SupplyScore = CalculateAiBaseSupplyScore(state);
        RefreshAiBaseBehavior(state, _timing.CurTime, $"drone-task:{normalizedRole}");

        var resourceAmount = GetAiBaseInventoryAmount(state, delivery.Resource);
        var target = GetAiBaseNeedTarget(state, delivery.Resource);
        var targetText = target > 0 ? $"/{target}" : string.Empty;
        var vesselText = string.IsNullOrWhiteSpace(displayName)
            ? Trim(vesselId, 96)
            : Trim(displayName, 96);
        if (string.IsNullOrWhiteSpace(vesselText))
            vesselText = "unassigned vessel";

        droneId = string.IsNullOrWhiteSpace(droneId)
            ? $"{normalizedRole}-drone"
            : Trim(droneId, 64);
        taskType = Trim(taskType, 64);
        zoneType = Trim(zoneType, 64);

        var summary = $"{normalizedRole} drone {droneId} on {vesselText}: {delivery.Summary} at {zoneType} during {taskType} cycle {Math.Max(1, taskCycle)}";
        state.TradeLog.Add(new LuaMAiBaseTradeEntry
        {
            Cycle = state.TradeCycles,
            Role = normalizedRole,
            Vessel = vesselText,
            Resource = delivery.Resource,
            Amount = delivery.Amount,
            Actor = Trim(actor, 64),
            Summary = Trim(summary, 192),
        });

        if (state.TradeLog.Count > AiBaseTradeLogLimit)
            state.TradeLog.RemoveRange(0, state.TradeLog.Count - AiBaseTradeLogLimit);

        SaveMemory(memory);

        return $"AI base drone task updated: {summary}; stock {delivery.Resource} {resourceAmount}{targetText}; supply score {state.SupplyScore}/100; behavior {state.BehaviorMode}/{state.BehaviorFocusResource}: {state.BehaviorDirective}.";
    }

    public string RecordAiBaseAutofixAttempt(
        string actor,
        string issue,
        string commandId,
        string beforeSummary,
        string resultSummary,
        string afterSummary,
        bool success)
    {
        if (!TryGetMemory(out var memory))
            return "AI base autofix memory is not available.";

        memory.AiBase ??= new LuaMAiBaseState();
        var state = memory.AiBase;
        EnsureAiBaseDefaults(state);

        state.AutofixLog.Add(new LuaMAiBaseAutofixEntry
        {
            Attempt = state.AutofixLog.Count == 0
                ? 1
                : state.AutofixLog.Max(entry => entry.Attempt) + 1,
            Actor = Trim(actor, 64),
            Issue = Trim(issue, 96),
            CommandId = Trim(commandId, 64),
            BeforeSummary = Trim(beforeSummary, 256),
            ResultSummary = Trim(resultSummary, 256),
            AfterSummary = Trim(afterSummary, 256),
            Success = success,
        });

        if (state.AutofixLog.Count > AiBaseAutofixLogLimit)
            state.AutofixLog.RemoveRange(0, state.AutofixLog.Count - AiBaseAutofixLogLimit);

        RefreshAiBaseBehavior(state, _timing.CurTime, $"autofix:{commandId}");
        SaveMemory(memory);

        var status = success ? "recorded" : "recorded failed";
        return $"AI base autofix memory {status}: #{state.AutofixLog[^1].Attempt} {state.AutofixLog[^1].Issue} via {state.AutofixLog[^1].CommandId}; behavior {state.BehaviorMode}/{state.BehaviorFocusResource}.";
    }

    public bool TryRecordRescueAfterAction(
        string actor,
        string patient,
        string location,
        string treatmentResult,
        string evacuationResult,
        string blockers,
        string playerContribution,
        string teamStatus,
        out LuaMSectorRescueAfterActionEntry? entry)
    {
        entry = null;

        if (!TryGetMemory(out var memory))
            return false;

        actor = Trim(actor, 64);
        patient = Trim(patient, 64);
        location = Trim(location, 128);
        treatmentResult = Trim(treatmentResult, 192);
        evacuationResult = Trim(evacuationResult, 192);
        blockers = Trim(blockers, 192);
        playerContribution = Trim(playerContribution, 128);
        teamStatus = Trim(teamStatus, 128);

        if (string.IsNullOrWhiteSpace(actor))
            actor = "LuaM Rescue";

        if (string.IsNullOrWhiteSpace(patient))
            patient = "withheld";

        if (string.IsNullOrWhiteSpace(blockers))
            blockers = "none";

        if (string.IsNullOrWhiteSpace(playerContribution))
            playerContribution = "unverified";

        if (string.IsNullOrWhiteSpace(teamStatus))
            teamStatus = "available";

        var nextSequence = memory.RescueAfterActions.Count == 0
            ? 1
            : memory.RescueAfterActions.Max(action => action.Sequence) + 1;
        entry = new LuaMSectorRescueAfterActionEntry
        {
            Sequence = nextSequence,
            Actor = actor,
            Patient = patient,
            Location = location,
            TreatmentResult = treatmentResult,
            EvacuationResult = evacuationResult,
            Blockers = blockers,
            PlayerContribution = playerContribution,
            TeamStatus = teamStatus,
        };
        entry.Summary = BuildRescueAfterActionSummary(entry);

        memory.RescueAfterActions.Add(entry);
        if (memory.RescueAfterActions.Count > RescueAfterActionLimit)
            memory.RescueAfterActions.RemoveRange(0, memory.RescueAfterActions.Count - RescueAfterActionLimit);

        TryAutoClearLatestRescueFollowUpFromAfterAction(memory, entry, out var clearedEntry);
        UpdateAiBaseMedicalStatus(memory.AiBase, entry, memory.RescueAfterActions.Count, _timing.CurTime);

        SaveMemory(memory);

        var ev = new LuaMSectorRescueAfterActionRecordedEvent(CloneRescueAfterActionEntry(entry));
        RaiseLocalEvent(ev);
        if (clearedEntry != null)
        {
            var clearedEv = new LuaMSectorRescueFollowUpClearedEvent(CloneRescueAfterActionEntry(clearedEntry));
            RaiseLocalEvent(clearedEv);
        }

        return true;
    }

    public bool TryGetLatestOpenRescueFollowUp(out LuaMSectorRescueAfterActionEntry? entry)
    {
        entry = null;

        if (!TryGetMemory(out var memory))
            return false;

        var match = memory.RescueAfterActions
            .OrderByDescending(action => action.Sequence)
            .FirstOrDefault(HasOpenRescueBlockers);
        if (match == null)
            return false;

        entry = CloneRescueAfterActionEntry(match);
        return true;
    }

    public bool TryClearLatestRescueFollowUp(
        string actor,
        string note,
        out LuaMSectorRescueAfterActionEntry? entry)
    {
        entry = null;

        if (!TryGetMemory(out var memory))
            return false;

        var match = memory.RescueAfterActions
            .OrderByDescending(action => action.Sequence)
            .FirstOrDefault(HasOpenRescueBlockers);
        if (match == null)
            return false;

        actor = Trim(actor, 64);
        note = Trim(note, 256);
        match.BlockersCleared = true;
        match.BlockersClearedBy = string.IsNullOrWhiteSpace(actor)
            ? "LuaM operator"
            : actor;
        match.BlockersClearedNote = string.IsNullOrWhiteSpace(note)
            ? "rescue corridor follow-up cleared"
            : note;
        match.Summary = BuildRescueAfterActionSummary(match);

        SaveMemory(memory);

        entry = CloneRescueAfterActionEntry(match);
        var ev = new LuaMSectorRescueFollowUpClearedEvent(CloneRescueAfterActionEntry(match));
        RaiseLocalEvent(ev);
        return true;
    }

    public IReadOnlyList<LuaMSectorStoryRecord> GetActiveHazards()
    {
        return GetRecords(record => !record.Resolved && !string.IsNullOrWhiteSpace(record.Hazard));
    }

    public bool TrySeedSectorCondition(
        string conditionId,
        string title,
        int severity,
        string summary,
        string actor,
        out LuaMSectorConditionEntry? entry,
        out string error)
    {
        entry = null;
        error = string.Empty;

        conditionId = Trim(conditionId, 64);
        title = Trim(title, 96);
        summary = Trim(summary, 256);
        actor = Trim(actor, 64);

        if (string.IsNullOrWhiteSpace(conditionId))
        {
            error = "LuaM sector condition id is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            error = "LuaM sector condition title is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            error = "LuaM sector condition summary is empty.";
            return false;
        }

        if (!TryGetMemory(out var memory))
        {
            error = "LuaM sector memory is not available.";
            return false;
        }

        severity = Math.Clamp(severity, 1, 5);
        var existing = memory.SectorConditions.FirstOrDefault(condition =>
            condition.ConditionId.Equals(conditionId, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            entry = new LuaMSectorConditionEntry
            {
                ConditionId = conditionId,
                Title = title,
                Severity = severity,
                Summary = summary,
                Actor = actor,
                Active = true,
            };
            memory.SectorConditions.Add(entry);
        }
        else
        {
            existing.Title = title;
            existing.Severity = severity;
            existing.Summary = summary;
            existing.Actor = actor;
            existing.Active = true;
            entry = existing;
        }

        SaveMemory(memory);
        var ev = new LuaMSectorConditionChangedEvent(CloneConditionEntry(entry));
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryClearSectorCondition(string conditionId, string actor, out string error)
    {
        error = string.Empty;
        conditionId = Trim(conditionId, 64);
        actor = Trim(actor, 64);

        if (string.IsNullOrWhiteSpace(conditionId))
        {
            error = "LuaM sector condition id is empty.";
            return false;
        }

        if (!TryGetMemory(out var memory))
        {
            error = "LuaM sector memory is not available.";
            return false;
        }

        var existing = memory.SectorConditions.FirstOrDefault(condition =>
            condition.ConditionId.Equals(conditionId, StringComparison.OrdinalIgnoreCase) && condition.Active);
        if (existing == null)
        {
            error = $"LuaM sector condition is not active: {conditionId}";
            return false;
        }

        existing.Active = false;
        existing.Actor = string.IsNullOrWhiteSpace(actor) ? existing.Actor : actor;
        SaveMemory(memory);
        var ev = new LuaMSectorConditionChangedEvent(CloneConditionEntry(existing));
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryGetOpenRuntimeDistressStory(out LuaMSectorStoryRecord? record)
    {
        record = null;

        if (!TryGetMemory(out var memory))
            return false;

        EnsureRecords(memory);
        var found = memory.Records
            .Where(record => !record.Resolved &&
                             record.Story.ToString().StartsWith(RuntimeDistressStoryPrefix, StringComparison.Ordinal))
            .OrderByDescending(record => record.Story.ToString())
            .FirstOrDefault();

        if (found == null)
            return false;

        record = CloneRecord(found);
        return true;
    }

    public IReadOnlyList<LuaMSectorStoryRecord> GetInsuranceCases()
    {
        return GetRecords(record => !string.IsNullOrWhiteSpace(record.Insurance));
    }

    public IReadOnlyList<LuaMSectorStoryRecord> GetBlackBoxCases()
    {
        return GetRecords(record => !string.IsNullOrWhiteSpace(record.BlackBox));
    }

    public IReadOnlyList<LuaMSectorStoryRecord> GetCompanyRecords()
    {
        return GetRecords(record => !string.IsNullOrWhiteSpace(record.CompanyRecord));
    }

    public IReadOnlyList<LuaMSectorStoryRecord> GetShipRecords()
    {
        return GetRecords(record => !string.IsNullOrWhiteSpace(record.ShipRecord));
    }

    public bool TrySeedDistressStory(
        string title,
        string vessel,
        int reward,
        string description,
        string hazard,
        string reputationTarget,
        int reputationDelta,
        string actor,
        out LuaMSectorStoryRecord? record,
        out string error,
        int hazardRewardBonusExtra = 0)
    {
        record = null;
        error = string.Empty;

        var service = _sectorService.GetServiceEntity();
        if (!service.IsValid() ||
            !TryComp<LuaMSectorMemoryComponent>(service, out var memory))
        {
            error = "LuaM sector memory is not available.";
            return false;
        }

        title = Trim(title, SharedNewsSystem.MaxTitleLength);
        vessel = Trim(vessel, SharedBountyContractSystem.MaxVesselLength);
        description = Trim(description, SharedBountyContractSystem.MaxDescriptionLength);
        hazard = Trim(hazard, 256);
        reputationTarget = Trim(reputationTarget, 64);
        actor = Trim(actor, 64);

        if (string.IsNullOrWhiteSpace(title))
        {
            error = "Runtime distress title is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            error = "Runtime distress description is empty.";
            return false;
        }

        EnsureRecords(memory);

        var storyId = GetNextRuntimeDistressId(memory);
        var rewardBonus = string.IsNullOrWhiteSpace(hazard)
            ? 0
            : Math.Min(Math.Max(reward / 4, 500), 3000);
        if (rewardBonus > 0 && hazardRewardBonusExtra > 0)
            rewardBonus += hazardRewardBonusExtra;

        record = new LuaMSectorStoryRecord
        {
            Story = storyId,
            Title = title,
            Author = actor,
            News = $"Runtime distress lead filed by {actor}: {description}",
            ContractCollection = "Distress",
            ContractCategory = BountyContractCategory.Service,
            ContractName = title,
            ContractVessel = vessel,
            ContractDescription = description,
            ContractReward = Math.Max(reward, 0),
            Hazard = hazard,
            HazardSeverity = string.IsNullOrWhiteSpace(hazard) ? 1 : 2,
            HazardRewardBonus = rewardBonus,
            ReputationTarget = reputationTarget,
            ReputationDelta = string.IsNullOrWhiteSpace(reputationTarget) ? 0 : reputationDelta,
        };

        memory.Records.Add(record);
        memory.NewsSeeded = false;
        memory.ContractsSeeded = false;

        TrySeedNews(memory);
        TrySeedContracts(service, memory);
        SaveMemory(memory);

        return true;
    }

    public LuaMSectorStatusSnapshot GetStatusSnapshot()
    {
        if (!TryGetMemory(out var memory))
        {
            return new LuaMSectorStatusSnapshot(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                new Dictionary<string, int>(),
                [],
                [],
                [],
                [],
                []);
        }

        EnsureRecords(memory);
        var lockedLeads = BuildLockedStoryStatuses(memory);
        var recentHistory = BuildRecentHistoryStatuses(memory);

        var hazards = memory.Records
            .Where(record => !string.IsNullOrWhiteSpace(record.Hazard))
            .Select(record => new LuaMSectorHazardStatus(
                record.Story,
                record.Title,
                record.Hazard,
                record.ContractDescription,
                record.HazardSeverity,
                record.HazardRewardBonus,
                memory.HazardReports.Any(report => report.Story == record.Story),
                record.Resolved))
            .ToList();

        return new LuaMSectorStatusSnapshot(
            memory.Records.Count,
            hazards.Count(hazard => !hazard.Resolved),
            memory.HazardReports.Count,
            memory.InsurancePayouts.Count,
            memory.BlackBoxRecoveries.Count,
            memory.CompanyRegistry.Count,
            memory.ShipRegistry.Count,
            memory.SectorConditions.Count(condition => condition.Active),
            lockedLeads.Count,
            new Dictionary<string, int>(memory.ReputationLedger),
            BuildReputationStatuses(memory.ReputationLedger),
            hazards,
            memory.SectorConditions
                .Select(condition => new LuaMSectorConditionStatus(
                    condition.ConditionId,
                    condition.Title,
                    condition.Severity,
                    condition.Summary,
                    condition.Actor,
                    condition.Active))
                .ToList(),
            lockedLeads,
            recentHistory);
    }

    public bool TryExportMemorySnapshot(out LuaMSectorMemorySnapshot snapshot)
    {
        snapshot = new LuaMSectorMemorySnapshot();

        if (!TryGetMemory(out var memory))
            return false;

        EnsureRecords(memory);

        snapshot.StoriesLoaded = memory.StoriesLoaded;
        snapshot.NewsSeeded = memory.NewsSeeded;
        snapshot.ContractsSeeded = memory.ContractsSeeded;
        snapshot.Records = memory.Records.Select(CloneRecord).ToList();
        snapshot.ReputationLedger = new Dictionary<string, int>(memory.ReputationLedger);
        snapshot.ReputationEntries = memory.ReputationEntries.Select(CloneReputationEntry).ToList();
        snapshot.HazardReports = memory.HazardReports.Select(CloneHazardReport).ToList();
        snapshot.InsurancePayouts = memory.InsurancePayouts.Select(CloneInsurancePayout).ToList();
        snapshot.BlackBoxRecoveries = memory.BlackBoxRecoveries.Select(CloneBlackBoxRecovery).ToList();
        snapshot.CompanyRegistry = memory.CompanyRegistry.Select(CloneRegistryEntry).ToList();
        snapshot.ShipRegistry = memory.ShipRegistry.Select(CloneRegistryEntry).ToList();
        snapshot.SectorConditions = memory.SectorConditions.Select(CloneConditionEntry).ToList();
        snapshot.RescueAfterActions = memory.RescueAfterActions.Select(CloneRescueAfterActionEntry).ToList();
        snapshot.AiBase = CloneAiBaseState(memory.AiBase);
        return true;
    }

    public bool TryImportMemorySnapshot(LuaMSectorMemorySnapshot snapshot)
    {
        if (!TryGetMemory(out var memory))
            return false;

        memory.StoriesLoaded = snapshot.StoriesLoaded;
        memory.NewsSeeded = snapshot.NewsSeeded;
        memory.ContractsSeeded = snapshot.ContractsSeeded;
        memory.Records = snapshot.Records.Select(CloneRecord).ToList();
        memory.ReputationLedger = new Dictionary<string, int>(snapshot.ReputationLedger);
        memory.ReputationEntries = snapshot.ReputationEntries.Select(CloneReputationEntry).ToList();
        memory.HazardReports = snapshot.HazardReports.Select(CloneHazardReport).ToList();
        memory.InsurancePayouts = snapshot.InsurancePayouts.Select(CloneInsurancePayout).ToList();
        memory.BlackBoxRecoveries = snapshot.BlackBoxRecoveries.Select(CloneBlackBoxRecovery).ToList();
        memory.CompanyRegistry = snapshot.CompanyRegistry.Select(CloneRegistryEntry).ToList();
        memory.ShipRegistry = snapshot.ShipRegistry.Select(CloneRegistryEntry).ToList();
        memory.SectorConditions = snapshot.SectorConditions.Select(CloneConditionEntry).ToList();
        memory.RescueAfterActions = snapshot.RescueAfterActions.Select(CloneRescueAfterActionEntry).ToList();
        memory.AiBase = CloneAiBaseState(snapshot.AiBase);
        SaveMemory(memory);
        return true;
    }

    public bool TryExportMemoryJson(out string json)
    {
        json = string.Empty;

        if (!TryGetMemory(out var memory))
            return false;

        EnsureRecords(memory);
        json = JsonSerializer.Serialize(ToPersistedMemory(memory), PersistenceJsonOptions);
        return true;
    }

    public bool TryImportMemoryJson(string json, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "LuaM sector memory JSON is empty.";
            return false;
        }

        if (!TryGetMemory(out var memory))
        {
            error = "LuaM sector memory is not available.";
            return false;
        }

        LuaMSectorPersistedMemory? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<LuaMSectorPersistedMemory>(json, PersistenceJsonOptions);
        }
        catch (Exception exc) when (exc is JsonException or NotSupportedException)
        {
            error = $"LuaM sector memory JSON is invalid: {exc.Message}";
            return false;
        }

        if (persisted == null)
        {
            error = "LuaM sector memory JSON did not contain a memory snapshot.";
            return false;
        }

        ApplyPersistedMemory(memory, persisted);
        memory.PersistenceLoaded = true;
        EnsureRecords(memory);
        SaveMemory(memory);
        return true;
    }

    public bool TryExportMemoryFile(string path, out ResPath exportPath, out string error)
    {
        exportPath = default;

        if (!TryGetBackupPath(path, out exportPath, out error))
            return false;

        if (!TryExportMemoryJson(out var json))
        {
            error = "LuaM sector memory is not available.";
            return false;
        }

        try
        {
            _resource.UserData.CreateDir(exportPath.Directory);
            _resource.UserData.WriteAllText(exportPath, json);
            return true;
        }
        catch (Exception exc)
        {
            error = $"Failed to export LuaM sector memory to {exportPath}: {exc.Message}";
            return false;
        }
    }

    public bool TryImportMemoryFile(string path, out ResPath importPath, out string error)
    {
        importPath = default;

        if (!TryGetBackupPath(path, out importPath, out error))
            return false;

        try
        {
            if (!_resource.UserData.Exists(importPath) ||
                !_resource.UserData.TryReadAllText(importPath, out var json))
            {
                error = $"LuaM sector memory backup does not exist: {importPath}";
                return false;
            }

            return TryImportMemoryJson(json, out error);
        }
        catch (Exception exc)
        {
            error = $"Failed to import LuaM sector memory from {importPath}: {exc.Message}";
            return false;
        }
    }

    public IReadOnlyList<ResPath> GetMemoryBackups()
    {
        try
        {
            if (!_resource.UserData.Exists(PersistenceDirectory))
                return [];

            return _resource.UserData.DirectoryEntries(PersistenceDirectory)
                .Select(entry => PersistenceDirectory / entry)
                .Where(file => !_resource.UserData.IsDir(file))
                .Where(file => file.ToString().EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Where(file => file != PersistencePath)
                .Where(file => file != PdaBankAccountRegistryPath)
                .OrderBy(file => file.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exc)
        {
            Log.Warning($"Failed to list LuaM sector memory backups under {PersistenceDirectory}: {exc}");
            return [];
        }
    }

    public bool TryDeleteMemoryBackup(string path, out ResPath deletePath, out string error)
    {
        deletePath = default;

        if (!TryGetBackupPath(path, out deletePath, out error))
            return false;

        if (deletePath == PersistencePath)
        {
            error = "LuaM sector live memory file cannot be deleted as a backup.";
            return false;
        }

        if (!_resource.UserData.Exists(deletePath))
        {
            error = $"LuaM sector memory backup does not exist: {deletePath}";
            return false;
        }

        try
        {
            _resource.UserData.Delete(deletePath);
            return true;
        }
        catch (Exception exc)
        {
            error = $"Failed to delete LuaM sector memory backup {deletePath}: {exc.Message}";
            return false;
        }
    }

    public bool TryResetMemory(bool deletePersisted)
    {
        if (deletePersisted && !TryDeletePersistedMemory())
            return false;

        if (!TryGetMemory(out var memory))
            return false;

        var newsSeeded = memory.NewsSeeded;
        var contractsSeeded = memory.ContractsSeeded;

        memory.StoriesLoaded = false;
        memory.NewsSeeded = newsSeeded;
        memory.ContractsSeeded = contractsSeeded;
        memory.PersistenceLoaded = true;
        memory.Records.Clear();
        memory.ReputationLedger.Clear();
        memory.ReputationEntries.Clear();
        memory.HazardReports.Clear();
        memory.InsurancePayouts.Clear();
        memory.BlackBoxRecoveries.Clear();
        memory.CompanyRegistry.Clear();
        memory.ShipRegistry.Clear();
        memory.SectorConditions.Clear();
        memory.RescueAfterActions.Clear();
        memory.AiBase = new LuaMAiBaseState();

        EnsureRecords(memory);

        if (!deletePersisted)
            SaveMemory(memory);

        var ev = new LuaMSectorMemoryResetEvent(deletePersisted);
        RaiseLocalEvent(ev);

        return true;
    }

    public bool TryAcknowledgeHazard(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        string actor,
        string note,
        out LuaMSectorHazardReportEntry? entry)
    {
        entry = null;

        if (!TryGetStoryRecord(storyId, out var memory, out var record) ||
            string.IsNullOrWhiteSpace(record.Hazard) ||
            memory.HazardReports.Any(report => report.Story == storyId))
        {
            return false;
        }

        entry = new LuaMSectorHazardReportEntry
        {
            Story = record.Story,
            Hazard = record.Hazard,
            Actor = Trim(actor, 64),
            Note = Trim(note, 256),
        };
        memory.HazardReports.Add(entry);
        SaveMemory(memory);

        var ev = new LuaMSectorHazardAcknowledgedEvent(entry);
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryClaimInsurance(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        string actor,
        string note,
        out LuaMSectorInsurancePayoutEntry? entry)
    {
        entry = null;

        if (!TryGetStoryRecord(storyId, out var memory, out var record) ||
            string.IsNullOrWhiteSpace(record.Insurance) ||
            memory.InsurancePayouts.Any(payout => payout.Story == storyId))
        {
            return false;
        }

        var reputationBonus = GetReputationRewardBonus(record.ReputationTarget, memory.ReputationLedger);
        var amount = Math.Max(record.ContractReward + reputationBonus, 0);
        var paid = amount > 0 &&
                   _bank.TrySectorWithdraw(SectorBankAccount.Frontier, amount, LedgerEntryType.StationWithdrawalBounty);

        entry = new LuaMSectorInsurancePayoutEntry
        {
            Story = record.Story,
            Amount = amount,
            Paid = paid,
            Policy = record.Insurance,
            Actor = Trim(actor, 64),
            Note = Trim(note, 256),
        };
        memory.InsurancePayouts.Add(entry);
        SaveMemory(memory);

        var ev = new LuaMSectorInsuranceClaimedEvent(entry);
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryRecoverBlackBox(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        string actor,
        string note,
        out LuaMSectorBlackBoxRecoveryEntry? entry)
    {
        return TryRecoverBlackBox(storyId, actor, note, string.Empty, out entry);
    }

    public bool TryRecoverBlackBox(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        string actor,
        string note,
        string sourceSnapshot,
        out LuaMSectorBlackBoxRecoveryEntry? entry)
    {
        entry = null;

        if (!TryGetStoryRecord(storyId, out var memory, out var record) ||
            string.IsNullOrWhiteSpace(record.BlackBox) ||
            memory.BlackBoxRecoveries.Any(recovery => recovery.Story == storyId))
        {
            return false;
        }

        entry = new LuaMSectorBlackBoxRecoveryEntry
        {
            Story = record.Story,
            Recovery = record.BlackBox,
            Actor = Trim(actor, 64),
            Note = Trim(note, 256),
            SourceSnapshot = Trim(sourceSnapshot, 1536),
        };
        memory.BlackBoxRecoveries.Add(entry);
        SaveMemory(memory);

        var ev = new LuaMSectorBlackBoxRecoveredEvent(entry);
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryRegisterCompanyRecord(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        string actor,
        string note,
        out LuaMSectorRegistryEntry? entry)
    {
        entry = null;

        if (!TryGetStoryRecord(storyId, out var memory, out var record) ||
            string.IsNullOrWhiteSpace(record.CompanyRecord) ||
            memory.CompanyRegistry.Any(registryEntry => registryEntry.Story == storyId))
        {
            return false;
        }

        entry = new LuaMSectorRegistryEntry
        {
            Story = record.Story,
            Record = record.CompanyRecord,
            Actor = Trim(actor, 64),
            Note = Trim(note, 256),
        };
        memory.CompanyRegistry.Add(entry);
        SaveMemory(memory);

        var ev = new LuaMSectorCompanyRegisteredEvent(entry);
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryRegisterShipRecord(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        string actor,
        string note,
        out LuaMSectorRegistryEntry? entry)
    {
        entry = null;

        if (!TryGetStoryRecord(storyId, out var memory, out var record) ||
            string.IsNullOrWhiteSpace(record.ShipRecord) ||
            memory.ShipRegistry.Any(registryEntry => registryEntry.Story == storyId))
        {
            return false;
        }

        entry = new LuaMSectorRegistryEntry
        {
            Story = record.Story,
            Record = record.ShipRecord,
            Actor = Trim(actor, 64),
            Note = Trim(note, 256),
        };
        memory.ShipRegistry.Add(entry);
        SaveMemory(memory);

        var ev = new LuaMSectorShipRegisteredEvent(entry);
        RaiseLocalEvent(ev);
        return true;
    }

    public bool TryResolveStory(ProtoId<LuaMSectorStoryPrototype> storyId, string actor, string note)
    {
        if (!TryGetMemory(out var memory))
            return false;

        EnsureRecords(memory);

        var record = memory.Records.FirstOrDefault(record => record.Story == storyId);
        if (record == null || record.Resolved)
            return false;

        record.Resolved = true;
        record.ResolvedBy = Trim(actor, 64);
        record.ResolutionNote = Trim(note, 256);

        if (!string.IsNullOrWhiteSpace(record.ReputationTarget) && record.ReputationDelta != 0)
        {
            if (!memory.ReputationLedger.TryAdd(record.ReputationTarget, record.ReputationDelta))
                memory.ReputationLedger[record.ReputationTarget] += record.ReputationDelta;

            memory.ReputationEntries.Add(new LuaMSectorReputationLedgerEntry
            {
                Story = record.Story,
                Target = record.ReputationTarget,
                Delta = record.ReputationDelta,
                Actor = record.ResolvedBy,
                Note = record.ResolutionNote,
            });

            var reputationEvent = new LuaMSectorReputationChangedEvent(memory.ReputationEntries[^1]);
            RaiseLocalEvent(reputationEvent);
        }

        TryAcknowledgeHazard(storyId, record.ResolvedBy, record.ResolutionNote, out _);
        TryClaimInsurance(storyId, record.ResolvedBy, record.ResolutionNote, out _);
        TryRecoverBlackBox(storyId, record.ResolvedBy, record.ResolutionNote, out _);
        TryRegisterCompanyRecord(storyId, record.ResolvedBy, record.ResolutionNote, out _);
        TryRegisterShipRecord(storyId, record.ResolvedBy, record.ResolutionNote, out _);
        if (record.ActiveContractId is { } contractId)
        {
            _bountyContracts.TryRemoveGeneratedContract(contractId);
            record.ActiveContractId = null;
        }
        SaveMemory(memory);

        var resolvedEvent = new LuaMSectorStoryResolvedEvent(record.Story, record.ResolvedBy, record.ResolutionNote);
        RaiseLocalEvent(resolvedEvent);

        return true;
    }

    private bool TryGetMemory(out LuaMSectorMemoryComponent memory)
    {
        memory = default!;

        var service = _sectorService.GetServiceEntity();
        if (!service.IsValid() ||
            !TryComp<LuaMSectorMemoryComponent>(service, out var component))
        {
            return false;
        }

        memory = component;
        return true;
    }

    private bool TryGetStoryRecord(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        out LuaMSectorMemoryComponent memory,
        out LuaMSectorStoryRecord record)
    {
        record = default!;

        if (!TryGetMemory(out memory))
            return false;

        EnsureRecords(memory);

        var found = memory.Records.FirstOrDefault(record => record.Story == storyId);
        if (found == null)
            return false;

        record = found;
        return true;
    }

    private IReadOnlyList<LuaMSectorStoryRecord> GetRecords(Func<LuaMSectorStoryRecord, bool> predicate)
    {
        if (!TryGetMemory(out var memory))
            return [];

        EnsureRecords(memory);
        return memory.Records.Where(predicate).ToList();
    }

    private void EnsureRecords(LuaMSectorMemoryComponent memory)
    {
        if (memory.StoriesLoaded)
        {
            var unlocked = EnsureUnlockedRecords(memory);
            if (unlocked.Count > 0)
            {
                SaveMemory(memory);
                foreach (var story in unlocked)
                {
                    var ev = new LuaMSectorStoryUnlockedEvent(story.Story, story.RequiredReputationTarget, story.RequiredReputation);
                    RaiseLocalEvent(ev);
                }
            }
            return;
        }

        EnsureUnlockedRecords(memory);
        memory.StoriesLoaded = true;
    }

    private List<LuaMSectorStoryRecord> EnsureUnlockedRecords(LuaMSectorMemoryComponent memory)
    {
        var added = new List<LuaMSectorStoryRecord>();

        foreach (var story in _prototype.EnumeratePrototypes<LuaMSectorStoryPrototype>()
                     .Where(story => story.ID != RescueAfterActionStoryId))
        {
            if (!IsStoryUnlocked(story, memory.ReputationLedger) ||
                memory.Records.Any(record => record.Story == story.ID))
            {
                continue;
            }

            var record = new LuaMSectorStoryRecord
            {
                Story = story.ID,
                Title = story.Title,
                Author = story.Author,
                News = story.News,
                ContractCollection = story.ContractCollection,
                ContractCategory = story.ContractCategory,
                ContractName = story.ContractName,
                ContractVessel = story.ContractVessel,
                ContractDescription = story.ContractDescription,
                Hazard = story.Hazard,
                HazardSeverity = story.HazardSeverity,
                HazardRewardBonus = story.HazardRewardBonus,
                ReputationTarget = story.ReputationTarget,
                ReputationDelta = story.ReputationDelta,
                RequiredReputationTarget = story.RequiredReputationTarget,
                RequiredReputation = story.RequiredReputation,
                ContractReward = story.ContractReward,
                Insurance = story.Insurance,
                BlackBox = story.BlackBox,
                CompanyRecord = story.CompanyRecord,
                ShipRecord = story.ShipRecord,
            };

            memory.Records.Add(record);
            added.Add(record);
        }

        return added;
    }

    private void TrySeedNews(LuaMSectorMemoryComponent memory)
    {
        var newsQuery = EntityQueryEnumerator<SectorNewsComponent>();
        if (!newsQuery.MoveNext(out _))
            return;

        foreach (var record in memory.Records)
        {
            if (record.NewsSeeded)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(record.News))
            {
                record.NewsSeeded = true;
                continue;
            }

            var article = new NewsArticle
            {
                Title = Trim(record.Title, SharedNewsSystem.MaxTitleLength),
                Content = Trim(record.News, SharedNewsSystem.MaxContentLength),
                Author = record.Author,
                ShareTime = _ticker.RoundDuration(),
            };

            SectorNewsComponent.Articles.Add(article);

            var published = new NewsArticlePublishedEvent(article);
            var readerQuery = EntityQueryEnumerator<NewsReaderCartridgeComponent>();
            while (readerQuery.MoveNext(out var readerUid, out _))
            {
                RaiseLocalEvent(readerUid, ref published);
            }

            record.NewsSeeded = true;
        }

        memory.NewsSeeded = memory.Records.All(record => record.NewsSeeded);
    }

    private void TrySeedContracts(EntityUid service, LuaMSectorMemoryComponent memory)
    {
        foreach (var record in memory.Records)
        {
            if (record.Resolved)
            {
                record.ContractSeeded = true;
                record.ActiveContractId = null;
                continue;
            }

            if (record.ContractSeeded)
            {
                continue;
            }

            if (record.ContractCollection == null ||
                record.ContractCategory == null ||
                string.IsNullOrWhiteSpace(record.ContractName) ||
                string.IsNullOrWhiteSpace(record.ContractDescription))
            {
                record.ContractSeeded = true;
                continue;
            }

            var reputationBonus = GetReputationRewardBonus(record.ReputationTarget, memory.ReputationLedger);
            var reward = GetAdjustedReward(record, reputationBonus);
            var description = BuildGeneratedContractDescription(record, reputationBonus);

            var contract = _bountyContracts.TryCreateGeneratedBountyContract(
                record.ContractCollection.Value,
                record.ContractCategory.Value,
                Trim(record.ContractName, SharedBountyContractSystem.MaxNameLength),
                reward,
                service,
                Trim(description, SharedBountyContractSystem.MaxDescriptionLength),
                Trim(record.ContractVessel, SharedBountyContractSystem.MaxVesselLength),
                author: record.Author);
            if (contract != null)
            {
                record.ContractSeeded = true;
                record.ActiveContractId = contract.ContractId;
            }
        }

        memory.ContractsSeeded = memory.Records.All(record => record.ContractSeeded);
    }

    public bool TryBindContractRouteTarget(ProtoId<LuaMSectorStoryPrototype> story, EntityUid target)
    {
        if (!TryGetMemory(out var memory))
            return false;

        var record = memory.Records.FirstOrDefault(entry => entry.Story == story);
        return record?.ActiveContractId is { } contractId &&
               _bountyContracts.TrySetGeneratedContractRouteTarget(contractId, target);
    }

    private static bool IsStoryUnlocked(LuaMSectorStoryPrototype story, IReadOnlyDictionary<string, int> reputationLedger)
    {
        if (story.RequiredReputation <= 0)
            return true;

        if (string.IsNullOrWhiteSpace(story.RequiredReputationTarget))
            return false;

        return reputationLedger.TryGetValue(story.RequiredReputationTarget, out var reputation) &&
               reputation >= story.RequiredReputation;
    }

    private IReadOnlyList<LuaMSectorLockedStoryStatus> BuildLockedStoryStatuses(LuaMSectorMemoryComponent memory)
    {
        return _prototype.EnumeratePrototypes<LuaMSectorStoryPrototype>()
            .Where(story => story.ID != RescueAfterActionStoryId)
            .Where(story => !memory.Records.Any(record => record.Story == story.ID))
            .OrderBy(story => story.RequiredReputationTarget)
            .ThenBy(story => story.RequiredReputation)
            .ThenBy(story => story.Title)
            .Select(story => new LuaMSectorLockedStoryStatus(
                story.ID,
                story.Title,
                story.RequiredReputationTarget,
                story.RequiredReputation,
                GetReputationValue(story.RequiredReputationTarget, memory.ReputationLedger)))
            .ToList();
    }

    private static IReadOnlyList<LuaMSectorHistoryStatus> BuildRecentHistoryStatuses(LuaMSectorMemoryComponent memory)
    {
        var titles = memory.Records.ToDictionary(record => record.Story, record => record.Title);
        var entries = new List<(int Index, int Priority, LuaMSectorHistoryStatus Entry)>();

        for (var i = 0; i < memory.ReputationEntries.Count; i++)
        {
            var entry = memory.ReputationEntries[i];
            AddHistory(entries, titles, i, 0, "Reputation", entry.Story, entry.Actor, $"{entry.Target} {entry.Delta:+#;-#;0}: {entry.Note}");
        }

        for (var i = 0; i < memory.HazardReports.Count; i++)
        {
            var entry = memory.HazardReports[i];
            AddHistory(entries, titles, i, 1, "Hazard", entry.Story, entry.Actor, entry.Note);
        }

        for (var i = 0; i < memory.InsurancePayouts.Count; i++)
        {
            var entry = memory.InsurancePayouts[i];
            var state = entry.Paid ? "paid" : "unpaid";
            AddHistory(entries, titles, i, 2, "Insurance", entry.Story, entry.Actor, $"{entry.Amount} {state}: {entry.Note}");
        }

        for (var i = 0; i < memory.BlackBoxRecoveries.Count; i++)
        {
            var entry = memory.BlackBoxRecoveries[i];
            var summary = string.IsNullOrWhiteSpace(entry.SourceSnapshot)
                ? entry.Note
                : $"{entry.Note}; {entry.SourceSnapshot}";
            AddHistory(entries, titles, i, 3, "Black box", entry.Story, entry.Actor, summary);
        }

        for (var i = 0; i < memory.CompanyRegistry.Count; i++)
        {
            var entry = memory.CompanyRegistry[i];
            AddHistory(entries, titles, i, 4, "Company", entry.Story, entry.Actor, entry.Note);
        }

        for (var i = 0; i < memory.ShipRegistry.Count; i++)
        {
            var entry = memory.ShipRegistry[i];
            AddHistory(entries, titles, i, 5, "Ship", entry.Story, entry.Actor, entry.Note);
        }

        for (var i = 0; i < memory.RescueAfterActions.Count; i++)
        {
            var entry = memory.RescueAfterActions[i];
            AddHistory(entries, titles, entry.Sequence, 6, "Rescue", RescueAfterActionStory, entry.Actor, entry.Summary);
        }

        return entries
            .OrderByDescending(entry => entry.Index)
            .ThenBy(entry => entry.Priority)
            .Take(RecentHistoryLimit)
            .Select(entry => entry.Entry)
            .ToList();
    }

    private static void AddHistory(
        List<(int Index, int Priority, LuaMSectorHistoryStatus Entry)> entries,
        IReadOnlyDictionary<ProtoId<LuaMSectorStoryPrototype>, string> titles,
        int index,
        int priority,
        string category,
        ProtoId<LuaMSectorStoryPrototype> story,
        string actor,
        string summary)
    {
        var title = titles.TryGetValue(story, out var recordTitle)
            ? recordTitle
            : story.ToString();

        entries.Add((index, priority, new LuaMSectorHistoryStatus(
            category,
            story,
            title,
            actor,
            summary)));
    }

    private static int GetAdjustedReward(LuaMSectorStoryRecord record, int reputationBonus)
    {
        var reward = Math.Max(record.ContractReward, 0) + reputationBonus;
        if (string.IsNullOrWhiteSpace(record.Hazard))
            return reward;

        return Math.Max(reward + record.HazardRewardBonus, 0);
    }

    private static int GetReputationRewardBonus(string target, IReadOnlyDictionary<string, int> reputationLedger)
    {
        var reputation = GetReputationValue(target, reputationLedger);
        if (reputation <= 0)
        {
            return 0;
        }

        return Math.Min(reputation * ReputationRewardStep, ReputationRewardCap);
    }

    private static int GetReputationValue(string target, IReadOnlyDictionary<string, int> reputationLedger)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            !reputationLedger.TryGetValue(target, out var reputation))
        {
            return 0;
        }

        return reputation;
    }

    private static IReadOnlyList<LuaMSectorReputationStatus> BuildReputationStatuses(IReadOnlyDictionary<string, int> reputationLedger)
    {
        return reputationLedger
            .OrderBy(entry => entry.Key)
            .Select(entry => new LuaMSectorReputationStatus(
                entry.Key,
                entry.Value,
                GetReputationTier(entry.Value),
                GetReputationRewardBonus(entry.Key, reputationLedger)))
            .ToList();
    }

    private static string GetReputationTier(int reputation)
    {
        if (reputation >= 6)
            return "preferred";

        if (reputation >= 3)
            return "trusted";

        if (reputation > 0)
            return "known";

        return "unrated";
    }

    private static string BuildGeneratedContractDescription(LuaMSectorStoryRecord record, int reputationBonus)
    {
        var output = new StringBuilder();
        output.Append(record.ContractDescription);

        if (reputationBonus > 0)
            output.Append($" [REP +{reputationBonus} {record.ReputationTarget}]");

        if (!string.IsNullOrWhiteSpace(record.Hazard))
            output.Append($" [HZ-{record.HazardSeverity}] {record.Hazard}");

        return output.ToString();
    }

    private static ProtoId<LuaMSectorStoryPrototype> GetNextRuntimeDistressId(LuaMSectorMemoryComponent memory)
    {
        var index = memory.Records.Count(record => record.Story.ToString().StartsWith(RuntimeDistressStoryPrefix, StringComparison.Ordinal)) + 1;
        string candidate;

        do
        {
            candidate = $"{RuntimeDistressStoryPrefix}{index:000}";
            index++;
        }
        while (memory.Records.Any(record => record.Story == candidate));

        return candidate;
    }

    private void TryLoadPersistedMemory(LuaMSectorMemoryComponent memory)
    {
        if (memory.PersistenceLoaded)
            return;

        memory.PersistenceLoaded = true;

        try
        {
            if (!_resource.UserData.Exists(PersistencePath) ||
                !_resource.UserData.TryReadAllText(PersistencePath, out var json))
            {
                return;
            }

            var persisted = JsonSerializer.Deserialize<LuaMSectorPersistedMemory>(json, PersistenceJsonOptions);
            if (persisted == null)
                return;

            ApplyPersistedMemory(memory, persisted);
        }
        catch (Exception exc)
        {
            Log.Warning($"Failed to load LuaM sector memory from {PersistencePath}: {exc}");
        }
    }

    private void SaveMemory(LuaMSectorMemoryComponent memory)
    {
        try
        {
            _resource.UserData.CreateDir(PersistenceDirectory);
            var json = JsonSerializer.Serialize(ToPersistedMemory(memory), PersistenceJsonOptions);
            _resource.UserData.WriteAllText(PersistencePath, json);
        }
        catch (Exception exc)
        {
            Log.Warning($"Failed to save LuaM sector memory to {PersistencePath}: {exc}");
        }
    }

    private bool TryDeletePersistedMemory()
    {
        try
        {
            _resource.UserData.Delete(PersistencePath);
            return true;
        }
        catch (Exception exc)
        {
            Log.Warning($"Failed to delete LuaM sector memory from {PersistencePath}: {exc}");
            return false;
        }
    }

    private static bool TryGetBackupPath(string rawPath, out ResPath path, out string error)
    {
        path = default;
        error = string.Empty;

        var trimmed = rawPath.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "LuaM sector memory backup path is empty.";
            return false;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains(':', StringComparison.Ordinal))
        {
            error = "LuaM sector memory backup path must stay under /luam.";
            return false;
        }

        if (!trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            trimmed += ".json";

        if (!trimmed.StartsWith(ResPath.Separator))
            trimmed = $"{PersistenceDirectory}/{trimmed}";

        path = new ResPath(trimmed).ToRootedPath();
        if (!path.TryRelativeTo(PersistenceDirectory, out _))
        {
            error = "LuaM sector memory backup path must stay under /luam.";
            return false;
        }

        return true;
    }

    private static LuaMSectorPersistedMemory ToPersistedMemory(LuaMSectorMemoryComponent memory)
    {
        return new LuaMSectorPersistedMemory
        {
            StoriesLoaded = memory.StoriesLoaded,
            NewsSeeded = memory.NewsSeeded,
            ContractsSeeded = memory.ContractsSeeded,
            Records = memory.Records.Select(ToPersistedStoryRecord).ToList(),
            ReputationLedger = new Dictionary<string, int>(memory.ReputationLedger),
            ReputationEntries = memory.ReputationEntries.Select(ToPersistedReputationEntry).ToList(),
            HazardReports = memory.HazardReports.Select(ToPersistedHazardReport).ToList(),
            InsurancePayouts = memory.InsurancePayouts.Select(ToPersistedInsurancePayout).ToList(),
            BlackBoxRecoveries = memory.BlackBoxRecoveries.Select(ToPersistedBlackBoxRecovery).ToList(),
            CompanyRegistry = memory.CompanyRegistry.Select(ToPersistedRegistryEntry).ToList(),
            ShipRegistry = memory.ShipRegistry.Select(ToPersistedRegistryEntry).ToList(),
            SectorConditions = memory.SectorConditions.Select(ToPersistedConditionEntry).ToList(),
            RescueAfterActions = memory.RescueAfterActions.Select(ToPersistedRescueAfterAction).ToList(),
            AiBase = ToPersistedAiBase(memory.AiBase),
        };
    }

    private static void ApplyPersistedMemory(LuaMSectorMemoryComponent memory, LuaMSectorPersistedMemory persisted)
    {
        memory.StoriesLoaded = persisted.StoriesLoaded;
        memory.NewsSeeded = false;
        memory.ContractsSeeded = false;
        memory.Records = persisted.Records.Select(FromPersistedStoryRecord).ToList();
        foreach (var record in memory.Records)
        {
            record.NewsSeeded = false;
            record.ContractSeeded = false;
        }
        memory.ReputationLedger = new Dictionary<string, int>(persisted.ReputationLedger);
        memory.ReputationEntries = persisted.ReputationEntries.Select(FromPersistedReputationEntry).ToList();
        memory.HazardReports = persisted.HazardReports.Select(FromPersistedHazardReport).ToList();
        memory.InsurancePayouts = persisted.InsurancePayouts.Select(FromPersistedInsurancePayout).ToList();
        memory.BlackBoxRecoveries = persisted.BlackBoxRecoveries.Select(FromPersistedBlackBoxRecovery).ToList();
        memory.CompanyRegistry = persisted.CompanyRegistry.Select(FromPersistedRegistryEntry).ToList();
        memory.ShipRegistry = persisted.ShipRegistry.Select(FromPersistedRegistryEntry).ToList();
        memory.SectorConditions = persisted.SectorConditions.Select(FromPersistedConditionEntry).ToList();
        memory.RescueAfterActions = persisted.RescueAfterActions?.Select(FromPersistedRescueAfterAction).ToList() ?? new();
        memory.AiBase = FromPersistedAiBase(persisted.AiBase);
    }

    private static LuaMAiBaseState EnsureAiBaseState(
        LuaMSectorMemoryComponent memory,
        string actor,
        TimeSpan now,
        out bool createdNow)
    {
        memory.AiBase ??= new LuaMAiBaseState();
        var state = memory.AiBase;
        EnsureAiBaseDefaults(state);

        createdNow = !state.Created;
        if (!createdNow)
            return state;

        state.Created = true;
        state.SupplyScore = CalculateAiBaseSupplyScore(state);
        RefreshAiBaseBehavior(state, now, "base-created");
        state.TradeLog.Add(new LuaMAiBaseTradeEntry
        {
            Cycle = state.TradeCycles,
            Role = "base",
            Vessel = state.Name,
            Resource = "base",
            Amount = 1,
            Actor = Trim(actor, 64),
            Summary = "AI base deployed and initial stock ledger created.",
        });

        return state;
    }

    private static void EnsureAiBaseDefaults(LuaMAiBaseState state)
    {
        if (string.IsNullOrWhiteSpace(state.BaseId))
            state.BaseId = "LuaM-AI-Base";
        if (string.IsNullOrWhiteSpace(state.Name))
            state.Name = "LuaM autonomous supply base";
        if (string.IsNullOrWhiteSpace(state.Location))
            state.Location = "hidden sector anchorage";

        state.Inventory ??= new List<LuaMAiBaseInventoryEntry>();
        state.Needs ??= new List<LuaMAiBaseNeedEntry>();
        state.TradeLog ??= new List<LuaMAiBaseTradeEntry>();
        state.AutofixLog ??= new List<LuaMAiBaseAutofixEntry>();
        if (string.IsNullOrWhiteSpace(state.LastRescueMedicalStatus))
            state.LastRescueMedicalStatus = "none";
        if (string.IsNullOrWhiteSpace(state.LastRescueMedicalLocation))
            state.LastRescueMedicalLocation = "none";
        if (string.IsNullOrWhiteSpace(state.LastRescueCooldownStatus))
            state.LastRescueCooldownStatus = "none";
        state.RescueMedicalOperations = Math.Max(0, state.RescueMedicalOperations);
        state.LastRescueAfterActionSequence = Math.Max(0, state.LastRescueAfterActionSequence);
        state.LastRescueCooldownSequence = Math.Max(0, state.LastRescueCooldownSequence);
        state.LastRescueCooldownSeconds = Math.Max(0, state.LastRescueCooldownSeconds);

        foreach (var (resource, amount) in AiBaseInitialInventory)
        {
            if (state.Inventory.Any(entry => ResourceEquals(entry.Resource, resource)))
                continue;

            state.Inventory.Add(new LuaMAiBaseInventoryEntry
            {
                Resource = resource,
                Amount = amount,
            });
        }

        foreach (var (resource, target, priority) in AiBaseDefaultNeeds)
        {
            if (state.Needs.Any(entry => ResourceEquals(entry.Resource, resource)))
                continue;

            state.Needs.Add(new LuaMAiBaseNeedEntry
            {
                Resource = resource,
                Target = target,
                Priority = priority,
            });
        }

        state.Inventory = state.Inventory
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Resource))
            .GroupBy(entry => NormalizeAiBaseResource(entry.Resource), StringComparer.OrdinalIgnoreCase)
            .Select(group => new LuaMAiBaseInventoryEntry
            {
                Resource = group.Key,
                Amount = group.Sum(entry => entry.Amount),
            })
            .OrderBy(entry => entry.Resource, StringComparer.OrdinalIgnoreCase)
            .ToList();

        state.Needs = state.Needs
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Resource) && entry.Target > 0)
            .GroupBy(entry => NormalizeAiBaseResource(entry.Resource), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var highest = group
                    .OrderByDescending(entry => entry.Priority)
                    .ThenByDescending(entry => entry.Target)
                    .First();
                return new LuaMAiBaseNeedEntry
                {
                    Resource = group.Key,
                    Target = Math.Max(1, highest.Target),
                    Priority = Math.Clamp(highest.Priority, 1, 5),
                };
            })
            .OrderByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.Resource, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EnsureAiBaseFactionDefaults(state);
        EnsureAiBaseBehaviorDefaults(state);
        RefreshAiBaseDerivedState(state, countRevision: false);
    }

    private static void EnsureAiBaseFactionDefaults(LuaMAiBaseState state)
    {
        if (string.IsNullOrWhiteSpace(state.FactionId))
            state.FactionId = "luam-ai-contour";
        if (string.IsNullOrWhiteSpace(state.FactionName))
            state.FactionName = "LuaM AI Contour";
        if (string.IsNullOrWhiteSpace(state.FactionCharter))
            state.FactionCharter = "autonomous in-game faction for resource extraction, base construction, logistics, medical support, and improvement audits";
        if (string.IsNullOrWhiteSpace(state.AutonomyModel))
            state.AutonomyModel = "mixed-initiative shared autonomy";
    }

    private static void EnsureAiBaseBehaviorDefaults(LuaMAiBaseState state)
    {
        state.BehaviorRevision = Math.Max(0, state.BehaviorRevision);
        var decision = SelectAiBaseBehaviorDecision(state);

        if (string.IsNullOrWhiteSpace(state.BehaviorMode))
            state.BehaviorMode = decision.Mode;
        if (string.IsNullOrWhiteSpace(state.BehaviorFocusResource))
            state.BehaviorFocusResource = decision.FocusResource;
        if (string.IsNullOrWhiteSpace(state.BehaviorFocusRole))
            state.BehaviorFocusRole = decision.FocusRole;
        if (string.IsNullOrWhiteSpace(state.BehaviorDirective))
            state.BehaviorDirective = decision.Directive;
        if (string.IsNullOrWhiteSpace(state.BehaviorReason))
            state.BehaviorReason = decision.Reason;
        if (string.IsNullOrWhiteSpace(state.LastBehaviorTrigger))
            state.LastBehaviorTrigger = "defaults";
    }

    private static string BuildAiBaseStatusText(LuaMAiBaseState state)
    {
        EnsureAiBaseDefaults(state);
        state.SupplyScore = CalculateAiBaseSupplyScore(state);

        if (state is not { Created: true })
            return $"AI base is not deployed. Faction: {state.FactionName} ({state.FactionId}); autonomy={state.AutonomyModel}; improvement={state.ImprovementLoopState}/{state.LastImprovementFocus}. Use command: create AI base. Compensation plan: deploy physical base anchor, then assign dock/storage/mining/patrol/contact zones.";

        var output = new StringBuilder();
        output.AppendLine($"{state.Name} [{state.BaseId}] at {state.Location}: supply score {state.SupplyScore}/100, logistics cycles {state.TradeCycles}.");
        output.AppendLine($"Faction: {state.FactionName} ({state.FactionId}); autonomy={state.AutonomyModel}; charter={state.FactionCharter}.");
        output.AppendLine($"Behavior: mode={state.BehaviorMode}; focus={state.BehaviorFocusResource}/{state.BehaviorFocusRole}; directive={state.BehaviorDirective}; reason={state.BehaviorReason}; revision={state.BehaviorRevision}; trigger={state.LastBehaviorTrigger}.");
        output.AppendLine($"Roles: {BuildAiBaseRoleDoctrineSummary(state)}");
        output.AppendLine($"Improvement loop: state={state.ImprovementLoopState}; focus={state.LastImprovementFocus}; finding={state.LastImprovementFinding}; revision={state.ImprovementRevision}.");

        if (state.Needs.Count == 0)
        {
            output.AppendLine("Needs: none configured.");
        }
        else
        {
            output.AppendLine("Needs: " + string.Join(", ", state.Needs
                .OrderByDescending(need => need.Priority)
                .ThenBy(need => need.Resource, StringComparer.OrdinalIgnoreCase)
                .Select(need => $"{need.Resource} {GetAiBaseInventoryAmount(state, need.Resource)}/{need.Target} p{need.Priority}")));
        }

        if (state.Inventory.Count == 0)
        {
            output.AppendLine("Inventory: empty.");
        }
        else
        {
            output.AppendLine("Inventory: " + string.Join(", ", state.Inventory
                .OrderBy(entry => entry.Resource, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{entry.Resource}={entry.Amount}")));
        }

        output.AppendLine("Compensation plan: " + BuildAiBaseCompensationSummary(state, 3));
        output.AppendLine("Medical status: " + BuildAiBaseMedicalStatusText(state));

        var recent = state.TradeLog
            .OrderByDescending(entry => entry.Cycle)
            .Take(3)
            .Select(entry => $"#{entry.Cycle} {entry.Summary}");
        output.AppendLine("Recent logistics: " + (state.TradeLog.Count == 0 ? "none." : string.Join(" | ", recent)));
        var recentAutofix = state.AutofixLog
            .OrderByDescending(entry => entry.Attempt)
            .Take(3)
            .Select(entry => $"#{entry.Attempt} {entry.CommandId} {(entry.Success ? "ok" : "failed")}: {entry.Issue}");
        output.AppendLine("Recent autofix: " + (state.AutofixLog.Count == 0 ? "none." : string.Join(" | ", recentAutofix)));
        return output.ToString().TrimEnd();
    }

    private static AiBaseDelivery SelectAiBaseDelivery(LuaMAiBaseState state, string role)
    {
        if (role == "scout")
            return new AiBaseDelivery("route-data", 1, 0, "mapped a new trade route for later haulers");
        if (role == "medic")
            return SelectBoundedNeedDelivery(state, ["medicine"], 18, "delivered medical stock and refreshed triage reserves");
        if (role == "service")
            return SelectBoundedNeedDelivery(state, ["food"], 18, "delivered crew support stock and service reserves");
        if (role == "guard")
            return new AiBaseDelivery("security", 1, 0, "reinforced patrol coverage and base perimeter confidence");

        var need = SelectMostMissingAiBaseNeed(state);
        if (need == null)
        {
            return role == "trader"
                ? new AiBaseDelivery("credits", 220, 0, "sold surplus manifests and returned credits")
                : new AiBaseDelivery("fuel", 12, 0, "stockpiled reserve fuel because configured needs are already satisfied");
        }

        var current = GetAiBaseInventoryAmount(state, need.Resource);
        var missing = Math.Max(1, need.Target - current);
        if (role == "trader")
        {
            var amount = Math.Clamp((int) Math.Ceiling(missing * 0.45f), 8, 20);
            amount = Math.Min(amount, missing);
            return new AiBaseDelivery(need.Resource, amount, -amount * 5, $"traded credits for {need.Resource}");
        }

        var supplyAmount = role == "builder"
            ? 22
            : 28;
        var delivered = Math.Min(Math.Max(10, supplyAmount), missing);
        return new AiBaseDelivery(need.Resource, delivered, 0, $"delivered priority stock for {need.Resource}");
    }

    private static AiBaseDelivery SelectAiBaseDroneTaskDelivery(
        LuaMAiBaseState state,
        string role,
        string taskType,
        string zoneType)
    {
        _ = taskType;
        _ = zoneType;

        return role switch
        {
            "repair" => SelectBoundedNeedDelivery(
                state,
                ["hull-parts", "electronics"],
                5,
                "fabricated repair stock and refreshed maintenance spares"),
            "logistics" => SelectDroneLogisticsDelivery(state),
            "guard" => new AiBaseDelivery("security", 1, 0, "stabilized patrol coverage and marked a safer perimeter"),
            "scout" => new AiBaseDelivery("route-data", 1, 0, "mapped a route contact and refreshed approach data"),
            "medic" => SelectBoundedNeedDelivery(
                state,
                ["medicine"],
                3,
                "prepared triage supplies and evacuation markers"),
            "service" => SelectBoundedNeedDelivery(
                state,
                ["food"],
                4,
                "organized crew support stock and service routes"),
            "miner" => new AiBaseDelivery("ore", 6, 0, "queued a small ore packet from a local mining route"),
            _ => new AiBaseDelivery("fuel", 2, 0, "kept dock reserves warm for the next logistics action"),
        };
    }

    private static AiBaseDelivery SelectDroneLogisticsDelivery(LuaMAiBaseState state)
    {
        var need = SelectMostMissingAiBaseNeed(state);
        if (need == null)
            return new AiBaseDelivery("credits", 25, 0, "balanced surplus manifests and returned service credits");

        var current = GetAiBaseInventoryAmount(state, need.Resource);
        var missing = Math.Max(1, need.Target - current);
        var amount = Math.Min(Math.Clamp((int) Math.Ceiling(missing * 0.12f), 3, 10), missing);
        return new AiBaseDelivery(need.Resource, amount, 0, $"moved local supplies toward the {need.Resource} deficit");
    }

    private static AiBaseDelivery SelectBoundedNeedDelivery(
        LuaMAiBaseState state,
        IReadOnlyList<string> resources,
        int defaultAmount,
        string summary)
    {
        var resource = resources
            .OrderByDescending(item =>
            {
                var target = GetAiBaseNeedTarget(state, item);
                var current = GetAiBaseInventoryAmount(state, item);
                return Math.Max(0, target - current);
            })
            .ThenBy(item => item, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "materials";

        var target = GetAiBaseNeedTarget(state, resource);
        var current = GetAiBaseInventoryAmount(state, resource);
        var missing = target > 0 ? Math.Max(1, target - current) : defaultAmount;
        var amount = Math.Min(Math.Clamp(defaultAmount, 1, 12), missing);
        return new AiBaseDelivery(resource, amount, 0, summary);
    }

    private static LuaMAiBaseNeedEntry? SelectMostMissingAiBaseNeed(LuaMAiBaseState state)
    {
        return state.Needs
            .Where(need => need.Target > GetAiBaseInventoryAmount(state, need.Resource))
            .OrderByDescending(need => (need.Target - GetAiBaseInventoryAmount(state, need.Resource)) * Math.Max(1, need.Priority))
            .ThenByDescending(need => need.Priority)
            .ThenBy(need => need.Resource, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static AiBaseBehaviorDecision RefreshAiBaseBehavior(
        LuaMAiBaseState state,
        TimeSpan now,
        string trigger)
    {
        EnsureAiBaseDefaults(state);
        state.SupplyScore = CalculateAiBaseSupplyScore(state);

        var decision = SelectAiBaseBehaviorDecision(state);
        trigger = string.IsNullOrWhiteSpace(trigger) ? "state-refresh" : Trim(trigger, 64);
        var changed = !state.BehaviorMode.Equals(decision.Mode, StringComparison.OrdinalIgnoreCase) ||
                      !state.BehaviorFocusResource.Equals(decision.FocusResource, StringComparison.OrdinalIgnoreCase) ||
                      !state.BehaviorFocusRole.Equals(decision.FocusRole, StringComparison.OrdinalIgnoreCase) ||
                      !state.BehaviorDirective.Equals(decision.Directive, StringComparison.Ordinal) ||
                      !state.BehaviorReason.Equals(decision.Reason, StringComparison.Ordinal) ||
                      !state.LastBehaviorTrigger.Equals(trigger, StringComparison.OrdinalIgnoreCase);

        state.BehaviorMode = decision.Mode;
        state.BehaviorFocusResource = decision.FocusResource;
        state.BehaviorFocusRole = decision.FocusRole;
        state.BehaviorDirective = decision.Directive;
        state.BehaviorReason = decision.Reason;
        state.LastBehaviorTrigger = trigger;

        if (changed)
        {
            state.BehaviorRevision = Math.Max(0, state.BehaviorRevision) + 1;
            state.BehaviorUpdatedAt = now;
        }

        RefreshAiBaseDerivedState(state, countRevision: changed);
        return decision;
    }

    private static void RefreshAiBaseDerivedState(LuaMAiBaseState state, bool countRevision)
    {
        var improvement = SelectAiBaseImprovementDecision(state);
        var changed = !string.Equals(state.ImprovementLoopState, improvement.State, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(state.LastImprovementFocus, improvement.Focus, StringComparison.Ordinal) ||
                      !string.Equals(state.LastImprovementFinding, improvement.Finding, StringComparison.Ordinal);

        state.ImprovementLoopState = improvement.State;
        state.LastImprovementFocus = improvement.Focus;
        state.LastImprovementFinding = improvement.Finding;
        state.RoleDoctrine = BuildAiBaseRoleDoctrine(state);

        if (changed && countRevision)
            state.ImprovementRevision = Math.Max(0, state.ImprovementRevision) + 1;
        else
            state.ImprovementRevision = Math.Max(0, state.ImprovementRevision);
    }

    private static List<LuaMAiBaseRoleEntry> BuildAiBaseRoleDoctrine(LuaMAiBaseState state)
    {
        var focusRole = NormalizeAiBaseDoctrineRole(state.BehaviorFocusRole);
        var mode = (state.BehaviorMode ?? string.Empty).Trim().ToLowerInvariant();
        var activeSupport = mode switch
        {
            "bootstrap" => new[] { "builder", "hauler", "guard" },
            "logistics-start" => new[] { "hauler", focusRole, "scout" },
            "critical-recovery" => new[] { focusRole, "hauler", "operator" },
            "extraction" => new[] { "miner", "hauler", "scout" },
            "construction" => new[] { "builder", "hauler", "guard" },
            "medical-followup" => new[] { "medic", "hauler", "guard" },
            "medical-support" => new[] { "medic", "hauler", "scout" },
            "crew-support" => new[] { "service", "hauler", "guard" },
            "stable-watch" => new[] { "scout", "guard", "operator" },
            _ => new[] { focusRole, "hauler", "operator" },
        };
        var active = new HashSet<string>(activeSupport.Where(role => !string.IsNullOrWhiteSpace(role)), StringComparer.OrdinalIgnoreCase)
        {
            focusRole,
            "operator",
        };

        return new[]
            {
                BuildAiBaseRoleEntry("builder", "construction", state, active),
                BuildAiBaseRoleEntry("miner", "resource extraction", state, active),
                BuildAiBaseRoleEntry("hauler", "logistics", state, active),
                BuildAiBaseRoleEntry("medic", "medical support", state, active),
                BuildAiBaseRoleEntry("guard", "security", state, active),
                BuildAiBaseRoleEntry("scout", "route intelligence", state, active),
                BuildAiBaseRoleEntry("service", "crew support", state, active),
                BuildAiBaseRoleEntry("operator", "audit and autofix", state, active),
            }
            .OrderBy(entry => entry.Active ? 0 : 1)
            .ThenBy(entry => entry.Priority.Equals("primary", StringComparison.OrdinalIgnoreCase) ? 0 : entry.Priority.Equals("support", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(entry => entry.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LuaMAiBaseRoleEntry BuildAiBaseRoleEntry(
        string role,
        string service,
        LuaMAiBaseState state,
        IReadOnlySet<string> activeRoles)
    {
        var focusRole = NormalizeAiBaseDoctrineRole(state.BehaviorFocusRole);
        var isPrimary = role.Equals(focusRole, StringComparison.OrdinalIgnoreCase);
        var isActive = activeRoles.Contains(role);
        return new LuaMAiBaseRoleEntry
        {
            Role = role,
            Service = service,
            Priority = isPrimary ? "primary" : isActive ? "support" : "standby",
            Directive = BuildAiBaseRoleDirective(role, state),
            Active = isActive,
        };
    }

    private static string BuildAiBaseRoleDirective(string role, LuaMAiBaseState state)
    {
        return role switch
        {
            "builder" => $"build/repair base assets while doctrine is {state.BehaviorMode}",
            "miner" => $"close ore/resource deficits for focus {state.BehaviorFocusResource}",
            "hauler" => $"move supplies toward {state.BehaviorFocusResource} and keep trade cycles alive",
            "medic" => "hold medicine stock, triage cache, and rescue follow-up support",
            "guard" => "protect base anchor, ships, and supply drops",
            "scout" => "map route-data and detect stuck or unreachable work zones",
            "service" => "support crew needs, food stock, and low-risk service routes",
            "operator" => $"audit diagnostics and propose autofix for {state.LastImprovementFocus}",
            _ => $"support base doctrine {state.BehaviorMode}",
        };
    }

    private static string NormalizeAiBaseDoctrineRole(string role)
    {
        return (role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "builder" or "engineer" or "repair" => "builder",
            "miner" or "prospector" => "miner",
            "hauler" or "trader" or "logistics" => "hauler",
            "medical" or "doctor" or "medic" => "medic",
            "security" or "guard" => "guard",
            "scout" => "scout",
            "janitor" or "cleaner" or "service" => "service",
            "operator" or "audit" => "operator",
            _ => "hauler",
        };
    }

    private static string BuildAiBaseRoleDoctrineSummary(LuaMAiBaseState state)
    {
        if (state.RoleDoctrine == null || state.RoleDoctrine.Count == 0)
            return "none configured.";

        return string.Join(", ", state.RoleDoctrine
            .Select(entry => $"{entry.Role}:{entry.Priority}/{(entry.Active ? "active" : "standby")}"));
    }

    private static AiBaseImprovementDecision SelectAiBaseImprovementDecision(LuaMAiBaseState state)
    {
        var lastAutofix = state.AutofixLog
            .OrderByDescending(entry => entry.Attempt)
            .FirstOrDefault();
        if (lastAutofix is { Success: false })
        {
            return new AiBaseImprovementDecision(
                "verify-failed-autofix",
                lastAutofix.CommandId,
                $"last autofix failed: {lastAutofix.Issue}");
        }

        if (!state.Created)
        {
            return new AiBaseImprovementDecision(
                "bootstrap-blocked",
                "deploy base anchor",
                "ai base memory or physical anchor is missing");
        }

        if (state.LastRescueMedicalFollowUpPending)
        {
            return new AiBaseImprovementDecision(
                "medical-followup",
                "clear rescue medical follow-up",
                $"pending rescue follow-up at {state.LastRescueMedicalLocation}");
        }

        if (state.TradeCycles <= 0)
        {
            return new AiBaseImprovementDecision(
                "start-first-cycle",
                "run first logistics/development cycle",
                "tradeCycles=0; stock ledger is not proven yet");
        }

        var topNeed = SelectMostMissingAiBaseNeed(state);
        if (topNeed != null)
        {
            var resource = NormalizeAiBaseResource(topNeed.Resource);
            var current = GetAiBaseInventoryAmount(state, resource);
            return new AiBaseImprovementDecision(
                "close-resource-deficit",
                $"close {resource} deficit",
                $"{resource} {current}/{topNeed.Target}; dispatch {PickAiBaseCompensationRole(resource)}");
        }

        return new AiBaseImprovementDecision(
            "stable-watch",
            "keep audit loop running",
            $"supplyScore={state.SupplyScore}/100; no configured deficits");
    }

    private static AiBaseBehaviorDecision SelectAiBaseBehaviorDecision(LuaMAiBaseState state)
    {
        var topNeed = SelectMostMissingAiBaseNeed(state);
        var focusResource = topNeed == null ? "route-data" : NormalizeAiBaseResource(topNeed.Resource);
        var focusRole = PickAiBaseCompensationRole(focusResource);
        var current = topNeed == null ? 0 : GetAiBaseInventoryAmount(state, topNeed.Resource);
        var target = topNeed?.Target ?? 0;

        if (!state.Created)
        {
            return new AiBaseBehaviorDecision(
                "bootstrap",
                "base",
                "builder",
                "deploy base anchor, mark dock/storage/mining zones, then start logistics",
                "aiBaseCreated=false");
        }

        if (state.LastRescueMedicalFollowUpPending)
        {
            return new AiBaseBehaviorDecision(
                "medical-followup",
                "medicine",
                "medic",
                "hold medicine, medic, logistics, and guard coverage until rescue follow-up is cleared",
                $"rescue follow-up pending at {state.LastRescueMedicalLocation}");
        }

        if (state.TradeCycles <= 0)
        {
            return new AiBaseBehaviorDecision(
                "logistics-start",
                focusResource,
                focusRole,
                $"run first supply cycle toward {focusResource} before surplus work",
                "tradeCycles=0");
        }

        if (state.SupplyScore < 45)
        {
            return new AiBaseBehaviorDecision(
                "critical-recovery",
                focusResource,
                focusRole,
                $"lock crew and ship priorities on {focusResource} until supply score leaves critical range",
                $"supplyScore={state.SupplyScore}/100; topNeed={focusResource} {current}/{target}");
        }

        if (topNeed == null)
        {
            return new AiBaseBehaviorDecision(
                "stable-watch",
                "route-data",
                "scout",
                "keep scout, guard, and surplus trade cycles active while monitoring new deficits",
                $"all configured needs met; supplyScore={state.SupplyScore}/100");
        }

        var mode = focusResource switch
        {
            "ore" => "extraction",
            "hull-parts" or "electronics" => "construction",
            "medicine" => "medical-support",
            "food" => "crew-support",
            _ => "balanced-logistics",
        };
        var directive = mode switch
        {
            "extraction" => "prioritize miners and ore handling; keep logistics drones attached to the mining route",
            "construction" => "prioritize builders, repair drones, and electronics/hull part stock before expansion",
            "medical-support" => "prioritize medic drones, medicine stock, and evacuation route markers",
            "crew-support" => "prioritize service drones, food stock, and low-risk crew support routes",
            _ => $"balance hauler/trader cycles toward {focusResource}",
        };

        return new AiBaseBehaviorDecision(
            mode,
            focusResource,
            focusRole,
            directive,
            $"topNeed={focusResource} {current}/{target}; supplyScore={state.SupplyScore}/100");
    }

    private static IReadOnlyList<LuaMAiBaseCompensationEntry> BuildAiBaseCompensationPlanInternal(LuaMAiBaseState state)
    {
        if (!state.Created)
        {
            return
            [
                new LuaMAiBaseCompensationEntry
                {
                    WeaknessId = "base-not-deployed",
                    Title = "AI base not deployed",
                    Severity = 5,
                    Evidence = "aiBaseCreated=false",
                    Compensation = "deploy base anchor and create visible work zones",
                    SuggestedRole = "builder",
                    SuggestedZone = "dock",
                },
            ];
        }

        var entries = new List<LuaMAiBaseCompensationEntry>();
        foreach (var need in state.Needs)
        {
            var current = GetAiBaseInventoryAmount(state, need.Resource);
            if (current >= need.Target)
                continue;

            var missing = Math.Max(1, need.Target - current);
            var resource = NormalizeAiBaseResource(need.Resource);
            var role = PickAiBaseCompensationRole(resource);
            var zone = PickAiBaseCompensationZone(resource, role);
            entries.Add(new LuaMAiBaseCompensationEntry
            {
                WeaknessId = $"resource-deficit:{resource}",
                Title = $"{resource} deficit",
                Severity = CalculateAiBaseDeficitSeverity(current, need.Target, need.Priority),
                Resource = resource,
                Current = current,
                Target = need.Target,
                Evidence = $"{resource} {current}/{need.Target}, priority {need.Priority}, missing {missing}",
                Compensation = $"dispatch {role} to {zone}; cover {missing} {resource} before surplus tasks",
                SuggestedRole = role,
                SuggestedZone = zone,
            });
        }

        if (state.TradeCycles <= 0)
        {
            entries.Add(new LuaMAiBaseCompensationEntry
            {
                WeaknessId = "logistics-not-started",
                Title = "logistics not started",
                Severity = 3,
                Evidence = "tradeCycles=0",
                Compensation = "run first confirmed supply ship or autonomous logistics pulse",
                SuggestedRole = "hauler",
                SuggestedZone = "storage",
            });
        }

        if (state.SupplyScore < 45)
        {
            entries.Add(new LuaMAiBaseCompensationEntry
            {
                WeaknessId = "low-supply-score",
                Title = "critical supply score",
                Severity = 5,
                Evidence = $"supplyScore={state.SupplyScore}/100",
                Compensation = "prioritize deficit resources and keep extra logistics ships active",
                SuggestedRole = "hauler",
                SuggestedZone = "storage",
            });
        }
        else if (state.SupplyScore < 70)
        {
            entries.Add(new LuaMAiBaseCompensationEntry
            {
                WeaknessId = "medium-supply-score",
                Title = "unstable supply score",
                Severity = 3,
                Evidence = $"supplyScore={state.SupplyScore}/100",
                Compensation = "continue supply cycles until top deficits are above target",
                SuggestedRole = "trader",
                SuggestedZone = "storage",
            });
        }

        if (entries.Count == 0)
        {
            entries.Add(new LuaMAiBaseCompensationEntry
            {
                WeaknessId = "stable",
                Title = "base stable",
                Severity = 1,
                Evidence = $"supplyScore={state.SupplyScore}/100; tradeCycles={state.TradeCycles}",
                Compensation = "shift drones to scouting, patrol, and surplus trade",
                SuggestedRole = "scout",
                SuggestedZone = "contact",
            });
        }

        return entries
            .OrderByDescending(entry => entry.Severity)
            .ThenByDescending(entry => GetAiBaseCompensationWeight(entry))
            .ThenBy(entry => entry.WeaknessId, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static int CalculateAiBaseDeficitSeverity(int current, int target, int priority)
    {
        if (target <= 0)
            return 1;

        var missingRatio = Math.Clamp((target - current) / (float) target, 0f, 1f);
        var severity = (int) Math.Ceiling(missingRatio * 3f) + Math.Clamp(priority, 1, 5) / 2;
        return Math.Clamp(severity, 1, 5);
    }

    private static int GetAiBaseCompensationWeight(LuaMAiBaseCompensationEntry entry)
    {
        if (entry.Target <= 0)
            return entry.Severity * 100;

        return Math.Max(0, entry.Target - entry.Current) * Math.Max(1, entry.Severity);
    }

    private static string PickAiBaseCompensationRole(string resource)
    {
        return resource switch
        {
            "ore" => "miner",
            "hull-parts" => "builder",
            "electronics" => "builder",
            "medicine" => "medic",
            "food" => "service",
            "security" => "guard",
            "route-data" => "scout",
            "credits" => "trader",
            _ => "hauler",
        };
    }

    private static string PickAiBaseCompensationZone(string resource, string role)
    {
        if (resource == "ore" || role == "miner")
            return "mining";

        if (role == "scout")
            return "contact";

        return "storage";
    }

    private static void AddAiBaseInventory(LuaMAiBaseState state, string resource, int amount)
    {
        resource = NormalizeAiBaseResource(resource);
        if (string.IsNullOrWhiteSpace(resource) || amount == 0)
            return;

        var entry = state.Inventory.FirstOrDefault(item => ResourceEquals(item.Resource, resource));
        if (entry == null)
        {
            state.Inventory.Add(new LuaMAiBaseInventoryEntry
            {
                Resource = resource,
                Amount = Math.Max(0, amount),
            });
            return;
        }

        entry.Amount = Math.Max(0, entry.Amount + amount);
    }

    private static int GetAiBaseInventoryAmount(LuaMAiBaseState state, string resource)
    {
        return state.Inventory
            .Where(entry => ResourceEquals(entry.Resource, resource))
            .Sum(entry => entry.Amount);
    }

    private static int GetAiBaseNeedTarget(LuaMAiBaseState state, string resource)
    {
        return state.Needs
            .Where(entry => ResourceEquals(entry.Resource, resource))
            .Select(entry => entry.Target)
            .FirstOrDefault();
    }

    private static int CalculateAiBaseSupplyScore(LuaMAiBaseState state)
    {
        var totalWeight = 0;
        var satisfiedWeight = 0f;
        foreach (var need in state.Needs)
        {
            if (need.Target <= 0)
                continue;

            var priority = Math.Max(1, need.Priority);
            totalWeight += priority;
            var amount = GetAiBaseInventoryAmount(state, need.Resource);
            satisfiedWeight += Math.Clamp(amount / (float) need.Target, 0f, 1f) * priority;
        }

        if (totalWeight <= 0)
            return 100;

        return Math.Clamp((int) MathF.Round(satisfiedWeight / totalWeight * 100), 0, 100);
    }

    private static string NormalizeAiBaseShipRole(string role)
    {
        var normalized = role.Trim().ToLowerInvariant();
        if (normalized.Contains("trader", StringComparison.Ordinal) ||
            normalized.Contains("trade", StringComparison.Ordinal) ||
            normalized.Contains("торг", StringComparison.Ordinal))
        {
            return "trader";
        }

        if (normalized.Contains("scout", StringComparison.Ordinal) ||
            normalized.Contains("развед", StringComparison.Ordinal))
        {
            return "scout";
        }

        if (normalized.Contains("build", StringComparison.Ordinal) ||
            normalized.Contains("стро", StringComparison.Ordinal) ||
            normalized.Contains("ремонт", StringComparison.Ordinal))
        {
            return "builder";
        }

        if (normalized.Contains("miner", StringComparison.Ordinal) ||
            normalized.Contains("mining", StringComparison.Ordinal) ||
            normalized.Contains("ore", StringComparison.Ordinal) ||
            normalized.Contains("РґРѕР±С‹", StringComparison.Ordinal) ||
            normalized.Contains("СЂСѓРґ", StringComparison.Ordinal))
        {
            return "miner";
        }

        return "hauler";
    }

    private static string NormalizeAiBaseDroneRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "builder" or "engineer" or "repairer" or "maintenance" or "repair" => "repair",
            "hauler" or "trader" or "cargo" or "logistics" => "logistics",
            "security" or "guard" => "guard",
            "scout" => "scout",
            "medical" or "doctor" or "medic" => "medic",
            "janitor" or "cleaner" or "service" => "service",
            "prospector" or "miner" => "miner",
            _ => "logistics",
        };
    }

    private static string GetAiBaseRoleLabel(string role)
    {
        return role switch
        {
            "trader" => "trader ship",
            "scout" => "scout ship",
            "builder" => "builder ship",
            "miner" => "mining ship",
            _ => "supply ship",
        };
    }

    private static string NormalizeAiBaseResource(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static bool ResourceEquals(string left, string right)
    {
        return NormalizeAiBaseResource(left).Equals(NormalizeAiBaseResource(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value, int maxLength)
    {
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private readonly record struct AiBaseDelivery(string Resource, int Amount, int CreditDelta, string Summary);

    private readonly record struct AiBaseBehaviorDecision(
        string Mode,
        string FocusResource,
        string FocusRole,
        string Directive,
        string Reason);

    private readonly record struct AiBaseImprovementDecision(
        string State,
        string Focus,
        string Finding);

    private static LuaMSectorStoryRecord CloneRecord(LuaMSectorStoryRecord record)
    {
        return new LuaMSectorStoryRecord
        {
            Story = record.Story,
            Title = record.Title,
            Author = record.Author,
            News = record.News,
            ContractCollection = record.ContractCollection?.ToString(),
            ContractCategory = record.ContractCategory,
            ContractName = record.ContractName,
            ContractVessel = record.ContractVessel,
            ContractDescription = record.ContractDescription,
            Hazard = record.Hazard,
            HazardSeverity = record.HazardSeverity,
            HazardRewardBonus = record.HazardRewardBonus,
            ReputationTarget = record.ReputationTarget,
            ReputationDelta = record.ReputationDelta,
            RequiredReputationTarget = record.RequiredReputationTarget,
            RequiredReputation = record.RequiredReputation,
            ContractReward = record.ContractReward,
            Insurance = record.Insurance,
            BlackBox = record.BlackBox,
            CompanyRecord = record.CompanyRecord,
            ShipRecord = record.ShipRecord,
            Resolved = record.Resolved,
            ResolvedBy = record.ResolvedBy,
            ResolutionNote = record.ResolutionNote,
            NewsSeeded = record.NewsSeeded,
            ContractSeeded = record.ContractSeeded,
        };
    }

    private static LuaMSectorReputationLedgerEntry CloneReputationEntry(LuaMSectorReputationLedgerEntry entry)
    {
        return new LuaMSectorReputationLedgerEntry
        {
            Story = entry.Story,
            Target = entry.Target,
            Delta = entry.Delta,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorHazardReportEntry CloneHazardReport(LuaMSectorHazardReportEntry entry)
    {
        return new LuaMSectorHazardReportEntry
        {
            Story = entry.Story,
            Hazard = entry.Hazard,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorInsurancePayoutEntry CloneInsurancePayout(LuaMSectorInsurancePayoutEntry entry)
    {
        return new LuaMSectorInsurancePayoutEntry
        {
            Story = entry.Story,
            Amount = entry.Amount,
            Paid = entry.Paid,
            Policy = entry.Policy,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorBlackBoxRecoveryEntry CloneBlackBoxRecovery(LuaMSectorBlackBoxRecoveryEntry entry)
    {
        return new LuaMSectorBlackBoxRecoveryEntry
        {
            Story = entry.Story,
            Recovery = entry.Recovery,
            Actor = entry.Actor,
            Note = entry.Note,
            SourceSnapshot = entry.SourceSnapshot,
        };
    }

    private static LuaMSectorRegistryEntry CloneRegistryEntry(LuaMSectorRegistryEntry entry)
    {
        return new LuaMSectorRegistryEntry
        {
            Story = entry.Story,
            Record = entry.Record,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorConditionEntry CloneConditionEntry(LuaMSectorConditionEntry entry)
    {
        return new LuaMSectorConditionEntry
        {
            ConditionId = entry.ConditionId,
            Title = entry.Title,
            Severity = entry.Severity,
            Summary = entry.Summary,
            Actor = entry.Actor,
            Active = entry.Active,
        };
    }

    private static bool HasOpenRescueBlockers(LuaMSectorRescueAfterActionEntry entry)
    {
        if (entry.BlockersCleared)
            return false;

        return !string.IsNullOrWhiteSpace(entry.Blockers) &&
               !entry.Blockers.Equals("none", StringComparison.OrdinalIgnoreCase) &&
               !entry.Blockers.Equals("team scene memory unavailable", StringComparison.OrdinalIgnoreCase) &&
               HasActionableRescueBlockerSummary(entry.Blockers);
    }

    private static bool HasActionableRescueBlockerSummary(string blockers)
    {
        if (HasRescueRouteClearBlockerEvidence(blockers))
            return false;

        var normalized = blockers.Replace(" ", string.Empty);
        if (!normalized.StartsWith("threat/crowd/route=0/0/0", StringComparison.OrdinalIgnoreCase))
            return true;

        var blockerCount = ExtractRescueBlockerField(blockers, "blockers");
        return !string.IsNullOrWhiteSpace(blockerCount) &&
               !blockerCount.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateAiBaseMedicalStatus(
        LuaMAiBaseState state,
        LuaMSectorRescueAfterActionEntry entry,
        int retainedAfterActionCount,
        TimeSpan now)
    {
        EnsureAiBaseDefaults(state);

        state.RescueMedicalOperations = Math.Max(
            Math.Max(0, state.RescueMedicalOperations) + 1,
            retainedAfterActionCount);
        state.LastRescueAfterActionSequence = entry.Sequence;
        state.LastRescueMedicalLocation = string.IsNullOrWhiteSpace(entry.Location)
            ? "unknown"
            : Trim(entry.Location, 96);
        state.LastRescueMedicalFollowUpPending = HasOpenRescueBlockers(entry);

        var blockerStatus = state.LastRescueMedicalFollowUpPending
            ? "follow-up pending"
            : entry.BlockersCleared
                ? "blockers cleared"
                : "corridor clear";

        state.LastRescueMedicalStatus = Trim(
            $"rescue #{entry.Sequence}: treatment={entry.TreatmentResult}; " +
            $"evacuation={entry.EvacuationResult}; blockers={blockerStatus}; " +
            $"location={state.LastRescueMedicalLocation}; team={entry.TeamStatus}",
            256);
        UpdateAiBaseRescueCooldown(state, entry, now);
        RefreshAiBaseBehavior(state, now, "rescue-after-action");
    }

    private static string BuildAiBaseMedicalStatusText(LuaMAiBaseState state)
    {
        if (state.RescueMedicalOperations <= 0 ||
            string.IsNullOrWhiteSpace(state.LastRescueMedicalStatus) ||
            state.LastRescueMedicalStatus.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return "no rescue after-action yet.";
        }

        var followUp = state.LastRescueMedicalFollowUpPending
            ? "follow-up pending"
            : "no open rescue follow-up";
        var cooldown = string.IsNullOrWhiteSpace(state.LastRescueCooldownStatus) ||
                       state.LastRescueCooldownStatus.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? "rescue cooldown=none"
            : state.LastRescueCooldownStatus;
        return $"ops={state.RescueMedicalOperations}; last={state.LastRescueMedicalStatus}; {followUp}; {cooldown}.";
    }

    private static void UpdateAiBaseRescueCooldown(
        LuaMAiBaseState state,
        LuaMSectorRescueAfterActionEntry entry,
        TimeSpan now)
    {
        var hasOpenFollowUp = state.LastRescueMedicalFollowUpPending;
        var cooldownSeconds = hasOpenFollowUp
            ? RescueAfterActionBlockedCooldownSeconds
            : RescueAfterActionCooldownSeconds;
        var reason = hasOpenFollowUp
            ? "follow-up pending"
            : "after-action complete";

        state.LastRescueCooldownSequence = entry.Sequence;
        state.LastRescueCooldownSeconds = cooldownSeconds;
        state.LastRescueCooldownStartedAt = now;
        state.NextRescueDispatchAllowedAt = now + TimeSpan.FromSeconds(cooldownSeconds);
        state.LastRescueCooldownStatus =
            $"rescue cooldown: sequence={entry.Sequence}; hold={cooldownSeconds}s; reason={reason}";
    }

    private static bool IsRescueCooldownActive(LuaMAiBaseState state, TimeSpan now)
    {
        if (state.LastRescueCooldownSeconds <= 0 ||
            state.LastRescueCooldownStartedAt > now)
        {
            return false;
        }

        return state.NextRescueDispatchAllowedAt > now;
    }

    private static bool TryAutoClearLatestRescueFollowUpFromAfterAction(
        LuaMSectorMemoryComponent memory,
        LuaMSectorRescueAfterActionEntry evidence,
        out LuaMSectorRescueAfterActionEntry? clearedEntry)
    {
        clearedEntry = null;

        if (!HasCrewHelpRouteClearEvidence(evidence))
            return false;

        var match = memory.RescueAfterActions
            .Where(action => action.Sequence < evidence.Sequence)
            .OrderByDescending(action => action.Sequence)
            .FirstOrDefault(HasOpenRescueBlockers);
        if (match == null)
            return false;

        match.BlockersCleared = true;
        match.BlockersClearedBy = string.IsNullOrWhiteSpace(evidence.Actor)
            ? "LuaM Rescue"
            : evidence.Actor;
        match.BlockersClearedNote = BuildCrewHelpRouteClearNote(evidence);
        match.Summary = BuildRescueAfterActionSummary(match);
        clearedEntry = CloneRescueAfterActionEntry(match);
        return true;
    }

    private static bool HasCrewHelpRouteClearEvidence(LuaMSectorRescueAfterActionEntry entry)
    {
        return HasRescueRouteClearBlockerEvidence(entry.Blockers) &&
               HasCrewHelpRequestEvidence(entry);
    }

    private static bool HasCrewHelpRequestEvidence(LuaMSectorRescueAfterActionEntry entry)
    {
        return ContainsCrewHelpRequest(entry.PlayerContribution) ||
               ContainsCrewHelpRequest(entry.Blockers);
    }

    private static bool ContainsCrewHelpRequest(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains("crew-help requested:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRescueRouteClearBlockerEvidence(string blockers)
    {
        if (string.IsNullOrWhiteSpace(blockers))
            return false;

        var normalized = blockers.Replace(" ", string.Empty);
        if (normalized.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("none;", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("none,", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalized.StartsWith("threat/crowd/route=0/0/0", StringComparison.OrdinalIgnoreCase))
            return false;

        var blockerCount = ExtractRescueBlockerField(blockers, "blockers");
        return string.IsNullOrWhiteSpace(blockerCount) ||
               blockerCount.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCrewHelpRouteClearNote(LuaMSectorRescueAfterActionEntry evidence)
    {
        return Trim(
            $"auto-cleared by rescue handoff: sequence={evidence.Sequence}; " +
            $"blockers={evidence.Blockers}; playerContribution={evidence.PlayerContribution}",
            256);
    }

    private static string ExtractRescueBlockerField(string summary, string field)
    {
        var marker = $"{field}=";
        var start = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += marker.Length;
        var semicolonEnd = summary.IndexOf(';', start);
        var commaEnd = summary.IndexOf(',', start);
        var end = semicolonEnd switch
        {
            < 0 when commaEnd < 0 => summary.Length,
            < 0 => commaEnd,
            _ when commaEnd >= 0 => Math.Min(semicolonEnd, commaEnd),
            _ => semicolonEnd,
        };

        return summary[start..end].Trim();
    }

    private static string BuildRescueAfterActionSummary(LuaMSectorRescueAfterActionEntry entry)
    {
        var output = new StringBuilder();
        output.Append($"treatment={entry.TreatmentResult}; evacuation={entry.EvacuationResult}; blockers={entry.Blockers}; ");

        if (entry.BlockersCleared)
        {
            output.Append("blockersCleared=true; ");
            output.Append($"clearedBy={entry.BlockersClearedBy}; clearedNote={entry.BlockersClearedNote}; ");
        }

        output.Append($"playerContribution={entry.PlayerContribution}; teamStatus={entry.TeamStatus}");
        return Trim(output.ToString(), 512);
    }

    private static LuaMSectorRescueAfterActionEntry CloneRescueAfterActionEntry(LuaMSectorRescueAfterActionEntry entry)
    {
        return new LuaMSectorRescueAfterActionEntry
        {
            Sequence = entry.Sequence,
            Actor = entry.Actor,
            Patient = entry.Patient,
            Location = entry.Location,
            TreatmentResult = entry.TreatmentResult,
            EvacuationResult = entry.EvacuationResult,
            Blockers = entry.Blockers,
            BlockersCleared = entry.BlockersCleared,
            BlockersClearedBy = entry.BlockersClearedBy,
            BlockersClearedNote = entry.BlockersClearedNote,
            PlayerContribution = entry.PlayerContribution,
            TeamStatus = entry.TeamStatus,
            Summary = entry.Summary,
        };
    }

    private static LuaMAiBaseState CloneAiBaseState(LuaMAiBaseState state)
    {
        return new LuaMAiBaseState
        {
            Created = state.Created,
            BaseId = state.BaseId,
            Name = state.Name,
            Location = state.Location,
            SupplyScore = state.SupplyScore,
            TradeCycles = state.TradeCycles,
            FactionId = state.FactionId,
            FactionName = state.FactionName,
            FactionCharter = state.FactionCharter,
            AutonomyModel = state.AutonomyModel,
            BehaviorMode = state.BehaviorMode,
            BehaviorFocusResource = state.BehaviorFocusResource,
            BehaviorFocusRole = state.BehaviorFocusRole,
            BehaviorDirective = state.BehaviorDirective,
            BehaviorReason = state.BehaviorReason,
            LastBehaviorTrigger = state.LastBehaviorTrigger,
            BehaviorRevision = state.BehaviorRevision,
            BehaviorUpdatedAt = state.BehaviorUpdatedAt,
            ImprovementLoopState = state.ImprovementLoopState,
            LastImprovementFocus = state.LastImprovementFocus,
            LastImprovementFinding = state.LastImprovementFinding,
            ImprovementRevision = state.ImprovementRevision,
            RoleDoctrine = state.RoleDoctrine == null
                ? new List<LuaMAiBaseRoleEntry>()
                : state.RoleDoctrine.Select(CloneAiBaseRoleEntry).ToList(),
            RescueMedicalOperations = state.RescueMedicalOperations,
            LastRescueAfterActionSequence = state.LastRescueAfterActionSequence,
            LastRescueMedicalStatus = state.LastRescueMedicalStatus,
            LastRescueMedicalLocation = state.LastRescueMedicalLocation,
            LastRescueMedicalFollowUpPending = state.LastRescueMedicalFollowUpPending,
            LastRescueCooldownSequence = state.LastRescueCooldownSequence,
            LastRescueCooldownSeconds = state.LastRescueCooldownSeconds,
            LastRescueCooldownStartedAt = state.LastRescueCooldownStartedAt,
            NextRescueDispatchAllowedAt = state.NextRescueDispatchAllowedAt,
            LastRescueCooldownStatus = state.LastRescueCooldownStatus,
            Inventory = state.Inventory.Select(CloneAiBaseInventoryEntry).ToList(),
            Needs = state.Needs.Select(CloneAiBaseNeedEntry).ToList(),
            TradeLog = state.TradeLog.Select(CloneAiBaseTradeEntry).ToList(),
            AutofixLog = state.AutofixLog.Select(CloneAiBaseAutofixEntry).ToList(),
        };
    }

    private static LuaMAiBaseRoleEntry CloneAiBaseRoleEntry(LuaMAiBaseRoleEntry entry)
    {
        return new LuaMAiBaseRoleEntry
        {
            Role = entry.Role,
            Service = entry.Service,
            Priority = entry.Priority,
            Directive = entry.Directive,
            Active = entry.Active,
        };
    }

    private static LuaMAiBaseInventoryEntry CloneAiBaseInventoryEntry(LuaMAiBaseInventoryEntry entry)
    {
        return new LuaMAiBaseInventoryEntry
        {
            Resource = entry.Resource,
            Amount = entry.Amount,
        };
    }

    private static LuaMAiBaseNeedEntry CloneAiBaseNeedEntry(LuaMAiBaseNeedEntry entry)
    {
        return new LuaMAiBaseNeedEntry
        {
            Resource = entry.Resource,
            Target = entry.Target,
            Priority = entry.Priority,
        };
    }

    private static LuaMAiBaseTradeEntry CloneAiBaseTradeEntry(LuaMAiBaseTradeEntry entry)
    {
        return new LuaMAiBaseTradeEntry
        {
            Cycle = entry.Cycle,
            Role = entry.Role,
            Vessel = entry.Vessel,
            Resource = entry.Resource,
            Amount = entry.Amount,
            Actor = entry.Actor,
            Summary = entry.Summary,
        };
    }

    private static LuaMAiBaseAutofixEntry CloneAiBaseAutofixEntry(LuaMAiBaseAutofixEntry entry)
    {
        return new LuaMAiBaseAutofixEntry
        {
            Attempt = entry.Attempt,
            Actor = entry.Actor,
            Issue = entry.Issue,
            CommandId = entry.CommandId,
            BeforeSummary = entry.BeforeSummary,
            ResultSummary = entry.ResultSummary,
            AfterSummary = entry.AfterSummary,
            Success = entry.Success,
        };
    }

    private static LuaMSectorPersistedStoryRecord ToPersistedStoryRecord(LuaMSectorStoryRecord record)
    {
        return new LuaMSectorPersistedStoryRecord
        {
            Story = record.Story,
            Title = record.Title,
            Author = record.Author,
            News = record.News,
            ContractCollection = string.IsNullOrWhiteSpace(record.ContractCollection) ? null : record.ContractCollection,
            ContractCategory = record.ContractCategory,
            ContractName = record.ContractName,
            ContractVessel = record.ContractVessel,
            ContractDescription = record.ContractDescription,
            Hazard = record.Hazard,
            HazardSeverity = record.HazardSeverity,
            HazardRewardBonus = record.HazardRewardBonus,
            ReputationTarget = record.ReputationTarget,
            ReputationDelta = record.ReputationDelta,
            RequiredReputationTarget = record.RequiredReputationTarget,
            RequiredReputation = record.RequiredReputation,
            ContractReward = record.ContractReward,
            Insurance = record.Insurance,
            BlackBox = record.BlackBox,
            CompanyRecord = record.CompanyRecord,
            ShipRecord = record.ShipRecord,
            Resolved = record.Resolved,
            ResolvedBy = record.ResolvedBy,
            ResolutionNote = record.ResolutionNote,
            NewsSeeded = record.NewsSeeded,
            ContractSeeded = record.ContractSeeded,
        };
    }

    private static LuaMSectorStoryRecord FromPersistedStoryRecord(LuaMSectorPersistedStoryRecord record)
    {
        return new LuaMSectorStoryRecord
        {
            Story = record.Story,
            Title = record.Title,
            Author = string.IsNullOrWhiteSpace(record.Author) ? "LuaM sector board" : record.Author,
            News = record.News,
            ContractCollection = record.ContractCollection,
            ContractCategory = record.ContractCategory,
            ContractName = record.ContractName,
            ContractVessel = record.ContractVessel,
            ContractDescription = record.ContractDescription,
            Hazard = record.Hazard,
            HazardSeverity = record.HazardSeverity,
            HazardRewardBonus = record.HazardRewardBonus,
            ReputationTarget = record.ReputationTarget,
            ReputationDelta = record.ReputationDelta,
            RequiredReputationTarget = record.RequiredReputationTarget,
            RequiredReputation = record.RequiredReputation,
            ContractReward = record.ContractReward,
            Insurance = record.Insurance,
            BlackBox = record.BlackBox,
            CompanyRecord = record.CompanyRecord,
            ShipRecord = record.ShipRecord,
            Resolved = record.Resolved,
            ResolvedBy = record.ResolvedBy,
            ResolutionNote = record.ResolutionNote,
            NewsSeeded = record.NewsSeeded,
            ContractSeeded = record.ContractSeeded,
        };
    }

    private static LuaMSectorPersistedReputationEntry ToPersistedReputationEntry(LuaMSectorReputationLedgerEntry entry)
    {
        return new LuaMSectorPersistedReputationEntry
        {
            Story = entry.Story,
            Target = entry.Target,
            Delta = entry.Delta,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorReputationLedgerEntry FromPersistedReputationEntry(LuaMSectorPersistedReputationEntry entry)
    {
        return new LuaMSectorReputationLedgerEntry
        {
            Story = entry.Story,
            Target = entry.Target,
            Delta = entry.Delta,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorPersistedHazardReport ToPersistedHazardReport(LuaMSectorHazardReportEntry entry)
    {
        return new LuaMSectorPersistedHazardReport
        {
            Story = entry.Story,
            Hazard = entry.Hazard,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorHazardReportEntry FromPersistedHazardReport(LuaMSectorPersistedHazardReport entry)
    {
        return new LuaMSectorHazardReportEntry
        {
            Story = entry.Story,
            Hazard = entry.Hazard,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorPersistedInsurancePayout ToPersistedInsurancePayout(LuaMSectorInsurancePayoutEntry entry)
    {
        return new LuaMSectorPersistedInsurancePayout
        {
            Story = entry.Story,
            Amount = entry.Amount,
            Paid = entry.Paid,
            Policy = entry.Policy,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorInsurancePayoutEntry FromPersistedInsurancePayout(LuaMSectorPersistedInsurancePayout entry)
    {
        return new LuaMSectorInsurancePayoutEntry
        {
            Story = entry.Story,
            Amount = entry.Amount,
            Paid = entry.Paid,
            Policy = entry.Policy,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorPersistedBlackBoxRecovery ToPersistedBlackBoxRecovery(LuaMSectorBlackBoxRecoveryEntry entry)
    {
        return new LuaMSectorPersistedBlackBoxRecovery
        {
            Story = entry.Story,
            Recovery = entry.Recovery,
            Actor = entry.Actor,
            Note = entry.Note,
            SourceSnapshot = entry.SourceSnapshot,
        };
    }

    private static LuaMSectorBlackBoxRecoveryEntry FromPersistedBlackBoxRecovery(LuaMSectorPersistedBlackBoxRecovery entry)
    {
        return new LuaMSectorBlackBoxRecoveryEntry
        {
            Story = entry.Story,
            Recovery = entry.Recovery,
            Actor = entry.Actor,
            Note = entry.Note,
            SourceSnapshot = entry.SourceSnapshot,
        };
    }

    private static LuaMSectorPersistedRegistryEntry ToPersistedRegistryEntry(LuaMSectorRegistryEntry entry)
    {
        return new LuaMSectorPersistedRegistryEntry
        {
            Story = entry.Story,
            Record = entry.Record,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorRegistryEntry FromPersistedRegistryEntry(LuaMSectorPersistedRegistryEntry entry)
    {
        return new LuaMSectorRegistryEntry
        {
            Story = entry.Story,
            Record = entry.Record,
            Actor = entry.Actor,
            Note = entry.Note,
        };
    }

    private static LuaMSectorPersistedConditionEntry ToPersistedConditionEntry(LuaMSectorConditionEntry entry)
    {
        return new LuaMSectorPersistedConditionEntry
        {
            ConditionId = entry.ConditionId,
            Title = entry.Title,
            Severity = entry.Severity,
            Summary = entry.Summary,
            Actor = entry.Actor,
            Active = entry.Active,
        };
    }

    private static LuaMSectorConditionEntry FromPersistedConditionEntry(LuaMSectorPersistedConditionEntry entry)
    {
        return new LuaMSectorConditionEntry
        {
            ConditionId = entry.ConditionId,
            Title = entry.Title,
            Severity = entry.Severity,
            Summary = entry.Summary,
            Actor = entry.Actor,
            Active = entry.Active,
        };
    }

    private static LuaMSectorPersistedRescueAfterAction ToPersistedRescueAfterAction(LuaMSectorRescueAfterActionEntry entry)
    {
        return new LuaMSectorPersistedRescueAfterAction
        {
            Sequence = entry.Sequence,
            Actor = entry.Actor,
            Patient = entry.Patient,
            Location = entry.Location,
            TreatmentResult = entry.TreatmentResult,
            EvacuationResult = entry.EvacuationResult,
            Blockers = entry.Blockers,
            BlockersCleared = entry.BlockersCleared,
            BlockersClearedBy = entry.BlockersClearedBy,
            BlockersClearedNote = entry.BlockersClearedNote,
            PlayerContribution = entry.PlayerContribution,
            TeamStatus = entry.TeamStatus,
            Summary = entry.Summary,
        };
    }

    private static LuaMSectorRescueAfterActionEntry FromPersistedRescueAfterAction(LuaMSectorPersistedRescueAfterAction entry)
    {
        return new LuaMSectorRescueAfterActionEntry
        {
            Sequence = entry.Sequence,
            Actor = entry.Actor,
            Patient = string.IsNullOrWhiteSpace(entry.Patient) ? "withheld" : entry.Patient,
            Location = entry.Location,
            TreatmentResult = entry.TreatmentResult,
            EvacuationResult = entry.EvacuationResult,
            Blockers = entry.Blockers,
            BlockersCleared = entry.BlockersCleared,
            BlockersClearedBy = entry.BlockersClearedBy,
            BlockersClearedNote = entry.BlockersClearedNote,
            PlayerContribution = entry.PlayerContribution,
            TeamStatus = entry.TeamStatus,
            Summary = entry.Summary,
        };
    }

    private static LuaMAiBasePersistedState ToPersistedAiBase(LuaMAiBaseState state)
    {
        return new LuaMAiBasePersistedState
        {
            Created = state.Created,
            BaseId = state.BaseId,
            Name = state.Name,
            Location = state.Location,
            SupplyScore = state.SupplyScore,
            TradeCycles = state.TradeCycles,
            FactionId = state.FactionId,
            FactionName = state.FactionName,
            FactionCharter = state.FactionCharter,
            AutonomyModel = state.AutonomyModel,
            BehaviorMode = state.BehaviorMode,
            BehaviorFocusResource = state.BehaviorFocusResource,
            BehaviorFocusRole = state.BehaviorFocusRole,
            BehaviorDirective = state.BehaviorDirective,
            BehaviorReason = state.BehaviorReason,
            LastBehaviorTrigger = state.LastBehaviorTrigger,
            BehaviorRevision = state.BehaviorRevision,
            BehaviorUpdatedAt = state.BehaviorUpdatedAt,
            ImprovementLoopState = state.ImprovementLoopState,
            LastImprovementFocus = state.LastImprovementFocus,
            LastImprovementFinding = state.LastImprovementFinding,
            ImprovementRevision = state.ImprovementRevision,
            RoleDoctrine = state.RoleDoctrine == null
                ? new List<LuaMAiBasePersistedRoleEntry>()
                : state.RoleDoctrine
                .Select(entry => new LuaMAiBasePersistedRoleEntry
                {
                    Role = entry.Role,
                    Service = entry.Service,
                    Priority = entry.Priority,
                    Directive = entry.Directive,
                    Active = entry.Active,
                })
                .ToList(),
            RescueMedicalOperations = state.RescueMedicalOperations,
            LastRescueAfterActionSequence = state.LastRescueAfterActionSequence,
            LastRescueMedicalStatus = state.LastRescueMedicalStatus,
            LastRescueMedicalLocation = state.LastRescueMedicalLocation,
            LastRescueMedicalFollowUpPending = state.LastRescueMedicalFollowUpPending,
            LastRescueCooldownSequence = state.LastRescueCooldownSequence,
            LastRescueCooldownSeconds = state.LastRescueCooldownSeconds,
            LastRescueCooldownStartedAt = state.LastRescueCooldownStartedAt,
            NextRescueDispatchAllowedAt = state.NextRescueDispatchAllowedAt,
            LastRescueCooldownStatus = state.LastRescueCooldownStatus,
            Inventory = state.Inventory
                .Select(entry => new LuaMAiBasePersistedInventoryEntry
                {
                    Resource = entry.Resource,
                    Amount = entry.Amount,
                })
                .ToList(),
            Needs = state.Needs
                .Select(entry => new LuaMAiBasePersistedNeedEntry
                {
                    Resource = entry.Resource,
                    Target = entry.Target,
                    Priority = entry.Priority,
                })
                .ToList(),
            TradeLog = state.TradeLog
                .Select(entry => new LuaMAiBasePersistedTradeEntry
                {
                    Cycle = entry.Cycle,
                    Role = entry.Role,
                    Vessel = entry.Vessel,
                    Resource = entry.Resource,
                    Amount = entry.Amount,
                    Actor = entry.Actor,
                    Summary = entry.Summary,
                })
                .ToList(),
            AutofixLog = state.AutofixLog
                .Select(entry => new LuaMAiBasePersistedAutofixEntry
                {
                    Attempt = entry.Attempt,
                    Actor = entry.Actor,
                    Issue = entry.Issue,
                    CommandId = entry.CommandId,
                    BeforeSummary = entry.BeforeSummary,
                    ResultSummary = entry.ResultSummary,
                    AfterSummary = entry.AfterSummary,
                    Success = entry.Success,
                })
                .ToList(),
        };
    }

    private static LuaMAiBaseState FromPersistedAiBase(LuaMAiBasePersistedState? state)
    {
        if (state == null)
            return new LuaMAiBaseState();

        return new LuaMAiBaseState
        {
            Created = state.Created,
            BaseId = state.BaseId,
            Name = state.Name,
            Location = state.Location,
            SupplyScore = state.SupplyScore,
            TradeCycles = state.TradeCycles,
            FactionId = state.FactionId,
            FactionName = state.FactionName,
            FactionCharter = state.FactionCharter,
            AutonomyModel = state.AutonomyModel,
            BehaviorMode = state.BehaviorMode,
            BehaviorFocusResource = state.BehaviorFocusResource,
            BehaviorFocusRole = state.BehaviorFocusRole,
            BehaviorDirective = state.BehaviorDirective,
            BehaviorReason = state.BehaviorReason,
            LastBehaviorTrigger = state.LastBehaviorTrigger,
            BehaviorRevision = state.BehaviorRevision,
            BehaviorUpdatedAt = state.BehaviorUpdatedAt,
            ImprovementLoopState = state.ImprovementLoopState,
            LastImprovementFocus = state.LastImprovementFocus,
            LastImprovementFinding = state.LastImprovementFinding,
            ImprovementRevision = state.ImprovementRevision,
            RoleDoctrine = state.RoleDoctrine == null
                ? new List<LuaMAiBaseRoleEntry>()
                : state.RoleDoctrine
                .Select(entry => new LuaMAiBaseRoleEntry
                {
                    Role = entry.Role,
                    Service = entry.Service,
                    Priority = entry.Priority,
                    Directive = entry.Directive,
                    Active = entry.Active,
                })
                .ToList(),
            RescueMedicalOperations = state.RescueMedicalOperations,
            LastRescueAfterActionSequence = state.LastRescueAfterActionSequence,
            LastRescueMedicalStatus = state.LastRescueMedicalStatus,
            LastRescueMedicalLocation = state.LastRescueMedicalLocation,
            LastRescueMedicalFollowUpPending = state.LastRescueMedicalFollowUpPending,
            LastRescueCooldownSequence = state.LastRescueCooldownSequence,
            LastRescueCooldownSeconds = state.LastRescueCooldownSeconds,
            LastRescueCooldownStartedAt = state.LastRescueCooldownStartedAt,
            NextRescueDispatchAllowedAt = state.NextRescueDispatchAllowedAt,
            LastRescueCooldownStatus = state.LastRescueCooldownStatus,
            Inventory = state.Inventory
                .Select(entry => new LuaMAiBaseInventoryEntry
                {
                    Resource = entry.Resource,
                    Amount = entry.Amount,
                })
                .ToList(),
            Needs = state.Needs
                .Select(entry => new LuaMAiBaseNeedEntry
                {
                    Resource = entry.Resource,
                    Target = entry.Target,
                    Priority = entry.Priority,
                })
                .ToList(),
            TradeLog = state.TradeLog
                .Select(entry => new LuaMAiBaseTradeEntry
                {
                    Cycle = entry.Cycle,
                    Role = entry.Role,
                    Vessel = entry.Vessel,
                    Resource = entry.Resource,
                    Amount = entry.Amount,
                    Actor = entry.Actor,
                    Summary = entry.Summary,
                })
                .ToList(),
            AutofixLog = state.AutofixLog
                .Select(entry => new LuaMAiBaseAutofixEntry
                {
                    Attempt = entry.Attempt,
                    Actor = entry.Actor,
                    Issue = entry.Issue,
                    CommandId = entry.CommandId,
                    BeforeSummary = entry.BeforeSummary,
                    ResultSummary = entry.ResultSummary,
                    AfterSummary = entry.AfterSummary,
                    Success = entry.Success,
                })
                .ToList(),
        };
    }
}

public sealed class LuaMSectorPersistedMemory
{
    public bool StoriesLoaded { get; set; }
    public bool NewsSeeded { get; set; }
    public bool ContractsSeeded { get; set; }
    public List<LuaMSectorPersistedStoryRecord> Records { get; set; } = new();
    public Dictionary<string, int> ReputationLedger { get; set; } = new();
    public List<LuaMSectorPersistedReputationEntry> ReputationEntries { get; set; } = new();
    public List<LuaMSectorPersistedHazardReport> HazardReports { get; set; } = new();
    public List<LuaMSectorPersistedInsurancePayout> InsurancePayouts { get; set; } = new();
    public List<LuaMSectorPersistedBlackBoxRecovery> BlackBoxRecoveries { get; set; } = new();
    public List<LuaMSectorPersistedRegistryEntry> CompanyRegistry { get; set; } = new();
    public List<LuaMSectorPersistedRegistryEntry> ShipRegistry { get; set; } = new();
    public List<LuaMSectorPersistedConditionEntry> SectorConditions { get; set; } = new();
    public List<LuaMSectorPersistedRescueAfterAction> RescueAfterActions { get; set; } = new();
    public LuaMAiBasePersistedState AiBase { get; set; } = new();
}

public sealed class LuaMSectorPersistedStoryRecord
{
    public string Story { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = "LuaM sector board";
    public string News { get; set; } = string.Empty;
    public string? ContractCollection { get; set; }
    public BountyContractCategory? ContractCategory { get; set; }
    public string ContractName { get; set; } = string.Empty;
    public string ContractVessel { get; set; } = string.Empty;
    public string ContractDescription { get; set; } = string.Empty;
    public string Hazard { get; set; } = string.Empty;
    public int HazardSeverity { get; set; } = 1;
    public int HazardRewardBonus { get; set; }
    public string ReputationTarget { get; set; } = string.Empty;
    public int ReputationDelta { get; set; }
    public string RequiredReputationTarget { get; set; } = string.Empty;
    public int RequiredReputation { get; set; }
    public int ContractReward { get; set; }
    public string Insurance { get; set; } = string.Empty;
    public string BlackBox { get; set; } = string.Empty;
    public string CompanyRecord { get; set; } = string.Empty;
    public string ShipRecord { get; set; } = string.Empty;
    public bool Resolved { get; set; }
    public string ResolvedBy { get; set; } = string.Empty;
    public string ResolutionNote { get; set; } = string.Empty;
    public bool NewsSeeded { get; set; }
    public bool ContractSeeded { get; set; }
}

public sealed class LuaMSectorPersistedReputationEntry
{
    public string Story { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Delta { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class LuaMSectorPersistedHazardReport
{
    public string Story { get; set; } = string.Empty;
    public string Hazard { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class LuaMSectorPersistedInsurancePayout
{
    public string Story { get; set; } = string.Empty;
    public int Amount { get; set; }
    public bool Paid { get; set; }
    public string Policy { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class LuaMSectorPersistedBlackBoxRecovery
{
    public string Story { get; set; } = string.Empty;
    public string Recovery { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SourceSnapshot { get; set; } = string.Empty;
}

public sealed class LuaMSectorPersistedRegistryEntry
{
    public string Story { get; set; } = string.Empty;
    public string Record { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class LuaMSectorPersistedConditionEntry
{
    public string ConditionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Severity { get; set; } = 1;
    public string Summary { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
}

public sealed class LuaMSectorPersistedRescueAfterAction
{
    public int Sequence { get; set; }
    public string Actor { get; set; } = "LuaM Rescue";
    public string Patient { get; set; } = "withheld";
    public string Location { get; set; } = string.Empty;
    public string TreatmentResult { get; set; } = string.Empty;
    public string EvacuationResult { get; set; } = string.Empty;
    public string Blockers { get; set; } = string.Empty;
    public bool BlockersCleared { get; set; }
    public string BlockersClearedBy { get; set; } = string.Empty;
    public string BlockersClearedNote { get; set; } = string.Empty;
    public string PlayerContribution { get; set; } = string.Empty;
    public string TeamStatus { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class LuaMAiBasePersistedState
{
    public bool Created { get; set; }
    public string BaseId { get; set; } = "LuaM-AI-Base";
    public string Name { get; set; } = "LuaM autonomous supply base";
    public string Location { get; set; } = "hidden sector anchorage";
    public int SupplyScore { get; set; }
    public int TradeCycles { get; set; }
    public string FactionId { get; set; } = "luam-ai-contour";
    public string FactionName { get; set; } = "LuaM AI Contour";
    public string FactionCharter { get; set; } = "autonomous in-game faction for resource extraction, base construction, logistics, medical support, and improvement audits";
    public string AutonomyModel { get; set; } = "mixed-initiative shared autonomy";
    public string BehaviorMode { get; set; } = "bootstrap";
    public string BehaviorFocusResource { get; set; } = "base";
    public string BehaviorFocusRole { get; set; } = "builder";
    public string BehaviorDirective { get; set; } = "deploy base anchor and start the first logistics cycle";
    public string BehaviorReason { get; set; } = "ai base not deployed";
    public string LastBehaviorTrigger { get; set; } = "initial";
    public int BehaviorRevision { get; set; }
    public TimeSpan BehaviorUpdatedAt { get; set; }
    public string ImprovementLoopState { get; set; } = "observe-plan-act-verify";
    public string LastImprovementFocus { get; set; } = "deploy base anchor";
    public string LastImprovementFinding { get; set; } = "ai base not deployed";
    public int ImprovementRevision { get; set; }
    public int RescueMedicalOperations { get; set; }
    public int LastRescueAfterActionSequence { get; set; }
    public string LastRescueMedicalStatus { get; set; } = "none";
    public string LastRescueMedicalLocation { get; set; } = "none";
    public bool LastRescueMedicalFollowUpPending { get; set; }
    public int LastRescueCooldownSequence { get; set; }
    public int LastRescueCooldownSeconds { get; set; }
    public TimeSpan LastRescueCooldownStartedAt { get; set; }
    public TimeSpan NextRescueDispatchAllowedAt { get; set; }
    public string LastRescueCooldownStatus { get; set; } = "none";
    public List<LuaMAiBasePersistedInventoryEntry> Inventory { get; set; } = new();
    public List<LuaMAiBasePersistedNeedEntry> Needs { get; set; } = new();
    public List<LuaMAiBasePersistedTradeEntry> TradeLog { get; set; } = new();
    public List<LuaMAiBasePersistedAutofixEntry> AutofixLog { get; set; } = new();
    public List<LuaMAiBasePersistedRoleEntry> RoleDoctrine { get; set; } = new();
}

public sealed class LuaMAiBasePersistedRoleEntry
{
    public string Role { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Priority { get; set; } = "support";
    public string Directive { get; set; } = string.Empty;
    public bool Active { get; set; }
}

public sealed class LuaMAiBasePersistedInventoryEntry
{
    public string Resource { get; set; } = string.Empty;
    public int Amount { get; set; }
}

public sealed class LuaMAiBasePersistedNeedEntry
{
    public string Resource { get; set; } = string.Empty;
    public int Target { get; set; }
    public int Priority { get; set; } = 1;
}

public sealed class LuaMAiBasePersistedTradeEntry
{
    public int Cycle { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Vessel { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public int Amount { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class LuaMAiBasePersistedAutofixEntry
{
    public int Attempt { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string BeforeSummary { get; set; } = string.Empty;
    public string ResultSummary { get; set; } = string.Empty;
    public string AfterSummary { get; set; } = string.Empty;
    public bool Success { get; set; }
}

public sealed class LuaMAiBaseCompensationEntry
{
    public string WeaknessId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Severity { get; init; }
    public string Resource { get; init; } = string.Empty;
    public int Current { get; init; }
    public int Target { get; init; }
    public string Evidence { get; init; } = string.Empty;
    public string Compensation { get; init; } = string.Empty;
    public string SuggestedRole { get; init; } = string.Empty;
    public string SuggestedZone { get; init; } = string.Empty;
}

public readonly record struct LuaMSectorStoryResolvedEvent(
    ProtoId<LuaMSectorStoryPrototype> Story,
    string Actor,
    string Note);

public readonly record struct LuaMSectorMemoryResetEvent(bool DeletePersisted);

public readonly record struct LuaMSectorStoryUnlockedEvent(
    ProtoId<LuaMSectorStoryPrototype> Story,
    string RequiredReputationTarget,
    int RequiredReputation);

public readonly record struct LuaMSectorReputationChangedEvent(LuaMSectorReputationLedgerEntry Entry);

public readonly record struct LuaMSectorHazardAcknowledgedEvent(LuaMSectorHazardReportEntry Entry);

public readonly record struct LuaMSectorInsuranceClaimedEvent(LuaMSectorInsurancePayoutEntry Entry);

public readonly record struct LuaMSectorBlackBoxRecoveredEvent(LuaMSectorBlackBoxRecoveryEntry Entry);

public readonly record struct LuaMSectorCompanyRegisteredEvent(LuaMSectorRegistryEntry Entry);

public readonly record struct LuaMSectorShipRegisteredEvent(LuaMSectorRegistryEntry Entry);

public readonly record struct LuaMSectorConditionChangedEvent(LuaMSectorConditionEntry Entry);

public readonly record struct LuaMSectorRescueAfterActionRecordedEvent(LuaMSectorRescueAfterActionEntry Entry);

public readonly record struct LuaMSectorRescueFollowUpClearedEvent(LuaMSectorRescueAfterActionEntry Entry);
