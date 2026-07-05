using System.Linq;
using System.Text;
using Content.Server.Administration;
using Content.Shared._LuaM.Sector;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Enums;

namespace Content.Server._LuaM.Sector;

[AnyCommand]
public sealed partial class LuaMAiPlayerChatCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam";
    public string Description => "Sends a player message to the LuaM AI director.";
    public string Help => $"Usage: {Command} <status|route|mission|nearby|stargate|message>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        if (player.Status != SessionStatus.InGame)
        {
            shell.WriteError("LuaM AI channel is available after entering the game.");
            return;
        }

        var message = string.Join(' ', args).Trim();
        if (string.IsNullOrWhiteSpace(message))
            message = "help";

        var director = _entities.System<LuaMSectorAiDirectorSystem>();
        var result = director.HandlePlayerAiRequest(player, message, "/luam command");
        director.SendAiChatMessage(result, $"ИИ-диспетчер LuaM / /luam reply {player.Name}", player.UserId.ToString());
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                ["статус", "маршрут", "задание", "событие рядом", "врата", "врата к заданию", "врата к оператору"],
                "LuaM AI request");
        }

        return CompletionResult.FromHint("message to LuaM AI");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorStatusCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_status";
    public string Description => "Prints the current LuaM sector memory status.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        var snapshot = storySystem.GetStatusSnapshot();

        var output = new StringBuilder();
        output.AppendLine($"Stories: {snapshot.TotalStories}");
        output.AppendLine($"Active hazards: {snapshot.ActiveHazards}");
        output.AppendLine($"Acknowledged hazards: {snapshot.AcknowledgedHazards}");
        output.AppendLine($"Insurance payouts: {snapshot.InsurancePayouts}");
        output.AppendLine($"Black box recoveries: {snapshot.BlackBoxRecoveries}");
        output.AppendLine($"Company records: {snapshot.CompanyRecords}");
        output.AppendLine($"Ship records: {snapshot.ShipRecords}");
        output.AppendLine($"Active conditions: {snapshot.ActiveConditions}");
        output.AppendLine($"Locked stories: {snapshot.LockedStories}");

        if (snapshot.LockedLeads.Count > 0)
        {
            output.AppendLine("Locked leads:");
            foreach (var lead in snapshot.LockedLeads)
            {
                output.AppendLine($"- {lead.Title}: {lead.RequiredTarget} {lead.CurrentValue}/{lead.RequiredValue}");
            }
        }

        if (snapshot.Reputation.Count > 0)
        {
            output.AppendLine("Reputation:");
            foreach (var entry in snapshot.Reputation)
            {
                output.AppendLine($"- {entry.Target}: {entry.Value} ({entry.Tier}, +{entry.RewardBonus} contracts)");
            }
        }

        var activeHazards = snapshot.Hazards
            .Where(hazard => !hazard.Resolved)
            .OrderByDescending(hazard => hazard.Severity)
            .ThenBy(hazard => hazard.Title)
            .ToList();

        if (activeHazards.Count > 0)
        {
            output.AppendLine("Hazards:");
            foreach (var hazard in activeHazards)
            {
                var acknowledged = hazard.Acknowledged ? "acknowledged" : "open";
                output.AppendLine($"- HZ-{hazard.Severity} {hazard.Title} ({acknowledged}, +{hazard.RewardBonus})");
            }
        }

        var activeConditions = snapshot.Conditions
            .Where(condition => condition.Active)
            .OrderByDescending(condition => condition.Severity)
            .ThenBy(condition => condition.Title)
            .ToList();

        if (activeConditions.Count > 0)
        {
            output.AppendLine("Sector conditions:");
            foreach (var condition in activeConditions)
            {
                output.AppendLine($"- SC-{condition.Severity} {condition.Title} [{condition.ConditionId}] by {condition.Actor}: {condition.Summary}");
            }
        }

        if (snapshot.RecentHistory.Count > 0)
        {
            output.AppendLine("Recent history:");
            foreach (var entry in snapshot.RecentHistory)
            {
                output.AppendLine($"- {entry.Category}: {entry.Title} by {entry.Actor} ({entry.Summary})");
            }
        }

        shell.WriteLine(output.ToString().TrimEnd());
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorConditionCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_condition";
    public string Description => "Creates or updates a LuaM sector condition.";
    public string Help => $"Usage: {Command} <id> <severity 1-5> <title>|<summary>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (!int.TryParse(args[1], out var severity))
        {
            shell.WriteError("Severity must be an integer from 1 to 5.");
            return;
        }

        var payload = string.Join(' ', args.Skip(2));
        var parts = payload.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        var actor = shell.Player?.Name ?? "console";
        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TrySeedSectorCondition(args[0], parts[0], severity, parts[1], actor, out var entry, out var error) ||
            entry == null)
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM sector condition active: SC-{entry.Severity} {entry.Title} [{entry.ConditionId}]");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorConditionPresetCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    private static readonly LuaMSectorConditionPreset[] Presets =
    [
        new(
            "comms-blackout",
            "Comms blackout",
            4,
            "Long-range radio and remote updates are unreliable across the sector."),
        new(
            "radiation-lane",
            "Radiation lane",
            4,
            "A salvage lane is reporting elevated radiation. Use shielding and short exposure windows."),
        new(
            "dust-cloud",
            "Dust cloud",
            3,
            "Sensor drift and poor visual fixes are likely near active route markers."),
        new(
            "unstable-trade-route",
            "Unstable trade route",
            3,
            "Courier and cargo traffic is slowed by route instability and reroute pressure."),
    ];

    public string Command => "luam_sector_condition_preset";
    public string Description => "Creates or updates a preset LuaM sector condition.";
    public string Help => $"Usage: {Command} <{string.Join('|', Presets.Select(preset => preset.Id))}>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var preset = Presets.FirstOrDefault(preset => preset.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            shell.WriteError($"Unknown LuaM sector condition preset: {args[0]}");
            shell.WriteError(Help);
            return;
        }

        var actor = shell.Player?.Name ?? "console";
        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TrySeedSectorCondition(
                preset.Id,
                preset.Title,
                preset.Severity,
                preset.Summary,
                actor,
                out var entry,
                out var error) ||
            entry == null)
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM sector condition preset active: SC-{entry.Severity} {entry.Title} [{entry.ConditionId}]");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions(Presets.Select(preset => preset.Id), "sector condition preset")
            : CompletionResult.Empty;
    }

    private sealed record LuaMSectorConditionPreset(
        string Id,
        string Title,
        int Severity,
        string Summary);
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorConditionClearCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_condition_clear";
    public string Description => "Clears an active LuaM sector condition.";
    public string Help => $"Usage: {Command} <id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var actor = shell.Player?.Name ?? "console";
        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryClearSectorCondition(args[0], actor, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM sector condition cleared: {args[0]}");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorExportCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_export";
    public string Description => "Prints the current LuaM sector memory snapshot as JSON.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryExportMemoryJson(out var json))
        {
            shell.WriteError("LuaM sector memory is not available.");
            return;
        }

        shell.WriteLine(json);
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorImportCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_import";
    public string Description => "Imports a LuaM sector memory snapshot from JSON.";
    public string Help => $"Usage: {Command} <json>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var json = argStr.Trim();
        if (json.StartsWith(Command, StringComparison.OrdinalIgnoreCase))
            json = json[Command.Length..].TrimStart();

        if (string.IsNullOrWhiteSpace(json))
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryImportMemoryJson(json, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("LuaM sector memory imported.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.FromHint("exported JSON");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorExportFileCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_export_file";
    public string Description => "Exports the current LuaM sector memory snapshot to a UserData JSON file under /luam.";
    public string Help => $"Usage: {Command} <path>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryExportMemoryFile(args[0], out var path, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM sector memory exported to {path}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? LuaMSectorCommandCompletions.GetBackupCompletion(_entities, "backup JSON path under /luam")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorImportFileCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_import_file";
    public string Description => "Imports a LuaM sector memory snapshot from a UserData JSON file under /luam.";
    public string Help => $"Usage: {Command} <path>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryImportMemoryFile(args[0], out var path, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM sector memory imported from {path}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? LuaMSectorCommandCompletions.GetBackupCompletion(_entities, "backup JSON path under /luam")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorBackupsCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_backups";
    public string Description => "Lists LuaM sector memory backup JSON files under /luam.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        var backups = storySystem.GetMemoryBackups();
        if (backups.Count == 0)
        {
            shell.WriteLine("No LuaM sector memory backups found.");
            return;
        }

        var output = new StringBuilder();
        output.AppendLine("LuaM sector memory backups:");
        foreach (var path in backups)
        {
            output.AppendLine($"- {path}");
        }

        shell.WriteLine(output.ToString().TrimEnd());
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorDeleteBackupCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_delete_backup";
    public string Description => "Deletes a LuaM sector memory backup JSON file under /luam.";
    public string Help => $"Usage: {Command} <path>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryDeleteMemoryBackup(args[0], out var path, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM sector memory backup deleted: {path}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? LuaMSectorCommandCompletions.GetBackupCompletion(_entities, "backup JSON path under /luam")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorResetCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_reset";
    public string Description => "Resets current LuaM sector memory and deletes its persisted snapshot.";
    public string Help => $"Usage: {Command} confirm";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !args[0].Equals("confirm", StringComparison.OrdinalIgnoreCase))
        {
            shell.WriteError(Help);
            return;
        }

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryResetMemory(deletePersisted: true))
        {
            shell.WriteError("LuaM sector memory is not available.");
            return;
        }

        shell.WriteLine("LuaM sector memory reset and persisted snapshot deleted.");
    }
}

internal static class LuaMSectorCommandCompletions
{
    public static CompletionResult GetBackupCompletion(IEntityManager entities, string hint)
    {
        var backups = entities.System<LuaMSectorStorySystem>()
            .GetMemoryBackups()
            .Select(path => path.ToString());

        return CompletionResult.FromHintOptions(backups, hint);
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorSeedDistressCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_seed_distress";
    public string Description => "Creates a runtime LuaM distress story, news post, and PDA contract.";
    public string Help => $"Usage: {Command} <title>|<vessel>|<reward>|<description>|[hazard]|[reputationTarget]|[reputationDelta]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var raw = argStr.Trim();
        if (raw.StartsWith(Command, StringComparison.OrdinalIgnoreCase))
            raw = raw[Command.Length..].TrimStart();

        var parts = raw.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 4 ||
            !int.TryParse(parts[2], out var reward))
        {
            shell.WriteError(Help);
            return;
        }

        var hazard = parts.Length > 4 ? parts[4] : string.Empty;
        var reputationTarget = parts.Length > 5 ? parts[5] : "Distress";
        var reputationDelta = 1;
        if (parts.Length > 6 &&
            (!int.TryParse(parts[6], out reputationDelta) || reputationDelta == 0))
        {
            shell.WriteError("Reputation delta must be a non-zero integer.");
            return;
        }

        var actor = shell.Player?.Name ?? "server-console";
        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TrySeedDistressStory(
                parts[0],
                parts[1],
                reward,
                parts[3],
                hazard,
                reputationTarget,
                reputationDelta,
                actor,
                out var record,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM runtime distress story seeded: {record!.Story}");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.FromHint("title|vessel|reward|description|hazard|reputation target|reputation delta");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorGenerateEventCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_generate_event";
    public string Description => "Generates a weighted dynamic LuaM sector event as a runtime lead, news post, and PDA contract.";
    public string Help => $"Usage: {Command} [templateId]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        var templateId = args.Length == 1 ? args[0] : null;
        var actor = shell.Player?.Name ?? "server-console";
        var generator = _entities.System<LuaMSectorDynamicEventSystem>();

        if (!generator.TryGenerateDynamicEvent(
                actor,
                out var record,
                out var error,
                templateId,
                ignorePlayerGate: true))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"LuaM dynamic sector event generated: {record!.Story} ({record.Title})");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        var generator = _entities.System<LuaMSectorDynamicEventSystem>();
        return CompletionResult.FromHintOptions(generator.GetTemplateIds(), "dynamic event template id");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorResolveCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_resolve";
    public string Description => "Manually resolves a LuaM sector story.";
    public string Help => $"Usage: {Command} <storyId> [note...]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        var storyId = args[0];
        var note = args.Length > 1
            ? string.Join(' ', args.Skip(1))
            : "manual console resolve";
        var actor = shell.Player?.Name ?? "server-console";

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        if (!storySystem.TryResolveStory(storyId, actor, note))
        {
            shell.WriteError($"LuaM sector story could not be resolved: {storyId}");
            return;
        }

        shell.WriteLine($"LuaM sector story resolved: {storyId}");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                CompletionHelper.PrototypeIDs<LuaMSectorStoryPrototype>(),
                "story prototype");
        }

        return CompletionResult.FromHint("resolution note");
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LuaMSectorHistoryCommand : IConsoleCommand
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    [Dependency] private IEntityManager _entities = default!;

    public string Command => "luam_sector_history";
    public string Description => "Prints recent LuaM sector memory ledger entries.";
    public string Help => $"Usage: {Command} [limit]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        var limit = DefaultLimit;
        if (args.Length == 1 &&
            (!int.TryParse(args[0], out limit) || limit < 1))
        {
            shell.WriteError("Limit must be a positive integer.");
            return;
        }

        limit = Math.Min(limit, MaxLimit);

        var storySystem = _entities.System<LuaMSectorStorySystem>();
        var output = new StringBuilder();

        AppendReputation(output, storySystem.GetReputationEntries(), limit);
        AppendHazards(output, storySystem.GetHazardReports(), limit);
        AppendInsurance(output, storySystem.GetInsurancePayouts(), limit);
        AppendBlackBoxes(output, storySystem.GetBlackBoxRecoveries(), limit);
        AppendRegistry(output, "Company registry", storySystem.GetCompanyRegistry(), limit);
        AppendRegistry(output, "Ship registry", storySystem.GetShipRegistry(), limit);

        shell.WriteLine(output.Length == 0
            ? "LuaM sector history is empty."
            : output.ToString().TrimEnd());
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHint("entry limit");

        return CompletionResult.Empty;
    }

    private static void AppendReputation(StringBuilder output, IReadOnlyList<LuaMSectorReputationLedgerEntry> entries, int limit)
    {
        if (entries.Count == 0)
            return;

        output.AppendLine("Reputation:");
        foreach (var entry in entries.TakeLast(limit))
        {
            output.AppendLine($"- {entry.Story}: {entry.Target} {entry.Delta:+#;-#;0} by {entry.Actor} ({entry.Note})");
        }
    }

    private static void AppendHazards(StringBuilder output, IReadOnlyList<LuaMSectorHazardReportEntry> entries, int limit)
    {
        if (entries.Count == 0)
            return;

        output.AppendLine("Hazards:");
        foreach (var entry in entries.TakeLast(limit))
        {
            output.AppendLine($"- {entry.Story}: {entry.Actor} ({entry.Note})");
        }
    }

    private static void AppendInsurance(StringBuilder output, IReadOnlyList<LuaMSectorInsurancePayoutEntry> entries, int limit)
    {
        if (entries.Count == 0)
            return;

        output.AppendLine("Insurance:");
        foreach (var entry in entries.TakeLast(limit))
        {
            var state = entry.Paid ? "paid" : "unpaid";
            output.AppendLine($"- {entry.Story}: {entry.Amount} {state} by {entry.Actor} ({entry.Note})");
        }
    }

    private static void AppendBlackBoxes(StringBuilder output, IReadOnlyList<LuaMSectorBlackBoxRecoveryEntry> entries, int limit)
    {
        if (entries.Count == 0)
            return;

        output.AppendLine("Black boxes:");
        foreach (var entry in entries.TakeLast(limit))
        {
            var snapshot = string.IsNullOrWhiteSpace(entry.SourceSnapshot)
                ? string.Empty
                : $" [{entry.SourceSnapshot}]";
            output.AppendLine($"- {entry.Story}: {entry.Actor} ({entry.Note}){snapshot}");
        }
    }

    private static void AppendRegistry(StringBuilder output, string title, IReadOnlyList<LuaMSectorRegistryEntry> entries, int limit)
    {
        if (entries.Count == 0)
            return;

        output.AppendLine($"{title}:");
        foreach (var entry in entries.TakeLast(limit))
        {
            output.AppendLine($"- {entry.Story}: {entry.Actor} ({entry.Note})");
        }
    }
}
