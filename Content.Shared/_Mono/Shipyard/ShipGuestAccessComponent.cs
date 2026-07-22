using Robust.Shared.GameStates;

namespace Content.Shared._Mono.Shipyard;

/// <summary>
/// Component that tracks round-local guest access to a ship. Entity references
/// are networked for live access checks but deliberately excluded from map
/// serialization because they are not durable ship ownership data.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ShipGuestAccessComponent : Component
{
    /// <summary>
    /// Set of ID card EntityUids that have been granted guest access to this ship.
    /// </summary>
    [AutoNetworkedField]
    public HashSet<EntityUid> GuestIdCards = new();

    /// <summary>
    /// Set of cyborg EntityUids that have been granted guest access to this ship.
    /// </summary>
    [AutoNetworkedField]
    public HashSet<EntityUid> GuestCyborgs = new();
}
