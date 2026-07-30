using Content.Shared.Gateway;
using Robust.Shared.Prototypes;
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
    /// Current state of the asynchronous dungeon generation transaction.
    /// </summary>
    [DataField]
    public GatewayDestinationGenerationState GenerationState;

    /// <summary>
    /// Whether all generated dungeon tiles were validated against the restricted world boundary.
    /// </summary>
    [DataField]
    public bool DungeonBoundsValidated;

    /// <summary>
    /// Earliest time at which a failed destination may be discarded and replaced.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan RetryAt;

    /// <summary>
    /// The creating generator was removed. Occupied orphaned worlds remain until safely empty.
    /// </summary>
    [DataField]
    public bool Orphaned;

    /// <summary>
    /// Profile used to generate this destination.
    /// </summary>
    [DataField]
    public ProtoId<GatewayWorldProfilePrototype> Profile;

    /// <summary>
    /// Stable display address derived from <see cref="Seed"/>.
    /// </summary>
    [DataField]
    public string Address = string.Empty;

    /// <summary>
    /// Return gateway on this generated map.
    /// </summary>
    [DataField]
    public EntityUid Gateway;

    /// <summary>
    /// Time at which this destination was generated. Used to retire unopened maps after their configured TTL.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan GeneratedAt;

    /// <summary>
    /// First time this destination was opened.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan OpenedAt;

    /// <summary>
    /// Earliest time at which an opened destination may enter empty-map retirement.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan RetireAt;

    /// <summary>
    /// Start of the current continuously empty grace period.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan EmptySince;

    [DataField]
    public GatewayDestinationRotationState RotationState;

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

