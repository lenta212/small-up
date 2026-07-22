#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Content.Server._Mono.MonoCoins;
using Content.Server.Database;
using Content.Server._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Preferences;
using NUnit.Framework;
using Robust.Server.Player;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Serilog.Events;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMBankPersistenceTest
{
    [Test]
    public async Task TeardownAfterDebitCommitSkipsFinalizerAndCompensatesExactly()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var playerManager = server.ResolveDependency<IPlayerManager>();
        var session = playerManager.GetSessionById(clientSession!.UserId);
        var entityManager = server.ResolveDependency<IEntityManager>();
        var bank = entityManager.System<BankSystem>();
        var map = await pair.CreateTestMap();
        var realDb = GetPrivateField<IServerDbManager>(bank, "_db");
        var proxy = DispatchProxy.Create<IServerDbManager, PausedBankWriteProxy>();
        var proxyState = (PausedBankWriteProxy) (object) proxy;
        proxyState.Inner = realDb;
        EntityUid actor = default;
        Task<bool>? withdrawal = null;

        async Task DrainWithdrawalAsync()
        {
            if (withdrawal == null)
                return;

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!withdrawal.IsCompleted && DateTime.UtcNow < deadline)
            {
                // The BankSystem continuation is bound to the server context,
                // while the proxy completes on a pool thread. Keep pumping both
                // the context and ticks until the real compensation CAS returns.
                await server.WaitIdleAsync();
                await pair.RunTicksSync(1);
                await Task.Yield();
            }
        }

        try
        {
            await server.WaitPost(() =>
            {
                actor = entityManager.SpawnEntity("MobHuman", map.GridCoords);
                playerManager.SetAttachedEntity(session, actor);
                entityManager.EnsureComponent<BankAccountComponent>(actor);
                bank.SyncBankBalance(actor);
            });
            await pair.RunTicksSync(2);

            var originalBalance = 0;
            await server.WaitAssertion(() =>
            {
                Assert.That(bank.TryGetBalance(actor, out originalBalance), Is.True);
                Assert.That(originalBalance, Is.GreaterThan(100));
            });
            var profileId = await realDb.GetCharacterIdAsync(session.UserId, 0);
            Assert.That(profileId, Is.Not.Null, "The teardown regression requires a durable profile identity.");
            Assert.That(
                await realDb.GetCharacterBankBalanceAsync(
                    session.UserId,
                    profileId.GetValueOrDefault(),
                    0),
                Is.EqualTo(originalBalance),
                "The runtime projection must start from the exact durable profile balance.");

            SetPrivateField(bank, "_db", proxy);
            var finalized = false;
            await server.WaitPost(() =>
            {
                withdrawal = bank.TryBankWithdrawAsync(
                    actor,
                    100,
                    () =>
                    {
                        finalized = true;
                        return true;
                    });
            });

            await proxyState.FirstWriteCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.WaitPost(() =>
            {
                // Reproduce the teardown window after the durable debit has been
                // accepted but before its continuation can invoke the world callback.
                playerManager.SetAttachedEntity(session, null, true);
                proxyState.ReleaseFirstWriteResult();
            });
            await proxyState.FirstWriteReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainWithdrawalAsync();

            Assert.That(await withdrawal!.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
            Assert.That(finalized, Is.False,
                "A finalizer must never materialize value for a detached session.");

            var writes = proxyState.GetWrites();
            var durableBalance = await realDb.GetCharacterBankBalanceAsync(
                session.UserId,
                profileId.GetValueOrDefault(),
                0);
            Assert.Multiple(() =>
            {
                Assert.That(writes, Has.Count.EqualTo(2));
                Assert.That(writes[0], Is.EqualTo((originalBalance, originalBalance - 100)));
                Assert.That(writes[1], Is.EqualTo((originalBalance - 100, originalBalance)),
                    "The committed debit must be compensated with an exact reverse CAS.");
                Assert.That(durableBalance, Is.EqualTo(originalBalance),
                    "The real debit and its exact compensation must conserve the durable profile balance " +
                    "even after the transient actor projection has been detached or removed.");
            });
        }
        finally
        {
            if (withdrawal is { IsCompleted: false })
            {
                proxyState.ReleaseFirstWriteResult();
                await DrainWithdrawalAsync();
            }

            SetPrivateField(bank, "_db", realDb);
            await server.WaitPost(() =>
            {
                if (session.AttachedEntity is { Valid: true })
                    playerManager.SetAttachedEntity(session, null, true);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task OfflineWriteExceptionUsesExactFreshReadAndBlocksUnknownOutcome()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var bank = pair.Server.ResolveDependency<IEntityManager>().System<BankSystem>();
        var realDb = GetPrivateField<IServerDbManager>(bank, "_db");
        var proxy = DispatchProxy.Create<IServerDbManager, BankWriteOutcomeProxy>();
        var proxyState = (BankWriteOutcomeProxy) (object) proxy;
        var rejectedUser = NewUserId();
        var committedUser = NewUserId();
        var unknownUser = NewUserId();
        var expectedErrorLogs = new Dictionary<string, int>();
        var expectedLogLock = new object();

        bool JudgeExpectedBankError(string sawmillName, LogEvent message)
        {
            if (sawmillName != "bank")
                return false;

            var rendered = message.RenderMessage();
            string? category = null;
            if (rendered.StartsWith(
                    $"Offline bank withdrawal did not commit for {rejectedUser}:0:",
                    StringComparison.Ordinal))
            {
                category = "confirmed-original";
            }
            else if (rendered.StartsWith(
                         $"Fresh balance 1050 matched neither original 1000 nor mutation 1100 for {unknownUser}:0",
                         StringComparison.Ordinal))
            {
                category = "ambiguous-fresh-read";
            }
            else if (rendered.StartsWith(
                         $"CRITICAL: bank profile {unknownUser}/103 (slot 0) blocked: offline deposit outcome is unknown:",
                         StringComparison.Ordinal))
            {
                category = "fail-closed-block";
            }

            if (category == null)
                return false;

            lock (expectedLogLock)
                expectedErrorLogs[category] = expectedErrorLogs.GetValueOrDefault(category) + 1;
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedBankError;
        SetPrivateField(bank, "_db", proxy);

        try
        {
            var rejectedProfile = NewProfile("Offline Confirmed Original", 1_000);
            var rejectedPrefs = PreferencesFor(rejectedProfile);
            proxyState.ObservedBalance = 1_000;
            Assert.That(await bank.TryBankWithdrawOffline(
                rejectedUser,
                rejectedPrefs,
                rejectedProfile,
                expectedProfileId: 101,
                amount: 100), Is.False);

            var committedProfile = NewProfile("Offline Confirmed Commit", 1_000);
            var committedPrefs = PreferencesFor(committedProfile);
            proxyState.ObservedBalance = 900;
            Assert.That(await bank.TryBankWithdrawOffline(
                committedUser,
                committedPrefs,
                committedProfile,
                expectedProfileId: 102,
                amount: 100), Is.True,
                "A fresh exact read of the mutated value must not report a committed debit as retryable.");

            var unknownProfile = NewProfile("Offline Unknown", 1_000);
            var unknownPrefs = PreferencesFor(unknownProfile);
            proxyState.ObservedBalance = 1_050;
            Assert.ThrowsAsync<BankMutationRollbackException>(async () =>
                await bank.TryBankDepositOffline(
                    unknownUser,
                    unknownPrefs,
                    unknownProfile,
                    expectedProfileId: 103,
                    amount: 100));
            Assert.That(await bank.TryBankDepositOffline(
                unknownUser,
                unknownPrefs,
                unknownProfile,
                expectedProfileId: 103,
                amount: 100), Is.False,
                "An ambiguous exact profile must remain blocked instead of accepting a retry.");

            lock (expectedLogLock)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(expectedErrorLogs.GetValueOrDefault("confirmed-original"), Is.EqualTo(1));
                    Assert.That(expectedErrorLogs.GetValueOrDefault("ambiguous-fresh-read"), Is.EqualTo(1));
                    Assert.That(expectedErrorLogs.GetValueOrDefault("fail-closed-block"), Is.EqualTo(1));
                });
            }
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedBankError;
            SetPrivateField(bank, "_db", realDb);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task MonoCoinsCasRejectsStaleDebitAndCompensation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var coins = pair.Server.ResolveDependency<MonoCoinsManager>();
        var userId = NewUserId();
        await db.InitPrefsAsync(userId, NewProfile("MonoCoins CAS", 1_000), default);

        var seed = await db.UpdateMonoCoinsBalanceAsync(userId, 0L, 400L);
        var stale = await db.UpdateMonoCoinsBalanceAsync(userId, 0L, 250L);
        var debit = await db.UpdateMonoCoinsBalanceAsync(userId, 400L, 300L);
        var interveningCredit = await db.UpdateMonoCoinsBalanceAsync(userId, 300L, 325L);
        var staleCompensation = await db.UpdateMonoCoinsBalanceAsync(userId, 300L, 400L);

        Assert.Multiple(() =>
        {
            Assert.That(seed.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.Success));
            Assert.That(stale.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.BalanceConflict));
            Assert.That(stale.CurrentBalance, Is.EqualTo(400L));
            Assert.That(debit.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.Success));
            Assert.That(interveningCredit.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.Success));
            Assert.That(staleCompensation.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.BalanceConflict));
            Assert.That(staleCompensation.CurrentBalance, Is.EqualTo(325L));
        });

        Assert.That(await coins.GetMonoCoinsBalanceAsync(userId), Is.EqualTo(325L));
        var managerCommit = await coins.UpdateMonoCoinsBalanceExactAsync(userId, 325L, 500L);
        var managerStale = await coins.UpdateMonoCoinsBalanceExactAsync(userId, 325L, 100L);
        Assert.Multiple(() =>
        {
            Assert.That(managerCommit.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.Success));
            Assert.That(managerStale.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.BalanceConflict));
            Assert.That(managerStale.CurrentBalance, Is.EqualTo(500L));
            Assert.That(coins.GetMonoCoinsBalance(userId), Is.EqualTo(500L));
        });

        Assert.ThrowsAsync<MonoCoinsMutationRejectedException>(async () =>
        {
            await coins.AddMonoCoinsAsync(userId, -501L);
        });
        Assert.That(await db.GetMonoCoinsAsync(userId), Is.EqualTo(500L));

        coins.BlockMonoCoinsMutations(userId, "test compensation ambiguity");
        var blocked = await coins.UpdateMonoCoinsBalanceExactAsync(userId, 500L, 499L);
        Assert.That(blocked.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.Blocked));
        Assert.That(await coins.ReconcileMonoCoinsBalanceAsync(userId), Is.True);
        var reconciled = await coins.UpdateMonoCoinsBalanceExactAsync(userId, 500L, 499L);
        Assert.Multiple(() =>
        {
            Assert.That(reconciled.Status, Is.EqualTo(MonoCoinsBalanceUpdateStatus.Success));
            Assert.That(coins.GetMonoCoinsBalance(userId), Is.EqualTo(499L));
        });

        var concurrentAdds = Enumerable.Range(0, 8)
            .Select(_ => coins.AddMonoCoinsAsync(userId, 1L))
            .ToArray();
        await Task.WhenAll(concurrentAdds);
        var persistedAfterAdds = await db.GetMonoCoinsAsync(userId);
        Assert.Multiple(() =>
        {
            Assert.That(persistedAfterAdds, Is.EqualTo(507L));
            Assert.That(coins.GetMonoCoinsBalance(userId), Is.EqualTo(507L));
        });

        await pair.CleanReturnAsync();
    }

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
            4_500,
            Guid.NewGuid());

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
            3_000,
            Guid.NewGuid());

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
            1_001,
            Guid.NewGuid());

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
            50,
            Guid.NewGuid());

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
            4_000,
            Guid.NewGuid());
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
    public async Task OperationJournalMakesRetryIdempotentAndRecoverableUntilAcknowledged()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();
        var operationId = Guid.NewGuid();

        await db.InitPrefsAsync(senderUserId, NewProfile("Journal Sender", 9_000), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Journal Recipient", 1_000), default);
        var senderProfileId = await GetProfileId(db, senderUserId);
        var recipientProfileId = await GetProfileId(db, recipientUserId);

        var first = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            2_500,
            operationId);
        var replay = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            2_500,
            operationId);
        var recovered = await db.GetUnacknowledgedCharacterBankTransferAsync(senderUserId, senderProfileId);
        var senderBalance = await GetBalance(db, senderUserId);
        var recipientBalance = await GetBalance(db, recipientUserId);

        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.True);
            Assert.That(first.AlreadyProcessed, Is.False);
            Assert.That(replay.Success, Is.True);
            Assert.That(replay.AlreadyProcessed, Is.True);
            Assert.That(recovered?.OperationId, Is.EqualTo(operationId));
            Assert.That(recovered?.Amount, Is.EqualTo(2_500));
            Assert.That(senderBalance, Is.EqualTo(6_500));
            Assert.That(recipientBalance, Is.EqualTo(3_500));
        });

        Assert.That(
            await db.AcknowledgeCharacterBankTransferAsync(senderUserId, senderProfileId, operationId),
            Is.True);
        Assert.That(
            await db.GetUnacknowledgedCharacterBankTransferAsync(senderUserId, senderProfileId),
            Is.Null);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task JournalReplaySurvivesRecipientArchiveAndRejectsChangedRequest()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var senderUserId = NewUserId();
        var recipientUserId = NewUserId();
        var operationId = Guid.NewGuid();

        await db.InitPrefsAsync(senderUserId, NewProfile("Archive Replay Sender", 8_000), default);
        await db.InitPrefsAsync(recipientUserId, NewProfile("Archive Replay Recipient", 2_000), default);
        var senderProfileId = await GetProfileId(db, senderUserId);
        var recipientProfileId = await GetProfileId(db, recipientUserId);

        var committed = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            1_500,
            operationId);
        Assert.That(committed.Success, Is.True);

        await db.SaveCharacterSlotAsync(recipientUserId, null, 0);

        var replay = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            1_500,
            operationId);
        var changedRequest = await db.TransferCharacterBankBalanceAsync(
            senderUserId,
            senderProfileId,
            recipientUserId,
            recipientProfileId,
            1_501,
            operationId);

        Assert.Multiple(() =>
        {
            Assert.That(replay.Status, Is.EqualTo(CharacterBankTransferStatus.Success));
            Assert.That(replay.AlreadyProcessed, Is.True);
            Assert.That(replay.SenderBalance, Is.EqualTo(6_500));
            Assert.That(replay.RecipientBalance, Is.EqualTo(3_500));
            Assert.That(changedRequest.Status, Is.EqualTo(CharacterBankTransferStatus.OperationConflict));
            Assert.That(changedRequest.AlreadyProcessed, Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PdaBankIdMappingIsCanonicalAndCollisionSafe()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var firstUserId = NewUserId();
        var secondUserId = NewUserId();
        var thirdUserId = NewUserId();
        var missingUserId = NewUserId();

        await db.InitPrefsAsync(firstUserId, NewProfile("Mapped First", 1_000), default);
        await db.InitPrefsAsync(secondUserId, NewProfile("Mapped Second", 1_000), default);
        await db.InitPrefsAsync(thirdUserId, NewProfile("Mapped Third", 1_000), default);

        var first = await db.RegisterPdaBankAccountAsync(
            firstUserId,
            0,
            "Mapped First",
            "first-user",
            new[] { "MP12345", "MF12346" });
        var second = await db.RegisterPdaBankAccountAsync(
            secondUserId,
            0,
            "Mapped Second",
            "second-user",
            new[] { "MP12345", "MS12347" });
        var stableFirst = await db.RegisterPdaBankAccountAsync(
            firstUserId,
            0,
            "Mapped First",
            "renamed-user",
            new[] { "ZZ99999" });
        var exhausted = await db.RegisterPdaBankAccountAsync(
            thirdUserId,
            0,
            "Mapped Third",
            "third-user",
            new[] { "MP12345" });
        var missing = await db.RegisterPdaBankAccountAsync(
            missingUserId,
            0,
            "Missing Profile",
            "missing-user",
            new[] { "MI12345" });
        var invalid = await db.RegisterPdaBankAccountAsync(
            thirdUserId,
            0,
            "Mapped Third",
            "third-user",
            new[] { "TOO-LONG-ID" });
        var routed = await db.GetPdaBankAccountAsync("MP12345");

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.Success));
            Assert.That(second.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.Success));
            Assert.That(stableFirst.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.Success));
            Assert.That(first.Account?.BankId, Is.EqualTo("MP12345"));
            Assert.That(second.Account?.BankId, Is.EqualTo("MS12347"));
            Assert.That(stableFirst.Account?.BankId, Is.EqualTo("MP12345"));
            Assert.That(stableFirst.Account?.LastUserName, Is.EqualTo("renamed-user"));
            Assert.That(exhausted.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.CandidatesExhausted));
            Assert.That(missing.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.ProfileMissing));
            Assert.That(invalid.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.InvalidRequest));
            Assert.That(routed?.UserId, Is.EqualTo(firstUserId));
            Assert.That(routed?.CharacterName, Is.EqualTo("Mapped First"));
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
                    transferAmount,
                    Guid.NewGuid()))
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

    private static PlayerPreferences PreferencesFor(HumanoidCharacterProfile profile)
    {
        return new PlayerPreferences(
            new[] { new KeyValuePair<int, ICharacterProfile>(0, profile) },
            0,
            Color.Transparent);
    }

    private static NetUserId NewUserId()
    {
        return new NetUserId(Guid.NewGuid());
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
        return (T) field!.GetValue(instance)!;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
        field!.SetValue(instance, value);
    }

    [Virtual]
    public class BankWriteOutcomeProxy : DispatchProxy
    {
        public int? ObservedBalance { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IServerDbManager.UpdateCharacterBankBalanceAsync) =>
                    Task.FromException<CharacterBankBalanceUpdateResult>(
                        new IOException("Injected uncertain offline bank write.")),
                nameof(IServerDbManager.GetCharacterBankBalanceAsync) =>
                    Task.FromResult(ObservedBalance),
                _ => throw new NotSupportedException(
                    $"Unexpected database call in offline outcome test: {targetMethod?.Name}"),
            };
        }
    }

    [Virtual]
    public class PausedBankWriteProxy : DispatchProxy
    {
        private readonly object _lock = new();
        private readonly List<(int ExpectedBalance, int NewBalance)> _writes = new();
        private readonly TaskCompletionSource<bool> _releaseFirstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServerDbManager Inner { get; set; } = default!;

        public TaskCompletionSource<bool> FirstWriteCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstWriteReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(int ExpectedBalance, int NewBalance)> GetWrites()
        {
            lock (_lock)
                return _writes.ToArray();
        }

        public void ReleaseFirstWriteResult()
        {
            _releaseFirstWrite.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IServerDbManager.UpdateCharacterBankBalanceAsync))
            {
                throw new NotSupportedException(
                    $"Unexpected database call in teardown race test: {targetMethod?.Name}");
            }

            return UpdateBalanceAsync(args!);
        }

        private async Task<CharacterBankBalanceUpdateResult> UpdateBalanceAsync(object?[] args)
        {
            var write = ((int) args[3]!, (int) args[4]!);
            var first = false;
            lock (_lock)
            {
                _writes.Add(write);
                first = _writes.Count == 1;
            }

            if (first)
            {
                var committed = await Inner.UpdateCharacterBankBalanceAsync(
                    (NetUserId) args[0]!,
                    (int) args[1]!,
                    (int) args[2]!,
                    write.Item1,
                    write.Item2,
                    (CancellationToken) args[5]!).ConfigureAwait(false);
                FirstWriteCommitted.TrySetResult(true);
                await _releaseFirstWrite.Task.ConfigureAwait(false);
                FirstWriteReturned.TrySetResult(true);
                return committed;
            }

            return await Inner.UpdateCharacterBankBalanceAsync(
                (NetUserId) args[0]!,
                (int) args[1]!,
                (int) args[2]!,
                write.Item1,
                write.Item2,
                (CancellationToken) args[5]!).ConfigureAwait(false);
        }
    }
}
