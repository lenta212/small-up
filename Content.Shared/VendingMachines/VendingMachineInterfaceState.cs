using Robust.Shared.Serialization;

namespace Content.Shared.VendingMachines
{
    [Serializable, NetSerializable]
    public sealed class VendingMachineEjectMessage : BoundUserInterfaceMessage
    {
        public readonly InventoryType Type;
        public readonly string ID;
        public readonly int Quantity;
        public readonly int ExpectedTotalPrice;

        public VendingMachineEjectMessage(
            InventoryType type,
            string id,
            int quantity = 1,
            int expectedTotalPrice = -1)
        {
            Type = type;
            ID = id;
            Quantity = quantity;
            ExpectedTotalPrice = expectedTotalPrice;
        }
    }

    /// <summary>
    /// Server-authoritative unit prices for the vending machine's current pricing context.
    /// The client multiplies these quotes by the selected quantity, and sends the shown
    /// total back as a purchase precondition.
    /// </summary>
    [Serializable, NetSerializable]
    public sealed class VendingMachineBoundUserInterfaceState : BoundUserInterfaceState
    {
        public readonly Dictionary<string, int> UnitPrices;
        public readonly bool RequiresCash;

        public VendingMachineBoundUserInterfaceState(
            Dictionary<string, int> unitPrices,
            bool requiresCash)
        {
            UnitPrices = unitPrices;
            RequiresCash = requiresCash;
        }
    }

    [Serializable, NetSerializable]
    public enum VendingMachineUiKey
    {
        Key,
    }
}
