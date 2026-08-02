using Content.Client._LuaM.Rescue.UI;
using Content.Shared._LuaM.Rescue;

namespace Content.Client._LuaM.Rescue;

public sealed class LuaMRescueDispatchConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private LuaMRescueDispatchConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new LuaMRescueDispatchConsoleWindow();
        _window.OnClose += Close;
        _window.RefreshRequested += OnRefresh;
        _window.ActionRequested += OnAction;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is LuaMRescueDispatchConsoleState rescueState)
            _window?.UpdateState(rescueState);
    }

    private void OnRefresh() => SendMessage(new LuaMRescueDispatchRefreshMessage());
    private void OnAction(LuaMRescueDispatchConsoleAction action, NetEntity? target, NetEntity? agent) =>
        SendMessage(new LuaMRescueDispatchActionMessage(action, target, agent));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_window != null)
        {
            _window.RefreshRequested -= OnRefresh;
            _window.ActionRequested -= OnAction;
        }
        _window?.Dispose();
    }
}
