using Content.Shared.Radio;
using Robust.Client.UserInterface;

namespace Content.Client.Radio;

public sealed class TelecomLogConsoleBoundUserInterface : BoundUserInterface
{
    private TelecomLogConsoleWindow? _window;

    public TelecomLogConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey) { }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<TelecomLogConsoleWindow>();
        _window.Query += query => SendMessage(query);
        SendMessage(new TelecomLogQueryMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is TelecomLogConsoleState log)
            _window?.UpdateState(log);
    }
}
