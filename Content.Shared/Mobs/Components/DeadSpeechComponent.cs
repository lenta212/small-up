namespace Content.Shared.Mobs.Components;

/// <summary>
/// Allows an entity whose body is dead to keep using speech. All other
/// dead-state action blockers remain in force.
/// </summary>
[RegisterComponent]
public sealed partial class DeadSpeechComponent : Component
{
}
