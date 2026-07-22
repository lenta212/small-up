using System.Linq;
using Content.Server.Interaction;
using Content.Server.Mech.Equipment.Components;
using Content.Server.Mech.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Mech;
using Content.Shared.Mech.Components;
using Content.Shared.Mech.Equipment.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Wall;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Content.Shared.Whitelist; // Frontier
using Content.Shared.Buckle.Components; // Frontier
using Content.Shared.Buckle; // Frontier
using Content.Shared.Mind.Components;
using Content.Server.Ghost.Roles.Components;

namespace Content.Server.Mech.Equipment.EntitySystems;

/// <summary>
/// Handles <see cref="MechGrabberComponent"/> and all related UI logic
/// </summary>
public sealed partial class MechGrabberSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private MechSystem _mech = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private InteractionSystem _interaction = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!; // Frontier
    [Dependency] private SharedBuckleSystem _buckle = default!; // Frontier

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<MechGrabberComponent, MechEquipmentUiMessageRelayEvent>(OnGrabberMessage);
        SubscribeLocalEvent<MechGrabberComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<MechGrabberComponent, MechEquipmentUiStateReadyEvent>(OnUiStateReady);
        SubscribeLocalEvent<MechGrabberComponent, MechEquipmentRemovedEvent>(OnEquipmentRemoved);
        SubscribeLocalEvent<MechGrabberComponent, AttemptRemoveMechEquipmentEvent>(OnAttemptRemove);

        SubscribeLocalEvent<MechGrabberComponent, UserActivateInWorldEvent>(OnInteract);
        SubscribeLocalEvent<MechGrabberComponent, GrabberDoAfterEvent>(OnMechGrab);
    }

    private void OnGrabberMessage(EntityUid uid, MechGrabberComponent component, MechEquipmentUiMessageRelayEvent args)
    {
        if (args.Message is not MechGrabberEjectMessage msg)
            return;

        if (!TryComp<MechEquipmentComponent>(uid, out var equipmentComponent) ||
            equipmentComponent.EquipmentOwner == null)
            return;
        var mech = equipmentComponent.EquipmentOwner.Value;

        var targetCoords = new EntityCoordinates(mech, component.DepositOffset);
        if (!_interaction.InRangeUnobstructed(mech, targetCoords))
            return;

        var item = GetEntity(msg.Item);

        if (!component.ItemContainer.Contains(item))
            return;

        RemoveItem(uid, mech, item, component);
    }

    /// <summary>
    /// Removes an item from the grabber's container
    /// </summary>
    /// <param name="uid">The mech grabber</param>
    /// <param name="mech">The mech it belongs to</param>
    /// <param name="toRemove">The item being removed</param>
    /// <param name="component"></param>
    public void RemoveItem(EntityUid uid, EntityUid mech, EntityUid toRemove, MechGrabberComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!_container.Remove(toRemove, component.ItemContainer))
            return;

        var mechxform = Transform(mech);
        var xform = Transform(toRemove);
        _transform.AttachToGridOrMap(toRemove, xform);
        var (mechPos, mechRot) = _transform.GetWorldPositionRotation(mechxform);

        var offset = mechPos + mechRot.RotateVec(component.DepositOffset);
        _transform.SetWorldPositionRotation(toRemove, offset, Angle.Zero);
        _mech.UpdateUserInterface(mech);
    }

    private void OnEquipmentRemoved(EntityUid uid, MechGrabberComponent component, ref MechEquipmentRemovedEvent args)
    {
        if (!TryComp<MechEquipmentComponent>(uid, out var equipmentComponent) ||
            equipmentComponent.EquipmentOwner == null)
            return;
        var mech = equipmentComponent.EquipmentOwner.Value;

        var allItems = new List<EntityUid>(component.ItemContainer.ContainedEntities);
        foreach (var item in allItems)
        {
            RemoveItem(uid, mech, item, component);
        }
    }

    private void OnAttemptRemove(EntityUid uid, MechGrabberComponent component, ref AttemptRemoveMechEquipmentEvent args)
    {
        args.Cancelled = component.ItemContainer.ContainedEntities.Any();
    }

    private void OnStartup(EntityUid uid, MechGrabberComponent component, ComponentStartup args)
    {
        component.ItemContainer = _container.EnsureContainer<Container>(uid, "item-container");
    }

    private void OnUiStateReady(EntityUid uid, MechGrabberComponent component, MechEquipmentUiStateReadyEvent args)
    {
        var state = new MechGrabberUiState
        {
            Contents = GetNetEntityList(component.ItemContainer.ContainedEntities.ToList()),
            MaxContents = component.MaxContents
        };
        args.States.Add(GetNetEntity(uid), state);
    }

    private void OnInteract(EntityUid uid, MechGrabberComponent component, UserActivateInWorldEvent args)
    {
        if (args.Handled)
            return;
        var target = args.Target;

        if (args.Target == args.User || component.DoAfter != null)
            return;

        if (!CanGrab(args.User, target, component) ||
            !_container.CanInsert(target, component.ItemContainer))
            return;

        args.Handled = true;
        component.AudioStream = _audio.PlayPvs(component.GrabSound, uid)?.Entity;
        var doAfterArgs = new DoAfterArgs(EntityManager, args.User, component.GrabDelay, new GrabberDoAfterEvent(), uid, target: target, used: uid)
        {
            BreakOnMove = true,
            MultiplyDelay = false, // Goobstation
        };

        _doAfter.TryStartDoAfter(doAfterArgs, out component.DoAfter);
    }

    private void OnMechGrab(EntityUid uid, MechGrabberComponent component, DoAfterEvent args)
    {
        component.DoAfter = null;

        if (args.Cancelled)
        {
            component.AudioStream = _audio.Stop(component.AudioStream);
            return;
        }

        if (args.Handled || args.Args.Target is not { } target)
            return;

        if (!TryComp<MechEquipmentComponent>(uid, out var equipmentComponent) || equipmentComponent.EquipmentOwner == null)
            return;

        var mech = equipmentComponent.EquipmentOwner.Value;
        if (!CanGrab(mech, target, component) ||
            !_container.CanInsert(target, component.ItemContainer))
            return;

        var targetXform = Transform(target);
        var originalCoordinates = targetXform.Coordinates;
        var originalRotation = targetXform.LocalRotation;
        var evacuationCoordinates = Transform(mech).Coordinates;

        if (!TryEvacuateOccupants(target, evacuationCoordinates, out var unbuckled, out var removed))
            return;

        if (!_container.Insert(target, component.ItemContainer))
        {
            RestoreEvacuatedOccupants(target, unbuckled, removed);
            return;
        }

        if (!_mech.TryChangeEnergy(mech, component.GrabEnergyDelta))
        {
            if (!_container.Remove(target,
                    component.ItemContainer,
                    force: true,
                    destination: originalCoordinates,
                    localRotation: originalRotation))
            {
                Log.Error($"Failed to roll back grabber insertion of {ToPrettyString(target)} into {ToPrettyString(uid)}.");
                return;
            }

            RestoreEvacuatedOccupants(target, unbuckled, removed);
            return;
        }

        _mech.UpdateUserInterface(mech);

        args.Handled = true;
    }

    private bool CanGrab(EntityUid mech, EntityUid target, MechGrabberComponent component)
    {
        if (!TryComp<MechComponent>(mech, out var mechComponent) ||
            mechComponent.Broken ||
            mechComponent.PilotSlot.ContainedEntity is not { } pilot ||
            pilot == target ||
            target == mech ||
            TerminatingOrDeleted(target) ||
            component.ItemContainer.Count >= component.MaxContents ||
            mechComponent.Energy + component.GrabEnergyDelta < 0 ||
            !_interaction.InRangeUnobstructed(mech, target) ||
            Transform(target).Anchored ||
            (TryComp<PhysicsComponent>(target, out var physics) && physics.BodyType == BodyType.Static) ||
            HasComp<WallMountComponent>(target) ||
            HasComp<MobStateComponent>(target) ||
            _whitelist.IsBlacklistPass(component.Blacklist, target))
        {
            return false;
        }

        return true;
    }

    private bool TryEvacuateOccupants(
        EntityUid target,
        EntityCoordinates destination,
        out List<EntityUid> unbuckled,
        out List<(EntityUid Entity, BaseContainer Container)> removed)
    {
        unbuckled = new List<EntityUid>();
        removed = new List<(EntityUid Entity, BaseContainer Container)>();
        var riders = TryComp<StrapComponent>(target, out var strap)
            ? strap.BuckledEntities.ToArray()
            : Array.Empty<EntityUid>();
        var occupants = new List<(EntityUid Entity, BaseContainer Container)>();

        if (TryComp<ContainerManagerComponent>(target, out var containerManager))
        {
            foreach (var container in containerManager.Containers.Values)
            {
                foreach (var contained in container.ContainedEntities)
                {
                    if (HasComp<GhostRoleComponent>(contained) ||
                        TryComp<MindContainerComponent>(contained, out var mind) && mind.HasMind)
                    {
                        occupants.Add((contained, container));
                    }
                }
            }
        }

        foreach (var occupant in occupants)
        {
            if (!_container.CanRemove(occupant.Entity, occupant.Container))
                return false;
        }

        foreach (var rider in riders)
        {
            if (_buckle.TryUnbuckle(rider, null, popup: false))
            {
                unbuckled.Add(rider);
                continue;
            }

            RestoreEvacuatedOccupants(target, unbuckled, removed);
            return false;
        }

        foreach (var occupant in occupants)
        {
            if (_container.Remove(occupant.Entity, occupant.Container, destination: destination))
            {
                removed.Add(occupant);
                continue;
            }

            RestoreEvacuatedOccupants(target, unbuckled, removed);
            return false;
        }

        return true;
    }

    private bool RestoreEvacuatedOccupants(
        EntityUid target,
        List<EntityUid> unbuckled,
        List<(EntityUid Entity, BaseContainer Container)> removed)
    {
        var restored = true;
        for (var i = removed.Count - 1; i >= 0; i--)
        {
            if (_container.Insert(removed[i].Entity, removed[i].Container))
                continue;

            restored = false;
            Log.Error($"Failed to restore {ToPrettyString(removed[i].Entity)} to {ToPrettyString(target)} after a cancelled grab.");
        }

        for (var i = unbuckled.Count - 1; i >= 0; i--)
        {
            if (_buckle.TryBuckle(unbuckled[i], null, target, popup: false))
                continue;

            restored = false;
            Log.Error($"Failed to re-buckle {ToPrettyString(unbuckled[i])} to {ToPrettyString(target)} after a cancelled grab.");
        }

        return restored;
    }
}
