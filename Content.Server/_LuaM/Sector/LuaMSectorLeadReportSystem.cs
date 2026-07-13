using System.Linq;
using System.Text;
using Content.Server.Popups;
using Content.Shared._LuaM.Sector;
using Content.Shared.Paper;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorLeadReportSystem : EntitySystem
{
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private LuaMSectorInsuranceTerminalSystem _insurance = default!;
    [Dependency] private LuaMSectorRegistryTerminalSystem _registry = default!;
    [Dependency] private LuaMSectorDynamicEventSystem _dynamicEvents = default!;
    [Dependency] private LuaMSectorTrafficSystem _traffic = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorLeadReportComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
        SubscribeLocalEvent<LuaMSectorLeadReportComponent, BoundUIOpenedEvent>(OnUiOpened);
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
        SubscribeLocalEvent<LuaMSectorTrafficChangedEvent>(OnSectorStatusChanged);

        Subs.BuiEvents<LuaMSectorLeadReportComponent>(LuaMSectorTerminalUiKey.Key, subs =>
        {
            subs.Event<LuaMSectorTerminalActionMessage>(OnTerminalAction);
        });
    }

    private void OnUiOpened(EntityUid uid, LuaMSectorLeadReportComponent component, BoundUIOpenedEvent args)
    {
        UpdateTerminalUi(uid);
    }

    private void OnTerminalAction(EntityUid uid, LuaMSectorLeadReportComponent component, LuaMSectorTerminalActionMessage args)
    {
        var result = string.Empty;

        switch (args.Action)
        {
            case LuaMSectorTerminalAction.Refresh:
                result = Loc.GetString("luam-sector-terminal-result-refresh");
                break;
            case LuaMSectorTerminalAction.InterceptTrafficContact:
                if (args.Contact is not { } netContact ||
                    !TryGetEntity(netContact, out var contact) ||
                    contact is not { } contactUid)
                {
                    result = Loc.GetString("luam-sector-terminal-result-contact-lost");
                    break;
                }

                result = _traffic.TryInterceptContact(contactUid, args.Actor, out var intercepted, out var interceptError)
                    ? Loc.GetString(
                        "luam-sector-terminal-result-contact-intercepted",
                        ("title", intercepted?.Title ?? Loc.GetString("luam-sector-terminal-result-generated")))
                    : interceptError;
                break;
            case LuaMSectorTerminalAction.RequestDynamicEvent:
                LuaMSectorStoryRecord? record;
                string error;
                var generated = string.IsNullOrWhiteSpace(args.TemplateId)
                    ? _dynamicEvents.TryGenerateDynamicEvent(Name(args.Actor), out record, out error)
                    : _dynamicEvents.TryGeneratePreferredDynamicEvent(Name(args.Actor), args.TemplateId, out record, out error);

                if (generated)
                    result = record == null
                        ? Loc.GetString("luam-sector-terminal-result-generated")
                        : Loc.GetString("luam-sector-terminal-result-generated-named", ("title", record.Title));
                else
                    result = error;
                break;
            case LuaMSectorTerminalAction.PingActiveRouteMarker:
                _dynamicEvents.TryPingActiveRouteMarker(args.Actor, out result);
                break;
            case LuaMSectorTerminalAction.PrintLeadReport:
                result = TryPrintLeadReport(uid, args.Actor, out _, component)
                    ? Loc.GetString("luam-sector-terminal-result-report-printed")
                    : Loc.GetString("luam-sector-terminal-result-report-failed");
                break;
            case LuaMSectorTerminalAction.PrintRuntimeCoordinatePacket:
                result = TryPrintRuntimeCoordinatePacket(uid, args.Actor, out _, component)
                    ? Loc.GetString("luam-sector-terminal-result-route-printed")
                    : Loc.GetString("luam-sector-terminal-result-route-failed");
                break;
            case LuaMSectorTerminalAction.PrintRuntimeClosureReport:
                result = TryPrintRuntimeClosureReport(uid, args.Actor, out _, component)
                    ? Loc.GetString("luam-sector-terminal-result-closure-printed")
                    : Loc.GetString("luam-sector-terminal-result-closure-failed");
                break;
            case LuaMSectorTerminalAction.PrintRescueFollowUpReport:
                result = TryPrintRescueFollowUpReport(uid, args.Actor, out _, component)
                    ? Loc.GetString("luam-sector-terminal-result-rescue-followup-printed")
                    : Loc.GetString("luam-sector-terminal-result-rescue-followup-failed");
                break;
            case LuaMSectorTerminalAction.PrintInsuranceDocket:
                result = _insurance.TryPrintInsuranceDocket(uid, args.Actor, out _)
                    ? Loc.GetString("luam-sector-terminal-result-insurance-printed")
                    : Loc.GetString("luam-sector-terminal-result-insurance-failed");
                break;
            case LuaMSectorTerminalAction.PrintInsuranceClaimVoucher:
                result = (string.IsNullOrWhiteSpace(args.StoryId)
                        ? _insurance.TryPrintInsuranceClaimVoucher(uid, args.Actor, out _)
                        : _insurance.TryPrintInsuranceClaimVoucherForStory(uid, args.Actor, out _, args.StoryId))
                    ? Loc.GetString("luam-sector-terminal-result-claim-printed")
                    : Loc.GetString("luam-sector-terminal-result-claim-failed");
                break;
            case LuaMSectorTerminalAction.PrintRegistryDocket:
                result = _registry.TryPrintRegistryDocket(uid, args.Actor, out _)
                    ? Loc.GetString("luam-sector-terminal-result-registry-printed")
                    : Loc.GetString("luam-sector-terminal-result-registry-failed");
                break;
            case LuaMSectorTerminalAction.PrintCharterVoucher:
                result = (string.IsNullOrWhiteSpace(args.StoryId)
                        ? _registry.TryPrintCharterVoucher(uid, args.Actor, out _)
                        : _registry.TryPrintCharterVoucherForStory(uid, args.Actor, out _, args.StoryId))
                    ? Loc.GetString("luam-sector-terminal-result-charter-printed")
                    : Loc.GetString("luam-sector-terminal-result-charter-failed");
                break;
        }

        UpdateTerminalUi(uid, result);
    }

    private void OnSectorStatusChanged<T>(T ev)
    {
        UpdateAllTerminalUis();
    }

    private void UpdateAllTerminalUis()
    {
        var query = EntityQueryEnumerator<LuaMSectorLeadReportComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            UpdateTerminalUi(uid);
        }
    }

    private void UpdateTerminalUi(EntityUid uid, string lastActionResult = "")
    {
        if (!_ui.HasUi(uid, LuaMSectorTerminalUiKey.Key))
            return;

        _ui.SetUiState(uid, LuaMSectorTerminalUiKey.Key, BuildTerminalUiState(lastActionResult));
    }

    public LuaMSectorStatusUiState BuildTerminalUiState(string lastActionResult = "")
    {
        var snapshot = _stories.GetStatusSnapshot();
        var openStory = _stories.TryGetOpenRuntimeDistressStory(out var runtimeStory) && runtimeStory != null
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
            _traffic.BuildTrafficUiEntries(automation.CanRequestDynamicEvent, automation.RequestBlockReason),
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

    private void OnGetVerbs(EntityUid uid, LuaMSectorLeadReportComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print sector lead report",
            Priority = 1,
            Act = () => TryPrintLeadReport(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print runtime coordinate packet",
            Priority = 1,
            Act = () => TryPrintRuntimeCoordinatePacket(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print runtime closure report",
            Priority = 0,
            Act = () => TryPrintRuntimeClosureReport(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print rescue follow-up report",
            Priority = 0,
            Act = () => TryPrintRescueFollowUpReport(uid, args.User, out _, component),
        });
    }

    public bool TryPrintLeadReport(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMSectorLeadReportComponent? component = null)
    {
        report = default;

        if (!Resolve(uid, ref component, false))
            return false;

        var snapshot = _stories.GetStatusSnapshot();
        var automation = _dynamicEvents.BuildAutomationUiEntry();
        var sectorMapNodes = _dynamicEvents.BuildSectorMapUiEntries();
        var content = BuildReport(snapshot, automation, sectorMapNodes);
        report = Spawn(component.PaperPrototype, Transform(uid).Coordinates);

        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-report-printer-failed"), uid, user);
            report = default;
            return false;
        }

        _paper.SetContent((report, paper), content);
        _popup.PopupEntity(Loc.GetString("luam-sector-terminal-result-report-printed"), uid, user);
        return true;
    }

    public bool TryPrintRuntimeCoordinatePacket(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMSectorLeadReportComponent? component = null)
    {
        report = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!_stories.TryGetOpenRuntimeDistressStory(out var story) || story == null)
        {
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-no-open-runtime"), uid, user);
            return false;
        }

        report = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-route-printer-failed"), uid, user);
            report = default;
            return false;
        }

        var marker = FindDynamicMarker(story);
        var content = new StringBuilder(BuildRuntimeCoordinatePacket(story, marker));
        AppendRuntimeRouteStatus(content, marker);
        _paper.SetContent((report, paper), content.ToString());
        _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-route-printed", ("title", story.Title)), uid, user);
        return true;
    }

    public bool TryPrintRuntimeClosureReport(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMSectorLeadReportComponent? component = null)
    {
        report = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!_stories.TryGetOpenRuntimeDistressStory(out var story) || story == null)
        {
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-no-open-runtime"), uid, user);
            return false;
        }

        if (_traffic.HasPendingRecovery(story.Story.ToString()))
        {
            _popup.PopupEntity(
                Loc.GetString("luam-sector-terminal-popup-intercept-recovery-pending"),
                uid,
                user);
            return false;
        }

        report = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-closure-printer-failed"), uid, user);
            report = default;
            return false;
        }

        var marker = FindDynamicMarker(story);
        var evidence = AddComp<LuaMSectorEvidenceComponent>(report);
        evidence.Story = story.Story;
        evidence.AcknowledgeHazard = true;
        evidence.ResolveStory = true;
        evidence.Note = BuildRuntimeClosureEvidenceNote(story, marker);

        var content = new StringBuilder(BuildRuntimeClosureReport(story));
        AppendRuntimeRouteStatus(content, marker);
        _paper.SetContent((report, paper), content.ToString());
        _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-closure-printed", ("title", story.Title)), uid, user);
        return true;
    }

    public bool TryPrintRescueFollowUpReport(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMSectorLeadReportComponent? component = null)
    {
        report = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!_stories.TryGetLatestOpenRescueFollowUp(out var followUp) || followUp == null)
        {
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-no-rescue-followup"), uid, user);
            return false;
        }

        report = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-rescue-followup-printer-failed"), uid, user);
            report = default;
            return false;
        }

        var evidence = AddComp<LuaMSectorEvidenceComponent>(report);
        evidence.Story = new ProtoId<LuaMSectorStoryPrototype>(LuaMSectorStorySystem.RescueAfterActionStoryId);
        evidence.ClearRescueFollowUp = true;
        evidence.Note = BuildRescueFollowUpEvidenceNote(followUp);

        _paper.SetContent((report, paper), BuildRescueFollowUpReport(followUp));
        _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-rescue-followup-printed", ("title", followUp.Location)), uid, user);
        return true;
    }

    private static string BuildReport(
        LuaMSectorStatusSnapshot snapshot,
        LuaMSectorAutomationUiEntry automation,
        LuaMSectorMapNodeUiEntry[] sectorMapNodes)
    {
        var output = new StringBuilder();
        output.AppendLine("# LuaM sector lead report");
        output.AppendLine();
        output.AppendLine($"Stories: {snapshot.TotalStories}");
        output.AppendLine($"Active hazards: {snapshot.ActiveHazards}");
        output.AppendLine($"Active conditions: {snapshot.ActiveConditions}");
        output.AppendLine($"Acknowledged hazards: {snapshot.AcknowledgedHazards}");
        output.AppendLine($"Locked leads: {snapshot.LockedStories}");
        output.AppendLine();

        output.AppendLine("## Process automation");
        output.AppendLine($"- Dispatch profile: {automation.DispatchTier}; active operators: {automation.ActivePlayers}");
        output.AppendLine($"- Route calibration reserve: {automation.RouteCalibrationCredits}");
        if (automation.RouteCalibrationSources.Length == 0)
        {
            output.AppendLine("  No route calibration sources queued.");
        }
        else
        {
            output.AppendLine("  Queued route calibration sources:");
            foreach (var source in automation.RouteCalibrationSources)
            {
                output.AppendLine($"  - {source}");
            }
            if (automation.RouteCalibrationSourceChainDepth > 0)
                output.AppendLine($"- Route calibration chain depth: {automation.RouteCalibrationSourceChainDepth}");
            if (automation.RouteCalibrationRewardBonus > 0)
                output.AppendLine($"- Route calibration reward bonus: +{automation.RouteCalibrationRewardBonus}");
            if (automation.RouteCalibrationClosureRewardBonus > 0)
                output.AppendLine($"- Route calibration closure bonus: +{automation.RouteCalibrationClosureRewardBonus}");
            if (automation.RouteCalibrationRadiationDampingPreview > 0)
                output.AppendLine($"- Route calibration radiation damping: -{automation.RouteCalibrationRadiationDampingPreview}");
            if (automation.RouteCalibrationSensorDriftSuppressionPreview)
                output.AppendLine("- Route calibration sensor drift echo suppressed");
        }
        if (automation.RouteCalibrationHandoffReady &&
            !string.IsNullOrWhiteSpace(automation.RouteCalibrationHandoffSource))
        {
            output.AppendLine($"- Route calibration handoff ready: {automation.RouteCalibrationHandoffSource}");
        }
        if (!string.IsNullOrWhiteSpace(automation.ActiveRouteCalibrationSource))
        {
            output.AppendLine($"- Active route calibration source: {automation.ActiveRouteCalibrationSource}");
            if (automation.ActiveRouteCalibrationChainDepth > 0)
                output.AppendLine($"- Active route calibration chain depth: {automation.ActiveRouteCalibrationChainDepth}");
        }

        output.AppendLine(
            $"- Physical hooks: markers {automation.ActiveMarkers}, site notes {automation.SiteNotes}, condition hazards {automation.ConditionHazards}, sensor drift markers {automation.SensorDriftMarkers}");
        output.AppendLine();

        output.AppendLine("## Sector map");
        if (sectorMapNodes.Length == 0)
        {
            output.AppendLine("- No sector map nodes visible.");
        }
        else
        {
            foreach (var node in sectorMapNodes
                         .OrderBy(node => node.SortOrder)
                         .ThenBy(node => node.Kind)
                         .ThenBy(node => node.Title))
            {
                output.AppendLine($"- {node.Kind}: {node.Title} [{node.State}]");
                output.AppendLine($"  Location: {node.Location}; template: {node.TemplateId}; story: {node.StoryId}; pings {node.RoutePingCount}");

                if (!string.IsNullOrWhiteSpace(node.Detail))
                    output.AppendLine($"  Detail: {node.Detail}");

                if (!string.IsNullOrWhiteSpace(node.Risk))
                    output.AppendLine($"  Risk: {node.Risk}");
            }
        }

        output.AppendLine();

        output.AppendLine("## Sector conditions");
        var activeConditions = snapshot.Conditions
            .Where(condition => condition.Active)
            .OrderByDescending(condition => condition.Severity)
            .ThenBy(condition => condition.Title)
            .ToList();

        if (activeConditions.Count == 0)
        {
            output.AppendLine("- No active sector conditions.");
        }
        else
        {
            foreach (var condition in activeConditions)
            {
                output.AppendLine($"- SC-{condition.Severity} {condition.Title} [{condition.ConditionId}] by {condition.Actor}");
                output.AppendLine($"  {condition.Summary}");
            }
        }

        output.AppendLine();

        output.AppendLine("## Active leads");
        var activeHazards = snapshot.Hazards
            .Where(hazard => !hazard.Resolved)
            .OrderByDescending(hazard => hazard.Severity)
            .ThenBy(hazard => hazard.Title)
            .ToList();

        if (activeHazards.Count == 0)
        {
            output.AppendLine("- No active sector hazards.");
        }
        else
        {
            foreach (var hazard in activeHazards)
            {
                var state = hazard.Acknowledged ? "filed" : "open";
                output.AppendLine($"- {hazard.Story}: HZ-{hazard.Severity} {hazard.Title} ({state}, +{hazard.RewardBonus})");
                output.AppendLine($"  {hazard.Hazard}");
                if (!string.IsNullOrWhiteSpace(hazard.Description))
                    output.AppendLine($"  {hazard.Description}");
            }
        }

        output.AppendLine();
        output.AppendLine("## Locked leads");
        if (snapshot.LockedLeads.Count == 0)
        {
            output.AppendLine("- No locked reputation leads.");
        }
        else
        {
            foreach (var lead in snapshot.LockedLeads)
            {
                output.AppendLine($"- {lead.Title}: {lead.RequiredTarget} {lead.CurrentValue}/{lead.RequiredValue}");
            }
        }

        output.AppendLine();
        output.AppendLine("## Reputation");
        if (snapshot.Reputation.Count == 0)
        {
            output.AppendLine("- No reputation records.");
        }
        else
        {
            foreach (var entry in snapshot.Reputation)
            {
                output.AppendLine($"- {entry.Target}: {entry.Value} ({entry.Tier}, +{entry.RewardBonus} contracts)");
            }
        }

        output.AppendLine();
        output.AppendLine("## Recent history");
        if (snapshot.RecentHistory.Count == 0)
        {
            output.AppendLine("- No recent sector history.");
        }
        else
        {
            foreach (var entry in snapshot.RecentHistory)
            {
                output.AppendLine($"- {entry.Category}: {entry.Title} by {entry.Actor}");
                output.AppendLine($"  {entry.Summary}");
            }
        }

        return output.ToString();
    }

    private static string BuildRuntimeClosureReport(LuaMSectorStoryRecord story)
    {
        var output = new StringBuilder();
        output.AppendLine("# Итоговый отчет по аварийному сигналу");
        output.AppendLine();
        output.AppendLine($"Сюжет: {story.Story}");
        output.AppendLine($"Заявка: {story.Title}");
        output.AppendLine($"Награда: {story.ContractReward + story.HazardRewardBonus}");
        output.AppendLine();
        output.AppendLine("## Проверки на месте");
        output.AppendLine("[ ] координаты маяка проверены");
        output.AppendLine("[ ] угрозы осмотрены");
        output.AppendLine("[ ] груз, выживший, буксировка или результат восстановления записаны");
        output.AppendLine("[ ] оставлена зацепка для следующего пилота");
        output.AppendLine();
        output.AppendLine("## Угроза");
        output.AppendLine(string.IsNullOrWhiteSpace(story.Hazard) ? "Угроза не записана." : story.Hazard);
        output.AppendLine();
        output.AppendLine("## Заметки");
        output.AppendLine(story.ContractDescription);
        return output.ToString();
    }

    private static string BuildRuntimeClosureEvidenceNote(
        LuaMSectorStoryRecord story,
        LuaMDynamicEventMarkerComponent? marker)
    {
        var output = new StringBuilder($"runtime distress closure report filed: {story.Title}");

        if (marker == null)
            return $"{output}; no dynamic route marker";

        output.Append($"; marker {marker.TemplateId}");
        output.Append($"; route pings {marker.RoutePingCount}/{LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold}");

        if (marker.StabilizedFieldPacketPrinted)
            output.Append("; field packet printed");
        else if (marker.RoutePingCount >= LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold)
            output.Append("; field packet ready");
        else
            output.Append("; field packet pending");

        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
        {
            output.Append($"; active route calibration source: {marker.RouteCalibrationSource}");
            var chainDepth = LuaMSectorDynamicEventSystem.GetRouteCalibrationChainDepth(marker.RouteCalibrationSource);
            if (chainDepth > 0)
                output.Append($"; active route calibration chain depth: {chainDepth}");
            if (marker.RouteCalibrationRelayInherited)
                output.Append("; inherited calibration relay");
        }

        if (marker.RoutePingCount >= LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold)
            output.Append($"; {LuaMSectorDynamicEventSystem.BuildStabilizedRouteEvidenceNote(marker)}");

        return output.ToString();
    }

    private static string BuildRescueFollowUpEvidenceNote(LuaMSectorRescueAfterActionEntry entry)
    {
        return
            $"rescue blocker follow-up cleared: sequence={entry.Sequence}; " +
            $"location={entry.Location}; blockers={entry.Blockers}; originalPatient={entry.Patient}";
    }

    private static string BuildRescueFollowUpReport(LuaMSectorRescueAfterActionEntry entry)
    {
        var output = new StringBuilder();
        output.AppendLine("# LuaM rescue blocker follow-up");
        output.AppendLine();
        output.AppendLine($"Sequence: {entry.Sequence}");
        output.AppendLine($"Location: {entry.Location}");
        output.AppendLine($"Original blockers: {entry.Blockers}");
        output.AppendLine($"Treatment: {entry.TreatmentResult}");
        output.AppendLine($"Evacuation: {entry.EvacuationResult}");
        output.AppendLine();
        output.AppendLine("## Field check");
        output.AppendLine("[ ] corridor/access is open");
        output.AppendLine("[ ] local threat or crowd pressure is cleared");
        output.AppendLine("[ ] rescue route can be used again");
        output.AppendLine();
        output.AppendLine("Turn-in: file this paper as LuaM evidence after the corridor is actually clear.");
        return output.ToString();
    }

    private LuaMDynamicEventMarkerComponent? FindDynamicMarker(LuaMSectorStoryRecord story)
    {
        var query = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
        while (query.MoveNext(out _, out var marker))
        {
            if (marker.Story == story.Story)
                return marker;
        }

        return null;
    }

    private static void AppendRuntimeRouteStatus(StringBuilder output, LuaMDynamicEventMarkerComponent? marker)
    {
        output.AppendLine();
        output.AppendLine("## Active route status");

        if (marker == null)
        {
            output.AppendLine("- No dynamic route marker found.");
            return;
        }

        output.AppendLine(
            $"- Route pings: {marker.RoutePingCount}/{LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold}");

        if (!string.IsNullOrWhiteSpace(marker.LastRoutePingSummary))
            output.AppendLine($"- Last route ping: {marker.LastRoutePingSummary}");

        if (!string.IsNullOrWhiteSpace(marker.ConditionRiskSummary))
            output.AppendLine($"- Condition risk: {marker.ConditionRiskSummary}");

        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
        {
            output.AppendLine($"- Route calibration source: {marker.RouteCalibrationSource}");
            var chainDepth = LuaMSectorDynamicEventSystem.GetRouteCalibrationChainDepth(marker.RouteCalibrationSource);
            if (chainDepth > 0)
                output.AppendLine($"- Route calibration chain depth: {chainDepth}");
            output.AppendLine(marker.RouteCalibrationRelayInherited
                ? "- Calibration relay: inherited relay armed for this route."
                : "- Calibration relay: no inherited relay.");
        }

        if (marker.StabilizedFieldPacketPrinted)
            output.AppendLine("- Field report: printed; click the LuaM marker to close the task, or file the report as evidence.");
        else if (marker.RoutePingCount >= LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold)
            output.AppendLine("- Field report: optional; click the LuaM marker to close the task, or print a report as evidence.");
        else
            output.AppendLine("- Turn-in: click the LuaM marker at the site to close the task. Field report evidence requires more route pings.");

        if (marker.RoutePingCount >= LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold)
        {
            output.AppendLine(
                $"- Route calibration handoff: {LuaMSectorDynamicEventSystem.BuildStabilizedRouteEvidenceNote(marker)}");
        }
    }

    private static string BuildRuntimeCoordinatePacket(
        LuaMSectorStoryRecord story,
        LuaMDynamicEventMarkerComponent? marker)
    {
        var output = new StringBuilder();
        output.AppendLine("# Координатный пакет аварийного сигнала");
        output.AppendLine();
        output.AppendLine($"Сюжет: {story.Story}");
        output.AppendLine($"Заявка: {story.Title}");
        output.AppendLine($"Судно: {story.ContractVessel}");
        output.AppendLine($"Награда: {story.ContractReward + story.HazardRewardBonus}");
        output.AppendLine();
        output.AppendLine("## Активная метка");
        if (marker == null)
        {
            output.AppendLine("Для этой заявки сейчас нет активной динамической метки. Проверьте описание контракта, GPS, карту сектора и оставленные маяки.");
        }
        else
        {
            output.AppendLine($"Координаты: {marker.MarkerLocation}");
            output.AppendLine($"Шаблон: {marker.TemplateId}");
            output.AppendLine($"Оформил: {marker.CreatedBy}");
        }

        output.AppendLine();
        output.AppendLine("## Маршрут");
        output.AppendLine(story.ContractDescription);
        output.AppendLine();
        output.AppendLine("## Шаги маршрута");
        output.AppendLine("[ ] перенести активную метку на навигационную карту");
        output.AppendLine("[ ] подойти вручную и проверить локальные угрозы");
        output.AppendLine("[ ] записать результат спасения, груза, буксировки или реестра");
        output.AppendLine("[ ] приложить этот пакет к итоговому отчету");
        output.AppendLine();
        output.AppendLine("## Угроза");
        output.AppendLine(string.IsNullOrWhiteSpace(story.Hazard) ? "Угроза не записана." : story.Hazard);
        output.AppendLine();
        output.AppendLine("## Использование в поле");
        output.AppendLine("[ ] проверить маяк или обломки");
        output.AppendLine("[ ] оставить пакет вместе с заметками восстановления");
        return output.ToString();
    }
}
