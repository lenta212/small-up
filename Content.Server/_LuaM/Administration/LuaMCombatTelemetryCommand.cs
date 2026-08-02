using Content.Server.Administration;
using Content.Server._Mono.NPC.HTN;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._LuaM.Administration;

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMCombatTelemetryCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_combat_telemetry";
    public string Description => "Reports bounded ship-combat targeting load telemetry.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var value = _entities.System<ShipTargetingSystem>().GetTelemetry();
        shell.WriteLine(
            $"ship-targeting updates={value.Updates}, avg={value.AverageUpdateMilliseconds:F3} ms, " +
            $"max={value.MaximumUpdateMilliseconds:F3} ms, controllers={value.LastControllers}, " +
            $"fire-control-passes={value.LastFireControlPasses}, cannons-inspected={value.LastCannonsInspected}");
    }
}
