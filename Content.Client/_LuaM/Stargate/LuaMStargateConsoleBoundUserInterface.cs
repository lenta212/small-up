using Content.Shared._LuaM.Stargate;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._LuaM.Stargate;

[UsedImplicitly]
public sealed class LuaMStargateConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private LuaMStargateConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<LuaMStargateConsoleWindow>();
        _window.SymbolPressed += symbol => SendMessage(new LuaMStargateInputMessage(symbol));
        _window.DialPressed += () => SendMessage(new LuaMStargateDialMessage());
        _window.ClearPressed += () => SendMessage(new LuaMStargateClearMessage());
        _window.ClosePressed += () => SendMessage(new LuaMStargateCloseMessage());
        _window.SaveAddressPressed += () => SendMessage(new LuaMStargateSaveDiskAddressMessage());
        _window.DiskAddressDialPressed += address => SendMessage(new LuaMStargateAutoDialFromDiskMessage(address));
        _window.DiskAddressDeletePressed += index => SendMessage(new LuaMStargateDeleteDiskAddressMessage(index));
        _window.IrisTogglePressed += () => SendMessage(new LuaMStargateToggleIrisMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is LuaMStargateConsoleUiState current)
            _window?.UpdateState(current);
    }
}
