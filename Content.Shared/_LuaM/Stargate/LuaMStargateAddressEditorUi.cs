using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Stargate;

[Serializable, NetSerializable]
public enum LuaMStargateAddressEditorUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorUiState(
    byte[] currentInput,
    int addressLength,
    byte maxSymbols,
    byte[][]? leftDiskAddresses,
    byte[][]? rightDiskAddresses) : BoundUserInterfaceState
{
    public readonly byte[] CurrentInput = currentInput;
    public readonly int AddressLength = addressLength;
    public readonly byte MaxSymbols = maxSymbols;
    public readonly byte[][]? LeftDiskAddresses = leftDiskAddresses;
    public readonly byte[][]? RightDiskAddresses = rightDiskAddresses;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorInputMessage(byte symbol) : BoundUserInterfaceMessage
{
    public readonly byte Symbol = symbol;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorClearMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorSaveLeftMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorSaveRightMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorDeleteLeftMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorDeleteRightMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorMoveLeftToRightMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorMoveRightToLeftMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorCopyLeftToRightMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorCopyRightToLeftMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorCloneLeftToRightMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateAddressEditorCloneRightToLeftMessage : BoundUserInterfaceMessage;
