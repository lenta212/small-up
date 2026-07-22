using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server._LuaM.ShipGen;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Content.Server._LuaM.Administration;

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMShipGeneratorCommand : IConsoleCommand
{
    private const string DefaultPreset = "expedition";
    private const string DefaultSize = "medium";
    private const string DefaultSeed = "auto";

    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_shipgen";
    public string Description => "Requests and spawns a bounded LuaM procedural ship near the attached admin.";
    public string Help =>
        $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} " +
        "[preset=expedition|fighter|salvage] [size=small|medium|large] [seed=auto|ID] [name...]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        if (!TryParse(confirmedArgs, out var request, out var error))
        {
            shell.WriteError(error);
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine("LuaM ship generator: запрос чертежа отправлен; ожидайте до 5 секунд.");
        _ = ExecuteAsync(shell, player, request);
    }

    private async Task ExecuteAsync(
        IConsoleShell shell,
        ICommonSession player,
        LuaMShipGenerationRequest request)
    {
        try
        {
            var system = _entities.System<LuaMShipGeneratorSystem>();
            var result = await system.GenerateNearAsync(player, request);
            if (result.Success)
                shell.WriteLine(result.Message);
            else
                shell.WriteError(result.Message);
        }
        catch (Exception e)
        {
            shell.WriteError($"LuaM ship generator failed: {e.Message}");
        }
    }

    private static bool TryParse(
        string[] args,
        out LuaMShipGenerationRequest request,
        out string error)
    {
        var preset = DefaultPreset;
        var size = DefaultSize;
        var seed = DefaultSeed;
        var nameParts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=');
            if (separator <= 0)
            {
                nameParts.Add(arg);
                continue;
            }

            var key = arg[..separator].Trim().ToLowerInvariant();
            var value = arg[(separator + 1)..].Trim();
            if (key is not ("preset" or "size" or "seed"))
            {
                request = default!;
                error = $"Unknown ship-generator option: {key}.";
                return false;
            }

            if (!seen.Add(key))
            {
                request = default!;
                error = $"Аргумент {key} задан несколько раз.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                request = default!;
                error = $"Аргумент {key} не может быть пустым.";
                return false;
            }

            switch (key)
            {
                case "preset":
                    preset = value;
                    break;
                case "size":
                    size = value;
                    break;
                case "seed":
                    seed = value;
                    break;
            }
        }

        var name = string.Join(' ', nameParts).Trim();
        request = new LuaMShipGenerationRequest(
            preset.ToLowerInvariant(),
            size.ToLowerInvariant(),
            seed.ToLowerInvariant(),
            name);
        error = string.Empty;
        return true;
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromOptions(
                LuaMAiConsoleConfirmation.PrependConfirm(["preset=expedition", "preset=fighter", "preset=salvage"])),
            2 => CompletionResult.FromOptions(["size=small", "size=medium", "size=large"]),
            3 => CompletionResult.FromHint("seed=auto or a deterministic seed ID"),
            _ => CompletionResult.FromHint("optional ship name"),
        };
    }
}
