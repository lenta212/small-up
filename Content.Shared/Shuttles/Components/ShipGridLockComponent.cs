using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared.Shuttles.Components;

/// <summary>
/// Component that handles grid-level locking for ships with deeds.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedShuttleConsoleLockSystem))]
public sealed partial class ShipGridLockComponent : Component
{
    /// <summary>
    /// Whether the ship grid is currently locked.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Locked = true;

    /// <summary>
    /// Stable persistent ship GUID when one is available. Legacy, nonpersistent
    /// ships may still use their runtime entity UID string.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? ShuttleId;
}
