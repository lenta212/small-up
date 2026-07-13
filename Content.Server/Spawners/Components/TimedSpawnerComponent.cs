using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Spawners.Components;

/// <summary>
/// Spawns entities at a set interval.
/// Can configure the set of entities, spawn timing, spawn chance,
/// and min/max number of entities to spawn.
/// </summary>
[RegisterComponent, EntityCategory("Spawner")]
[AutoGenerateComponentPause]
public sealed partial class TimedSpawnerComponent : Component, ISerializationHooks
{
    /// <summary>
    /// List of entities that can be spawned by this component. One will be randomly
    /// chosen for each entity spawned. When multiple entities are spawned at once,
    /// each will be randomly chosen separately.
    /// </summary>
    [DataField]
    public List<EntProtoId> Prototypes = [];

    /// <summary>
    /// Chance of an entity being spawned at the end of each interval.
    /// </summary>
    [DataField]
    public float Chance = 1.0f;

    /// <summary>
    /// Length of the interval between spawn attempts.
    /// </summary>
    [DataField]
    public TimeSpan IntervalSeconds = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The minimum number of entities that can be spawned when an interval elapses.
    /// </summary>
    [DataField]
    public int MinimumEntitiesSpawned = 1;

    /// <summary>
    /// The maximum number of entities that can be spawned when an interval elapses.
    /// </summary>
    [DataField]
    public int MaximumEntitiesSpawned = 1;

    /// <summary>
    /// Optional lifetime budget for this spawner. Once this many entities have been
    /// created, later timer firings are ignored. Null keeps the legacy unlimited behavior.
    /// </summary>
    [DataField]
    public int? MaximumTotalSpawns;

    /// <summary>
    /// Number of entities successfully created by this spawner so far.
    /// Serialized so a persistent map reload cannot reset a finite spawn budget.
    /// </summary>
    [DataField]
    public int TotalSpawned;

    /// <summary>
    /// The time at which the current interval will have elapsed and entities may be spawned.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextFire = TimeSpan.Zero;

    void ISerializationHooks.AfterDeserialization()
    {
        if (MinimumEntitiesSpawned > MaximumEntitiesSpawned)
            throw new ArgumentException("MaximumEntitiesSpawned can't be lower than MinimumEntitiesSpawned!");

        if (MaximumTotalSpawns is < 0)
            throw new ArgumentException("MaximumTotalSpawns can't be negative!");

        if (TotalSpawned < 0)
            throw new ArgumentException("TotalSpawned can't be negative!");
    }
}
