using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Enables the LuaM AI director that can request player-centered procedural events from an external gateway.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorEnabled =
        CVarDef.Create("luam.ai_director.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Enables the physical LuaM AI base, including logistics ships, drones, work zones, traces, and supply drops.
    /// Runtime hard limits remain in force while this is enabled.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiPhysicalBaseEnabled =
        CVarDef.Create("luam.ai_physical_base.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// HTTP endpoint that accepts a LuaM sector context JSON payload and returns one validated event proposal.
    /// Leave empty to use only local fallback generation.
    /// </summary>
    public static readonly CVarDef<string> LuaMAiDirectorGatewayUrl =
        CVarDef.Create("luam.ai_director.gateway_url", string.Empty, CVar.SERVERONLY);

    /// <summary>
    /// Optional bearer token for the external LuaM AI gateway.
    /// </summary>
    public static readonly CVarDef<string> LuaMAiDirectorGatewayToken =
        CVarDef.Create("luam.ai_director.gateway_token", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>
    /// Allows the director to fall back to the deterministic LuaM generator if the AI gateway is unavailable.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorFallbackEnabled =
        CVarDef.Create("luam.ai_director.fallback_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Allows LuaM AI director chat commands to execute guarded SS14 server console commands as the local server console.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorAdminMode =
        CVarDef.Create("luam.ai_director.admin_mode", false, CVar.SERVERONLY);

    /// <summary>
    /// Lets the LuaM AI director execute gameplay-affecting LuaM actions without EUI confirmations.
    /// This does not grant operating-system, secret, server lifecycle, database, or arbitrary console access.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorGameMasterMode =
        CVarDef.Create("luam.ai_director.game_master_mode", false, CVar.SERVERONLY);

    /// <summary>
    /// Enables a local UserData inbox bridge for development-time AI operator commands.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorLocalBridgeEnabled =
        CVarDef.Create("luam.ai_director.local_bridge_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Allows the local UserData AI bridge to run round-affecting commands. Keep disabled outside isolated operator work.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorLocalBridgeUnsafeActionsEnabled =
        CVarDef.Create("luam.ai_director.local_bridge_unsafe_actions_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Seeds every default LuaM sector condition at round start, enabling all sector hazards.
    /// </summary>
    public static readonly CVarDef<bool> LuaMSectorAllHazardsEnabled =
        CVarDef.Create("luam.sector.all_hazards_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Enables automatic, manual, and AI-proposed LuaM dynamic sector events.
    /// Existing sites remain available for completion when this is disabled.
    /// </summary>
    public static readonly CVarDef<bool> LuaMDynamicEventsEnabled =
        CVarDef.Create("luam.dynamic_events.enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Maximum number of unresolved LuaM runtime event sites. Values less than one block new sites.
    /// </summary>
    public static readonly CVarDef<int> LuaMDynamicEventsMaxActiveSites =
        CVarDef.Create("luam.dynamic_events.max_active_sites", 3, CVar.SERVERONLY);

    /// <summary>
    /// Maximum number of animal-husbandry population units allowed on one map.
    /// Values less than one prevent new conceptions and births without removing existing entities.
    /// </summary>
    public static readonly CVarDef<int> LuaMAnimalHusbandryMaxPopulationPerMap =
        CVarDef.Create("luam.animal_husbandry.max_population_per_map", 32, CVar.SERVERONLY);

    /// <summary>
    /// Enables lightweight moving transit signatures on sector radar.
    /// These contacts have no shuttle grid, colliding fixture, crew, or NPC logic.
    /// </summary>
    public static readonly CVarDef<bool> LuaMSectorTrafficEnabled =
        CVarDef.Create("luam.sector_traffic.enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Desired radar-only transit contacts on the active primary sector map.
    /// Runtime code always clamps this to a global hard maximum of four.
    /// </summary>
    public static readonly CVarDef<int> LuaMSectorTrafficContacts =
        CVarDef.Create("luam.sector_traffic.contacts", 2, CVar.SERVERONLY);

    /// <summary>
    /// Sends a local sector AI greeting to players when they enter the round.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorGreetOnJoin =
        CVarDef.Create("luam.ai_director.greet_on_join", true, CVar.SERVERONLY);

    /// <summary>
    /// Enables the local AI world pulse that continuously applies sector pressure without waiting for the external gateway.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorWorldPulseEnabled =
        CVarDef.Create("luam.ai_director.world_pulse_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Keeps the AI director in maximum-danger pressure mode while enabled.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorMaxDanger =
        CVarDef.Create("luam.ai_director.max_danger", false, CVar.SERVERONLY);

    /// <summary>
    /// Seconds after the first active player before the AI director may create the first player-centered event.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorInitialDelay =
        CVarDef.Create("luam.ai_director.initial_delay", 180, CVar.SERVERONLY);

    /// <summary>
    /// Seconds between automatic AI director event attempts.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorInterval =
        CVarDef.Create("luam.ai_director.interval", 3600, CVar.SERVERONLY);

    /// <summary>
    /// Seconds between local AI world pressure pulses.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorWorldPulseInterval =
        CVarDef.Create("luam.ai_director.world_pulse_interval", 300, CVar.SERVERONLY);

    /// <summary>
    /// Timeout in seconds for one external AI gateway request.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorRequestTimeout =
        CVarDef.Create("luam.ai_director.request_timeout", 12, CVar.SERVERONLY);

    /// <summary>
    /// Rolling window in seconds for external AI gateway request budgeting.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorGatewayBudgetWindow =
        CVarDef.Create("luam.ai_director.gateway_budget_window", 300, CVar.SERVERONLY);

    /// <summary>
    /// Maximum external AI gateway requests allowed in the rolling budget window. Set to 0 to block provider calls.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorGatewayBudgetWindowRequests =
        CVarDef.Create("luam.ai_director.gateway_budget_window_requests", 8, CVar.SERVERONLY);

    /// <summary>
    /// Maximum external AI gateway requests allowed per in-round budget. Set to 0 to block provider calls.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorGatewayBudgetRoundRequests =
        CVarDef.Create("luam.ai_director.gateway_budget_round_requests", 60, CVar.SERVERONLY);

    /// <summary>
    /// Plays local TTS audio for LuaM AI director chat and radio messages through the configured gateway /tts route.
    /// </summary>
    public static readonly CVarDef<bool> LuaMAiDirectorTtsEnabled =
        CVarDef.Create("luam.ai_director.tts_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Plays TTS audio for in-character character speech through the configured gateway /tts route.
    /// </summary>
    public static readonly CVarDef<bool> LuaMCharacterTtsEnabled =
        CVarDef.Create("luam.ai_director.tts_characters_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Maximum characters sent to the TTS gateway for one in-character speech line.
    /// </summary>
    public static readonly CVarDef<int> LuaMCharacterTtsMaxChars =
        CVarDef.Create("luam.ai_director.tts_character_max_chars", 180, CVar.SERVERONLY);

    /// <summary>
    /// Per-speaker cooldown in seconds for in-character TTS lines.
    /// </summary>
    public static readonly CVarDef<float> LuaMCharacterTtsCooldown =
        CVarDef.Create("luam.ai_director.tts_character_cooldown", 1.25f, CVar.SERVERONLY);

    /// <summary>
    /// Comma-separated Piper voice names used for deterministic in-character voice selection.
    /// </summary>
    public static readonly CVarDef<string> LuaMCharacterTtsVoices =
        CVarDef.Create("luam.ai_director.tts_character_voices", "ru_RU-irina-medium,ru_RU-denis-medium,ru_RU-dmitri-medium,ru_RU-ruslan-medium", CVar.SERVERONLY);

    /// <summary>
    /// Client-side volume in decibels for LuaM TTS playback.
    /// </summary>
    public static readonly CVarDef<float> LuaMAiDirectorTtsVolume =
        CVarDef.Create("luam.ai_director.tts_volume", -5f, CVar.SERVERONLY);

    /// <summary>
    /// Maximum WAV/OGG bytes accepted from the local LuaM AI gateway for one TTS line.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorTtsMaxBytes =
        CVarDef.Create("luam.ai_director.tts_max_bytes", 524288, CVar.SERVERONLY);

    /// <summary>
    /// Maximum queued LuaM TTS lines waiting for the gateway.
    /// </summary>
    public static readonly CVarDef<int> LuaMAiDirectorTtsMaxQueue =
        CVarDef.Create("luam.ai_director.tts_max_queue", 8, CVar.SERVERONLY);
}
