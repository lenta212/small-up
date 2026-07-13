using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared._LuaM.Sector;
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
    public const string CargoContactPrototype = "LuaMSectorTrafficContactCargo";
    public const string DistressContactPrototype = "LuaMSectorTrafficContactDistress";
    public const string UnknownContactPrototype = "LuaMSectorTrafficContactUnknown";
    public const string CargoRecoveryPrototype = "LuaMSectorTrafficRecoveryCargo";
    public const string EvidenceRecoveryPrototype = "LuaMSectorTrafficRecoveryEvidence";
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
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromMinutes(30);

    private static readonly TrafficProfileDefinition[] TrafficProfiles =
    [
        new(
            LuaMSectorTrafficProfile.Civilian,
            ContactPrototype,
            "navigation-drift",
            "гражданская",
            "стабильный гражданский транспондер",
            "Просканировать маршрут, проверить навигационное расхождение и доставить отчёт.",
            null,
            "route-report"),
        new(
            LuaMSectorTrafficProfile.Cargo,
            CargoContactPrototype,
            "courier-handoff",
            "грузовая",
            "тяжёлый грузовой транспондер",
            "Просканировать передачу, забрать запечатанный груз и подать квитанцию.",
            CargoRecoveryPrototype,
            "sealed-cargo"),
        new(
            LuaMSectorTrafficProfile.Distress,
            DistressContactPrototype,
            "quiet-distress",
            "аварийная",
            "повторяющийся аварийный импульс",
            "Зафиксировать сигнал бедствия, забрать регистратор и оформить спасательный отчёт.",
            EvidenceRecoveryPrototype,
            "distress-recorder"),
        new(
            LuaMSectorTrafficProfile.Unknown,
            UnknownContactPrototype,
            "black-box-echo",
            "неизвестная",
            "неопознанное широкополосное эхо",
            "Просканировать эхо, восстановить запись и передать доказательство в LuaM.",
            EvidenceRecoveryPrototype,
            "signal-evidence"),
    ];

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly LuaMSectorDynamicEventSystem _dynamicEvents = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;

    private readonly List<Vector2> _playerAnchors = new();
    private readonly List<Vector2> _radarAnchors = new();
    private readonly HashSet<MapId> _activeMaps = new();
    private TimeSpan _nextMaintenance;
    private int _nextProfileIndex;
    private int _nextContactSerial = 1;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<LuaMSectorStoryResolvedEvent>(OnStoryResolved);
        SubscribeLocalEvent<LuaMSectorMemoryResetEvent>(OnMemoryReset);
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
            ClearAllTrafficEntities();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        ClearAllTrafficEntities();
        _nextMaintenance = TimeSpan.Zero;
        _nextProfileIndex = 0;
        _nextContactSerial = 1;
    }

    private void OnStoryResolved(LuaMSectorStoryResolvedEvent ev)
    {
        var changed = false;
        var query = EntityQueryEnumerator<LuaMSectorTrafficRecoveryComponent>();
        while (query.MoveNext(out var uid, out var recovery))
        {
            if (TerminatingOrDeleted(uid) ||
                !recovery.StoryId.Equals(ev.Story.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            QueueDel(uid);
            changed = true;
        }

        if (changed)
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
    }

    private void OnMemoryReset(LuaMSectorMemoryResetEvent ev)
    {
        ClearAllRecoveries();
    }

    /// <summary>
    /// Reconciles traffic for the active primary sector map around radar consoles
    /// that are close to non-ghost players.
    /// </summary>
    public void MaintainTraffic()
    {
        CleanupExpiredRecoveries();
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
        var changed = false;

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
                changed = true;
                continue;
            }

            EnsureContactMetadata(contact);
            contacts.Add(uid);
        }

        if (desired == 0 || trafficAnchors.Count == 0 || mapId == MapId.Nullspace)
        {
            if (changed)
                RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
            return contacts;
        }

        var remainingSpawnBudget = Math.Max(0, spawnBudget);
        while (contacts.Count < desired && remainingSpawnBudget-- > 0)
        {
            var anchor = trafficAnchors[_random.Next(trafficAnchors.Count)];
            var contact = SpawnContact(new MapCoordinates(anchor, mapId));
            contacts.Add(contact);
            changed = true;
        }

        if (changed)
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());

        return contacts;
    }

    /// <summary>
    /// Builds the small terminal/PDA view from contacts that are actually inside
    /// at least one radar console's configured range.
    /// </summary>
    public LuaMSectorTrafficUiEntry[] BuildTrafficUiEntries(bool canIntercept, string blockReason)
    {
        var entries = new List<LuaMSectorTrafficUiEntry>(HardMaxContacts);
        var sectorMap = _ticker.DefaultMap;
        if (sectorMap == MapId.Nullspace)
            return [];

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMSectorTrafficContactComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var contact, out var xform))
        {
            if (TerminatingOrDeleted(uid) ||
                xform.MapID != sectorMap ||
                contact.ExpiresAt <= now)
            {
                continue;
            }

            EnsureContactMetadata(contact);
            var position = _transform.GetWorldPosition(xform);
            if (!TryGetClosestDetectingRadar(sectorMap, position, out var range))
                continue;

            var profile = GetProfileDefinition(contact.Profile);
            entries.Add(new LuaMSectorTrafficUiEntry
            {
                Contact = GetNetEntity(uid),
                ContactCode = contact.ContactCode,
                Profile = profile.DisplayName,
                Signature = profile.Signature,
                Objective = profile.Objective,
                TemplateId = profile.DynamicEventTemplateId,
                RangeMeters = (int) MathF.Round(range),
                SecondsRemaining = Math.Max(0, (int) Math.Ceiling((contact.ExpiresAt - now).TotalSeconds)),
                CanIntercept = canIntercept,
                BlockReason = canIntercept ? string.Empty : blockReason,
            });
        }

        entries.Sort((left, right) => string.Compare(left.ContactCode, right.ContactCode, StringComparison.Ordinal));
        return entries.ToArray();
    }

    /// <summary>
    /// Converts one still-detectable radar contact into the existing bounded
    /// dynamic-event/contract path. The optional gate bypasses are intentionally
    /// explicit and are used only by integration tests and admin diagnostics.
    /// </summary>
    public bool TryInterceptContact(
        EntityUid uid,
        EntityUid actor,
        out LuaMSectorStoryRecord? record,
        out string error,
        bool ignorePlayerGate = false,
        bool ignoreRadarGate = false)
    {
        record = null;
        error = string.Empty;

        if (!TryComp<LuaMSectorTrafficContactComponent>(uid, out var contact) ||
            !TryComp<TransformComponent>(uid, out var xform) ||
            TerminatingOrDeleted(uid))
        {
            error = "Радарный контакт уже потерян.";
            return false;
        }

        if (contact.ExpiresAt <= _timing.CurTime)
        {
            QueueDel(uid);
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
            error = "Радарный контакт уже вышел из окна перехвата.";
            return false;
        }

        var position = _transform.GetWorldPosition(xform);
        if (!ignoreRadarGate)
        {
            if (_ticker.RunLevel != GameRunLevel.InRound ||
                xform.MapID != _ticker.DefaultMap ||
                !TryComp<TransformComponent>(actor, out var actorXform) ||
                actorXform.MapID != xform.MapID ||
                !TryGetClosestDetectingRadar(xform.MapID, position, out _))
            {
                error = "Контакт нельзя подтвердить активным радаром сектора.";
                return false;
            }
        }

        EnsureContactMetadata(contact);
        var profile = GetProfileDefinition(contact.Profile);
        var markerCoordinates = new MapCoordinates(position, xform.MapID);
        if (!_dynamicEvents.TryGenerateDynamicEvent(
                Name(actor),
                out record,
                out error,
                templateId: profile.DynamicEventTemplateId,
                ignorePlayerGate: ignorePlayerGate,
                markerCoordinates: markerCoordinates,
                spawnDebrisSite: false,
                spawnSiteNote: false,
                allowDirectSubmission: false))
        {
            return false;
        }

        if (record != null)
            SpawnRecovery(profile, record, markerCoordinates);

        QueueDel(uid);
        RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
        return true;
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
        var profile = TrafficProfiles[_nextProfileIndex++ % TrafficProfiles.Length];
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

        var uid = Spawn(profile.ContactPrototype, spawnCoordinates);
        var contact = Comp<LuaMSectorTrafficContactComponent>(uid);
        var body = Comp<PhysicsComponent>(uid);
        contact.RouteVelocity = velocity;
        contact.Profile = profile.Profile;
        contact.DynamicEventTemplateId = profile.DynamicEventTemplateId;
        contact.ContactCode = $"TRF-{GetProfileCode(profile.Profile)}-{_nextContactSerial++:000}";
        contact.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(
            _random.NextFloat((float) MinimumLifetime.TotalSeconds, (float) MaximumLifetime.TotalSeconds));

        _physics.SetLinearVelocity(uid, velocity, body: body);
        _transform.SetLocalRotation(uid, velocity.ToWorldAngle());
        return uid;
    }

    private void SpawnRecovery(
        TrafficProfileDefinition profile,
        LuaMSectorStoryRecord record,
        MapCoordinates coordinates)
    {
        if (profile.RecoveryPrototype == null)
            return;

        var recoveryUid = Spawn(profile.RecoveryPrototype, coordinates);
        var recovery = EnsureComp<LuaMSectorTrafficRecoveryComponent>(recoveryUid);
        recovery.StoryId = record.Story.ToString();
        recovery.ExpiresAt = _timing.CurTime + RecoveryLifetime;

        var site = EnsureComp<LuaMDynamicEventSiteObjectComponent>(recoveryUid);
        site.Story = record.Story;
        site.TemplateId = profile.DynamicEventTemplateId;
        site.CreatedBy = "перехват секторного радара";
        site.MarkerLocation = FormatCoordinates(coordinates);
        site.SiteObjectKind = profile.RecoveryKind;
        site.DirectSubmissionAllowed = false;

        var evidence = EnsureComp<LuaMSectorEvidenceComponent>(recoveryUid);
        evidence.Story = record.Story;
        evidence.AcknowledgeHazard = true;
        evidence.ResolveStory = true;
        evidence.RequireSectorTerminal = true;
        evidence.Note = $"{profile.RecoveryKind} recovered from intercepted contact at {site.MarkerLocation}";

        _metaData.SetEntityName(recoveryUid, profile.Profile == LuaMSectorTrafficProfile.Cargo
            ? $"запечатанный груз: {record.Title}"
            : $"регистратор сигнала: {record.Title}");
        _metaData.SetEntityDescription(
            recoveryUid,
            "Заберите находку с точки перехвата и подайте её как доказательство LuaM либо оформите отчёт закрытия в секторном терминале.");
    }

    /// <summary>
    /// Returns whether an intercepted story still has a physical recovery in
    /// play. Used to prevent the generic printable closure report from bypassing
    /// the cargo/evidence delivery objective. Expiry or destruction intentionally
    /// restores the ordinary report fallback so a round cannot be soft-locked.
    /// </summary>
    public bool HasPendingRecovery(string storyId)
    {
        var query = EntityQueryEnumerator<LuaMSectorTrafficRecoveryComponent>();
        while (query.MoveNext(out var uid, out var recovery))
        {
            if (!TerminatingOrDeleted(uid) &&
                recovery.StoryId.Equals(storyId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureContactMetadata(LuaMSectorTrafficContactComponent contact)
    {
        var profile = GetProfileDefinition(contact.Profile);
        contact.DynamicEventTemplateId = profile.DynamicEventTemplateId;
        if (string.IsNullOrWhiteSpace(contact.ContactCode))
            contact.ContactCode = $"TRF-{GetProfileCode(profile.Profile)}-{_nextContactSerial++:000}";
    }

    private static TrafficProfileDefinition GetProfileDefinition(LuaMSectorTrafficProfile profile)
    {
        foreach (var definition in TrafficProfiles)
        {
            if (definition.Profile == profile)
                return definition;
        }

        return TrafficProfiles[0];
    }

    private static string GetProfileCode(LuaMSectorTrafficProfile profile)
    {
        return profile switch
        {
            LuaMSectorTrafficProfile.Civilian => "CIV",
            LuaMSectorTrafficProfile.Cargo => "CGO",
            LuaMSectorTrafficProfile.Distress => "SOS",
            LuaMSectorTrafficProfile.Unknown => "UNK",
            _ => "CIV",
        };
    }

    private bool TryGetClosestDetectingRadar(MapId mapId, Vector2 position, out float distance)
    {
        distance = float.MaxValue;
        var found = false;
        var query = EntityQueryEnumerator<RadarConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var radar, out var xform))
        {
            if (TerminatingOrDeleted(uid) || xform.MapID != mapId)
                continue;

            var candidate = Vector2.Distance(position, _transform.GetWorldPosition(xform));
            if (candidate > radar.MaxRange || candidate >= distance)
                continue;

            distance = candidate;
            found = true;
        }

        return found;
    }

    private void CleanupExpiredRecoveries()
    {
        var changed = false;
        var query = EntityQueryEnumerator<LuaMSectorTrafficRecoveryComponent>();
        while (query.MoveNext(out var uid, out var recovery))
        {
            if (TerminatingOrDeleted(uid) || recovery.ExpiresAt > _timing.CurTime)
                continue;

            QueueDel(uid);
            changed = true;
        }

        if (changed)
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
    }

    private static string FormatCoordinates(MapCoordinates coordinates)
    {
        return FormattableString.Invariant(
            $"GPS map {coordinates.MapId} x {coordinates.Position.X:0.0} y {coordinates.Position.Y:0.0}");
    }

    private void RemoveContactsOutsideActiveMaps()
    {
        var changed = false;
        var query = EntityQueryEnumerator<LuaMSectorTrafficContactComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!_activeMaps.Contains(xform.MapID) && !TerminatingOrDeleted(uid))
            {
                QueueDel(uid);
                changed = true;
            }
        }

        if (changed)
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
    }

    private void ClearAllContacts()
    {
        var changed = false;
        var query = EntityQueryEnumerator<LuaMSectorTrafficContactComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (!TerminatingOrDeleted(uid))
            {
                QueueDel(uid);
                changed = true;
            }
        }

        _playerAnchors.Clear();
        _radarAnchors.Clear();
        _activeMaps.Clear();

        if (changed)
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
    }

    private void ClearAllTrafficEntities()
    {
        ClearAllContacts();

        ClearAllRecoveries();
    }

    private void ClearAllRecoveries()
    {
        var changed = false;
        var query = EntityQueryEnumerator<LuaMSectorTrafficRecoveryComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            QueueDel(uid);
            changed = true;
        }

        if (changed)
            RaiseLocalEvent(new LuaMSectorTrafficChangedEvent());
    }

    private readonly record struct TrafficProfileDefinition(
        LuaMSectorTrafficProfile Profile,
        string ContactPrototype,
        string DynamicEventTemplateId,
        string DisplayName,
        string Signature,
        string Objective,
        string? RecoveryPrototype,
        string RecoveryKind);
}

public readonly record struct LuaMSectorTrafficChangedEvent;
