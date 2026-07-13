using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Shuttles.Components;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Sector;

/// <summary>
/// Maintains a very small number of moving, radar-only transit contacts on the
/// primary sector map while active players and a radar console are present. These entities have no grid, sprite,
/// crew, AI, or colliding fixture and are therefore intentionally much cheaper than NPC ships.
/// </summary>
public sealed class LuaMSectorTrafficSystem : EntitySystem
{
    public const string ContactPrototype = "LuaMSectorTrafficContact";
    public const int HardMaxContacts = 4;
    public const float RadarActivationRadius = 128f;
    public const float MinimumRouteRadius = 170f;
    public const float MaximumRouteRadius = 230f;
    public const float MinimumRouteClearance = 120f;
    public const float MaximumRouteClearance = 170f;
    public const float MinimumSpeed = 4f;
    public const float MaximumSpeed = 7f;
    public const float MaximumRetentionDistance = 384f;

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly List<Vector2> _playerAnchors = new();
    private readonly List<Vector2> _radarAnchors = new();
    private readonly HashSet<MapId> _activeMaps = new();
    private TimeSpan _nextMaintenance;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_ticker.RunLevel != GameRunLevel.InRound || _timing.CurTime < _nextMaintenance)
            return;

        _nextMaintenance = _timing.CurTime + MaintenanceInterval;
        MaintainTraffic();
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New == GameRunLevel.InRound)
        {
            _nextMaintenance = TimeSpan.Zero;
            return;
        }

        if (ev.Old == GameRunLevel.InRound)
            ClearAllContacts();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        ClearAllContacts();
        _nextMaintenance = TimeSpan.Zero;
    }

    /// <summary>
    /// Reconciles traffic for the active primary sector map around radar consoles
    /// that are close to non-ghost players.
    /// </summary>
    public void MaintainTraffic()
    {
        _playerAnchors.Clear();
        _radarAnchors.Clear();
        _activeMaps.Clear();

        var sectorMap = _ticker.DefaultMap;
        if (sectorMap == MapId.Nullspace)
        {
            RemoveContactsOutsideActiveMaps();
            return;
        }

        CollectActivePlayerAnchors(sectorMap, _playerAnchors);
        if (_playerAnchors.Count == 0)
        {
            RemoveContactsOutsideActiveMaps();
            return;
        }

        CollectActiveRadarAnchors(sectorMap, _playerAnchors, _radarAnchors);
        if (_radarAnchors.Count == 0)
        {
            RemoveContactsOutsideActiveMaps();
            return;
        }

        var enabled = _cfg.GetCVar(CCVars.LuaMSectorTrafficEnabled);
        var desired = enabled
            ? Math.Clamp(_cfg.GetCVar(CCVars.LuaMSectorTrafficContacts), 0, HardMaxContacts)
            : 0;

        _activeMaps.Add(sectorMap);
        EnsureTrafficForMap(sectorMap, _radarAnchors, desired, spawnBudget: 1);

        RemoveContactsOutsideActiveMaps();
    }

    private void CollectActiveRadarAnchors(
        MapId mapId,
        IReadOnlyList<Vector2> playerAnchors,
        List<Vector2> radarAnchors)
    {
        var activationDistanceSquared = RadarActivationRadius * RadarActivationRadius;
        var radarQuery = EntityQueryEnumerator<RadarConsoleComponent, TransformComponent>();
        while (radarQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (TerminatingOrDeleted(uid) || xform.MapID != mapId)
                continue;

            var radarPosition = _transform.GetWorldPosition(xform);
            foreach (var playerPosition in playerAnchors)
            {
                if ((radarPosition - playerPosition).LengthSquared() > activationDistanceSquared)
                    continue;

                radarAnchors.Add(radarPosition);
                break;
            }
        }
    }

    private void CollectActivePlayerAnchors(MapId mapId, List<Vector2> anchors)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame ||
                session.AttachedEntity is not { Valid: true } player ||
                HasComp<GhostComponent>(player) ||
                Transform(player).MapID != mapId)
            {
                continue;
            }

            anchors.Add(_transform.GetWorldPosition(Transform(player)));
        }
    }

    /// <summary>
    /// Reconciles one map to a bounded contact count. Kept public so admin tooling
    /// and integration tests can verify the same hard-cap path used in production.
    /// </summary>
    public IReadOnlyList<EntityUid> EnsureTrafficForMap(
        MapId mapId,
        IReadOnlyList<Vector2> trafficAnchors,
        int desiredCount,
        int spawnBudget = int.MaxValue)
    {
        var desired = Math.Clamp(desiredCount, 0, HardMaxContacts);
        var contacts = new List<EntityUid>(HardMaxContacts);
        var now = _timing.CurTime;

        var query = EntityQueryEnumerator<LuaMSectorTrafficContactComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var contact, out var xform))
        {
            if (xform.MapID != mapId || TerminatingOrDeleted(uid))
                continue;

            if (contact.ExpiresAt <= now ||
                contacts.Count >= desired ||
                !IsNearAnyAnchor(_transform.GetWorldPosition(xform), trafficAnchors))
            {
                QueueDel(uid);
                continue;
            }

            contacts.Add(uid);
        }

        if (desired == 0 || trafficAnchors.Count == 0 || mapId == MapId.Nullspace)
            return contacts;

        var remainingSpawnBudget = Math.Max(0, spawnBudget);
        while (contacts.Count < desired && remainingSpawnBudget-- > 0)
        {
            var anchor = trafficAnchors[_random.Next(trafficAnchors.Count)];
            var contact = SpawnContact(new MapCoordinates(anchor, mapId));
            contacts.Add(contact);
        }

        return contacts;
    }

    private static bool IsNearAnyAnchor(Vector2 position, IReadOnlyList<Vector2> anchors)
    {
        var maximumDistanceSquared = MaximumRetentionDistance * MaximumRetentionDistance;
        foreach (var anchor in anchors)
        {
            if ((position - anchor).LengthSquared() <= maximumDistanceSquared)
                return true;
        }

        return false;
    }

    private EntityUid SpawnContact(MapCoordinates anchor)
    {
        var radial = _random.NextAngle().ToVec();
        var tangent = new Vector2(-radial.Y, radial.X);
        var radius = _random.NextFloat(MinimumRouteRadius, MaximumRouteRadius);
        var speed = _random.NextFloat(MinimumSpeed, MaximumSpeed);
        var clearance = _random.NextFloat(MinimumRouteClearance, MaximumRouteClearance);
        if (_random.Prob(0.5f))
            clearance = -clearance;

        var direction = (-radial * radius + tangent * clearance).Normalized();
        var velocity = direction * speed;
        var spawnCoordinates = new MapCoordinates(anchor.Position + radial * radius, anchor.MapId);

        var uid = Spawn(ContactPrototype, spawnCoordinates);
        var contact = Comp<LuaMSectorTrafficContactComponent>(uid);
        var body = Comp<PhysicsComponent>(uid);
        contact.RouteVelocity = velocity;
        contact.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(
            _random.NextFloat((float) MinimumLifetime.TotalSeconds, (float) MaximumLifetime.TotalSeconds));

        _physics.SetLinearVelocity(uid, velocity, body: body);
        _transform.SetLocalRotation(uid, velocity.ToWorldAngle());
        return uid;
    }

    private void RemoveContactsOutsideActiveMaps()
    {
        var query = EntityQueryEnumerator<LuaMSectorTrafficContactComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!_activeMaps.Contains(xform.MapID) && !TerminatingOrDeleted(uid))
                QueueDel(uid);
        }
    }

    private void ClearAllContacts()
    {
        var query = EntityQueryEnumerator<LuaMSectorTrafficContactComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        _playerAnchors.Clear();
        _radarAnchors.Clear();
        _activeMaps.Clear();
    }
}
