using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Shared.Damage;
using Content.Shared.Item;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Localization;
using System.Text;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorEvidenceSystem : EntitySystem
{
    private const float BlackBoxSnapshotRadius = 8f;
    private const float SectorTerminalFilingRadius = 3f;

    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorEvidenceComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
    }

    private void OnGetVerbs(EntityUid uid, LuaMSectorEvidenceComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        var verb = new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = Loc.GetString("luam-sector-evidence-file-verb"),
            Priority = 2,
            Act = () => TryFileEvidence(uid, args.User, component),
        };

        args.Verbs.Add(verb);
    }

    public bool TryFileEvidence(EntityUid uid, EntityUid user, LuaMSectorEvidenceComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return false;

        if (component.RequireSectorTerminal && !IsNearSectorTerminal(user))
        {
            _popup.PopupEntity(
                Loc.GetString("luam-sector-evidence-requires-terminal"),
                uid,
                user);
            return false;
        }

        var actor = MetaData(user).EntityName;
        var note = string.IsNullOrWhiteSpace(component.Note)
            ? MetaData(uid).EntityName
            : component.Note;

        var changed = false;

        if (component.AcknowledgeHazard)
            changed |= _stories.TryAcknowledgeHazard(component.Story, actor, note, out _);

        if (component.ClaimInsurance)
            changed |= _stories.TryClaimInsurance(component.Story, actor, note, out _);

        if (component.RecoverBlackBox)
            changed |= _stories.TryRecoverBlackBox(component.Story, actor, note, BuildBlackBoxSourceSnapshot(uid), out _);

        if (component.RegisterCompany)
            changed |= _stories.TryRegisterCompanyRecord(component.Story, actor, note, out _);

        if (component.RegisterShip)
            changed |= _stories.TryRegisterShipRecord(component.Story, actor, note, out _);

        if (component.ClearRescueFollowUp)
            changed |= _stories.TryClearLatestRescueFollowUp(actor, note, out _);

        if (component.ResolveStory)
            changed |= _stories.TryResolveStory(component.Story, actor, note);

        _popup.PopupEntity(changed
            ? Loc.GetString("luam-sector-evidence-filed")
            : Loc.GetString("luam-sector-evidence-already-filed"),
            uid,
            user);

        return changed;
    }

    private bool IsNearSectorTerminal(EntityUid user)
    {
        if (!TryComp(user, out TransformComponent? userXform))
            return false;

        var userPosition = _transform.GetWorldPosition(userXform);
        var maximumDistanceSquared = SectorTerminalFilingRadius * SectorTerminalFilingRadius;
        var query = EntityQueryEnumerator<LuaMSectorLeadReportComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var terminalXform))
        {
            if (TerminatingOrDeleted(uid) || terminalXform.MapID != userXform.MapID)
                continue;

            if ((_transform.GetWorldPosition(terminalXform) - userPosition).LengthSquared() <= maximumDistanceSquared)
                return true;
        }

        return false;
    }

    private string BuildBlackBoxSourceSnapshot(EntityUid uid)
    {
        var xform = Transform(uid);
        var mapCoordinates = _transform.ToMapCoordinates(xform.Coordinates, logError: false);
        var location = FormatMapCoordinates(mapCoordinates);
        var grid = xform.GridUid == null || !EntityManager.EntityExists(xform.GridUid.Value)
            ? "grid unavailable"
            : $"grid {MetaData(xform.GridUid.Value).EntityName} ({xform.GridUid.Value})";
        var container = xform.ParentUid == EntityUid.Invalid ||
                        xform.ParentUid == xform.GridUid ||
                        xform.ParentUid == xform.MapUid ||
                        !EntityManager.EntityExists(xform.ParentUid)
            ? "no container"
            : $"parent {MetaData(xform.ParentUid).EntityName} ({xform.ParentUid})";

        var output = new StringBuilder();
        output.Append($"Source {MetaData(uid).EntityName} ({uid}); {location}; {grid}; {container}");

        AppendGridPhysicalSnapshot(output, xform);
        AppendNearbySnapshot(output, uid, mapCoordinates, xform.GridUid);

        return output.ToString();
    }

    private void AppendGridPhysicalSnapshot(StringBuilder output, TransformComponent xform)
    {
        if (xform.GridUid == null ||
            !EntityManager.EntityExists(xform.GridUid.Value))
        {
            output.Append("; grid physical unavailable");
            return;
        }

        if (TryComp<PhysicsComponent>(xform.GridUid.Value, out var physics))
        {
            output.Append(FormattableString.Invariant(
                $"; grid physics {physics.BodyType}, mass {physics.Mass:0.0}"));
            return;
        }

        output.Append("; grid physics unavailable");
    }

    private void AppendNearbySnapshot(
        StringBuilder output,
        EntityUid source,
        MapCoordinates coordinates,
        EntityUid? sourceGrid)
    {
        if (coordinates == MapCoordinates.Nullspace)
        {
            output.Append("; nearby unavailable");
            return;
        }

        var mobs = 0;
        var items = 0;
        var storage = 0;
        var damageables = 0;
        var damaged = 0;
        var powered = 0;
        var unpowered = 0;
        var physicsBodies = 0;
        var dynamicBodies = 0;
        var staticBodies = 0;
        var sameGrid = 0;
        var nearbyNames = new List<string>(4);

        foreach (var ent in _lookup.GetEntitiesInRange(coordinates, BlackBoxSnapshotRadius, LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Sundries))
        {
            if (ent == source || Terminating(ent))
                continue;

            if (TryComp<TransformComponent>(ent, out var entXform) &&
                sourceGrid != null &&
                entXform.GridUid == sourceGrid)
            {
                sameGrid++;
            }

            if (HasComp<MobStateComponent>(ent))
                mobs++;

            if (HasComp<ItemComponent>(ent))
                items++;

            if (HasComp<StorageComponent>(ent))
                storage++;

            if (TryComp<DamageableComponent>(ent, out var damageable))
            {
                damageables++;
                if (damageable.TotalDamage > 0)
                    damaged++;
            }

            if (TryComp<ApcPowerReceiverComponent>(ent, out var receiver))
            {
                if (!receiver.NeedsPower || receiver.Powered)
                    powered++;
                else
                    unpowered++;
            }

            if (TryComp<PhysicsComponent>(ent, out var physics))
            {
                physicsBodies++;
                if ((physics.BodyType & (BodyType.Dynamic | BodyType.KinematicController)) != 0)
                    dynamicBodies++;
                else if ((physics.BodyType & BodyType.Static) != 0)
                    staticBodies++;
            }

            if (nearbyNames.Count < 4)
                nearbyNames.Add(MetaData(ent).EntityName);
        }

        output.Append(FormattableString.Invariant(
            $"; nearby {BlackBoxSnapshotRadius:0.#}m: same-grid {sameGrid}, mobs {mobs}, items {items}, storage {storage}, damageable {damageables} ({damaged} damaged), power {powered} on/{unpowered} off, physics {physicsBodies} ({dynamicBodies} dynamic/{staticBodies} static)"));

        if (nearbyNames.Count > 0)
            output.Append($"; nearby names: {string.Join(", ", nearbyNames)}");
    }

    private static string FormatMapCoordinates(MapCoordinates coordinates)
    {
        if (coordinates == MapCoordinates.Nullspace)
            return "GPS unavailable";

        return FormattableString.Invariant(
            $"GPS map {coordinates.MapId} x {coordinates.Position.X:0.0} y {coordinates.Position.Y:0.0}");
    }
}
