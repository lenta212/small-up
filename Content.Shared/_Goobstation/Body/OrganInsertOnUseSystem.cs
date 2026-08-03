using Content.Shared.Body.Systems;
using Content.Shared.Interaction.Events;
using Robust.Shared.Containers;

namespace Content.Goobstation.Shared.Body;

public sealed class OrganInsertOnUseSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<OrganInsertOnUseComponent, UseInHandEvent>(OnUseInHand);
    }

    private void OnUseInHand(Entity<OrganInsertOnUseComponent> ent, ref UseInHandEvent args)
    {
        args.Handled = true;
        foreach (var (partUid, _) in _body.GetBodyChildrenOfType(args.User, ent.Comp.PartType))
        {
            if (!_body.CanInsertOrgan(partUid, ent.Comp.SlotId) ||
                !_container.TryGetContainer(partUid,
                    SharedBodySystem.GetOrganContainerId(ent.Comp.SlotId),
                    out var container) || container is not ContainerSlot slot)
                continue;

            if (slot.ContainedEntity != null && !_body.RemoveOrgan(slot.ContainedEntity.Value))
                continue;

            if (!_container.Insert(ent.Owner, slot, force: true))
                continue;

            return;
        }
    }
}
