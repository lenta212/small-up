using Robust.Shared.Serialization;
using System.Linq;

namespace Content.Shared._LuaM.Stargate;

public static class LuaMStargateGlyphs
{
    public const string Charmap = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn";
    public const int Count = 40;

    public static char GetChar(byte symbol) =>
        symbol is >= 1 and <= Count ? Charmap[symbol - 1] : '?';

    public static byte GetByte(char symbol)
    {
        var index = Charmap.IndexOf(symbol);
        return index < 0 ? (byte) 0 : (byte) (index + 1);
    }

    public static byte[]? ParseAddress(string address)
    {
        var result = new byte[address.Length];
        for (var i = 0; i < address.Length; i++)
        {
            var index = Charmap.IndexOf(address[i]);
            if (index < 0)
                return null;
            result[i] = (byte) (index + 1);
        }
        return result;
    }

    public static string ToGlyphString(IReadOnlyList<byte> address) =>
        new(address.Select(GetChar).ToArray());
}

[Serializable, NetSerializable]
public enum LuaMStargateConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class LuaMStargateConsoleUiState(
    byte[] currentInput,
    byte[] gateAddress,
    int addressLength,
    bool portalOpen,
    bool dialing,
    string status,
    byte[][]? diskAddresses,
    bool hasControllable,
    bool irisOpen,
    bool irisBusy) : BoundUserInterfaceState
{
    public readonly byte[] CurrentInput = currentInput;
    public readonly byte[] GateAddress = gateAddress;
    public readonly int AddressLength = addressLength;
    public readonly bool PortalOpen = portalOpen;
    public readonly bool Dialing = dialing;
    public readonly string Status = status;
    public readonly byte[][]? DiskAddresses = diskAddresses;
    public readonly bool HasControllable = hasControllable;
    public readonly bool IrisOpen = irisOpen;
    public readonly bool IrisBusy = irisBusy;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateInputMessage(byte symbol) : BoundUserInterfaceMessage
{
    public readonly byte Symbol = symbol;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateDialMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateClearMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateCloseMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateSaveDiskAddressMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMStargateDeleteDiskAddressMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateAutoDialFromDiskMessage(byte[] address) : BoundUserInterfaceMessage
{
    public readonly byte[] Address = address;
}

[Serializable, NetSerializable]
public sealed class LuaMStargateToggleIrisMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public enum LuaMStargateVisuals : byte
{
    State,
    IrisState,
}

[Serializable, NetSerializable]
public enum LuaMStargateVisualLayers : byte
{
    Portal,
    Iris,
}

[Serializable, NetSerializable]
public enum LuaMStargateVisualState : byte
{
    Off,
    Starting,
    Opening,
    Idle,
    Closing,
}

[Serializable, NetSerializable]
public enum LuaMStargateIrisVisualState : byte
{
    Open,
    Opening,
    Closing,
    Closed,
}
