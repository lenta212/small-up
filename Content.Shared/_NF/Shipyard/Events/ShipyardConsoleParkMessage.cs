using Robust.Shared.Serialization;
using Robust.Shared.GameObjects;

namespace Content.Shared._NF.Shipyard.Events;

[Serializable, NetSerializable]
public sealed class ShipyardConsoleParkMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ShipyardConsoleCallMessage : BoundUserInterfaceMessage
{
    public Guid ShipId;
    public NetEntity Gate;

    public ShipyardConsoleCallMessage(Guid shipId, NetEntity gate)
    {
        ShipId = shipId;
        Gate = gate;
    }
}
