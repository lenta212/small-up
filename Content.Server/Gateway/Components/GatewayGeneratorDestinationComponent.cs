using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Gateway.Components;

/// <summary>
/// Destination created by <see cref="GatewayGeneratorComponent"/>
/// </summary>
[RegisterComponent]
public sealed partial class GatewayGeneratorDestinationComponent : Component
{
    /// <summary>
    /// Generator that created this destination.
    /// </summary>
    [DataField]
    public EntityUid Generator;

    /// <summary>
    /// Is the map locked from being used still or unlocked.
    /// Used in conjunction with the attached generator's NextUnlock.
    /// </summary>
    [DataField]
    public bool Locked = true;

    [DataField]
    public bool Loaded;

    /// <summary>
    /// Time at which this destination was generated. Used to retire unopened maps after their configured TTL.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan GeneratedAt;

    /// <summary>
    /// Seed used for this destination.
    /// </summary>
    [DataField]
    public int Seed;

    /// <summary>
    /// Origin of the gateway.
    /// </summary>
    [DataField]
    public Vector2i Origin;
}

