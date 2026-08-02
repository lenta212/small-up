using Content.Shared._LuaM.Stargate;
using Robust.Shared.Timing;
using System.Linq;

namespace Content.Server._LuaM.Stargate;

/// <summary>
/// Gives newly mapped address disks a useful, reachable local address.
/// </summary>
public sealed class LuaMStargateAddressDiskSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMStargateAddressDiskComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<LuaMStargateAddressDiskComponent> entity, ref MapInitEvent args)
    {
        if (entity.Comp.Addresses.Count > 0 || TryPopulate(entity))
            return;

        // Gate components from a loaded grid may finish their startup later in
        // the same map-init pass. Retry once after that pass has completed.
        Timer.Spawn(0, () =>
        {
            if (Exists(entity.Owner) &&
                !TerminatingOrDeleted(entity.Owner) &&
                TryComp<LuaMStargateAddressDiskComponent>(entity.Owner, out var disk) &&
                disk.Addresses.Count == 0)
            {
                TryPopulate((entity.Owner, disk));
            }
        });
    }

    private bool TryPopulate(Entity<LuaMStargateAddressDiskComponent> entity)
    {
        if (!TryFindNearestLocalAddress(entity.Owner, out var address))
            return false;

        entity.Comp.Addresses.Add(new List<byte>(address));
        Dirty(entity);
        return true;
    }

    private bool TryFindNearestLocalAddress(EntityUid owner, out byte[] address)
    {
        address = Array.Empty<byte>();
        var origin = Transform(owner);
        var originCoordinates = origin.Coordinates;
        var bestDistance = float.MaxValue;

        var query = EntityQueryEnumerator<LuaMStargateComponent, TransformComponent>();
        while (query.MoveNext(out _, out var gate, out var transform))
        {
            if (transform.MapID != origin.MapID ||
                gate.Address.Length == 0 ||
                gate.Address.Any(symbol => symbol is < 1 or > LuaMStargateGlyphs.Count) ||
                !originCoordinates.TryDistance(EntityManager, transform.Coordinates, out var distance) ||
                distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            address = gate.Address.ToArray();
        }

        return address.Length > 0;
    }
}
