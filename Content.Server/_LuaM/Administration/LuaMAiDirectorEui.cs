using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server._LuaM.Sector;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Eui;
using Content.Shared._LuaM.Administration;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Administration;

public sealed partial class LuaMAiDirectorEui : BaseEui
{
    [Dependency] private IAdminManager _admin = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private const int AiActionHistoryMaxLength = 6000;

    private readonly LuaMSectorAiDirectorSystem _director;
    private string _lastResult = "Ready.";
    private string _lastReview = string.Empty;
    private string _reviewHistory = string.Empty;
    private int _reviewHistoryCount;
    private string _aiActionHistory = string.Empty;
    private int _aiActionHistoryCount;
    private string _chatTranscript = "AI Director link ready. Ask a question or request an action.";
    private PendingAiDirectorAction? _pendingConfirmation;

    private sealed record GatewayShipPreset(string GameMap, string DisplayName);

    public LuaMAiDirectorEui()
    {
        IoCManager.InjectDependencies(this);
        _director = _systems.GetEntitySystem<LuaMSectorAiDirectorSystem>();
    }

    public override void Opened()
    {
        StateDirty();
    }

    public override EuiStateBase GetNewState()
    {
        return _director.BuildAdminState(
            _lastResult,
            _chatTranscript,
            _lastReview,
            _reviewHistory,
            _reviewHistoryCount,
            _aiActionHistory,
            _aiActionHistoryCount,
            _admin.HasAdminFlag(Player, AdminFlags.Server),
            _pendingConfirmation?.Id ?? string.Empty,
            _pendingConfirmation?.Title ?? string.Empty,
            _pendingConfirmation?.Detail ?? string.Empty,
            BuildGatewayShipPresets());
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!_admin.HasAdminFlag(Player, AdminFlags.Admin))
        {
            Close();
            return;
        }

        switch (msg)
        {
            case LuaMAiDirectorEuiMsg.Refresh:
                StateDirty();
                break;
            case LuaMAiDirectorEuiMsg.SetEnabled setEnabled:
                SetEnabledWithPolicy(setEnabled.Enabled);
                break;
            case LuaMAiDirectorEuiMsg.Generate generate:
                GenerateWithPolicy(generate);
                break;
            case LuaMAiDirectorEuiMsg.Review:
                _ = ReviewAsync();
                break;
            case LuaMAiDirectorEuiMsg.LogReview:
                LogReview();
                break;
            case LuaMAiDirectorEuiMsg.Chat chat:
                _ = ChatAsync(chat, allowServerActions: false);
                break;
            case LuaMAiDirectorEuiMsg.QuickAction quick:
                QuickActionWithPolicy(quick);
                break;
            case LuaMAiDirectorEuiMsg.ConfirmPendingAction confirm:
                _ = ConfirmPendingActionAsync(confirm.ConfirmationId);
                break;
            case LuaMAiDirectorEuiMsg.CancelPendingAction cancel:
                CancelPendingAction(cancel.ConfirmationId);
                break;
        }
    }

    private void SetEnabledWithPolicy(bool enabled)
    {
        if (!TryRequireServerAction("toggle auto AI"))
            return;

        if (!enabled)
        {
            ExecuteSetEnabled(false);
            return;
        }

        RequestConfirmation(
            "enable-auto-ai",
            "Enable auto AI",
            BuildEnableAutoAiDetail(),
            () =>
            {
                ExecuteSetEnabled(true);
                return Task.CompletedTask;
            },
            LogImpact.High);
    }

    private void ExecuteSetEnabled(bool enabled)
    {
        _director.AdminSetEnabled(enabled);
        _lastResult = enabled
            ? "AI Director auto mode enabled."
            : "AI Director auto mode disabled. Manual actions remain available.";
        LogAiAction(LogImpact.High, "executed", $"auto_ai_enabled={enabled}");
        StateDirty();
    }

    private void GenerateWithPolicy(LuaMAiDirectorEuiMsg.Generate generate)
    {
        RequestConfirmation(
            "generate-process",
            "Generate AI process",
            BuildGenerateDetail(generate),
            () => GenerateAsync(generate),
            LogImpact.High);
    }

    private async Task GenerateAsync(LuaMAiDirectorEuiMsg.Generate generate)
    {
        if (!TryRequireServerAction("generate AI process"))
            return;

        _lastResult = "AI Director is preparing a process...";
        StateDirty();

        _lastResult = await _director.AdminGenerateAsync(
            Player,
            generate.TargetUserId,
            generate.TemplateId,
            generate.Instruction,
            generate.UseGateway,
            generate.IgnoreOpenLead);

        LogAiAction(LogImpact.High, "executed", $"generate_process target={TrimForLog(generate.TargetUserId)} template={TrimForLog(generate.TemplateId)}");
        StateDirty();
    }

    private async Task ReviewAsync()
    {
        _lastResult = "AI Director is preparing impact and process review...";
        StateDirty();

        var review = await _director.AdminReviewAsync(Player);
        _lastReview = review;
        _lastResult = review;
        AppendReviewHistory("AI review", review);
        StateDirty();
    }

    private async Task ChatAsync(LuaMAiDirectorEuiMsg.Chat chat, bool allowServerActions)
    {
        var message = chat.Message.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (message.Length > 1200)
            message = message[..1200];

        AppendChat($"Admin {Player.Name}: {message}");

        if (await TryHandleChatAiBaseActionAsync(message))
            return;

        if (TryRequestChatShipSpawnAction(message))
            return;

        const string pending = "AI Director: processing request...";
        AppendChat(pending);
        StateDirty();

        var reply = await _director.AdminChatAsync(
            Player,
            message,
            chat.TargetUserId,
            chat.TemplateId,
            allowServerActions || IsGameMasterModeEnabled());

        RemovePendingChatLine(pending);
        AppendChat($"AI Director: {reply}");
        StateDirty();
    }

    private void QuickActionWithPolicy(LuaMAiDirectorEuiMsg.QuickAction quick)
    {
        var action = quick.Action.Trim();
        if (action == LuaMAiDirectorEuiMsg.QuickRecommendations)
        {
            ShowLocalRecommendations();
            return;
        }

        if (IsGatewayShipQuickAction(action))
        {
            if (!TryGetGatewayShipPreset(quick, out var gatewayShipPreset, out var gatewayShipError))
            {
                _lastResult = gatewayShipError;
                AppendChat($"AI Director: {gatewayShipError}");
                StateDirty();
                return;
            }

            RequestConfirmation(
                $"quick:{action}",
                $"AI quick action: gateway ship {gatewayShipPreset.GameMap}",
                BuildQuickActionDetail(quick),
                () => ExecuteGatewayShipQuickActionAsync(quick, gatewayShipPreset),
                LogImpact.High);
            return;
        }

        var message = BuildQuickActionMessage(action);
        if (message == null)
        {
            _lastResult = $"Unknown AI quick action: {action}";
            StateDirty();
            return;
        }

        if (IsSafeQuickAction(action))
        {
            _ = QuickActionAsync(quick, allowServerActions: false);
            return;
        }

        RequestConfirmation(
            $"quick:{action}",
            $"AI quick action: {action}",
            BuildQuickActionDetail(quick),
            () => QuickActionAsync(quick, allowServerActions: true),
            GetQuickActionImpact(action));
    }

    private async Task QuickActionAsync(LuaMAiDirectorEuiMsg.QuickAction quick, bool allowServerActions)
    {
        var action = quick.Action.Trim();
        var message = BuildQuickActionMessage(action);
        if (message == null)
        {
            _lastResult = $"Unknown AI quick action: {action}";
            StateDirty();
            return;
        }

        if (allowServerActions && !TryRequireServerAction($"AI quick action {action}"))
            return;

        const string pending = "AI Director: processing quick action...";
        AppendChat($"Admin {Player.Name}: quick action {action}");
        AppendChat(pending);
        StateDirty();

        var reply = await _director.AdminChatAsync(
            Player,
            message,
            quick.TargetUserId,
            quick.TemplateId,
            allowServerActions);

        RemovePendingChatLine(pending);
        AppendChat($"AI Director: {reply}");
        LogAiAction(GetQuickActionImpact(action), "executed", $"quick_action={action} target={TrimForLog(quick.TargetUserId)} template={TrimForLog(quick.TemplateId)}");
        StateDirty();
    }

    private async Task<bool> TryHandleChatAiBaseActionAsync(string message)
    {
        if (!_director.TryResolveAiBaseAdminRequest(message, out var action, out var error))
            return false;

        if (!string.IsNullOrWhiteSpace(error))
        {
            _lastResult = error;
            AppendChat($"AI Director: {error}");
            StateDirty();
            return true;
        }

        if (!action.RequiresConfirmation)
        {
            AppendChat("AI Director: executing local read-only AI base action.");
            _lastResult = await _director.ExecuteAiBaseAdminActionAsync(Player, action, message);
            AppendChat($"AI Director: {_lastResult}");
            LogAiAction(LogImpact.Medium, "executed", $"chat_action=ai_base_{TrimForLog(action.Kind)}");
            StateDirty();
            return true;
        }

        RequestConfirmation(
            $"chat:ai-base:{action.Kind}:{action.Role}:{action.VesselId}",
            BuildChatAiBaseTitle(action),
            BuildChatAiBaseDetail(message, action),
            () => ExecuteChatAiBaseActionAsync(message, action),
            LogImpact.High);
        return true;
    }

    private async Task ExecuteChatAiBaseActionAsync(
        string message,
        LuaMSectorAiDirectorSystem.AiBaseAdminAction action)
    {
        if (!TryRequireServerAction(BuildChatAiBaseTitle(action)))
            return;

        AppendChat($"AI Director: executing local server action: {BuildChatAiBaseTitle(action)}.");
        _lastResult = await _director.ExecuteAiBaseAdminActionAsync(Player, action, message);
        AppendChat($"AI Director: {_lastResult}");
        LogAiAction(
            LogImpact.High,
            "executed",
            $"chat_action=ai_base_{TrimForLog(action.Kind)} role={TrimForLog(action.Role)} vessel={TrimForLog(action.VesselId)}");
        StateDirty();
    }

    private bool TryRequestChatShipSpawnAction(string message)
    {
        if (!_director.TryResolveAdminShipSpawnRequest(message, out var vesselId, out var displayName, out var error))
            return false;

        if (!string.IsNullOrWhiteSpace(error))
        {
            _lastResult = error;
            AppendChat($"AI Director: {error}");
            StateDirty();
            return true;
        }

        RequestConfirmation(
            $"chat:spawn-ship:{vesselId}",
            $"Spawn ship {displayName}",
            BuildChatShipSpawnDetail(message, vesselId, displayName),
            () => ExecuteChatShipSpawnActionAsync(message, vesselId, displayName),
            LogImpact.High);
        return true;
    }

    private async Task ExecuteChatShipSpawnActionAsync(string message, string vesselId, string displayName)
    {
        if (!TryRequireServerAction($"spawn ship {vesselId} near admin"))
            return;

        AppendChat($"AI Director: executing local server action: spawn {displayName} near your current position.");
        _lastResult = await _director.SpawnShipNearAdminAsync(Player, message);
        AppendChat($"AI Director: {_lastResult}");
        LogAiAction(LogImpact.High, "executed", $"chat_action=spawn_ship vessel={TrimForLog(vesselId)}");
        StateDirty();
    }

    private void ShowLocalRecommendations()
    {
        var recommendations = _director.BuildAdminRecommendationsText();
        _lastResult = recommendations;
        AppendChat($"AI Director: {recommendations}");
        AppendReviewHistory("Local recommendations", recommendations);
        LogAiAction(LogImpact.Medium, "reviewed", "local_ai_recommendations");
        StateDirty();
    }

    private async Task ExecuteGatewayShipQuickActionAsync(LuaMAiDirectorEuiMsg.QuickAction quick, GatewayShipPreset preset)
    {
        var action = quick.Action.Trim();
        if (!TryRequireServerAction($"AI quick action {action}"))
            return;

        AppendChat($"Admin {Player.Name}: quick action {action}");
        AppendChat($"AI Director: executing local server action: spawn {preset.DisplayName} near your current position.");
        _lastResult = await _director.SpawnShipNearAdminAsync(Player, $"spawn {preset.GameMap} ship near me");
        AppendChat($"AI Director: {_lastResult}");
        LogAiAction(LogImpact.High, "executed", $"quick_action={action} vessel={TrimForLog(preset.GameMap)}");
        StateDirty();
    }

    private void LogReview()
    {
        if (string.IsNullOrWhiteSpace(_lastReview))
        {
            _lastResult = "No AI review is available to save yet.";
            StateDirty();
            return;
        }

        _director.AdminLogReview(Player, _lastReview);
        _lastResult = "Last AI review saved to the server log.";
        AppendReviewHistory("Saved to server log", _lastReview);
        LogAiAction(LogImpact.Medium, "logged", "ai_review");
        StateDirty();
    }

    private void RequestConfirmation(string actionKey, string title, string detail, Func<Task> executeAsync, LogImpact impact)
    {
        if (!TryRequireServerAction(title))
            return;

        if (IsGameMasterModeEnabled())
        {
            _ = ExecuteGameMasterActionAsync(actionKey, title, detail, executeAsync, impact);
            return;
        }

        if (_pendingConfirmation != null)
        {
            _lastResult = "Confirm or cancel the current pending AI action before starting another one.";
            StateDirty();
            return;
        }

        _pendingConfirmation = new PendingAiDirectorAction(
            Guid.NewGuid().ToString("N"),
            actionKey,
            title,
            detail,
            executeAsync,
            impact);

        _lastResult = $"Confirmation required: {title}.";
        AppendChat($"AI Director: confirmation required for {title}. {detail}");
        LogAiAction(impact, "confirmation requested", $"{actionKey}; {CompactForLog(detail)}");
        StateDirty();
    }

    private async Task ExecuteGameMasterActionAsync(
        string actionKey,
        string title,
        string detail,
        Func<Task> executeAsync,
        LogImpact impact)
    {
        var action = new PendingAiDirectorAction(
            Guid.NewGuid().ToString("N"),
            actionKey,
            title,
            detail,
            executeAsync,
            impact);

        _lastResult = $"Game-master mode: executing {title} without manual confirmation.";
        AppendChat($"AI Director: game-master mode executing {title}. {detail}");
        LogAiAction(impact, "game-master executed", $"{actionKey}; {CompactForLog(detail)}");
        StateDirty();

        try
        {
            await executeAsync();
            AppendAiActionHistory(
                action,
                "game-master",
                "game-master mode; manual confirmation bypassed; execution finished or submitted locally");
            StateDirty();
        }
        catch (Exception e)
        {
            _lastResult = "AI game-master action failed: local execution error; details kept in the server log.";
            LogAiAction(LogImpact.High, "failed", $"{actionKey}; {e.Message}");
            AppendAiActionHistory(
                action,
                "failed",
                "game-master mode; local execution error; details kept in server log");
            StateDirty();
        }
    }

    private async Task ConfirmPendingActionAsync(string confirmationId)
    {
        if (_pendingConfirmation == null ||
            !string.Equals(_pendingConfirmation.Id, confirmationId, StringComparison.Ordinal))
        {
            _lastResult = "No matching pending AI action to confirm.";
            StateDirty();
            return;
        }

        var pending = _pendingConfirmation;
        _pendingConfirmation = null;
        _lastResult = $"Confirmed: {pending.Title}.";
        LogAiAction(pending.Impact, "confirmed", $"{pending.ActionKey}; {pending.Detail}");
        StateDirty();

        try
        {
            await pending.ExecuteAsync();
            AppendAiActionHistory(
                pending,
                "confirmed",
                "confirmed; execution finished or submitted locally");
            StateDirty();
        }
        catch (Exception e)
        {
            _lastResult = "AI action failed: local execution error; details kept in the server log.";
            LogAiAction(LogImpact.High, "failed", $"{pending.ActionKey}; {e.Message}");
            AppendAiActionHistory(
                pending,
                "failed",
                "failed; local execution error; details kept in server log");
            StateDirty();
        }
    }

    private void CancelPendingAction(string confirmationId)
    {
        if (_pendingConfirmation == null ||
            !string.Equals(_pendingConfirmation.Id, confirmationId, StringComparison.Ordinal))
        {
            _lastResult = "No matching pending AI action to cancel.";
            StateDirty();
            return;
        }

        var pending = _pendingConfirmation;
        _pendingConfirmation = null;
        _lastResult = $"Canceled: {pending.Title}.";
        AppendAiActionHistory(
            pending,
            "canceled",
            "canceled; no server effect executed");
        LogAiAction(LogImpact.Medium, "canceled", $"{pending.ActionKey}; {pending.Detail}");
        StateDirty();
    }

    private bool TryRequireServerAction(string actionLabel)
    {
        if (_admin.HasAdminFlag(Player, AdminFlags.Server) || IsGameMasterModeEnabled())
            return true;

        _pendingConfirmation = null;
        _lastResult = $"Denied: {actionLabel} requires the Server admin flag.";
        AppendChat($"AI Director: denied {actionLabel}; missing Server admin flag.");
        LogAiAction(LogImpact.High, "denied", $"{actionLabel}; missing Server flag");
        StateDirty();
        return false;
    }

    private bool IsGameMasterModeEnabled()
    {
        return _director.IsGameMasterModeEnabled();
    }

    private void LogAiAction(LogImpact impact, string outcome, string detail)
    {
        _adminLogger.Add(
            LogType.Action,
            impact,
            $"LuaM AI Director admin {Player.Name} {outcome}: {detail}");
    }

    private static string? BuildQuickActionMessage(string action)
    {
        return action switch
        {
            LuaMAiDirectorEuiMsg.QuickRecommendations => "director recommendations",
            LuaMAiDirectorEuiMsg.QuickStatus => "sector status",
            LuaMAiDirectorEuiMsg.QuickEvent => "nearby_event: create a dynamic event near the selected player.",
            LuaMAiDirectorEuiMsg.QuickPersonalPressure => "personal_pressure: create pressure around the selected player.",
            LuaMAiDirectorEuiMsg.QuickPersonalDanger => "personal max danger around the selected player.",
            LuaMAiDirectorEuiMsg.QuickAiPressure => "amplify_world_ai ai pressure pressure pulse.",
            LuaMAiDirectorEuiMsg.QuickSyntheticControl => "ai-synthetic-control synthetic robot remote control.",
            LuaMAiDirectorEuiMsg.QuickSubspaceRift => "subspace_rift near the selected player.",
            LuaMAiDirectorEuiMsg.QuickSubspaceRoute => "subspace_rift route from the selected player to the current sector lead.",
            LuaMAiDirectorEuiMsg.QuickRadiation => "radiation condition_radiation",
            LuaMAiDirectorEuiMsg.QuickSensorDrift => "sensor drift condition_sensor_drift",
            LuaMAiDirectorEuiMsg.QuickComms => "comms condition_comms_blackout",
            LuaMAiDirectorEuiMsg.QuickMonolith => "monolith resonance condition_monolith",
            LuaMAiDirectorEuiMsg.QuickClearCondition => "clear condition",
            LuaMAiDirectorEuiMsg.QuickResolveLead => "resolve lead close open lead",
            LuaMAiDirectorEuiMsg.QuickCleanupMarkers => "cleanup markers",
            LuaMAiDirectorEuiMsg.QuickSpawnBeacon => "spawn_entity emergency beacon for the selected player.",
            LuaMAiDirectorEuiMsg.QuickSpawnScanner => "spawn_entity anomaly scanner for the selected player.",
            LuaMAiDirectorEuiMsg.QuickMonolithKit => "monolith kit",
            LuaMAiDirectorEuiMsg.QuickPaperPack => "paper pack",
            LuaMAiDirectorEuiMsg.QuickHistory => "sector history",
            LuaMAiDirectorEuiMsg.QuickAiChat => "ai_chat say in chat: LuaM AI Director confirms active sector monitoring channel.",
            LuaMAiDirectorEuiMsg.QuickAnnouncement => "ai_chat say in chat: LuaM AI Director reports a sector situation change.",
            LuaMAiDirectorEuiMsg.QuickAiBaseDiagnostics => "ai base diagnostics: what is wrong and what can be improved",
            LuaMAiDirectorEuiMsg.QuickAiBasePlan => "ai base development plan and next steps",
            LuaMAiDirectorEuiMsg.QuickAiBaseAutofix => "ai base autofix: fix the top diagnostic issue now",
            LuaMAiDirectorEuiMsg.QuickAiBaseMine => "ai base mine: dispatch AI mining robots to gather resources",
            LuaMAiDirectorEuiMsg.QuickAiBaseBuild => "ai base build: dispatch AI builder robots to build and repair the base",
            LuaMAiDirectorEuiMsg.QuickAiBaseDevelop => "open ai robots should mine resources and build the AI base",
            LuaMAiDirectorEuiMsg.QuickGatewayShip
                or LuaMAiDirectorEuiMsg.QuickGatewayShipSelected
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTriage
                or LuaMAiDirectorEuiMsg.QuickGatewayShipHammerhead
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTzipora
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTokarev => "gateway ship",
            _ => null,
        };
    }

    private static bool IsSafeQuickAction(string action)
    {
        return action is LuaMAiDirectorEuiMsg.QuickRecommendations
            or LuaMAiDirectorEuiMsg.QuickStatus
            or LuaMAiDirectorEuiMsg.QuickHistory
            or LuaMAiDirectorEuiMsg.QuickAiBaseDiagnostics
            or LuaMAiDirectorEuiMsg.QuickAiBasePlan;
    }

    private static LogImpact GetQuickActionImpact(string action)
    {
        if (IsGatewayShipQuickAction(action))
            return LogImpact.High;

        return action is LuaMAiDirectorEuiMsg.QuickPersonalDanger
            or LuaMAiDirectorEuiMsg.QuickAiPressure
            or LuaMAiDirectorEuiMsg.QuickSubspaceRift
            or LuaMAiDirectorEuiMsg.QuickSubspaceRoute
            or LuaMAiDirectorEuiMsg.QuickSyntheticControl
            or LuaMAiDirectorEuiMsg.QuickAnnouncement
            or LuaMAiDirectorEuiMsg.QuickAiBaseAutofix
            or LuaMAiDirectorEuiMsg.QuickAiBaseMine
            or LuaMAiDirectorEuiMsg.QuickAiBaseBuild
            or LuaMAiDirectorEuiMsg.QuickAiBaseDevelop
            ? LogImpact.High
            : LogImpact.Medium;
    }

    private static bool IsGatewayShipQuickAction(string action)
    {
        return action is LuaMAiDirectorEuiMsg.QuickGatewayShip
            or LuaMAiDirectorEuiMsg.QuickGatewayShipSelected
            or LuaMAiDirectorEuiMsg.QuickGatewayShipTriage
            or LuaMAiDirectorEuiMsg.QuickGatewayShipHammerhead
            or LuaMAiDirectorEuiMsg.QuickGatewayShipTzipora
            or LuaMAiDirectorEuiMsg.QuickGatewayShipTokarev;
    }

    private bool TryGetGatewayShipPreset(LuaMAiDirectorEuiMsg.QuickAction quick, out GatewayShipPreset preset, out string error)
    {
        var action = quick.Action.Trim();
        if (action == LuaMAiDirectorEuiMsg.QuickGatewayShipSelected)
        {
            return TryResolveGatewayShipPreset(
                quick.GatewayShipGameMapId,
                string.Empty,
                out preset,
                out error);
        }

        var fixedPreset = action switch
        {
            LuaMAiDirectorEuiMsg.QuickGatewayShip => ("Baeg", "Z-22 Baeg"),
            LuaMAiDirectorEuiMsg.QuickGatewayShipTriage => ("Triage", "Triage"),
            LuaMAiDirectorEuiMsg.QuickGatewayShipHammerhead => ("Hammerhead", "Hammerhead"),
            LuaMAiDirectorEuiMsg.QuickGatewayShipTzipora => ("Tzipora", "Tzipora"),
            LuaMAiDirectorEuiMsg.QuickGatewayShipTokarev => ("Tokarev", "Tokarev"),
            _ => (string.Empty, string.Empty),
        };

        if (!string.IsNullOrWhiteSpace(fixedPreset.Item1))
            return TryResolveGatewayShipPreset(fixedPreset.Item1, fixedPreset.Item2, out preset, out error);

        preset = default!;
        error = $"Unknown gateway ship quick action: {action}";
        return false;
    }

    private bool TryResolveGatewayShipPreset(string rawGameMapId, string displayName, out GatewayShipPreset preset, out string error)
    {
        var gameMapId = rawGameMapId.Trim();
        if (!IsSafeGatewayShipGameMapId(gameMapId))
        {
            preset = default!;
            error = $"Denied gateway ship request: invalid gameMap id '{TrimForLog(rawGameMapId)}'.";
            return false;
        }

        if (!_director.TryResolveAdminShipBuildId(gameMapId, out var shipBuildId, out var resolvedDisplayName, out var resolveError))
        {
            preset = default!;
            error = $"Denied gateway ship request: unknown vessel; {resolveError}";
            return false;
        }

        preset = new GatewayShipPreset(
            shipBuildId,
            string.IsNullOrWhiteSpace(displayName)
                ? resolvedDisplayName
                : displayName);
        error = string.Empty;
        return true;
    }

    private LuaMAiDirectorGatewayShipEntry[] BuildGatewayShipPresets()
    {
        return _director.BuildAdminShipBuildPresets()
            .Where(entry => IsSafeGatewayShipGameMapId(entry.GameMapId))
            .ToArray();
    }

    private static bool IsSafeGatewayShipGameMapId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;

        foreach (var c in value)
        {
            if (c is >= 'a' and <= 'z' ||
                c is >= 'A' and <= 'Z' ||
                c is >= '0' and <= '9' ||
                c is '-' or '_' or '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string BuildGenerateDetail(LuaMAiDirectorEuiMsg.Generate generate)
    {
        var target = string.IsNullOrWhiteSpace(generate.TargetUserId)
            ? "round/sector scoped; no player target selected"
            : "selected player; raw user id withheld from preview";
        var execution = generate.UseGateway
            ? "gateway may be used if configured; local schema and policy checks still gate execution"
            : "local/fallback path only; provider request disabled for this action";

        return BuildActionPreview(
            "generate-process",
            "generate one LuaM AI process proposal/action for the current round context",
            execution,
            "high; can create player-visible sector pressure or events",
            target,
            "single manual process; auto mode unchanged",
            "Server flag check, explicit Confirm, gateway budget, schema validation, and local action allowlist.",
            $"template={TrimForLog(generate.TemplateId)} useGateway={generate.UseGateway} ignoreOpenLead={generate.IgnoreOpenLead} instructionLength={generate.Instruction.Length}");
    }

    private static string BuildQuickActionDetail(LuaMAiDirectorEuiMsg.QuickAction quick)
    {
        var action = quick.Action.Trim();
        var context = $"template={TrimForLog(quick.TemplateId)}";
        if (!string.IsNullOrWhiteSpace(quick.GatewayShipGameMapId))
            context += $" gatewayShipGameMap={TrimForLog(quick.GatewayShipGameMapId)}";

        return BuildActionPreview(
            $"quick:{action}",
            GetQuickActionOutcome(action),
            GetQuickActionExecution(action),
            $"{GetQuickActionRisk(action)}; admin log impact={GetImpactLabel(GetQuickActionImpact(action))}",
            BuildQuickActionTargetPreview(action, quick.TargetUserId),
            GetQuickActionDuration(action),
            "Known quick-action allowlist, Server flag check, explicit Confirm, local validation, and audit log.",
            context);
    }

    private static string BuildEnableAutoAiDetail()
    {
        return BuildActionPreview(
            "enable-auto-ai",
            "enable automatic LuaM AI Director actions for this round",
            "local scheduler may run future AI Director attempts after timers and policy checks",
            "high; can affect round pacing without another button press",
            "round/sector scoped; no player target required",
            "until disabled or the round ends",
            "Server flag check, explicit Confirm, configured timers, gateway budget, schema validation, and local action allowlist.",
            "auto mode changes future behavior; cancel leaves the current round state unchanged");
    }

    private static string BuildChatShipSpawnDetail(string message, string vesselId, string displayName)
    {
        return BuildActionPreview(
            "chat:spawn-ship",
            $"spawn one ship build near the admin current position: {displayName}",
            "local server action only; no external provider call; resolves a shipyard vessel or shuttle gameMap and loads its grid",
            "high; spawns a ship/grid in the current map and can overlap existing structures if used in tight space",
            "admin current position; raw coordinates and identifiers withheld from preview",
            "single spawn per confirmation; spawned ship persists by normal game rules; command can be repeated for more copies",
            "Server flag check, explicit Confirm, local ship-build resolution, grid load validation, and audit log.",
            $"vesselId={TrimForLog(vesselId)}; messageLength={message.Trim().Length}");
    }

    private static string BuildChatAiBaseTitle(LuaMSectorAiDirectorSystem.AiBaseAdminAction action)
    {
        return action.Kind switch
        {
            "create" => "Create AI supply base",
            "ship" => $"Dispatch AI {action.Role} {action.DisplayName}",
            "status" => "Show AI base status",
            "diagnostics" => "Show AI base diagnostics",
            "plan" => "Show AI base development plan",
            "autofix" => "Autofix AI base",
            "develop" => "Develop AI base",
            _ => "AI base action",
        };
    }

    private static string BuildChatAiBaseDetail(
        string message,
        LuaMSectorAiDirectorSystem.AiBaseAdminAction action)
    {
        var outcome = action.Kind switch
        {
            "create" => "create or ensure the LuaM AI supply base runtime ledger",
            "ship" => $"spawn one AI logistics ship near the admin and update AI base stock after the spawn succeeds: {action.DisplayName}",
            "status" => "show the AI base stock, needs, supply score, and recent logistics",
            "diagnostics" => "show AI base diagnostics, stuck drone status, improvements, and suggested commands",
            "plan" => "show the staged AI base development plan and next command queue",
            "autofix" => "run one local autofix for the highest-severity AI base diagnostic and record the attempt",
            "develop" => "deploy the base and dispatch miner plus builder AI crews",
            _ => "run one AI base local action",
        };
        var execution = action.Kind is "ship" or "autofix" or "develop"
            ? "local server action only; no external provider call; may resolve a shipyard vessel or shuttle gameMap, load a grid, and record AI-base memory"
            : "local memory/read-only action only; no external provider call";
        var risk = action.RequiresConfirmation
            ? "high; can mutate sector memory and may spawn a ship/grid in the current map"
            : "low; read-only status result";
        var duration = action.Kind is "ship" or "autofix" or "develop"
            ? "spawned ship persists by normal game rules; AI base stock persists in LuaM sector memory"
            : "AI base ledger persists in LuaM sector memory until reset/import";

        return BuildActionPreview(
            $"chat:ai-base:{action.Kind}",
            outcome,
            execution,
            risk,
            "round/sector scoped; ship actions use admin current position; raw coordinates withheld",
            duration,
            "Server flag check for mutating actions, explicit Confirm, local AI-base intent parser, ship-build validation, grid load validation, and audit log.",
            $"kind={TrimForLog(action.Kind)} role={TrimForLog(action.Role)} vesselId={TrimForLog(action.VesselId)} messageLength={message.Trim().Length}");
    }

    private static string BuildActionPreview(
        string actionKey,
        string outcome,
        string execution,
        string risk,
        string target,
        string duration,
        string guardrails,
        string operatorContext)
    {
        var output = new StringBuilder();
        output.AppendLine("AI action preview:");
        output.AppendLine($"- action={actionKey}");
        output.AppendLine($"- outcome={outcome}");
        output.AppendLine($"- execution={execution}");
        output.AppendLine($"- risk={risk}");
        output.AppendLine($"- risk gate={BuildActionPreviewRiskGate(risk)}");
        output.AppendLine($"- target={target}");
        output.AppendLine($"- duration={duration}");
        output.AppendLine("- confirm policy=Server flag required; action waits for explicit Confirm.");
        output.AppendLine($"- confirm means={BuildActionPreviewConfirmMeaning(risk)}");
        output.AppendLine("- cancel means=no server effect is executed; the pending AI action is removed and audit history records the cancel.");
        output.AppendLine($"- guardrails={guardrails}");
        output.AppendLine($"- operator checklist={BuildActionPreviewOperatorChecklist(risk, target)}");
        output.AppendLine("- sensitive details withheld: provider URL, bearer token, raw prompt, raw exception text, coordinates, and raw player/admin identifiers stay out of this preview.");

        if (!string.IsNullOrWhiteSpace(operatorContext))
            output.AppendLine($"- operator context={operatorContext}");

        return output.ToString().TrimEnd();
    }

    private static string BuildActionPreviewRiskGate(string risk)
    {
        if (IsHighRiskPreview(risk))
            return "high - pause before Confirm; verify intent, target/scope, duration, and rollback/cleanup path";

        if (IsLowRiskPreview(risk))
            return "low - read-only or advice path; still verify the request matches current round context";

        return "medium - verify local sector state and player-facing impact before Confirm";
    }

    private static string BuildActionPreviewConfirmMeaning(string risk)
    {
        if (IsHighRiskPreview(risk))
            return "execute a player-visible or round-affecting action locally after validation; treat Confirm as the final human approval step";

        if (IsLowRiskPreview(risk))
            return "run a local safe/advisory action; no world mutation is expected";

        return "run one local sector-changing action after validation; review outcome and action history afterward";
    }

    private static string BuildActionPreviewOperatorChecklist(string risk, string target)
    {
        var targetCheck = target.Contains("required; no target", StringComparison.OrdinalIgnoreCase)
            ? "target missing"
            : target.StartsWith("round/sector scoped", StringComparison.OrdinalIgnoreCase)
                ? "round scoped"
                : target.Contains("selected player", StringComparison.OrdinalIgnoreCase)
                    ? "target selected"
                    : "round scoped";

        if (IsHighRiskPreview(risk))
            return $"evidence reviewed, {targetCheck}, risk accepted, impact understood, cleanup/rollback path known";

        if (IsLowRiskPreview(risk))
            return $"context reviewed, {targetCheck}, local-safe result expected";

        return $"context reviewed, {targetCheck}, player-facing impact acceptable";
    }

    private static bool IsHighRiskPreview(string risk)
    {
        return risk.Contains("high", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowRiskPreview(string risk)
    {
        return risk.Contains("low", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetQuickActionOutcome(string action)
    {
        return action switch
        {
            LuaMAiDirectorEuiMsg.QuickEvent => "ask AI Director to create one nearby dynamic event for the selected player",
            LuaMAiDirectorEuiMsg.QuickPersonalPressure => "apply controlled sector pressure around the selected player",
            LuaMAiDirectorEuiMsg.QuickPersonalDanger => "raise danger around the selected player",
            LuaMAiDirectorEuiMsg.QuickAiPressure => "amplify world AI pressure for the sector",
            LuaMAiDirectorEuiMsg.QuickSyntheticControl => "arm local synthetic control support",
            LuaMAiDirectorEuiMsg.QuickSubspaceRift => "create a subspace rift near the selected player",
            LuaMAiDirectorEuiMsg.QuickSubspaceRoute => "create a subspace route from selected player context to the active sector lead",
            LuaMAiDirectorEuiMsg.QuickRadiation => "apply a radiation sector condition",
            LuaMAiDirectorEuiMsg.QuickSensorDrift => "apply a sensor drift sector condition",
            LuaMAiDirectorEuiMsg.QuickComms => "apply a comms blackout sector condition",
            LuaMAiDirectorEuiMsg.QuickMonolith => "apply a monolith resonance sector condition",
            LuaMAiDirectorEuiMsg.QuickClearCondition => "clear one active sector condition",
            LuaMAiDirectorEuiMsg.QuickResolveLead => "resolve the current open sector lead",
            LuaMAiDirectorEuiMsg.QuickCleanupMarkers => "clean local LuaM event markers",
            LuaMAiDirectorEuiMsg.QuickSpawnBeacon => "spawn an emergency beacon for the selected player",
            LuaMAiDirectorEuiMsg.QuickSpawnScanner => "spawn an anomaly scanner for the selected player",
            LuaMAiDirectorEuiMsg.QuickMonolithKit => "spawn a monolith investigation kit for the selected player",
            LuaMAiDirectorEuiMsg.QuickPaperPack => "spawn a paper/report pack for the selected player",
            LuaMAiDirectorEuiMsg.QuickAiChat => "send one AI Director chat message",
            LuaMAiDirectorEuiMsg.QuickAnnouncement => "send one player-visible AI Director announcement",
            LuaMAiDirectorEuiMsg.QuickAiBaseDiagnostics => "show AI-base diagnostics without spawning anything",
            LuaMAiDirectorEuiMsg.QuickAiBasePlan => "show AI-base development plan and next-step queue",
            LuaMAiDirectorEuiMsg.QuickAiBaseAutofix => "run one AI-base autofix for the top diagnostic issue",
            LuaMAiDirectorEuiMsg.QuickAiBaseMine => "dispatch AI-base mining robots to gather resources",
            LuaMAiDirectorEuiMsg.QuickAiBaseBuild => "dispatch AI-base builder robots to construct and repair",
            LuaMAiDirectorEuiMsg.QuickAiBaseDevelop => "deploy the AI base and dispatch miner plus builder crews",
            LuaMAiDirectorEuiMsg.QuickGatewayShip
                or LuaMAiDirectorEuiMsg.QuickGatewayShipSelected
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTriage
                or LuaMAiDirectorEuiMsg.QuickGatewayShipHammerhead
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTzipora
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTokarev => "spawn one selected ship build near the admin through local server action",
            LuaMAiDirectorEuiMsg.QuickStatus => "show local sector status only",
            LuaMAiDirectorEuiMsg.QuickHistory => "show local sector history only",
            LuaMAiDirectorEuiMsg.QuickRecommendations => "show local AI recommendations only",
            _ => "run a LuaM AI quick action",
        };
    }

    private static string GetQuickActionExecution(string action)
    {
        if (IsGatewayShipQuickAction(action))
            return "local server action only; no external provider call; resolves a shipyard vessel or shuttle gameMap and loads its grid near the admin";

        return action switch
        {
            LuaMAiDirectorEuiMsg.QuickAiChat or LuaMAiDirectorEuiMsg.QuickAnnouncement =>
                "AI chat command may create one player-visible message after local policy parsing",
            LuaMAiDirectorEuiMsg.QuickAiBaseAutofix
                or LuaMAiDirectorEuiMsg.QuickAiBaseMine
                or LuaMAiDirectorEuiMsg.QuickAiBaseBuild
                or LuaMAiDirectorEuiMsg.QuickAiBaseDevelop =>
                "local AI-base server action; no external provider call; may spawn a ship/grid, assign drones, and update AI-base memory",
            _ when IsSafeQuickAction(action) =>
                "read-only local result; no server-side world mutation expected",
            _ =>
                "AI admin chat command; provider may assist if configured, but local parser and validators execute only known actions",
        };
    }

    private static string GetQuickActionRisk(string action)
    {
        if (IsGatewayShipQuickAction(action))
            return "high; spawns a ship/grid and can affect player movement";

        return action switch
        {
            LuaMAiDirectorEuiMsg.QuickPersonalDanger
                or LuaMAiDirectorEuiMsg.QuickAiPressure
                or LuaMAiDirectorEuiMsg.QuickSubspaceRift
                or LuaMAiDirectorEuiMsg.QuickSubspaceRoute
                or LuaMAiDirectorEuiMsg.QuickSyntheticControl
                or LuaMAiDirectorEuiMsg.QuickAnnouncement
                or LuaMAiDirectorEuiMsg.QuickAiBaseAutofix
                or LuaMAiDirectorEuiMsg.QuickAiBaseMine
                or LuaMAiDirectorEuiMsg.QuickAiBaseBuild
                or LuaMAiDirectorEuiMsg.QuickAiBaseDevelop => "high; player-visible or round-affecting action",
            _ when IsSafeQuickAction(action) => "low; advice/status/history only",
            _ => "medium; creates or changes local sector state",
        };
    }

    private static string BuildQuickActionTargetPreview(string action, string rawTarget)
    {
        var hasTarget = !string.IsNullOrWhiteSpace(rawTarget);
        if (QuickActionRequiresTarget(action))
        {
            return hasTarget
                ? "selected player; raw user id withheld from preview"
                : "selected player required; no target is currently selected";
        }

        return hasTarget
            ? "round/sector scoped; optional selected player present but raw user id withheld from preview"
            : "round/sector scoped; no player target required";
    }

    private static bool QuickActionRequiresTarget(string action)
    {
        return action is LuaMAiDirectorEuiMsg.QuickEvent
            or LuaMAiDirectorEuiMsg.QuickPersonalPressure
            or LuaMAiDirectorEuiMsg.QuickPersonalDanger
            or LuaMAiDirectorEuiMsg.QuickSubspaceRift
            or LuaMAiDirectorEuiMsg.QuickSubspaceRoute
            or LuaMAiDirectorEuiMsg.QuickSpawnBeacon
            or LuaMAiDirectorEuiMsg.QuickSpawnScanner
            or LuaMAiDirectorEuiMsg.QuickMonolithKit
            or LuaMAiDirectorEuiMsg.QuickPaperPack;
    }

    private static string GetQuickActionDuration(string action)
    {
        return action switch
        {
            LuaMAiDirectorEuiMsg.QuickRadiation
                or LuaMAiDirectorEuiMsg.QuickSensorDrift
                or LuaMAiDirectorEuiMsg.QuickComms
                or LuaMAiDirectorEuiMsg.QuickMonolith => "sector condition persists until cleared, resolved, or replaced by game logic",
            LuaMAiDirectorEuiMsg.QuickClearCondition
                or LuaMAiDirectorEuiMsg.QuickResolveLead
                or LuaMAiDirectorEuiMsg.QuickCleanupMarkers => "one cleanup/resolve operation",
            LuaMAiDirectorEuiMsg.QuickAiChat
                or LuaMAiDirectorEuiMsg.QuickAnnouncement => "one player-visible message",
            LuaMAiDirectorEuiMsg.QuickAiBaseAutofix => "single autofix attempt; attempt memory persists in LuaM sector memory",
            LuaMAiDirectorEuiMsg.QuickAiBaseMine
                or LuaMAiDirectorEuiMsg.QuickAiBaseBuild
                or LuaMAiDirectorEuiMsg.QuickAiBaseDevelop => "single dispatch; spawned ships/drones persist by normal game rules",
            LuaMAiDirectorEuiMsg.QuickGatewayShip
                or LuaMAiDirectorEuiMsg.QuickGatewayShipSelected
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTriage
                or LuaMAiDirectorEuiMsg.QuickGatewayShipHammerhead
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTzipora
                or LuaMAiDirectorEuiMsg.QuickGatewayShipTokarev => "single spawn; spawned entities persist by normal game rules",
            _ when IsSafeQuickAction(action) => "read-only response",
            _ => "single manual action",
        };
    }

    private static string GetImpactLabel(LogImpact impact)
    {
        return impact.ToString();
    }

    private static string TrimForLog(string value)
    {
        value = value.Trim();
        return value.Length <= 80 ? value : value[..80];
    }

    private static string CompactForLog(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private void AppendChat(string line)
    {
        _chatTranscript = string.IsNullOrWhiteSpace(_chatTranscript)
            ? line
            : $"{_chatTranscript}\n\n{line}";

        if (_chatTranscript.Length > 6000)
            _chatTranscript = _chatTranscript[^6000..];
    }

    private void RemovePendingChatLine(string pendingLine)
    {
        var pending = $"\n\n{pendingLine}";
        if (_chatTranscript.EndsWith(pending))
            _chatTranscript = _chatTranscript[..^pending.Length];
    }

    private void AppendReviewHistory(string title, string body)
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            return;

        _reviewHistoryCount++;
        var header = $"#{_reviewHistoryCount} {DateTime.UtcNow:HH:mm:ss} UTC - {title}";
        var entry = $"{header}\n{body}";
        _reviewHistory = string.IsNullOrWhiteSpace(_reviewHistory)
            ? entry
            : $"{entry}\n\n---\n\n{_reviewHistory}";

        if (_reviewHistory.Length > 12000)
            _reviewHistory = _reviewHistory[..12000].TrimEnd();
    }

    private void AppendAiActionHistory(PendingAiDirectorAction pending, string decision, string result)
    {
        _aiActionHistoryCount++;
        var entry = BuildAiActionHistoryEntry(
            _aiActionHistoryCount,
            decision,
            pending.ActionKey,
            pending.Title,
            pending.Impact,
            pending.Detail,
            result);

        _aiActionHistory = AppendBoundedAiActionHistory(_aiActionHistory, entry);
    }

    private static string BuildAiActionHistoryEntry(
        int sequence,
        string decision,
        string actionKey,
        string title,
        LogImpact impact,
        string preview,
        string result)
    {
        var output = new StringBuilder();
        output.AppendLine($"#{sequence} {DateTime.UtcNow:HH:mm:ss} UTC - AI action history");
        output.AppendLine($"- decision={CompactForActionHistory(decision)}");
        output.AppendLine($"- action={CompactForActionHistory(actionKey)}");
        output.AppendLine($"- title={CompactForActionHistory(title)}");
        output.AppendLine($"- impact={impact}");
        output.AppendLine($"- result={CompactForActionHistory(result)}");
        output.AppendLine($"- preview={CompactForActionHistory(preview)}");
        output.AppendLine("- privacy=bounded local admin history; raw provider body, gateway URL, bearer token, raw prompt text, coordinates, and raw player/admin identifiers are not stored here.");
        return output.ToString().TrimEnd();
    }

    private static string AppendBoundedAiActionHistory(string currentHistory, string entry)
    {
        var history = string.IsNullOrWhiteSpace(currentHistory)
            ? entry
            : $"{entry}\n\n---\n\n{currentHistory}";

        return history.Length <= AiActionHistoryMaxLength
            ? history
            : history[..AiActionHistoryMaxLength].TrimEnd();
    }

    private static string CompactForActionHistory(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1600 ? value : value[..1600];
    }

    private sealed record PendingAiDirectorAction(
        string Id,
        string ActionKey,
        string Title,
        string Detail,
        Func<Task> ExecuteAsync,
        LogImpact Impact);
}
