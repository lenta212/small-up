using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared._LuaM.AntiCheat;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.AntiCheat;

/// <summary>
/// Correlates server-side validation failures into an observe-only suspicion score.
/// It deliberately performs no automatic punishment.
/// </summary>
public sealed class LuaMAntiCheatSystem : EntitySystem
{
    private const int MaxEvidenceEntries = 24;

    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IConfigurationManager _configuration = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    private readonly Dictionary<ICommonSession, PlayerEvidence> _evidence = new();

    private bool _enabled;
    private float _alertThreshold;
    private float _decayPerMinute;
    private TimeSpan _alertCooldown;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMAntiCheatSignalEvent>(OnSignal);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        _players.PlayerStatusChanged += OnPlayerStatusChanged;

        Subs.CVar(_configuration, CCVars.AntiCheatEnabled, value => _enabled = value, true);
        Subs.CVar(
            _configuration,
            CCVars.AntiCheatAlertThreshold,
            value => _alertThreshold = Math.Max(1f, value),
            true);
        Subs.CVar(
            _configuration,
            CCVars.AntiCheatScoreDecayPerMinute,
            value => _decayPerMinute = Math.Max(0f, value),
            true);
        Subs.CVar(
            _configuration,
            CCVars.AntiCheatAlertCooldown,
            value => _alertCooldown = TimeSpan.FromSeconds(Math.Max(0, value)),
            true);
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _evidence.Clear();
        base.Shutdown();
    }

    private void OnSignal(LuaMAntiCheatSignalEvent ev)
    {
        if (!_enabled || ev.Session.Status == SessionStatus.Disconnected)
            return;

        var now = _timing.RealTime;
        if (!_evidence.TryGetValue(ev.Session, out var state))
        {
            state = new PlayerEvidence
            {
                LastUpdated = now,
                LastSignalTick = GameTick.MaxValue,
            };
            _evidence.Add(ev.Session, state);
        }

        state.Score = DecayScore(state.Score, now - state.LastUpdated, _decayPerMinute);
        state.LastUpdated = now;

        if (state.LastSignalTick == _timing.CurTick &&
            state.LastSignalKind == ev.Kind &&
            state.LastSignalSubject == ev.Subject)
        {
            return;
        }

        state.LastSignalTick = _timing.CurTick;
        state.LastSignalKind = ev.Kind;
        state.LastSignalSubject = ev.Subject;
        state.Score += GetWeight(ev.Kind);
        state.TotalSignals++;
        state.Recent.Enqueue(ev.Kind);

        while (state.Recent.Count > MaxEvidenceEntries)
            state.Recent.Dequeue();

        if (state.Score < _alertThreshold || now < state.NextAlert)
            return;

        var summary = string.Join(", ", state.Recent
            .GroupBy(kind => kind)
            .Select(group => $"{group.Key}={group.Count()}"));
        var subject = ev.Subject is { } uid && Exists(uid)
            ? ToPrettyString(uid)
            : "none";
        var alertMessage =
            $"[AntiCheat/observe] {ev.Session.Name} score={state.Score:0.0}, recent=[{summary}], " +
            $"lastSubject={subject}, totalSignals={state.TotalSignals}. Review before taking action.";

        _adminLog.Add(
            LogType.RateLimited,
            LogImpact.Medium,
            $"[AntiCheat/observe] {ev.Session} score={state.Score:0.0}, recent=[{summary}], " +
            $"lastSubject={subject}, totalSignals={state.TotalSignals}. Review before taking action.");
        _chat.SendAdminAlert(alertMessage);
        state.NextAlert = now + _alertCooldown;
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _evidence.Clear();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus == SessionStatus.Disconnected)
            _evidence.Remove(args.Session);
    }

    internal static float DecayScore(float score, TimeSpan elapsed, float decayPerMinute)
    {
        if (score <= 0f || elapsed <= TimeSpan.Zero || decayPerMinute <= 0f)
            return Math.Max(0f, score);

        return Math.Max(0f, score - (float) elapsed.TotalMinutes * decayPerMinute);
    }

    internal static float GetWeight(LuaMAntiCheatSignalKind kind)
    {
        return kind switch
        {
            LuaMAntiCheatSignalKind.RemoteBoundUi => 5f,
            LuaMAntiCheatSignalKind.BoundUiRateLimit => 3f,
            LuaMAntiCheatSignalKind.InvalidShootCoordinates => 5f,
            LuaMAntiCheatSignalKind.InvalidShootTarget => 4f,
            LuaMAntiCheatSignalKind.PredictedHitFlood => 4f,
            _ => 1f,
        };
    }

    private sealed class PlayerEvidence
    {
        public float Score;
        public int TotalSignals;
        public TimeSpan LastUpdated;
        public TimeSpan NextAlert;
        public GameTick LastSignalTick;
        public LuaMAntiCheatSignalKind LastSignalKind;
        public EntityUid? LastSignalSubject;
        public readonly Queue<LuaMAntiCheatSignalKind> Recent = new();
    }
}
