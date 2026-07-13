#nullable enable

using System.Linq;
using Content.Server.Database;
using Content.Shared.Preferences;
using NUnit.Framework;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMBankPersistenceTest
{
    [Test]
    public void UnknownCommitOutcomeUsesFailClosedError()
    {
        var result = new CharacterBankTransferResult(CharacterBankTransferStatus.UnknownOutcome);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("outcome-unknown"));
        });
    }

    [Test]
    public async Task AtomicTransferSucceedsAndConservesFunds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();

        await db.InitPrefsAsync(senderUserId, NewProfile("Successful Sender", 12_000), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Successful Recipient", 3_000), default);
        var senderProfileId = await GetProfileId(db, senderUserId);
        var recipientProfileId = await GetProfileId(db, recipientUserId);

        var result = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            4_500);

        var senderBalance = await GetBalance(db, senderUserId);
        var recipientBalance = await GetBalance(db, recipientUserId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CharacterBankTransferStatus.Success));
            Assert.That(result.SenderBalance, Is.EqualTo(7_500));
            Assert.That(result.RecipientBalance, Is.EqualTo(7_500));
            Assert.That(senderBalance, Is.EqualTo(7_500));
            Assert.That(recipientBalance, Is.EqualTo(7_500));
            Assert.That(senderBalance + recipientBalance, Is.EqualTo(15_000));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AtomicTransferSucceedsWhenRecipientRowComesFirst()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var recipientUserId = NewUserId();
        var senderUserId = NewUserId();

        await db.InitPrefsAsync(recipientUserId, NewProfile("Earlier Recipient", 2_000), default);
        await db.InitPrefsAsync(senderUserId, NewProfile("Later Sender", 8_000), default);
        var recipientProfileId = await GetProfileId(db, recipientUserId);
        var senderProfileId = await GetProfileId(db, senderUserId);
        Assert.That(recipientProfileId, Is.LessThan(senderProfileId));

        var result = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            3_000);

        var senderBalance = await GetBalance(db, senderUserId);
        var recipientBalance = await GetBalance(db, recipientUserId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CharacterBankTransferStatus.Success));
            Assert.That(senderBalance, Is.EqualTo(5_000));
            Assert.That(recipientBalance, Is.EqualTo(5_000));
            Assert.That(senderBalance + recipientBalance, Is.EqualTo(10_000));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InsufficientFundsLeavesBothAccountsUnchanged()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();

        await db.InitPrefsAsync(senderUserId, NewProfile("Insufficient Sender", 1_000), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Insufficient Recipient", 2_000), default);
        var senderProfileId = await GetProfileId(db, senderUserId);
        var recipientProfileId = await GetProfileId(db, recipientUserId);

        var result = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            1_001);

        var senderBalance = await GetBalance(db, senderUserId);
        var recipientBalance = await GetBalance(db, recipientUserId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CharacterBankTransferStatus.InsufficientFunds));
            Assert.That(senderBalance, Is.EqualTo(1_000));
            Assert.That(recipientBalance, Is.EqualTo(2_000));
            Assert.That(senderBalance + recipientBalance, Is.EqualTo(3_000));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecipientOverflowRollsBackEarlierSenderDebit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();
        const int senderInitialBalance = 1_000;
        const int recipientInitialBalance = int.MaxValue - 10;

        // The sender is inserted first deliberately: the implementation acquires
        // row locks by profile id, so this exercises the debit-first ordering.
        await db.InitPrefsAsync(senderUserId, NewProfile("Overflow Sender", senderInitialBalance), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Overflow Recipient", recipientInitialBalance), default);
        var senderProfileId = await GetProfileId(db, senderUserId);
        var recipientProfileId = await GetProfileId(db, recipientUserId);
        Assert.That(senderProfileId, Is.LessThan(recipientProfileId));

        var result = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            50);

        var senderBalance = await GetBalance(db, senderUserId);
        var recipientBalance = await GetBalance(db, recipientUserId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CharacterBankTransferStatus.RecipientOverflow));
            Assert.That(senderBalance, Is.EqualTo(senderInitialBalance));
            Assert.That(recipientBalance, Is.EqualTo(recipientInitialBalance));
            Assert.That((long) senderBalance + recipientBalance,
                Is.EqualTo((long) senderInitialBalance + recipientInitialBalance));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NonAuthoritativeProfileSaveCannotRestoreStaleBalance()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();

        await db.InitPrefsAsync(senderUserId, NewProfile("Profile Save Sender", 10_000), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Profile Save Recipient", 1_000), default);

        var stalePreferences = await db.GetPlayerPreferencesAsync(senderUserId, default);
        Assert.That(stalePreferences, Is.Not.Null);
        var staleProfile = (HumanoidCharacterProfile) stalePreferences!.Characters[0];

        var result = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            await GetProfileId(db, senderUserId),
            recipientUserId,
            await GetProfileId(db, recipientUserId),
            4_000);
        Assert.That(result.Success, Is.True);

        await db.SaveCharacterSlotAsync(
            senderUserId,
            staleProfile.WithName("Profile Save Renamed"),
            0,
            preserveBankBalance: true);

        var savedPreferences = await db.GetPlayerPreferencesAsync(senderUserId, default);
        Assert.That(savedPreferences, Is.Not.Null);
        var savedProfile = (HumanoidCharacterProfile) savedPreferences!.Characters[0];
        Assert.Multiple(() =>
        {
            Assert.That(savedProfile.Name, Is.EqualTo("Profile Save Renamed"));
            Assert.That(savedProfile.BankBalance, Is.EqualTo(6_000));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BatchedTransfersNeverOverdrawAndConserveFunds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();
        const int senderInitialBalance = 10_000;
        const int recipientInitialBalance = 1_000;
        const int transferAmount = 1_000;
        const int transferAttempts = 24;

        await db.InitPrefsAsync(senderUserId, NewProfile("Concurrent Sender", senderInitialBalance), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Concurrent Recipient", recipientInitialBalance), default);
        var senderProfileId = await GetProfileId(db, senderUserId);
        var recipientProfileId = await GetProfileId(db, recipientUserId);

        // PoolManager intentionally runs SQLite synchronously and rejects cross-thread
        // access. Queue the complete batch before awaiting it so this still exercises
        // the public API's Task.WhenAll call pattern without bypassing that test guard.
        var transfers = Enumerable.Range(0, transferAttempts)
            .Select(_ => db.TransferCharacterBankBalanceAsync(
                    senderUserId,
                    senderProfileId,
                    recipientUserId,
                    recipientProfileId,
                    transferAmount))
            .ToArray();

        var results = await Task.WhenAll(transfers);

        var successfulTransfers = results.Count(result => result.Success);
        var senderBalance = await GetBalance(db, senderUserId);
        var recipientBalance = await GetBalance(db, recipientUserId);
        Assert.Multiple(() =>
        {
            Assert.That(successfulTransfers, Is.EqualTo(senderInitialBalance / transferAmount));
            Assert.That(senderBalance, Is.GreaterThanOrEqualTo(0));
            Assert.That(senderBalance, Is.EqualTo(senderInitialBalance - successfulTransfers * transferAmount));
            Assert.That(recipientBalance, Is.EqualTo(recipientInitialBalance + successfulTransfers * transferAmount));
            Assert.That(senderBalance + recipientBalance, Is.EqualTo(senderInitialBalance + recipientInitialBalance));
        });

        await pair.CleanReturnAsync();
    }

    private static async Task<int> GetProfileId(IServerDbManager db, NetUserId userId)
    {
        var profileId = await db.GetCharacterIdAsync(userId, 0);
        Assert.That(profileId, Is.Not.Null);
        return profileId.GetValueOrDefault();
    }

    private static async Task<int> GetBalance(IServerDbManager db, NetUserId userId)
    {
        var preferences = await db.GetPlayerPreferencesAsync(userId, default);
        Assert.That(preferences, Is.Not.Null);
        Assert.That(preferences!.Characters.TryGetValue(0, out var profile), Is.True);
        Assert.That(profile, Is.TypeOf<HumanoidCharacterProfile>());
        return ((HumanoidCharacterProfile) profile!).BankBalance;
    }

    private static HumanoidCharacterProfile NewProfile(string name, int bankBalance)
    {
        return new HumanoidCharacterProfile
        {
            Name = name,
            FlavorText = string.Empty,
            Species = "Human",
            Age = 30,
            Appearance = new(
                "Afro",
                Color.Aqua,
                "Shaved",
                Color.Aquamarine,
                Color.Azure,
                Color.Beige,
                new()),
        }.WithBankBalance(bankBalance);
    }

    private static NetUserId NewUserId()
    {
        return new NetUserId(Guid.NewGuid());
    }
}
