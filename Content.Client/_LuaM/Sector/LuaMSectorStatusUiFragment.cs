using Content.Shared._LuaM.Sector;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using System.Linq;

namespace Content.Client._LuaM.Sector;

public sealed class LuaMSectorStatusUiFragment : BoxContainer
{
    [Dependency] private readonly IClipboardManager _clipboard = default!;

    private readonly Label _summary = new();
    private readonly BoxContainer _digest = new();
    private readonly BoxContainer _briefing = new();
    private readonly BoxContainer _questTasks = new();
    private readonly BoxContainer _automation = new();
    private readonly BoxContainer _sectorMap = new();
    private readonly BoxContainer _conditions = new();
    private readonly BoxContainer _preferredProcesses = new();
    private readonly BoxContainer _hazards = new();
    private readonly BoxContainer _lockedLeads = new();
    private readonly BoxContainer _reputation = new();
    private readonly BoxContainer _history = new();

    public LuaMSectorStatusUiFragment()
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;
        Margin = new Thickness(4, 2);

        AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-header"),
            StyleClasses = { "LabelHeading" },
        });

        _summary.Margin = new Thickness(0, 2, 0, 4);
        AddChild(_summary);

        var scroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
        };

        var body = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        scroll.AddChild(body);
        AddChild(scroll);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-digest")));
        _digest.Orientation = LayoutOrientation.Vertical;
        _digest.HorizontalExpand = true;
        body.AddChild(_digest);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-briefing")));
        _briefing.Orientation = LayoutOrientation.Vertical;
        _briefing.HorizontalExpand = true;
        body.AddChild(_briefing);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-quests")));
        _questTasks.Orientation = LayoutOrientation.Vertical;
        _questTasks.HorizontalExpand = true;
        body.AddChild(_questTasks);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-automation")));
        _automation.Orientation = LayoutOrientation.Vertical;
        _automation.HorizontalExpand = true;
        body.AddChild(_automation);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-sector-map")));
        _sectorMap.Orientation = LayoutOrientation.Vertical;
        _sectorMap.HorizontalExpand = true;
        body.AddChild(_sectorMap);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-locked")));
        _lockedLeads.Orientation = LayoutOrientation.Vertical;
        _lockedLeads.HorizontalExpand = true;
        body.AddChild(_lockedLeads);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-conditions")));
        _conditions.Orientation = LayoutOrientation.Vertical;
        _conditions.HorizontalExpand = true;
        body.AddChild(_conditions);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-preferred")));
        _preferredProcesses.Orientation = LayoutOrientation.Vertical;
        _preferredProcesses.HorizontalExpand = true;
        body.AddChild(_preferredProcesses);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-hazards")));
        _hazards.Orientation = LayoutOrientation.Vertical;
        _hazards.HorizontalExpand = true;
        body.AddChild(_hazards);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-reputation")));
        _reputation.Orientation = LayoutOrientation.Vertical;
        _reputation.HorizontalExpand = true;
        body.AddChild(_reputation);

        body.AddChild(MakeSection(Loc.GetString("luam-sector-status-history")));
        _history.Orientation = LayoutOrientation.Vertical;
        _history.HorizontalExpand = true;
        body.AddChild(_history);
    }

    public void UpdateState(LuaMSectorStatusUiState state)
    {
        _summary.Text = Loc.GetString("luam-sector-status-summary",
            ("stories", state.TotalStories),
            ("active", state.ActiveHazards),
            ("acknowledged", state.AcknowledgedHazards),
            ("insurance", state.InsurancePayouts),
            ("blackbox", state.BlackBoxRecoveries),
            ("companies", state.CompanyRecords),
            ("ships", state.ShipRecords),
            ("conditions", state.ActiveConditions),
            ("locked", state.LockedStories));

        _digest.RemoveAllChildren();
        if (state.DigestLines.Length == 0)
        {
            _digest.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-digest")));
        }
        else
        {
            foreach (var line in state.DigestLines)
            {
                _digest.AddChild(new Label
                {
                    Text = Loc.GetString("luam-sector-status-digest-line", ("line", line)),
                    ClipText = false,
                });
            }
        }

        _briefing.RemoveAllChildren();
        if (state.BriefingSteps.Length == 0)
        {
            _briefing.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-briefing")));
        }
        else
        {
            foreach (var step in state.BriefingSteps)
            {
                _briefing.AddChild(new Label
                {
                    Text = Loc.GetString("luam-sector-status-briefing-step", ("step", step)),
                    ClipText = false,
                });
            }
        }

        _questTasks.RemoveAllChildren();
        if (state.QuestTasks.Length == 0)
        {
            _questTasks.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-quests")));
        }
        else
        {
            _questTasks.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-quests-hint")));
            var tasks = state.QuestTasks.Take(6).ToArray();
            for (var i = 0; i < tasks.Length; i++)
                _questTasks.AddChild(MakeQuestTaskRow(tasks[i], i + 1));
        }

        _automation.RemoveAllChildren();
        _automation.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-automation-next",
                ("next", state.Automation.NextAutomaticEvent),
                ("players", state.Automation.ActivePlayers)),
            ClipText = true,
        });
        _automation.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-dispatch-profile",
                ("tier", state.Automation.DispatchTier),
                ("score", state.Automation.DispatchReputationScore),
                ("reduction", state.Automation.DispatchCooldownReductionPercent),
                ("min", FormatMinutes(state.Automation.DispatchCooldownMinSeconds)),
                ("max", FormatMinutes(state.Automation.DispatchCooldownMaxSeconds))),
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        });
        _automation.AddChild(new Label
        {
            Text = BuildRouteCalibrationReserveText(state.Automation),
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
        _automation.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-route-closure-instruction")));

        _sectorMap.RemoveAllChildren();
        if (state.SectorMapNodes.Length == 0)
        {
            _sectorMap.AddChild(MakeMutedLabel(Loc.GetString("luam-sector-status-no-sector-map")));
        }
        else
        {
            foreach (var node in state.SectorMapNodes.Take(8))
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
            {
                _lockedLeads.AddChild(new Label
                {
                    Text = Loc.GetString("luam-sector-status-locked-entry",
                        ("title", lead.Title),
                        ("target", lead.RequiredTarget),
                        ("current", lead.CurrentValue),
                        ("required", lead.RequiredValue)),
                    ClipText = true,
                });
            }
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
            {
                _reputation.AddChild(new Label
                {
                    Text = Loc.GetString("luam-sector-status-reputation-effect-entry",
                        ("target", entry.Target),
                        ("value", entry.Value),
                        ("tier", entry.Tier),
                        ("bonus", entry.RewardBonus)),
                    ClipText = true,
                });
            }
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

    private static Label MakeSection(string text)
    {
        return new Label
        {
            Text = text,
            StyleClasses = { "LabelSubText" },
            Margin = new Thickness(0, 4, 0, 2),
        };
    }

    private static Label MakeMutedLabel(string text)
    {
        return new Label
        {
            Text = text,
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        };
    }

    private static int FormatMinutes(int seconds)
    {
        return (int) Math.Ceiling(seconds / 60f);
    }

    private static string BuildRouteCalibrationReserveText(LuaMSectorAutomationUiEntry automation)
    {
        var text = Loc.GetString("luam-sector-status-route-calibration-reserve",
            ("credits", automation.RouteCalibrationCredits));

        if (!string.IsNullOrWhiteSpace(automation.RouteCalibrationSource))
        {
            text += $" | {Loc.GetString("luam-sector-status-route-calibration-source",
                ("source", automation.RouteCalibrationSource))}";

            if (automation.RouteCalibrationSourceChainDepth > 0)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-chain-depth",
                    ("depth", automation.RouteCalibrationSourceChainDepth))}";
            }

            if (automation.RouteCalibrationRewardBonus > 0)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-reward-bonus",
                    ("bonus", automation.RouteCalibrationRewardBonus))}";
            }

            if (automation.RouteCalibrationClosureRewardBonus > 0)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-closure-bonus",
                    ("bonus", automation.RouteCalibrationClosureRewardBonus))}";
            }

            if (automation.RouteCalibrationRadiationDampingPreview > 0)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-radiation-damping",
                    ("damping", automation.RouteCalibrationRadiationDampingPreview))}";
            }

            if (automation.RouteCalibrationSensorDriftSuppressionPreview)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-sensor-drift-suppressed")}";
            }

            if (automation.RouteCalibrationSources is { Length: > 1 } sources)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-queue",
                    ("sources", string.Join(" -> ", sources.Skip(1))))}";
            }
        }

        if (automation.RouteCalibrationHandoffReady &&
            !string.IsNullOrWhiteSpace(automation.RouteCalibrationHandoffSource))
        {
            text += $" | {Loc.GetString("luam-sector-status-route-calibration-handoff",
                ("source", automation.RouteCalibrationHandoffSource))}";
        }

        if (!string.IsNullOrWhiteSpace(automation.ActiveRouteCalibrationSource))
        {
            text += $" | {Loc.GetString("luam-sector-status-route-calibration-active-source",
                ("source", automation.ActiveRouteCalibrationSource))}";
            if (automation.ActiveRouteCalibrationChainDepth > 0)
            {
                text += $" | {Loc.GetString("luam-sector-status-route-calibration-active-chain-depth",
                    ("depth", automation.ActiveRouteCalibrationChainDepth))}";
            }
        }

        return text;
    }

    private static Control MakeQuestTaskRow(LuaMSectorQuestTaskUiEntry task, int number)
    {
        var panel = new PanelContainer
        {
            StyleClasses = { "AngleRect" },
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

        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-quest-header",
                ("number", number),
                ("status", task.Status),
                ("title", task.Title)),
            ClipText = false,
        });

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
        row.AddChild(new Label
        {
            Text = text,
            StyleClasses = { "LabelSubText" },
            HorizontalExpand = true,
            ClipText = false,
        });
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

        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-hazard-title",
                ("title", hazard.Title),
                ("severity", hazard.Severity),
                ("bonus", hazard.RewardBonus),
                ("state", state)),
            ClipText = true,
        });

        row.AddChild(new Label
        {
            Text = hazard.Hazard,
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        });

        if (!string.IsNullOrWhiteSpace(hazard.Description))
        {
            row.AddChild(new Label
            {
                Text = hazard.Description,
                StyleClasses = { "LabelSubText" },
                ClipText = true,
            });
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

        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-sector-map-title",
                ("kind", node.Kind),
                ("title", node.Title),
                ("state", node.State)),
            ClipText = true,
        });

        var idRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };

        idRow.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-sector-map-entry",
                ("location", node.Location),
                ("template", node.TemplateId),
                ("story", node.StoryId),
                ("pings", node.RoutePingCount)),
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        });
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
            row.AddChild(new Label
            {
                Text = detail,
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }

        if (!string.IsNullOrWhiteSpace(node.Risk))
        {
            row.AddChild(new Label
            {
                Text = node.Risk,
                StyleClasses = { "LabelSubText" },
                ClipText = true,
            });
        }

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
        titleRow.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-condition-title",
                ("title", condition.Title),
                ("severity", condition.Severity),
                ("id", condition.ConditionId),
                ("actor", condition.Actor)),
            ClipText = true,
        });
        titleRow.AddChild(MakeCopyIdButton(condition.ConditionId));
        row.AddChild(titleRow);

        row.AddChild(new Label
        {
            Text = condition.Summary,
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        });

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
        titleRow.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-preferred-title",
                ("title", process.Title),
                ("id", process.TemplateId),
                ("state", process.Unlocked
                    ? Loc.GetString("luam-sector-status-preferred-open")
                    : Loc.GetString("luam-sector-status-preferred-locked"))),
            ClipText = true,
        });
        titleRow.AddChild(MakeCopyIdButton(process.TemplateId));
        row.AddChild(titleRow);

        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-preferred-entry",
                ("target", process.ReputationTarget),
                ("current", process.CurrentReputation),
                ("required", process.RequiredReputation),
                ("tier", process.Tier),
                ("reward", process.BaseReward),
                ("bonus", process.ReputationBonus)),
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        });

        if (!process.CanRequestNow && !string.IsNullOrWhiteSpace(process.BlockReason))
        {
            row.AddChild(new Label
            {
                Text = process.BlockReason,
                StyleClasses = { "LabelSubText" },
                ClipText = true,
            });
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

        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-status-history-title",
                ("category", entry.Category),
                ("title", entry.Title),
                ("actor", entry.Actor)),
            ClipText = true,
        });

        row.AddChild(new Label
        {
            Text = entry.Summary,
            StyleClasses = { "LabelSubText" },
            ClipText = true,
        });

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
