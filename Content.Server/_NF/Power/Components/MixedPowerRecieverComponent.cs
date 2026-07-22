using Content.Server.Power.Components;
using Content.Server.Power.NodeGroups;

namespace Content.Server._NF.Power.Components;

/// <summary>
/// Marks an entity as capable of using both APC and battery power.
/// </summary>
[RegisterComponent]
public sealed partial class MixedPowerReceiverComponent : Component
{
}
