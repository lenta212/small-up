using System.Numerics;
using Robust.Shared.Timing;

namespace Content.Server._Mono.ShadowCatPortal;

public sealed class ShadowCatPortalSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShadowCatPortalComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<ShadowCatPortalComponent> ent, ref MapInitEvent args)
    {
        var catPrototype = ent.Comp.CatPrototype;
        var delay = ent.Comp.SpawnDelay;

        Timer.Spawn(delay, () =>
        {
            if (!Exists(ent.Owner))
                return;

            var coords = Transform(ent.Owner).Coordinates.Offset(new Vector2(0.75f, 0f));
            Spawn(catPrototype, coords);
        });
    }
}
