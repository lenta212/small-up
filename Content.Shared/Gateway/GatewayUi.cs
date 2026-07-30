using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared.Gateway;

[Serializable, NetSerializable]
public enum GatewayVisuals : byte
{
    Active
}

[Serializable, NetSerializable]
public enum GatewayVisualLayers : byte
{
    Portal
}

[Serializable, NetSerializable]
public enum GatewayUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public enum GatewayThreatLevel : byte
{
    Minimal = 1,
    Low,
    Moderate,
    High,
    Extreme,
}

[Serializable, NetSerializable]
public enum GatewayDestinationRotationState : byte
{
    None,
    Scheduled,
    EmptyGracePeriod,
    WaitingForClearance,
}

[Serializable, NetSerializable]
public enum GatewayDestinationGenerationState : byte
{
    Ready,
    Generating,
    Failed,
}

[Serializable, NetSerializable]
public sealed class GatewayBoundUserInterfaceState : BoundUserInterfaceState
{
    /// <summary>
    /// List of enabled destinations and information about them.
    /// </summary>
    public readonly List<GatewayDestinationData> Destinations;

    /// <summary>
    /// Which destination it is currently linked to, if any.
    /// </summary>
    public readonly NetEntity? Current;

    /// <summary>
    /// Next time the portal is ready to be used.
    /// </summary>
    public readonly TimeSpan NextReady;

    public readonly TimeSpan Cooldown;

    /// <summary>
    /// Next time the destination generator unlocks another destination.
    /// </summary>
    public readonly TimeSpan NextUnlock;

    /// <summary>
    /// How long an unlock takes.
    /// </summary>
    public readonly TimeSpan UnlockTime;

    public GatewayBoundUserInterfaceState(List<GatewayDestinationData> destinations,
        NetEntity? current, TimeSpan nextReady, TimeSpan cooldown, TimeSpan nextUnlock, TimeSpan unlockTime)
    {
        Destinations = destinations;
        Current = current;
        NextReady = nextReady;
        Cooldown = cooldown;
        NextUnlock = nextUnlock;
        UnlockTime = unlockTime;
    }
}

[Serializable, NetSerializable]
public record struct GatewayDestinationData
{
    public NetEntity Entity;

    public FormattedMessage Name;

    /// <summary>
    /// Is the portal currently open.
    /// </summary>
    public bool Portal;

    /// <summary>
    /// Is the map the gateway on locked or unlocked.
    /// </summary>
    public bool Locked;

    /// <summary>
    /// Whether this destination has generated-world reconnaissance data.
    /// </summary>
    public bool HasIntel;

    public LocId ProfileName;
    public LocId ProfileDescription;
    public LocId BiomeName;
    public LocId WeatherName;
    public LocId AtmosphereName;
    public LocId Resources;
    public LocId Hostiles;
    public GatewayThreatLevel Threat;
    public Color AccentColor;
    public string Address;
    public bool Loaded;
    public bool Orphaned;
    public GatewayDestinationGenerationState GenerationState;
    public GatewayDestinationRotationState RotationState;
    public TimeSpan RotationAt;
}

[Serializable, NetSerializable]
public sealed class GatewayOpenPortalMessage : BoundUserInterfaceMessage
{
    public NetEntity Destination;

    public GatewayOpenPortalMessage(NetEntity destination)
    {
        Destination = destination;
    }
}
