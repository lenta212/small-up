using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Materials;

[RegisterComponent, NetworkedComponent]
public sealed partial class LuaMResourceStorageCrateComponent : Component;

[Serializable, NetSerializable]
public enum LuaMResourceStorageCrateUiKey : byte
{
    Key,
}
[Serializable, NetSerializable]
public enum LuaMResourceTransferDirection : byte
{
    SiloToCrate,
    CrateToSilo,
}

[Serializable, NetSerializable]
public sealed class LuaMResourceStorageCrateUiState(
    bool linked,
    string siloName,
    int used,
    int capacity,
    List<LuaMResourceStorageCrateMaterial> materials,
    string status) : BoundUserInterfaceState
{
    public bool Linked = linked;
    public string SiloName = siloName;
    public int Used = used;
    public int Capacity = capacity;
    public List<LuaMResourceStorageCrateMaterial> Materials = materials;
    public string Status = status;
}

[Serializable, NetSerializable]
public sealed record LuaMResourceStorageCrateMaterial(
    string Id,
    string Name,
    int CrateAmount,
    int SiloAmount);

[Serializable, NetSerializable]
public sealed class LuaMResourceStorageCrateTransferMessage(
    string material,
    int amount,
    bool transferAll,
    LuaMResourceTransferDirection direction) : BoundUserInterfaceMessage
{
    public string Material = material;
    public int Amount = amount;
    public bool TransferAll = transferAll;
    public LuaMResourceTransferDirection Direction = direction;
}
