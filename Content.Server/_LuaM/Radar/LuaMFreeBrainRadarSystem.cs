using Content.Server._Mono.Radar;
using Content.Shared._Mono.Radar;
using Robust.Shared.Containers;

namespace Content.Server._LuaM.Radar;

/// <summary>
/// Adds a short-range space-radar signature to marked brains while they are outside containers.
/// </summary>
public sealed class LuaMFreeBrainRadarSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _containers = default!;

    private const float RadarRange = 512f;

    private static readonly BlipConfig BrainBlipConfig = new()
    {
        Bounds = new Box2(-4.5f, -4.5f, 4.5f, 4.5f),
        Color = Color.FromHex("#E0FFFF"),
        Shape = RadarBlipShape.Star,
    };

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMFreeBrainRadarComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<LuaMFreeBrainRadarComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<LuaMFreeBrainRadarComponent, EntGotInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<LuaMFreeBrainRadarComponent, EntGotRemovedFromContainerMessage>(OnContainerChanged);
    }

    private void OnStartup(Entity<LuaMFreeBrainRadarComponent> ent, ref ComponentStartup args)
    {
        UpdateRadarBlip(ent.Owner);
    }

    private void OnShutdown(Entity<LuaMFreeBrainRadarComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent.Owner))
            return;

        RemoveOwnedRadarBlip(ent.Owner);
    }

    private void OnContainerChanged(
        Entity<LuaMFreeBrainRadarComponent> ent,
        ref EntGotInsertedIntoContainerMessage args)
    {
        UpdateRadarBlip(ent.Owner);
    }

    private void OnContainerChanged(
        Entity<LuaMFreeBrainRadarComponent> ent,
        ref EntGotRemovedFromContainerMessage args)
    {
        UpdateRadarBlip(ent.Owner);
    }

    private void UpdateRadarBlip(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid))
            return;

        if (_containers.IsEntityInContainer(uid))
        {
            RemoveOwnedRadarBlip(uid);
            return;
        }

        if (HasComp<RadarBlipComponent>(uid))
            return;

        var blip = AddComp<RadarBlipComponent>(uid);
        blip.Config = BrainBlipConfig;
        blip.RequireNoGrid = true;
        blip.VisibleFromOtherGrids = true;
        blip.MaxDistance = RadarRange;
        EnsureComp<LuaMFreeBrainRadarOwnedComponent>(uid);
    }

    private void RemoveOwnedRadarBlip(EntityUid uid)
    {
        if (!HasComp<LuaMFreeBrainRadarOwnedComponent>(uid))
            return;

        // If another system has replaced or materially changed our blip, relinquish ownership
        // without deleting that other system's signature.
        if (TryComp<RadarBlipComponent>(uid, out var blip) && IsOurRadarBlip(blip))
            RemComp<RadarBlipComponent>(uid);

        RemComp<LuaMFreeBrainRadarOwnedComponent>(uid);
    }

    private static bool IsOurRadarBlip(RadarBlipComponent blip)
    {
        return blip.Config == BrainBlipConfig &&
               blip.RequireNoGrid &&
               blip.VisibleFromOtherGrids &&
               blip.Enabled &&
               blip.MaxDistance.Equals(RadarRange) &&
               blip.GridConfig == null;
    }
}
