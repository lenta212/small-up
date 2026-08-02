using System.Numerics;
using System.Linq;
using Content.Client.UserInterface.Controls;
using Content.Shared._LuaM.Rescue;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._LuaM.Rescue.UI;

public sealed class LuaMRescueDispatchConsoleWindow : FancyWindow
{
    public event Action? RefreshRequested;
    public event Action<LuaMRescueDispatchConsoleAction, NetEntity?, NetEntity?>? ActionRequested;

    private readonly Label _summary = new();
    private readonly Label _actionResult = new();
    private readonly BoxContainer _agents = MakeList();
    private readonly BoxContainer _dispatches = MakeList();

    public LuaMRescueDispatchConsoleWindow()
    {
        Title = Loc.GetString("luam-rescue-dispatch-console-title");
        Resizable = true;
        MinSize = new Vector2(600, 420);
        SetSize = new Vector2(760, 620);

        var root = MakeList();
        root.VerticalExpand = true;
        root.Margin = new Thickness(8);
        ContentsContainer.AddChild(root);

        var refresh = new Button { Text = Loc.GetString("luam-rescue-dispatch-console-refresh") };
        refresh.OnPressed += _ => RefreshRequested?.Invoke();
        root.AddChild(refresh);
        _summary.Margin = new Thickness(0, 8);
        root.AddChild(_summary);
        _actionResult.StyleClasses.Add("LabelSubText");
        root.AddChild(_actionResult);

        var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        var body = MakeList();
        scroll.AddChild(body);
        root.AddChild(scroll);
        AddSection(body, "luam-rescue-dispatch-console-agents", _agents);
        AddSection(body, "luam-rescue-dispatch-console-queue", _dispatches);
    }

    public void UpdateState(LuaMRescueDispatchConsoleState state)
    {
        _summary.Text = Loc.GetString("luam-rescue-dispatch-console-summary",
            ("active", state.ActiveMissions), ("pending", state.PendingMissions),
            ("terminal", state.TerminalMissions), ("operational", state.OperationalAgents),
            ("incapacitated", state.IncapacitatedAgents));
        _actionResult.Text = state.ActionResult;
        _agents.RemoveAllChildren();
        foreach (var agent in state.Agents)
        {
            var row = MakeList();
            row.AddChild(new Label { Text = Loc.GetString("luam-rescue-dispatch-console-agent-row",
                ("name", agent.AgentName), ("state", agent.MobState),
                ("activity", agent.Activity), ("patient", string.IsNullOrEmpty(agent.PatientName) ? "—" : agent.PatientName),
                ("status", agent.LastStatus)) });
            row.AddChild(new Label { Text = Loc.GetString("luam-rescue-dispatch-console-supply-row",
                ("total", agent.MedicalUnits), ("effective", agent.EffectiveMedicalUnits),
                ("state", agent.Resupplying
                    ? Loc.GetString("luam-rescue-dispatch-console-resupplying")
                    : Loc.GetString("luam-rescue-dispatch-console-supply-ready")),
                ("status", agent.SupplyStatus)), StyleClasses = { "LabelSubText" } });
            row.AddChild(new Label { Text = Loc.GetString("luam-rescue-dispatch-console-life-support-row",
                ("state", agent.LifeSupportEmergency
                    ? Loc.GetString("luam-rescue-dispatch-console-internals-emergency")
                    : agent.InternalsActive
                        ? Loc.GetString("luam-rescue-dispatch-console-internals-active")
                        : Loc.GetString("luam-rescue-dispatch-console-internals-offline")),
                ("pressure", agent.OxygenPressureKpa),
                ("reserveCount", agent.ReserveTankCount),
                ("reservePressure", agent.BestReservePressureKpa),
                ("swapCount", agent.AutomaticSwapCount),
                ("emergencyCount", agent.LifeSupportEmergencyCount),
                ("status", agent.LifeSupportStatus)), StyleClasses = { "LabelSubText" } });
            if (agent.Patient != null)
            {
                var release = new Button { Text = Loc.GetString("luam-rescue-dispatch-console-release") };
                release.OnPressed += _ => ActionRequested?.Invoke(
                    LuaMRescueDispatchConsoleAction.Release, null, agent.Agent);
                row.AddChild(release);
            }
            _agents.AddChild(row);
        }
        AddEmpty(_agents, state.Agents.Count);

        _dispatches.RemoveAllChildren();
        foreach (var entry in state.Dispatches)
        {
            var row = MakeList();
            row.AddChild(new Label { Text = Loc.GetString("luam-rescue-dispatch-console-dispatch-row",
                ("target", entry.TargetName), ("kind", entry.Kind), ("age", entry.AgeSeconds),
                ("attempts", entry.Attempts), ("status", entry.LastStatus)) });
            var actions = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
            if (entry.Terminal)
            {
                var retry = new Button { Text = Loc.GetString("luam-rescue-dispatch-console-retry") };
                retry.OnPressed += _ => ActionRequested?.Invoke(
                    LuaMRescueDispatchConsoleAction.Retry, entry.Target, null);
                actions.AddChild(retry);
            }
            foreach (var agent in state.Agents.Where(agent => agent.Operational && agent.Patient == null))
            {
                var assign = new Button { Text = Loc.GetString("luam-rescue-dispatch-console-assign", ("agent", agent.AgentName)) };
                assign.OnPressed += _ => ActionRequested?.Invoke(
                    LuaMRescueDispatchConsoleAction.Assign, entry.Target, agent.Agent);
                actions.AddChild(assign);
            }
            row.AddChild(actions);
            _dispatches.AddChild(row);
        }
        AddEmpty(_dispatches, state.Dispatches.Count);
    }

    private static BoxContainer MakeList() => new() { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };

    private static void AddSection(Control parent, string title, Control list)
    {
        parent.AddChild(new Label { Text = Loc.GetString(title), StyleClasses = { "LabelHeading" }, Margin = new Thickness(0, 8, 0, 4) });
        parent.AddChild(list);
    }

    private static void AddEmpty(Control list, int count)
    {
        if (count == 0)
            list.AddChild(new Label { Text = Loc.GetString("luam-rescue-dispatch-console-empty") });
    }
}
