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
using Content.Shared.Whitelist;
using Content.Shared.Buckle.Components;
using Content.Shared.Buckle;
using Content.Server._NF.Mech.Equipment.Components;
using Content.Shared._NF.Cargo.Components;
using Content.Server.Actions;
using Content.Shared._NF.Mech.Equipment.Events;
using Content.Shared.Mind.Components;
using Content.Server.Ghost.Roles.Components;

namespace Content.Server._NF.Mech.Equipment.EntitySystems;

/// <summary>
/// Handles <see cref="MechForkComponent"/> and all related UI logic
/// </summary>
public sealed class MechForkSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly MechSystem _mech = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly InteractionSystem _interaction = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly ActionsSystem _action = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<MechForkComponent, MechEquipmentUiMessageRelayEvent>(OnGrabberMessage);
        SubscribeLocalEvent<MechForkComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<MechForkComponent, MechEquipmentUiStateReadyEvent>(OnUiStateReady);
        SubscribeLocalEvent<MechForkComponent, MechEquipmentRemovedEvent>(OnEquipmentRemoved);
        SubscribeLocalEvent<MechForkComponent, AttemptRemoveMechEquipmentEvent>(OnAttemptRemove);
        SubscribeLocalEvent<MechForkComponent, MechEquipmentEquippedAction>(OnEquipped);
        SubscribeLocalEvent<MechForkComponent, MechForkToggleActionEvent>(OnForkToggled);

        SubscribeLocalEvent<MechForkComponent, UserActivateInWorldEvent>(OnInteract);
        SubscribeLocalEvent<MechForkComponent, GrabberDoAfterEvent>(OnMechGrab);
        SubscribeLocalEvent<MechForkComponent, ForkInsertDoAfterEvent>(OnMechInsertIntoStorage);
        SubscribeLocalEvent<MechForkComponent, ForkRemoveDoAfterEvent>(OnMechRemoveFromStorage);
    }

    private void OnGrabberMessage(EntityUid uid, MechForkComponent component, MechEquipmentUiMessageRelayEvent args)
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
    public void RemoveItem(EntityUid uid, EntityUid mech, EntityUid toRemove, MechForkComponent? component = null)
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

    private void OnEquipmentRemoved(EntityUid uid, MechForkComponent component, ref MechEquipmentRemovedEvent args)
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

    private void OnAttemptRemove(EntityUid uid, MechForkComponent component, ref AttemptRemoveMechEquipmentEvent args)
    {
        args.Cancelled = component.ItemContainer.ContainedEntities.Any();
    }

    private void OnStartup(EntityUid uid, MechForkComponent component, ComponentStartup args)
    {
        component.ItemContainer = _container.EnsureContainer<Container>(uid, "item-container");
    }

    private void OnUiStateReady(EntityUid uid, MechForkComponent component, MechEquipmentUiStateReadyEvent args)
    {
        var state = new MechGrabberUiState
        {
            Contents = GetNetEntityList(component.ItemContainer.ContainedEntities.ToList()),
            MaxContents = component.MaxContents
        };
        args.States.Add(GetNetEntity(uid), state);
    }

    private void OnEquipped(EntityUid uid, MechForkComponent component, MechEquipmentEquippedAction args)
    {
        if (args.Handled)
            return;

        if (args.Pilot != null)
        {
            component.ToggleActionEntity = _action.AddAction(args.Pilot.Value, component.ToggleAction, uid);
            _action.SetToggled(component.ToggleActionEntity, component.Inserting);
        }
        args.Handled = true;
    }

    private void OnForkToggled(EntityUid uid, MechForkComponent component, MechForkToggleActionEvent args)
    {
        if (args.Handled)
            return;

        component.Inserting = !component.Inserting;
        _action.SetToggled(component.ToggleActionEntity, component.Inserting);
        args.Handled = true;
    }

    private void OnInteract(EntityUid uid, MechForkComponent component, UserActivateInWorldEvent args)
    {
        if (args.Handled)
            return;
        var target = args.Target;

        if (args.Target == args.User || component.DoAfter != null)
            return;

        if (!TryComp<MechComponent>(args.User, out var mech) ||
            mech.Broken ||
            mech.PilotSlot.ContainedEntity is not { } pilot ||
            pilot == target)
            return;

        if (mech.Energy + component.GrabEnergyDelta < 0)
            return;

        if (!_interaction.InRangeUnobstructed(args.User, target))
            return;

        // TODO: swap this out for a "forkable storage"
        if (TryComp<CrateStorageRackComponent>(target, out var rack))
        {
            if (!_container.TryGetContainer(target, rack.ContainerName, out var targetContainer))
                return;

            if (component.Inserting)
            {
                // Check if crate is full
                if (targetContainer.Count >= rack.MaxObjectsStored || component.ItemContainer.Count <= 0)
                    return;

                args.Handled = true;
                component.AudioStream = _audio.PlayPvs(component.GrabSound, uid)?.Entity;
                var insertDoAfterArgs = new DoAfterArgs(EntityManager, args.User, component.GrabDelay, new ForkInsertDoAfterEvent(), uid, target: target, used: uid)
                {
                    BreakOnMove = true
                };

                _doAfter.TryStartDoAfter(insertDoAfterArgs, out component.DoAfter);
                return;
            }
            else
            {
                // Check if crate is empty or
                if (targetContainer.Count <= 0 || component.ItemContainer.Count >= component.MaxContents)
                    return;

                args.Handled = true;
                component.AudioStream = _audio.PlayPvs(component.GrabSound, uid)?.Entity;
                var insertDoAfterArgs = new DoAfterArgs(EntityManager, args.User, component.GrabDelay, new ForkRemoveDoAfterEvent(), uid, target: target, used: uid)
                {
                    BreakOnMove = true
                };

                _doAfter.TryStartDoAfter(insertDoAfterArgs, out component.DoAfter);
                return;
            }
        }

        if (!CanGrab(args.User, target, component) ||
            !_container.CanInsert(target, component.ItemContainer))
            return;

        args.Handled = true;
        component.AudioStream = _audio.PlayPvs(component.GrabSound, uid)?.Entity;
        var doAfterArgs = new DoAfterArgs(EntityManager, args.User, component.GrabDelay, new GrabberDoAfterEvent(), uid, target: target, used: uid)
        {
            BreakOnMove = true
        };

        _doAfter.TryStartDoAfter(doAfterArgs, out component.DoAfter);
    }

    private void OnMechGrab(EntityUid uid, MechForkComponent component, DoAfterEvent args)
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
                Log.Error($"Failed to roll back fork insertion of {ToPrettyString(target)} into {ToPrettyString(uid)}.");
                return;
            }

            RestoreEvacuatedOccupants(target, unbuckled, removed);
            return;
        }

        _mech.UpdateUserInterface(mech);

        args.Handled = true;
    }

    private void OnMechInsertIntoStorage(EntityUid uid, MechForkComponent component, DoAfterEvent args)
    {
        component.DoAfter = null;

        if (args.Cancelled)
        {
            component.AudioStream = _audio.Stop(component.AudioStream);
            return;
        }

        if (args.Handled || args.Args.Target is not { } target)
            return;

        if (!TryComp<CrateStorageRackComponent>(target, out var rack))
            return;
        if (!_container.TryGetContainer(target, rack.ContainerName, out var rackContainer))
            return;
        var itemsToInsert = Math.Min(component.ItemContainer.Count, rack.MaxObjectsStored - rackContainer.Count);
        if (itemsToInsert <= 0)
            return;
        if (!TryComp<MechEquipmentComponent>(uid, out var equipmentComponent) || equipmentComponent.EquipmentOwner == null)
            return;

        var mech = equipmentComponent.EquipmentOwner.Value;
        if (!CanOperateFork(mech) ||
            !_interaction.InRangeUnobstructed(mech, target) ||
            !_mech.TryChangeEnergy(mech, component.GrabEnergyDelta))
            return;

        // Insert items until they won't fit - if something fails with one, proceed to the next item
        var moved = 0;
        var candidates = component.ItemContainer.ContainedEntities.Take(itemsToInsert).ToList();
        foreach (var item in candidates)
        {
            if (rackContainer.Count >= rack.MaxObjectsStored)
                break;

            if (_container.Insert(item, rackContainer))
                moved++;
        }

        if (moved == 0)
        {
            RefundGrabEnergy(mech, component);
            return;
        }

        _mech.UpdateUserInterface(mech);

        args.Handled = true;
    }

    private void OnMechRemoveFromStorage(EntityUid uid, MechForkComponent component, DoAfterEvent args)
    {
        component.DoAfter = null;

        if (args.Cancelled)
        {
            component.AudioStream = _audio.Stop(component.AudioStream);
            return;
        }

        if (args.Handled || args.Args.Target is not { } target)
            return;

        if (!TryComp<CrateStorageRackComponent>(target, out var rack))
            return;
        if (!_container.TryGetContainer(target, rack.ContainerName, out var rackContainer))
            return;
        var itemsToInsert = Math.Min(rackContainer.Count, component.MaxContents - component.ItemContainer.Count);
        if (itemsToInsert <= 0)
            return;
        if (!TryComp<MechEquipmentComponent>(uid, out var equipmentComponent) || equipmentComponent.EquipmentOwner == null)
            return;

        var mech = equipmentComponent.EquipmentOwner.Value;
        if (!CanOperateFork(mech) ||
            !_interaction.InRangeUnobstructed(mech, target) ||
            !_mech.TryChangeEnergy(mech, component.GrabEnergyDelta))
            return;

        // Insert items until they won't fit - if something fails with one, proceed to the next item
        var moved = 0;
        var candidates = rackContainer.ContainedEntities.Take(itemsToInsert).ToList();
        foreach (var item in candidates)
        {
            if (component.ItemContainer.Count >= component.MaxContents)
                break;

            if (_container.Insert(item, component.ItemContainer))
                moved++;
        }

        if (moved == 0)
        {
            RefundGrabEnergy(mech, component);
            return;
        }

        _mech.UpdateUserInterface(mech);

        args.Handled = true;
    }

    private bool CanGrab(EntityUid mech, EntityUid target, MechForkComponent component)
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
            _whitelist.IsWhitelistFail(component.Whitelist, target))
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
            Log.Error($"Failed to restore {ToPrettyString(removed[i].Entity)} to {ToPrettyString(target)} after a cancelled fork grab.");
        }

        for (var i = unbuckled.Count - 1; i >= 0; i--)
        {
            if (_buckle.TryBuckle(unbuckled[i], null, target, popup: false))
                continue;

            restored = false;
            Log.Error($"Failed to re-buckle {ToPrettyString(unbuckled[i])} to {ToPrettyString(target)} after a cancelled fork grab.");
        }

        return restored;
    }

    private bool CanOperateFork(EntityUid mech)
    {
        return TryComp<MechComponent>(mech, out var mechComponent) &&
               !mechComponent.Broken &&
               mechComponent.PilotSlot.ContainedEntity != null;
    }

    private void RefundGrabEnergy(EntityUid mech, MechForkComponent component)
    {
        if (component.GrabEnergyDelta != 0f)
            _mech.TryChangeEnergy(mech, -component.GrabEnergyDelta);
    }
}
