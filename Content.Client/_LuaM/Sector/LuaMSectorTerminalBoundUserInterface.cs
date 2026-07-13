using Content.Client._LuaM.Sector.UI;
using Content.Shared._LuaM.Sector;

namespace Content.Client._LuaM.Sector;

public sealed class LuaMSectorTerminalBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private LuaMSectorTerminalWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = new LuaMSectorTerminalWindow();
        _window.OnClose += Close;
        _window.ActionRequested += SendTerminalAction;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is LuaMSectorStatusUiState sectorState)
            _window?.UpdateState(sectorState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        if (_window != null)
            _window.ActionRequested -= SendTerminalAction;

        _window?.Dispose();
    }

    private void SendTerminalAction(
        LuaMSectorTerminalAction action,
        string storyId,
        string templateId,
        NetEntity? contact)
    {
        SendMessage(new LuaMSectorTerminalActionMessage(action, storyId, templateId, contact));
    }
}
