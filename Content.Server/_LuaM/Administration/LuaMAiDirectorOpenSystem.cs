using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._LuaM.Administration;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Network;

namespace Content.Server._LuaM.Administration;

public sealed class LuaMAiDirectorOpenSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly EuiManager _eui = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IServerNetManager _net = default!;

    public override void Initialize()
    {
        base.Initialize();

        _net.RegisterNetMessage<MsgLuaMAiDirectorOpen>(OnOpenRequested);
    }

    private void OnOpenRequested(MsgLuaMAiDirectorOpen msg)
    {
        if (!_players.TryGetSessionByChannel(msg.MsgChannel, out var session))
            return;

        if (!_admin.HasAdminFlag(session, AdminFlags.Admin))
            return;

        _eui.OpenEui(new LuaMAiDirectorEui(), session);
    }
}
