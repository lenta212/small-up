using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content.Server.Administration;
using Content.Server._LuaM.Animals;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Administration;

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAnimalPopulationCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_animal_population";
    public string Description => "Reports capped animal and known pest population by map and species.";
    public string Help => $"Usage: {Command} [mapId]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1 ||
            args.Length == 1 && !int.TryParse(args[0], out _))
        {
            shell.WriteError(Help);
            return;
        }

        var population = _entities.System<LuaMAnimalPopulationSystem>();
        IReadOnlyList<LuaMAnimalPopulationReport> reports;
        if (args.Length == 1)
        {
            var mapId = new MapId(int.Parse(args[0]));
            reports = [population.GetPopulationReport(mapId)];
        }
        else
        {
            reports = population.GetPopulationReports();
        }

        if (reports.Count == 0)
        {
            shell.WriteLine("No tracked animal populations found.");
            return;
        }

        var output = new StringBuilder("LuaM animal population:\n");
        foreach (var report in reports)
        {
            var species = report.Species.Count == 0
                ? "none"
                : string.Join(", ", report.Species
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
            var state = report.Overflow > 0 ? $"OVER +{report.Overflow}" : "OK";
            output.AppendLine(
                $"- map {report.MapId}: {report.Total}/{report.Limit} [{state}], " +
                $"safe pest candidates={report.CleanupEligible}; {species}");
        }

        shell.WriteLine(output.ToString().TrimEnd());
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMAnimalCleanupCommand : IConsoleCommand
{
    private const string ConfirmFlag = "--confirm";

    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_animal_cleanup";
    public string Description => "Previews or confirms conservative cleanup of known pest overflow.";
    public string Help =>
        $"Usage: {Command} <mapId> [keep] [{ConfirmFlag} <previewFingerprint>]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var mapIdValue))
        {
            shell.WriteError(Help);
            return;
        }

        var population = _entities.System<LuaMAnimalPopulationSystem>();
        var keep = population.GetPopulationLimit();
        var index = 1;
        if (index < args.Length && !args[index].Equals(ConfirmFlag, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args[index], out keep) || keep < 0)
            {
                shell.WriteError("keep must be a non-negative integer.");
                return;
            }

            index++;
        }

        var confirmed = false;
        var fingerprint = string.Empty;
        if (index < args.Length)
        {
            if (args.Length != index + 2 ||
                !args[index].Equals(ConfirmFlag, StringComparison.OrdinalIgnoreCase))
            {
                shell.WriteError(Help);
                return;
            }

            confirmed = true;
            fingerprint = args[index + 1];
        }

        var mapId = new MapId(mapIdValue);
        if (!confirmed)
        {
            WritePreview(shell, population.BuildCleanupPreview(mapId, keep));
            return;
        }

        var actor = shell.Player?.Name ?? "server-console";
        if (!population.TryExecuteCleanup(mapId, keep, fingerprint, actor, out var result, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(
            $"LuaM animal cleanup queued {result.QueuedForDeletion} safe pest entities on map " +
            $"{mapId}; projected population={result.PopulationAfterCleanup}.");
    }

    private void WritePreview(IConsoleShell shell, LuaMAnimalCleanupPreview preview)
    {
        shell.WriteLine(
            $"PREVIEW ONLY: map {preview.MapId}, population={preview.TotalPopulation}, " +
            $"overflow above keep {preview.Keep}={preview.Overflow}, " +
            $"safe candidates={preview.SafeCandidates}, would remove={preview.SelectedEntities.Count}, " +
            $"projected population={preview.PopulationAfterCleanup}.");

        if (preview.SelectedEntities.Count == 0)
        {
            shell.WriteLine("Nothing is eligible for conservative cleanup.");
            return;
        }

        shell.WriteLine(
            "Only exact allowlisted pests without a player, mind, custom name, container, critical state, " +
            "nearby player, friendship/companion state, or husbandry role are eligible; at least two of every " +
            "pest species are preserved.");
        shell.WriteLine(
            $"Confirm this unchanged snapshot with: `{Command} {preview.MapId} {preview.Keep} " +
            $"{ConfirmFlag} {preview.Fingerprint}`");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("map id"),
            2 => CompletionResult.FromHintOptions([ConfirmFlag], "population to keep or confirmation flag"),
            3 => CompletionResult.FromHintOptions([ConfirmFlag], "confirmation flag"),
            _ => CompletionResult.Empty,
        };
    }
}
