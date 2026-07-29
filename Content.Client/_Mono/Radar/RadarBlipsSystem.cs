using System.Linq;
using System.Numerics;
using Content.Shared._Mono.Radar;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Client._Mono.Radar;

public sealed partial class RadarBlipsSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _xform = default!;

    private const double BlipStaleSeconds = 3.0;
    private static readonly TimeSpan RequestThrottle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromSeconds(30);
    private static readonly Color MissileVectorColor = Color.FromHex("#00AACC");
    private static readonly Color MissileArcColor = Color.FromHex("#FF0040");

    private readonly Dictionary<NetEntity, RadarSnapshot> _snapshots = new();
    private readonly List<BlipData> _emptyBlips = new();
    private readonly List<MissileVectorData> _emptyMissiles = new();
    private readonly List<HitscanNetData> _emptyHitscans = new();
    private readonly List<ShipHitReportNetData> _emptyHitReports = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<GiveBlipsEvent>(HandleReceiveBlips);
    }

    private void HandleReceiveBlips(GiveBlipsEvent ev, EntitySessionEventArgs args)
    {
        if (!_snapshots.TryGetValue(ev.Radar, out var snapshot) ||
            !snapshot.HasRequested ||
            !ShouldAcceptResponse(
                ev.RequestId,
                snapshot.LatestRequestId,
                snapshot.HasApplied,
                snapshot.LastAppliedRequestId))
        {
            return;
        }

        snapshot.ConfigPalette = ev.ConfigPalette;
        snapshot.Blips = ev.Blips;
        snapshot.Missiles = ev.Missiles;
        snapshot.Hitscans = ev.HitscanLines;
        snapshot.OwnshipTelemetry = ev.OwnshipTelemetry;
        snapshot.HitReports = ev.HitReports;
        snapshot.BlipsByUid.Clear();
        foreach (var blip in ev.Blips)
            snapshot.BlipsByUid[blip.Uid] = blip;

        snapshot.HasApplied = true;
        snapshot.LastAppliedRequestId = ev.RequestId;
        snapshot.SampleTime = ev.SampleTime;
    }

    public void RequestBlips(EntityUid console)
    {
        // Only request if we have a valid console
        if (!Exists(console))
            return;

        var netConsole = GetNetEntity(console);
        var snapshot = GetOrCreateSnapshot(netConsole);
        if (snapshot.HasRequested && _timing.CurTime - snapshot.LastRequestTime < RequestThrottle)
            return;

        snapshot.HasRequested = true;
        snapshot.LastRequestTime = _timing.CurTime;
        snapshot.LatestRequestId = unchecked(snapshot.LatestRequestId + 1);
        if (snapshot.LatestRequestId == 0)
            snapshot.LatestRequestId = 1;

        PruneSnapshots(netConsole);
        var ev = new RequestBlipsEvent(netConsole, snapshot.LatestRequestId);
        RaiseNetworkEvent(ev);
    }

    /// <summary>
    /// Gets the current blips as world positions with their scale, color and shape.
    /// </summary>
    public List<BlipData> GetCurrentBlips(EntityUid? console)
    {
        if (!TryGetSnapshot(console, out var snapshot))
        {
            _emptyBlips.Clear();
            return _emptyBlips;
        }

        snapshot.CachedBlips.Clear();
        if (IsStale(snapshot))
            return snapshot.CachedBlips;

        foreach (var blip in snapshot.Blips)
        {
            var coord = GetCoordinates(blip.Position);

            if (!coord.IsValid(EntityManager))
                continue;

            var predictedPos = new EntityCoordinates(
                coord.EntityId,
                coord.Position + blip.Vel * GetSnapshotAge(snapshot));

            if (blip.ConfigIndex >= snapshot.ConfigPalette.Count)
                continue;

            var config = snapshot.ConfigPalette[blip.ConfigIndex];
            var rotation = blip.Rotation;
            EntityUid? gridUid = null;
            var snapshotAge = GetSnapshotAge(snapshot);

            // The server deliberately parents blip coordinates to their exact grid.
            // Spatial lookup is ambiguous when grids overlap, so preserve that parent.
            if (HasComp<MapGridComponent>(coord.EntityId))
            {
                gridUid = coord.EntityId;
                if (blip.OnGridConfigIndex is { } gridIdx && gridIdx < snapshot.ConfigPalette.Count)
                    config = snapshot.ConfigPalette[gridIdx];
                rotation += Transform(coord.EntityId).LocalRotation;
            }

            snapshot.CachedBlips.Add(new(
                blip.Uid,
                predictedPos,
                blip.Vel,
                rotation,
                gridUid,
                config,
                blip.IsWeaponProjectile,
                blip.Threat,
                blip.TimeToImpact is { } impact
                    ? MathF.Max(0f, impact - snapshotAge)
                    : null));
        }

        return snapshot.CachedBlips;
    }

    /// <summary>
    /// Gets the missile vectors to be rendered on the radar
    /// </summary>
    public List<MissileVectorData> GetMissileLines(EntityUid? console)
    {
        if (!TryGetSnapshot(console, out var snapshot))
        {
            _emptyMissiles.Clear();
            return _emptyMissiles;
        }

        snapshot.CachedMissiles.Clear();
        if (IsStale(snapshot))
            return snapshot.CachedMissiles;

        foreach (var missile in snapshot.Missiles)
        {
            if (!snapshot.BlipsByUid.TryGetValue(missile.Uid, out var tiedBlip))
                continue;

            var coord = GetCoordinates(tiedBlip.Position);
            if (!coord.IsValid(EntityManager))
                continue;

            var elapsed = GetSnapshotAge(snapshot);
            var predictedCoordinates = new EntityCoordinates(
                coord.EntityId,
                coord.Position + tiedBlip.Vel * elapsed);
            var predictedMap = _xform.ToMapCoordinates(predictedCoordinates);
            var rotation = tiedBlip.Rotation;
            EntityUid? gridUid = null;

            // Blip positions and rotations are grid-local when the server can safely
            // parent them to a grid. Missile vectors are rendered in map space, so
            // restore the exact parent grid rotation before projecting endpoints.
            // Spatial lookup is deliberately avoided because overlapping grids can
            // make TryFindGridAt return a different grid after extrapolation.
            if (HasComp<MapGridComponent>(coord.EntityId))
            {
                gridUid = coord.EntityId;
                rotation += Transform(coord.EntityId).LocalRotation;
            }

            var predictedPosStart = predictedMap.Position;
            var posEnd = Vector2.Create(
                predictedPosStart.X + missile.Range / 2 * (float) Math.Cos(rotation + Math.PI * -0.5),
                predictedPosStart.Y + missile.Range / 2 * (float) Math.Sin(rotation + Math.PI * -0.5));

            snapshot.CachedMissiles.Add(new(
                missile.Uid,
                predictedPosStart,
                posEnd,
                gridUid,
                MissileVectorColor));
            if (missile.ScanArc > 0)
            {
                var posEndLeft = Vector2.Create(
                    predictedPosStart.X + missile.Range * (float) Math.Cos(rotation + Math.PI * -0.5 - missile.ScanArc * 0.5),
                    predictedPosStart.Y + missile.Range * (float) Math.Sin(rotation + Math.PI * -0.5 - missile.ScanArc * 0.5));
                var posEndRight = Vector2.Create(
                    predictedPosStart.X + missile.Range * (float) Math.Cos(rotation + Math.PI * -0.5 + missile.ScanArc * 0.5),
                    predictedPosStart.Y + missile.Range * (float) Math.Sin(rotation + Math.PI * -0.5 + missile.ScanArc * 0.5));
                snapshot.CachedMissiles.Add(new(
                    missile.Uid,
                    predictedPosStart,
                    posEndLeft,
                    gridUid,
                    MissileArcColor));
                snapshot.CachedMissiles.Add(new(
                    missile.Uid,
                    predictedPosStart,
                    posEndRight,
                    gridUid,
                    MissileArcColor));
            }
        }

        return snapshot.CachedMissiles;
    }

    /// <summary>
    /// Gets the hitscan lines to be rendered on the radar
    /// </summary>
    public List<HitscanNetData> GetHitscanLines(EntityUid? console)
    {
        if (!TryGetSnapshot(console, out var snapshot) || IsStale(snapshot))
        {
            _emptyHitscans.Clear();
            return _emptyHitscans;
        }

        return snapshot.Hitscans;
    }

    public bool TryGetOwnshipTelemetry(EntityUid? console, out OwnshipTelemetryNetData telemetry)
    {
        if (!TryGetSnapshot(console, out var snapshot) ||
            IsStale(snapshot) ||
            snapshot.OwnshipTelemetry is not { } sampled)
        {
            telemetry = default;
            return false;
        }

        telemetry = sampled with
        {
            Position = sampled.Position + sampled.Velocity * GetSnapshotAge(snapshot),
        };
        return true;
    }

    public List<ShipHitReportNetData> GetHitReports(EntityUid? console)
    {
        if (!TryGetSnapshot(console, out var snapshot) || IsStale(snapshot))
        {
            _emptyHitReports.Clear();
            return _emptyHitReports;
        }

        return snapshot.HitReports;
    }

    public RadarThreatSummary GetThreatSummary(EntityUid? console)
    {
        if (!TryGetSnapshot(console, out var snapshot) || IsStale(snapshot))
            return default;

        var incoming = 0;
        var locks = 0;
        float? nearestImpact = null;
        var snapshotAge = GetSnapshotAge(snapshot);
        foreach (var blip in snapshot.Blips)
        {
            switch (blip.Threat)
            {
                case RadarThreatKind.Incoming:
                    incoming++;
                    break;
                case RadarThreatKind.MissileLock:
                    locks++;
                    break;
            }

            if (blip.TimeToImpact is not { } sampledImpact)
                continue;

            var impact = MathF.Max(0f, sampledImpact - snapshotAge);
            nearestImpact = nearestImpact is { } current
                ? MathF.Min(current, impact)
                : impact;
        }

        return new RadarThreatSummary(incoming, locks, nearestImpact);
    }

    private RadarSnapshot GetOrCreateSnapshot(NetEntity radar)
    {
        if (_snapshots.TryGetValue(radar, out var snapshot))
            return snapshot;

        snapshot = new RadarSnapshot();
        _snapshots.Add(radar, snapshot);
        return snapshot;
    }

    private bool TryGetSnapshot(EntityUid? console, out RadarSnapshot snapshot)
    {
        if (console is not { Valid: true } uid ||
            !Exists(uid) ||
            !_snapshots.TryGetValue(GetNetEntity(uid), out var found))
        {
            snapshot = default!;
            return false;
        }

        snapshot = found;
        return true;
    }

    private bool IsStale(RadarSnapshot snapshot)
    {
        return !snapshot.HasApplied || (_timing.CurTime - snapshot.SampleTime).TotalSeconds > BlipStaleSeconds;
    }

    private float GetSnapshotAge(RadarSnapshot snapshot)
    {
        return MathF.Max(0f, (float) (_timing.CurTime - snapshot.SampleTime).TotalSeconds);
    }

    /// <summary>
    /// Compares wrapping request sequence numbers. A response may legitimately arrive
    /// after a newer request has already been sent, but it must never replace a newer
    /// snapshot that was already applied.
    /// </summary>
    internal static bool IsNewerRequestId(uint candidate, uint baseline)
    {
        return unchecked((int) (candidate - baseline)) > 0;
    }

    internal static bool ShouldAcceptResponse(
        uint responseId,
        uint latestRequestId,
        bool hasApplied,
        uint lastAppliedRequestId)
    {
        return !IsNewerRequestId(responseId, latestRequestId) &&
               (!hasApplied || IsNewerRequestId(responseId, lastAppliedRequestId));
    }

    private void PruneSnapshots(NetEntity activeRadar)
    {
        if (_snapshots.Count < 64)
            return;

        var now = _timing.CurTime;
        foreach (var (radar, snapshot) in _snapshots.ToArray())
        {
            if (radar != activeRadar && now - snapshot.LastRequestTime > SnapshotRetention)
                _snapshots.Remove(radar);
        }
    }

    private sealed class RadarSnapshot
    {
        public uint LatestRequestId;
        public uint LastAppliedRequestId;
        public bool HasRequested;
        public bool HasApplied;
        public TimeSpan LastRequestTime;
        public TimeSpan SampleTime;
        public List<BlipNetData> Blips = new();
        public List<MissileVectorNetData> Missiles = new();
        public List<HitscanNetData> Hitscans = new();
        public OwnshipTelemetryNetData? OwnshipTelemetry;
        public List<ShipHitReportNetData> HitReports = new();
        public List<BlipConfig> ConfigPalette = new();
        public readonly Dictionary<NetEntity, BlipNetData> BlipsByUid = new();
        public readonly List<BlipData> CachedBlips = new();
        public readonly List<MissileVectorData> CachedMissiles = new();
    }
}

public record struct BlipData
(
    NetEntity NetUid,
    EntityCoordinates Position,
    Vector2 Velocity,
    Angle Rotation,
    EntityUid? GridUid,
    BlipConfig Config,
    bool IsWeaponProjectile,
    RadarThreatKind Threat,
    float? TimeToImpact
);

public record struct MissileVectorData
(
    NetEntity NetUid,
    Vector2 PositionStart,
    Vector2 PositionEnd,
    EntityUid? GridUid,
    Color Color
);

public readonly record struct RadarThreatSummary(
    int IncomingCount,
    int MissileLockCount,
    float? NearestImpactTime);
