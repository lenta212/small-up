using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Administration;
using Content.Server.Bed.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Administration;
using Content.Shared.Buckle.Components;
using Content.Shared.Mobs;
using Content.Shared.Radio;
using Content.Shared.Station.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueShuttleSystem : EntitySystem
{
    public const string DefaultVessel = "Triage";
    private const double AutomaticDeathSignalCooldownSeconds = 180;
    private const double AutomaticCriticalSignalCooldownSeconds = 300;
    private static readonly ProtoId<RadioChannelPrototype> MedicalRadioChannel = "Medical";

    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly LuaMRescueAgentSystem _rescueAgent = default!;
    [Dependency] private readonly LuaMRescueTeamSystem _rescueTeam = default!;
    [Dependency] private readonly LuaMSectorStorySystem _sectorStory = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, TimeSpan> _automaticDeathSignalCooldowns = new();
    private readonly Dictionary<EntityUid, TimeSpan> _automaticCriticalSignalCooldowns = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (ev.OldMobState != MobState.Dead &&
            ev.NewMobState == MobState.Dead)
        {
            TryDispatchAutomaticDeathSignal(ev.Target);
            return;
        }

        if (ev.OldMobState is MobState.Critical or MobState.Dead ||
            ev.NewMobState != MobState.Critical)
        {
            return;
        }

        TryDispatchAutomaticCriticalSignal(ev.Target);
    }

    private bool TryDispatchAutomaticDeathSignal(EntityUid target)
    {
        if (Deleted(target) ||
            !HasComp<ActorComponent>(target) ||
            HasComp<LuaMRescueAgentComponent>(target) ||
            HasComp<LuaMRescueEscortComponent>(target))
        {
            return false;
        }

        PruneAutomaticDeathSignalCooldowns();

        var now = _timing.CurTime;
        if (_automaticDeathSignalCooldowns.TryGetValue(target, out var until) &&
            until > now)
        {
            return false;
        }

        _automaticDeathSignalCooldowns[target] = now + TimeSpan.FromSeconds(AutomaticDeathSignalCooldownSeconds);

        if (TryFindActiveRescueForTarget(target, out var activeAgent, out var activeRescue))
        {
            activeRescue.DeathSignalTarget = target;
            activeRescue.DeathSignalDispatchReported = false;
            SendDispatchRadio(activeAgent, target);
            MarkDeathSignalDispatchReported(activeAgent, activeRescue, target);
            return true;
        }

        if (!_prototypes.TryIndex<VesselPrototype>(DefaultVessel, out var vessel) ||
            !TryResolveAutomaticDeathSignalStation(target, out var station))
        {
            return false;
        }

        return TryDispatchRescueShuttle(
            station,
            vessel,
            target,
            controller: null,
            spawnAgent: true,
            spawnTeam: true,
            control: false,
            routeToTarget: true,
            deathSignal: true,
            out _,
            out _,
            out _,
            out _,
            out _);
    }

    private bool TryDispatchAutomaticCriticalSignal(EntityUid target)
    {
        if (Deleted(target) ||
            !HasComp<ActorComponent>(target) ||
            HasComp<LuaMRescueAgentComponent>(target) ||
            HasComp<LuaMRescueEscortComponent>(target) ||
            TryFindActiveRescueForTarget(target, out _, out _))
        {
            return false;
        }

        PruneAutomaticCriticalSignalCooldowns();

        var now = _timing.CurTime;
        if (_automaticCriticalSignalCooldowns.TryGetValue(target, out var until) &&
            until > now)
        {
            return false;
        }

        if (!_prototypes.TryIndex<VesselPrototype>(DefaultVessel, out var vessel) ||
            !TryResolveAutomaticCriticalSignalStation(target, out var station) ||
            _sectorStory.TryGetActiveRescueCooldown(out _, out _))
        {
            return false;
        }

        var dispatched = TryDispatchRescueShuttle(
            station,
            vessel,
            target,
            controller: null,
            spawnAgent: true,
            spawnTeam: true,
            control: false,
            routeToTarget: true,
            deathSignal: false,
            out _,
            out var agent,
            out _,
            out _,
            out _);

        if (!dispatched)
            return false;

        _automaticCriticalSignalCooldowns[target] = now + TimeSpan.FromSeconds(AutomaticCriticalSignalCooldownSeconds);

        if (agent is { Valid: true } agentUid &&
            !Deleted(agentUid))
        {
            SendCriticalDispatchRadio(agentUid, target);
            MarkCriticalSignalDispatchReported(agentUid, target);
        }

        return true;
    }

    private void PruneAutomaticDeathSignalCooldowns()
    {
        PruneAutomaticSignalCooldowns(_automaticDeathSignalCooldowns);
    }

    private void PruneAutomaticCriticalSignalCooldowns()
    {
        PruneAutomaticSignalCooldowns(_automaticCriticalSignalCooldowns);
    }

    private void PruneAutomaticSignalCooldowns(Dictionary<EntityUid, TimeSpan> cooldowns)
    {
        if (cooldowns.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in cooldowns
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            cooldowns.Remove(target);
        }
    }

    private bool TryResolveAutomaticDeathSignalStation(EntityUid target, out EntityUid station)
    {
        return TryResolveAutomaticMedicalSignalStation(target, out station);
    }

    private bool TryResolveAutomaticCriticalSignalStation(EntityUid target, out EntityUid station)
    {
        return TryResolveAutomaticMedicalSignalStation(target, out station);
    }

    private bool TryResolveAutomaticMedicalSignalStation(EntityUid target, out EntityUid station)
    {
        if (_station.GetOwningStation(target) is { Valid: true } owningStation)
        {
            station = owningStation;
            return true;
        }

        var stations = _station.GetStationsSet();
        if (stations.Count == 1)
        {
            station = stations.First();
            return true;
        }

        station = default;
        return false;
    }

    private bool TryFindActiveRescueForTarget(
        EntityUid target,
        out EntityUid agent,
        out LuaMRescueAgentComponent rescue)
    {
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescueComp))
        {
            if (Deleted(uid))
                continue;

            if (rescueComp.AssignedTarget == target ||
                rescueComp.DeathSignalTarget == target ||
                rescueComp.EvacuatingTarget == target ||
                rescueComp.OnboardCareTarget == target ||
                rescueComp.TaskPatientTarget == target)
            {
                agent = uid;
                rescue = rescueComp;
                return true;
            }
        }

        agent = default;
        rescue = default!;
        return false;
    }

    public bool TryDispatchRescueShuttle(
        EntityUid station,
        VesselPrototype vessel,
        EntityUid? followTarget,
        ICommonSession? controller,
        bool spawnAgent,
        bool spawnTeam,
        bool control,
        bool routeToTarget,
        bool deathSignal,
        out EntityUid? shuttle,
        out EntityUid? agent,
        out EntityUid? autopilotConsole,
        out int escortCount,
        out string status)
    {
        shuttle = null;
        agent = null;
        autopilotConsole = null;
        escortCount = 0;
        status = string.Empty;

        if (deathSignal)
        {
            if (!spawnAgent)
            {
                status = "Death signal dispatch requires a rescue agent so Aibolit can report who it is flying to.";
                return false;
            }

            if (followTarget is not { Valid: true } reportTarget ||
                Deleted(reportTarget))
            {
                status = "Death signal dispatch requires target=<entity|player> so Aibolit can report who it is flying to.";
                return false;
            }
        }

        if (!HasComp<StationDataComponent>(station))
        {
            status = "Target is not a station.";
            return false;
        }

        if (spawnAgent &&
            _rescueAgent.TryFindActiveAgent(out var activeAgent, out var activeRescue))
        {
            shuttle = activeRescue.AssignedShuttle;
            agent = activeAgent;
            autopilotConsole = activeRescue.AssignedShuttleConsole;
            status =
                $"Only one Aibolit rescue agent may be active. Existing agent={GetNetEntity(activeAgent)}; " +
                "new rescue shuttle purchase and agent spawn blocked.";
            return false;
        }

        if (!deathSignal &&
            spawnAgent &&
            spawnTeam &&
            _sectorStory.TryGetActiveRescueCooldown(out var remainingSeconds, out var cooldownStatus))
        {
            status =
                $"Rescue dispatch cooldown active for {remainingSeconds}s after the last after-action: {cooldownStatus}. " +
                "Use --death-signal for a confirmed death signal or wait before launching another full rescue team.";
            return false;
        }

        if (!_shipyard.TryPurchaseShuttle(station, vessel.ShuttlePath, out shuttle))
        {
            status = $"Failed to purchase rescue vessel {vessel.ID} from {vessel.ShuttlePath}.";
            return false;
        }

        var shuttleName = $"LuaM Rescue {vessel.Name}";
        _metaData.SetEntityName(shuttle.Value, shuttleName);
        if (_station.GetOwningStation(shuttle.Value) is { Valid: true } shuttleStation)
            _station.RenameStation(shuttleStation, shuttleName, loud: false);

        if (TryFindAutopilotConsole(shuttle.Value, out var shuttleConsoleEntity, out _, out _))
            autopilotConsole = shuttleConsoleEntity;

        var returnTarget = TryFindStationReturnTarget(station, out var stationReturnTarget)
            ? stationReturnTarget
            : (EntityUid?) null;

        var routed = false;
        if (routeToTarget &&
            followTarget is { Valid: true } routeTarget)
        {
            routed = TrySetAutopilotTarget(shuttle.Value, routeTarget, out autopilotConsole);
        }

        if (!spawnAgent)
        {
            status = BuildStatus(shuttleName, deployedAgent: false, deployedEscorts: 0, routeRequested: routeToTarget && followTarget != null, routed);
            return true;
        }

        var anchor = TryFindShuttleAnchor(shuttle.Value, out var anchorEntity)
            ? anchorEntity
            : shuttle.Value;

        if (!_rescueAgent.TrySpawnAgent(anchor, followTarget, controller, control, out var spawnedAgent, out var spawnStatus))
        {
            status = spawnStatus;
            agent = spawnedAgent;
            return false;
        }

        agent = spawnedAgent;
        var rescue = EnsureComp<LuaMRescueAgentComponent>(agent.Value);
        rescue.AssignedShuttle = shuttle;
        rescue.AssignedShuttleAnchor = anchor;
        rescue.AssignedShuttleConsole = autopilotConsole;
        rescue.AssignedReturnTarget = returnTarget;
        rescue.AssignedTarget = followTarget;
        if (deathSignal &&
            followTarget is { Valid: true } deathSignalTarget)
        {
            rescue.DeathSignalTarget = deathSignalTarget;
            rescue.DeathSignalDispatchReported = false;
        }
        Dirty(agent.Value, rescue);

        if (spawnTeam)
        {
            var escorts = _rescueTeam.SpawnEscortTeam(agent.Value, anchor, followTarget, shuttle, anchor);
            escortCount = escorts.Count;
        }

        if (deathSignal &&
            followTarget is { Valid: true } dispatchTarget &&
            !Deleted(dispatchTarget))
        {
            SendDispatchRadio(agent.Value, dispatchTarget);
            MarkDeathSignalDispatchReported(agent.Value, rescue, dispatchTarget);
        }

        status = BuildStatus(shuttleName, deployedAgent: true, deployedEscorts: escortCount, routeRequested: routeToTarget && followTarget != null, routed);
        return true;
    }

    private void SendDispatchRadio(EntityUid agent, EntityUid target)
    {
        var targetName = Name(target);
        _radio.SendRadioMessage(
            agent,
            $"Медсигнал смерти принят. Вылетаю к пациенту {targetName}.",
            MedicalRadioChannel,
            agent);
    }

    private void SendCriticalDispatchRadio(EntityUid agent, EntityUid target)
    {
        var targetName = Name(target);
        _radio.SendRadioMessage(
            agent,
            $"\u041a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u0438\u0439 \u043c\u0435\u0434\u0441\u0438\u0433\u043d\u0430\u043b \u043f\u0440\u0438\u043d\u044f\u0442. \u0412\u044b\u043b\u0435\u0442\u0430\u044e \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 {targetName}.",
            MedicalRadioChannel,
            agent);
    }

    private void MarkDeathSignalDispatchReported(EntityUid agent, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        rescue.DeathSignalDispatchReported = true;
        rescue.LastAutoCommsKey = $"death-signal-dispatch:{target}";
        rescue.NextAutoCommsAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoCommsCooldown));
        rescue.LastRescueSpeechKey = rescue.LastAutoCommsKey;
        rescue.NextRescueSpeechAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueSpeechCooldown));
        rescue.LastRescueSpeechStatus = $"speech:auto-comms:{rescue.LastAutoCommsKey}; cooldown={rescue.RescueSpeechCooldown:0}s";
        Dirty(agent, rescue);
    }

    private void MarkCriticalSignalDispatchReported(EntityUid agent, EntityUid target)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
            return;

        rescue.LastAutoCommsKey = $"critical-signal-dispatch:{target}";
        rescue.NextAutoCommsAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoCommsCooldown));
        rescue.LastRescueSpeechKey = rescue.LastAutoCommsKey;
        rescue.NextRescueSpeechAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueSpeechCooldown));
        rescue.LastRescueSpeechStatus = $"speech:auto-comms:{rescue.LastAutoCommsKey}; cooldown={rescue.RescueSpeechCooldown:0}s";
        Dirty(agent, rescue);
    }

    public bool TrySetAutopilotTarget(EntityUid shuttle, EntityUid target, out EntityUid? autopilotConsole)
    {
        autopilotConsole = null;

        if (!TryFindAutopilotConsole(shuttle, out var console, out var shuttleConsole, out var htn))
            return false;

        _npc.SetBlackboard(console, shuttleConsole.AutopilotTargetKey, new EntityCoordinates(target, Vector2.Zero), htn);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);
        _npc.WakeNPC(console, htn);
        autopilotConsole = console;
        return true;
    }

    private static string BuildStatus(string shuttleName, bool deployedAgent, int deployedEscorts, bool routeRequested, bool routed)
    {
        var status = $"Purchased rescue shuttle {shuttleName}";

        if (deployedAgent)
        {
            status += deployedEscorts > 0
                ? $" and deployed a LuaM rescue agent with {deployedEscorts} autonomous escorts"
                : " and deployed a LuaM rescue agent";
        }

        if (routeRequested)
            status += routed
                ? " with autopilot routed to the rescue target"
                : " but autopilot routing was unavailable";

        return status + ".";
    }

    private bool TryFindAutopilotConsole(
        EntityUid shuttle,
        out EntityUid console,
        out ShuttleConsoleComponent shuttleConsole,
        out HTNComponent htn)
    {
        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, HTNComponent, TransformComponent>();
        while (consoleQuery.MoveNext(out var uid, out var consoleComp, out var htnComp, out var xform))
        {
            if (xform.GridUid != shuttle)
                continue;

            console = uid;
            shuttleConsole = consoleComp;
            htn = htnComp;
            return true;
        }

        console = default;
        shuttleConsole = default!;
        htn = default!;
        return false;
    }

    private bool TryFindStationReturnTarget(EntityUid station, out EntityUid returnTarget)
    {
        if (TryComp<StationDataComponent>(station, out var stationData) &&
            _station.GetLargestGrid((station, stationData)) is { Valid: true } grid)
        {
            returnTarget = grid;
            return true;
        }

        returnTarget = default;
        return false;
    }

    private bool TryFindShuttleAnchor(EntityUid shuttle, out EntityUid anchor)
    {
        if (TryFindPatientCareAnchor(shuttle, out anchor))
            return true;

        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (consoleQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == shuttle)
            {
                anchor = uid;
                return true;
            }
        }

        var dockingQuery = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (dockingQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == shuttle)
            {
                anchor = uid;
                return true;
            }
        }

        anchor = default;
        return false;
    }

    private bool TryFindPatientCareAnchor(EntityUid shuttle, out EntityUid anchor)
    {
        EntityUid? careAnchor = null;
        EntityUid? fallbackStrap = null;
        var strapQuery = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (strapQuery.MoveNext(out var uid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !strap.Enabled)
            {
                continue;
            }

            if (HasComp<StasisBedComponent>(uid))
            {
                anchor = uid;
                return true;
            }

            if (HasComp<HealOnBuckleComponent>(uid))
                careAnchor ??= uid;

            fallbackStrap ??= uid;
        }

        if (careAnchor is { Valid: true } care)
        {
            anchor = care;
            return true;
        }

        if (fallbackStrap is { Valid: true } fallback)
        {
            anchor = fallback;
            return true;
        }

        anchor = default;
        return false;
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueShuttleCommand : IConsoleCommand
{
    private const string ControlFlag = "--control";
    private const string NoAgentFlag = "--no-agent";
    private const string NoTeamFlag = "--no-team";
    private const string NoAutopilotFlag = "--no-autopilot";
    private const string DeathSignalFlag = "--death-signal";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public string Command => "luam_rescue_shuttle";
    public string Description => "Purchases a LuaM rescue shuttle and optionally deploys a rescue agent aboard it.";
    public string Help =>
        $"Usage: {Command} [station=<stationEntity>] [vessel={LuaMRescueShuttleSystem.DefaultVessel}] [target=<entity|player>] [{DeathSignalFlag}] [{ControlFlag}] [{NoAgentFlag}] [{NoTeamFlag}] [{NoAutopilotFlag}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var deathSignal = args.Any(arg => arg.Equals(DeathSignalFlag, StringComparison.OrdinalIgnoreCase));
        var control = args.Any(arg => arg.Equals(ControlFlag, StringComparison.OrdinalIgnoreCase));
        var spawnAgent = !args.Any(arg => arg.Equals(NoAgentFlag, StringComparison.OrdinalIgnoreCase));
        var spawnTeam = spawnAgent && !args.Any(arg => arg.Equals(NoTeamFlag, StringComparison.OrdinalIgnoreCase));
        var routeToTarget = !args.Any(arg => arg.Equals(NoAutopilotFlag, StringComparison.OrdinalIgnoreCase));

        if (control && shell.Player == null)
        {
            shell.WriteError($"{ControlFlag} requires a player shell.");
            return;
        }

        var stationArg = GetValue(args, "station");
        var vesselId = GetValue(args, "vessel") ?? LuaMRescueShuttleSystem.DefaultVessel;
        var targetArg = GetValue(args, "target");

        if (deathSignal && string.IsNullOrWhiteSpace(targetArg))
        {
            shell.WriteError($"{DeathSignalFlag} requires target=<entity|player> so Aibolit can report who it is flying to.");
            return;
        }

        if (!TryResolveStation(shell, stationArg, out var station, out var error))
        {
            shell.WriteError(error);
            return;
        }

        if (!_prototypes.TryIndex<VesselPrototype>(vesselId, out var vessel))
        {
            shell.WriteError($"Unknown vessel prototype: {vesselId}.");
            return;
        }

        EntityUid? target = null;
        if (!string.IsNullOrWhiteSpace(targetArg) &&
            !TryResolveTarget(targetArg, out target, out error))
        {
            shell.WriteError(error);
            return;
        }

        var system = _entities.System<LuaMRescueShuttleSystem>();
        if (!system.TryDispatchRescueShuttle(
                station,
                vessel,
                target,
                shell.Player,
                spawnAgent,
                spawnTeam,
                control,
                routeToTarget,
                deathSignal,
                out var shuttle,
                out var agent,
                out var autopilotConsole,
                out var escortCount,
                out var status))
        {
            shell.WriteError(status);
            return;
        }

        var shuttleNet = shuttle is { Valid: true } shuttleUid
            ? _entities.GetNetEntity(shuttleUid).ToString()
            : "none";
        var agentNet = agent is { Valid: true } agentUid
            ? _entities.GetNetEntity(agentUid).ToString()
            : "none";
        var autopilotNet = autopilotConsole is { Valid: true } autopilotUid
            ? _entities.GetNetEntity(autopilotUid).ToString()
            : "none";

        shell.WriteLine($"{status} shuttle={shuttleNet}; agent={agentNet}; escorts={escortCount}; autopilotConsole={autopilotNet}; vessel={vessel.ID}; deathSignal={deathSignal}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.FromHintOptions(
            [
                $"vessel={LuaMRescueShuttleSystem.DefaultVessel}",
                "station=",
                "target=",
                DeathSignalFlag,
                ControlFlag,
                NoAgentFlag,
                NoTeamFlag,
                NoAutopilotFlag,
            ],
            "rescue shuttle option");
    }

    private bool TryResolveStation(IConsoleShell shell, string? raw, out EntityUid station, out string error)
    {
        station = default;
        error = string.Empty;
        var stationSystem = _entities.System<StationSystem>();

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (TryResolveEntity(raw, out var parsed) &&
                _entities.HasComponent<StationDataComponent>(parsed))
            {
                station = parsed;
                return true;
            }

            error = $"Station not found or target is not a station: {raw}.";
            return false;
        }

        if (shell.Player?.AttachedEntity is { Valid: true } attached &&
            stationSystem.GetOwningStation(attached) is { Valid: true } owningStation)
        {
            station = owningStation;
            return true;
        }

        var stations = stationSystem.GetStationsSet();
        if (stations.Count == 1)
        {
            station = stations.First();
            return true;
        }

        error = $"Could not infer station. Pass station=<entity>; current station count is {stations.Count}.";
        return false;
    }

    private bool TryResolveTarget(string raw, out EntityUid? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (TryResolveEntity(raw, out var parsedTarget))
        {
            target = parsedTarget;
            return true;
        }

        var matches = _players.Sessions
            .Where(session => session.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 1 && matches[0].AttachedEntity is { Valid: true } attached)
        {
            target = attached;
            return true;
        }

        error = matches.Length > 1
            ? $"Target player name is ambiguous: {raw}."
            : $"Target not found or has no attached entity: {raw}.";
        return false;
    }

    private bool TryResolveEntity(string raw, out EntityUid entity)
    {
        entity = default;

        if (NetEntity.TryParse(raw, out var netEntity) &&
            _entities.TryGetEntity(netEntity, out var parsed) &&
            parsed is { Valid: true })
        {
            entity = parsed.Value;
            return true;
        }

        if (!int.TryParse(raw, out var integerId))
            return false;

        if (_entities.TryGetEntity(new NetEntity(integerId), out parsed) &&
            parsed is { Valid: true })
        {
            entity = parsed.Value;
            return true;
        }

        var uid = new EntityUid(integerId);
        if (!_entities.EntityExists(uid))
            return false;

        entity = uid;
        return true;
    }

    private static string? GetValue(string[] args, string key)
    {
        var prefix = $"{key}=";
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match?.Substring(prefix.Length);
    }
}
