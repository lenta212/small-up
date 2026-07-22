using Content.Shared._LuaM.NPC;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.NPC;

/// <summary>
/// Opt-in generic lifecycle carrier for sector NPCs. A role-specific executor
/// reads the active intent and reports attempts/progress/completion through
/// <see cref="LuaMNpcActivityLifecycleSystem"/>.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMNpcActivityComponent : Component
{
    [DataField(required: true)]
    public ProtoId<LuaMNpcRoleActivityPrototype> RoleProfile = string.Empty;

    [DataField]
    public LuaMNpcActivityState Context = new();

    [DataField]
    public string LastLifecycleStatus = "none";
}
