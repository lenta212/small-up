using System.Linq;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Administration;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Administration;
using Content.Shared.Station.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueShuttleSystem : EntitySystem
{
    public const string DefaultVessel = "Triage";

    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly LuaMRescueAgentSystem _rescueAgent = default!;

    public bool TryDispatchRescueShuttle(
        EntityUid station,
        VesselPrototype vessel,
        EntityUid? followTarget,
        ICommonSession? controller,
        bool spawnAgent,
        bool control,
        out EntityUid? shuttle,
        out EntityUid? agent,
        out string status)
    {
        shuttle = null;
        agent = null;
        status = string.Empty;

        if (!HasComp<StationDataComponent>(station))
        {
            status = "Target is not a station.";
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

        if (!spawnAgent)
        {
            status = $"Purchased rescue shuttle {shuttleName}.";
            return true;
        }

        var anchor = TryFindShuttleAnchor(shuttle.Value, out var anchorEntity)
            ? anchorEntity
            : shuttle.Value;

        agent = _rescueAgent.SpawnAgent(anchor, followTarget, controller, control);
        var rescue = EnsureComp<LuaMRescueAgentComponent>(agent.Value);
        rescue.AssignedShuttle = shuttle;
        rescue.AssignedShuttleAnchor = anchor;
        rescue.AssignedTarget = followTarget;
        Dirty(agent.Value, rescue);

        status = $"Purchased rescue shuttle {shuttleName} and deployed a LuaM rescue agent.";
        return true;
    }

    private bool TryFindShuttleAnchor(EntityUid shuttle, out EntityUid anchor)
    {
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
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueShuttleCommand : IConsoleCommand
{
    private const string ControlFlag = "--control";
    private const string NoAgentFlag = "--no-agent";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public string Command => "luam_rescue_shuttle";
    public string Description => "Purchases a LuaM rescue shuttle and optionally deploys a rescue agent aboard it.";
    public string Help =>
        $"Usage: {Command} [station=<stationEntity>] [vessel={LuaMRescueShuttleSystem.DefaultVessel}] [target=<entity|player>] [{ControlFlag}] [{NoAgentFlag}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var control = args.Any(arg => arg.Equals(ControlFlag, StringComparison.OrdinalIgnoreCase));
        var spawnAgent = !args.Any(arg => arg.Equals(NoAgentFlag, StringComparison.OrdinalIgnoreCase));

        if (control && shell.Player == null)
        {
            shell.WriteError($"{ControlFlag} requires a player shell.");
            return;
        }

        var stationArg = GetValue(args, "station");
        var vesselId = GetValue(args, "vessel") ?? LuaMRescueShuttleSystem.DefaultVessel;
        var targetArg = GetValue(args, "target");

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
        if (!system.TryDispatchRescueShuttle(station, vessel, target, shell.Player, spawnAgent, control, out var shuttle, out var agent, out var status))
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

        shell.WriteLine($"{status} shuttle={shuttleNet}; agent={agentNet}; vessel={vessel.ID}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.FromHintOptions(
            [
                $"vessel={LuaMRescueShuttleSystem.DefaultVessel}",
                "station=",
                "target=",
                ControlFlag,
                NoAgentFlag,
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
