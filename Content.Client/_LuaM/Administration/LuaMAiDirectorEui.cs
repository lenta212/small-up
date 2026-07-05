using Content.Client.Eui;
using Content.Shared.Eui;
using Content.Shared._LuaM.Administration;
using JetBrains.Annotations;

namespace Content.Client._LuaM.Administration;

[UsedImplicitly]
public sealed class LuaMAiDirectorEui : BaseEui
{
    private readonly LuaMAiDirectorWindow _window;

    public LuaMAiDirectorEui()
    {
        _window = new LuaMAiDirectorWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.RefreshPressed += () => SendMessage(new LuaMAiDirectorEuiMsg.Refresh());
        _window.TogglePressed += enabled => SendMessage(new LuaMAiDirectorEuiMsg.SetEnabled { Enabled = enabled });
        _window.ReviewPressed += () => SendMessage(new LuaMAiDirectorEuiMsg.Review());
        _window.LogReviewPressed += () => SendMessage(new LuaMAiDirectorEuiMsg.LogReview());
        _window.ConfirmPendingPressed += confirmationId => SendMessage(new LuaMAiDirectorEuiMsg.ConfirmPendingAction { ConfirmationId = confirmationId });
        _window.CancelPendingPressed += confirmationId => SendMessage(new LuaMAiDirectorEuiMsg.CancelPendingAction { ConfirmationId = confirmationId });
        _window.GeneratePressed += request => SendMessage(new LuaMAiDirectorEuiMsg.Generate
        {
            TargetUserId = request.TargetUserId,
            TemplateId = request.TemplateId,
            Instruction = request.Instruction,
            UseGateway = request.UseGateway,
            IgnoreOpenLead = request.IgnoreOpenLead,
        });
        _window.ChatPressed += request => SendMessage(new LuaMAiDirectorEuiMsg.Chat
        {
            Message = request.Message,
            TargetUserId = request.TargetUserId,
            TemplateId = request.TemplateId,
        });
        _window.QuickActionPressed += request => SendMessage(new LuaMAiDirectorEuiMsg.QuickAction
        {
            Action = request.Action,
            TargetUserId = request.TargetUserId,
            TemplateId = request.TemplateId,
            GatewayShipGameMapId = request.GatewayShipGameMapId,
        });
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is LuaMAiDirectorEuiState cast)
            _window.SetState(cast);
    }
}
