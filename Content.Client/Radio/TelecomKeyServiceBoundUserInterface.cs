using Content.Shared.Radio;
using Robust.Client.UserInterface;

namespace Content.Client.Radio;

public sealed class TelecomKeyServiceBoundUserInterface : BoundUserInterface
{
    private TelecomKeyServiceWindow? _window;
    public TelecomKeyServiceBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey) { }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<TelecomKeyServiceWindow>();
        _window.CreateKey += message => SendMessage(message);
        _window.ToggleLock += locked => SendMessage(new TelecomToggleLockMessage { Locked = locked });
        _window.DeleteKey += key => SendMessage(new TelecomDeleteKeyMessage { Key = key });
        _window.DeleteAllKeys += () => SendMessage(new TelecomDeleteAllKeysMessage());
        _window.DetachFrequency += frequency => SendMessage(new TelecomDetachFrequencyMessage { Frequency = frequency });
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is TelecomKeyServiceState service)
            _window?.UpdateState(service);
    }
}
