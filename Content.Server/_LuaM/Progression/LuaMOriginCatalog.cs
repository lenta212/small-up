using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Content.Server._LuaM.Progression;

public sealed record LuaMOriginDefinition(
    string Id,
    IReadOnlyDictionary<LuaMCharacterParameter, int> Modifiers,
    string KnowledgeTag,
    string ContactTag,
    string LimitationTag);

/// <summary>
/// Closed server-side origin catalog. Origins only reshape the initial forty
/// parameter points and expose narrative tags; they never grant money, access,
/// equipment, XP, mastery, reputation, or licenses.
/// </summary>
public static class LuaMOriginCatalog
{
    private static readonly LuaMCharacterParameters Baseline = new(5, 5, 5, 5, 5, 5, 5, 5);

    public static IReadOnlyList<LuaMOriginDefinition> All { get; } = new[]
    {
        Origin("OriginShipborn", "ship-layout", "ship-quartermaster", "ground-hazard-orientation",
            (LuaMCharacterParameter.Agility, 1), (LuaMCharacterParameter.Perception, 1),
            (LuaMCharacterParameter.Strength, -1), (LuaMCharacterParameter.Charisma, -1)),
        Origin("OriginFrontierColonist", "frontier-hazards", "settlement-coordinator", "corporate-terminal-orientation",
            (LuaMCharacterParameter.Endurance, 1), (LuaMCharacterParameter.Willpower, 1),
            (LuaMCharacterParameter.Intelligence, -1), (LuaMCharacterParameter.Luck, -1)),
        Origin("OriginCorporateWard", "corporate-compliance", "corporate-registrar", "accounted-trades-only",
            (LuaMCharacterParameter.Intelligence, 1), (LuaMCharacterParameter.Charisma, 1),
            (LuaMCharacterParameter.Endurance, -1), (LuaMCharacterParameter.Willpower, -1)),
        Origin("OriginFleetAuxiliary", "fleet-emergency-protocol", "veteran-dispatcher", "official-service-history",
            (LuaMCharacterParameter.Perception, 1), (LuaMCharacterParameter.Willpower, 1),
            (LuaMCharacterParameter.Charisma, -1), (LuaMCharacterParameter.Luck, -1)),
        Origin("OriginIndustrialStation", "industrial-lockout", "shift-supervisor", "experimental-equipment-orientation",
            (LuaMCharacterParameter.Strength, 1), (LuaMCharacterParameter.Endurance, 1),
            (LuaMCharacterParameter.Agility, -1), (LuaMCharacterParameter.Intelligence, -1)),
        Origin("OriginResearchHabitat", "clean-lab-protocol", "laboratory-curator", "heavy-equipment-orientation",
            (LuaMCharacterParameter.Intelligence, 1), (LuaMCharacterParameter.Perception, 1),
            (LuaMCharacterParameter.Strength, -1), (LuaMCharacterParameter.Endurance, -1)),
        Origin("OriginReliefFleet", "relief-triage", "rescue-coordinator", "controlled-material-registration",
            (LuaMCharacterParameter.Intelligence, 1), (LuaMCharacterParameter.Willpower, 1),
            (LuaMCharacterParameter.Strength, -1), (LuaMCharacterParameter.Luck, -1)),
        Origin("OriginMerchantConvoy", "customs-and-routes", "trade-house-factor", "security-contract-reference",
            (LuaMCharacterParameter.Charisma, 1), (LuaMCharacterParameter.Luck, 1),
            (LuaMCharacterParameter.Endurance, -1), (LuaMCharacterParameter.Willpower, -1)),
        Origin("OriginSalvageFlotilla", "salvage-rights", "salvage-broker", "corporate-property-verification",
            (LuaMCharacterParameter.Strength, 1), (LuaMCharacterParameter.Luck, 1),
            (LuaMCharacterParameter.Intelligence, -1), (LuaMCharacterParameter.Charisma, -1)),
        Origin("OriginAgriculturalHabitat", "habitat-ecosystems", "supply-agronomist", "eva-qualification",
            (LuaMCharacterParameter.Endurance, 1), (LuaMCharacterParameter.Intelligence, 1),
            (LuaMCharacterParameter.Agility, -1), (LuaMCharacterParameter.Perception, -1)),
        Origin("OriginResettled", "shelter-services", "community-mediator", "first-license-document-review",
            (LuaMCharacterParameter.Agility, 1), (LuaMCharacterParameter.Willpower, 1),
            (LuaMCharacterParameter.Strength, -1), (LuaMCharacterParameter.Intelligence, -1)),
        Origin("OriginRehabilitation", "maintenance-inventory", "reintegration-inspector", "security-command-sponsor",
            (LuaMCharacterParameter.Strength, 1), (LuaMCharacterParameter.Willpower, 1),
            (LuaMCharacterParameter.Charisma, -1), (LuaMCharacterParameter.Luck, -1)),
    };

    static LuaMOriginCatalog()
    {
        if (All.Select(origin => origin.Id).Distinct(StringComparer.Ordinal).Count() != All.Count)
            throw new InvalidOperationException("LuaM origin IDs must be unique.");

        foreach (var origin in All)
        {
            if (string.IsNullOrWhiteSpace(origin.Id) ||
                string.IsNullOrWhiteSpace(origin.KnowledgeTag) ||
                string.IsNullOrWhiteSpace(origin.ContactTag) ||
                string.IsNullOrWhiteSpace(origin.LimitationTag) ||
                !LuaMCharacterBuildRules.TryApplyOrigin(Baseline, origin.Modifiers, out _))
            {
                throw new InvalidOperationException($"Invalid LuaM origin definition: {origin.Id}");
            }
        }
    }

    public static bool TryGet(string id, out LuaMOriginDefinition? origin)
    {
        origin = All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return origin != null;
    }

    private static LuaMOriginDefinition Origin(
        string id,
        string knowledgeTag,
        string contactTag,
        string limitationTag,
        params (LuaMCharacterParameter Parameter, int Modifier)[] modifiers)
    {
        var dictionary = modifiers.ToDictionary(pair => pair.Parameter, pair => pair.Modifier);
        return new LuaMOriginDefinition(
            id,
            new ReadOnlyDictionary<LuaMCharacterParameter, int>(dictionary),
            knowledgeTag,
            contactTag,
            limitationTag);
    }
}
