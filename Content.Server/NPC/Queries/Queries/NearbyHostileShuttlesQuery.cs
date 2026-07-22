using Content.Server.NPC.Systems;
using Content.Shared._Mono.Company;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

// Mono - whole file

namespace Content.Server.NPC.Queries.Queries;

/// <summary>
/// Returns nearby entities tagged with ShipNpcTargetComponent.
/// </summary>
public sealed partial class NearbyNpcTargetsQuery : UtilityQuery
{
    [DataField]
    public float Range = 4000f;

    /// <summary>
    /// If set, only targets carried by an entity or grid affiliated with one of these companies are returned.
    /// </summary>
    [DataField]
    public HashSet<ProtoId<CompanyPrototype>> TargetCompanies = new();

    [DataField]
    public EntityWhitelist Blacklist = new();
}
