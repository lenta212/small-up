using System.Numerics;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Hitscan.Systems;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._Mono.Radar;

public sealed partial class HitscanRadarSystem : EntitySystem
{
    private const int MaxRecentHitscans = 512;
    private const int MaxRecentHitscansPerEmitter = 16;
    private const float MinimumSnapshotRetention = 0.75f;
    private const float MaximumSnapshotRetention = 5f;

    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ShipCombatTelemetrySystem _combatTelemetry = default!;

    private readonly List<RecentHitscan> _recentHitscans = new(MaxRecentHitscans);

    public override void Initialize()
    {
        base.Initialize();
        // Listen for fire events on anything that has a HitscanRadarSignatureComponent
        SubscribeLocalEvent<HitscanRadarSignatureComponent, HitscanRaycastFiredEvent>(
            OnHitscanRaycastFired,
            before: [typeof(HitscanBasicDamageSystem)]);
    }

    private void OnHitscanRaycastFired(Entity<HitscanRadarSignatureComponent> ent, ref HitscanRaycastFiredEvent ev)
    {
        // The engine permits only one directed subscription for this component/event
        // pair. Feed the impact recorder from the existing radar handler so damage
        // events still see their pending context.
        _combatTelemetry.RecordHitscanFired(ent, ref ev);

        if (!ent.Comp.Enabled ||
            !float.IsFinite(ev.DistanceTried) ||
            ev.DistanceTried <= 0f ||
            !float.IsFinite(ev.ShotDirection.X) ||
            !float.IsFinite(ev.ShotDirection.Y) ||
            ev.ShotDirection.LengthSquared() <= 1e-8f)
        {
            return;
        }

        var shooter = ev.Shooter ?? ev.Gun; // If "there is no shooter" then the shooter is a gun
        if (!TryComp<TransformComponent>(shooter, out var shooterXform))
            return;

        var mapCoordinates = _transform.ToMapCoordinates(ev.FromCoordinates);
        if (mapCoordinates.MapId == MapId.Nullspace)
            return;

        var startPos = mapCoordinates.Position;
        var endPos = startPos + Vector2.Normalize(ev.ShotDirection) * ev.DistanceTried;
        var now = _timing.CurTime;
        PruneExpired(now);

        // A rapid-fire barrel only needs enough history to bridge one radar poll.
        // Bounding both per-emitter and global history prevents a battery of hitscan
        // weapons from growing entity count or network payload without limit.
        var emitterCount = 0;
        var oldestEmitterIndex = -1;
        for (var i = 0; i < _recentHitscans.Count; i++)
        {
            if (_recentHitscans[i].Emitter != ev.Gun)
                continue;

            emitterCount++;
            if (oldestEmitterIndex == -1)
                oldestEmitterIndex = i;
        }

        if (emitterCount >= MaxRecentHitscansPerEmitter && oldestEmitterIndex >= 0)
            _recentHitscans.RemoveAt(oldestEmitterIndex);
        else if (_recentHitscans.Count >= MaxRecentHitscans)
            _recentHitscans.RemoveAt(0);

        var configuredLifetime = float.IsFinite(ent.Comp.LifeTime)
            ? Math.Clamp(ent.Comp.LifeTime, 0f, MaximumSnapshotRetention)
            : 0f;
        var retention = MathF.Max(MinimumSnapshotRetention, configuredLifetime);
        var thickness = float.IsFinite(ent.Comp.LineThickness)
            ? MathF.Max(0f, ent.Comp.LineThickness)
            : 1f;

        _recentHitscans.Add(new RecentHitscan(
            ev.Gun,
            mapCoordinates.MapId,
            startPos,
            endPos,
            thickness,
            ent.Comp.RadarColor,
            shooterXform.GridUid,
            now + TimeSpan.FromSeconds(retention)));
    }

    internal void CollectRecentHitscans(MapId mapId, List<RecentHitscan> destination)
    {
        var now = _timing.CurTime;
        PruneExpired(now);
        foreach (var hitscan in _recentHitscans)
        {
            if (hitscan.MapId == mapId)
                destination.Add(hitscan);
        }
    }

    private void PruneExpired(TimeSpan now)
    {
        for (var i = _recentHitscans.Count - 1; i >= 0; i--)
        {
            if (_recentHitscans[i].ExpiresAt <= now)
                _recentHitscans.RemoveAt(i);
        }
    }

    internal readonly record struct RecentHitscan(
        EntityUid Emitter,
        MapId MapId,
        Vector2 StartPosition,
        Vector2 EndPosition,
        float LineThickness,
        Color RadarColor,
        EntityUid? OriginGrid,
        TimeSpan ExpiresAt);
}
