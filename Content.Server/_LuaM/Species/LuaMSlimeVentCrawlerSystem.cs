using System.Linq;
using Content.Server.Atmos.Piping.Unary.Components;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared._LuaM.Species;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Species;

public sealed class LuaMSlimeVentCrawlerSystem : EntitySystem
{
    private const float EntryRange = 1.6f;

    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NodeContainerSystem _nodes = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMSlimeVentCrawlerComponent, LuaMSlimeVentCrawlActionEvent>(OnVentCrawl);
    }

    private void OnVentCrawl(Entity<LuaMSlimeVentCrawlerComponent> ent, ref LuaMSlimeVentCrawlActionEvent args)
    {
        if (args.Handled || !TryVentCrawl(ent.Owner, args.Target))
            return;

        args.Handled = true;
    }

    public bool TryVentCrawl(EntityUid user, EntityUid exit)
    {
        if (!HasComp<LuaMSlimeVentCrawlerComponent>(user) ||
            !TryComp<GasVentPumpComponent>(exit, out var exitVent) ||
            _mobState.IsIncapacitated(user) || IsEncumbered(user) ||
            !_nodes.TryGetNode(exit, exitVent.Inlet, out PipeNode? exitNode) ||
            exitNode.NodeGroup == null)
        {
            return false;
        }

        var userPos = _transform.GetMapCoordinates(user);
        EntityUid? entry = null;
        var bestDistance = EntryRange * EntryRange;
        var query = EntityQueryEnumerator<GasVentPumpComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var vent, out var xform))
        {
            if (uid == exit || xform.MapID != userPos.MapId ||
                !_nodes.TryGetNode(uid, vent.Inlet, out PipeNode? node) ||
                !ReferenceEquals(node.NodeGroup, exitNode.NodeGroup))
            {
                continue;
            }

            var distance = (_transform.GetWorldPosition(xform) - userPos.Position).LengthSquared();
            if (distance > bestDistance)
                continue;

            bestDistance = distance;
            entry = uid;
        }

        if (entry == null)
            return false;

        var destination = Transform(exit).Coordinates;
        _transform.SetCoordinates(user, Transform(user), destination);
        return true;
    }

    private bool IsEncumbered(EntityUid uid)
    {
        if (_hands.EnumerateHeld(uid).Any())
            return true;

        if (!_inventory.TryGetContainerSlotEnumerator(uid, out var slots))
            return false;

        return slots.NextItem(out _);
    }
}
