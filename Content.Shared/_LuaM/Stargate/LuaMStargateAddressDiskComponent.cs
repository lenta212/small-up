using Robust.Shared.GameStates;

namespace Content.Shared._LuaM.Stargate;

/// <summary>
/// Stores dialable addresses as physical Stargate data media.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LuaMStargateAddressDiskComponent : Component
{
    public const int MaxAddresses = 64;

    [DataField, AutoNetworkedField]
    public List<List<byte>> Addresses = new();
}
