using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._LuaM.Species;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LuaMDionaRootingComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Rooted;

    [DataField]
    public float MinimumNutrition = 50f;

    [DataField]
    public float NutritionCost = 8f;

    [DataField]
    public DamageSpecifier Healing = new();

    [DataField]
    public TimeSpan Interval = TimeSpan.FromSeconds(2);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextHeal;
}

public sealed partial class LuaMDionaToggleRootingActionEvent : InstantActionEvent;
