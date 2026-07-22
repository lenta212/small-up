using System.Linq;
using System.Reflection;
using Content.Client._LuaM.Administration;
using Content.Shared._LuaM.Administration;
using NUnit.Framework;
using Robust.UnitTesting;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMAiDirectorWindowTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    [Test]
    public void OutcomeCopyTextStaysUsefulAndRedactsSensitiveValues()
    {
        var state = new LuaMAiDirectorEuiState
        {
            AiOutcomeGroup = "transport failed",
            AiOutcomeSummary = "Provider call failed for admin chat at https://luam.invalid/chat token=secret-provider-token GPS: 123, 456 11111111-2222-3333-4444-555555555555",
            AiNextStepHint = "Check local gateway; bearer=secret-bearer-value",
            AiOutcomeStatus = "last gateway transport failure=network error; purpose=admin chat; provider call failed; sensitive details withheld.",
            HasPendingConfirmation = false,
            GatewayConfigured = true,
            GatewayBudgetWindowUsed = 2,
            GatewayBudgetWindowLimit = 10,
            GatewayBudgetWindowRemaining = 8,
            GatewayBudgetRoundUsed = 3,
            GatewayBudgetRoundLimit = 60,
            GatewayBudgetRoundRemaining = 57,
            GatewayBudgetRetrySeconds = 0,
            GatewayBlockUnsafeInputs = 1,
            GatewayBlockBudgets = 2,
            GatewayBlockInvalidSchemas = 3,
            GatewayBlockForbiddenActions = 4,
            GatewayBlockLocalValidations = 5,
        };

        var summary = InvokeBuildOutcomeCopyText(state);

        Assert.That(summary, Does.Contain("LuaM AI outcome summary"));
        Assert.That(summary, Does.Contain("group=transport failed"));
        Assert.That(summary, Does.Contain("summary=Provider call failed"));
        Assert.That(summary, Does.Contain("[redacted-url]"));
        Assert.That(summary, Does.Contain("token=[redacted]"));
        Assert.That(summary, Does.Contain("GPS [withheld]"));
        Assert.That(summary, Does.Contain("[redacted-id]"));
        Assert.That(summary, Does.Contain("next=Check local gateway"));
        Assert.That(summary, Does.Contain("gatewayBudget=window 2/10 used, 8 left; round 3/60 used, 57 left; retrySeconds=0"));
        Assert.That(summary, Does.Contain("gatewayBlocks=unsafeInput 1, budget 2, invalidSchema 3, forbiddenAction 4, localValidation 5"));
        Assert.That(summary, Does.Contain("privacy=copy-safe local admin summary"));
        Assert.That(summary, Does.Not.Contain("https://luam.invalid"));
        Assert.That(summary, Does.Not.Contain("secret-provider-token"));
        Assert.That(summary, Does.Not.Contain("secret-bearer-value"));
        Assert.That(summary, Does.Not.Contain("123, 456"));
        Assert.That(summary, Does.Not.Contain("11111111-2222-3333-4444-555555555555"));
    }

    [Test]
    public void ReadinessChecklistExplainsLocalOnlyAndBlockedStates()
    {
        var localOnly = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = false,
            CanRunServerActions = false,
            GatewayAuditRedactions = 3,
            GatewayAuditIdRedactions = 1,
            GatewayAuditSecretRedactions = 1,
            GatewayAuditLocationRedactions = 1,
            GatewaySharedContext = ["counts only"],
            GatewayWithheldContext = ["raw identifiers"],
            GatewayRagAllowedSources = 0,
            GatewayRagDeniedSources = 6,
            AiNextStepHint = "Configure the OpenAI-compatible gateway or use local Status.",
        };
        var waitingBudgetBlocked = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            HasPendingConfirmation = true,
            GatewayBudgetWindowUsed = 10,
            GatewayBudgetWindowLimit = 10,
            GatewayBudgetWindowRemaining = 0,
            GatewayBudgetRoundUsed = 4,
            GatewayBudgetRoundLimit = 60,
            GatewayBudgetRoundRemaining = 56,
            GatewayBudgetRetrySeconds = 30,
            GatewayRagAllowedSources = 2,
            GatewayRagDeniedSources = 1,
        };

        var localReadiness = InvokeBuildReadiness(localOnly);
        var waitingReadiness = InvokeBuildReadiness(waitingBudgetBlocked);

        Assert.That(localReadiness, Does.Contain("AI readiness: limited"));
        Assert.That(localReadiness, Does.Contain("gateway: limited - API missing"));
        Assert.That(localReadiness, Does.Contain("local safe commands"));
        Assert.That(localReadiness, Does.Contain("server actions: blocked"));
        Assert.That(localReadiness, Does.Contain("budget: not used"));
        Assert.That(localReadiness, Does.Contain("privacy: ready"));
        Assert.That(localReadiness, Does.Contain("redactions=3"));
        Assert.That(localReadiness, Does.Contain("rag: limited"));
        Assert.That(localReadiness, Does.Contain("denied sources=6"));
        Assert.That(localReadiness, Does.Contain("next: limited - Configure the OpenAI-compatible gateway"));

        Assert.That(waitingReadiness, Does.Contain("AI readiness: waiting confirmation"));
        Assert.That(waitingReadiness, Does.Contain("gateway: ready"));
        Assert.That(waitingReadiness, Does.Contain("server actions: ready"));
        Assert.That(waitingReadiness, Does.Contain("request state: waiting"));
        Assert.That(waitingReadiness, Does.Contain("budget: blocked"));
        Assert.That(waitingReadiness, Does.Contain("window 10/10 used, 0 left"));
        Assert.That(waitingReadiness, Does.Contain("retrySeconds=30"));
        Assert.That(waitingReadiness, Does.Contain("rag: ready"));
    }

    [Test]
    public void OperatorStateNamesBusySafeGatedAndDirectExecutionModes()
    {
        var busy = InvokeGetOperatorStateLocKey(new LuaMAiDirectorEuiState
        {
            RequestInFlight = true,
            CanRunServerActions = true,
        });
        var confirmation = InvokeGetOperatorStateLocKey(new LuaMAiDirectorEuiState
        {
            HasPendingConfirmation = true,
            CanRunServerActions = true,
        });
        var readOnly = InvokeGetOperatorStateLocKey(new LuaMAiDirectorEuiState
        {
            CanRunServerActions = false,
        });
        var gated = InvokeGetOperatorStateLocKey(new LuaMAiDirectorEuiState
        {
            CanRunServerActions = true,
        });
        var direct = InvokeGetOperatorStateLocKey(new LuaMAiDirectorEuiState
        {
            GameMasterModeEnabled = true,
            CanRunServerActions = true,
        });

        Assert.That(busy, Is.EqualTo("luam-ai-director-operator-state-busy"));
        Assert.That(confirmation, Is.EqualTo("luam-ai-director-operator-state-confirmation"));
        Assert.That(readOnly, Is.EqualTo("luam-ai-director-operator-state-read-only"));
        Assert.That(gated, Is.EqualTo("luam-ai-director-operator-state-gated"));
        Assert.That(direct, Is.EqualTo("luam-ai-director-operator-state-direct"));
    }

    [Test]
    public void OperationsAuditSummarizesControlsAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        const string history = """
            #2 12:02:00 UTC - AI action history
            - decision=confirmed
            - action=generate-process
            - title=Generate AI process
            - impact=High
            - result=blocked by local validation; action did not run
            - preview=action=generate-process; UseGateway=True; instructionLength=42
            - privacy=bounded local admin history

            ---

            #1 12:01:00 UTC - AI action history
            - decision=confirmed
            - action=quick:status
            - title=AI quick action: status
            - impact=Low
            - result=confirmed; local status returned
            - preview=action=status; local only
            - privacy=bounded local admin history
            """;
        var state = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            HasPendingConfirmation = true,
            PendingConfirmationId = targetId,
            PendingConfirmationTitle = "Spawn gateway ship through https://luam.invalid token=secret-provider-token",
            PendingConfirmationDetail = $"Confirm near GPS: 123, 456 for {targetId}",
            AiOutcomeGroup = "provider rejected output",
            AiOutcomeSummary = $"Provider returned bad body from https://luam.invalid token=secret-provider-token GPS: 123, 456 {targetId}",
            AiNextStepHint = "Cancel pending action and review token=secret-bearer-value.",
            GatewayBudgetWindowUsed = 10,
            GatewayBudgetWindowLimit = 10,
            GatewayBudgetWindowRemaining = 0,
            GatewayBudgetRoundUsed = 4,
            GatewayBudgetRoundLimit = 60,
            GatewayBudgetRoundRemaining = 56,
            GatewayBudgetRetrySeconds = 30,
            GatewayAuditUnsafeInputBlocks = 1,
            GatewayAuditBudgetBlocks = 2,
            GatewayAuditProviderOutputBlocks = 3,
            GatewayAuditTransportFailures = 4,
            GatewayBlockUnsafeInputs = 5,
            GatewayBlockBudgets = 6,
            GatewayBlockInvalidSchemas = 7,
            GatewayBlockForbiddenActions = 8,
            GatewayBlockLocalValidations = 9,
            GatewayAuditRedactions = 6,
            GatewayAuditIdRedactions = 1,
            GatewayAuditSecretRedactions = 2,
            GatewayAuditLocationRedactions = 3,
            GatewayRagAllowedSources = 4,
            GatewayRagDeniedSources = 5,
            GatewayBlockReasonSummary =
            [
                $"unsafe input from https://luam.invalid token=secret-provider-token GPS: 123, 456 {targetId}",
            ],
            ReviewHistoryCount = 2,
            AiActionHistory = history,
        };

        var audit = InvokeBuildOperationsAudit(state);

        Assert.That(audit, Does.Contain("AI operations audit: waiting approval"));
        Assert.That(audit, Does.Contain("gate: waiting approval"));
        Assert.That(audit, Does.Contain("pending: waiting"));
        Assert.That(audit, Does.Contain("id=[redacted-id]"));
        Assert.That(audit, Does.Contain("last outcome: provider rejected output"));
        Assert.That(audit, Does.Contain("gateway budget: blocked - window 10/10 used, 0 left"));
        Assert.That(audit, Does.Contain("gateway stops: watch - unsafe=1, budget=2, providerOutput=3, transport=4"));
        Assert.That(audit, Does.Contain("block reasons: recorded - unsafe=5, budget=6, invalidSchema=7, forbiddenAction=8, localValidation=9, recent=1"));
        Assert.That(audit, Does.Contain("action history: recorded - total=2, confirmed=2, canceled=0, blocked=1, gateway=1, serverImpact=1, localSafe=1"));
        Assert.That(audit, Does.Contain("review history: recorded - reviews=2"));
        Assert.That(audit, Does.Contain("external context: minimized - redactions=6, id=1, secrets=2, locations=3, ragAllowed=4, ragDenied=5"));
        Assert.That(audit, Does.Contain("recent block reasons:"));
        Assert.That(audit, Does.Contain("next: waiting approval - Cancel pending action and review token=[redacted]"));
        Assert.That(audit, Does.Contain("[redacted-url]"));
        Assert.That(audit, Does.Contain("token=[redacted]"));
        Assert.That(audit, Does.Contain("GPS [withheld]"));
        Assert.That(audit, Does.Not.Contain("https://luam.invalid"));
        Assert.That(audit, Does.Not.Contain("secret-provider-token"));
        Assert.That(audit, Does.Not.Contain("secret-bearer-value"));
        Assert.That(audit, Does.Not.Contain("123, 456"));
        Assert.That(audit, Does.Not.Contain(targetId));
    }

    [Test]
    public void RoundAuditFooterShowsLatestGateDecisionAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        const string history = """
            #2 12:02:00 UTC - AI action history
            - decision=confirmed
            - action=generate-process
            - title=Generate AI process
            - impact=High
            - result=blocked by local validation from https://luam.invalid token=secret-provider-token GPS: 123, 456 11111111-2222-3333-4444-555555555555
            - preview=action=generate-process; UseGateway=True; instructionLength=42
            - privacy=bounded local admin history
            """;
        var state = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            HasPendingConfirmation = true,
            GatewayBudgetWindowUsed = 10,
            GatewayBudgetWindowLimit = 10,
            GatewayBudgetWindowRemaining = 0,
            GatewayBudgetRoundUsed = 60,
            GatewayBudgetRoundLimit = 60,
            GatewayBudgetRoundRemaining = 0,
            GatewayBudgetRetrySeconds = 30,
            GatewayAuditUnsafeInputBlocks = 1,
            GatewayAuditBudgetBlocks = 2,
            GatewayAuditProviderOutputBlocks = 3,
            GatewayAuditTransportFailures = 4,
            GatewayBlockUnsafeInputs = 5,
            GatewayBlockBudgets = 6,
            GatewayBlockInvalidSchemas = 7,
            GatewayBlockForbiddenActions = 8,
            GatewayBlockLocalValidations = 9,
            GatewayBlockReasonSummary =
            [
                $"unsafe input from https://luam.invalid token=secret-provider-token GPS: 123, 456 {targetId}",
            ],
            AiActionHistory = history,
            AiNextStepHint = "Cancel pending action and rotate bearer=secret-bearer-value.",
        };

        var footer = InvokeBuildRoundAuditFooter(state);

        Assert.That(footer, Does.Contain("AI round audit footer: waiting approval"));
        Assert.That(footer, Does.Contain("gate=waiting-approval; pending=True; budget=blocked retrySeconds=30; stops=10; blocks=35"));
        Assert.That(footer, Does.Contain("lastDecision=decision=confirmed, action=generate-process, impact=High, result=blocked by local validation"));
        Assert.That(footer, Does.Contain("lastBlock=recent - unsafe input from [redacted-url] token=[redacted] GPS [withheld]"));
        Assert.That(footer, Does.Contain("next=Cancel pending action and rotate bearer=[redacted]"));
        Assert.That(footer, Does.Not.Contain("https://luam.invalid"));
        Assert.That(footer, Does.Not.Contain("secret-provider-token"));
        Assert.That(footer, Does.Not.Contain("secret-bearer-value"));
        Assert.That(footer, Does.Not.Contain("123, 456"));
        Assert.That(footer, Does.Not.Contain(targetId));
    }

    [Test]
    public void WorkflowPresetsGateQuickActionsRecommendationsAndExplainMode()
    {
        var state = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
        };

        var reviewSummary = InvokeBuildWorkflowPresetSummary("review-only", state);
        var lowRiskSummary = InvokeBuildWorkflowPresetSummary("low-risk-local", state);
        var gatedSummary = InvokeBuildWorkflowPresetSummary("gated-server-impact", state);

        Assert.That(reviewSummary, Does.Contain("AI workflow preset: review-only"));
        Assert.That(reviewSummary, Does.Contain("Generate, Apply advice, and server-impact quick actions are disabled"));
        Assert.That(lowRiskSummary, Does.Contain("AI workflow preset: low-risk-local"));
        Assert.That(lowRiskSummary, Does.Contain("low-risk non-server recommendations only"));
        Assert.That(gatedSummary, Does.Contain("AI workflow preset: gated-server-impact"));
        Assert.That(gatedSummary, Does.Contain("still require confirmation"));

        Assert.That(InvokeWorkflowForcesLocal("review-only"), Is.True);
        Assert.That(InvokeWorkflowForcesLocal("low-risk-local"), Is.True);
        Assert.That(InvokeWorkflowForcesLocal("gated-server-impact"), Is.False);

        Assert.That(InvokeWorkflowAllowsGenerateProcess("review-only"), Is.False);
        Assert.That(InvokeWorkflowAllowsGenerateProcess("low-risk-local"), Is.False);
        Assert.That(InvokeWorkflowAllowsGenerateProcess("gated-server-impact"), Is.True);

        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickStatus), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickRecommendations), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickHistory), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickAiBaseDiagnostics), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickAiBasePlan), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickAiBaseAutofix), Is.False);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickAiBaseAutopilot), Is.False);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickEvent), Is.False);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickGatewayShip), Is.False);
        Assert.That(InvokeWorkflowAllowsQuickAction("review-only", LuaMAiDirectorEuiMsg.QuickAnnouncement), Is.False);

        Assert.That(InvokeWorkflowAllowsQuickAction("low-risk-local", LuaMAiDirectorEuiMsg.QuickHistory), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("low-risk-local", LuaMAiDirectorEuiMsg.QuickAiBaseDiagnostics), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("low-risk-local", LuaMAiDirectorEuiMsg.QuickAiBaseAutofix), Is.False);
        Assert.That(InvokeWorkflowAllowsQuickAction("low-risk-local", LuaMAiDirectorEuiMsg.QuickAiBaseAutopilot), Is.False);
        Assert.That(InvokeWorkflowAllowsQuickAction("low-risk-local", LuaMAiDirectorEuiMsg.QuickPersonalPressure), Is.False);
        Assert.That(InvokeWorkflowAllowsRecommendationAction("low-risk-local", false, "low"), Is.True);
        Assert.That(InvokeWorkflowAllowsRecommendationAction("low-risk-local", false, "medium"), Is.False);
        Assert.That(InvokeWorkflowAllowsRecommendationAction("low-risk-local", true, "low"), Is.False);

        Assert.That(InvokeWorkflowAllowsQuickAction("gated-server-impact", LuaMAiDirectorEuiMsg.QuickGatewayShip), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("gated-server-impact", LuaMAiDirectorEuiMsg.QuickPersonalDanger), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("gated-server-impact", LuaMAiDirectorEuiMsg.QuickAiBaseAutofix), Is.True);
        Assert.That(InvokeWorkflowAllowsQuickAction("gated-server-impact", LuaMAiDirectorEuiMsg.QuickAiBaseAutopilot), Is.True);
        Assert.That(InvokeWorkflowAllowsRecommendationAction("gated-server-impact", true, "high"), Is.True);
    }

    [Test]
    public void WorkflowTransitionSummaryExplainsRiskIncreaseAndTightening()
    {
        var gatewayReady = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
        };
        var apiMissing = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = false,
            CanRunServerActions = false,
        };

        var elevated = InvokeBuildWorkflowTransitionSummary(
            "review-only",
            "gated-server-impact",
            gatewayReady,
            true);
        var tightened = InvokeBuildWorkflowTransitionSummary(
            "gated-server-impact",
            "review-only",
            gatewayReady,
            false);
        var unchangedApiMissing = InvokeBuildWorkflowTransitionSummary(
            "gated-server-impact",
            "gated-server-impact",
            apiMissing,
            false);

        Assert.That(elevated, Does.Contain("Workflow transition: review-only -> gated-server-impact"));
        Assert.That(elevated, Does.Contain("risk change: elevated - Generate, server-impact quick actions, high-risk recommendations"));
        Assert.That(elevated, Does.Contain("gateway change: expanded - OpenAI-compatible API can be used for generated process with minimized context"));
        Assert.That(elevated, Does.Contain("server-impact change: gated-expanded - server-impact controls are visible but still require preview, validation, confirmation, and audit history; gate=open"));
        Assert.That(elevated, Does.Contain("next: treat this as a deliberate risk increase; re-check Gateway exposure"));

        Assert.That(tightened, Does.Contain("Workflow transition: gated-server-impact -> review-only"));
        Assert.That(tightened, Does.Contain("risk change: tightened - generated server-impact process and server-impact quick actions are disabled by preset"));
        Assert.That(tightened, Does.Contain("gateway change: reduced - selected preset forces generated processes local even when gateway is configured"));
        Assert.That(tightened, Does.Contain("server-impact change: blocked-by-preset - server-impact quick actions and generated process are hidden/disabled; gate=open"));
        Assert.That(tightened, Does.Contain("next: continue with local advice/status/history"));

        Assert.That(unchangedApiMissing, Does.Contain("Workflow transition: gated-server-impact -> gated-server-impact"));
        Assert.That(unchangedApiMissing, Does.Contain("risk change: unchanged - current workflow controls remain in effect"));
        Assert.That(unchangedApiMissing, Does.Contain("gateway change: off - API missing"));
        Assert.That(unchangedApiMissing, Does.Contain("server-impact change: blocked - server-impact actions are currently disabled by server/admin gate; gate=server-actions-blocked"));
    }

    [Test]
    public void RecommendationApplyStateExplainsDisabledAndReadyReasons()
    {
        var noSelection = InvokeBuildRecommendationApplyState(
            "gated-server-impact",
            "",
            false,
            false,
            "",
            1,
            true,
            false,
            false);
        var requestBusy = InvokeBuildRecommendationApplyState(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            false,
            true,
            "high",
            1,
            true,
            true,
            false);
        var pending = InvokeBuildRecommendationApplyState(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            false,
            true,
            "high",
            1,
            true,
            false,
            true);
        var reviewOnly = InvokeBuildRecommendationApplyState(
            "review-only",
            LuaMAiDirectorEuiMsg.QuickStatus,
            false,
            false,
            "low",
            1,
            true,
            false,
            false);
        var lowRiskBlocksServer = InvokeBuildRecommendationApplyState(
            "low-risk-local",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            false,
            true,
            "high",
            1,
            true,
            false,
            false);
        var lowRiskBlocksMedium = InvokeBuildRecommendationApplyState(
            "low-risk-local",
            LuaMAiDirectorEuiMsg.QuickPersonalPressure,
            false,
            false,
            "medium",
            1,
            true,
            false,
            false);
        var targetMissing = InvokeBuildRecommendationApplyState(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickPersonalPressure,
            true,
            false,
            "medium",
            0,
            true,
            false,
            false);
        var serverBlocked = InvokeBuildRecommendationApplyState(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            false,
            true,
            "high",
            1,
            false,
            false,
            false);
        var ready = InvokeBuildRecommendationApplyState(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            false,
            true,
            "high",
            1,
            true,
            false,
            false);

        Assert.That(noSelection, Does.Contain("Apply advice: disabled - no recommendation action is selected"));
        Assert.That(noSelection, Does.Contain("preset=gated-server-impact"));
        Assert.That(requestBusy, Does.Contain("request is already in flight"));
        Assert.That(pending, Does.Contain("waiting for confirm/cancel"));
        Assert.That(reviewOnly, Does.Contain("workflow preset=review-only blocks all Apply advice actions"));
        Assert.That(lowRiskBlocksServer, Does.Contain("workflow preset=low-risk-local blocks server-impact recommendations"));
        Assert.That(lowRiskBlocksServer, Does.Contain("selected risk=high"));
        Assert.That(lowRiskBlocksMedium, Does.Contain("only allows low-risk local recommendations"));
        Assert.That(lowRiskBlocksMedium, Does.Contain("selected risk=medium"));
        Assert.That(targetMissing, Does.Contain("requires a target"));
        Assert.That(serverBlocked, Does.Contain("requires server confirmation"));
        Assert.That(serverBlocked, Does.Contain("server-impact actions are currently blocked"));
        Assert.That(ready, Does.Contain("Apply advice: ready"));
        Assert.That(ready, Does.Contain("action=gateway-ship"));
        Assert.That(ready, Does.Contain("mode=server-confirmation"));
        Assert.That(ready, Does.Contain("risk=high"));
    }

    [Test]
    public void RecommendationExplanationShowsWhyBlockedAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var ready = InvokeBuildRecommendationExplanation(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            "gateway ship",
            $"Gate ship https://luam.invalid/title token=secret-provider-token {targetId}",
            "Spawn a controlled review ship near GPS: 123, 456 after checking lead evidence.",
            false,
            true,
            "high",
            "server-impact action with visible confirmation",
            "high",
            88,
            "lead and route evidence are aligned",
            1,
            true,
            false,
            false);
        var presetBlocked = InvokeBuildRecommendationExplanation(
            "low-risk-local",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            "gateway ship",
            "Gate ship",
            "Server-impact spawn",
            false,
            true,
            "high",
            "server-impact action",
            "medium",
            60,
            "needs admin confirmation",
            1,
            true,
            false,
            false);
        var missingTarget = InvokeBuildRecommendationExplanation(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickPersonalPressure,
            "target pressure",
            "Target pressure",
            "Needs selected player",
            true,
            false,
            "medium",
            "target-specific pressure",
            "low",
            42,
            "weak target signal",
            0,
            true,
            false,
            false);

        Assert.That(ready, Does.Contain("Recommendation explanation: ready-server-confirmation"));
        Assert.That(ready, Does.Contain("why this action: Gate ship [redacted-url] token=[redacted] [redacted-id] - Spawn a controlled review ship near GPS [withheld] after checking lead evidence."));
        Assert.That(ready, Does.Contain("action: gateway ship; preset=gated-server-impact"));
        Assert.That(ready, Does.Contain("risk: high - server-impact action with visible confirmation"));
        Assert.That(ready, Does.Contain("confidence: high/88% - lead and route evidence are aligned"));
        Assert.That(ready, Does.Contain("requirements: target=not-required, server=confirmation-required"));
        Assert.That(ready, Does.Contain("blocked reason: not blocked; server confirmation still required before execution"));
        Assert.That(ready, Does.Contain("next: review risk/confidence and confirmation preview before execution"));
        Assert.That(ready, Does.Not.Contain("https://luam.invalid"));
        Assert.That(ready, Does.Not.Contain("secret-provider-token"));
        Assert.That(ready, Does.Not.Contain("123, 456"));
        Assert.That(ready, Does.Not.Contain(targetId));

        Assert.That(presetBlocked, Does.Contain("Recommendation explanation: blocked-by-preset"));
        Assert.That(presetBlocked, Does.Contain("blocked reason: workflow preset=low-risk-local blocks server-impact recommendations"));
        Assert.That(presetBlocked, Does.Contain("next: stay local or switch to gated-server-impact only after reviewing risk"));

        Assert.That(missingTarget, Does.Contain("Recommendation explanation: blocked-missing-target"));
        Assert.That(missingTarget, Does.Contain("requirements: target=missing, server=not-required"));
        Assert.That(missingTarget, Does.Contain("blocked reason: selected recommendation requires a target but no valid target is available"));
    }

    [Test]
    public void RecommendationReviewSummaryKeepsPrimaryUiCompactAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var summary = InvokeBuildRecommendationReviewSummary(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            $"gateway ship https://luam.invalid/action token=secret-provider-token {targetId}",
            false,
            true,
            "high",
            "high",
            88,
            $"activePlayers=3; GPS: 123, 456 {targetId}",
            "local sector story + gateway source class; bearer=secret-bearer-value",
            "gateway,sector",
            1,
            true,
            false,
            false);
        var noSelection = InvokeBuildRecommendationReviewSummary(
            "review-only",
            string.Empty,
            string.Empty,
            false,
            false,
            string.Empty,
            string.Empty,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            false,
            false,
            false);

        Assert.That(summary, Does.Contain("Selected recommendation review: ready-server-confirmation"));
        Assert.That(summary, Does.Contain("action=gateway ship [redacted-url] token=[redacted] [redacted-id]"));
        Assert.That(summary, Does.Contain("mode=server-confirmation; risk=high; confidence=high/88%"));
        Assert.That(summary, Does.Contain("requirements: target=not-required, server=confirmation-required"));
        Assert.That(summary, Does.Contain("source=local sector story + gateway source class; bearer=[redacted]"));
        Assert.That(summary, Does.Contain("evidence=activePlayers=3; GPS [withheld] [redacted-id]"));
        Assert.That(summary, Does.Not.Contain("source check: warning-"));
        Assert.That(summary, Does.Contain("open details for explanation, checklist, and provenance"));
        Assert.That(summary, Does.Not.Contain("https://luam.invalid"));
        Assert.That(summary, Does.Not.Contain("secret-provider-token"));
        Assert.That(summary, Does.Not.Contain("secret-bearer-value"));
        Assert.That(summary, Does.Not.Contain("123, 456"));
        Assert.That(summary, Does.Not.Contain(targetId));

        Assert.That(noSelection, Does.Contain("Selected recommendation review: no-selection"));
        Assert.That(noSelection, Does.Contain("action=none"));
    }

    [Test]
    public void RecommendationCommandPreviewShowsImpactGateAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var serverImpact = InvokeBuildRecommendationCommandPreview(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            $"gateway ship https://luam.invalid/action token=secret-provider-token {targetId} GPS: 123, 456",
            false,
            true,
            "high",
            "ship/grid spawn near GPS: 123, 456 token=secret-provider-token",
            1,
            true,
            false,
            false);
        var localSafe = InvokeBuildRecommendationCommandPreview(
            "low-risk-local",
            LuaMAiDirectorEuiMsg.QuickStatus,
            "status",
            false,
            false,
            "low",
            "read-only status",
            1,
            true,
            false,
            false);
        var blocked = InvokeBuildRecommendationCommandPreview(
            "review-only",
            LuaMAiDirectorEuiMsg.QuickPersonalPressure,
            "target pressure",
            true,
            false,
            "medium",
            "target-scoped advice",
            0,
            true,
            false,
            false);

        Assert.That(serverImpact, Does.Contain("Safe command preview: ready-server-confirmation"));
        Assert.That(serverImpact, Does.Contain("command path: quick-action=gateway-ship"));
        Assert.That(serverImpact, Does.Contain("suggested=gateway ship [redacted-url] token=[redacted] [redacted-id] GPS [withheld]"));
        Assert.That(serverImpact, Does.Contain("likely impact: high - server-impact action"));
        Assert.That(serverImpact, Does.Contain("risk reason=ship/grid spawn near GPS [withheld] token=[redacted]"));
        Assert.That(serverImpact, Does.Contain("target boundary: not-required"));
        Assert.That(serverImpact, Does.Contain("execution gate: local validation -> confirmation preview -> explicit Confirm"));
        Assert.That(serverImpact, Does.Contain("Apply advice does not execute directly"));
        Assert.That(serverImpact, Does.Contain("privacy: raw ids, exact coordinates, gateway URL, bearer token, provider body, and hidden context stay out of this preview"));
        Assert.That(serverImpact, Does.Not.Contain("https://luam.invalid"));
        Assert.That(serverImpact, Does.Not.Contain("secret-provider-token"));
        Assert.That(serverImpact, Does.Not.Contain("123, 456"));
        Assert.That(serverImpact, Does.Not.Contain(targetId));

        Assert.That(localSafe, Does.Contain("Safe command preview: ready-local-safe"));
        Assert.That(localSafe, Does.Contain("likely impact: low - local/read-only advice"));
        Assert.That(localSafe, Does.Contain("execution gate: local-safe Apply advice; no server command preview"));

        Assert.That(blocked, Does.Contain("Safe command preview: blocked-by-preset"));
        Assert.That(blocked, Does.Contain("likely impact: medium - target-scoped local advice"));
        Assert.That(blocked, Does.Contain("target boundary: missing target; choose a valid player target first"));
        Assert.That(blocked, Does.Contain("blocked reason: workflow preset=review-only blocks Apply advice"));
    }

    [Test]
    public void RecommendationOperatorChecklistShowsHumanGateForHighRiskAdvice()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var serverImpact = InvokeBuildRecommendationOperatorChecklist(
            "gated-server-impact",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            $"gateway ship https://luam.invalid token=secret-provider-token GPS: 123, 456 {targetId}",
            false,
            true,
            "high",
            "high",
            88,
            1,
            true,
            false,
            false);
        var localSafe = InvokeBuildRecommendationOperatorChecklist(
            "low-risk-local",
            LuaMAiDirectorEuiMsg.QuickStatus,
            "status",
            false,
            false,
            "low",
            "high",
            91,
            1,
            true,
            false,
            false);
        var blocked = InvokeBuildRecommendationOperatorChecklist(
            "low-risk-local",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            "gateway ship",
            false,
            true,
            "high",
            "medium",
            60,
            1,
            true,
            false,
            false);

        Assert.That(serverImpact, Does.Contain("Operator checklist: server-impact-review"));
        Assert.That(serverImpact, Does.Contain("recommendation status=ready-server-confirmation"));
        Assert.That(serverImpact, Does.Contain("risk review: high - verify evidence and current round context; do not rely on AI alone"));
        Assert.That(serverImpact, Does.Contain("server review: confirmation-required"));
        Assert.That(serverImpact, Does.Contain("human step: apply opens server confirmation preview; confirm only after evidence, risk, and impact review"));
        Assert.That(serverImpact, Does.Contain("audit: apply/confirm/cancel decisions stay in local AI action history"));
        Assert.That(serverImpact, Does.Contain("[redacted-url]"));
        Assert.That(serverImpact, Does.Contain("token=[redacted]"));
        Assert.That(serverImpact, Does.Contain("GPS [withheld]"));
        Assert.That(serverImpact, Does.Contain("[redacted-id]"));
        Assert.That(serverImpact, Does.Not.Contain("https://luam.invalid"));
        Assert.That(serverImpact, Does.Not.Contain("secret-provider-token"));
        Assert.That(serverImpact, Does.Not.Contain("123, 456"));
        Assert.That(serverImpact, Does.Not.Contain(targetId));

        Assert.That(localSafe, Does.Contain("Operator checklist: local-safe-review"));
        Assert.That(localSafe, Does.Contain("human step: apply only if the local-safe recommendation still matches current round context"));

        Assert.That(blocked, Does.Contain("Operator checklist: not-ready - blocked-by-preset"));
        Assert.That(blocked, Does.Contain("human step: keep the advice local unless the workflow preset is deliberately changed"));
    }

    [Test]
    public void RecommendationProvenanceShowsBoundedEvidenceAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var provenance = InvokeBuildRecommendationProvenance(
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            $"activePlayers=3; source=https://luam.invalid/evidence token=secret-provider-token GPS: 123, 456 {targetId}",
            "local sector snapshot + gateway source class; bearer=secret-bearer-value",
            "gateway,sector",
            "ship/grid spawning",
            "gateway evidence matched route");
        var noSelection = InvokeBuildRecommendationProvenance(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

        Assert.That(provenance, Does.Contain("Recommendation provenance: local-bounded"));
        Assert.That(provenance, Does.Contain("evidence: activePlayers=3"));
        Assert.That(provenance, Does.Contain("[redacted-url]"));
        Assert.That(provenance, Does.Contain("token=[redacted]"));
        Assert.That(provenance, Does.Contain("GPS [withheld]"));
        Assert.That(provenance, Does.Contain("[redacted-id]"));
        Assert.That(provenance, Does.Contain("source scope: local sector snapshot + gateway source class; bearer=[redacted]"));
        Assert.That(provenance, Does.Contain("source classes: gateway,sector"));
        Assert.That(provenance, Does.Contain("source check: aligned - SourceClasses match source/evidence cues (gateway,sector)"));
        Assert.That(provenance, Does.Contain("risk basis: ship/grid spawning"));
        Assert.That(provenance, Does.Contain("confidence basis: gateway evidence matched route"));
        Assert.That(provenance, Does.Contain("raw player identifiers"));
        Assert.That(provenance, Does.Not.Contain("https://luam.invalid"));
        Assert.That(provenance, Does.Not.Contain("secret-provider-token"));
        Assert.That(provenance, Does.Not.Contain("secret-bearer-value"));
        Assert.That(provenance, Does.Not.Contain("123, 456"));
        Assert.That(provenance, Does.Not.Contain(targetId));

        Assert.That(noSelection, Does.Contain("Recommendation provenance: no-selection"));
    }

    [Test]
    public void RecommendationSourceClassCheckWarnsWhenTextAndMetadataDisagree()
    {
        var partial = InvokeBuildRecommendationProvenance(
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            "openLead=true; unresolvedHazard=1",
            "local sector story + hazard snapshot",
            "gateway,pressure",
            "server-impact action",
            "mixed evidence");
        var mismatch = InvokeBuildRecommendationProvenance(
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            "openLead=true; recentHistory=4",
            "local sector story only",
            "gateway",
            "server-impact action",
            "metadata disagrees");
        var fallback = InvokeBuildRecommendationProvenance(
            LuaMAiDirectorEuiMsg.QuickStatus,
            "activePlayers=2",
            "local status snapshot",
            "fallback-text",
            "local read-only",
            "fallback source parser");
        var aiBase = InvokeBuildRecommendationProvenance(
            LuaMAiDirectorEuiMsg.QuickStatus,
            "aiBaseDiagnostic=S4; drones=0; physical beacons=1",
            "local AI-base diagnostics: beacons, drones, ships, drops",
            LuaMAiDirectorRecommendationSourceClass.AiBase,
            "read-only diagnostics",
            "AI base evidence aligned");

        Assert.That(partial, Does.Contain("source classes: gateway,pressure"));
        Assert.That(partial, Does.Contain("source check: warning-partial - SourceClasses=gateway,pressure; textCues=sector,pressure; verify source evidence before applying"));

        Assert.That(mismatch, Does.Contain("source check: warning-mismatch - SourceClasses=gateway; textCues=sector; review evidence before applying"));

        Assert.That(fallback, Does.Contain("source classes: fallback-text"));
        Assert.That(fallback, Does.Contain("source check: fallback-text - explicit SourceClasses unavailable; verify source/evidence text before applying"));

        Assert.That(aiBase, Does.Contain("source classes: ai-base"));
        Assert.That(aiBase, Does.Contain("source check: aligned - SourceClasses match source/evidence cues (ai-base)"));
    }

    [Test]
    public void GenerateProcessStateExplainsDisabledAndReadyReasons()
    {
        var busy = InvokeBuildGenerateProcessState(
            "gated-server-impact",
            1,
            true,
            true,
            true,
            true,
            false);
        var pending = InvokeBuildGenerateProcessState(
            "gated-server-impact",
            1,
            true,
            true,
            true,
            false,
            true);
        var presetBlocked = InvokeBuildGenerateProcessState(
            "review-only",
            1,
            true,
            true,
            false,
            false,
            false);
        var targetMissing = InvokeBuildGenerateProcessState(
            "gated-server-impact",
            0,
            true,
            true,
            true,
            false,
            false);
        var serverBlocked = InvokeBuildGenerateProcessState(
            "gated-server-impact",
            1,
            false,
            false,
            false,
            false,
            false);
        var readyGateway = InvokeBuildGenerateProcessState(
            "gated-server-impact",
            1,
            true,
            true,
            true,
            false,
            false);
        var readyLocalFallback = InvokeBuildGenerateProcessState(
            "gated-server-impact",
            1,
            true,
            false,
            false,
            false,
            false);

        Assert.That(busy, Does.Contain("Generate process: disabled - request is already in flight"));
        Assert.That(pending, Does.Contain("waiting for confirm/cancel"));
        Assert.That(presetBlocked, Does.Contain("workflow preset=review-only blocks generated server-impact processes"));
        Assert.That(targetMissing, Does.Contain("no valid player target is available"));
        Assert.That(serverBlocked, Does.Contain("server-impact actions are currently blocked"));
        Assert.That(serverBlocked, Does.Contain("gatewayConfigured=False"));
        Assert.That(readyGateway, Does.Contain("Generate process: ready"));
        Assert.That(readyGateway, Does.Contain("mode=external-gateway"));
        Assert.That(readyGateway, Does.Contain("preset=gated-server-impact"));
        Assert.That(readyGateway, Does.Contain("preview/confirmation gates"));
        Assert.That(readyLocalFallback, Does.Contain("mode=local-fallback"));
    }

    [Test]
    public void SafeModeSummaryExplainsLocalActionsGatewayExposureAndServerGate()
    {
        var apiMissing = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = false,
            CanRunServerActions = false,
        };
        var gatewayReady = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
        };
        var pending = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            HasPendingConfirmation = true,
        };
        var busy = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            RequestInFlight = true,
        };

        var localOnly = InvokeBuildSafeModeSummary("review-only", apiMissing, false);
        var presetLocal = InvokeBuildSafeModeSummary("low-risk-local", gatewayReady, false);
        var gated = InvokeBuildSafeModeSummary("gated-server-impact", gatewayReady, true);
        var waiting = InvokeBuildSafeModeSummary("gated-server-impact", pending, true);
        var busySummary = InvokeBuildSafeModeSummary("gated-server-impact", busy, true);

        Assert.That(localOnly, Does.Contain("AI safe mode summary: local-only"));
        Assert.That(localOnly, Does.Contain("local available: Advice, Status, History"));
        Assert.That(localOnly, Does.Contain("external/gateway: off - API missing"));
        Assert.That(localOnly, Does.Contain("server-impact: blocked-by-preset"));
        Assert.That(localOnly, Does.Contain("raw player identifiers"));

        Assert.That(presetLocal, Does.Contain("AI safe mode summary: preset-local"));
        Assert.That(presetLocal, Does.Contain("external/gateway: off - workflow preset=low-risk-local forces local-only generated processes"));
        Assert.That(presetLocal, Does.Contain("server-impact: blocked-by-preset"));

        Assert.That(gated, Does.Contain("AI safe mode summary: gated"));
        Assert.That(gated, Does.Contain("external/gateway: available"));
        Assert.That(gated, Does.Contain("minimized context and privacy redactions"));
        Assert.That(gated, Does.Contain("server-impact: gated"));
        Assert.That(gated, Does.Contain("preview, local validation, confirmation, and audit history"));

        Assert.That(waiting, Does.Contain("AI safe mode summary: waiting-confirmation"));
        Assert.That(waiting, Does.Contain("confirm or cancel the pending action"));
        Assert.That(busySummary, Does.Contain("AI safe mode summary: busy"));
        Assert.That(busySummary, Does.Contain("wait for the active AI/admin request"));
    }

    [Test]
    public void GatewayExposureSummaryExplainsWhatCanLeaveServer()
    {
        var apiMissing = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = false,
            CanRunServerActions = false,
        };
        var gatewayReady = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
        };
        var busy = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            RequestInFlight = true,
        };
        var pending = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            HasPendingConfirmation = true,
        };

        var missingSummary = InvokeBuildGatewayExposureSummary("gated-server-impact", apiMissing, true);
        var presetLocal = InvokeBuildGatewayExposureSummary("low-risk-local", gatewayReady, true);
        var generateLocal = InvokeBuildGatewayExposureSummary("gated-server-impact", gatewayReady, false);
        var externalPossible = InvokeBuildGatewayExposureSummary("gated-server-impact", gatewayReady, true);
        var busySummary = InvokeBuildGatewayExposureSummary("gated-server-impact", busy, true);
        var pendingSummary = InvokeBuildGatewayExposureSummary("gated-server-impact", pending, true);

        Assert.That(missingSummary, Does.Contain("Gateway exposure: off - API missing"));
        Assert.That(missingSummary, Does.Contain("generate process: local-only - API missing"));
        Assert.That(missingSummary, Does.Contain("review/chat: unavailable - API missing"));
        Assert.That(missingSummary, Does.Contain("kept local: raw player/admin identifiers"));

        Assert.That(presetLocal, Does.Contain("Gateway exposure: off by preset - preset=low-risk-local"));
        Assert.That(presetLocal, Does.Contain("generate process: local-only - preset=low-risk-local forces generated processes off gateway"));
        Assert.That(presetLocal, Does.Contain("review/chat: available - gateway configured but preset only constrains generated processes"));

        Assert.That(generateLocal, Does.Contain("Gateway exposure: generate-local - gateway checkbox is off"));
        Assert.That(generateLocal, Does.Contain("generate process: local-fallback - gateway checkbox is off for generated process"));

        Assert.That(externalPossible, Does.Contain("Gateway exposure: possible - gateway may receive minimized generated-process context"));
        Assert.That(externalPossible, Does.Contain("generate process: possible - minimized generated-process context can be sent only through gated request"));
        Assert.That(externalPossible, Does.Contain("sent outside if used: minimized admin request shape"));
        Assert.That(externalPossible, Does.Contain("kept local: raw player/admin identifiers, exact coordinates, provider bodies, gateway URL, bearer tokens, raw prompt internals"));
        Assert.That(externalPossible, Does.Contain("gate: open; preset=gated-server-impact"));

        Assert.That(busySummary, Does.Contain("Gateway exposure: busy - request in flight"));
        Assert.That(busySummary, Does.Contain("review/chat: busy - active request in flight"));
        Assert.That(pendingSummary, Does.Contain("Gateway exposure: waiting-confirmation - confirm/cancel pending action before sending more"));
        Assert.That(pendingSummary, Does.Contain("review/chat: waiting - pending confirmation should be resolved before sending more"));
    }

    [Test]
    public void WorkflowCopyTextSummarizesExposureAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var state = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            GatewayAuditRedactions = 4,
            GatewayAuditIdRedactions = 1,
            GatewayAuditSecretRedactions = 2,
            GatewayAuditLocationRedactions = 1,
            GatewayRagAllowedSources = 3,
            GatewayRagDeniedSources = 2,
            AiOutcomeSummary = $"Provider reviewed https://luam.invalid token=secret-provider-token GPS: 123, 456 {targetId}",
            AiNextStepHint = "Stay local unless review is complete; bearer=secret-bearer-value.",
        };

        var summary = InvokeBuildWorkflowCopyText(
            "low-risk-local",
            state,
            false,
            1,
            LuaMAiDirectorEuiMsg.QuickStatus,
            false,
            false,
            "low",
            "status",
            "Status check",
            "Read current sector pressure without server impact",
            "local read-only advice",
            "high",
            91,
            "status uses local round counters",
            "activePlayers=2; openLead=true; recentHistory=4",
            "local sector status snapshot",
            "sector");

        Assert.That(summary, Does.Contain("LuaM AI workflow exposure summary"));
        Assert.That(summary, Does.Contain("preset=low-risk-local"));
        Assert.That(summary, Does.Contain("gate=open; gatewayConfigured=True; useGateway=False; targetAvailable=True"));
        Assert.That(summary, Does.Contain("externalContext=redactions 4, ids 1, secrets 2, locations 1, ragAllowed 3, ragDenied 2"));
        Assert.That(summary, Does.Contain("AI safe mode summary: preset-local"));
        Assert.That(summary, Does.Contain("external/gateway: off - workflow preset=low-risk-local forces local-only generated processes"));
        Assert.That(summary, Does.Contain("Generate process: disabled - workflow preset=low-risk-local blocks generated server-impact processes"));
        Assert.That(summary, Does.Contain("Apply advice: ready - action=status, mode=local-safe, risk=low, preset=low-risk-local."));
        Assert.That(summary, Does.Contain("Recommendation explanation: ready-local-safe"));
        Assert.That(summary, Does.Contain("why this action: Status check - Read current sector pressure without server impact"));
        Assert.That(summary, Does.Contain("risk: low - local read-only advice"));
        Assert.That(summary, Does.Contain("confidence: high/91% - status uses local round counters"));
        Assert.That(summary, Does.Contain("Safe command preview: ready-local-safe"));
        Assert.That(summary, Does.Contain("likely impact: low - local/read-only advice"));
        Assert.That(summary, Does.Contain("execution gate: local-safe Apply advice; no server command preview"));
        Assert.That(summary, Does.Contain("Operator checklist: local-safe-review"));
        Assert.That(summary, Does.Contain("human step: apply only if the local-safe recommendation still matches current round context"));
        Assert.That(summary, Does.Contain("Recommendation provenance: local-bounded"));
        Assert.That(summary, Does.Contain("evidence: activePlayers=2; openLead=true; recentHistory=4"));
        Assert.That(summary, Does.Contain("source scope: local sector status snapshot"));
        Assert.That(summary, Does.Contain("source classes: sector"));
        Assert.That(summary, Does.Contain("source check: aligned - SourceClasses match source/evidence cues (sector)"));
        Assert.That(summary, Does.Contain("lastOutcome=Provider reviewed [redacted-url] token=[redacted] GPS [withheld] [redacted-id]"));
        Assert.That(summary, Does.Contain("next=Stay local unless review is complete; bearer=[redacted]"));
        Assert.That(summary, Does.Contain("privacy=copy-safe workflow summary"));
        Assert.That(summary, Does.Not.Contain("https://luam.invalid"));
        Assert.That(summary, Does.Not.Contain("secret-provider-token"));
        Assert.That(summary, Does.Not.Contain("secret-bearer-value"));
        Assert.That(summary, Does.Not.Contain("123, 456"));
        Assert.That(summary, Does.Not.Contain(targetId));
    }

    [Test]
    public void AuditBundleCopyTextExportsMarkdownAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var state = new LuaMAiDirectorEuiState
        {
            GatewayConfigured = true,
            CanRunServerActions = true,
            ActivePlayers = 3,
            GatewayBudgetWindowLimit = 10,
            GatewayBudgetWindowRemaining = 8,
            GatewayBudgetRoundLimit = 60,
            GatewayBudgetRoundRemaining = 58,
            GatewayAuditRedactions = 5,
            GatewayAuditIdRedactions = 1,
            GatewayAuditSecretRedactions = 2,
            GatewayAuditLocationRedactions = 1,
            GatewayRagAllowedSources = 2,
            GatewayRagDeniedSources = 1,
            GatewayLastRequestShape = [$"request shape https://luam.invalid/gateway token=secret-provider-token GPS: 123, 456 {targetId}"],
            GatewayRagSourceShape = ["allowed local summaries only"],
            GatewaySharedContext = ["counts and risk summaries"],
            GatewayWithheldContext = ["raw player/admin identifiers"],
            GatewayPrivacyNotes = ["bearer=secret-bearer-value is withheld"],
            AiOutcomeGroup = "gateway review",
            AiOutcomeSummary = $"Provider reviewed https://luam.invalid/review token=secret-provider-token GPS: 123, 456 {targetId}",
            AiNextStepHint = "Re-check gate before impact.",
            AiOutcomeStatus = "provider response stored locally; bearer=secret-bearer-value",
            Recommendations =
            [
                new LuaMAiDirectorRecommendationEntry
                {
                    Title = "Gate ship for investigation",
                    Detail = $"Spawn review ship after checking https://luam.invalid/detail token=secret-provider-token {targetId}",
                    SuggestedAction = LuaMAiDirectorEuiMsg.QuickGatewayShip,
                    Priority = 1,
                    RiskLevel = "high",
                    RiskReason = "server-impact action",
                    ConfidenceBand = "high",
                    ConfidencePercent = 88,
                    ConfidenceReason = "clear lead and target",
                    EvidenceSummary = "activePlayers=3; openLead=true; unresolvedHazards=1",
                    SourceSummary = "local sector story + hazard snapshot",
                    RequiresServerAction = true,
                },
            ],
            AiActionHistoryCount = 1,
            AiActionHistory = """
                #1 12:01:00 UTC - AI action history
                - decision=confirmed
                - action=quick:gateway-ship
                - title=AI quick action: gateway ship
                - impact=High
                - result=confirmed after checking https://luam.invalid/history token=secret-provider-token GPS: 123, 456
                - preview=action=gateway-ship; UseGateway=True
                - privacy=bounded local admin history
                """,
        };

        var bundle = InvokeBuildAuditBundleCopyText(
            "gated-server-impact",
            state,
            true,
            1,
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            false,
            true,
            "high",
            LuaMAiDirectorEuiMsg.QuickGatewayShip,
            "Gate ship for investigation",
            "Spawn review ship after checking gateway evidence",
            "server-impact action",
            "high",
            88,
            "clear lead and target",
            "activePlayers=3; openLead=true; unresolvedHazards=1",
            "local sector story + hazard snapshot",
            "gateway,pressure",
            "all",
            "risk-desc",
            "all",
            "Workflow transition: review-only -> gated-server-impact\nrisk change: elevated - manual test");

        Assert.That(bundle, Does.Contain("# LuaM AI admin audit bundle"));
        Assert.That(bundle, Does.Contain("scope=admin-only local copy-safe markdown"));
        Assert.That(bundle, Does.Contain("preset=gated-server-impact; gate=open; gatewayConfigured=True; useGateway=True; targetAvailable=True"));
        Assert.That(bundle, Does.Contain("## Outcome"));
        Assert.That(bundle, Does.Contain("## Workflow Gates"));
        Assert.That(bundle, Does.Contain("Workflow transition: review-only -> gated-server-impact"));
        Assert.That(bundle, Does.Contain("Gateway exposure: possible - gateway may receive minimized generated-process context"));
        Assert.That(bundle, Does.Contain("Generate process: ready - target available, mode=external-gateway"));
        Assert.That(bundle, Does.Contain("Apply advice: ready - action=gateway-ship, mode=server-confirmation, risk=high"));
        Assert.That(bundle, Does.Contain("Recommendation explanation: ready-server-confirmation"));
        Assert.That(bundle, Does.Contain("why this action: Gate ship for investigation - Spawn review ship after checking gateway evidence"));
        Assert.That(bundle, Does.Contain("confidence: high/88% - clear lead and target"));
        Assert.That(bundle, Does.Contain("Safe command preview: ready-server-confirmation"));
        Assert.That(bundle, Does.Contain("likely impact: high - server-impact action"));
        Assert.That(bundle, Does.Contain("execution gate: local validation -> confirmation preview -> explicit Confirm"));
        Assert.That(bundle, Does.Contain("Operator checklist: server-impact-review"));
        Assert.That(bundle, Does.Contain("human step: apply opens server confirmation preview; confirm only after evidence, risk, and impact review"));
        Assert.That(bundle, Does.Contain("Recommendation provenance: local-bounded"));
        Assert.That(bundle, Does.Contain("evidence: activePlayers=3; openLead=true; unresolvedHazards=1"));
        Assert.That(bundle, Does.Contain("source scope: local sector story + hazard snapshot"));
        Assert.That(bundle, Does.Contain("source classes: gateway,pressure"));
        Assert.That(bundle, Does.Contain("source check: warning-partial - SourceClasses=gateway,pressure; textCues=sector,pressure; verify source evidence before applying"));
        Assert.That(bundle, Does.Contain("## Readiness"));
        Assert.That(bundle, Does.Contain("## Privacy Boundary"));
        Assert.That(bundle, Does.Contain("## Operations Audit"));
        Assert.That(bundle, Does.Contain("## Round Audit"));
        Assert.That(bundle, Does.Contain("## Recommendations"));
        Assert.That(bundle, Does.Contain("filter=all; sort=risk-high-first; total=1; showing=1"));
        Assert.That(bundle, Does.Contain("top visible recommendations:"));
        Assert.That(bundle, Does.Contain("## Action History"));
        Assert.That(bundle, Does.Contain("latest matching decisions:"));
        Assert.That(bundle, Does.Contain("auditBundle=bounded copy-safe local admin export"));
        Assert.That(bundle, Does.Contain("[redacted-url]"));
        Assert.That(bundle, Does.Contain("token=[redacted]"));
        Assert.That(bundle, Does.Contain("GPS [withheld]"));
        Assert.That(bundle, Does.Contain("[redacted-id]"));
        Assert.That(bundle, Does.Not.Contain("https://luam.invalid"));
        Assert.That(bundle, Does.Not.Contain("secret-provider-token"));
        Assert.That(bundle, Does.Not.Contain("secret-bearer-value"));
        Assert.That(bundle, Does.Not.Contain("123, 456"));
        Assert.That(bundle, Does.Not.Contain(targetId));
    }

    [Test]
    public void ActionHistoryOverviewFiltersAndGroupsDecisions()
    {
        const string history = """
            #3 12:03:00 UTC - AI action history
            - decision=confirmed
            - action=quick:status
            - title=AI quick action: status
            - impact=Low
            - result=confirmed; local status returned
            - preview=action=status; local only
            - privacy=bounded local admin history

            ---

            #2 12:02:00 UTC - AI action history
            - decision=canceled
            - action=quick:gateway-ship-selected
            - title=AI quick action: gateway ship Twilight
            - impact=High
            - result=canceled; no server effect executed
            - preview=action=gateway-ship-selected; gatewayShipGameMap=LuaM-Twilight-GateShip
            - privacy=bounded local admin history

            ---

            #1 12:01:00 UTC - AI action history
            - decision=confirmed
            - action=generate-process
            - title=Generate AI process
            - impact=High
            - result=blocked by local validation; action did not run
            - preview=action=generate-process; UseGateway=True; instructionLength=42
            - privacy=bounded local admin history
            """;
        var all = InvokeBuildActionHistoryDisplayBody(history, "all");
        var gateway = InvokeBuildActionHistoryDisplayBody(history, "gateway");
        var blocked = InvokeBuildActionHistoryDisplayBody(history, "blocked");

        Assert.That(all, Does.Contain("Action history overview: total=3, showing=3, filter=all"));
        Assert.That(all, Does.Contain("decisions: confirmed=2, canceled=1, blocked=1"));
        Assert.That(all, Does.Contain("source/scope: gateway=2, server-impact=2, local-safe=2"));
        Assert.That(all, Does.Contain("latest: #3 12:03:00 UTC - AI action history"));

        Assert.That(gateway, Does.Contain("showing=2, filter=gateway"));
        Assert.That(gateway, Does.Contain("quick:gateway-ship-selected"));
        Assert.That(gateway, Does.Contain("generate-process"));
        Assert.That(gateway, Does.Not.Contain("- action=quick:status"));

        Assert.That(blocked, Does.Contain("showing=1, filter=blocked"));
        Assert.That(blocked, Does.Contain("blocked by local validation"));
        Assert.That(blocked, Does.Not.Contain("quick:gateway-ship-selected"));
        Assert.That(blocked, Does.Not.Contain("- action=quick:status"));
    }

    [Test]
    public void RecommendationFiltersSortByRiskConfidenceAndServerScope()
    {
        var recommendations = new[]
        {
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Safe status",
                Detail = "Read local status.",
                SuggestedAction = "Status",
                QuickAction = LuaMAiDirectorEuiMsg.QuickStatus,
                Priority = 3,
                RiskLevel = "low",
                RiskReason = "read-only/local advice",
                ConfidenceBand = "high",
                ConfidencePercent = 88,
                ConfidenceReason = "local signal",
                RequiresServerAction = false,
                RequiresTarget = false,
            },
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Target pressure",
                Detail = "Apply pressure.",
                SuggestedAction = "Target pressure",
                QuickAction = LuaMAiDirectorEuiMsg.QuickPersonalPressure,
                Priority = 1,
                RiskLevel = "medium",
                RiskReason = "targeted local mutation",
                ConfidenceBand = "medium",
                ConfidencePercent = 66,
                ConfidenceReason = "target required",
                RequiresServerAction = true,
                RequiresTarget = true,
            },
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Gate ship",
                Detail = "Spawn a ship.",
                SuggestedAction = "Gate ship",
                QuickAction = LuaMAiDirectorEuiMsg.QuickGatewayShip,
                Priority = 2,
                RiskLevel = "high",
                RiskReason = "ship/grid spawning",
                ConfidenceBand = "low",
                ConfidencePercent = 52,
                ConfidenceReason = "high impact",
                RequiresServerAction = true,
                RequiresTarget = false,
            },
        };

        var overview = InvokeBuildRecommendationDisplayBody(recommendations, "all", "risk-desc");
        var highRisk = InvokeGetVisibleRecommendations(recommendations, "high-risk", "confidence-desc");
        var safe = InvokeGetVisibleRecommendations(recommendations, "safe", "priority");
        var confidenceSorted = InvokeGetVisibleRecommendations(recommendations, "all", "confidence-desc");
        var lowConfidence = InvokeGetVisibleRecommendations(recommendations, "low-confidence", "priority");

        Assert.That(overview, Does.Contain("Recommendation overview: total=3, showing=3, filter=all, sort=risk-high-first"));
        Assert.That(overview, Does.Contain("risk: low=1, medium=1, high=1"));
        Assert.That(overview, Does.Contain("confidence: high=1, low=1; server=2, target=1"));
        Assert.That(overview, Does.Contain("top priority: Target pressure; risk=medium; confidence=66%"));

        Assert.That(highRisk, Has.Length.EqualTo(1));
        Assert.That(highRisk[0].Title, Is.EqualTo("Gate ship"));

        Assert.That(safe, Has.Length.EqualTo(1));
        Assert.That(safe[0].Title, Is.EqualTo("Safe status"));

        Assert.That(confidenceSorted.Select(recommendation => recommendation.Title), Is.EqualTo(new[]
        {
            "Safe status",
            "Target pressure",
            "Gate ship",
        }));

        Assert.That(lowConfidence, Has.Length.EqualTo(1));
        Assert.That(lowConfidence[0].Title, Is.EqualTo("Gate ship"));
    }

    [Test]
    public void RecommendationSourceFiltersGroupEvidenceClasses()
    {
        var recommendations = new[]
        {
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Target pressure",
                Detail = "Apply pressure.",
                SuggestedAction = "Target pressure",
                QuickAction = LuaMAiDirectorEuiMsg.QuickPersonalPressure,
                Priority = 1,
                RiskLevel = "medium",
                RiskReason = "targeted local mutation",
                ConfidenceBand = "medium",
                ConfidencePercent = 66,
                ConfidenceReason = "target required",
                EvidenceSummary = "target required; selectedTarget=true",
                SourceSummary = "local player/session count",
                SourceClasses = [LuaMAiDirectorRecommendationSourceClass.Player],
                RequiresServerAction = true,
                RequiresTarget = true,
            },
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Lead status",
                Detail = "Read sector lead.",
                SuggestedAction = "Status",
                QuickAction = LuaMAiDirectorEuiMsg.QuickStatus,
                Priority = 2,
                RiskLevel = "low",
                RiskReason = "read-only/local advice",
                ConfidenceBand = "high",
                ConfidencePercent = 84,
                ConfidenceReason = "local signal",
                EvidenceSummary = "openLead=true; recentHistory=3",
                SourceSummary = "local sector story history snapshot",
                SourceClasses = [LuaMAiDirectorRecommendationSourceClass.Sector],
                RequiresServerAction = false,
                RequiresTarget = false,
            },
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Clear condition",
                Detail = "Stabilize active condition.",
                SuggestedAction = "Clear condition",
                QuickAction = LuaMAiDirectorEuiMsg.QuickClearCondition,
                Priority = 3,
                RiskLevel = "high",
                RiskReason = "server-impact quick action",
                ConfidenceBand = "medium",
                ConfidencePercent = 71,
                ConfidenceReason = "condition severity",
                EvidenceSummary = "activeCondition=ai-radiation-spike; activeConditions=1",
                SourceSummary = "local active condition snapshot with gateway text that should not override source classes",
                SourceClasses = [LuaMAiDirectorRecommendationSourceClass.Pressure],
                RequiresServerAction = true,
                RequiresTarget = false,
            },
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Gateway state",
                Detail = "Review provider configuration.",
                SuggestedAction = "Recommendations, Status, History",
                QuickAction = LuaMAiDirectorEuiMsg.QuickRecommendations,
                Priority = 4,
                RiskLevel = "low",
                RiskReason = "read-only/local advice",
                ConfidenceBand = "low",
                ConfidencePercent = 52,
                ConfidenceReason = "gateway config",
                EvidenceSummary = "gatewayConfigured=false; activeCondition=misleading-fallback",
                SourceSummary = "local gateway configuration",
                SourceClasses = [LuaMAiDirectorRecommendationSourceClass.Gateway],
                RequiresServerAction = false,
                RequiresTarget = false,
            },
        };

        var overview = InvokeBuildRecommendationDisplayBody(recommendations, "source-pressure", "priority");
        var sourceSummary = InvokeBuildRecommendationSourceCounterSummary(recommendations, "source-pressure");
        var player = InvokeGetVisibleRecommendations(recommendations, "source-player", "priority");
        var sector = InvokeGetVisibleRecommendations(recommendations, "source-sector", "priority");
        var pressure = InvokeGetVisibleRecommendations(recommendations, "source-pressure", "priority");
        var gateway = InvokeGetVisibleRecommendations(recommendations, "source-gateway", "priority");

        Assert.That(overview, Does.Contain("filter=source-condition/hazard"));
        Assert.That(overview, Does.Contain("sources: player=1, sector=1, pressure=1, gateway=1"));
        Assert.That(sourceSummary, Is.EqualTo("source mix: player 1 | sector 1 | pressure 1 | gateway 1; filter=source-condition/hazard"));

        Assert.That(player.Select(recommendation => recommendation.Title), Is.EqualTo(new[] { "Target pressure" }));
        Assert.That(sector.Select(recommendation => recommendation.Title), Is.EqualTo(new[] { "Lead status" }));
        Assert.That(pressure.Select(recommendation => recommendation.Title), Is.EqualTo(new[] { "Clear condition" }));
        Assert.That(gateway.Select(recommendation => recommendation.Title), Is.EqualTo(new[] { "Gateway state" }));
    }

    [Test]
    public void RecommendationEmptyStateExplainsSafeNextStep()
    {
        var recommendations = new[]
        {
            new LuaMAiDirectorRecommendationEntry
            {
                Title = "Gate ship",
                Detail = "Spawn a ship.",
                SuggestedAction = "Gate ship",
                QuickAction = LuaMAiDirectorEuiMsg.QuickGatewayShip,
                Priority = 1,
                RiskLevel = "high",
                RiskReason = "ship/grid spawning",
                ConfidenceBand = "medium",
                ConfidencePercent = 64,
                ConfidenceReason = "needs review",
                RequiresServerAction = true,
                RequiresTarget = false,
            },
        };

        var safeEmpty = InvokeBuildRecommendationDisplayBody(recommendations, "safe", "priority");
        var highConfidenceEmpty = InvokeBuildRecommendationDisplayBody(recommendations, "high-confidence", "confidence-desc");

        Assert.That(safeEmpty, Does.Contain("Recommendation overview: total=1, showing=0, filter=safe/read-only, sort=priority"));
        Assert.That(safeEmpty, Does.Contain("No recommendations match filter=safe/read-only with sort=priority."));
        Assert.That(safeEmpty, Does.Contain("No safe/read-only recommendation is available; keep server-impact actions gated and request a review before acting."));
        Assert.That(safeEmpty, Does.Not.Contain("P1 Gate ship"));

        Assert.That(highConfidenceEmpty, Does.Contain("No recommendations match filter=high-confidence with sort=confidence-high-first."));
        Assert.That(highConfidenceEmpty, Does.Contain("treat visible AI advice as uncertain and prefer local status/review"));
    }

    [Test]
    public void RecommendationCopyTextUsesFilteredSortAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        var state = new LuaMAiDirectorEuiState
        {
            Recommendations =
            [
                new LuaMAiDirectorRecommendationEntry
                {
                    Title = "Spawn gateway ship from https://luam.invalid/ship",
                    Detail = $"Use gateway token=secret-provider-token near GPS: 123, 456 for {targetId}.",
                    SuggestedAction = "Gate ship",
                    QuickAction = LuaMAiDirectorEuiMsg.QuickGatewayShip,
                    Priority = 1,
                    RiskLevel = "high",
                    RiskReason = "ship/grid spawning with bearer=secret-bearer-value",
                    ConfidenceBand = "medium",
                    ConfidencePercent = 68,
                    ConfidenceReason = "gateway evidence matched route",
                    EvidenceSummary = $"activePlayers=3; route=https://luam.invalid/source token=secret-provider-token GPS: 123, 456 {targetId}",
                    SourceSummary = "local sector snapshot + gateway source class",
                    SourceClasses = [LuaMAiDirectorRecommendationSourceClass.Gateway],
                    RequiresServerAction = true,
                    RequiresTarget = false,
                },
                new LuaMAiDirectorRecommendationEntry
                {
                    Title = "Safe status",
                    Detail = "Read local status.",
                    SuggestedAction = "Status",
                    QuickAction = LuaMAiDirectorEuiMsg.QuickStatus,
                    Priority = 2,
                    RiskLevel = "low",
                    RiskReason = "read-only/local advice",
                    ConfidenceBand = "high",
                    ConfidencePercent = 92,
                    ConfidenceReason = "local signal",
                    RequiresServerAction = false,
                    RequiresTarget = false,
                },
            ],
        };

        var summary = InvokeBuildRecommendationCopyText(state, "high-risk", "confidence-desc");

        Assert.That(summary, Does.Contain("LuaM AI recommendation summary"));
        Assert.That(summary, Does.Contain("filter=high-risk"));
        Assert.That(summary, Does.Contain("sort=confidence-high-first"));
        Assert.That(summary, Does.Contain("total=2; showing=1"));
        Assert.That(summary, Does.Contain("Spawn gateway ship"));
        Assert.That(summary, Does.Contain("risk=high"));
        Assert.That(summary, Does.Contain("confidence=medium/68%"));
        Assert.That(summary, Does.Contain("server=True"));
        Assert.That(summary, Does.Contain("evidence=activePlayers=3"));
        Assert.That(summary, Does.Contain("source=local sector snapshot + gateway source class"));
        Assert.That(summary, Does.Contain("sourceClasses=gateway"));
        Assert.That(summary, Does.Contain("privacy=filtered copy-safe local admin summary"));
        Assert.That(summary, Does.Contain("[redacted-url]"));
        Assert.That(summary, Does.Contain("token=[redacted]"));
        Assert.That(summary, Does.Contain("bearer=[redacted]"));
        Assert.That(summary, Does.Contain("GPS [withheld]"));
        Assert.That(summary, Does.Contain("[redacted-id]"));
        Assert.That(summary, Does.Not.Contain("Safe status"));
        Assert.That(summary, Does.Not.Contain("https://luam.invalid"));
        Assert.That(summary, Does.Not.Contain("secret-provider-token"));
        Assert.That(summary, Does.Not.Contain("secret-bearer-value"));
        Assert.That(summary, Does.Not.Contain("123, 456"));
        Assert.That(summary, Does.Not.Contain(targetId));
    }

    [Test]
    public void ActionHistoryCopyTextUsesFilterAndRedactsSensitiveValues()
    {
        const string targetId = "11111111-2222-3333-4444-555555555555";
        const string history = """
            #2 12:02:00 UTC - AI action history
            - decision=confirmed
            - action=quick:gateway-ship-selected
            - title=AI quick action: gateway ship
            - impact=High
            - result=confirmed through gateway https://luam.invalid/ship token=secret-provider-token
            - preview=action=gateway-ship-selected; gatewayShipGameMap=LuaM-Twilight-GateShip; GPS: 123, 456; id=11111111-2222-3333-4444-555555555555
            - privacy=bounded local admin history

            ---

            #1 12:01:00 UTC - AI action history
            - decision=confirmed
            - action=quick:status
            - title=AI quick action: status
            - impact=Low
            - result=confirmed; local status returned
            - preview=action=status; local only
            - privacy=bounded local admin history
            """;
        var state = new LuaMAiDirectorEuiState
        {
            AiActionHistory = history,
            AiActionHistoryCount = 2,
        };

        var summary = InvokeBuildActionHistoryCopyText(state, "gateway");

        Assert.That(summary, Does.Contain("LuaM AI action history summary"));
        Assert.That(summary, Does.Contain("filter=gateway"));
        Assert.That(summary, Does.Contain("saved=2"));
        Assert.That(summary, Does.Contain("Action history overview: total=2, showing=1, filter=gateway"));
        Assert.That(summary, Does.Contain("quick:gateway-ship-selected"));
        Assert.That(summary, Does.Contain("privacy=filtered copy-safe local admin history"));
        Assert.That(summary, Does.Contain("[redacted-url]"));
        Assert.That(summary, Does.Contain("token=[redacted]"));
        Assert.That(summary, Does.Contain("GPS [withheld]"));
        Assert.That(summary, Does.Contain("[redacted-id]"));
        Assert.That(summary, Does.Not.Contain("- action=quick:status"));
        Assert.That(summary, Does.Not.Contain("https://luam.invalid"));
        Assert.That(summary, Does.Not.Contain("secret-provider-token"));
        Assert.That(summary, Does.Not.Contain("123, 456"));
        Assert.That(summary, Does.Not.Contain(targetId));
    }

    private static string InvokeBuildOutcomeCopyText(LuaMAiDirectorEuiState state)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildOutcomeCopyText",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildOutcomeCopyText method");

        return (string) method!.Invoke(null, new object[] { state })!;
    }

    private static string InvokeBuildReadiness(LuaMAiDirectorEuiState state)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildReadiness",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildReadiness method");

        return (string) method!.Invoke(null, new object[] { state })!;
    }

    private static string InvokeBuildOperationsAudit(LuaMAiDirectorEuiState state)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildOperationsAudit",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildOperationsAudit method");

        return (string) method!.Invoke(null, new object[] { state })!;
    }

    private static string InvokeBuildRoundAuditFooter(LuaMAiDirectorEuiState state)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRoundAuditFooter",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRoundAuditFooter method");

        return (string) method!.Invoke(null, new object[] { state })!;
    }

    private static string InvokeBuildWorkflowPresetSummary(string preset, LuaMAiDirectorEuiState state)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildWorkflowPresetSummary",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildWorkflowPresetSummary method");

        return (string) method!.Invoke(null, new object[] { preset, state })!;
    }

    private static string InvokeBuildWorkflowTransitionSummary(
        string previousPreset,
        string selectedPreset,
        LuaMAiDirectorEuiState state,
        bool useGateway)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildWorkflowTransitionSummary",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildWorkflowTransitionSummary method");

        return (string) method!.Invoke(null, new object[] { previousPreset, selectedPreset, state, useGateway })!;
    }

    private static bool InvokeWorkflowForcesLocal(string preset)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "WorkflowForcesLocal",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private WorkflowForcesLocal method");

        return (bool) method!.Invoke(null, new object[] { preset })!;
    }

    private static bool InvokeWorkflowAllowsGenerateProcess(string preset)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "WorkflowAllowsGenerateProcess",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private WorkflowAllowsGenerateProcess method");

        return (bool) method!.Invoke(null, new object[] { preset })!;
    }

    private static bool InvokeWorkflowAllowsQuickAction(string preset, string action)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "WorkflowAllowsQuickAction",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private WorkflowAllowsQuickAction method");

        return (bool) method!.Invoke(null, new object[] { preset, action })!;
    }

    private static bool InvokeWorkflowAllowsRecommendationAction(
        string preset,
        bool requiresServerAction,
        string riskLevel)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "WorkflowAllowsRecommendationAction",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private WorkflowAllowsRecommendationAction method");

        return (bool) method!.Invoke(null, new object[] { preset, requiresServerAction, riskLevel })!;
    }

    private static string InvokeBuildRecommendationApplyState(
        string preset,
        string selectedAction,
        bool requiresTarget,
        bool requiresServerAction,
        string riskLevel,
        int targetCount,
        bool canRunServerActions,
        bool requestInFlight,
        bool hasPendingConfirmation)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationApplyState",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationApplyState method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                selectedAction,
                requiresTarget,
                requiresServerAction,
                riskLevel,
                targetCount,
                canRunServerActions,
                requestInFlight,
                hasPendingConfirmation,
            })!;
    }

    private static string InvokeBuildRecommendationExplanation(
        string preset,
        string selectedAction,
        string suggestedAction,
        string title,
        string detail,
        bool requiresTarget,
        bool requiresServerAction,
        string riskLevel,
        string riskReason,
        string confidenceBand,
        int confidencePercent,
        string confidenceReason,
        int targetCount,
        bool canRunServerActions,
        bool requestInFlight,
        bool hasPendingConfirmation)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationExplanation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationExplanation method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                selectedAction,
                suggestedAction,
                title,
                detail,
                requiresTarget,
                requiresServerAction,
                riskLevel,
                riskReason,
                confidenceBand,
                confidencePercent,
                confidenceReason,
                targetCount,
                canRunServerActions,
                requestInFlight,
                hasPendingConfirmation,
            })!;
    }

    private static string InvokeBuildRecommendationReviewSummary(
        string preset,
        string selectedAction,
        string suggestedAction,
        bool requiresTarget,
        bool requiresServerAction,
        string riskLevel,
        string confidenceBand,
        int confidencePercent,
        string evidenceSummary,
        string sourceSummary,
        string sourceClassesSummary,
        int targetCount,
        bool canRunServerActions,
        bool requestInFlight,
        bool hasPendingConfirmation)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationReviewSummary",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationReviewSummary method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                selectedAction,
                suggestedAction,
                requiresTarget,
                requiresServerAction,
                riskLevel,
                confidenceBand,
                confidencePercent,
                evidenceSummary,
                sourceSummary,
                sourceClassesSummary,
                targetCount,
                canRunServerActions,
                requestInFlight,
                hasPendingConfirmation,
            })!;
    }

    private static string InvokeBuildRecommendationCommandPreview(
        string preset,
        string selectedAction,
        string suggestedAction,
        bool requiresTarget,
        bool requiresServerAction,
        string riskLevel,
        string riskReason,
        int targetCount,
        bool canRunServerActions,
        bool requestInFlight,
        bool hasPendingConfirmation)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationCommandPreview",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationCommandPreview method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                selectedAction,
                suggestedAction,
                requiresTarget,
                requiresServerAction,
                riskLevel,
                riskReason,
                targetCount,
                canRunServerActions,
                requestInFlight,
                hasPendingConfirmation,
            })!;
    }

    private static string InvokeBuildRecommendationOperatorChecklist(
        string preset,
        string selectedAction,
        string suggestedAction,
        bool requiresTarget,
        bool requiresServerAction,
        string riskLevel,
        string confidenceBand,
        int confidencePercent,
        int targetCount,
        bool canRunServerActions,
        bool requestInFlight,
        bool hasPendingConfirmation)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationOperatorChecklist",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationOperatorChecklist method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                selectedAction,
                suggestedAction,
                requiresTarget,
                requiresServerAction,
                riskLevel,
                confidenceBand,
                confidencePercent,
                targetCount,
                canRunServerActions,
                requestInFlight,
                hasPendingConfirmation,
            })!;
    }

    private static string InvokeBuildRecommendationProvenance(
        string selectedAction,
        string evidenceSummary,
        string sourceSummary,
        string sourceClassesSummary,
        string riskReason,
        string confidenceReason)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationProvenance",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationProvenance method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                selectedAction,
                evidenceSummary,
                sourceSummary,
                sourceClassesSummary,
                riskReason,
                confidenceReason,
            })!;
    }

    private static string InvokeBuildGenerateProcessState(
        string preset,
        int targetCount,
        bool canRunServerActions,
        bool gatewayConfigured,
        bool useGateway,
        bool requestInFlight,
        bool hasPendingConfirmation)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildGenerateProcessState",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildGenerateProcessState method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                targetCount,
                canRunServerActions,
                gatewayConfigured,
                useGateway,
                requestInFlight,
                hasPendingConfirmation,
            })!;
    }

    private static string InvokeGetOperatorStateLocKey(LuaMAiDirectorEuiState state)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "GetOperatorStateLocKey",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private GetOperatorStateLocKey method");

        return (string) method!.Invoke(null, new object[] { state })!;
    }

    private static string InvokeBuildSafeModeSummary(
        string preset,
        LuaMAiDirectorEuiState state,
        bool useGateway)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildSafeModeSummary",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildSafeModeSummary method");

        return (string) method!.Invoke(null, new object[] { preset, state, useGateway })!;
    }

    private static string InvokeBuildGatewayExposureSummary(
        string preset,
        LuaMAiDirectorEuiState state,
        bool useGateway)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildGatewayExposureSummary",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildGatewayExposureSummary method");

        return (string) method!.Invoke(null, new object[] { preset, state, useGateway })!;
    }

    private static string InvokeBuildWorkflowCopyText(
        string preset,
        LuaMAiDirectorEuiState state,
        bool useGateway,
        int targetCount,
        string selectedRecommendationAction,
        bool selectedRecommendationRequiresTarget,
        bool selectedRecommendationRequiresServerAction,
        string selectedRecommendationRiskLevel,
        string selectedRecommendationSuggestedAction,
        string selectedRecommendationTitle,
        string selectedRecommendationDetail,
        string selectedRecommendationRiskReason,
        string selectedRecommendationConfidenceBand,
        int selectedRecommendationConfidencePercent,
        string selectedRecommendationConfidenceReason,
        string selectedRecommendationEvidenceSummary,
        string selectedRecommendationSourceSummary,
        string selectedRecommendationSourceClassesSummary)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildWorkflowCopyText",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildWorkflowCopyText method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                state,
                useGateway,
                targetCount,
                selectedRecommendationAction,
                selectedRecommendationRequiresTarget,
                selectedRecommendationRequiresServerAction,
                selectedRecommendationRiskLevel,
                selectedRecommendationSuggestedAction,
                selectedRecommendationTitle,
                selectedRecommendationDetail,
                selectedRecommendationRiskReason,
                selectedRecommendationConfidenceBand,
                selectedRecommendationConfidencePercent,
                selectedRecommendationConfidenceReason,
                selectedRecommendationEvidenceSummary,
                selectedRecommendationSourceSummary,
                selectedRecommendationSourceClassesSummary,
            })!;
    }

    private static string InvokeBuildAuditBundleCopyText(
        string preset,
        LuaMAiDirectorEuiState state,
        bool useGateway,
        int targetCount,
        string selectedRecommendationAction,
        bool selectedRecommendationRequiresTarget,
        bool selectedRecommendationRequiresServerAction,
        string selectedRecommendationRiskLevel,
        string selectedRecommendationSuggestedAction,
        string selectedRecommendationTitle,
        string selectedRecommendationDetail,
        string selectedRecommendationRiskReason,
        string selectedRecommendationConfidenceBand,
        int selectedRecommendationConfidencePercent,
        string selectedRecommendationConfidenceReason,
        string selectedRecommendationEvidenceSummary,
        string selectedRecommendationSourceSummary,
        string selectedRecommendationSourceClassesSummary,
        string recommendationFilter,
        string recommendationSort,
        string actionHistoryFilter,
        string workflowTransitionSummary)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildAuditBundleCopyText",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildAuditBundleCopyText method");

        return (string) method!.Invoke(
            null,
            new object[]
            {
                preset,
                state,
                useGateway,
                targetCount,
                selectedRecommendationAction,
                selectedRecommendationRequiresTarget,
                selectedRecommendationRequiresServerAction,
                selectedRecommendationRiskLevel,
                selectedRecommendationSuggestedAction,
                selectedRecommendationTitle,
                selectedRecommendationDetail,
                selectedRecommendationRiskReason,
                selectedRecommendationConfidenceBand,
                selectedRecommendationConfidencePercent,
                selectedRecommendationConfidenceReason,
                selectedRecommendationEvidenceSummary,
                selectedRecommendationSourceSummary,
                selectedRecommendationSourceClassesSummary,
                recommendationFilter,
                recommendationSort,
                actionHistoryFilter,
                workflowTransitionSummary,
            })!;
    }

    private static string InvokeBuildActionHistoryDisplayBody(string history, string filter)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildActionHistoryDisplayBody",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildActionHistoryDisplayBody method");

        return (string) method!.Invoke(null, new object[] { history, filter })!;
    }

    private static string InvokeBuildRecommendationDisplayBody(
        LuaMAiDirectorRecommendationEntry[] recommendations,
        string filter,
        string sort)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationDisplayBody",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationDisplayBody method");

        return (string) method!.Invoke(null, new object[] { recommendations, filter, sort })!;
    }

    private static string InvokeBuildRecommendationSourceCounterSummary(
        LuaMAiDirectorRecommendationEntry[] recommendations,
        string filter)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationSourceCounterSummary",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationSourceCounterSummary method");

        return (string) method!.Invoke(null, new object[] { recommendations, filter })!;
    }

    private static LuaMAiDirectorRecommendationEntry[] InvokeGetVisibleRecommendations(
        LuaMAiDirectorRecommendationEntry[] recommendations,
        string filter,
        string sort)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "GetVisibleRecommendations",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private GetVisibleRecommendations method");

        return (LuaMAiDirectorRecommendationEntry[]) method!.Invoke(
            null,
            new object[] { recommendations, filter, sort })!;
    }

    private static string InvokeBuildRecommendationCopyText(
        LuaMAiDirectorEuiState state,
        string filter,
        string sort)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildRecommendationCopyText",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildRecommendationCopyText method");

        return (string) method!.Invoke(null, new object[] { state, filter, sort })!;
    }

    private static string InvokeBuildActionHistoryCopyText(
        LuaMAiDirectorEuiState state,
        string filter)
    {
        var method = typeof(LuaMAiDirectorWindow).GetMethod(
            "BuildActionHistoryCopyText",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private BuildActionHistoryCopyText method");

        return (string) method!.Invoke(null, new object[] { state, filter })!;
    }
}
