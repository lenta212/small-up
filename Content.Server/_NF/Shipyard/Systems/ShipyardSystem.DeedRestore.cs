using Content.Server._LuaM.ShipPersistence;
using Content.Server._NF.Shipyard.Components;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.GameTicking;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    /// <summary>
    /// Re-issues the shipyard deed onto the player's new ID card after a
    /// respawn. Ghostrespawn deletes the previous body together with its card,
    /// which would otherwise silently strip the account's shuttle ownership.
    /// Only live persistent ships owned by the account are rebound; stored
    /// ships get their deed written again by the normal shipyard call flow.
    /// </summary>
    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_enabled ||
            !Exists(args.Mob) ||
            !_player.TryGetSessionById(args.Player.UserId, out var session) ||
            !_idSystem.TryFindIdCard(args.Mob, out var idCard))
        {
            return;
        }

        var userId = args.Player.UserId;
        var restoredAny = false;

        var query = EntityQueryEnumerator<ShipOwnershipComponent, LuaMShipIdentityComponent>();
        while (query.MoveNext(out var gridUid, out var ownership, out var identity))
        {
            if (ownership.OwnerUserId != userId ||
                identity.ShipId == Guid.Empty ||
                Deleted(gridUid))
            {
                continue;
            }

            // A card holds at most one deed; never clobber an existing one.
            if (HasComp<ShuttleDeedComponent>(idCard))
                break;

            var deed = AddComp<ShuttleDeedComponent>(idCard);
            if (TryComp<ShuttleDeedComponent>(gridUid, out var gridDeed))
            {
                deed.ShuttleName = gridDeed.ShuttleName;
                deed.ShuttleNameSuffix = gridDeed.ShuttleNameSuffix;
                deed.ShuttleOwner = gridDeed.ShuttleOwner;
                deed.PurchasedWithVoucher = gridDeed.PurchasedWithVoucher;
                deed.PurchaseVoucherUid = gridDeed.PurchaseVoucherUid;
            }

            if (string.IsNullOrWhiteSpace(deed.ShuttleOwner))
                deed.ShuttleOwner = session.Name;

            deed.ShuttleUid = gridUid;
            deed.PersistentShipId = identity.ShipId.ToString("D");
            deed.DeedHolder = idCard;
            Dirty(idCard, deed);

            GrantRestoredShipAccess(idCard, gridUid);
            restoredAny = true;
            break;
        }

        if (restoredAny)
        {
            _sawmill.Info(
                $"Re-issued ship deed for {session.Name} ({userId}) onto new body {ToPrettyString(args.Mob)}.");
        }
    }

    private void GrantRestoredShipAccess(EntityUid idCard, EntityUid gridUid)
    {
        TryComp<PersistentShipyardAccessComponent>(gridUid, out var persistentAccess);
        ShipyardConsoleUiKey? legacyShipyardGroup = null;
        if (TryComp<VesselComponent>(gridUid, out var vessel) &&
            _prototypeManager.TryIndex(vessel.VesselId, out var vesselPrototype))
        {
            legacyShipyardGroup = vesselPrototype.Group;
        }

        GrantShipAccessLevels(
            idCard,
            ResolvePersistentShipAccessLevels(persistentAccess?.GrantedLevels, legacyShipyardGroup));
    }
}
