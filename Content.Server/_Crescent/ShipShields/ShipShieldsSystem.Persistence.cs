using System.Linq;
using Content.Shared._Crescent.ShipShields;

namespace Content.Server._Crescent.ShipShields;

public sealed partial class ShipShieldsSystem
{
    /// <summary>
    /// Removes transient shield entities and runtime-only links copied by a persistent ship restore.
    /// The ordinary shield update will recreate the envelope once a restored emitter is powered.
    /// </summary>
    public void ReconcileRestoredShipShields(
        EntityUid grid,
        IReadOnlySet<EntityUid> restoredEntities)
    {
        if (!Exists(grid))
            return;

        // A shield envelope is derived runtime state. Its reciprocal entity references are not
        // serialized, so retaining a copied envelope leaves the grid marked as shielded while no
        // usable shield is linked to the emitter.
        var envelopes = restoredEntities
            .Where(uid => Exists(uid) && HasComp<ShipShieldComponent>(uid))
            .ToHashSet();
        var shieldQuery = EntityQueryEnumerator<ShipShieldComponent, TransformComponent>();
        while (shieldQuery.MoveNext(out var shieldUid, out var shield, out var xform))
        {
            if (xform.GridUid == grid || shield.Shielded == grid)
                envelopes.Add(shieldUid);
        }

        foreach (var envelope in envelopes)
            Del(envelope);

        foreach (var uid in restoredEntities)
        {
            if (!Exists(uid))
                continue;

            RemComp<ShipShieldedComponent>(uid);

            if (!TryComp<ShipShieldEmitterComponent>(uid, out var emitter))
                continue;

            emitter.Shield = null;
            emitter.Shielded = null;
        }
    }
}
