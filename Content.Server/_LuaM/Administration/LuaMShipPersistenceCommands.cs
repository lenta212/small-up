using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server._LuaM.ShipPersistence;
using Content.Shared.Administration;
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
