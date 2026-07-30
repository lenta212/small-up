using System.Numerics;
using Content.Server._Crescent.ShipShields;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._Mono.Radar;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Mono.Radar;

/// <summary>
/// Keeps a short, bounded history of impacts for ship consoles. The history is
/// indexed by the struck grid, so a radar never receives another ship's reports.
/// </summary>
public sealed partial class ShipCombatTelemetrySystem : EntitySystem
{
    private const int MaxReportsPerGrid = 8;
    private const int MaxTrackedGrids = 128;
    private const int MaxWeaponNameLength = 48;
    private static readonly TimeSpan ReportLifetime = TimeSpan.FromSeconds(8);
    private const float CleanupInterval = 5f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<EntityUid, List<ShipHitReportNetData>> _reports = new();
    private readonly Dictionary<EntityUid, HitscanContext> _hitscanContexts = new();
    private readonly List<EntityUid> _expiredGrids = new();
    private float _cleanupAccumulator;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShipWeaponProjectileComponent, ProjectileDamageDealtEvent>(OnProjectileDamageDealt);
        SubscribeLocalEvent<HitscanRadarSignatureComponent, HitscanDamageDealtEvent>(OnHitscanDamageDealt);
        SubscribeLocalEvent<HitscanRadarSignatureComponent, ComponentShutdown>(OnHitscanShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _cleanupAccumulator += frameTime;
        if (_cleanupAccumulator < CleanupInterval)
            return;

        _cleanupAccumulator = 0f;
        PruneAll(_timing.CurTime);
    }

    private void OnProjectileDamageDealt(
        Entity<ShipWeaponProjectileComponent> ent,
        ref ProjectileDamageDealtEvent args)
    {
        if (!TryComp<ProjectileComponent>(ent, out var projectile) ||
            !TryGetTargetGrid(args.Target, out var grid))
        {
            return;
        }

        var impact = _transform.ToMapCoordinates(args.Coordinates);
        if (impact.MapId == MapId.Nullspace)
            return;

        var result = args.TargetDeleted
            ? ShipHitResult.Destroyed
            : args.DamageDealt > 0f
                ? ShipHitResult.Damaged
                : ShipHitResult.Blocked;
        AddReport(
            grid,
            impact.Position,
            args.MapVelocity,
            GetProjectileWeaponName(ent, projectile),
            result,
            args.DamageDealt);
    }

    internal void RecordShieldDeflection(
        EntityUid emitter,
        ShipShieldsSystem.ShieldDeflectedEvent args)
    {
        if (Transform(emitter).GridUid is not { } grid ||
            !TryComp<TransformComponent>(args.Deflected, out var projectileXform))
        {
            return;
        }

        var impact = _transform.ToMapCoordinates(projectileXform.Coordinates);
        if (impact.MapId == MapId.Nullspace)
            return;

        var velocity = TryComp<PhysicsComponent>(args.Deflected, out var body)
            ? _physics.GetMapLinearVelocity(args.Deflected, body, projectileXform)
            : Vector2.Zero;
        AddReport(
            grid,
            impact.Position,
            velocity,
            GetProjectileWeaponName(args.Deflected, args.Projectile),
            ShipHitResult.Shielded,
            0f);
    }

    internal void RecordHitscanFired(
        Entity<HitscanRadarSignatureComponent> ent,
        ref HitscanRaycastFiredEvent args)
    {
        _hitscanContexts.Remove(ent);
        if (args.HitEntity is not { } target ||
            !float.IsFinite(args.DistanceTried) ||
            args.DistanceTried < 0f ||
            !IsFinite(args.ShotDirection) ||
            args.ShotDirection.LengthSquared() <= 1e-8f)
        {
            return;
        }

        var start = _transform.ToMapCoordinates(args.FromCoordinates);
        if (start.MapId == MapId.Nullspace)
            return;

        var direction = Vector2.Normalize(args.ShotDirection);
        var impact = start.Position + direction * args.DistanceTried;
        var context = new HitscanContext(args.Gun, target, impact, direction);

        if (TryComp<ShipShieldComponent>(target, out var shield) &&
            Exists(shield.Shielded))
        {
            AddReport(
                shield.Shielded,
                impact,
                direction,
                GetEntityName(args.Gun),
                ShipHitResult.Shielded,
                0f);
            return;
        }

        _hitscanContexts[ent] = context;
    }

    private void OnHitscanDamageDealt(
        Entity<HitscanRadarSignatureComponent> ent,
        ref HitscanDamageDealtEvent args)
    {
        if (!_hitscanContexts.Remove(ent, out var context) ||
            context.Target != args.Target ||
            !TryGetTargetGrid(args.Target, out var grid))
        {
            return;
        }

        var damage = args.DamageDealt.GetTotal().Float();
        var result = TerminatingOrDeleted(args.Target)
            ? ShipHitResult.Destroyed
            : damage > 0f
                ? ShipHitResult.Damaged
                : ShipHitResult.Blocked;
        AddReport(
            grid,
            context.ImpactPosition,
            context.Direction,
            GetEntityName(context.Gun),
            result,
            damage);
    }

    private void OnHitscanShutdown(
        Entity<HitscanRadarSignatureComponent> ent,
        ref ComponentShutdown args)
    {
        _hitscanContexts.Remove(ent);
    }

    internal void CollectReports(EntityUid grid, List<ShipHitReportNetData> destination)
    {
        if (!_reports.TryGetValue(grid, out var reports))
            return;

        PruneGrid(reports, _timing.CurTime);
        for (var i = reports.Count - 1; i >= 0; i--)
            destination.Add(reports[i]);
    }

    private void AddReport(
        EntityUid grid,
        Vector2 mapImpact,
        Vector2 incomingVelocity,
        string weaponName,
        ShipHitResult result,
        float damage)
    {
        if (!TryComp<MapGridComponent>(grid, out var gridComponent) ||
            !TryComp<TransformComponent>(grid, out var gridXform) ||
            !IsFinite(mapImpact))
        {
            return;
        }

        if (!_reports.TryGetValue(grid, out var reports))
        {
            if (_reports.Count >= MaxTrackedGrids)
                PruneAll(_timing.CurTime);
            if (_reports.Count >= MaxTrackedGrids)
                return;

            reports = new List<ShipHitReportNetData>(MaxReportsPerGrid);
            _reports.Add(grid, reports);
        }

        PruneGrid(reports, _timing.CurTime);
        if (reports.Count >= MaxReportsPerGrid)
            reports.RemoveAt(0);

        var (_, _, inverseMatrix) = _transform.GetWorldPositionRotationInvMatrix(gridXform);
        var localImpact = Vector2.Transform(mapImpact, inverseMatrix);
        var incomingDirection = IsFinite(incomingVelocity) &&
                                incomingVelocity.LengthSquared() > 1e-8f
            ? Vector2.Normalize(incomingVelocity)
            : Vector2.Zero;
        reports.Add(new ShipHitReportNetData(
            _timing.CurTime,
            mapImpact,
            incomingDirection,
            weaponName,
            RadarCombatTelemetryMath.GetHitSection(localImpact, gridComponent.LocalAABB),
            result,
            float.IsFinite(damage) ? MathF.Max(0f, damage) : 0f));
    }

    private bool TryGetTargetGrid(EntityUid target, out EntityUid grid)
    {
        if (HasComp<MapGridComponent>(target))
        {
            grid = target;
            return true;
        }

        if (TryComp<TransformComponent>(target, out var xform) && xform.GridUid is { } targetGrid)
        {
            grid = targetGrid;
            return true;
        }

        grid = default;
        return false;
    }

    private string GetProjectileWeaponName(EntityUid projectileUid, ProjectileComponent projectile)
    {
        if (projectile.Weapon is { } weapon && Exists(weapon))
            return GetEntityName(weapon);

        return GetEntityName(projectileUid);
    }

    private string GetEntityName(EntityUid uid)
    {
        if (!TryComp<MetaDataComponent>(uid, out var metadata))
            return Loc.GetString("shuttle-combat-weapon-unknown");

        var name = metadata.EntityName.Trim();
        if (name.Length == 0)
            return Loc.GetString("shuttle-combat-weapon-unknown");

        return name.Length <= MaxWeaponNameLength
            ? name
            : $"{name[..(MaxWeaponNameLength - 1)]}…";
    }

    private void PruneAll(TimeSpan now)
    {
        _expiredGrids.Clear();
        foreach (var (grid, reports) in _reports)
        {
            PruneExpired(reports, now);
            if (reports.Count == 0 || TerminatingOrDeleted(grid))
                _expiredGrids.Add(grid);
        }

        foreach (var grid in _expiredGrids)
            _reports.Remove(grid);
    }

    private static void PruneGrid(List<ShipHitReportNetData> reports, TimeSpan now)
    {
        PruneExpired(reports, now);
    }

    private static void PruneExpired(List<ShipHitReportNetData> reports, TimeSpan now)
    {
        for (var i = reports.Count - 1; i >= 0; i--)
        {
            if (now - reports[i].EventTime >= ReportLifetime)
                reports.RemoveAt(i);
        }
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private readonly record struct HitscanContext(
        EntityUid Gun,
        EntityUid Target,
        Vector2 ImpactPosition,
        Vector2 Direction);
}
