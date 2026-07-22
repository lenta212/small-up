using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.Components;

/// <summary>
/// Component that handles locking shuttle consoles.
/// </summary>
[RegisterComponent, NetworkedComponent]
[Access(typeof(SharedShuttleConsoleLockSystem))]
public sealed partial class ShuttleConsoleLockComponent : Component
{
    /// <summary>
    /// Whether the console is currently locked
    /// </summary>
    [DataField]
    public bool Locked = true;

    /// <summary>
    /// Stable persistent ship GUID when one is available. Legacy, nonpersistent
    /// ships may still use their runtime entity UID string.
    /// </summary>
    [DataField]
    public string? ShuttleId;
}

[Serializable, NetSerializable]
public enum ShuttleConsoleLockVisuals : byte
{
    Locked,
}
