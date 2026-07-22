using System.Numerics;
using Robust.Shared.Serialization;
using Robust.Shared.GameObjects;

namespace Content.Shared._NF.Shipyard.BUI;

[NetSerializable, Serializable]
public sealed class ShipyardConsoleInterfaceState : BoundUserInterfaceState
{
    public int Balance;
    public readonly bool AccessGranted;
    public readonly string? ShipDeedTitle;
    public int ShipSellValue;
    public readonly bool IsTargetIdPresent;
    public readonly NetEntity? TargetCard;
    public readonly byte UiKey;

    public readonly (List<string> available, List<string> unavailable) ShipyardPrototypes;
    public readonly string ShipyardName;
    public readonly bool FreeListings;
    public readonly float SellRate;
    public readonly List<ShipyardGateInfo> Gates;
    public readonly NetEntity? StationGrid;
    public readonly bool ShipParked;
    public readonly List<ShipyardStoredShipInfo> StoredShips;
    public readonly Guid? BoundShipId;
    public readonly bool IsForeignDeed;
    public readonly bool CanSell;
    public readonly bool CanRename;
    public readonly bool CanUnassign;
    public readonly bool CanPark;
    public readonly bool CanCall;
    public readonly bool DeedMutationInFlight;

    public ShipyardConsoleInterfaceState(
        int balance,
        bool accessGranted,
        string? shipDeedTitle,
        int shipSellValue,
        bool isTargetIdPresent,
        NetEntity? targetCard,
        byte uiKey,
        (List<string> available, List<string> unavailable) shipyardPrototypes,
        string shipyardName,
        bool freeListings,
        float sellRate,
        List<ShipyardGateInfo>? gates = null,
        NetEntity? stationGrid = null,
        bool shipParked = false,
        List<ShipyardStoredShipInfo>? storedShips = null,
        Guid? boundShipId = null,
        bool isForeignDeed = false,
        bool canSell = false,
        bool canRename = false,
        bool canUnassign = false,
        bool canPark = false,
        bool canCall = false,
        bool deedMutationInFlight = false)
    {
        Balance = balance;
        AccessGranted = accessGranted;
        ShipDeedTitle = shipDeedTitle;
        ShipSellValue = shipSellValue;
        IsTargetIdPresent = isTargetIdPresent;
        TargetCard = targetCard;
        UiKey = uiKey;
        ShipyardPrototypes = shipyardPrototypes;
        ShipyardName = shipyardName;
        FreeListings = freeListings;
        SellRate = sellRate;
        Gates = gates ?? new();
        StationGrid = stationGrid;
        ShipParked = shipParked;
        StoredShips = storedShips ?? new();
        BoundShipId = boundShipId;
        IsForeignDeed = isForeignDeed;
        CanSell = canSell;
        CanRename = canRename;
        CanUnassign = canUnassign;
        CanPark = canPark;
        CanCall = canCall;
        DeedMutationInFlight = deedMutationInFlight;
    }
}

[NetSerializable, Serializable]
public sealed record ShipyardGateInfo(NetEntity Entity, string Name, bool Available, Vector2 Position);

[NetSerializable, Serializable]
public sealed record ShipyardStoredShipInfo(Guid ShipId, string DisplayName, string VesselPrototypeId);

/// <summary>
/// Actor-targeted shipyard state. Ship ownership, deed authorization and bank
/// balances must never be stored in the globally replicated BUI component state.
/// </summary>
[NetSerializable, Serializable]
public sealed class ShipyardConsoleStateMessage : BoundUserInterfaceMessage
{
    public ShipyardConsoleInterfaceState State;

    public ShipyardConsoleStateMessage(ShipyardConsoleInterfaceState state)
    {
        State = state;
    }
}
