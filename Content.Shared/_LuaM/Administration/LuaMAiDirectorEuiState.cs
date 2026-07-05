using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Administration;

[Serializable, NetSerializable]
public sealed class LuaMAiDirectorEuiState : EuiStateBase
{
    public bool Enabled { get; init; }
    public bool FallbackEnabled { get; init; }
    public bool AdminModeEnabled { get; init; }
    public bool GameMasterModeEnabled { get; init; }
    public bool GatewayConfigured { get; init; }
    public bool RequestInFlight { get; init; }
    public bool CanRunServerActions { get; init; }
    public bool HasPendingConfirmation { get; init; }
    public bool HasOpenRuntimeLead { get; init; }
    public bool WorldPulseEnabled { get; init; }
    public bool MaxDangerEnabled { get; init; }
    public int ActivePlayers { get; init; }
    public int InitialDelaySeconds { get; init; }
    public int IntervalSeconds { get; init; }
    public int WorldPulseIntervalSeconds { get; init; }
    public int TimeoutSeconds { get; init; }
    public int NextAttemptSeconds { get; init; }
    public int NextWorldPulseSeconds { get; init; }
    public int PressureSeverity { get; init; }
    public int SyntheticDevicesTotal { get; init; }
    public int SyntheticDevicesReady { get; init; }
    public int SyntheticDevicesOccupied { get; init; }
    public int SyntheticDevicesLinked { get; init; }
    public bool AiBaseCreated { get; init; }
    public int AiBaseSupplyScore { get; init; }
    public int AiBaseTradeCycles { get; init; }
    public string AiBaseSummary { get; init; } = string.Empty;
    public string AiBaseDiagnostics { get; init; } = string.Empty;
    public string AiBaseAutofixSummary { get; init; } = string.Empty;
    public string AiBaseDevelopmentPlan { get; init; } = string.Empty;
    public string RunLevel { get; init; } = string.Empty;
    public string AiOutcomeStatus { get; init; } = string.Empty;
    public string AiOutcomeGroup { get; init; } = string.Empty;
    public string AiOutcomeSummary { get; init; } = string.Empty;
    public string AiNextStepHint { get; init; } = string.Empty;
    public string LastResult { get; init; } = string.Empty;
    public string LastReview { get; init; } = string.Empty;
    public string ReviewHistory { get; init; } = string.Empty;
    public int ReviewHistoryCount { get; init; }
    public string AiActionHistory { get; init; } = string.Empty;
    public int AiActionHistoryCount { get; init; }
    public string ChatTranscript { get; init; } = string.Empty;
    public string PendingConfirmationId { get; init; } = string.Empty;
    public string PendingConfirmationTitle { get; init; } = string.Empty;
    public string PendingConfirmationDetail { get; init; } = string.Empty;
    public string[] GatewaySharedContext { get; init; } = [];
    public string[] GatewayWithheldContext { get; init; } = [];
    public string[] GatewayPrivacyNotes { get; init; } = [];
    public string[] GatewayLastRequestShape { get; init; } = [];
    public string[] GatewayRagSourceShape { get; init; } = [];
    public string[] GatewayBlockReasonSummary { get; init; } = [];
    public int GatewayBudgetWindowSeconds { get; init; }
    public int GatewayBudgetWindowUsed { get; init; }
    public int GatewayBudgetWindowLimit { get; init; }
    public int GatewayBudgetWindowRemaining { get; init; }
    public int GatewayBudgetRoundUsed { get; init; }
    public int GatewayBudgetRoundLimit { get; init; }
    public int GatewayBudgetRoundRemaining { get; init; }
    public int GatewayBudgetRetrySeconds { get; init; }
    public int GatewayAuditRedactions { get; init; }
    public int GatewayAuditIdRedactions { get; init; }
    public int GatewayAuditSecretRedactions { get; init; }
    public int GatewayAuditLocationRedactions { get; init; }
    public int GatewayAuditTruncatedFields { get; init; }
    public int GatewayAuditUnsafeInputBlocks { get; init; }
    public int GatewayAuditBudgetBlocks { get; init; }
    public int GatewayAuditProviderOutputBlocks { get; init; }
    public int GatewayAuditTransportFailures { get; init; }
    public int GatewayBlockUnsafeInputs { get; init; }
    public int GatewayBlockBudgets { get; init; }
    public int GatewayBlockInvalidSchemas { get; init; }
    public int GatewayBlockForbiddenActions { get; init; }
    public int GatewayBlockLocalValidations { get; init; }
    public int GatewayRagAllowedSources { get; init; }
    public int GatewayRagDeniedSources { get; init; }
    public string[] TemplateIds { get; init; } = [];
    public LuaMAiDirectorGatewayShipEntry[] GatewayShipPresets { get; init; } = [];
    public LuaMAiDirectorPlayerEntry[] Players { get; init; } = [];
    public LuaMAiDirectorConditionEntry[] ActiveConditions { get; init; } = [];
    public LuaMAiDirectorRecommendationEntry[] Recommendations { get; init; } = [];
}

[Serializable, NetSerializable]
public sealed class LuaMAiDirectorPlayerEntry
{
    public string UserId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool CanTarget { get; init; }
}

[Serializable, NetSerializable]
public sealed class LuaMAiDirectorConditionEntry
{
    public string ConditionId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Severity { get; init; }
    public string Summary { get; init; } = string.Empty;
}

[Serializable, NetSerializable]
public sealed class LuaMAiDirectorGatewayShipEntry
{
    public string GameMapId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

[Serializable, NetSerializable]
public sealed class LuaMAiDirectorRecommendationEntry
{
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;
    public string QuickAction { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string RiskLevel { get; init; } = string.Empty;
    public string RiskReason { get; init; } = string.Empty;
    public string ConfidenceBand { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public string ConfidenceReason { get; init; } = string.Empty;
    public string EvidenceSummary { get; init; } = string.Empty;
    public string SourceSummary { get; init; } = string.Empty;
    public string[] SourceClasses { get; init; } = [];
    public bool RequiresServerAction { get; init; }
    public bool RequiresTarget { get; init; }
}

public static class LuaMAiDirectorRecommendationSourceClass
{
    public const string Player = "player";
    public const string Sector = "sector";
    public const string Pressure = "pressure";
    public const string Gateway = "gateway";
    public const string AiBase = "ai-base";
}

public static class LuaMAiDirectorEuiMsg
{
    public const string AutoTemplateId = "__auto";
    public const string QuickRecommendations = "recommendations";
    public const string QuickStatus = "status";
    public const string QuickEvent = "event";
    public const string QuickPersonalPressure = "personal-pressure";
    public const string QuickPersonalDanger = "personal-danger";
    public const string QuickAiPressure = "ai-pressure";
    public const string QuickSyntheticControl = "synthetic-control";
    public const string QuickSubspaceRift = "subspace-rift";
    public const string QuickSubspaceRoute = "subspace-route";
    public const string QuickRadiation = "radiation";
    public const string QuickSensorDrift = "sensor-drift";
    public const string QuickComms = "comms";
    public const string QuickMonolith = "monolith";
    public const string QuickClearCondition = "clear-condition";
    public const string QuickResolveLead = "resolve-lead";
    public const string QuickCleanupMarkers = "cleanup-markers";
    public const string QuickSpawnBeacon = "spawn-beacon";
    public const string QuickSpawnScanner = "spawn-scanner";
    public const string QuickMonolithKit = "monolith-kit";
    public const string QuickPaperPack = "paper-pack";
    public const string QuickHistory = "history";
    public const string QuickAiChat = "ai-chat";
    public const string QuickAnnouncement = "announcement";
    public const string QuickGatewayShip = "gateway-ship";
    public const string QuickGatewayShipSelected = "gateway-ship-selected";
    public const string QuickGatewayShipTriage = "gateway-ship-triage";
    public const string QuickGatewayShipHammerhead = "gateway-ship-hammerhead";
    public const string QuickGatewayShipTzipora = "gateway-ship-tzipora";
    public const string QuickGatewayShipTokarev = "gateway-ship-tokarev";
    public const string QuickAiBaseDiagnostics = "ai-base-diagnostics";
    public const string QuickAiBaseAutofix = "ai-base-autofix";
    public const string QuickAiBaseAutopilot = "ai-base-autopilot";
    public const string QuickAiBasePlan = "ai-base-plan";
    public const string QuickAiBaseMine = "ai-base-mine";
    public const string QuickAiBaseBuild = "ai-base-build";
    public const string QuickAiBaseDevelop = "ai-base-develop";

    [Serializable, NetSerializable]
    public sealed class Refresh : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class SetEnabled : EuiMessageBase
    {
        public bool Enabled { get; init; }
    }

    [Serializable, NetSerializable]
    public sealed class Generate : EuiMessageBase
    {
        public string TargetUserId { get; init; } = string.Empty;
        public string TemplateId { get; init; } = AutoTemplateId;
        public string Instruction { get; init; } = string.Empty;
        public bool UseGateway { get; init; } = true;
        public bool IgnoreOpenLead { get; init; }
    }

    [Serializable, NetSerializable]
    public sealed class Review : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class LogReview : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class Chat : EuiMessageBase
    {
        public string Message { get; init; } = string.Empty;
        public string TargetUserId { get; init; } = string.Empty;
        public string TemplateId { get; init; } = AutoTemplateId;
    }

    [Serializable, NetSerializable]
    public sealed class QuickAction : EuiMessageBase
    {
        public string Action { get; init; } = string.Empty;
        public string TargetUserId { get; init; } = string.Empty;
        public string TemplateId { get; init; } = AutoTemplateId;
        public string GatewayShipGameMapId { get; init; } = string.Empty;
    }

    [Serializable, NetSerializable]
    public sealed class ConfirmPendingAction : EuiMessageBase
    {
        public string ConfirmationId { get; init; } = string.Empty;
    }

    [Serializable, NetSerializable]
    public sealed class CancelPendingAction : EuiMessageBase
    {
        public string ConfirmationId { get; init; } = string.Empty;
    }
}
