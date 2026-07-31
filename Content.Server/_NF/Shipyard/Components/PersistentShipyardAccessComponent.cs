using Content.Shared.Access;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Shipyard.Components;

/// <summary>
/// Authoritative access levels granted by the shipyard that sold this vessel.
/// Stored on the grid so persistent snapshots restore the original grant rather
/// than deriving privileges from whichever console later calls the ship.
/// </summary>
[RegisterComponent]
public sealed partial class PersistentShipyardAccessComponent : Component
{
    [DataField]
    public HashSet<ProtoId<AccessLevelPrototype>> GrantedLevels = new();
}
