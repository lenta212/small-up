using Content.Client.Message;
using Content.Shared._LuaM.Sector;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Client._LuaM.Sector;

public sealed class LuaMSectorStatusUiFragment : BoxContainer
{
    [Dependency] private readonly IClipboardManager _clipboard = default!;

    private readonly RichTextLabel _summary = new();
    private readonly RichTextLabel _operationalState = new();
    private readonly BoxContainer _digest = new();
    private readonly BoxContainer _briefing = new();
    private readonly BoxContainer _questTasks = new();
    private readonly BoxContainer _automation = new();
    private readonly BoxContainer _trafficContacts = new();
    private readonly BoxContainer _sectorMap = new();
    private readonly BoxContainer _conditions = new();
    private readonly BoxContainer _preferredProcesses = new();
    private readonly BoxContainer _hazards = new();
    private readonly BoxContainer _lockedLeads = new();
    private readonly BoxContainer _reputation = new();
    private readonly BoxContainer _history = new();

    public TabContainer Tabs { get; }

    public LuaMSectorStatusUiFragment()
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;
        Margin = new Thickness(0);

        AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-header"),
            StyleClasses = { "UiTextTitle" },
        });

        var summaryPanel = new PanelContainer
        {
            StyleClasses = { "UiSurfaceHeader" },
            HorizontalExpand = true,
            Margin = new Thickness(0, 5, 0, 8),
        };
        var summaryBody = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(10, 7),
        };
        _operationalState.StyleClasses.Add("UiTextMuted");
        _operationalState.Margin = new Thickness(0, 4, 0, 0);
        summaryBody.AddChild(_summary);
        summaryBody.AddChild(_operationalState);
        summaryPanel.AddChild(summaryBody);
        AddChild(summaryPanel);

        Tabs = new TabContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
        };

        var (overviewPage, overviewBody) = MakeTabPage();
        var (tasksPage, tasksBody) = MakeTabPage();
        var (sectorPage, sectorBody) = MakeTabPage();
        var (journalPage, journalBody) = MakeTabPage();

        Tabs.AddChild(overviewPage);
        Tabs.AddChild(tasksPage);
        Tabs.AddChild(sectorPage);
        Tabs.AddChild(journalPage);
        TabContainer.SetTabTitle(overviewPage, Loc.GetString("luam-sector-status-tab-overview"));
        TabContainer.SetTabTitle(tasksPage, Loc.GetString("luam-sector-status-tab-tasks"));
        TabContainer.SetTabTitle(sectorPage, Loc.GetString("luam-sector-status-tab-sector"));
        TabContainer.SetTabTitle(journalPage, Loc.GetString("luam-sector-status-tab-journal"));

        AddSection(overviewBody, Loc.GetString("luam-sector-status-digest"), _digest);
        AddSection(overviewBody, Loc.GetString("luam-sector-status-briefing"), _briefing);
        AddSection(overviewBody, Loc.GetString("luam-sector-status-automation"), _automation);

        AddSection(tasksBody, Loc.GetString("luam-sector-status-quests"), _questTasks);
        AddSection(tasksBody, Loc.GetString("luam-sector-status-preferred"), _preferredProcesses);
        AddSection(tasksBody, Loc.GetString("luam-sector-status-locked"), _lockedLeads);

        AddSection(sectorBody, Loc.GetString("luam-sector-status-traffic"), _trafficContacts);
        AddSection(sectorBody, Loc.GetString("luam-sector-status-sector-map"), _sectorMap);
        AddSection(sectorBody, Loc.GetString("luam-sector-status-conditions"), _conditions);
        AddSection(sectorBody, Loc.GetString("luam-sector-status-hazards"), _hazards);

        AddSection(journalBody, Loc.GetString("luam-sector-status-reputation"), _reputation);
        AddSection(journalBody, Loc.GetString("luam-sector-status-history"), _history);

        AddChild(Tabs);
    }

    public void UpdateState(LuaMSectorStatusUiState state)
    {
        _summary.SetMarkup(Loc.GetString("luam-sector-status-summary",
            ("stories", state.TotalStories),
            ("active", state.ActiveHazards),
            ("acknowledged", state.AcknowledgedHazards),
            ("insurance", state.InsurancePayouts),
            ("blackbox", state.BlackBoxRecoveries),
            ("companies", state.CompanyRecords),
            ("ships", state.ShipRecords),
            ("conditions", state.ActiveConditions),
            ("locked", state.LockedStories)));
        var highestSeverity = state.Hazards
            .Where(hazard => !hazard.Resolved)
            .Select(hazard => hazard.Severity)
            .Concat(state.Conditions
                .Where(condition => condition.Active)
                .Select(condition => condition.Severity))
            .DefaultIfEmpty(0)
            .Max();
        _operationalState.SetMarkup(BuildOperationalState(
            state.ActiveHazards + state.ActiveConditions,
            state.AcknowledgedHazards,
            highestSeverity));

        _digest.RemoveAllChildren();
        if (state.DigestLines.Length == 0)
        {
            _digest.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-digest")));
        }
        else
        {
            foreach (var line in state.DigestLines)
                _digest.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-digest-line", ("line", line))));
        }

        _briefing.RemoveAllChildren();
        if (state.BriefingSteps.Length == 0)
        {
            _briefing.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-briefing")));
        }
        else
        {
            foreach (var step in state.BriefingSteps)
                _briefing.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-briefing-step", ("step", step))));
        }

        _questTasks.RemoveAllChildren();
        if (state.QuestTasks.Length == 0)
        {
            _questTasks.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-quests")));
        }
        else
        {
            _questTasks.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-quests-hint")));
            var tasks = state.QuestTasks.Take(GetVisibleCount(state.QuestTasks.Length, 6)).ToArray();
            _questTasks.AddChild(MakeMutedLabel(BuildVisibleCount(tasks.Length, state.QuestTasks.Length)));
            for (var i = 0; i < tasks.Length; i++)
                _questTasks.AddChild(MakeQuestTaskRow(tasks[i], i + 1));
        }

        _automation.RemoveAllChildren();
        _automation.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-automation-next",
            ("next", state.Automation.NextAutomaticEvent),
            ("players", state.Automation.ActivePlayers))));
        _automation.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-dispatch-profile",
                ("tier", state.Automation.DispatchTier),
                ("score", state.Automation.DispatchReputationScore),
                ("reduction", state.Automation.DispatchCooldownReductionPercent),
                ("min", FormatMinutes(state.Automation.DispatchCooldownMinSeconds)),
                ("max", FormatMinutes(state.Automation.DispatchCooldownMaxSeconds)))));
        _automation.AddChild(MakeMutedLabel(BuildRouteCalibrationReserveText(state.Automation)));
        _automation.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-route-closure-instruction")));

        _trafficContacts.RemoveAllChildren();
        if (state.TrafficContacts.Length == 0)
        {
            _trafficContacts.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-traffic")));
        }
        else
        {
            foreach (var contact in state.TrafficContacts)
                _trafficContacts.AddChild(MakeTrafficContactRow(contact));
        }

        _sectorMap.RemoveAllChildren();
        if (state.SectorMapNodes.Length == 0)
        {
            _sectorMap.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-sector-map")));
        }
        else
        {
            var nodes = state.SectorMapNodes.Take(GetVisibleCount(state.SectorMapNodes.Length, 8)).ToArray();
            _sectorMap.AddChild(MakeMutedLabel(BuildVisibleCount(nodes.Length, state.SectorMapNodes.Length)));
            foreach (var node in nodes)
                _sectorMap.AddChild(MakeSectorMapRow(node));
        }

        _lockedLeads.RemoveAllChildren();
        if (state.LockedLeads.Length == 0)
        {
            _lockedLeads.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-locked")));
        }
        else
        {
            foreach (var lead in state.LockedLeads)
                _lockedLeads.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-locked-entry",
                        ("title", lead.Title),
                        ("target", lead.RequiredTarget),
                        ("current", lead.CurrentValue),
                        ("required", lead.RequiredValue))));
        }

        _conditions.RemoveAllChildren();
        if (state.Conditions.Length == 0)
        {
            _conditions.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-conditions")));
        }
        else
        {
            foreach (var condition in state.Conditions)
                _conditions.AddChild(MakeConditionRow(condition));
        }

        _preferredProcesses.RemoveAllChildren();
        if (state.PreferredProcesses.Length == 0)
        {
            _preferredProcesses.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-preferred")));
        }
        else
        {
            foreach (var process in state.PreferredProcesses)
                _preferredProcesses.AddChild(MakePreferredProcessRow(process));
        }

        _hazards.RemoveAllChildren();
        if (state.Hazards.Length == 0)
        {
            _hazards.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-hazards")));
        }
        else
        {
            foreach (var hazard in state.Hazards)
                _hazards.AddChild(MakeHazardRow(hazard));
        }

        _reputation.RemoveAllChildren();
        if (state.Reputation.Length == 0)
        {
            _reputation.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-reputation")));
        }
        else
        {
            foreach (var entry in state.Reputation)
                _reputation.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-reputation-effect-entry",
                        ("target", entry.Target),
                        ("value", entry.Value),
                        ("tier", entry.Tier),
                        ("bonus", entry.RewardBonus))));
        }

        _history.RemoveAllChildren();
        if (state.RecentHistory.Length == 0)
        {
            _history.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-history")));
        }
        else
        {
            foreach (var entry in state.RecentHistory)
                _history.AddChild(MakeHistoryRow(entry));
        }
    }

    private static (ScrollContainer Page, BoxContainer Body) MakeTabPage()
    {
        var page = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
            VScrollEnabled = true,
            ReserveScrollbarSpace = true,
        };

        var body = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new Thickness(4, 8, 4, 0),
        };
        page.AddChild(body);
        return (page, body);
    }

    private static void AddSection(BoxContainer parent, string title, BoxContainer content)
    {
        content.Orientation = LayoutOrientation.Vertical;
        content.HorizontalExpand = true;

        var panel = new PanelContainer
        {
            StyleClasses = { "UiSurfaceSection" },
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var body = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(9, 7),
        };
        body.AddChild(new Label
        {
            Text = title,
            StyleClasses = { "UiTextSection" },
            Margin = new Thickness(0, 0, 0, 5),
        });
        body.AddChild(content);
        panel.AddChild(body);
        parent.AddChild(panel);
    }

    private static RichTextLabel MakeBodyLabel(string text)
    {
        var message = new FormattedMessage();
        message.AddText(text);
        var label = new RichTextLabel
        {
            HorizontalExpand = true,
        };
        label.SetMessage(message);
        return label;
    }

    private static RichTextLabel MakeMutedLabel(string text)
    {
        var label = MakeBodyLabel(text);
        label.StyleClasses.Add("UiTextMuted");
        return label;
    }

    private static int FormatMinutes(int seconds)
    {
        return (int) Math.Ceiling(seconds / 60f);
    }

    private static string BuildOperationalState(int activeSignals, int acknowledgedHazards, int highestSeverity)
    {
        var state = Loc.GetString(GetOperationalStateLocKey(
            activeSignals,
            acknowledgedHazards,
            highestSeverity));

        return Loc.GetString(
            "luam-sector-status-operational-state",
            ("state", state),
            ("severity", highestSeverity),
            ("active", activeSignals));
    }

    private static string GetOperationalStateLocKey(int activeSignals, int acknowledgedHazards, int highestSeverity)
    {
        if (highestSeverity >= 4)
            return "luam-sector-status-operational-critical";

        if (activeSignals > 0)
            return "luam-sector-status-operational-danger";

        return acknowledgedHazards > 0
            ? "luam-sector-status-operational-monitoring"
            : "luam-sector-status-operational-stable";
    }

    private static int GetVisibleCount(int total, int limit)
    {
        return Math.Min(total, limit);
    }

    private static string BuildVisibleCount(int shown, int total)
    {
        return Loc.GetString(
            "luam-sector-status-showing-count",
            ("shown", shown),
            ("total", total));
    }

    private static string BuildRouteCalibrationReserveText(LuaMSectorAutomationUiEntry automation)
    {
        var lines = new List<string>
        {
            Loc.GetString("luam-sector-status-route-calibration-reserve",
                ("credits", automation.RouteCalibrationCredits)),
        };

        if (!string.IsNullOrWhiteSpace(automation.RouteCalibrationSource))
        {
            lines.Add(Loc.GetString("luam-sector-status-route-calibration-source",
                ("source", automation.RouteCalibrationSource)));

            if (automation.RouteCalibrationSourceChainDepth > 0)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-chain-depth",
                    ("depth", automation.RouteCalibrationSourceChainDepth)));
            }

            if (automation.RouteCalibrationRewardBonus > 0)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-reward-bonus",
                    ("bonus", automation.RouteCalibrationRewardBonus)));
            }

            if (automation.RouteCalibrationClosureRewardBonus > 0)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-closure-bonus",
                    ("bonus", automation.RouteCalibrationClosureRewardBonus)));
            }

            if (automation.RouteCalibrationRadiationDampingPreview > 0)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-radiation-damping",
                    ("damping", automation.RouteCalibrationRadiationDampingPreview)));
            }

            if (automation.RouteCalibrationSensorDriftSuppressionPreview)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-sensor-drift-suppressed"));
            }

            if (automation.RouteCalibrationSources is { Length: > 1 } sources)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-queue",
                    ("sources", string.Join(" -> ", sources.Skip(1)))));
            }
        }

        if (automation.RouteCalibrationHandoffReady &&
            !string.IsNullOrWhiteSpace(automation.RouteCalibrationHandoffSource))
        {
            lines.Add(Loc.GetString("luam-sector-status-route-calibration-handoff",
                ("source", automation.RouteCalibrationHandoffSource)));
        }

        if (!string.IsNullOrWhiteSpace(automation.ActiveRouteCalibrationSource))
        {
            lines.Add(Loc.GetString("luam-sector-status-route-calibration-active-source",
                ("source", automation.ActiveRouteCalibrationSource)));
            if (automation.ActiveRouteCalibrationChainDepth > 0)
            {
                lines.Add(Loc.GetString("luam-sector-status-route-calibration-active-chain-depth",
                    ("depth", automation.ActiveRouteCalibrationChainDepth)));
            }
        }

        return string.Join("\n", lines.Select(line => $"- {line}"));
    }

    private static Control MakeQuestTaskRow(LuaMSectorQuestTaskUiEntry task, int number)
    {
        var panel = new PanelContainer
        {
            StyleClasses = { "UiSurfaceCard" },
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(6, 5),
        };
        panel.AddChild(row);

        row.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-quest-header",
                ("number", number),
                ("status", task.Status),
                ("title", task.Title))));

        row.AddChild(new PanelContainer
        {
            StyleClasses = { "HighDivider" },
            HorizontalExpand = true,
            Margin = new Thickness(0, 4, 0, 4),
        });

        if (!string.IsNullOrWhiteSpace(task.Location))
            AddQuestLine(row, Loc.GetString("luam-sector-status-quest-step-location", ("location", task.Location)));

        if (!string.IsNullOrWhiteSpace(task.Objective))
            AddQuestLine(row, Loc.GetString("luam-sector-status-quest-step-action", ("objective", task.Objective)));

        if (!string.IsNullOrWhiteSpace(task.TurnIn))
            AddQuestLine(row, Loc.GetString("luam-sector-status-quest-step-finish", ("turnin", task.TurnIn)));

        if (!string.IsNullOrWhiteSpace(task.Reward))
            AddQuestLine(row, Loc.GetString("luam-sector-status-quest-step-reward", ("reward", task.Reward)));

        return panel;
    }

    private static void AddQuestLine(BoxContainer row, string text)
    {
        row.AddChild(MakeMutedLabel(text));
    }

    private BoxContainer MakeHazardRow(LuaMSectorHazardUiEntry hazard)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var state = hazard.Resolved
            ? Loc.GetString("luam-sector-status-state-resolved")
            : hazard.Acknowledged
                ? Loc.GetString("luam-sector-status-state-filed")
                : Loc.GetString("luam-sector-status-state-active");

        row.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-hazard-title",
                ("title", hazard.Title),
                ("severity", hazard.Severity),
                ("bonus", hazard.RewardBonus),
                ("state", state))));

        row.AddChild(MakeMutedLabel(hazard.Hazard));

        if (!string.IsNullOrWhiteSpace(hazard.Description))
        {
            row.AddChild(MakeMutedLabel(hazard.Description));
        }

        return row;
    }

    private BoxContainer MakeSectorMapRow(LuaMSectorMapNodeUiEntry node)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };

        row.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-sector-map-title",
                ("kind", node.Kind),
                ("title", node.Title),
                ("state", node.State))));

        var idRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };

        idRow.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-sector-map-entry",
                ("location", node.Location),
                ("template", node.TemplateId),
                ("story", node.StoryId),
                ("pings", node.RoutePingCount))));
        idRow.AddChild(MakeCopyIdButton(node.TemplateId));
        idRow.AddChild(MakeCopyIdButton(node.StoryId));
        row.AddChild(idRow);

        var detail = node.RoutePingCount > 0
            ? Loc.GetString("luam-sector-terminal-sector-map-detail-pings",
                ("detail", node.Detail),
                ("pings", node.RoutePingCount))
            : node.Detail;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            row.AddChild(MakeMutedLabel(detail));
        }

        if (!string.IsNullOrWhiteSpace(node.Risk))
        {
            row.AddChild(MakeMutedLabel(node.Risk));
        }

        return row;
    }

    private static BoxContainer MakeTrafficContactRow(LuaMSectorTrafficUiEntry contact)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };
        row.AddChild(MakeBodyLabel(Loc.GetString(
                "luam-sector-terminal-traffic-title",
                ("code", contact.ContactCode),
                ("profile", contact.Profile))));
        row.AddChild(MakeMutedLabel(Loc.GetString(
                "luam-sector-terminal-traffic-detail",
                ("signature", contact.Signature),
                ("range", contact.RangeMeters),
                ("seconds", contact.SecondsRemaining))));
        row.AddChild(MakeMutedLabel(Loc.GetString(
                "luam-sector-terminal-traffic-objective",
                ("objective", contact.Objective),
                ("template", contact.TemplateId))));
        row.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-terminal-contact-use-terminal")));
        return row;
    }

    private BoxContainer MakeConditionRow(LuaMSectorConditionUiEntry condition)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var titleRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-condition-title",
                ("title", condition.Title),
                ("severity", condition.Severity),
                ("id", condition.ConditionId),
                ("actor", condition.Actor))));
        titleRow.AddChild(MakeCopyIdButton(condition.ConditionId));
        row.AddChild(titleRow);

        row.AddChild(MakeMutedLabel(condition.Summary));

        return row;
    }

    private BoxContainer MakePreferredProcessRow(LuaMSectorPreferredProcessUiEntry process)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var titleRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-preferred-title",
                ("title", process.Title),
                ("id", process.TemplateId),
                ("state", process.Unlocked
                    ? Loc.GetString("luam-sector-status-preferred-open")
                    : Loc.GetString("luam-sector-status-preferred-locked")))));
        titleRow.AddChild(MakeCopyIdButton(process.TemplateId));
        row.AddChild(titleRow);

        row.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-preferred-entry",
                ("target", process.ReputationTarget),
                ("current", process.CurrentReputation),
                ("required", process.RequiredReputation),
                ("tier", process.Tier),
                ("reward", process.BaseReward),
                ("bonus", process.ReputationBonus))));

        if (!process.CanRequestNow && !string.IsNullOrWhiteSpace(process.BlockReason))
        {
            row.AddChild(MakeMutedLabel(process.BlockReason));
        }

        return row;
    }

    private static BoxContainer MakeHistoryRow(LuaMSectorHistoryUiEntry entry)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };

        row.AddChild(MakeBodyLabel(Loc.GetString("luam-sector-status-history-title",
                ("category", entry.Category),
                ("title", entry.Title),
                ("actor", entry.Actor))));

        row.AddChild(MakeMutedLabel(entry.Summary));

        return row;
    }

    private Button MakeCopyIdButton(string id)
    {
        var shortId = LuaMShortIdFormatter.BuildShortId(id);
        var button = new Button
        {
            Text = shortId,
            ToolTip = Loc.GetString("luam-sector-copy-id-tooltip", ("id", id)),
            Margin = new Thickness(4, 0, 0, 0),
            MinHeight = 24,
        };
        button.OnPressed += _ => _clipboard.SetText(shortId);
        return button;
    }
}
