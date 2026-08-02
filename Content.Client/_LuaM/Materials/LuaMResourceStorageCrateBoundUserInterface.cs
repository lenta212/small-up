using Content.Client._LuaM.Materials.UI;
using Content.Shared._LuaM.Materials;

namespace Content.Client._LuaM.Materials;

public sealed class LuaMResourceStorageCrateBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private LuaMResourceStorageCrateWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new LuaMResourceStorageCrateWindow();
        _window.OnClose += Close;
        _window.TransferRequested += OnTransferRequested;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is LuaMResourceStorageCrateUiState crateState)
            _window?.UpdateState(crateState);
    }

    private void OnTransferRequested(
        string material,
        int amount,
        bool transferAll,
        LuaMResourceTransferDirection direction)
    {
        SendMessage(new LuaMResourceStorageCrateTransferMessage(material, amount, transferAll, direction));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        if (_window != null)
            _window.TransferRequested -= OnTransferRequested;
        _window?.Dispose();
    }
}
