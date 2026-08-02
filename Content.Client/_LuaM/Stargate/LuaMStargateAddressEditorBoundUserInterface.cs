using Content.Shared._LuaM.Stargate;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._LuaM.Stargate;

[UsedImplicitly]
public sealed class LuaMStargateAddressEditorBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private LuaMStargateAddressEditorWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<LuaMStargateAddressEditorWindow>();
        _window.SymbolPressed += symbol => SendMessage(new LuaMStargateAddressEditorInputMessage(symbol));
        _window.ClearPressed += () => SendMessage(new LuaMStargateAddressEditorClearMessage());
        _window.SaveLeftPressed += () => SendMessage(new LuaMStargateAddressEditorSaveLeftMessage());
        _window.SaveRightPressed += () => SendMessage(new LuaMStargateAddressEditorSaveRightMessage());
        _window.DeleteLeftPressed += index => SendMessage(new LuaMStargateAddressEditorDeleteLeftMessage(index));
        _window.DeleteRightPressed += index => SendMessage(new LuaMStargateAddressEditorDeleteRightMessage(index));
        _window.MoveLeftToRightPressed += index => SendMessage(new LuaMStargateAddressEditorMoveLeftToRightMessage(index));
        _window.MoveRightToLeftPressed += index => SendMessage(new LuaMStargateAddressEditorMoveRightToLeftMessage(index));
        _window.CopyLeftToRightPressed += index => SendMessage(new LuaMStargateAddressEditorCopyLeftToRightMessage(index));
        _window.CopyRightToLeftPressed += index => SendMessage(new LuaMStargateAddressEditorCopyRightToLeftMessage(index));
        _window.CloneLeftToRightPressed += () => SendMessage(new LuaMStargateAddressEditorCloneLeftToRightMessage());
        _window.CloneRightToLeftPressed += () => SendMessage(new LuaMStargateAddressEditorCloneRightToLeftMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is LuaMStargateAddressEditorUiState current)
            _window?.UpdateState(current);
    }
}
