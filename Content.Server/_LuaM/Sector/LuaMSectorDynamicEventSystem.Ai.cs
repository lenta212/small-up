using System.Linq;
using System.Text;
using Content.Shared._LuaM.Sector;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Sector;

public sealed class LuaMSectorAiEventProposal
{
    public string TemplateId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Vessel { get; set; } = string.Empty;
    public int Reward { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Hazard { get; set; } = string.Empty;
    public string ReputationTarget { get; set; } = string.Empty;
    public int ReputationDelta { get; set; }
    public string Briefing { get; set; } = string.Empty;
}

public sealed partial class LuaMSectorDynamicEventSystem
{
    public bool TryGenerateDynamicEventFromAiProposal(
        LuaMSectorAiEventProposal proposal,
        string actor,
        out LuaMSectorStoryRecord? record,
        out string error,
        bool ignoreOpenRuntimeLead = false,
        bool ignorePlayerGate = false,
        MapCoordinates? markerCoordinates = null)
    {
        record = null;
        error = string.Empty;

        var playerCount = CountActivePlayers();
        if (!ignorePlayerGate && playerCount <= 0)
        {
            error = "Нет активных игроков для динамического события сектора LuaM.";
            return false;
        }

        var status = _stories.GetStatusSnapshot();
        if (!CanCreateDynamicEvent(status, out error))
            return false;

        if (!ignoreOpenRuntimeLead && HasOpenRuntimeLead(status))
        {
            error = "Открытая активная зацепка сектора LuaM уже существует.";
            return false;
        }

        var template = Templates.FirstOrDefault(template =>
            template.Id.Equals(proposal.TemplateId, StringComparison.OrdinalIgnoreCase));
        if (template == null)
        {
            error = $"AI предложил запрещенный шаблон динамического события LuaM: {proposal.TemplateId}";
            return false;
        }

        if (!TryResolveMarkerCoordinates(markerCoordinates, out var resolvedMarkerCoordinates, out error))
            return false;
        var markerLocation = FormatMarkerLocation(resolvedMarkerCoordinates);
        var debrisPlan = BuildDebrisSitePlan(resolvedMarkerCoordinates, false);
        var conditionRewardBonus = GetConditionRewardBonus(status);
        var queuedRouteCalibrationSource = GetQueuedRouteCalibrationSource();
        var routeCalibrationChainDepth = GetRouteCalibrationChainDepth(queuedRouteCalibrationSource);
        var routeCalibrationRewardBonus = GetRouteCalibrationRewardBonus(queuedRouteCalibrationSource);
        var routeCalibrationClosureRewardBonus = GetRouteCalibrationClosureRewardBonus(queuedRouteCalibrationSource);
        var conditionRiskSummary = BuildConditionRiskSummary(status, conditionRewardBonus);
        var conditionSeverity = GetPrimaryConditionSeverity(status);

        var title = SelectRussianText(proposal.Title, template.Title);
        var vessel = string.IsNullOrWhiteSpace(proposal.Vessel)
            ? template.Vessel
            : proposal.Vessel.Trim();
        var description = BuildAiDescription(
            SelectRussianText(proposal.Description, template.Description),
            markerLocation,
            conditionRewardBonus,
            routeCalibrationRewardBonus,
            routeCalibrationClosureRewardBonus,
            routeCalibrationChainDepth,
            status);
        var hazard = SelectRussianText(proposal.Hazard, template.Hazard);
        if (routeCalibrationClosureRewardBonus > 0)
            hazard = $"Route calibration closure bonus: +{routeCalibrationClosureRewardBonus}; chain depth {routeCalibrationChainDepth}. {hazard}";
        if (routeCalibrationRewardBonus > 0)
            hazard = $"Route calibration handoff bonus: +{routeCalibrationRewardBonus}; chain depth {routeCalibrationChainDepth}. {hazard}";
        hazard = AppendDebrisHazardContext(hazard, debrisPlan);

        var reputationTarget = SelectReputationTarget(proposal.ReputationTarget, template.ReputationTarget);
        var reputationDelta = proposal.ReputationDelta == 0
            ? template.ReputationDelta
            : Math.Clamp(proposal.ReputationDelta, -3, 3);
        var reward = Math.Clamp(
            (proposal.Reward <= 0 ? template.Reward : proposal.Reward) +
            conditionRewardBonus +
            routeCalibrationRewardBonus,
            DynamicRewardMin,
            DynamicRewardMax);

        if (!_stories.TrySeedDistressStory(
                title,
                vessel,
                reward,
                description,
                hazard,
                reputationTarget,
                reputationDelta,
                actor,
                out record,
                out error,
                hazardRewardBonusExtra: routeCalibrationClosureRewardBonus))
        {
            return false;
        }

        SpawnDebrisSite(template, record, actor, resolvedMarkerCoordinates, markerLocation, debrisPlan);
        var routeCalibrationApplied = SpawnWorldMarker(
            template,
            record,
            actor,
            resolvedMarkerCoordinates,
            markerLocation,
            conditionRiskSummary,
            conditionSeverity,
            status,
            true,
            out var routeCalibrationSource,
            out var markerUid);
        if (record != null)
            _stories.TryBindContractRouteTarget(record.Story, markerUid);
        SpawnSiteNote(template, record, actor, resolvedMarkerCoordinates, markerLocation, conditionRiskSummary, routeCalibrationSource);
        SpawnSiteObjects(template, record, actor, resolvedMarkerCoordinates, conditionRiskSummary, routeCalibrationSource);
        ScheduleNextAutomaticEvent();
        return true;
    }

    private static string SelectRussianText(string value, string fallback)
    {
        value = value.Trim();
        return HasCyrillic(value) ? value : fallback;
    }

    private static bool HasCyrillic(string value)
    {
        return value.Any(character => character >= '\u0400' && character <= '\u04FF');
    }

    private static string SelectReputationTarget(string value, string fallback)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value is "Distress" or "Salvage" or "Research" or "Station Records" or "Trade"
            ? value
            : fallback;
    }

    private static string BuildAiDescription(
        string description,
        string markerLocation,
        int conditionRewardBonus,
        int routeCalibrationRewardBonus,
        int routeCalibrationClosureRewardBonus,
        int routeCalibrationChainDepth,
        LuaMSectorStatusSnapshot status)
    {
        var output = new StringBuilder();
        output.Append($"Координаты маркера: {markerLocation}.");
        if (routeCalibrationRewardBonus > 0)
            output.Append($" Route calibration handoff bonus: +{routeCalibrationRewardBonus}; chain depth {routeCalibrationChainDepth}.");
        if (routeCalibrationClosureRewardBonus > 0)
            output.Append($" Route calibration closure bonus: +{routeCalibrationClosureRewardBonus}; chain depth {routeCalibrationChainDepth}.");
        if (!string.IsNullOrWhiteSpace(description))
            output.Append($" {description.Trim()}");

        var activeConditions = status.Conditions
            .Where(condition => condition.Active)
            .OrderByDescending(condition => condition.Severity)
            .ThenBy(condition => condition.Title)
            .ToList();

        if (activeConditions.Count > 0)
        {
            var conditionText = string.Join(", ", activeConditions
                .Select(condition => $"SC-{condition.Severity} {condition.Title}"));
            output.Append($" AI учел активные условия сектора: {conditionText}.");
        }

        if (conditionRewardBonus > 0)
            output.Append($" Надбавка за условия сектора: +{conditionRewardBonus}.");

        return output.ToString();
    }
}
