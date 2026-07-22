using Robust.Shared.Serialization;
using Robust.Shared.GameObjects;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
///     Purchase a Vessel from the console
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsolePurchaseMessage : BoundUserInterfaceMessage
{
    public string Vessel; //vessel prototype ID
    public NetEntity Gate;

    public ShipyardConsolePurchaseMessage(string vessel, NetEntity gate)
    {
        Vessel = vessel;
        Gate = gate;
    }
}
