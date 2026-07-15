using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server._LuaM.Sector;
using Content.Shared._LuaM.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._LuaM.Administration;

internal static class LuaMAiConsoleConfirmation
{
    public const string ConfirmFlag = "--confirm";

    public static bool TryConsume(IConsoleShell shell, string command, string[] args, out string[] confirmedArgs)
    {
        var confirmed = false;
        var cleanArgs = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (arg.Equals(ConfirmFlag, StringComparison.OrdinalIgnoreCase))
            {
                confirmed = true;
                continue;
            }

            cleanArgs.Add(arg);
        }

        confirmedArgs = cleanArgs.ToArray();
        if (confirmed)
            return true;

        shell.WriteError($"LuaM AI action not executed. Re-run `{command} {ConfirmFlag}` after reviewing the command.");
        shell.WriteError("This command can affect the round or send player-visible AI output.");
        return false;
    }

    public static IEnumerable<string> PrependConfirm(IEnumerable<string> options)
    {
        return options.Prepend(ConfirmFlag);
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class LuaMAiDirectorCommand : IConsoleCommand
{
    public string Command => "luamai";
    public string Description => "Opens the LuaM AI director admin window or sends a direct AI director message.";
    public string Help => $"Usage: {Command} [{LuaMAiConsoleConfirmation.ConfirmFlag}] [message...]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(argStr))
        {
            var confirmed = args.Any(arg =>
                arg.Equals(LuaMAiConsoleConfirmation.ConfirmFlag, StringComparison.OrdinalIgnoreCase));
            var message = confirmed
                ? string.Join(' ', args.Where(arg =>
                    !arg.Equals(LuaMAiConsoleConfirmation.ConfirmFlag, StringComparison.OrdinalIgnoreCase)))
                : argStr;

            if (string.IsNullOrWhiteSpace(message))
            {
                shell.WriteError($"Usage: {Command} [{LuaMAiConsoleConfirmation.ConfirmFlag}] [message...]");
                return;
            }

            _ = ExecuteChatAsync(shell, player, message, confirmed);
            return;
        }

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new LuaMAiDirectorEui(), player);
    }

    private static async Task ExecuteChatAsync(
        IConsoleShell shell,
        ICommonSession player,
        string message,
        bool confirmed)
    {
        try
        {
            var director = IoCManager.Resolve<IEntityManager>().System<LuaMSectorAiDirectorSystem>();
            var admin = IoCManager.Resolve<IAdminManager>();
            var canRunServerActions = confirmed && admin.HasAdminFlag(player, AdminFlags.Server);
            var reply = await director.AdminChatAsync(
                player,
                message,
                string.Empty,
                LuaMAiDirectorEuiMsg.AutoTemplateId,
                allowServerActions: canRunServerActions);

            shell.WriteLine(reply);
        }
        catch (Exception e)
        {
            shell.WriteError($"LuaM AI chat command failed: {e.Message}");
        }
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAiGenerateEventCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_ai_generate_event";
    public string Description => "Generates a LuaM event around an active player through the OpenAI-compatible AI director.";
    public string Help => $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} [templateId|auto] [--force] [instruction...]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        _ = ExecuteAsync(shell, confirmedArgs);
    }

    private async Task ExecuteAsync(IConsoleShell shell, string[] args)
    {
        try
        {
            await ExecuteInnerAsync(shell, args);
        }
        catch (Exception e)
        {
            shell.WriteError($"LuaM AI generation command failed: {e.Message}");
        }
    }

    private async Task ExecuteInnerAsync(IConsoleShell shell, string[] args)
    {
        var templateId = LuaMAiDirectorEuiMsg.AutoTemplateId;
        var ignoreOpenLead = false;
        var instructionStart = 0;
        var generator = _entities.System<LuaMSectorDynamicEventSystem>();
        var templateIds = generator.GetTemplateIds();

        if (args.Length > 0)
        {
            if (args[0].Equals("--force", StringComparison.OrdinalIgnoreCase))
            {
                ignoreOpenLead = true;
                instructionStart = 1;
            }
            else if (args[0].Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                     templateIds.Contains(args[0], StringComparer.OrdinalIgnoreCase))
            {
                templateId = args[0].Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? LuaMAiDirectorEuiMsg.AutoTemplateId
                    : args[0];
                instructionStart = 1;
            }
        }

        if (args.Length > instructionStart &&
            args[instructionStart].Equals("--force", StringComparison.OrdinalIgnoreCase))
        {
            ignoreOpenLead = true;
            instructionStart++;
        }

        var instruction = args.Length > instructionStart
            ? string.Join(' ', args.Skip(instructionStart))
            : string.Empty;

        var actor = shell.Player?.Name ?? "server-console";
        var director = _entities.System<LuaMSectorAiDirectorSystem>();
        var result = await director.GenerateImmediateAsync(
            $"LuaM AI director / console {actor}",
            string.Empty,
            templateId,
            instruction,
            useGateway: true,
            ignoreOpenLead);

        shell.WriteLine(result);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var generator = _entities.System<LuaMSectorDynamicEventSystem>();
            return CompletionResult.FromHintOptions(
                LuaMAiConsoleConfirmation.PrependConfirm(generator.GetTemplateIds().Prepend("auto").Prepend("--force")),
                "template id, auto, --force, or --confirm");
        }

        if (args.Length == 2 && !args[0].Equals("--force", StringComparison.OrdinalIgnoreCase))
            return CompletionResult.FromHintOptions(["--force", LuaMAiConsoleConfirmation.ConfirmFlag], "optional flag");

        return CompletionResult.FromHint("optional AI instruction");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAiSyntheticControlCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_ai_synthetic_control";
    public string Description => "Arms LuaM AI synthetic control over empty borgs, bots, and law-provider synthetic devices.";
    public string Help => $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} [--silent]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        var announce = !confirmedArgs.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        var actor = shell.Player?.Name ?? "server-console";
        var director = _entities.System<LuaMSectorAiDirectorSystem>();
        var result = director.ApplySyntheticControl($"LuaM AI director / console {actor}", announce);
        shell.WriteLine(result);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions([LuaMAiConsoleConfirmation.ConfirmFlag, "--silent"], "optional flag")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAiSubspaceRiftCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_ai_subspace_rift";
    public string Description => "Opens a temporary LuaM Stargate-style linked portal pair near an active player.";
    public string Help => $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} [targetUserId|--random] [--silent] [--route] [--to-player playerNameOrId] [instruction...]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        var targetUserId = string.Empty;
        var announce = true;
        var routeDestination = false;
        var operatorDestination = false;
        var operatorDestinationSelector = string.Empty;
        var instructionParts = new List<string>();

        for (var i = 0; i < confirmedArgs.Length; i++)
        {
            var arg = confirmedArgs[i];
            if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase))
            {
                announce = false;
                continue;
            }

            if (arg.Equals("--route", StringComparison.OrdinalIgnoreCase))
            {
                routeDestination = true;
                continue;
            }

            if (arg.Equals("--to-player", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--to-operator", StringComparison.OrdinalIgnoreCase))
            {
                operatorDestination = true;
                if (i + 1 < confirmedArgs.Length && !confirmedArgs[i + 1].StartsWith("--", StringComparison.Ordinal))
                    operatorDestinationSelector = confirmedArgs[++i];
                continue;
            }

            if (arg.Equals("--random", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(targetUserId) && instructionParts.Count == 0)
                targetUserId = arg;
            else
                instructionParts.Add(arg);
        }

        var instruction = string.Join(' ', instructionParts).Trim();
        if (routeDestination)
            instruction = $"к маршруту {instruction}".Trim();
        if (operatorDestination)
            instruction = $"к оператору {operatorDestinationSelector} {instruction}".Trim();

        var actor = shell.Player?.Name ?? "server-console";
        var director = _entities.System<LuaMSectorAiDirectorSystem>();
        var result = director.ApplySubspaceRiftAroundTarget(
            targetUserId,
            $"LuaM AI director / console {actor} / subspace rift",
            instruction,
            announce);

        shell.WriteLine(result);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions([LuaMAiConsoleConfirmation.ConfirmFlag, "--random", "--silent", "--route", "--to-player"], "target user id or optional flag");

        if (args.Length == 2)
            return CompletionResult.FromHintOptions([LuaMAiConsoleConfirmation.ConfirmFlag, "--silent", "--route", "--to-player"], "optional flag");

        return CompletionResult.FromHint("optional AI instruction");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAiSayCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_ai_say";
    public string Description => "Sends a visible LuaM AI message to chat.";
    public string Help => $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} [--target playerNameOrId] <message>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        var targetUserId = string.Empty;
        var messageParts = new List<string>();

        for (var i = 0; i < confirmedArgs.Length; i++)
        {
            var arg = confirmedArgs[i];
            if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < confirmedArgs.Length)
            {
                targetUserId = confirmedArgs[++i];
                continue;
            }

            messageParts.Add(arg);
        }

        var message = string.Join(' ', messageParts).Trim();
        var actor = shell.Player?.Name ?? "server-console";
        var director = _entities.System<LuaMSectorAiDirectorSystem>();
        var result = director.SendAiChatMessage(
            message,
            $"LuaM AI director / console {actor} / chat",
            targetUserId);

        shell.WriteLine(result);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions([LuaMAiConsoleConfirmation.ConfirmFlag, "--target"], "optional player target or message");

        return CompletionResult.FromHint("AI chat message");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAiRadioCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_ai_radio";
    public string Description => "Sends a visible LuaM AI message to a radio channel.";
    public string Help => $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} [--channel channelId] <message>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        var channelId = string.Empty;
        var messageParts = new List<string>();

        for (var i = 0; i < confirmedArgs.Length; i++)
        {
            var arg = confirmedArgs[i];
            if ((arg.Equals("--channel", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-c", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < confirmedArgs.Length)
            {
                channelId = confirmedArgs[++i];
                continue;
            }

            if (arg.StartsWith("--channel=", StringComparison.OrdinalIgnoreCase))
            {
                channelId = arg["--channel=".Length..];
                continue;
            }

            messageParts.Add(arg);
        }

        var message = string.Join(' ', messageParts).Trim();
        var actor = shell.Player?.Name ?? "server-console";
        var director = _entities.System<LuaMSectorAiDirectorSystem>();
        var result = director.SendAiRadioMessage(
            message,
            $"LuaM AI director / console {actor} / radio",
            channelId);

        shell.WriteLine(result);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions([LuaMAiConsoleConfirmation.ConfirmFlag, "--channel", "-c"], "optional radio channel or message");

        if (args.Length == 2 && (args[0].Equals("--channel", StringComparison.OrdinalIgnoreCase) ||
                                 args[0].Equals("-c", StringComparison.OrdinalIgnoreCase)))
        {
            return CompletionResult.FromHintOptions(
                ["Common", "Command", "Engineering", "Medical", "Nfsd", "Science", "Security", "Supply", "Traffic"],
                "radio channel id");
        }

        return CompletionResult.FromHint("AI radio message");
    }
}
