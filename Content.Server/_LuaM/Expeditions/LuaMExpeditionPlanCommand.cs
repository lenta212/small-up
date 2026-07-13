using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._LuaM.Expeditions;

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMExpeditionPlanCommand : IConsoleCommand
{
    public string Command => "luam_expedition_plan";
    public string Description => "Builds a server-only deterministic expedition plan summary.";
    public string Help => $"Usage: {Command} <campaignId> <expeditionId> <seed> [version] [width] [height] [regionSize]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 3 or > 7)
        {
            shell.WriteError(Help);
            return;
        }

        if (!ulong.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
        {
            shell.WriteError("Seed must be an unsigned decimal integer.");
            return;
        }

        if (!TryParseOptional(args, 3, LuaMExpeditionPlanner.CurrentGeneratorVersion, out var version) ||
            !TryParseOptional(args, 4, LuaMExpeditionPlanner.DefaultWidth, out var width) ||
            !TryParseOptional(args, 5, LuaMExpeditionPlanner.DefaultHeight, out var height) ||
            !TryParseOptional(args, 6, LuaMExpeditionPlanner.DefaultMacroRegionSize, out var regionSize))
        {
            shell.WriteError("Version, width, height and region size must be integers.");
            return;
        }

        try
        {
            var plan = new LuaMExpeditionPlanner().Build(args[0], args[1], seed, version, width, height, regionSize);
            var requiredSites = plan.Sites.Count(site => site.Kind != LuaMExpeditionSiteKind.Secondary);
            var output = new StringBuilder()
                .AppendLine($"PlanHash: {plan.PlanHash}")
                .AppendLine($"Bounds: {plan.Bounds.Width}x{plan.Bounds.Height}")
                .AppendLine($"Regions: {plan.Regions.Count}")
                .AppendLine($"Sites: {plan.Sites.Count} ({requiredSites} required)")
                .AppendLine($"Connections: {plan.Connections.Count}")
                .AppendLine($"GeneratorVersion: {plan.GeneratorVersion}")
                .AppendLine("Required route: entry -> objective -> extraction");

            shell.WriteLine(output.ToString().TrimEnd());
        }
        catch (ArgumentException exception)
        {
            shell.WriteError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            shell.WriteError(exception.Message);
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.FromHint(Help);
    }

    private static bool TryParseOptional(string[] args, int index, int defaultValue, out int value)
    {
        if (args.Length <= index)
        {
            value = defaultValue;
            return true;
        }

        return int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
