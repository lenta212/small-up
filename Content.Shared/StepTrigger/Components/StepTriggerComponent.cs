using Content.Shared.StepTrigger.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.StepTrigger.Components;

[Prototype("stepTriggerType")]
public sealed partial class StepTriggerTypePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;
}

[DataDefinition]
public sealed partial class StepTriggerGroup
{
    [DataField]
    public List<ProtoId<StepTriggerTypePrototype>> Types = new();

    public bool IsValid(StepTriggerComponent component)
    {
        // Backwards-compatible bridge for Goob enchant data. This codebase's
        // StepTriggerComponent does not carry typed trigger groups, so the only
        // currently meaningful imported group is Lava: lava tiles are identified
        // by their zero-speed, low-intersection trigger configuration.
        return Types.Contains("Lava")
            && component.RequiredTriggeredSpeed == 0f
            && component.IntersectRatio <= 0.1f;
    }
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(StepTriggerSystem))]
public sealed partial class StepTriggerComponent : Component
{
    /// <summary>
    ///     List of entities that are currently colliding with the entity.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public HashSet<EntityUid> Colliding = new();

    /// <summary>
    ///     The list of entities that are standing on this entity,
    /// which shouldn't be able to trigger it again until stepping off.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public HashSet<EntityUid> CurrentlySteppedOn = new();

    /// <summary>
    ///     Whether or not this component will currently try to trigger for entities.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Active = true;

    /// <summary>
    ///     Ratio of shape intersection for a trigger to occur.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float IntersectRatio = 0.3f;

    /// <summary>
    ///     Entities will only be triggered if their speed exceeds this limit.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float RequiredTriggeredSpeed = 3.5f;

    /// <summary>
    ///     If any entities occupy the blacklist on the same tile then steptrigger won't work.
    /// </summary>
    [DataField]
    public EntityWhitelist? Blacklist;

    /// <summary>
    ///     If this is true, steptrigger will still occur on entities that are in air / weightless. They do not
    ///     by default.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool IgnoreWeightless;

    /// <summary>
    /// Does this have separate "StepOn" and "StepOff" triggers.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool StepOn = false;
}

[RegisterComponent]
[Access(typeof(StepTriggerSystem))]
public sealed partial class StepTriggerActiveComponent : Component
{

}
