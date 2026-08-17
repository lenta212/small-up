using Content.Server.Objectives.Systems;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.List;

/// <summary>
/// Requires that the player not have a certain job to have this objective.
/// </summary>
[RegisterComponent, Access(typeof(NotJobRequirementSystem))]
public sealed partial class NotJobRequirementComponent : Component
{
    /// <summary>
    /// ID of the job to ban from having this objective.
    /// </summary>
    [DataField(customTypeSerializer: typeof(PrototypeIdSerializer<JobPrototype>))]
    public string Job = string.Empty;

    /// <summary>
    /// Compatibility with imported Goobstation objectives that use a list field named "jobs".
    /// The local objective logic only needs to reject if the mind has any listed job.
    /// </summary>
    [DataField("jobs", customTypeSerializer: typeof(PrototypeIdListSerializer<JobPrototype>))]
    public List<string> Jobs = new();
}
