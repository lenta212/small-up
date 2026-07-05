using Content.Shared._LuaM.Administration;
using Robust.Shared.Network;

namespace Content.Client._LuaM.Administration;

public sealed class LuaMAiDirectorOpenSystem : EntitySystem
{
    [Dependency] private readonly IClientNetManager _net = default!;

    public override void Initialize()
    {
        base.Initialize();

        _net.RegisterNetMessage<MsgLuaMAiDirectorOpen>();
    }

    public void RequestOpen()
    {
        _net.ClientSendMessage(new MsgLuaMAiDirectorOpen());
    }
}
