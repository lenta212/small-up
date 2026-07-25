using Content.Server.Emp; // Frontier: Upstream - #28984
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Gravity;

namespace Content.Server.Gravity;

public sealed partial class GravityGeneratorSystem : EntitySystem
{
    [Dependency] private GravitySystem _gravitySystem = default!;
    [Dependency] private SharedPointLightSystem _lights = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GravityGeneratorComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<GravityGeneratorComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<GravityGeneratorComponent, ChargedMachineActivatedEvent>(OnActivated);
        SubscribeLocalEvent<GravityGeneratorComponent, ChargedMachineDeactivatedEvent>(OnDeactivated);
        // SubscribeLocalEvent<GravityGeneratorComponent, EmpPulseEvent>(OnEmpPulse); // Frontier: Upstream - #28984
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<GravityGeneratorComponent, PowerChargeComponent>();
        while (query.MoveNext(out var uid, out var grav, out var charge))
        {
            if (!_lights.TryGetLight(uid, out var pointLight))
                continue;

            _lights.SetEnabled(uid, charge.Charge > 0, pointLight);
            _lights.SetRadius(uid, MathHelper.Lerp(grav.LightRadiusMin, grav.LightRadiusMax, charge.Charge),
                pointLight);
        }
    }

    private void OnComponentInit(Entity<GravityGeneratorComponent> ent, ref ComponentInit args)
    {
        // Snapshots preserve the map-initialized life stage, so MapInitEvent is not raised again on restore.
        // ComponentInit still runs after the saved component data and entity hierarchy have been restored.
        if (!TryComp<PowerChargeComponent>(ent, out var charge))
            return;

        ent.Comp.GravityActive = charge.Active;

        var xform = Transform(ent);
        if (!TryComp(xform.ParentUid, out GravityComponent? gravity))
            return;

        if (ent.Comp.GravityActive)
            _gravitySystem.EnableGravity(xform.ParentUid, gravity);
        else
            _gravitySystem.RefreshGravity(xform.ParentUid, gravity);
    }

    private void OnActivated(Entity<GravityGeneratorComponent> ent, ref ChargedMachineActivatedEvent args)
    {
        ent.Comp.GravityActive = true;

        var xform = Transform(ent);

        if (TryComp(xform.ParentUid, out GravityComponent? gravity))
        {
            _gravitySystem.EnableGravity(xform.ParentUid, gravity);
        }
    }

    private void OnDeactivated(Entity<GravityGeneratorComponent> ent, ref ChargedMachineDeactivatedEvent args)
    {
        ent.Comp.GravityActive = false;

        var xform = Transform(ent);

        if (TryComp(xform.ParentUid, out GravityComponent? gravity))
        {
            _gravitySystem.RefreshGravity(xform.ParentUid, gravity);
        }
    }

    private void OnParentChanged(EntityUid uid, GravityGeneratorComponent component, ref EntParentChangedMessage args)
    {
        if (component.GravityActive && TryComp(args.OldParent, out GravityComponent? gravity))
        {
            _gravitySystem.RefreshGravity(args.OldParent.Value, gravity);
        }
    }
}
