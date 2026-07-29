using System.Linq;
using System.Numerics;
using Content.Shared._Crescent.DroneControl;
using Content.Shared._Mono.Detection;
using Content.Shared._Mono.FireControl;
using Content.Shared._Mono.SpaceArtillery;
using Content.Server._Mono.Projectiles.TargetSeeking;
using Content.Server.Physics.Controllers;
using Content.Shared._Mono.Radar;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using ShuttleComponent = Content.Server.Shuttles.Components.ShuttleComponent;

namespace Content.Server._Mono.Radar;

public sealed partial class RadarBlipSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private HitscanRadarSystem _hitscanRadar = default!;
    [Dependency] private DetectionSystem _detection = default!;
    [Dependency] private MoverController _mover = default!;
    [Dependency] private ShipCombatTelemetrySystem _combatTelemetry = default!;

    private static readonly TimeSpan BlipRequestCooldown = TimeSpan.FromMilliseconds(500);
    private const int MaxHitscansPerReport = 256;
    private const float IncomingThreatLeadTime = 12f;
    private const float IncomingThreatBoundsPadding = 1.5f;
    private static readonly Enum[] RadarUiKeys =
    [
        RadarConsoleUiKey.Key,
        ShuttleConsoleUiKey.Key,
        FireControlConsoleUiKey.Key,
        DroneConsoleUiKey.Key,
    ];

    // Pooled collections to avoid per-request heap churn
    private readonly List<BlipNetData> _tempBlipsCache = new();
    private readonly List<MissileVectorNetData> _tempMissileCache = new();
    private readonly List<HitscanNetData> _tempHitscansCache = new();
    private readonly List<EntityUid> _tempSourcesCache = new();
    private readonly List<Vector2> _tempSourcePositionsCache = new();
    private readonly List<BlipConfig> _tempPaletteCache = new();
    private readonly List<HitscanRadarSystem.RecentHitscan> _tempRecentHitscansCache = new();
    private readonly List<ShipHitReportNetData> _tempHitReportsCache = new();
    private readonly HashSet<EntityUid> _tempDetectionSourcesCache = new();
    private readonly Dictionary<EntityUid, DetectionLevel> _tempDetectionCache = new();
    private readonly HashSet<Entity<RadarBlipComponent>> _tempBlipCandidates = new();
    private readonly Dictionary<BlipConfig, ushort> _paletteIndex = new();
    private readonly Dictionary<(NetUserId UserId, EntityUid Radar), TimeSpan> _nextBlipRequestByUserRadar = new();
    private OwnshipTelemetryNetData? _tempOwnshipTelemetry;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestBlipsEvent>(OnBlipsRequested);
        SubscribeLocalEvent<RadarConsoleComponent, ComponentShutdown>(OnRadarShutdown);
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _nextBlipRequestByUserRadar.Clear();
    }

    private void OnBlipsRequested(RequestBlipsEvent ev, EntitySessionEventArgs args)
    {
        if (!TryGetEntity(ev.Radar, out var radarUid) ||
            !TryComp<RadarConsoleComponent>(radarUid, out var radar) ||
            args.SenderSession.AttachedEntity is not { Valid: true } actor ||
            !HasOpenRadarUi(radarUid.Value, actor))
        {
            return;
        }

        var key = (args.SenderSession.UserId, radarUid.Value);
        var now = _timing.CurTime;
        if (_nextBlipRequestByUserRadar.TryGetValue(key, out var nextRequest) &&
            now < nextRequest)
        {
            return;
        }

        _nextBlipRequestByUserRadar[key] = now + BlipRequestCooldown;

        ClearTemporaryBuffers();
        try
        {
            var sourcesEv = new GetRadarSourcesEvent();
            RaiseLocalEvent(radarUid.Value, ref sourcesEv);

            if (sourcesEv.Sources != null)
                _tempSourcesCache.AddRange(sourcesEv.Sources);
            else
                _tempSourcesCache.Add(radarUid.Value);

            AssembleBlipsReport(radarUid.Value, _tempSourcesCache, radar);
            AssembleHitscanReport(radarUid.Value, _tempSourcesCache, radar);
            if (Transform(radarUid.Value).GridUid is { } radarGrid)
                _combatTelemetry.CollectReports(radarGrid, _tempHitReportsCache);

            var giveEv = new GiveBlipsEvent(
                ev.Radar,
                ev.RequestId,
                now,
                _tempPaletteCache,
                _tempBlipsCache,
                _tempMissileCache,
                _tempHitscansCache,
                _tempOwnshipTelemetry,
                _tempHitReportsCache);
            RaiseNetworkEvent(giveEv, args.SenderSession);
        }
        finally
        {
            // RaiseNetworkEvent serializes synchronously, so these buffers can be reused.
            ClearTemporaryBuffers();
        }
    }

    private bool HasOpenRadarUi(EntityUid radar, EntityUid actor)
    {
        foreach (var key in RadarUiKeys)
        {
            if (_ui.IsUiOpen(radar, key, actor))
                return true;
        }

        return false;
    }

    private void OnRadarShutdown(EntityUid uid, RadarConsoleComponent component, ComponentShutdown args)
    {
        RemoveRateLimitEntries(key => key.Radar == uid);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus is not (SessionStatus.Disconnected or SessionStatus.Zombie))
            return;

        RemoveRateLimitEntries(key => key.UserId == args.Session.UserId);
    }

    private void RemoveRateLimitEntries(Func<(NetUserId UserId, EntityUid Radar), bool> predicate)
    {
        foreach (var key in _nextBlipRequestByUserRadar.Keys.Where(predicate).ToArray())
            _nextBlipRequestByUserRadar.Remove(key);
    }

    private void ClearTemporaryBuffers()
    {
        _tempBlipsCache.Clear();
        _tempMissileCache.Clear();
        _tempHitscansCache.Clear();
        _tempSourcesCache.Clear();
        _tempSourcePositionsCache.Clear();
        _tempPaletteCache.Clear();
        _tempRecentHitscansCache.Clear();
        _tempHitReportsCache.Clear();
        _tempDetectionSourcesCache.Clear();
        _tempDetectionCache.Clear();
        _tempBlipCandidates.Clear();
        _paletteIndex.Clear();
        _tempOwnshipTelemetry = null;
    }

    private void AssembleBlipsReport(EntityUid uid, List<EntityUid> sources, RadarConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var radarXform = Transform(uid);
        var radarGrid = radarXform.GridUid;
        var radarMapId = radarXform.MapID;
        var hasOwnship = TryBuildOwnshipContext(radarGrid, out var ownship);
        if (hasOwnship)
            _tempOwnshipTelemetry = ownship.Telemetry;

        var sourceBoundsInitialized = false;
        var minimumSource = Vector2.Zero;
        var maximumSource = Vector2.Zero;
        foreach (var source in sources)
        {
            if (TerminatingOrDeleted(source) ||
                !TryComp<TransformComponent>(source, out var sourceXform) ||
                sourceXform.MapID != radarMapId)
            {
                continue;
            }

            var sourcePosition = _xform.GetWorldPosition(sourceXform);
            _tempSourcePositionsCache.Add(sourcePosition);
            // Match ShuttleNavControl's detector semantics. A normal console uses
            // itself (and its configured multiplier); linked drone devices detect
            // through their owning grids, which is what the client UI receives.
            _tempDetectionSourcesCache.Add(
                source == uid
                    ? source
                    : sourceXform.GridUid ?? source);
            if (!sourceBoundsInitialized)
            {
                minimumSource = sourcePosition;
                maximumSource = sourcePosition;
                sourceBoundsInitialized = true;
            }
            else
            {
                minimumSource = Vector2.Min(minimumSource, sourcePosition);
                maximumSource = Vector2.Max(maximumSource, sourcePosition);
            }
        }

        if (!sourceBoundsInitialized)
            return;

        // Query one union bound for every source. Large radar ranges make the lookup
        // fall back to a component scan; doing that once avoids an N-sources-times-N-
        // blips regression on drone consoles. Exact radial filtering remains below.
        var maximumRange = float.IsFinite(component.MaxRange)
            ? MathF.Max(0f, component.MaxRange)
            : 0f;
        var rangeVector = new Vector2(maximumRange);
        _lookup.GetEntitiesIntersecting<RadarBlipComponent>(
            radarMapId,
            new Box2(minimumSource - rangeVector, maximumSource + rangeVector),
            _tempBlipCandidates);

        foreach (var (blipUid, blip) in _tempBlipCandidates)
        {
            if (!TryComp<TransformComponent>(blipUid, out var blipXform) ||
                !TryComp<PhysicsComponent>(blipUid, out var blipPhysics))
            {
                continue;
            }

            var blipWorldPosition = _xform.GetWorldPosition(blipXform);
            if (!blip.Enabled
                || blipXform.MapID != radarMapId
                || !NearAnySourcePosition(
                    blipWorldPosition,
                    _tempSourcePositionsCache,
                    MathF.Min(blip.MaxDistance, component.MaxRange))
            )
                continue;

            var blipGrid = blipXform.GridUid;

            // Client-side culling is only a rendering defense. Never serialize the
            // exact entity/coordinates of contacts on a grid that these detector
            // sources cannot fully resolve.
            if (blipGrid is { } detectedGrid &&
                detectedGrid != radarGrid &&
                !IsGridFullyDetected(detectedGrid))
            {
                continue;
            }

            if (blip.RequireNoGrid && blipGrid != null // if we want no grid but we are on a grid
                || !blip.VisibleFromOtherGrids && blipGrid != radarGrid // or if we don't want to be visible from other grids but we're on another grid
            )
                continue; // don't show this blip

            var netBlipUid = GetNetEntity(blipUid);

            var blipVelocity = _physics.GetMapLinearVelocity(blipUid, blipPhysics, blipXform);
            var mapBlipVelocity = blipVelocity;
            var isWeaponProjectile = HasComp<ShipWeaponProjectileComponent>(blipUid);
            var threat = RadarThreatKind.None;
            float? timeToImpact = null;
            TryComp<TargetSeekingComponent>(blipUid, out var seeker);
            if (hasOwnship &&
                isWeaponProjectile &&
                TryComp<ProjectileComponent>(blipUid, out var projectile) &&
                !OriginatesFromGrid(projectile, ownship.Grid))
            {
                if (seeker is { ExposesTracking: true, CurrentTarget: { } target } &&
                    IsTargetOnGrid(target, ownship.Grid))
                {
                    threat = RadarThreatKind.MissileLock;
                }

                if (TryGetIncomingTime(
                        ownship,
                        blipWorldPosition,
                        mapBlipVelocity,
                        out var incomingTime))
                {
                    timeToImpact = incomingTime;
                    if (threat == RadarThreatKind.None)
                        threat = RadarThreatKind.Incoming;
                }
            }

            // due to PVS being a thing, things will break if we try to parent to not the map or a grid
            var coord = blipXform.Coordinates;
            if (blipXform.ParentUid != blipXform.MapUid && blipXform.ParentUid != blipGrid)
                coord = _xform.WithEntityId(coord, blipGrid ?? blipXform.MapUid!.Value);

            var gridCfg = (BlipConfig?)null;
            var rotation = _xform.GetWorldRotation(blipXform);

            // we're parented to either the map or a grid and this is relative velocity so account for grid movement
            if (blipGrid != null)
            {
                var gridXform = Transform(blipGrid.Value);
                if (TryComp<PhysicsComponent>(blipGrid.Value, out var gridBody)) // prevent log spam
                    blipVelocity -= _physics.GetLinearVelocity(blipGrid.Value, coord.Position, gridBody);
                // it's local-frame velocity so rotate it too
                blipVelocity = (-gridXform.LocalRotation).RotateVec(blipVelocity);
                // and also offset the rotation
                rotation -= gridXform.LocalRotation;
                // and hijack our shape if we want to
                gridCfg = blip.GridConfig;
            }

            var configIdx = GetOrAddConfig(blip.Config);
            ushort? gridConfigIdx = gridCfg is { } gridCf ? GetOrAddConfig(gridCf) : null;

            // ideally we would handle blips being culled by detection on server but detection grid culling is already clientside so might as well
            _tempBlipsCache.Add(new(netBlipUid,
                            GetNetCoordinates(coord),
                            blipVelocity,
                            rotation,
                            configIdx,
                            gridConfigIdx,
                            isWeaponProjectile,
                            threat,
                            timeToImpact));

            // Only expose seeker vectors for blips that passed this radar's map,
            // range and visibility filters above. Querying all seekers globally
            // leaks missiles from other maps and leaves clients without a tied blip.
            if (seeker != null)
            {
                var missileArc = MathHelper.DegreesToRadians(seeker.ScanArc);
                _tempMissileCache.Add(new(netBlipUid,
                    (float)(seeker.MaxSpeed * 0.2),
                    missileArc));
            }
        }
    }

    /// <summary>
    /// Gets or create palette index for blip config.
    /// </summary>
    private ushort GetOrAddConfig(BlipConfig config)
    {
        if (_paletteIndex.TryGetValue(config, out var index))
            return index;

        if (_tempPaletteCache.Count >= ushort.MaxValue)
        {
            Log.Error($"Blip config count overflow! Reached max {ushort.MaxValue}, but trying to add more.");
            return 0;
        }

        index = (ushort)_tempPaletteCache.Count;
        _tempPaletteCache.Add(config);
        _paletteIndex[config] = index;
        return index;
    }

    /// <summary>
    /// Assembles trajectory information for hitscan projectiles to be displayed on radar
    /// </summary>
    private void AssembleHitscanReport(EntityUid uid, List<EntityUid> sources, RadarConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var radarXform = Transform(uid);

        var hitscanQuery = EntityQueryEnumerator<HitscanRadarComponent, TransformComponent>();

        while (hitscanQuery.MoveNext(out _, out var hitscan, out var hitscanXform))
        {
            if (_tempHitscansCache.Count >= MaxHitscansPerReport)
                break;

            if (!hitscan.Enabled || hitscanXform.MapID != radarXform.MapID)
                continue;

            if (!SegmentNearAnySources(
                    hitscan.StartPosition,
                    hitscan.EndPosition,
                    _tempSourcePositionsCache,
                    component.MaxRange))
            {
                continue;
            }

            NetEntity? originGrid = null;
            if ((hitscan.OriginGrid ?? hitscanXform.GridUid) is { } grid)
            {
                if (!Exists(grid) ||
                    Transform(grid).MapID != radarXform.MapID ||
                    grid != radarXform.GridUid && !IsGridFullyDetected(grid))
                {
                    continue;
                }

                originGrid = GetNetEntity(grid);
            }

            _tempHitscansCache.Add(new(
                hitscan.StartPosition,
                hitscan.EndPosition,
                hitscan.LineThickness,
                hitscan.RadarColor,
                originGrid));
        }

        if (_tempHitscansCache.Count >= MaxHitscansPerReport)
            return;

        _hitscanRadar.CollectRecentHitscans(radarXform.MapID, _tempRecentHitscansCache);
        // Prefer the newest retained shots when a large battle reaches the bounded
        // per-response budget. Draw order is irrelevant for these transient lines.
        for (var i = _tempRecentHitscansCache.Count - 1;
             i >= 0 && _tempHitscansCache.Count < MaxHitscansPerReport;
             i--)
        {
            var hitscan = _tempRecentHitscansCache[i];
            if (!SegmentNearAnySources(
                    hitscan.StartPosition,
                    hitscan.EndPosition,
                    _tempSourcePositionsCache,
                    component.MaxRange))
            {
                continue;
            }

            NetEntity? originGrid = null;
            if (hitscan.OriginGrid is { } grid)
            {
                if (!Exists(grid) ||
                    Transform(grid).MapID != radarXform.MapID ||
                    grid != radarXform.GridUid && !IsGridFullyDetected(grid))
                {
                    continue;
                }

                originGrid = GetNetEntity(grid);
            }

            _tempHitscansCache.Add(new(
                hitscan.StartPosition,
                hitscan.EndPosition,
                hitscan.LineThickness,
                hitscan.RadarColor,
                originGrid));
        }
    }

    private bool IsGridFullyDetected(EntityUid grid)
    {
        if (_tempDetectionCache.TryGetValue(grid, out var cached))
            return cached == DetectionLevel.Detected;

        var level = TryComp<MapGridComponent>(grid, out var gridComponent) &&
                    _tempDetectionSourcesCache.Count > 0
            ? _detection.IsGridDetected((grid, gridComponent), _tempDetectionSourcesCache)
            : DetectionLevel.Undetected;
        _tempDetectionCache[grid] = level;
        return level == DetectionLevel.Detected;
    }

    private static bool NearAnySourcePosition(Vector2 position, List<Vector2> sources, float range)
    {
        var rangeSquared = MathF.Max(0f, range) * MathF.Max(0f, range);
        foreach (var sourcePosition in sources)
        {
            if (Vector2.DistanceSquared(sourcePosition, position) <= rangeSquared)
                return true;
        }

        return false;
    }

    private static bool SegmentNearAnySources(
        Vector2 start,
        Vector2 end,
        List<Vector2> sources,
        float range)
    {
        foreach (var sourcePosition in sources)
        {
            if (IsSegmentWithinRange(start, end, sourcePosition, range))
                return true;
        }

        return false;
    }

    internal static bool IsSegmentWithinRange(Vector2 start, Vector2 end, Vector2 source, float range)
    {
        var clampedRange = MathF.Max(0f, range);
        var delta = end - start;
        var lengthSquared = delta.LengthSquared();
        var t = lengthSquared <= 1e-8f
            ? 0f
            : Math.Clamp(Vector2.Dot(source - start, delta) / lengthSquared, 0f, 1f);
        var nearest = start + delta * t;
        return Vector2.DistanceSquared(source, nearest) <= clampedRange * clampedRange;
    }

    private bool TryBuildOwnshipContext(EntityUid? gridUid, out OwnshipContext context)
    {
        context = default;
        if (gridUid is not { } grid ||
            !TryComp<MapGridComponent>(grid, out var mapGrid) ||
            !TryComp<ShuttleComponent>(grid, out var shuttle) ||
            !TryComp<PhysicsComponent>(grid, out var body) ||
            !TryComp<TransformComponent>(grid, out var xform))
        {
            return false;
        }

        var gridOrigin = _xform.GetWorldPosition(xform);
        var center = _xform.ToMapCoordinates(new EntityCoordinates(grid, body.LocalCenter));
        var velocity = _physics.GetMapLinearVelocity(grid, body, xform);
        if (!IsFinite(gridOrigin) || !IsFinite(center.Position) || !IsFinite(velocity))
            return false;

        var brakeAcceleration = 0f;
        if (velocity.LengthSquared() > 1e-6f)
        {
            brakeAcceleration = _mover
                .GetWorldDirectionAccel(-Vector2.Normalize(velocity), shuttle, body, xform)
                .Length() * ShuttleComponent.BrakeCoefficient;
        }

        if (!float.IsFinite(brakeAcceleration) || brakeAcceleration < 0f)
            brakeAcceleration = 0f;

        var telemetry = new OwnshipTelemetryNetData(center.Position, velocity, brakeAcceleration);
        context = new OwnshipContext(
            grid,
            gridOrigin,
            _xform.GetWorldRotation(xform),
            velocity,
            mapGrid.LocalAABB.Enlarged(IncomingThreatBoundsPadding),
            telemetry);
        return true;
    }

    private static bool TryGetIncomingTime(
        OwnshipContext ownship,
        Vector2 projectilePosition,
        Vector2 projectileVelocity,
        out float time)
    {
        var localPosition = (-ownship.Rotation).RotateVec(projectilePosition - ownship.GridOrigin);
        var localVelocity = (-ownship.Rotation).RotateVec(projectileVelocity - ownship.Velocity);
        return RadarCombatTelemetryMath.TryGetIncomingIntersectionTime(
            localPosition,
            localVelocity,
            ownship.LocalBounds,
            IncomingThreatLeadTime,
            out time);
    }

    private bool OriginatesFromGrid(ProjectileComponent projectile, EntityUid grid)
    {
        return EntityIsOnGrid(projectile.Shooter, grid) ||
               EntityIsOnGrid(projectile.Weapon, grid);
    }

    private bool EntityIsOnGrid(EntityUid? uid, EntityUid grid)
    {
        if (uid is not { } entity || !Exists(entity))
            return false;

        if (entity == grid)
            return true;

        return TryComp<TransformComponent>(entity, out var xform) && xform.GridUid == grid;
    }

    private bool IsTargetOnGrid(EntityUid target, EntityUid grid)
    {
        if (target == grid)
            return true;

        return TryComp<TransformComponent>(target, out var xform) && xform.GridUid == grid;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private readonly record struct OwnshipContext(
        EntityUid Grid,
        Vector2 GridOrigin,
        Angle Rotation,
        Vector2 Velocity,
        Box2 LocalBounds,
        OwnshipTelemetryNetData Telemetry);
}
