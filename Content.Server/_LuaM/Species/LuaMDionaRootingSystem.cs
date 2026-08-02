using Content.Shared.Damage;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Popups;
using Content.Shared._LuaM.Species;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Species;

public sealed class LuaMDionaRootingSystem : EntitySystem
{
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private HungerSystem _hunger = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMDionaRootingComponent, LuaMDionaToggleRootingActionEvent>(OnToggle);
        SubscribeLocalEvent<LuaMDionaRootingComponent, RefreshMovementSpeedModifiersEvent>(OnMovement);
        SubscribeLocalEvent<LuaMDionaRootingComponent, DamageChangedEvent>(OnDamage);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<LuaMDionaRootingComponent, HungerComponent>();
        while (query.MoveNext(out var uid, out var rooting, out var hunger))
        {
            if (!rooting.Rooted || _timing.CurTime < rooting.NextHeal)
                continue;

            if (_mobState.IsIncapacitated(uid) || _hunger.GetHunger(hunger) <= rooting.MinimumNutrition)
            {
                SetRooted((uid, rooting), false);
                continue;
            }

            rooting.NextHeal = _timing.CurTime + rooting.Interval;
            _hunger.ModifyHunger(uid, -rooting.NutritionCost, hunger);
            _damageable.TryChangeDamage(uid, rooting.Healing, interruptsDoAfters: false);
        }
    }

    private void OnToggle(Entity<LuaMDionaRootingComponent> ent, ref LuaMDionaToggleRootingActionEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.Rooted)
        {
            SetRooted(ent, false);
            args.Handled = true;
            return;
        }

        if (_mobState.IsIncapacitated(ent.Owner) ||
            Transform(ent).GridUid == null ||
            !TryComp<HungerComponent>(ent, out var hunger) ||
            _hunger.GetHunger(hunger) <= ent.Comp.MinimumNutrition)
        {
            _popup.PopupEntity("Для укоренения нужна твёрдая поверхность и достаточный запас питания.", ent, ent);
            return;
        }

        SetRooted(ent, true);
        args.Handled = true;
    }

    private void OnMovement(Entity<LuaMDionaRootingComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.Rooted)
            args.ModifySpeed(0f);
    }

    private void OnDamage(Entity<LuaMDionaRootingComponent> ent, ref DamageChangedEvent args)
    {
        if (ent.Comp.Rooted && args.DamageIncreased)
            SetRooted(ent, false);
    }

    public void SetRooted(Entity<LuaMDionaRootingComponent> ent, bool rooted)
    {
        if (ent.Comp.Rooted == rooted)
            return;

        ent.Comp.Rooted = rooted;
        ent.Comp.NextHeal = _timing.CurTime + ent.Comp.Interval;
        Dirty(ent);
        _movement.RefreshMovementSpeedModifiers(ent);
        _popup.PopupEntity(rooted ? "Вы укореняетесь и начинаете восстанавливаться." : "Вы выдёргиваете корни.", ent, ent);
    }
}
