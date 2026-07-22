using System;
using System.Collections.Generic;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Prometheus;
using Robust.Shared.Configuration;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Sector;

public enum LuaMAiPhysicalEntityKind
{
    Anchor,
    Zone,
    Ship,
    Drone,
    Trace,
    Drop,
}

public readonly record struct LuaMAiPhysicalLimit(int PerMap, int LivePerRound, int SpawnsPerRound);

public readonly record struct LuaMAiPhysicalSliceDiagnostics(int LastProcessed, long Exhaustions);

/// <summary>
/// Owns the runtime feature gate and hard admission limits for every physical AI-base entity.
/// Producer checks avoid expensive work, while startup checks fail closed for prototypes and admin bypasses.
/// </summary>
public sealed partial class LuaMAiPhysicalBaseBudgetSystem : EntitySystem
{
    public const int EcologyAnchorsPerSlice = 1;
    public const int EcologyZonesPerSlice = 5;
    public const int EcologyDronesPerSlice = 6;
    public const int LogisticsShipsPerSlice = 1;
    public const int MiningActionsPerSlice = 8;

    private static readonly Gauge ActiveEntities = Metrics.CreateGauge(
        "luam_ai_physical_entities",
        "Live admitted LuaM physical AI entities by kind.",
        new GaugeConfiguration { LabelNames = new[] { "kind" } });

    private static readonly Counter SpawnRejections = Metrics.CreateCounter(
        "luam_ai_physical_spawn_rejections_total",
        "Rejected LuaM physical AI entity admissions by kind and bounded reason.",
        new CounterConfiguration { LabelNames = new[] { "kind", "reason" } });

    private static readonly Counter BudgetExhaustions = Metrics.CreateCounter(
        "luam_ai_physical_budget_exhaustions_total",
        "LuaM physical AI update slices that yielded after an item or time budget.",
        new CounterConfiguration { LabelNames = new[] { "system", "budget" } });

    [Dependency] private IConfigurationManager _configuration = default!;

    private readonly Dictionary<EntityUid, Admission> _admitted = new();
    private readonly Dictionary<(LuaMAiPhysicalEntityKind Kind, MapId Map), int> _liveByMap = new();
    private readonly int[] _liveByKind = new int[Enum.GetValues<LuaMAiPhysicalEntityKind>().Length];
    private readonly int[] _roundSpawns = new int[Enum.GetValues<LuaMAiPhysicalEntityKind>().Length];
    private readonly Dictionary<(LuaMAiPhysicalEntityKind Kind, string Reason), long> _rejections = new();
    private readonly Dictionary<string, LuaMAiPhysicalSliceDiagnostics> _sliceDiagnostics = new(StringComparer.Ordinal);

    public bool Enabled { get; private set; }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);

        SubscribeLocalEvent<LuaMAiBaseAnchorComponent, MoveEvent>(OnAnchorMoved);
        SubscribeLocalEvent<LuaMAiBaseZoneComponent, ComponentShutdown>(OnZoneShutdown);
        SubscribeLocalEvent<LuaMAiBaseZoneComponent, MoveEvent>(OnZoneMoved);
        SubscribeLocalEvent<LuaMAiLogisticsShipComponent, ComponentStartup>(OnShipStartup);
        SubscribeLocalEvent<LuaMAiLogisticsShipComponent, MoveEvent>(OnShipMoved);
        SubscribeLocalEvent<LuaMAiMiningDroneComponent, ComponentShutdown>(OnDroneShutdown);
        SubscribeLocalEvent<LuaMAiMiningDroneComponent, MoveEvent>(OnDroneMoved);
        SubscribeLocalEvent<LuaMAiDroneTraceComponent, ComponentShutdown>(OnTraceShutdown);
        SubscribeLocalEvent<LuaMAiDroneTraceComponent, MoveEvent>(OnTraceMoved);
        SubscribeLocalEvent<LuaMAiSupplyDropComponent, ComponentShutdown>(OnDropShutdown);
        SubscribeLocalEvent<LuaMAiSupplyDropComponent, MoveEvent>(OnDropMoved);

        Subs.CVar(_configuration, CCVars.LuaMAiPhysicalBaseEnabled, OnEnabledChanged, true);
        RefreshEntityMetrics();
    }

    public static LuaMAiPhysicalLimit GetLimit(LuaMAiPhysicalEntityKind kind)
    {
        return kind switch
        {
            LuaMAiPhysicalEntityKind.Anchor => new LuaMAiPhysicalLimit(1, 2, 2),
            LuaMAiPhysicalEntityKind.Zone => new LuaMAiPhysicalLimit(5, 10, 20),
            LuaMAiPhysicalEntityKind.Ship => new LuaMAiPhysicalLimit(1, 2, 2),
            LuaMAiPhysicalEntityKind.Drone => new LuaMAiPhysicalLimit(6, 12, 12),
            LuaMAiPhysicalEntityKind.Trace => new LuaMAiPhysicalLimit(32, 64, 128),
            LuaMAiPhysicalEntityKind.Drop => new LuaMAiPhysicalLimit(4, 8, 16),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public bool CanSpawn(LuaMAiPhysicalEntityKind kind, MapId mapId, out string reason)
    {
        if (!Enabled)
            return Reject(kind, "disabled", LuaMAiPhysicalBaseFeature.DisabledReason, out reason);

        if (mapId == MapId.Nullspace)
            return Reject(kind, "nullspace", $"AI physical {GetLabel(kind)} spawn rejected: nullspace is not admitted", out reason);

        var limit = GetLimit(kind);
        var mapCount = _liveByMap.GetValueOrDefault((kind, mapId));
        if (mapCount >= limit.PerMap)
            return Reject(kind, "map_cap", $"AI physical {GetLabel(kind)} spawn rejected: map cap {mapCount}/{limit.PerMap}", out reason);

        var live = _liveByKind[(int) kind];
        if (live >= limit.LivePerRound)
            return Reject(kind, "round_live_cap", $"AI physical {GetLabel(kind)} spawn rejected: round live cap {live}/{limit.LivePerRound}", out reason);

        var spawned = _roundSpawns[(int) kind];
        if (spawned >= limit.SpawnsPerRound)
            return Reject(kind, "round_spawn_cap", $"AI physical {GetLabel(kind)} spawn rejected: round spawn cap {spawned}/{limit.SpawnsPerRound}", out reason);

        reason = string.Empty;
        return true;
    }

    public int GetLiveCount(LuaMAiPhysicalEntityKind kind, MapId? mapId = null)
    {
        return mapId == null
            ? _liveByKind[(int) kind]
            : _liveByMap.GetValueOrDefault((kind, mapId.Value));
    }

    public int GetRoundSpawnCount(LuaMAiPhysicalEntityKind kind)
    {
        return _roundSpawns[(int) kind];
    }

    public bool TryGetAdmissionMap(EntityUid uid, LuaMAiPhysicalEntityKind kind, out MapId mapId)
    {
        if (_admitted.TryGetValue(uid, out var admission) && admission.Kind == kind)
        {
            mapId = admission.MapId;
            return true;
        }

        mapId = MapId.Nullspace;
        return false;
    }

    public long GetRejectionCount(LuaMAiPhysicalEntityKind kind, string reason)
    {
        return _rejections.GetValueOrDefault((kind, reason));
    }

    public LuaMAiPhysicalSliceDiagnostics GetSliceDiagnostics(string system)
    {
        return _sliceDiagnostics.GetValueOrDefault(system);
    }

    public void RecordSlice(string system, int processed, bool exhausted, string budget = "item_or_time")
    {
        var previous = _sliceDiagnostics.GetValueOrDefault(system);
        var totalExhaustions = previous.Exhaustions;
        if (exhausted)
        {
            totalExhaustions++;
            BudgetExhaustions.WithLabels(system, budget).Inc();
        }

        _sliceDiagnostics[system] = new LuaMAiPhysicalSliceDiagnostics(processed, totalExhaustions);
    }

    public List<EntityUid> GetEntitySlice(LuaMAiPhysicalEntityKind kind, ref int cursor, int maximum)
    {
        var candidates = new List<EntityUid>();
        foreach (var (uid, admission) in _admitted)
        {
            if (admission.Kind == kind && !TerminatingOrDeleted(uid))
                candidates.Add(uid);
        }

        if (candidates.Count == 0 || maximum <= 0)
        {
            cursor = 0;
            return new List<EntityUid>();
        }

        cursor = Math.Abs(cursor % candidates.Count);
        var take = Math.Min(maximum, candidates.Count);
        var result = new List<EntityUid>(take);
        for (var i = 0; i < take; i++)
            result.Add(candidates[(cursor + i) % candidates.Count]);

        cursor = (cursor + take) % candidates.Count;
        return result;
    }

    private void OnEnabledChanged(bool enabled)
    {
        Enabled = enabled;
        if (enabled)
        {
            AuditExistingEntities();
            return;
        }

        var admitted = new List<(EntityUid Uid, LuaMAiPhysicalEntityKind Kind)>(_admitted.Count);
        foreach (var (uid, admission) in _admitted)
            admitted.Add((uid, admission.Kind));

        foreach (var (uid, kind) in admitted)
        {
            ReleaseAdmission(uid, kind);
            RejectEntity(uid, kind);
        }
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        Array.Clear(_roundSpawns);
        _sliceDiagnostics.Clear();
    }

    private void AuditExistingEntities()
    {
        Audit<LuaMAiBaseAnchorComponent>(LuaMAiPhysicalEntityKind.Anchor);
        Audit<LuaMAiBaseZoneComponent>(LuaMAiPhysicalEntityKind.Zone);
        Audit<LuaMAiLogisticsShipComponent>(LuaMAiPhysicalEntityKind.Ship);
        Audit<LuaMAiMiningDroneComponent>(LuaMAiPhysicalEntityKind.Drone);
        Audit<LuaMAiDroneTraceComponent>(LuaMAiPhysicalEntityKind.Trace);
        Audit<LuaMAiSupplyDropComponent>(LuaMAiPhysicalEntityKind.Drop);
    }

    private void Audit<T>(LuaMAiPhysicalEntityKind kind) where T : IComponent
    {
        var query = EntityQueryEnumerator<T>();
        while (query.MoveNext(out var uid, out _))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            AdmitOrReject(uid, kind);
        }
    }

    private void OnShipStartup(EntityUid uid, LuaMAiLogisticsShipComponent component, ComponentStartup args) => AdmitOrReject(uid, LuaMAiPhysicalEntityKind.Ship);

    private void OnZoneShutdown(EntityUid uid, LuaMAiBaseZoneComponent component, ComponentShutdown args) => ReleaseAdmission(uid, LuaMAiPhysicalEntityKind.Zone);
    private void OnDroneShutdown(EntityUid uid, LuaMAiMiningDroneComponent component, ComponentShutdown args) => ReleaseAdmission(uid, LuaMAiPhysicalEntityKind.Drone);
    private void OnTraceShutdown(EntityUid uid, LuaMAiDroneTraceComponent component, ComponentShutdown args) => ReleaseAdmission(uid, LuaMAiPhysicalEntityKind.Trace);
    private void OnDropShutdown(EntityUid uid, LuaMAiSupplyDropComponent component, ComponentShutdown args) => ReleaseAdmission(uid, LuaMAiPhysicalEntityKind.Drop);

    private void OnAnchorMoved(EntityUid uid, LuaMAiBaseAnchorComponent component, ref MoveEvent args) => HandleMove(uid, LuaMAiPhysicalEntityKind.Anchor);
    private void OnZoneMoved(EntityUid uid, LuaMAiBaseZoneComponent component, ref MoveEvent args) => HandleMove(uid, LuaMAiPhysicalEntityKind.Zone);
    private void OnShipMoved(EntityUid uid, LuaMAiLogisticsShipComponent component, ref MoveEvent args) => HandleMove(uid, LuaMAiPhysicalEntityKind.Ship);
    private void OnDroneMoved(EntityUid uid, LuaMAiMiningDroneComponent component, ref MoveEvent args) => HandleMove(uid, LuaMAiPhysicalEntityKind.Drone);
    private void OnTraceMoved(EntityUid uid, LuaMAiDroneTraceComponent component, ref MoveEvent args) => HandleMove(uid, LuaMAiPhysicalEntityKind.Trace);
    private void OnDropMoved(EntityUid uid, LuaMAiSupplyDropComponent component, ref MoveEvent args) => HandleMove(uid, LuaMAiPhysicalEntityKind.Drop);

    public void AdmitOrReject(EntityUid uid, LuaMAiPhysicalEntityKind kind)
    {
        if (_admitted.TryGetValue(uid, out var existing))
        {
            if (existing.Kind == kind)
                return;

            RecordRejection(kind, "component_conflict");
            RejectEntity(uid, kind);
            return;
        }

        if (!Enabled)
        {
            RecordRejection(kind, "disabled");
            RejectEntity(uid, kind);
            return;
        }

        if (!TryComp<TransformComponent>(uid, out var transform) ||
            !CanSpawn(kind, transform.MapID, out _))
        {
            RejectEntity(uid, kind);
            return;
        }

        AddAdmission(uid, kind, transform.MapID);
    }

    private void HandleMove(EntityUid uid, LuaMAiPhysicalEntityKind kind)
    {
        if (!_admitted.TryGetValue(uid, out var admission) ||
            admission.Kind != kind ||
            !TryComp<TransformComponent>(uid, out var transform) ||
            transform.MapID == admission.MapId)
        {
            return;
        }

        RemoveLive(admission);
        _admitted.Remove(uid);
        if (!CanSpawnAfterMove(admission.Kind, transform.MapID, out _))
        {
            RejectEntity(uid, admission.Kind);
            return;
        }

        AddLive(uid, admission.Kind, transform.MapID, chargeRoundSpawn: false);
    }

    private bool CanSpawnAfterMove(LuaMAiPhysicalEntityKind kind, MapId mapId, out string reason)
    {
        if (mapId == MapId.Nullspace)
            return Reject(kind, "nullspace", $"AI physical {GetLabel(kind)} move rejected: nullspace is not admitted", out reason);

        var limit = GetLimit(kind);
        var mapCount = _liveByMap.GetValueOrDefault((kind, mapId));
        if (mapCount >= limit.PerMap)
            return Reject(kind, "map_cap", $"AI physical {GetLabel(kind)} move rejected: map cap {mapCount}/{limit.PerMap}", out reason);

        reason = string.Empty;
        return true;
    }

    private void AddAdmission(EntityUid uid, LuaMAiPhysicalEntityKind kind, MapId mapId)
    {
        AddLive(uid, kind, mapId, chargeRoundSpawn: true);
    }

    private void AddLive(EntityUid uid, LuaMAiPhysicalEntityKind kind, MapId mapId, bool chargeRoundSpawn)
    {
        _admitted[uid] = new Admission(kind, mapId);
        _liveByMap[(kind, mapId)] = _liveByMap.GetValueOrDefault((kind, mapId)) + 1;
        _liveByKind[(int) kind]++;
        if (chargeRoundSpawn)
            _roundSpawns[(int) kind]++;
        RefreshEntityMetric(kind);
    }

    public void ReleaseAdmission(EntityUid uid, LuaMAiPhysicalEntityKind kind)
    {
        if (!_admitted.TryGetValue(uid, out var admission) || admission.Kind != kind)
            return;

        _admitted.Remove(uid);
        RemoveLive(admission);
    }

    private void RemoveLive(Admission admission)
    {
        var key = (admission.Kind, admission.MapId);
        var mapCount = Math.Max(0, _liveByMap.GetValueOrDefault(key) - 1);
        if (mapCount == 0)
            _liveByMap.Remove(key);
        else
            _liveByMap[key] = mapCount;

        _liveByKind[(int) admission.Kind] = Math.Max(0, _liveByKind[(int) admission.Kind] - 1);
        RefreshEntityMetric(admission.Kind);
    }

    private void RejectEntity(EntityUid uid, LuaMAiPhysicalEntityKind kind)
    {
        if (TerminatingOrDeleted(uid))
            return;

        // Logistics admission happens after a ship grid has already been created. Removing only
        // its marker would leave the expensive grid behind and turn repeated bypasses into growth.
        // The component belongs on the grid owner; if it is malformed on a child, delete only that
        // child rather than risking an unrelated station grid.
        if (kind == LuaMAiPhysicalEntityKind.Ship &&
            TryComp<TransformComponent>(uid, out var transform) &&
            transform.GridUid is { Valid: true } gridUid &&
            gridUid == uid)
        {
            QueueDel(gridUid);
            return;
        }

        QueueDel(uid);
    }

    private bool Reject(LuaMAiPhysicalEntityKind kind, string code, string message, out string reason)
    {
        RecordRejection(kind, code);
        reason = message;
        return false;
    }

    private void RecordRejection(LuaMAiPhysicalEntityKind kind, string reason)
    {
        _rejections[(kind, reason)] = _rejections.GetValueOrDefault((kind, reason)) + 1;
        SpawnRejections.WithLabels(GetLabel(kind), reason).Inc();
    }

    private void RefreshEntityMetrics()
    {
        foreach (var kind in Enum.GetValues<LuaMAiPhysicalEntityKind>())
            RefreshEntityMetric(kind);
    }

    private void RefreshEntityMetric(LuaMAiPhysicalEntityKind kind)
    {
        ActiveEntities.WithLabels(GetLabel(kind)).Set(_liveByKind[(int) kind]);
    }

    private static string GetLabel(LuaMAiPhysicalEntityKind kind)
    {
        return kind.ToString().ToLowerInvariant();
    }

    private readonly record struct Admission(LuaMAiPhysicalEntityKind Kind, MapId MapId);
}
