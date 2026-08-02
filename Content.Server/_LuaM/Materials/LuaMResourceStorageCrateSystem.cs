using System.Linq;
using Content.Shared.ActionBlocker;
using Content.Shared.IdentityManagement;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Storage.Components;
using Content.Shared._LuaM.Materials;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Materials;

public sealed class LuaMResourceStorageCrateSystem : EntitySystem
{
    private const int MaxTransferPerRequest = 10_000_000;

    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private SharedMaterialStorageSystem _materials = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMResourceStorageCrateComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<LuaMResourceStorageCrateComponent, MaterialAmountChangedEvent>(OnMaterialsChanged);
        SubscribeLocalEvent<LuaMResourceStorageCrateComponent, StorageOpenAttemptEvent>(OnStorageOpenAttempt);
        Subs.BuiEvents<LuaMResourceStorageCrateComponent>(LuaMResourceStorageCrateUiKey.Key,
            subscriptions => subscriptions.Event<LuaMResourceStorageCrateTransferMessage>(OnTransfer));
    }

    private void OnStorageOpenAttempt(
        Entity<LuaMResourceStorageCrateComponent> ent,
        ref StorageOpenAttemptEvent args)
    {
        args.Cancelled = true;
    }

    private void OnUiOpened(Entity<LuaMResourceStorageCrateComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent, string.Empty);
    }

    private void OnMaterialsChanged(Entity<LuaMResourceStorageCrateComponent> ent, ref MaterialAmountChangedEvent args)
    {
        UpdateUi(ent, string.Empty);
    }

    private void OnTransfer(
        Entity<LuaMResourceStorageCrateComponent> ent,
        ref LuaMResourceStorageCrateTransferMessage args)
    {
        if (!_actionBlocker.CanInteract(args.Actor, ent.Owner))
        {
            return;
        }

        if (!TryTransfer(ent.Owner, args.Material, args.Amount, args.TransferAll, args.Direction,
                out var transfer, out var status))
        {
            UpdateUi(ent, status);
            return;
        }

        var material = _prototypes.Index<MaterialPrototype>(args.Material);
        UpdateUi(ent, Loc.GetString("luam-resource-crate-status-transferred",
            ("amount", transfer),
            ("material", Loc.GetString(material.Name))));
    }

    public bool TryTransfer(
        EntityUid crate,
        string materialId,
        int amount,
        bool transferAll,
        LuaMResourceTransferDirection direction,
        out int transferred,
        out string status)
    {
        transferred = 0;
        status = string.Empty;
        if (!TryComp<MaterialStorageComponent>(crate, out var crateStorage) ||
            !TryGetLinkedSilo(crate, out var silo, out var siloStorage))
        {
            status = Loc.GetString("luam-resource-crate-status-no-silo");
            return false;
        }

        if (!_prototypes.TryIndex<MaterialPrototype>(materialId, out var material))
        {
            status = Loc.GetString("luam-resource-crate-status-invalid-material");
            return false;
        }

        var source = direction == LuaMResourceTransferDirection.SiloToCrate
            ? new Entity<MaterialStorageComponent>(silo, siloStorage)
            : new Entity<MaterialStorageComponent>(crate, crateStorage);
        var destination = direction == LuaMResourceTransferDirection.SiloToCrate
            ? new Entity<MaterialStorageComponent>(crate, crateStorage)
            : new Entity<MaterialStorageComponent>(silo, siloStorage);

        var available = _materials.GetMaterialAmount(source.Owner, material, source.Comp, localOnly: true);
        var requested = transferAll ? available : Math.Clamp(amount, 0, MaxTransferPerRequest);
        var free = destination.Comp.StorageLimit is { } limit
            ? Math.Max(0, limit - _materials.GetTotalMaterialAmount(destination.Owner, destination.Comp, localOnly: true))
            : int.MaxValue;
        var transfer = Math.Min(requested, Math.Min(available, free));

        // MaterialStorage can only materialize complete stack entities when it is
        // deconstructed. Keep the transport crate sheet-aligned so every unit put
        // inside it can later be returned to the world without a fractional loss.
        var sheetVolume = Math.Max(1, _materials.GetSheetVolume(material));
        transfer -= transfer % sheetVolume;
        if (transfer <= 0)
        {
            status = Loc.GetString("luam-resource-crate-status-nothing-to-transfer");
            return false;
        }

        if (!_materials.TryChangeMaterialAmount(source.Owner, material.ID, -transfer, source.Comp, localOnly: true))
        {
            status = Loc.GetString("luam-resource-crate-status-source-changed");
            return false;
        }

        if (!_materials.TryChangeMaterialAmount(destination.Owner, material.ID, transfer, destination.Comp, localOnly: true))
        {
            // The destination was revalidated by the material system. Roll back
            // the already-debited source if another event changed it synchronously.
            _materials.TryChangeMaterialAmount(source.Owner, material.ID, transfer, source.Comp, localOnly: true);
            status = Loc.GetString("luam-resource-crate-status-destination-changed");
            return false;
        }

        transferred = transfer;
        return true;
    }

    private bool TryGetLinkedSilo(
        EntityUid crate,
        out EntityUid silo,
        out MaterialStorageComponent storage)
    {
        silo = default;
        storage = default!;
        if (!TryComp<OreSiloClientComponent>(crate, out var client) ||
            client.Silo is not { } linked ||
            !Exists(linked) ||
            !HasComp<OreSiloComponent>(linked) ||
            !TryComp<MaterialStorageComponent>(linked, out var linkedStorage))
        {
            return false;
        }

        silo = linked;
        storage = linkedStorage;
        return true;
    }

    private void UpdateUi(Entity<LuaMResourceStorageCrateComponent> ent, string status)
    {
        if (!_ui.IsUiOpen(ent.Owner, LuaMResourceStorageCrateUiKey.Key) ||
            !TryComp<MaterialStorageComponent>(ent, out var crateStorage))
        {
            return;
        }

        var linked = TryGetLinkedSilo(ent.Owner, out var silo, out var siloStorage);
        var crateMaterials = _materials.GetStoredMaterials((ent.Owner, crateStorage), localOnly: true);
        var siloMaterials = linked
            ? _materials.GetStoredMaterials((silo, siloStorage), localOnly: true)
            : new Dictionary<ProtoId<MaterialPrototype>, int>();
        var ids = crateMaterials.Keys.Concat(siloMaterials.Keys).Distinct().OrderBy(id => id.Id);
        var rows = new List<LuaMResourceStorageCrateMaterial>();

        foreach (var id in ids)
        {
            if (!_prototypes.TryIndex(id, out MaterialPrototype? material) || material == null)
                continue;

            rows.Add(new LuaMResourceStorageCrateMaterial(
                id.Id,
                Loc.GetString(material.Name),
                crateMaterials.GetValueOrDefault(id),
                siloMaterials.GetValueOrDefault(id)));
        }

        _ui.SetUiState(ent.Owner, LuaMResourceStorageCrateUiKey.Key,
            new LuaMResourceStorageCrateUiState(
                linked,
                linked ? Identity.Name(silo, EntityManager) : string.Empty,
                crateMaterials.Values.Sum(),
                crateStorage.StorageLimit ?? int.MaxValue,
                rows,
                status));
    }
}
