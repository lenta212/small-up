using Content.Server.EUI;
using Content.Shared._NF.CryoSleep;
using Content.Shared.Eui;

namespace Content.Server._NF.CryoSleep;

public sealed class CryoSleepEui : BaseEui
{
    private readonly CryoSleepSystem _cryoSystem;
    private readonly EntityUid _body;
    private readonly EntityUid _cryopod;
    private readonly Guid _episodeId;
    private bool _valid = true;
    private bool _closed;

    public CryoSleepEui(EntityUid body, EntityUid cryopod, Guid episodeId, CryoSleepSystem cryoSys)
    {
        _body = body;
        _cryopod = cryopod;
        _episodeId = episodeId;
        _cryoSystem = cryoSys;
    }

    public override void Closed()
    {
        _closed = true;
    }

    internal void Invalidate()
    {
        _valid = false;
        CloseIfOpen();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (IsShutDown || _closed)
            return;

        if (!_valid || msg is not AcceptCryoChoiceMessage choice)
        {
            CloseIfOpen();
            return;
        }

        if (choice.Button == AcceptCryoUiButton.Accept)
        {
            _cryoSystem.CryoStoreBody(_body, _cryopod, _episodeId);
        }
        else
        {
            _cryoSystem.EjectBody(_cryopod, body: _body, episodeId: _episodeId);
        }

        CloseIfOpen();
    }

    private void CloseIfOpen()
    {
        // EuiManager calls Closed() and drops its player entry on disconnect
        // without marking the instance shut down, so do not close it twice.
        if (Id != 0 && !_closed && !IsShutDown)
            Close();
    }
}
