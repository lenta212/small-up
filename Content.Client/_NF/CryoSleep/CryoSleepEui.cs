using Content.Client.Eui;
using Content.Shared._NF.CryoSleep;
using Content.Shared._NF.CCVar;
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;

namespace Content.Client._NF.CryoSleep;

[UsedImplicitly]
public sealed class CryoSleepEui : BaseEui
{
    private const int WarningDisplayLimit = 2;

    private readonly AcceptCryoWindow _window;
    private readonly IConfigurationManager _configuration;

    public CryoSleepEui()
    {
        _configuration = IoCManager.Resolve<IConfigurationManager>();
        _window = new AcceptCryoWindow();

        _window.DenyButton.OnPressed += _ =>
        {
            SendMessage(new AcceptCryoChoiceMessage(AcceptCryoUiButton.Deny));
            _window.Close();
        };

        _window.AcceptButton.OnPressed += _ =>
        {
            SendMessage(new AcceptCryoChoiceMessage(AcceptCryoUiButton.Accept));
            _window.Close();
        };
    }

    public override void Opened()
    {
        var warningCount = _configuration.GetCVar(NFCCVars.CryoWarningAcknowledgements);
        if (warningCount >= WarningDisplayLimit)
        {
            SendMessage(new AcceptCryoChoiceMessage(AcceptCryoUiButton.Accept));
            return;
        }

        _configuration.SetCVar(NFCCVars.CryoWarningAcknowledgements, warningCount + 1);
        IoCManager.Resolve<IClyde>().RequestWindowAttention();
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }

}
