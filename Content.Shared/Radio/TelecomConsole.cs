using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.Radio;

[RegisterComponent]
public sealed partial class TelecomLogConsoleComponent : Component;

[RegisterComponent]
public sealed partial class TelecomKeyServiceConsoleComponent : Component;

[Serializable, NetSerializable]
public enum TelecomConsoleUiKey
{
    Log,
    KeyService
}

[Serializable, NetSerializable]
public sealed class TelecomLogEntryNet
{
    public DateTime Timestamp;
    public MapId Map;
    public string Channel = string.Empty;
    public int Frequency;
    public string Speaker = string.Empty;
    public string Message = string.Empty;
}

[Serializable, NetSerializable]
public sealed class TelecomLogConsoleState : BoundUserInterfaceState
{
    public List<MapId> Maps = new();
    public List<TelecomChannelInfo> Channels = new();
    public List<TelecomLogEntryNet> Entries = new();
    public bool RequiresPassword;
    public bool Authorized;
    public string? Error;
}

[Serializable, NetSerializable]
public sealed class TelecomLogQueryMessage : BoundUserInterfaceMessage
{
    public MapId? Map;
    public string? Channel;
    public string? Frequency;
    public string? Speaker;
    public string? From;
    public string? To;
    public string? Words;
    public string? Password;
}

[Serializable, NetSerializable]
public sealed class TelecomKeyServiceState : BoundUserInterfaceState
{
    public List<TelecomChannelInfo> Channels = new();
    public bool KeysLocked;
    public string? Error;
}

[Serializable, NetSerializable]
public sealed class TelecomChannelInfo
{
    public string Id = string.Empty;
    public string Name = string.Empty;
}

[Serializable, NetSerializable]
public sealed class TelecomCreateKeyMessage : BoundUserInterfaceMessage
{
    public int Frequency;
    public string ChannelName = string.Empty;
    public string? Tag;
    public string? Color;
    public string? GradientColor;
}

[Serializable, NetSerializable]
public sealed class TelecomToggleLockMessage : BoundUserInterfaceMessage
{
    public bool Locked;
}
