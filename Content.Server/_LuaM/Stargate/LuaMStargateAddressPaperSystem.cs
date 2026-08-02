using Content.Shared._LuaM.Stargate;
using Content.Shared.Paper;
using Robust.Shared.Timing;
using System.Linq;

namespace Content.Server._LuaM.Stargate;

/// <summary>
/// Prints a mapped paper with the address of the nearest reachable gate on the
/// same map. It deliberately does not invent a procedural destination.
/// </summary>
public sealed class LuaMStargateAddressPaperSystem : EntitySystem
{
    [Dependency] private readonly PaperSystem _paper = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMStargateAddressPaperComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<LuaMStargateAddressPaperComponent> entity, ref MapInitEvent args)
    {
        if (TryPopulate(entity))
            return;

        Timer.Spawn(0, () =>
        {
            if (Exists(entity.Owner) &&
                !TerminatingOrDeleted(entity.Owner) &&
                TryComp<LuaMStargateAddressPaperComponent>(entity.Owner, out var marker))
            {
                TryPopulate((entity.Owner, marker));
            }
        });
    }

    private bool TryPopulate(Entity<LuaMStargateAddressPaperComponent> entity)
    {
        if (!TryComp<PaperComponent>(entity.Owner, out var paper))
            return true;

        if (entity.Comp.Address.Count == 0)
        {
            if (!TryFindNearestLocalAddress(entity.Owner, out var address))
                return false;

            entity.Comp.Address.AddRange(address);
        }

        var glyphs = string.Join(" ", entity.Comp.Address
            .Select(LuaMStargateGlyphs.GetChar)
            .Select(character => character.ToString()));
        var numbers = string.Join("-", entity.Comp.Address);
        var heading = Loc.GetString("stargate-address-paper-heading");
        _paper.SetContent(
            (entity.Owner, paper),
            $"{heading}\n\n[stargate size=20]{glyphs}[/stargate]\n\n{numbers}");
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
