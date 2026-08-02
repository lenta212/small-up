using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server._LuaM.ShipPersistence;
using Content.Server._Mono.FireControl;
using Content.Server.Power.Components;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Administration;
using Content.Shared.APC;
using Content.Shared.Power.Components;
using Robust.Shared.Console;

namespace Content.Server._LuaM.Administration;

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMShipStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_ship_status";
    public string Description => "Reports active persistent-ship leases and their local runtime state.";
    public string Help => $"Usage: {Command} [shipId]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1 ||
            args.Length == 1 && !Guid.TryParse(args[0], out _))
        {
            shell.WriteError(Help);
            return;
        }

        var shipId = args.Length == 1 ? Guid.Parse(args[0]) : (Guid?) null;
        var diagnostics = _entities
            .System<LuaMShipPersistenceOrchestrator>()
            .GetActiveShipDiagnostics(shipId);
        if (diagnostics.Count == 0)
        {
            shell.WriteLine(shipId == null
                ? "No active persistent ships are registered in this process."
                : $"Persistent ship {shipId:D} is not active in this process.");
            return;
        }

        var output = new StringBuilder($"Active persistent ships: {diagnostics.Count}\n");
        foreach (var ship in diagnostics)
        {
            output.AppendLine(
                $"- {ship.ShipId:D} \"{ship.ShipName}\" prototype={ship.VesselPrototypeId}, " +
                $"owner={ship.OwnerUserId}, grid={ship.Grid} ({(ship.GridExists ? "present" : "missing")}), " +
                $"mobs={ship.MobStateEntityCount}, registryRev={ship.RegistryRevision}, " +
                $"payloadRev={ship.PayloadRevision}, durablePayloadRev={ship.DurablePayloadRevision?.ToString() ?? "none"}, " +
                $"lease={ship.LeaseId:D}, leaseRev={ship.LeaseRevision}");
        }

        shell.WriteLine(output.ToString().TrimEnd());
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMShipDiagnoseCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_ship_diagnose";
    public string Description => "Read-only power, shield and gunnery diagnostics for an active persistent ship.";
    public string Help => $"Usage: {Command} <shipId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var shipId))
        {
            shell.WriteError(Help);
            return;
        }

        var ship = _entities.System<LuaMShipPersistenceOrchestrator>()
            .GetActiveShipDiagnostics(shipId)
            .SingleOrDefault();
        if (ship == null || !ship.GridExists)
        {
            shell.WriteError($"Persistent ship {shipId:D} is not active with a live grid.");
            return;
        }

        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        var receivers = 0;
        var unpowered = 0;
        var apcs = 0;
        var powerlessApcs = 0;
        var substations = 0;
        var powerlessSubstations = 0;
        var nonFinitePower = 0;
        var shields = 0;
        var brokenShieldLinks = 0;
        var fireControlServers = 0;
        var controllableWeapons = 0;
        var brokenGunneryLinks = 0;
        pending.Push(ship.Grid);

        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !_entities.EntityExists(uid))
                continue;

            if (_entities.TryGetComponent(uid, out ApcPowerReceiverComponent? receiver))
            {
                receivers++;
                if (!receiver.Powered)
                    unpowered++;
            }

            if (_entities.TryGetComponent(uid, out ApcComponent? apc))
            {
                apcs++;
                if (!apc.MainBreakerEnabled || apc.LastExternalState == ApcExternalPowerState.None)
                    powerlessApcs++;
            }

            if (_entities.TryGetComponent(uid, out BatteryComponent? battery) && !float.IsFinite(battery.CurrentCharge))
                nonFinitePower++;
            if (_entities.TryGetComponent(uid, out PowerNetworkBatteryComponent? network))
            {
                var state = network.NetworkBattery;
                if (!float.IsFinite(state.CurrentStorage) || !float.IsFinite(state.SupplyRampPosition) ||
                    !float.IsFinite(state.CurrentSupply) || !float.IsFinite(state.CurrentReceiving) ||
                    !float.IsFinite(state.LoadingNetworkDemand))
                    nonFinitePower++;

                var prototype = _entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;
                if (prototype?.Contains("Substation", StringComparison.OrdinalIgnoreCase) == true)
                {
                    substations++;
                    if (state.CurrentStorage <= 0f && state.CurrentSupply <= 0f && state.CurrentReceiving <= 0f)
                        powerlessSubstations++;
                }
            }
            if (_entities.TryGetComponent(uid, out PowerChargeComponent? charge) && !float.IsFinite(charge.Charge))
                nonFinitePower++;
            if (_entities.TryGetComponent(uid, out ShipShieldComponent? shield))
            {
                shields++;
                if (shield.Source is not { } source || !_entities.EntityExists(source) ||
                    !_entities.EntityExists(shield.Shielded))
                    brokenShieldLinks++;
            }
            if (_entities.TryGetComponent(uid, out ShipShieldEmitterComponent? emitter) &&
                (emitter.Shield is not { } emitterShield || !_entities.EntityExists(emitterShield) ||
                 emitter.Shielded is not { } emitterShielded || !_entities.EntityExists(emitterShielded)))
                brokenShieldLinks++;
            if (_entities.TryGetComponent(uid, out FireControlServerComponent? server))
            {
                fireControlServers++;
                if (server.ConnectedGrid != ship.Grid)
                    brokenGunneryLinks++;
                foreach (var controlled in server.Controlled)
                {
                    if (!_entities.TryGetComponent(controlled, out FireControllableComponent? weapon) ||
                        weapon.ControllingServer != uid)
                        brokenGunneryLinks++;
                }
            }
            if (_entities.TryGetComponent(uid, out FireControllableComponent? controllable))
            {
                controllableWeapons++;
                if (controllable.ControllingServer is not { } controllingServer ||
                    !_entities.TryGetComponent(controllingServer, out FireControlServerComponent? linkedServer) ||
                    !linkedServer.Controlled.Contains(uid))
                    brokenGunneryLinks++;
            }

            var children = _entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        shell.WriteLine(
            $"ship={ship.ShipId:D} grid={ship.Grid} entities={visited.Count}; " +
            $"power-receivers={receivers}, unpowered={unpowered}, non-finite-power-components={nonFinitePower}; " +
            $"apcs={apcs}, powerless-apcs={powerlessApcs}, substations={substations}, " +
            $"powerless-substations={powerlessSubstations}; shields={shields}, broken-shield-links={brokenShieldLinks}; " +
            $"fire-control-servers={fireControlServers}, controllable-weapons={controllableWeapons}, " +
            $"broken-gunnery-links={brokenGunneryLinks}");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMShipEmergencySaveCommand : IConsoleCommand
{
    private const string ConfirmFlag = "--confirm";

    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_ship_emergency_save";
    public string Description =>
        "Previews or confirms an exact active ship save followed by grid deletion; refuses ships with mobs aboard.";
    public string Help => $"Usage: {Command} <shipId> [{ConfirmFlag} <sameShipId>]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is not (1 or 3) || !Guid.TryParse(args[0], out var shipId))
        {
            shell.WriteError(Help);
            return;
        }

        var persistence = _entities.System<LuaMShipPersistenceOrchestrator>();
        var diagnostic = persistence.GetActiveShipDiagnostics(shipId).SingleOrDefault();
        if (diagnostic == null)
        {
            shell.WriteError($"Persistent ship {shipId:D} is not active in this process.");
            return;
        }

        if (args.Length == 1)
        {
            shell.WriteLine(
                $"PREVIEW ONLY: {shipId:D} \"{diagnostic.ShipName}\", grid={diagnostic.Grid}, " +
                $"mobs={diagnostic.MobStateEntityCount}. A confirmed operation stores a new durable snapshot " +
                "and queues this exact grid for deletion.");
            shell.WriteLine(
                $"Confirm with: `{Command} {shipId:D} {ConfirmFlag} {shipId:D}`");
            return;
        }

        if (!args[1].Equals(ConfirmFlag, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(args[2], out var confirmedShipId) ||
            confirmedShipId != shipId)
        {
            shell.WriteError("Confirmation must repeat the exact shipId.");
            shell.WriteError(Help);
            return;
        }

        var requestedBy = shell.Player?.Name ?? "server-console";
        _ = ExecuteConfirmedAsync(shell, persistence, shipId, requestedBy);
    }

    private static async Task ExecuteConfirmedAsync(
        IConsoleShell shell,
        LuaMShipPersistenceOrchestrator persistence,
        Guid shipId,
        string requestedBy)
    {
        try
        {
            var result = await persistence.EmergencyStoreAndDeleteActiveShipAsync(
                shipId,
                requestedBy,
                DateTime.UtcNow);
            if (!result.Success)
            {
                shell.WriteError(
                    $"Emergency save failed for {shipId:D}: {result.Status}; {result.Reason ?? "no reason"}.");
                return;
            }

            shell.WriteLine(
                $"Emergency save completed for {shipId:D}; durable revision={result.Revision}, " +
                "grid deletion queued.");
        }
        catch (Exception exception)
        {
            shell.WriteError(
                $"Emergency save failed for {shipId:D}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
