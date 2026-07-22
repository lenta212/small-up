using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Enables collection and aggregation of server-side anti-cheat evidence.
    /// This system is observe-only and never bans or kicks players automatically.
    /// </summary>
    public static readonly CVarDef<bool> AntiCheatEnabled =
        CVarDef.Create("anticheat.enabled", true, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Suspicion score required before an aggregated admin alert is sent.
    /// </summary>
    public static readonly CVarDef<float> AntiCheatAlertThreshold =
        CVarDef.Create("anticheat.alert_threshold", 10f, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Suspicion points removed per minute without new evidence.
    /// </summary>
    public static readonly CVarDef<float> AntiCheatScoreDecayPerMinute =
        CVarDef.Create("anticheat.score_decay_per_minute", 2f, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Minimum delay between anti-cheat admin alerts for the same player, in seconds.
    /// </summary>
    public static readonly CVarDef<int> AntiCheatAlertCooldown =
        CVarDef.Create("anticheat.alert_cooldown", 30, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Maximum number of validated bound-UI messages accepted per player during one rate-limit period.
    /// </summary>
    public static readonly CVarDef<int> AntiCheatBoundUiRateLimitCount =
        CVarDef.Create("anticheat.bound_ui_limit_count", 240, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Bound-UI message rate-limit period, in seconds.
    /// </summary>
    public static readonly CVarDef<float> AntiCheatBoundUiRateLimitPeriod =
        CVarDef.Create("anticheat.bound_ui_limit_period", 1f, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Maximum collision candidates accepted in one client-predicted projectile hit report.
    /// </summary>
    public static readonly CVarDef<int> AntiCheatPredictedHitCandidateLimit =
        CVarDef.Create("anticheat.predicted_hit_candidate_limit", 16, CVar.ARCHIVE | CVar.SERVERONLY);

    /// <summary>
    /// Maximum client-predicted projectile hit reports accepted from one player during a server tick.
    /// </summary>
    public static readonly CVarDef<int> AntiCheatPredictedHitEventLimit =
        CVarDef.Create("anticheat.predicted_hit_event_limit", 64, CVar.ARCHIVE | CVar.SERVERONLY);
}
