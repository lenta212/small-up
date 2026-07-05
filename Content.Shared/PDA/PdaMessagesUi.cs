using Robust.Shared.Serialization;

namespace Content.Shared.PDA;

[Serializable, NetSerializable]
public sealed class PdaToggleFlashlightMessage : BoundUserInterfaceMessage
{
    public PdaToggleFlashlightMessage() { }
}

[Serializable, NetSerializable]
public sealed class PdaShowRingtoneMessage : BoundUserInterfaceMessage
{
    public PdaShowRingtoneMessage() { }
}

[Serializable, NetSerializable]
public sealed class PdaShowUplinkMessage : BoundUserInterfaceMessage
{
    public PdaShowUplinkMessage() { }
}

[Serializable, NetSerializable]
public sealed class PdaLockUplinkMessage : BoundUserInterfaceMessage
{
    public PdaLockUplinkMessage() { }
}

[Serializable, NetSerializable]
public sealed class PdaShowMusicMessage : BoundUserInterfaceMessage
{
    public PdaShowMusicMessage() { }
}

[Serializable, NetSerializable]
public sealed class PdaRequestUpdateInterfaceMessage : BoundUserInterfaceMessage
{
    public PdaRequestUpdateInterfaceMessage() { }
}

[Serializable, NetSerializable]
public sealed class PdaBankTransferMessage : BoundUserInterfaceMessage
{
    public string RecipientBankId { get; }
    public int Amount { get; }

    public PdaBankTransferMessage(string recipientBankId, int amount)
    {
        RecipientBankId = recipientBankId;
        Amount = amount;
    }
}

[Serializable, NetSerializable]
public sealed class PdaDonationShopPurchaseMessage : BoundUserInterfaceMessage
{
    public string ListingId { get; }

    public PdaDonationShopPurchaseMessage(string listingId)
    {
        ListingId = listingId;
    }
}
