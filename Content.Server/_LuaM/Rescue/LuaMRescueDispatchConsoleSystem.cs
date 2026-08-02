using Content.Shared._LuaM.Rescue;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Access.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueDispatchConsoleSystem : EntitySystem
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    [Dependency] private LuaMRescueShuttleSystem _shuttle = default!;
    [Dependency] private LuaMRescueAgentSystem _agent = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IGameTiming _timing = default!;

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMRescueDispatchConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        Subs.BuiEvents<LuaMRescueDispatchConsoleComponent>(LuaMRescueDispatchConsoleUiKey.Key,
            subscriptions =>
            {
                subscriptions.Event<LuaMRescueDispatchRefreshMessage>(OnRefresh);
                subscriptions.Event<LuaMRescueDispatchActionMessage>(OnAction);
            });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextRefresh)
            return;

        _nextRefresh = _timing.CurTime + RefreshInterval;
        var query = EntityQueryEnumerator<LuaMRescueDispatchConsoleComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_ui.IsUiOpen(uid, LuaMRescueDispatchConsoleUiKey.Key))
                UpdateUi(uid);
        }
    }

    private void OnUiOpened(
        Entity<LuaMRescueDispatchConsoleComponent> console,
        ref BoundUIOpenedEvent args)
    {
        UpdateUi(console.Owner);
    }

    private void OnRefresh(
        Entity<LuaMRescueDispatchConsoleComponent> console,
        ref LuaMRescueDispatchRefreshMessage args)
    {
        UpdateUi(console.Owner);
    }

    private void OnAction(
        Entity<LuaMRescueDispatchConsoleComponent> console,
        ref LuaMRescueDispatchActionMessage args)
    {
        if (!_access.IsAllowed(args.Actor, console.Owner))
        {
            console.Comp.LastActionResult = "access denied";
            UpdateUi(console.Owner);
            return;
        }

        EntityUid? target = args.Target is { } netTarget ? GetEntity(netTarget) : null;
        EntityUid? agent = args.Agent is { } netAgent ? GetEntity(netAgent) : null;
        bool accepted;
        string status;
        switch (args.Action)
        {
            case LuaMRescueDispatchConsoleAction.Retry when target is { Valid: true } retryTarget:
                accepted = _shuttle.TryRetryTerminalAutomaticDispatch(retryTarget, out status);
                if (accepted)
                    _shuttle.ProcessPendingAutomaticDispatchesNow();
                break;
            case LuaMRescueDispatchConsoleAction.Assign
                when target is { Valid: true } assignTarget && agent is { Valid: true } assignAgent:
                accepted = _shuttle.TryAssignQueuedDispatch(assignTarget, assignAgent, out status);
                break;
            case LuaMRescueDispatchConsoleAction.Release when agent is { Valid: true } releaseAgent:
                accepted = _agent.TryOrderAgent(releaseAgent, null, out status);
                break;
            default:
                accepted = false;
                status = "invalid dispatch console action";
                break;
        }

        console.Comp.LastActionResult = $"{(accepted ? "accepted" : "rejected")}: {status}";
        Dirty(console);
        UpdateUi(console.Owner);
    }

    public LuaMRescueDispatchConsoleState BuildState(string actionResult = "")
    {
        var now = _timing.CurTime;
        var dispatches = new List<LuaMRescueDispatchConsoleEntry>();
        foreach (var pending in _shuttle.GetPendingAutomaticDispatches())
        {
            dispatches.Add(new LuaMRescueDispatchConsoleEntry(
                GetNetEntity(pending.Target),
                SafeName(pending.Target),
                pending.Kind.ToString(),
                false,
                pending.RequiredOnboardHandoff,
                pending.Attempts,
                Math.Max(0, (int) (now - pending.EnqueuedAt).TotalSeconds),
                pending.LastStatus));
        }

        foreach (var terminal in _shuttle.GetTerminalAutomaticDispatches())
        {
            dispatches.Add(new LuaMRescueDispatchConsoleEntry(
                GetNetEntity(terminal.Target),
                SafeName(terminal.Target),
                terminal.Kind.ToString(),
                true,
                terminal.RequiredOnboardHandoff,
                terminal.Attempts,
                Math.Max(0, (int) (now - terminal.EnqueuedAt).TotalSeconds),
                terminal.LastStatus));
        }

        var agents = new List<LuaMRescueDispatchAgentEntry>();
        var activeMissions = 0;
        var operationalAgents = 0;
        var incapacitatedAgents = 0;
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescue))
        {
            if (Deleted(uid))
                continue;

            var mobState = TryComp<MobStateComponent>(uid, out var mob)
                ? mob.CurrentState
                : MobState.Invalid;
            var operational = mobState is not (MobState.Dead or MobState.Critical);
            if (operational)
                operationalAgents++;
            else
                incapacitatedAgents++;

            var patient = ResolvePatient(rescue);
            var supplies = _agent.GetMedicalSupplySnapshot(uid, patient);
            var lifeSupport = _agent.GetLifeSupportSnapshot(uid);
            var resupplying = rescue.ActivityContext.Activity == LuaMRescueActivity.Resupplying ||
                              rescue.TaskStage is LuaMRescueTaskStage.PickingUpSupply or LuaMRescueTaskStage.VendingSupply ||
                              rescue.TaskSupplyTarget is { Valid: true };
            if (patient is { Valid: true })
                activeMissions++;
            agents.Add(new LuaMRescueDispatchAgentEntry(
                GetNetEntity(uid),
                SafeName(uid),
                mobState,
                operational,
                patient is { Valid: true } patientUid ? GetNetEntity(patientUid) : null,
                patient is { Valid: true } namedPatient ? SafeName(namedPatient) : string.Empty,
                rescue.ActivityContext.Activity.ToString(),
                BuildAgentStatus(rescue, patient),
                supplies.MedicalUnits,
                supplies.EffectiveMedicalUnits,
                resupplying,
                BuildSupplyStatus(rescue, supplies.Status),
                lifeSupport.Active,
                lifeSupport.Emergency,
                lifeSupport.PressureKpa,
                lifeSupport.ReserveTankCount,
                lifeSupport.BestReservePressureKpa,
                lifeSupport.AutomaticSwapCount,
                lifeSupport.EmergencyCount,
                lifeSupport.Status));
        }

        agents.Sort((left, right) => string.Compare(left.AgentName, right.AgentName, StringComparison.Ordinal));
        return new LuaMRescueDispatchConsoleState(
            dispatches,
            agents,
            activeMissions,
            _shuttle.PendingAutomaticDispatchCount,
            _shuttle.GetTerminalAutomaticDispatches().Count,
            operationalAgents,
            incapacitatedAgents,
            actionResult);
    }

    private void UpdateUi(EntityUid console)
    {
        var result = Comp<LuaMRescueDispatchConsoleComponent>(console).LastActionResult;
        _ui.SetUiState(console, LuaMRescueDispatchConsoleUiKey.Key, BuildState(result));
    }

    private EntityUid? ResolvePatient(LuaMRescueAgentComponent rescue)
    {
        if (rescue.LifeSupportEmergencyActive)
        {
            var emergencyPatient = rescue.LifeSupportEmergencyPatient;
            return emergencyPatient is { Valid: true } emergencyUid && !Deleted(emergencyUid)
                ? emergencyUid
                : null;
        }

        var target = rescue.ActivityContext.Target ??
                     rescue.EvacuatingTarget ??
                     rescue.OnboardCareTarget ??
                     rescue.TaskPatientTarget ??
                     rescue.AssignedTarget ??
                     rescue.ManualOverrideTarget ??
                     rescue.DeathSignalTarget;
        return target is { Valid: true } uid && !Deleted(uid) ? uid : null;
    }

    private static string BuildAgentStatus(LuaMRescueAgentComponent rescue, EntityUid? patient)
    {
        if (rescue.LifeSupportEmergencyActive)
        {
            return $"life-support emergency: {rescue.LastLifeSupportStatus}; " +
                   $"return={rescue.LastShuttleReturnStatus}";
        }

        if (patient == null)
            return string.IsNullOrWhiteSpace(rescue.LastAutoEvacuationStatus)
                ? "standby"
                : rescue.LastAutoEvacuationStatus;
        if (rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.None)
            return $"{rescue.ActivityContext.TerminalStatus}: {rescue.ActivityContext.FailureReason}";
        return string.IsNullOrWhiteSpace(rescue.LastAutoEvacuationStatus)
            ? "mission active"
            : rescue.LastAutoEvacuationStatus;
    }

    private static string BuildSupplyStatus(LuaMRescueAgentComponent rescue, string inventoryStatus)
    {
        if (!string.IsNullOrWhiteSpace(rescue.LastAutoSupplyStatus) &&
            !rescue.LastAutoSupplyStatus.Equals("none", StringComparison.OrdinalIgnoreCase))
            return $"{inventoryStatus}; {rescue.LastAutoSupplyStatus}";
        return inventoryStatus;
    }

    private string SafeName(EntityUid uid)
    {
        return Deleted(uid) ? $"#{uid.Id}" : Name(uid);
    }
}
