using Content.Shared._LuaM.Stargate;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using System.Linq;

namespace Content.Server._LuaM.Stargate;

/// <summary>
/// Server-authoritative two-disk address editor.
/// </summary>
public sealed class LuaMStargateAddressEditorSystem : EntitySystem
{
    private const string LeftSlot = "left_disk_slot";
    private const string RightSlot = "right_disk_slot";
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorInputMessage>(OnInput);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorClearMessage>(OnClear);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorSaveLeftMessage>(OnSaveLeft);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorSaveRightMessage>(OnSaveRight);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorDeleteLeftMessage>(OnDeleteLeft);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorDeleteRightMessage>(OnDeleteRight);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorMoveLeftToRightMessage>(OnMoveLeftToRight);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorMoveRightToLeftMessage>(OnMoveRightToLeft);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorCopyLeftToRightMessage>(OnCopyLeftToRight);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorCopyRightToLeftMessage>(OnCopyRightToLeft);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorCloneLeftToRightMessage>(OnCloneLeftToRight);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, LuaMStargateAddressEditorCloneRightToLeftMessage>(OnCloneRightToLeft);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, EntInsertedIntoContainerMessage>(OnDiskInserted);
        SubscribeLocalEvent<LuaMStargateAddressEditorComponent, EntRemovedFromContainerMessage>(OnDiskRemoved);
    }

    private void OnUiOpened(Entity<LuaMStargateAddressEditorComponent> entity, ref BoundUIOpenedEvent args)
    {
        UpdateUi(entity);
    }

    private void OnInput(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorInputMessage args)
    {
        if (args.Symbol is < 1 ||
            args.Symbol > entity.Comp.SymbolCount ||
            entity.Comp.CurrentInput.Count >= GetRequiredLength(
                entity.Comp.CurrentInput,
                entity.Comp.AddressLength,
                args.Symbol) ||
            entity.Comp.CurrentInput.Contains(args.Symbol))
        {
            return;
        }

        entity.Comp.CurrentInput.Add(args.Symbol);
        UpdateUi(entity);
    }

    private void OnClear(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorClearMessage args)
    {
        entity.Comp.CurrentInput.Clear();
        UpdateUi(entity);
    }

    private void OnSaveLeft(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorSaveLeftMessage args)
    {
        SaveInput(entity, LeftSlot);
    }

    private void OnSaveRight(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorSaveRightMessage args)
    {
        SaveInput(entity, RightSlot);
    }

    private void OnDeleteLeft(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorDeleteLeftMessage args)
    {
        DeleteAddress(entity, LeftSlot, args.Index);
    }

    private void OnDeleteRight(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorDeleteRightMessage args)
    {
        DeleteAddress(entity, RightSlot, args.Index);
    }

    private void OnMoveLeftToRight(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorMoveLeftToRightMessage args)
    {
        TransferAddress(entity, LeftSlot, RightSlot, args.Index, move: true);
    }

    private void OnMoveRightToLeft(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorMoveRightToLeftMessage args)
    {
        TransferAddress(entity, RightSlot, LeftSlot, args.Index, move: true);
    }

    private void OnCopyLeftToRight(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorCopyLeftToRightMessage args)
    {
        TransferAddress(entity, LeftSlot, RightSlot, args.Index, move: false);
    }

    private void OnCopyRightToLeft(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorCopyRightToLeftMessage args)
    {
        TransferAddress(entity, RightSlot, LeftSlot, args.Index, move: false);
    }

    private void OnCloneLeftToRight(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorCloneLeftToRightMessage args)
    {
        CloneAddresses(entity, LeftSlot, RightSlot);
    }

    private void OnCloneRightToLeft(Entity<LuaMStargateAddressEditorComponent> entity, ref LuaMStargateAddressEditorCloneRightToLeftMessage args)
    {
        CloneAddresses(entity, RightSlot, LeftSlot);
    }

    private void OnDiskInserted(Entity<LuaMStargateAddressEditorComponent> entity, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID is LeftSlot or RightSlot)
            UpdateUi(entity);
    }

    private void OnDiskRemoved(Entity<LuaMStargateAddressEditorComponent> entity, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID is LeftSlot or RightSlot)
            UpdateUi(entity);
    }

    private void SaveInput(Entity<LuaMStargateAddressEditorComponent> editor, string slotId)
    {
        if (!TryGetDisk(editor.Owner, slotId, out var diskUid, out var disk))
            return;

        var address = editor.Comp.CurrentInput.ToArray();
        if (!ValidateAddress(address, editor.Comp) ||
            disk.Addresses.Count >= LuaMStargateAddressDiskComponent.MaxAddresses ||
            AddressExists(disk, address))
        {
            return;
        }

        disk.Addresses.Add(new List<byte>(address));
        Dirty(diskUid, disk);
        UpdateUi(editor);
    }

    private void DeleteAddress(Entity<LuaMStargateAddressEditorComponent> editor, string slotId, int index)
    {
        if (!TryGetDisk(editor.Owner, slotId, out var diskUid, out var disk) ||
            index < 0 ||
            index >= disk.Addresses.Count)
        {
            return;
        }

        disk.Addresses.RemoveAt(index);
        Dirty(diskUid, disk);
        UpdateUi(editor);
    }

    private void TransferAddress(
        Entity<LuaMStargateAddressEditorComponent> editor,
        string sourceSlot,
        string destinationSlot,
        int index,
        bool move)
    {
        if (!TryGetDisk(editor.Owner, sourceSlot, out var sourceUid, out var source) ||
            !TryGetDisk(editor.Owner, destinationSlot, out var destinationUid, out var destination) ||
            index < 0 ||
            index >= source.Addresses.Count ||
            destination.Addresses.Count >= LuaMStargateAddressDiskComponent.MaxAddresses)
        {
            return;
        }

        var address = source.Addresses[index];
        if (!ValidateAddress(address, editor.Comp) || AddressExists(destination, address))
            return;

        destination.Addresses.Add(new List<byte>(address));
        Dirty(destinationUid, destination);

        if (move)
        {
            source.Addresses.RemoveAt(index);
            Dirty(sourceUid, source);
        }

        UpdateUi(editor);
    }

    private void CloneAddresses(
        Entity<LuaMStargateAddressEditorComponent> editor,
        string sourceSlot,
        string destinationSlot)
    {
        if (!TryGetDisk(editor.Owner, sourceSlot, out _, out var source) ||
            !TryGetDisk(editor.Owner, destinationSlot, out var destinationUid, out var destination))
        {
            return;
        }

        var changed = false;
        foreach (var address in source.Addresses)
        {
            if (destination.Addresses.Count >= LuaMStargateAddressDiskComponent.MaxAddresses)
                break;

            if (!ValidateAddress(address, editor.Comp) || AddressExists(destination, address))
                continue;

            destination.Addresses.Add(new List<byte>(address));
            changed = true;
        }

        if (!changed)
            return;

        Dirty(destinationUid, destination);
        UpdateUi(editor);
    }

    private bool TryGetDisk(
        EntityUid editor,
        string slotId,
        out EntityUid diskUid,
        out LuaMStargateAddressDiskComponent disk)
    {
        diskUid = EntityUid.Invalid;
        disk = null!;
        if (!_itemSlots.TryGetSlot(editor, slotId, out var slot) ||
            slot.Item is not { } item ||
            !TryComp<LuaMStargateAddressDiskComponent>(item, out var diskComponent))
        {
            return false;
        }

        diskUid = item;
        disk = diskComponent;
        return true;
    }

    private static bool ValidateAddress(IReadOnlyCollection<byte> address, LuaMStargateAddressEditorComponent editor)
    {
        var symbols = address.ToArray();
        var requiredLength = symbols.Length > 0 && symbols[0] == 1 ? 7 : editor.AddressLength;
        return symbols.Length == requiredLength &&
               symbols.All(symbol => symbol is >= 1 && symbol <= editor.SymbolCount) &&
               symbols.Distinct().Count() == symbols.Length;
    }

    private static int GetRequiredLength(
        IReadOnlyCollection<byte> input,
        int defaultLength,
        byte? nextSymbol = null)
    {
        if (input.Count > 0)
            return input.First() == 1 ? 7 : defaultLength;
        return nextSymbol == 1 ? 7 : defaultLength;
    }

    private static bool AddressExists(LuaMStargateAddressDiskComponent disk, IReadOnlyList<byte> address)
    {
        return disk.Addresses.Any(existing => existing.SequenceEqual(address));
    }

    public void UpdateUi(Entity<LuaMStargateAddressEditorComponent> editor)
    {
        byte[][]? leftAddresses = null;
        if (TryGetDisk(editor.Owner, LeftSlot, out _, out var left))
            leftAddresses = left.Addresses.Select(address => address.ToArray()).ToArray();

        byte[][]? rightAddresses = null;
        if (TryGetDisk(editor.Owner, RightSlot, out _, out var right))
            rightAddresses = right.Addresses.Select(address => address.ToArray()).ToArray();

        _ui.SetUiState(
            editor.Owner,
            LuaMStargateAddressEditorUiKey.Key,
            new LuaMStargateAddressEditorUiState(
                editor.Comp.CurrentInput.ToArray(),
                GetRequiredLength(editor.Comp.CurrentInput, editor.Comp.AddressLength),
                editor.Comp.SymbolCount,
                leftAddresses,
                rightAddresses));
    }
}
