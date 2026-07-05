using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.Server.Chat.Managers;
using Content.Server.Popups;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Paper;
using Content.Shared.PDA;
using Content.Shared.Popups;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._LuaM.Donation;

public sealed class LuaMDonationShopSystem : EntitySystem
{
    public const string CurrencyCode = "LC";
    public const string UnitName = "month";
    private static readonly ResPath LedgerPath = new("/luam/donation-shop.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly DonationShopCatalogListing[] Catalog =
    [
        new(
            "supporter-badge",
            "comp-pda-ui-donation-shop-item-supporter-badge",
            "comp-pda-ui-donation-shop-item-supporter-badge-desc",
            500,
            false),
        new(
            "pda-gold-frame",
            "comp-pda-ui-donation-shop-item-pda-gold-frame",
            "comp-pda-ui-donation-shop-item-pda-gold-frame-desc",
            750,
            false),
        new(
            "sector-certificate",
            "comp-pda-ui-donation-shop-item-sector-certificate",
            "comp-pda-ui-donation-shop-item-sector-certificate-desc",
            250,
            true),
        new(
            "luam-announcement",
            "comp-pda-ui-donation-shop-item-luam-announcement",
            "comp-pda-ui-donation-shop-item-luam-announcement-desc",
            1000,
            true),
    ];

    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private IChatManager _chat = default!;

    public DonationShopPdaState GetPdaState(ICommonSession? session)
    {
        if (session == null)
            return DonationShopPdaState.Locked(BuildListings(null, false));

        var records = LoadRecords();
        if (!records.TryGetValue(session.UserId.ToString(), out var record))
            return DonationShopPdaState.Locked(BuildListings(null, false));

        var access = IsAccessActive(record);
        return new DonationShopPdaState(
            record.Balance,
            access,
            FormatAccessUntil(record),
            BuildListings(record, access));
    }

    public bool TryPurchase(EntityUid buyer, string listingId, out string status)
    {
        status = string.Empty;

        if (!_players.TryGetSessionByEntity(buyer, out var session))
        {
            status = Loc.GetString("comp-pda-ui-donation-shop-status-no-user");
            return false;
        }

        var listing = Catalog.FirstOrDefault(entry => entry.Id.Equals(listingId, StringComparison.OrdinalIgnoreCase));
        if (listing == null)
        {
            status = Loc.GetString("comp-pda-ui-donation-shop-status-unknown-item");
            return false;
        }

        var records = LoadRecords();
        if (!records.TryGetValue(session.UserId.ToString(), out var record) || !IsAccessActive(record))
        {
            status = Loc.GetString("comp-pda-ui-donation-shop-status-locked");
            return false;
        }

        if (!listing.Consumable && record.Purchases.ContainsKey(listing.Id))
        {
            status = Loc.GetString("comp-pda-ui-donation-shop-status-owned");
            return false;
        }

        if (record.Balance < listing.Price)
        {
            status = Loc.GetString("comp-pda-ui-donation-shop-status-insufficient",
                ("balance", record.Balance),
                ("price", listing.Price),
                ("currency", CurrencyCode));
            return false;
        }

        record.Balance -= listing.Price;
        record.LastUserName = session.Name;
        if (!record.Purchases.TryAdd(listing.Id, 1))
            record.Purchases[listing.Id]++;
        AddLedgerEntry(record, "purchase", -listing.Price, "pda", listing.Id);
        SaveRecords(records);

        ApplyPurchaseEffect(buyer, session, listing);
        status = Loc.GetString("comp-pda-ui-donation-shop-status-purchased",
            ("item", Loc.GetString(listing.NameLocId)),
            ("balance", record.Balance),
            ("currency", CurrencyCode));
        RaiseLocalEvent(new LuaMDonationShopAccountChangedEvent(session.UserId));
        return true;
    }

    public DonationShopPlayerRecord GrantAccessUnits(NetUserId userId, string userName, int units, string actor, string reason)
    {
        var records = LoadRecords();
        var record = GetOrCreateRecord(records, userId, userName);

        units = Math.Max(1, units);
        var now = DateTimeOffset.UtcNow;
        var baseTime = record.AccessUntil > now ? record.AccessUntil : now;
        record.Access = true;
        record.LastUserName = userName;
        record.AccessUntil = baseTime.AddMonths(units);
        AddLedgerEntry(record, "grant-access", units, actor, $"{reason}; units={units}; unit={UnitName}");
        SaveRecords(records);
        RaiseLocalEvent(new LuaMDonationShopAccountChangedEvent(userId));
        return record;
    }

    public DonationShopPlayerRecord GrantBalance(NetUserId userId, string userName, int amount, string actor, string reason)
    {
        var records = LoadRecords();
        var record = GetOrCreateRecord(records, userId, userName);

        record.LastUserName = userName;
        record.Balance = Math.Max(0, record.Balance + amount);
        AddLedgerEntry(record, "grant-balance", amount, actor, reason);
        SaveRecords(records);
        RaiseLocalEvent(new LuaMDonationShopAccountChangedEvent(userId));
        return record;
    }

    public DonationShopPlayerRecord SetBalance(NetUserId userId, string userName, int amount, string actor, string reason)
    {
        var records = LoadRecords();
        var record = GetOrCreateRecord(records, userId, userName);

        var delta = Math.Max(0, amount) - record.Balance;
        record.LastUserName = userName;
        record.Balance = Math.Max(0, amount);
        AddLedgerEntry(record, "set-balance", delta, actor, reason);
        SaveRecords(records);
        RaiseLocalEvent(new LuaMDonationShopAccountChangedEvent(userId));
        return record;
    }

    public DonationShopPlayerRecord SetAccess(NetUserId userId, string userName, bool access, string actor, string reason)
    {
        var records = LoadRecords();
        var record = GetOrCreateRecord(records, userId, userName);

        record.Access = access;
        if (access && record.AccessUntil <= DateTimeOffset.UtcNow)
            record.AccessUntil = DateTimeOffset.UtcNow.AddMonths(1);

        record.LastUserName = userName;
        AddLedgerEntry(record, access ? "access-enabled" : "access-disabled", 0, actor, reason);
        SaveRecords(records);
        RaiseLocalEvent(new LuaMDonationShopAccountChangedEvent(userId));
        return record;
    }

    public bool TryGetRecord(NetUserId userId, out DonationShopPlayerRecord record)
    {
        var records = LoadRecords();
        return records.TryGetValue(userId.ToString(), out record!);
    }

    private List<PdaDonationShopListing> BuildListings(DonationShopPlayerRecord? record, bool access)
    {
        var listings = new List<PdaDonationShopListing>(Catalog.Length);
        foreach (var listing in Catalog)
        {
            var owned = record?.Purchases.ContainsKey(listing.Id) == true && !listing.Consumable;
            var available = access && !owned && (record?.Balance ?? 0) >= listing.Price;
            listings.Add(new PdaDonationShopListing(
                listing.Id,
                listing.NameLocId,
                listing.DescriptionLocId,
                listing.Price,
                owned,
                available));
        }

        return listings;
    }

    private static bool IsAccessActive(DonationShopPlayerRecord record)
    {
        return record.Access && record.AccessUntil > DateTimeOffset.UtcNow;
    }

    private static string FormatAccessUntil(DonationShopPlayerRecord? record)
    {
        if (record == null || record.AccessUntil == default)
            return string.Empty;

        return record.AccessUntil.ToString("yyyy-MM-dd HH:mm 'UTC'");
    }

    private void ApplyPurchaseEffect(EntityUid buyer, ICommonSession session, DonationShopCatalogListing listing)
    {
        switch (listing.Id)
        {
            case "sector-certificate":
                PrintCertificate(buyer, session, listing);
                break;
            case "luam-announcement":
                _chat.DispatchServerAnnouncement(Loc.GetString(
                    "comp-pda-ui-donation-shop-announcement",
                    ("player", Name(buyer))));
                break;
        }
    }

    private void PrintCertificate(EntityUid buyer, ICommonSession session, DonationShopCatalogListing listing)
    {
        var paperUid = Spawn("Paper", Transform(buyer).Coordinates);
        if (TryComp<PaperComponent>(paperUid, out var paper))
        {
            _paper.SetContent((paperUid, paper), Loc.GetString(
                "comp-pda-ui-donation-shop-certificate-content",
                ("player", Name(buyer)),
                ("user", session.Name),
                ("item", Loc.GetString(listing.NameLocId)),
                ("date", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'"))));
        }

        if (!_hands.TryForcePickupAnyHand(buyer, paperUid, checkActionBlocker: false))
            _popup.PopupEntity(Loc.GetString("comp-pda-ui-donation-shop-certificate-nearby"), buyer, buyer, PopupType.Medium);
    }

    private DonationShopPlayerRecord GetOrCreateRecord(
        Dictionary<string, DonationShopPlayerRecord> records,
        NetUserId userId,
        string userName)
    {
        var key = userId.ToString();
        if (records.TryGetValue(key, out var record))
            return record;

        record = new DonationShopPlayerRecord
        {
            UserId = key,
            LastUserName = userName,
        };
        records[key] = record;
        return record;
    }

    private void AddLedgerEntry(DonationShopPlayerRecord record, string action, int amount, string actor, string reason)
    {
        record.Ledger.Add(new DonationShopLedgerEntry
        {
            Time = DateTimeOffset.UtcNow,
            Action = action,
            Amount = amount,
            Actor = actor,
            Reason = reason,
            BalanceAfter = record.Balance,
        });

        if (record.Ledger.Count > 80)
            record.Ledger.RemoveRange(0, record.Ledger.Count - 80);
    }

    private Dictionary<string, DonationShopPlayerRecord> LoadRecords()
    {
        _resources.UserData.CreateDir(LedgerPath.Directory);

        if (!_resources.UserData.TryReadAllText(LedgerPath, out var json))
            return new Dictionary<string, DonationShopPlayerRecord>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var records = JsonSerializer.Deserialize<Dictionary<string, DonationShopPlayerRecord>>(json) ??
                          new Dictionary<string, DonationShopPlayerRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, record) in records)
            {
                if (string.IsNullOrWhiteSpace(record.UserId))
                    record.UserId = key;

                record.LastUserName ??= string.Empty;
                record.Purchases = record.Purchases.Count == 0
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(record.Purchases, StringComparer.OrdinalIgnoreCase);
                record.Ledger ??= new List<DonationShopLedgerEntry>();
            }

            return new Dictionary<string, DonationShopPlayerRecord>(records, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, DonationShopPlayerRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveRecords(Dictionary<string, DonationShopPlayerRecord> records)
    {
        _resources.UserData.CreateDir(LedgerPath.Directory);
        _resources.UserData.WriteAllText(LedgerPath, JsonSerializer.Serialize(records, JsonOptions));
    }

    private sealed record DonationShopCatalogListing(
        string Id,
        string NameLocId,
        string DescriptionLocId,
        int Price,
        bool Consumable);
}

public sealed record DonationShopPdaState(
    int Balance,
    bool Access,
    string AccessUntil,
    List<PdaDonationShopListing> Listings)
{
    public static DonationShopPdaState Locked(List<PdaDonationShopListing> listings)
    {
        return new DonationShopPdaState(0, false, string.Empty, listings);
    }
}

public sealed class DonationShopPlayerRecord
{
    public string UserId { get; set; } = string.Empty;
    public string LastUserName { get; set; } = string.Empty;
    public bool Access { get; set; }
    public DateTimeOffset AccessUntil { get; set; }
    public int Balance { get; set; }
    public Dictionary<string, int> Purchases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DonationShopLedgerEntry> Ledger { get; set; } = new();
}

public sealed class DonationShopLedgerEntry
{
    public DateTimeOffset Time { get; set; }
    public string Action { get; set; } = string.Empty;
    public int Amount { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int BalanceAfter { get; set; }
}

public readonly record struct LuaMDonationShopAccountChangedEvent(NetUserId UserId);
