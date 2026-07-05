using Content.Client.UserInterface.Controls;
using Content.Shared._LuaM.Sector;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Content.Client._LuaM.Sector.UI;

public sealed class LuaMSectorTerminalWindow : FancyWindow
{
    public event Action<LuaMSectorTerminalAction, string, string>? ActionRequested;

    [Dependency] private readonly IClipboardManager _clipboard = default!;

    private readonly Label _summary = new();
    private readonly Label _actionResult = new();
    private readonly BoxContainer _questTasks = MakeList();
    private readonly BoxContainer _automation = MakeList();
    private readonly BoxContainer _sectorMap = MakeList();
    private readonly BoxContainer _preferredProcesses = MakeList();
    private readonly BoxContainer _insuranceCases = MakeList();
    private readonly BoxContainer _registryRecords = MakeList();
    private readonly BoxContainer _conditions = MakeList();
    private readonly BoxContainer _leads = MakeList();
    private readonly BoxContainer _reputation = MakeList();
    private readonly BoxContainer _locked = MakeList();
    private readonly BoxContainer _history = MakeList();
    private readonly Dictionary<LuaMSectorTerminalAction, Button> _actionButtons = new();

    public LuaMSectorTerminalWindow()
    {
        IoCManager.InjectDependencies(this);
        Title = Loc.GetString("luam-sector-terminal-title");
        Resizable = true;
        MinSize = new Vector2(640, 480);
        SetSize = new Vector2(820, 700);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new Thickness(8),
        };

        ContentsContainer.AddChild(root);

        root.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-heading"),
            StyleClasses = { "LabelHeading" },
        });

        _summary.Margin = new Thickness(0, 4, 0, 8);
        root.AddChild(_summary);

        _actionResult.StyleClasses.Add("LabelSubText");
        _actionResult.Margin = new Thickness(0, 0, 0, 8);
        root.AddChild(_actionResult);

        AddActionGroup(root, Loc.GetString("luam-sector-terminal-group-process"), [
            (Loc.GetString("luam-sector-terminal-action-refresh"), LuaMSectorTerminalAction.Refresh, Loc.GetString("luam-sector-terminal-action-refresh-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-generate"), LuaMSectorTerminalAction.RequestDynamicEvent, Loc.GetString("luam-sector-terminal-action-generate-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-ping"), LuaMSectorTerminalAction.PingActiveRouteMarker, Loc.GetString("luam-sector-terminal-action-ping-tooltip"))
        ]);

        AddActionGroup(root, Loc.GetString("luam-sector-terminal-group-reports"), [
            (Loc.GetString("luam-sector-terminal-action-print-report"), LuaMSectorTerminalAction.PrintLeadReport, Loc.GetString("luam-sector-terminal-action-print-report-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-print-route"), LuaMSectorTerminalAction.PrintRuntimeCoordinatePacket, Loc.GetString("luam-sector-terminal-action-print-route-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-print-closure"), LuaMSectorTerminalAction.PrintRuntimeClosureReport, Loc.GetString("luam-sector-terminal-action-print-closure-tooltip"))
        ]);

        AddActionGroup(root, Loc.GetString("luam-sector-terminal-group-paperwork"), [
            (Loc.GetString("luam-sector-terminal-action-print-insurance"), LuaMSectorTerminalAction.PrintInsuranceDocket, Loc.GetString("luam-sector-terminal-action-print-insurance-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-print-claim"), LuaMSectorTerminalAction.PrintInsuranceClaimVoucher, Loc.GetString("luam-sector-terminal-action-print-claim-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-print-registry"), LuaMSectorTerminalAction.PrintRegistryDocket, Loc.GetString("luam-sector-terminal-action-print-registry-tooltip")),
            (Loc.GetString("luam-sector-terminal-action-print-charter"), LuaMSectorTerminalAction.PrintCharterVoucher, Loc.GetString("luam-sector-terminal-action-print-charter-tooltip"))
        ]);

        var scroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
        };
        root.AddChild(scroll);

        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        scroll.AddChild(body);

        AddSection(body, Loc.GetString("luam-sector-terminal-section-quests"), _questTasks);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-automation"), _automation);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-map"), _sectorMap);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-preferred"), _preferredProcesses);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-insurance"), _insuranceCases);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-registry"), _registryRecords);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-leads"), _leads);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-conditions"), _conditions);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-reputation"), _reputation);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-locked"), _locked);
        AddSection(body, Loc.GetString("luam-sector-terminal-section-history"), _history);
    }

    public void UpdateState(LuaMSectorStatusUiState state)
    {
        _summary.Text = Loc.GetString("luam-sector-terminal-summary",
            ("stories", state.TotalStories),
            ("active", state.ActiveHazards),
            ("filed", state.AcknowledgedHazards),
            ("conditions", state.ActiveConditions),
            ("insurance", state.InsurancePayouts),
            ("blackbox", state.BlackBoxRecoveries),
            ("companies", state.CompanyRecords),
            ("ships", state.ShipRecords));
        _actionResult.Text = string.IsNullOrWhiteSpace(state.LastActionResult)
            ? Loc.GetString("luam-sector-terminal-last-action-ready")
            : Loc.GetString("luam-sector-terminal-last-action", ("result", state.LastActionResult));

        if (_actionButtons.TryGetValue(LuaMSectorTerminalAction.RequestDynamicEvent, out var requestButton))
        {
            requestButton.Disabled = !state.Automation.CanRequestDynamicEvent;
            requestButton.ToolTip = state.Automation.CanRequestDynamicEvent
                ? Loc.GetString("luam-sector-terminal-action-generate-tooltip")
                : state.Automation.RequestBlockReason;
        }
        if (_actionButtons.TryGetValue(LuaMSectorTerminalAction.PingActiveRouteMarker, out var pingButton))
        {
            pingButton.Disabled = !state.Automation.CanPingRoute;
            pingButton.ToolTip = state.Automation.CanPingRoute
                ? Loc.GetString("luam-sector-terminal-action-ping-tooltip")
                : state.Automation.RoutePingBlockReason;
        }

        _questTasks.RemoveAllChildren();
        if (state.QuestTasks.Length == 0)
        {
            AddMuted(_questTasks, Loc.GetString("luam-sector-terminal-no-quests"));
        }
        else
        {
            AddMuted(_questTasks, Loc.GetString("luam-sector-terminal-quests-hint"));
            for (var i = 0; i < state.QuestTasks.Length; i++)
                AddQuestTask(_questTasks, state.QuestTasks[i], i + 1);
        }

        _automation.RemoveAllChildren();
        AddRow(
            _automation,
            Loc.GetString("luam-sector-terminal-automation-state", ("state", state.Automation.State)),
            Loc.GetString("luam-sector-terminal-automation-next",
                ("next", state.Automation.NextAutomaticEvent),
                ("players", state.Automation.ActivePlayers)),
            state.Automation.HasOpenRuntimeLead
                ? Loc.GetString("luam-sector-terminal-open-runtime-lead", ("lead", state.Automation.OpenRuntimeLead))
                : Loc.GetString("luam-sector-terminal-open-runtime-none"));
        AddRow(
            _automation,
            Loc.GetString("luam-sector-terminal-dispatch-profile", ("tier", state.Automation.DispatchTier)),
            Loc.GetString("luam-sector-terminal-dispatch-score",
                ("score", state.Automation.DispatchReputationScore),
                ("reduction", state.Automation.DispatchCooldownReductionPercent)),
            Loc.GetString("luam-sector-terminal-dispatch-cooldown",
                ("min", FormatMinutes(state.Automation.DispatchCooldownMinSeconds)),
                ("max", FormatMinutes(state.Automation.DispatchCooldownMaxSeconds))));
        AddRow(
            _automation,
            state.Automation.CanPingRoute
                ? Loc.GetString("luam-sector-terminal-route-ping-ready")
                : Loc.GetString("luam-sector-terminal-route-ping-blocked"),
            string.IsNullOrWhiteSpace(state.Automation.ActiveRouteMarker)
                ? state.Automation.RoutePingBlockReason
                : Loc.GetString("luam-sector-terminal-route-marker",
                    ("marker", state.Automation.ActiveRouteMarker),
                    ("story", state.Automation.ActiveRouteStory)),
            BuildRouteCalibrationReserveText(state.Automation));
        AddRow(
            _automation,
            Loc.GetString("luam-sector-terminal-route-last"),
            state.Automation.RoutePingCount <= 0
                ? Loc.GetString("luam-sector-terminal-route-last-none")
                : state.Automation.LastRoutePing,
            string.Empty);
        AddMuted(_automation, Loc.GetString("luam-sector-terminal-route-closure-instruction"));
        AddRow(
            _automation,
            Loc.GetString("luam-sector-terminal-physical-hooks"),
            Loc.GetString("luam-sector-terminal-physical-hooks-entry",
                ("markers", state.Automation.ActiveMarkers),
                ("notes", state.Automation.SiteNotes),
                ("hazards", state.Automation.ConditionHazards),
                ("drift", state.Automation.SensorDriftMarkers)),
            string.Empty);
        AddRow(
            _automation,
            Loc.GetString("luam-sector-terminal-generator-templates"),
            state.Automation.TemplateIds.Length == 0
                ? Loc.GetString("luam-sector-terminal-generator-no-templates")
                : string.Join(", ", state.Automation.TemplateIds),
            string.Empty);

        _sectorMap.RemoveAllChildren();
        if (state.SectorMapNodes.Length == 0)
        {
            AddMuted(_sectorMap, Loc.GetString("luam-sector-terminal-no-sector-map"));
        }
        else
        {
            foreach (var node in state.SectorMapNodes)
                AddSectorMapNode(_sectorMap, node);
        }

        _preferredProcesses.RemoveAllChildren();
        if (state.PreferredProcesses.Length == 0)
        {
            AddMuted(_preferredProcesses, Loc.GetString("luam-sector-terminal-no-preferred"));
        }
        else
        {
            foreach (var process in state.PreferredProcesses)
                AddPreferredProcess(_preferredProcesses, process);
        }

        _insuranceCases.RemoveAllChildren();
        if (state.InsuranceCases.Length == 0)
        {
            AddMuted(_insuranceCases, Loc.GetString("luam-sector-terminal-no-insurance"));
        }
        else
        {
            foreach (var claim in state.InsuranceCases)
                AddInsuranceCase(_insuranceCases, claim);
        }

        _registryRecords.RemoveAllChildren();
        if (state.RegistryRecords.Length == 0)
        {
            AddMuted(_registryRecords, Loc.GetString("luam-sector-terminal-no-registry"));
        }
        else
        {
            foreach (var record in state.RegistryRecords)
                AddRegistryRecord(_registryRecords, record);
        }

        _leads.RemoveAllChildren();
        if (state.Hazards.Length == 0)
        {
            AddMuted(_leads, Loc.GetString("luam-sector-terminal-no-leads"));
        }
        else
        {
            foreach (var hazard in state.Hazards)
            {
                var status = hazard.Resolved
                    ? Loc.GetString("luam-sector-status-state-resolved")
                    : hazard.Acknowledged
                        ? Loc.GetString("luam-sector-status-state-filed")
                        : Loc.GetString("luam-sector-status-state-active");
                AddRow(
                    _leads,
                    Loc.GetString("luam-sector-terminal-lead-title",
                        ("severity", hazard.Severity),
                        ("title", hazard.Title),
                        ("state", status),
                        ("bonus", hazard.RewardBonus)),
                    hazard.Hazard,
                    hazard.Description);
            }
        }

        _conditions.RemoveAllChildren();
        if (state.Conditions.Length == 0)
        {
            AddMuted(_conditions, Loc.GetString("luam-sector-terminal-no-conditions"));
        }
        else
        {
            foreach (var condition in state.Conditions)
            {
                AddRow(
                    _conditions,
                    Loc.GetString("luam-sector-terminal-condition-title",
                        ("severity", condition.Severity),
                        ("title", condition.Title),
                        ("id", condition.ConditionId)),
                    condition.Summary,
                    Loc.GetString("luam-sector-terminal-actor", ("actor", condition.Actor)),
                    condition.ConditionId);
            }
        }

        _reputation.RemoveAllChildren();
        if (state.Reputation.Length == 0)
        {
            AddMuted(_reputation, Loc.GetString("luam-sector-terminal-no-reputation"));
        }
        else
        {
            foreach (var entry in state.Reputation)
            {
                AddRow(
                    _reputation,
                    Loc.GetString("luam-sector-terminal-reputation-entry",
                        ("target", entry.Target),
                        ("value", entry.Value),
                        ("tier", entry.Tier)),
                    Loc.GetString("luam-sector-terminal-reputation-bonus", ("bonus", entry.RewardBonus)),
                    string.Empty);
            }
        }

        _locked.RemoveAllChildren();
        if (state.LockedLeads.Length == 0)
        {
            AddMuted(_locked, Loc.GetString("luam-sector-terminal-no-locked"));
        }
        else
        {
            foreach (var lead in state.LockedLeads)
            {
                AddRow(
                    _locked,
                    lead.Title,
                    Loc.GetString("luam-sector-terminal-locked-requirement",
                        ("target", lead.RequiredTarget),
                        ("current", lead.CurrentValue),
                        ("required", lead.RequiredValue)),
                    string.Empty);
            }
        }

        _history.RemoveAllChildren();
        if (state.RecentHistory.Length == 0)
        {
            AddMuted(_history, Loc.GetString("luam-sector-terminal-no-history"));
        }
        else
        {
            foreach (var entry in state.RecentHistory)
            {
                AddRow(
                    _history,
                    Loc.GetString("luam-sector-terminal-history-title",
                        ("category", entry.Category),
                        ("title", entry.Title)),
                    entry.Summary,
                    Loc.GetString("luam-sector-terminal-actor", ("actor", entry.Actor)));
            }
        }
    }

    private void AddActionGroup(
        BoxContainer root,
        string title,
        (string Text, LuaMSectorTerminalAction Action, string ToolTip)[] actions)
    {
        root.AddChild(new Label
        {
            Text = title,
            StyleClasses = { "LabelSubText" },
            Margin = new Thickness(0, 4, 0, 2),
        });

        var grid = new GridContainer
        {
            Columns = 2,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 4),
        };

        foreach (var action in actions)
            AddAction(grid, action.Text, action.Action, action.ToolTip);

        root.AddChild(grid);
    }

    private Button AddAction(GridContainer actions, string text, LuaMSectorTerminalAction action, string toolTip)
    {
        var button = new Button
        {
            Text = text,
            HorizontalExpand = true,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 4, 4),
            ToolTip = toolTip,
        };

        button.OnPressed += _ => ActionRequested?.Invoke(action, string.Empty, string.Empty);
        _actionButtons[action] = button;
        actions.AddChild(button);
        return button;
    }

    private static void AddSection(BoxContainer body, string title, BoxContainer list)
    {
        body.AddChild(new Label
        {
            Text = title,
            StyleClasses = { "LabelSubText" },
            Margin = new Thickness(0, 8, 0, 4),
        });
        body.AddChild(list);
    }

    private static BoxContainer MakeList()
    {
        return new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };
    }

    private static int FormatMinutes(int seconds)
    {
        return (int) Math.Ceiling(seconds / 60f);
    }

    private static string BuildRouteCalibrationReserveText(LuaMSectorAutomationUiEntry automation)
    {
        var text = Loc.GetString("luam-sector-terminal-route-calibration-reserve",
            ("credits", automation.RouteCalibrationCredits));

        if (!string.IsNullOrWhiteSpace(automation.RouteCalibrationSource))
        {
            text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-source",
                ("source", automation.RouteCalibrationSource))}";

            if (automation.RouteCalibrationSourceChainDepth > 0)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-chain-depth",
                    ("depth", automation.RouteCalibrationSourceChainDepth))}";
            }

            if (automation.RouteCalibrationRewardBonus > 0)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-reward-bonus",
                    ("bonus", automation.RouteCalibrationRewardBonus))}";
            }

            if (automation.RouteCalibrationClosureRewardBonus > 0)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-closure-bonus",
                    ("bonus", automation.RouteCalibrationClosureRewardBonus))}";
            }

            if (automation.RouteCalibrationRadiationDampingPreview > 0)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-radiation-damping",
                    ("damping", automation.RouteCalibrationRadiationDampingPreview))}";
            }

            if (automation.RouteCalibrationSensorDriftSuppressionPreview)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-sensor-drift-suppressed")}";
            }

            if (automation.RouteCalibrationSources is { Length: > 1 } sources)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-queue",
                    ("sources", string.Join(" -> ", sources, 1, sources.Length - 1)))}";
            }
        }

        if (automation.RouteCalibrationHandoffReady &&
            !string.IsNullOrWhiteSpace(automation.RouteCalibrationHandoffSource))
        {
            text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-handoff",
                ("source", automation.RouteCalibrationHandoffSource))}";
        }

        if (!string.IsNullOrWhiteSpace(automation.ActiveRouteCalibrationSource))
        {
            text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-active-source",
                ("source", automation.ActiveRouteCalibrationSource))}";
            if (automation.ActiveRouteCalibrationChainDepth > 0)
            {
                text += $" | {Loc.GetString("luam-sector-terminal-route-calibration-active-chain-depth",
                    ("depth", automation.ActiveRouteCalibrationChainDepth))}";
            }
        }

        return text;
    }

    private static void AddMuted(BoxContainer list, string text)
    {
        list.AddChild(new Label
        {
            Text = text,
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
    }

    private void AddRow(BoxContainer list, string title, string lineOne, string lineTwo, string copyId = "")
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };

        if (string.IsNullOrWhiteSpace(copyId))
        {
            row.AddChild(new Label
            {
                Text = title,
                ClipText = false,
            });
        }
        else
        {
            var titleRow = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                HorizontalExpand = true,
            };
            titleRow.AddChild(new Label
            {
                Text = title,
                ClipText = false,
            });
            titleRow.AddChild(MakeCopyIdButton(copyId));
            row.AddChild(titleRow);
        }

        if (!string.IsNullOrWhiteSpace(lineOne))
        {
            row.AddChild(new Label
            {
                Text = lineOne,
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }

        if (!string.IsNullOrWhiteSpace(lineTwo))
        {
            row.AddChild(new Label
            {
                Text = lineTwo,
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }

        list.AddChild(row);
    }

    private void AddQuestTask(BoxContainer list, LuaMSectorQuestTaskUiEntry task, int number)
    {
        var location = string.IsNullOrWhiteSpace(task.Location)
            ? Loc.GetString("luam-sector-terminal-quest-location-unknown")
            : task.Location;
        var turnIn = string.IsNullOrWhiteSpace(task.TurnIn)
            ? Loc.GetString("luam-sector-terminal-quest-turnin-unknown")
            : task.TurnIn;
        var reward = string.IsNullOrWhiteSpace(task.Reward)
            ? Loc.GetString("luam-sector-terminal-quest-reward-unknown")
            : task.Reward;

        var panel = new PanelContainer
        {
            StyleClasses = { "AngleRect" },
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(6, 5),
        };
        panel.AddChild(row);

        var titleRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-quest-header",
                ("number", number),
                ("status", task.Status),
                ("title", task.Title)),
            HorizontalExpand = true,
            ClipText = false,
        });
        if (!string.IsNullOrWhiteSpace(task.TaskId))
            titleRow.AddChild(MakeCopyIdButton(task.TaskId));
        row.AddChild(titleRow);

        row.AddChild(new PanelContainer
        {
            StyleClasses = { "HighDivider" },
            HorizontalExpand = true,
            Margin = new Thickness(0, 4, 0, 4),
        });

        AddQuestLine(row, Loc.GetString("luam-sector-terminal-quest-step-location", ("location", location)));
        AddQuestLine(row, Loc.GetString("luam-sector-terminal-quest-step-action", ("objective", task.Objective)));
        AddQuestLine(row, Loc.GetString("luam-sector-terminal-quest-step-finish", ("turnin", turnIn)));
        AddQuestLine(row, Loc.GetString("luam-sector-terminal-quest-step-reward", ("reward", reward)));

        list.AddChild(panel);
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

    private void AddInsuranceCase(BoxContainer list, LuaMSectorInsuranceUiEntry claim)
    {
        var row = MakeRow();
        var titleRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(new Label
        {
            Text = $"{claim.Title} [{claim.StoryId}]",
            ClipText = false,
        });
        titleRow.AddChild(MakeCopyIdButton(claim.StoryId));
        row.AddChild(titleRow);
        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-insurance-summary",
                ("state", claim.State),
                ("vessel", claim.Vessel),
                ("requested", claim.RequestedAmount),
                ("paid", claim.PaidAmount)),
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
        row.AddChild(new Label
        {
            Text = claim.ServiceLine,
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
        if (!string.IsNullOrWhiteSpace(claim.Policy))
        {
            row.AddChild(new Label
            {
                Text = claim.Policy,
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }

        var button = new Button
        {
            Text = claim.Claimed
                ? Loc.GetString("luam-sector-terminal-claim-filed")
                : Loc.GetString("luam-sector-terminal-print-claim"),
            Disabled = claim.Claimed,
            HorizontalExpand = true,
            MinHeight = 30,
            ToolTip = claim.Claimed
                ? Loc.GetString("luam-sector-terminal-claim-filed-tooltip")
                : Loc.GetString("luam-sector-terminal-print-claim-tooltip"),
        };
        button.OnPressed += _ => ActionRequested?.Invoke(LuaMSectorTerminalAction.PrintInsuranceClaimVoucher, claim.StoryId, string.Empty);
        row.AddChild(button);
        list.AddChild(row);
    }

    private void AddSectorMapNode(BoxContainer list, LuaMSectorMapNodeUiEntry node)
    {
        var row = MakeRow();
        var titleRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-sector-map-title",
                ("kind", node.Kind),
                ("title", node.Title),
                ("state", node.State)),
            ClipText = false,
        });
        titleRow.AddChild(MakeCopyIdButton(node.TemplateId));
        titleRow.AddChild(MakeCopyIdButton(node.StoryId));
        row.AddChild(titleRow);
        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-sector-map-entry",
                ("location", node.Location),
                ("template", node.TemplateId),
                ("story", node.StoryId)),
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });

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
                Text = Loc.GetString("luam-sector-terminal-sector-map-risk", ("risk", node.Risk)),
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }

        list.AddChild(row);
    }

    private void AddPreferredProcess(BoxContainer list, LuaMSectorPreferredProcessUiEntry process)
    {
        var row = MakeRow();
        var titleRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(new Label
        {
            Text = $"{process.Title} [{process.TemplateId}]",
            ClipText = false,
        });
        titleRow.AddChild(MakeCopyIdButton(process.TemplateId));
        row.AddChild(titleRow);
        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-preferred-entry",
                ("target", process.ReputationTarget),
                ("current", process.CurrentReputation),
                ("required", process.RequiredReputation),
                ("tier", process.Tier),
                ("reward", process.BaseReward),
                ("bonus", process.ReputationBonus)),
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-preferred-detail",
                ("vessel", process.Vessel),
                ("description", process.Description)),
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });

        var button = new Button
        {
            Text = process.CanRequestNow
                ? Loc.GetString("luam-sector-terminal-preferred-request")
                : process.Unlocked
                    ? Loc.GetString("luam-sector-terminal-preferred-unavailable")
                    : Loc.GetString("luam-sector-terminal-preferred-needs-reputation", ("target", process.ReputationTarget)),
            Disabled = !process.CanRequestNow,
            HorizontalExpand = true,
            MinHeight = 30,
            ToolTip = process.CanRequestNow
                ? Loc.GetString("luam-sector-terminal-preferred-request-tooltip")
                : process.BlockReason,
        };
        button.OnPressed += _ => ActionRequested?.Invoke(LuaMSectorTerminalAction.RequestDynamicEvent, string.Empty, process.TemplateId);
        row.AddChild(button);
        list.AddChild(row);
    }

    private void AddRegistryRecord(BoxContainer list, LuaMSectorRegistryUiEntry record)
    {
        var row = MakeRow();
        var titleRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        titleRow.AddChild(new Label
        {
            Text = $"{record.Title} [{record.StoryId}]",
            ClipText = false,
        });
        titleRow.AddChild(MakeCopyIdButton(record.StoryId));
        row.AddChild(titleRow);
        row.AddChild(new Label
        {
            Text = Loc.GetString("luam-sector-terminal-registry-summary",
                ("company", record.CompanyState),
                ("ship", record.ShipState),
                ("vessel", record.Vessel)),
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
        row.AddChild(new Label
        {
            Text = record.ServiceLine,
            StyleClasses = { "LabelSubText" },
            ClipText = false,
        });
        if (!string.IsNullOrWhiteSpace(record.CompanyRecord))
        {
            row.AddChild(new Label
            {
                Text = Loc.GetString("luam-sector-terminal-company-record", ("record", record.CompanyRecord)),
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }
        if (!string.IsNullOrWhiteSpace(record.ShipRecord))
        {
            row.AddChild(new Label
            {
                Text = Loc.GetString("luam-sector-terminal-ship-record", ("record", record.ShipRecord)),
                StyleClasses = { "LabelSubText" },
                ClipText = false,
            });
        }

        var canPrint = record.CanRegisterCompany || record.CanRegisterShip;
        var button = new Button
        {
            Text = canPrint
                ? Loc.GetString("luam-sector-terminal-print-charter")
                : Loc.GetString("luam-sector-terminal-registry-filed"),
            Disabled = !canPrint,
            HorizontalExpand = true,
            MinHeight = 30,
            ToolTip = canPrint
                ? Loc.GetString("luam-sector-terminal-print-charter-tooltip")
                : Loc.GetString("luam-sector-terminal-registry-filed-tooltip"),
        };
        button.OnPressed += _ => ActionRequested?.Invoke(LuaMSectorTerminalAction.PrintCharterVoucher, record.StoryId, string.Empty);
        row.AddChild(button);
        list.AddChild(row);
    }

    private static BoxContainer MakeRow()
    {
        return new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 8),
        };
    }
}
