using Content.Server.Polymorph.Systems;
using Content.Server.Polymorph.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Polymorph;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Species;

public sealed class LuaMMiniSlimeSystem : EntitySystem
{
    [Dependency] private HungerSystem _hunger = default!;
    [Dependency] private PolymorphSystem _polymorph = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<LuaMMiniSlimeComponent, HungerComponent, PolymorphedEntityComponent>();
        while (query.MoveNext(out var uid, out var mini, out var hunger, out var polymorphed))
        {
            if (_timing.CurTime < mini.NextCheck)
                continue;

            mini.NextCheck = _timing.CurTime + TimeSpan.FromSeconds(1);
            if (_hunger.GetHunger(hunger) >= mini.GrowthHunger)
                _polymorph.Revert((uid, polymorphed));
        }
    }
}
