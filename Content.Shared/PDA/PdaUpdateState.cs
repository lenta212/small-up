using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared.PDA
{
    [Serializable, NetSerializable]
    public sealed class PdaUpdateState : CartridgeLoaderUiState
    {
        public bool FlashlightEnabled;
        public bool HasPen;
        public bool HasPai;
        public PdaIdInfoText PdaOwnerInfo;
        public string? StationName;
        public bool HasUplink;
        public bool CanPlayMusic;
        public string? Address;
        public int Balance; // Frontier
        public string? BankAccountId; // Frontier
        public string? BankTransferStatus; // Frontier
        public bool BankTransferRetryBlocked; // LuaM
        public bool BankTransferReady; // LuaM
        public PdaBankTransferConfirmation? BankTransferConfirmation; // LuaM
        public PdaBankTransferRecovery? BankTransferRecovery; // LuaM
        public int PayrollHourly; // LuaM
        public int PayrollNextSeconds; // LuaM
        public string? OwnedShipName; // Frontier
        public int DonationBalance; // LuaM
        public bool DonationShopAccess; // LuaM
        public string? DonationShopAccessUntil; // LuaM
        public string? DonationShopStatus; // LuaM
        public List<PdaDonationShopListing> DonationShopListings; // LuaM
        public bool HasGoldPdaFrame; // LuaM

        public PdaUpdateState(
            List<NetEntity> programs,
            NetEntity? activeUI,
            bool flashlightEnabled,
            bool hasPen,
            bool hasPai,
            PdaIdInfoText pdaOwnerInfo,
            int balance, // Frontier
            string? bankAccountId, // Frontier
            string? bankTransferStatus, // Frontier
            bool bankTransferRetryBlocked, // LuaM
            bool bankTransferReady, // LuaM
            PdaBankTransferConfirmation? bankTransferConfirmation, // LuaM
            PdaBankTransferRecovery? bankTransferRecovery, // LuaM
            int payrollHourly, // LuaM
            int payrollNextSeconds, // LuaM
            string? ownedShipName, // Frontier
            string? stationName,
            bool hasUplink = false,
            bool canPlayMusic = false,
            string? address = null,
            int donationBalance = 0,
            bool donationShopAccess = false,
            string? donationShopAccessUntil = null,
            string? donationShopStatus = null,
            List<PdaDonationShopListing>? donationShopListings = null,
            bool hasGoldPdaFrame = false)
            : base(programs, activeUI)
        {
            FlashlightEnabled = flashlightEnabled;
            HasPen = hasPen;
            HasPai = hasPai;
            PdaOwnerInfo = pdaOwnerInfo;
            HasUplink = hasUplink;
            CanPlayMusic = canPlayMusic;
            StationName = stationName;
            Address = address;
            Balance = balance; // Frontier
            BankAccountId = bankAccountId; // Frontier
            BankTransferStatus = bankTransferStatus; // Frontier
            BankTransferRetryBlocked = bankTransferRetryBlocked; // LuaM
            BankTransferReady = bankTransferReady; // LuaM
            BankTransferConfirmation = bankTransferConfirmation; // LuaM
            BankTransferRecovery = bankTransferRecovery; // LuaM
            PayrollHourly = payrollHourly; // LuaM
            PayrollNextSeconds = payrollNextSeconds; // LuaM
            OwnedShipName = ownedShipName; // Frontier
            DonationBalance = donationBalance; // LuaM
            DonationShopAccess = donationShopAccess; // LuaM
            DonationShopAccessUntil = donationShopAccessUntil; // LuaM
            DonationShopStatus = donationShopStatus; // LuaM
            DonationShopListings = donationShopListings ?? new List<PdaDonationShopListing>(); // LuaM
            HasGoldPdaFrame = hasGoldPdaFrame; // LuaM
        }
    }

    [Serializable, NetSerializable]
    public sealed class PdaBankTransferConfirmation
    {
        public Guid OperationId;
        public string RecipientName = string.Empty;
        public string RecipientBankId = string.Empty;
        public int Amount;

        public PdaBankTransferConfirmation()
        {
        }

        public PdaBankTransferConfirmation(Guid operationId, string recipientName, string recipientBankId, int amount)
        {
            OperationId = operationId;
            RecipientName = recipientName;
            RecipientBankId = recipientBankId;
            Amount = amount;
        }
    }

    [Serializable, NetSerializable]
    public sealed class PdaBankTransferRecovery
    {
        public Guid OperationId;
        public string RecipientName = string.Empty;
        public string RecipientBankId = string.Empty;
        public int Amount;
        public int SenderBalance;

        public PdaBankTransferRecovery()
        {
        }

        public PdaBankTransferRecovery(
            Guid operationId,
            string recipientName,
            string recipientBankId,
            int amount,
            int senderBalance)
        {
            OperationId = operationId;
            RecipientName = recipientName;
            RecipientBankId = recipientBankId;
            Amount = amount;
            SenderBalance = senderBalance;
        }
    }

    [Serializable, NetSerializable]
    public sealed class PdaDonationShopListing
    {
        public string Id = string.Empty;
        public string NameLocId = string.Empty;
        public string DescriptionLocId = string.Empty;
        public int Price;
        public bool Owned;
        public bool Available;

        public PdaDonationShopListing()
        {
        }

        public PdaDonationShopListing(
            string id,
            string nameLocId,
            string descriptionLocId,
            int price,
            bool owned,
            bool available)
        {
            Id = id;
            NameLocId = nameLocId;
            DescriptionLocId = descriptionLocId;
            Price = price;
            Owned = owned;
            Available = available;
        }
    }

    [Serializable, NetSerializable]
    public struct PdaIdInfoText
    {
        public string? ActualOwnerName;
        public string? IdOwner;
        public string? JobTitle;
        public string? CompanyName;
        public Color CompanyColor;
        public string? StationAlertLevel;
        public Color StationAlertColor;
    }
}
