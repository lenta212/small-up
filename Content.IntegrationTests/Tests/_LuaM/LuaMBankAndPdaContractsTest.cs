#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.PDA;
using Content.Server.Database;
using Content.Server._NF.CryoSleep;
using Content.Server.Preferences.Managers;
using Content.Server._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Preferences;
using Content.Shared.PDA;
using NUnit.Framework;
using Robust.Shared.ContentPack;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.UnitTesting;
using Robust.Shared.Prototypes;
using Robust.Server.Player;
using Robust.Shared.Utility;
using Serilog.Events;
using PdaSystem = Content.Server.PDA.PdaSystem;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMBankAndPdaContractsTest
{
    [TestCase("Passenger", 75000)]
    [TestCase("Janitor", 75000)]
    [TestCase("CargoTechnician", 100000)]
    [TestCase("SecurityGuard", 125000)]
    [TestCase("Sheriff", 200000)]
    [TestCase("CentralCommandOfficial", 250000)]
    [TestCase("UnknownJob", 75000)]
    [TestCase("Prisoner", 0)]
    public void PayrollMappingMatchesExpectedBands(string jobId, int expectedHourly)
    {
        var hourly = InvokePrivateStatic<int>(
            typeof(BankSystem),
            "GetPayrollHourly",
            jobId);

        Assert.That(hourly, Is.EqualTo(expectedHourly));
    }

    [Test]
    public async Task ProfileMutationGateIsExclusiveAtomicFairAndOverflowSafe()
    {
        var preferences = new ServerPreferencesManager();
        var firstUser = new NetUserId(Guid.NewGuid());
        var secondUser = new NetUserId(Guid.NewGuid());

        Assert.That(preferences.TryAcquireProfileMutation(
            new[] { firstUser },
            out var firstLease), Is.True);

        var queuedPreference = preferences.AcquireProfileMutationAsync(new[] { firstUser });
        Assert.That(queuedPreference.IsCompleted, Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(preferences.TryAcquireProfileMutation(
                new[] { firstUser, secondUser },
                out _), Is.False, "A multi-user bank try-acquire must be all-or-nothing.");
            Assert.That(preferences.TryAcquireProfileMutation(
                new[] { secondUser },
                out var secondLease), Is.True,
                "A failed multi-user acquire must not retain an otherwise-free user.");
            secondLease!.Dispose();
        });

        firstLease!.Dispose();
        var queuedLease = await queuedPreference.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(preferences.TryAcquireProfileMutation(
            new[] { firstUser },
            out _), Is.False,
            "A queued preference waiter must receive ownership before a later bank try-acquire.");
        queuedLease.Dispose();

        var versions = GetPrivateField<Dictionary<NetUserId, long>>(
            preferences,
            "_profileMutationVersions");
        versions[firstUser] = long.MaxValue;
        Assert.That(preferences.TryAcquireProfileMutation(
            new[] { firstUser },
            out var overflowLease), Is.True);
        Assert.DoesNotThrow(() => overflowLease!.Dispose(),
            "Change-token wrap must never strand exclusive ownership.");
        Assert.That(preferences.TryAcquireProfileMutation(
            new[] { firstUser },
            out var postOverflowLease), Is.True);
        postOverflowLease!.Dispose();
    }

    [Test]
    public void PayrollOwnerFromPreviousGenerationCannotClearCurrentRoundOwner()
    {
        var bankSystem = new BankSystem();
        var userId = new NetUserId(Guid.NewGuid());
        var oldOwner = CreatePrivateNestedInstance(
            typeof(BankSystem),
            "PayrollDepositOwner",
            10L,
            Guid.NewGuid());
        var currentOwner = CreatePrivateNestedInstance(
            typeof(BankSystem),
            "PayrollDepositOwner",
            11L,
            Guid.NewGuid());
        var inFlight = (System.Collections.IDictionary) GetPrivateField<object>(
            bankSystem,
            "_payrollDepositsInFlight");
        SetPrivateField(bankSystem, "_payrollGeneration", 11L);
        inFlight[userId] = currentOwner;

        Assert.Multiple(() =>
        {
            Assert.That(InvokePrivate<bool>(
                bankSystem,
                "IsCurrentPayrollDeposit",
                userId,
                oldOwner), Is.False);
            Assert.That(InvokePrivate<bool>(
                bankSystem,
                "IsCurrentPayrollDeposit",
                userId,
                currentOwner), Is.True);
        });

        InvokePrivateVoid(bankSystem, "ReleasePayrollDeposit", userId, oldOwner);
        Assert.That(inFlight[userId], Is.EqualTo(currentOwner),
            "An old task's finally block must not remove the new round owner.");
    }

    [Test]
    public void ExactSaveOutcomeClassificationSeparatesRetryableFromCommittedAndUnknown()
    {
        var original = InvokePrivateStatic<object>(
            typeof(BankSystem),
            "ClassifyProfileSaveFailure",
            100,
            100,
            75);
        var committed = InvokePrivateStatic<object>(
            typeof(BankSystem),
            "ClassifyProfileSaveFailure",
            75,
            100,
            75);
        var unknown = InvokePrivateStatic<object>(
            typeof(BankSystem),
            "ClassifyProfileSaveFailure",
            90,
            100,
            75);

        Assert.Multiple(() =>
        {
            Assert.That(original.ToString(), Is.EqualTo("ConfirmedOriginal"));
            Assert.That(committed.ToString(), Is.EqualTo("ConfirmedMutation"));
            Assert.That(unknown.ToString(), Is.EqualTo("Unknown"));
        });
    }

    [Test]
    public async Task PreferenceMessagesPublishOnlyAfterDatabaseSuccessAndObserveFailures()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        ServerPreferencesManager? preferences = null;
        IServerDbManager? realDb = null;
        FailingPreferenceWriteProxy? proxyState = null;
        Func<string, LogEvent, bool>? judgeExpectedPrefsError = null;
        var expectedPrefsErrors = new Dictionary<string, int>();
        var expectedPrefsLogLock = new object();
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var session = playerManager.GetSessionById(clientSession!.UserId);
            attachedSession = session;
            preferences = (ServerPreferencesManager) server.ResolveDependency<IServerPreferencesManager>();
            realDb = server.ResolveDependency<IServerDbManager>();

            judgeExpectedPrefsError = (sawmillName, message) =>
            {
                if (sawmillName != "prefs")
                    return false;

                var rendered = message.RenderMessage();
                string? category = null;
                if (rendered.StartsWith(
                        $"Could not update character for {session.UserId}: " +
                        "System.IO.IOException: Injected ambiguous preference write failure in SaveCharacterSlotAsync.",
                        StringComparison.Ordinal))
                {
                    category = "update-slot";
                }
                else if (rendered.StartsWith(
                             $"Could not select character for {session.UserId}: " +
                             "System.IO.IOException: Injected ambiguous preference write failure in SaveSelectedCharacterIndexAsync.",
                             StringComparison.Ordinal))
                {
                    category = "select-index";
                }
                else if (rendered.StartsWith(
                             $"Could not delete character for {session.UserId}: " +
                             "System.IO.IOException: Injected ambiguous preference write failure in SaveCharacterSlotAsync.",
                             StringComparison.Ordinal))
                {
                    category = "delete-slot";
                }

                if (category == null)
                    return false;

                lock (expectedPrefsLogLock)
                    expectedPrefsErrors[category] = expectedPrefsErrors.GetValueOrDefault(category) + 1;
                return true;
            };
            pair.ServerLogHandler.JudgeLog += judgeExpectedPrefsError;

            Task? createSecondSlot = null;
            var secondProfile = NewBankProfile("DB First Baseline", 4_321);
            await server.WaitPost(() =>
                createSecondSlot = preferences.SetProfile(session.UserId, 1, secondProfile));
            await createSecondSlot!;
            await realDb.SaveSelectedCharacterIndexAsync(session.UserId, 1);

            var durableSnapshot = await realDb.GetPlayerPreferencesSnapshotAsync(session.UserId);
            Assert.That(durableSnapshot, Is.Not.Null);
            var slotZeroProfileId = durableSnapshot!.ProfileIdsBySlot[0];
            var slotOneProfileId = durableSnapshot.ProfileIdsBySlot[1];
            Assert.That(durableSnapshot.Preferences.SelectedCharacterIndex, Is.EqualTo(1));

            var proxy = DispatchProxy.Create<IServerDbManager, FailingPreferenceWriteProxy>();
            proxyState = (FailingPreferenceWriteProxy) (object) proxy;
            proxyState.Inner = realDb;
            SetPrivateField(preferences, "_db", proxy);

            async Task FailMessageAndInspectAsync(Action sendMessage, Action<PlayerPreferences> inspect)
            {
                var generationBefore = preferences.GetCharacterSlotGeneration(session.UserId, 0);
                proxyState.Arm(durableSnapshot);
                await server.WaitPost(sendMessage);
                await proxyState.WriteAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await proxyState.RefreshReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await server.WaitAssertion(() =>
                    inspect(preferences.GetPreferences(session.UserId)));

                proxyState.ReleaseRefresh();
                await proxyState.RefreshCompleted.WaitAsync(TimeSpan.FromSeconds(5));
                await PoolManager.WaitUntil(
                    server,
                    () => preferences.GetCharacterSlotGeneration(session.UserId, 0) > generationBefore,
                    maxTicks: 600);
                await server.WaitAssertion(() =>
                {
                    Assert.That(
                        preferences.GetCharacterSlotGeneration(session.UserId, 0),
                        Is.GreaterThan(generationBefore),
                        "The top-level message observer must reconcile an ambiguous DB exception.");
                    Assert.That(preferences.TryGetCharacterProfileId(
                        session.UserId,
                        0,
                        out var refreshedSlotZeroId), Is.True);
                    Assert.That(refreshedSlotZeroId, Is.EqualTo(slotZeroProfileId));
                    Assert.That(preferences.TryGetCharacterProfileId(
                        session.UserId,
                        1,
                        out var refreshedSlotOneId), Is.True);
                    Assert.That(refreshedSlotOneId, Is.EqualTo(slotOneProfileId));
                });

                // Ensure the observer has left the exclusive gate before arming the
                // next failure in this sequence.
                await server.WaitAssertion(() =>
                {
                    Assert.That(preferences.TryAcquireProfileMutation(
                        new[] { session.UserId },
                        out var lease), Is.True);
                    lease!.Dispose();
                });
            }

            var unpublishedProfile = NewBankProfile("Must Never Publish", 99_999);
            await FailMessageAndInspectAsync(
                () => InvokePrivateVoid(
                    preferences,
                    "HandleUpdateCharacterMessage",
                    new MsgUpdateCharacter
                    {
                        MsgChannel = session.Channel,
                        Slot = 1,
                        Profile = unpublishedProfile,
                    }),
                current =>
                {
                    Assert.That(current.SelectedCharacterIndex, Is.EqualTo(1));
                    Assert.That(current.Characters[1].Name, Is.EqualTo(secondProfile.Name));
                });

            await FailMessageAndInspectAsync(
                () => InvokePrivateVoid(
                    preferences,
                    "HandleSelectCharacterMessage",
                    new MsgSelectCharacter
                    {
                        MsgChannel = session.Channel,
                        SelectedCharacterIndex = 0,
                    }),
                current => Assert.That(current.SelectedCharacterIndex, Is.EqualTo(1)));

            await FailMessageAndInspectAsync(
                () => InvokePrivateVoid(
                    preferences,
                    "HandleDeleteCharacterMessage",
                    new MsgDeleteCharacter
                    {
                        MsgChannel = session.Channel,
                        Slot = 0,
                    }),
                current =>
                {
                    Assert.That(current.Characters.ContainsKey(0), Is.True);
                    Assert.That(current.SelectedCharacterIndex, Is.EqualTo(1));
                });

            lock (expectedPrefsLogLock)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(expectedPrefsErrors.GetValueOrDefault("update-slot"), Is.EqualTo(1));
                    Assert.That(expectedPrefsErrors.GetValueOrDefault("select-index"), Is.EqualTo(1));
                    Assert.That(expectedPrefsErrors.GetValueOrDefault("delete-slot"), Is.EqualTo(1));
                    Assert.That(expectedPrefsErrors.Values.Sum(), Is.EqualTo(3),
                        "Only the three exact injected preference failures may be suppressed.");
                });
            }
        }
        finally
        {
            if (judgeExpectedPrefsError != null)
                pair.ServerLogHandler.JudgeLog -= judgeExpectedPrefsError;
            proxyState?.ReleaseRefresh();
            if (preferences != null && realDb != null)
                SetPrivateField(preferences, "_db", realDb);
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public void PdaBankIdsStayCopyFriendlyAndNormalized()
    {
        const string ownerName = "John Doe";
        const string userName = "Johnny";
        const string userId = "0f66df9a-0c63-4f1d-b661-3ab0f4c8a70d";

        var bankAccountId = InvokePrivateStatic<string>(
            typeof(PdaSystem),
            "BuildPdaBankAccountId",
            ownerName,
            userName,
            userId)!;

        Assert.That(bankAccountId, Does.Match("^[A-Z]{2}\\d{5}$"));

        var normalized = InvokePrivateStatic<string>(
            typeof(PdaSystem),
            "ExtractPdaBankAccountId",
            $"  id: {bankAccountId[..2].ToLowerInvariant()}-{bankAccountId[2..4]} {bankAccountId[4..]} !!");

        Assert.That(normalized, Is.EqualTo(bankAccountId));
    }

    [TestCase("10000", 10000)]
    [TestCase("10 000", 10000)]
    [TestCase("10\u00A0000", 10000)]
    [TestCase("10\u202F000", 10000)]
    public void PdaBankAmountAcceptsCommonGroupingSpaces(string input, int expected)
    {
        var method = typeof(PdaMenu).GetMethod(
            "TryParseBankTransferAmount",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null);

        object?[] arguments = { input, 0 };
        var parsed = (bool) method!.Invoke(null, arguments)!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(arguments[1], Is.EqualTo(expected));
        });
    }

    [Test]
    public void PdaBankCollisionIdsAreDeterministicPerCharacterSlot()
    {
        const string characterName = "Alex Archer";
        const string userName = "Alex";
        const string userId = "0f66df9a-0c63-4f1d-b661-3ab0f4c8a70d";

        var unsalted = InvokePrivateStatic<string>(
            typeof(PdaSystem),
            "BuildPdaBankAccountId",
            characterName,
            userName,
            userId);
        var slotZero = InvokePrivateStatic<string>(
            typeof(PdaSystem),
            "BuildPdaBankAccountCollisionId",
            characterName,
            userName,
            userId,
            0,
            1);
        var slotZeroAgain = InvokePrivateStatic<string>(
            typeof(PdaSystem),
            "BuildPdaBankAccountCollisionId",
            characterName,
            userName,
            userId,
            0,
            1);
        var slotOne = InvokePrivateStatic<string>(
            typeof(PdaSystem),
            "BuildPdaBankAccountCollisionId",
            characterName,
            userName,
            userId,
            1,
            1);

        Assert.Multiple(() =>
        {
            Assert.That(slotZero, Does.Match("^[A-Z]{2}\\d{5}$"));
            Assert.That(slotZero[..2], Is.EqualTo(unsalted[..2]));
            Assert.That(slotOne[..2], Is.EqualTo(unsalted[..2]));
            Assert.That(slotZeroAgain, Is.EqualTo(slotZero));
            Assert.That(slotZero, Is.Not.EqualTo(unsalted));
            Assert.That(slotOne, Is.Not.EqualTo(slotZero));
        });
    }

    [Test]
    public async Task PdaBankTransferByIdWorksForOfflineRecipient()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = serverSession;
            var entMan = server.ResolveDependency<IEntityManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var resourceManager = server.ResolveDependency<IResourceManager>();
            var mindSystem = entMan.System<Content.Server.Mind.MindSystem>();
            var bankSystem = entMan.System<BankSystem>();
            var pdaSystem = entMan.System<PdaSystem>();
            var testMap = await pair.CreateTestMap();

            var recipientId = new NetUserId(new Guid("7d5cc42d-0d0b-4f7a-8f1c-17df4c1f3c21"));
            var recipientProfile = new HumanoidCharacterProfile()
            {
                Name = "Receiver Rook",
                FlavorText = string.Empty,
                Species = "Human",
                Age = 28,
                Appearance = new(
                    "Afro",
                    Color.Aqua,
                    "Shaved",
                    Color.Aquamarine,
                    Color.Azure,
                    Color.Beige,
                    new())
            }.WithBankBalance(1000);

            await db.InitPrefsAsync(recipientId, recipientProfile, default);

            var unsaltedRecipientBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "BuildPdaBankAccountId",
                recipientProfile.Name,
                "Receiver",
                recipientId.ToString());
            var recipientBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "BuildPdaBankAccountCollisionId",
                recipientProfile.Name,
                "Receiver",
                recipientId.ToString(),
                0,
                1);

            Assert.That(recipientBankId[..2], Is.EqualTo(unsaltedRecipientBankId[..2]));
            Assert.That(recipientBankId, Is.Not.EqualTo(unsaltedRecipientBankId));

            resourceManager.UserData.CreateDir(new ResPath("/luam"));
            resourceManager.UserData.WriteAllText(
                new ResPath("/luam/pda-bank-accounts.json"),
                JsonSerializer.Serialize(
                    new Dictionary<string, object>
                    {
                        [recipientBankId] = new
                        {
                            UserId = recipientId.ToString(),
                            Slot = 0,
                            CharacterName = recipientProfile.Name,
                            LastUserName = "Receiver",
                        }
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            // A pooled server may already have completed its process-local import
            // before this test writes the compatibility fixture. Reset only that
            // in-memory task to simulate a fresh server process; the JSON itself is
            // deliberately left untouched.
            var importTaskField = typeof(PdaSystem).GetField(
                "_legacyBankRegistryImportTask",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(importTaskField, Is.Not.Null);
            importTaskField!.SetValue(pdaSystem, null);
            await InvokePrivateAsync(pdaSystem, "EnsureLegacyPdaBankRegistryImportedAsync");
            Assert.That((await db.GetPdaBankAccountAsync(recipientBankId))?.BankId, Is.EqualTo(recipientBankId));

            EntityUid sender = default;
            await server.WaitPost(() =>
            {
                sender = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMBankAndPdaContractsTest");
                mindSystem.TransferTo(mind, sender);
                playerMan.SetAttachedEntity(serverSession, sender);
            });

            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                entMan.EnsureComponent<BankAccountComponent>(sender);
                bankSystem.SyncBankBalance(sender);
            });

            await OpenPdaAndLoadBankIdentity(pair, pdaSystem, entMan, sender, testMap.GridCoords, serverSession);

            Assert.That(entMan.TryGetComponent(sender, out BankAccountComponent? bankAccount), Is.True);
            Assert.That(bankAccount, Is.Not.Null);

            var formattedRecipientBankId = $"{recipientBankId[..2].ToLowerInvariant()}-{recipientBankId[2..4]} {recipientBankId[4..]}";
            var normalizedRecipientBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "ExtractPdaBankAccountId",
                formattedRecipientBankId);
            var recipientRecord = await db.GetPdaBankAccountAsync(normalizedRecipientBankId);
            Assert.That(recipientRecord, Is.Not.Null);
            var result = await InvokePrivateAsync(
                pdaSystem,
                "TryRegisteredBankTransfer",
                sender,
                recipientRecord!.Value,
                5000,
                Guid.NewGuid());

            Assert.That(result, Is.Not.Null);
            Assert.That(GetPrivateProperty<bool>(result, "Success"), Is.True);
            Assert.That(GetPrivateProperty<int>(result, "SenderBalance"), Is.EqualTo(70000));
            Assert.That(GetPrivateProperty<int>(result, "RecipientBalance"), Is.EqualTo(6000));
            Assert.That(GetPrivateProperty<string>(result, "RecipientName"), Is.EqualTo(recipientProfile.Name));

            var savedRecipient = await db.GetPlayerPreferencesAsync(recipientId, default);
            Assert.That(savedRecipient, Is.Not.Null);
            Assert.That(savedRecipient!.Characters[0], Is.TypeOf<HumanoidCharacterProfile>());
            Assert.That(((HumanoidCharacterProfile) savedRecipient.Characters[0]).BankBalance, Is.EqualTo(6000));
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task InFlightDuplicatePdaBankTransferIsRejectedAndConservesFunds()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = serverSession;
            var entMan = server.ResolveDependency<IEntityManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var resourceManager = server.ResolveDependency<IResourceManager>();
            var mindSystem = entMan.System<Content.Server.Mind.MindSystem>();
            var bankSystem = entMan.System<BankSystem>();
            var pdaSystem = entMan.System<PdaSystem>();
            var testMap = await pair.CreateTestMap();

            var recipientId = new NetUserId(new Guid("5e4ad613-905e-4010-8356-5eefbb577fad"));
            var recipientProfile = new HumanoidCharacterProfile()
            {
                Name = "Duplicate Target",
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
                    new())
            }.WithBankBalance(1000);

            await db.InitPrefsAsync(recipientId, recipientProfile, default);

            var recipientBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "BuildPdaBankAccountCollisionId",
                recipientProfile.Name,
                "Duplicate",
                recipientId.ToString(),
                0,
                1);

            var recipientRecord = await db.RegisterPdaBankAccountAsync(
                recipientId,
                0,
                recipientProfile.Name,
                "Duplicate",
                new[] { recipientBankId });
            Assert.That(recipientRecord.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.Success));
            Assert.That(recipientRecord.Account, Is.Not.Null);

            resourceManager.UserData.CreateDir(new ResPath("/luam"));
            resourceManager.UserData.WriteAllText(
                new ResPath("/luam/pda-bank-accounts.json"),
                JsonSerializer.Serialize(
                    new Dictionary<string, object>
                    {
                        [recipientBankId] = new
                        {
                            UserId = recipientId.ToString(),
                            Slot = 0,
                            CharacterName = recipientProfile.Name,
                            LastUserName = "Duplicate",
                        }
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            EntityUid sender = default;
            await server.WaitPost(() =>
            {
                sender = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMBankAndPdaContractsTest");
                mindSystem.TransferTo(mind, sender);
                playerMan.SetAttachedEntity(serverSession, sender);
            });

            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                entMan.EnsureComponent<BankAccountComponent>(sender);
                bankSystem.SyncBankBalance(sender);
            });

            await OpenPdaAndLoadBankIdentity(pair, pdaSystem, entMan, sender, testMap.GridCoords, serverSession);

            var inFlight = GetPrivateField<HashSet<EntityUid>>(pdaSystem, "_registeredBankTransfersInFlight");
            Assert.That(inFlight.Add(sender), Is.True);

            object? duplicate;
            try
            {
                duplicate = await InvokePrivateAsync(
                    pdaSystem,
                    "TryRegisteredBankTransfer",
                    sender,
                    recipientRecord.Account!.Value,
                    5000,
                    Guid.NewGuid());
            }
            finally
            {
                inFlight.Remove(sender);
            }

            Assert.That(GetPrivateProperty<bool>(duplicate, "Success"), Is.False);
            Assert.That(GetPrivateProperty<string>(duplicate, "Error"), Is.EqualTo("transfer-pending"));

            var accepted = await InvokePrivateAsync(
                pdaSystem,
                "TryRegisteredBankTransfer",
                sender,
                recipientRecord.Account!.Value,
                5000,
                Guid.NewGuid());
            Assert.That(GetPrivateProperty<bool>(accepted, "Success"), Is.True);

            Assert.That(bankSystem.TryGetBalance(sender, out var senderBalance), Is.True);
            var savedRecipient = await db.GetPlayerPreferencesAsync(recipientId, default);
            Assert.That(savedRecipient, Is.Not.Null);
            var recipientBalance = ((HumanoidCharacterProfile) savedRecipient!.Characters[0]).BankBalance;

            Assert.Multiple(() =>
            {
                Assert.That(senderBalance, Is.EqualTo(70000));
                Assert.That(recipientBalance, Is.EqualTo(6000));
                Assert.That(senderBalance + recipientBalance, Is.EqualTo(76000));
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task PdaBankIdentityIsReplacedWhenSameNameCharacterReusesSlot()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var session = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var bankSystem = entMan.System<BankSystem>();
            var pdaSystem = entMan.System<PdaSystem>();
            var testMap = await pair.CreateTestMap();

            await SelectFreshBankProfile(server, session, "PDA replacement fixture");

            var recipientId = new NetUserId(Guid.NewGuid());
            var recipientProfile = NewBankProfile("Recreate Recipient", 1_000);
            await db.InitPrefsAsync(recipientId, recipientProfile, default);
            var recipientBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "BuildPdaBankAccountId",
                recipientProfile.Name,
                "RecreateRecipient",
                recipientId.ToString());
            var recipientCollisionBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "BuildPdaBankAccountCollisionId",
                recipientProfile.Name,
                "RecreateRecipient",
                recipientId.ToString(),
                0,
                1);
            var recipientRegistration = await db.RegisterPdaBankAccountAsync(
                recipientId,
                0,
                recipientProfile.Name,
                "RecreateRecipient",
                new[] { recipientBankId, recipientCollisionBankId });
            Assert.That(recipientRegistration.Status, Is.EqualTo(PdaBankAccountRegistrationStatus.Success));
            Assert.That(recipientRegistration.Account, Is.Not.Null);

            EntityUid actor = default;
            await server.WaitPost(() =>
            {
                actor = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                AttachBankBody(entMan, playerMan, session, actor);
                entMan.EnsureComponent<BankAccountComponent>(actor);
                bankSystem.SyncBankBalance(actor);
            });

            var (senderPda, senderPdaComponent) = await OpenPdaAndLoadBankIdentity(
                pair,
                pdaSystem,
                entMan,
                actor,
                testMap.GridCoords,
                session);

            var slot = -1;
            HumanoidCharacterProfile? replacement = null;
            await server.WaitPost(() =>
            {
                var current = preferences.GetPreferences(session.UserId);
                slot = current.SelectedCharacterIndex;
                replacement = ((HumanoidCharacterProfile) current.Characters[slot])
                    .WithBankBalance(HumanoidCharacterProfile.SectorPioneerGrant);
            });

            var oldProfileId = await db.GetCharacterIdAsync(session.UserId, slot);
            Assert.That(oldProfileId, Is.Not.Null);
            var oldAccount = await db.GetPdaBankAccountByProfileIdAsync(oldProfileId!.Value);
            Assert.That(oldAccount, Is.Not.Null);
            Assert.That(HasProfileIdentityKey(
                pdaSystem,
                "_registeredBankAccountIds",
                oldProfileId.Value), Is.True);
            var oldGeneration = preferences.GetCharacterSlotGeneration(session.UserId, slot);

            var preview = new PdaBankTransferPreviewMessage(
                recipientRegistration.Account!.Value.BankId,
                500)
            {
                UiKey = PdaUiKey.Key,
                Actor = actor,
                Entity = entMan.GetNetEntity(senderPda),
            };
            await InvokePrivateAsync(
                pdaSystem,
                "HandleBankTransferPreviewAsync",
                senderPda,
                senderPdaComponent,
                preview);
            Assert.That(PrivateDictionaryContainsKey(
                pdaSystem,
                "_pendingBankTransferConfirmations",
                senderPda), Is.True);
            RemovePrivateDictionaryEntry(
                preferences,
                "_characterProfileIds",
                (session.UserId, slot));
            Assert.Multiple(() =>
            {
                Assert.That(bankSystem.IsBankOperationPending(actor), Is.True,
                    "A stored profile with an unresolved durable identity must fail closed.");
                Assert.That(bankSystem.TryBankWithdraw(actor, 1), Is.False);
            });
            Assert.That(await bankSystem.TryBankWithdrawAsync(actor, 1), Is.False);

            var oldBlockedIdentity = ArtificiallyBlockBankProfile(
                bankSystem,
                session.UserId,
                oldProfileId.Value);
            var oldPdaRetryBlock = CreatePrivateNestedInstance(
                typeof(PdaSystem),
                "BankStableProfileIdentity",
                session.UserId,
                oldProfileId.Value);
            AddPrivateSetValue(
                pdaSystem,
                "_bankTransferRetryBlockedProfiles",
                oldPdaRetryBlock);
            Assert.That(bankSystem.IsBankOperationPending(actor), Is.True);

            Task? sameProfileRefresh = null;
            await server.WaitPost(() =>
                sameProfileRefresh = preferences.RefreshPreferencesAsync(session, default));
            await sameProfileRefresh!;

            Assert.Multiple(() =>
            {
                Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out var refreshedProfileId),
                    Is.True);
                Assert.That(refreshedProfileId, Is.EqualTo(oldProfileId.Value));
                Assert.That(bankSystem.IsBankOperationPending(actor), Is.True,
                    "Refreshing the same durable ProfileId must preserve its fail-closed block.");
                Assert.That(PrivateSetContains(
                    bankSystem,
                    "_blockedBalanceMutations",
                    oldBlockedIdentity), Is.True);
                Assert.That(PrivateSetContains(
                    pdaSystem,
                    "_bankTransferRetryBlockedProfiles",
                    oldPdaRetryBlock), Is.True,
                    "PDA retry protection must survive a generation-only refresh of the same ProfileId.");
            });
            Assert.That(bankSystem.TryBankWithdraw(actor, 1), Is.False,
                "The stable block must stop the synchronous mutation path.");
            Assert.That(await bankSystem.TryBankWithdrawAsync(actor, 1), Is.False,
                "The stable block must stop the awaited mutation path.");
            PlayerPreferences? blockedPreferences = null;
            await server.WaitPost(() =>
                blockedPreferences = preferences.GetPreferences(session.UserId));
            var blockedProfile = (HumanoidCharacterProfile) blockedPreferences!.Characters[slot];
            Assert.That(await bankSystem.TryBankWithdrawOffline(
                    session.UserId,
                    blockedPreferences,
                    blockedProfile,
                    oldProfileId.Value,
                    1),
                Is.False,
                "The stable block must stop the offline mutation path.");

            Task? disconnectCleanup = null;
            Task? reconnectLoad = null;
            await server.WaitPost(() =>
            {
                disconnectCleanup = preferences.OnClientDisconnectedAsync(session);
                reconnectLoad = preferences.LoadData(session, default);
            });
            await Task.WhenAll(disconnectCleanup!, reconnectLoad!);
            Task? finishLoad = null;
            await server.WaitPost(() => finishLoad = preferences.FinishLoadAsync(session));
            await finishLoad!;
            Assert.Multiple(() =>
            {
                Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out var reconnectedProfileId),
                    Is.True);
                Assert.That(reconnectedProfileId, Is.EqualTo(oldProfileId.Value));
                Assert.That(bankSystem.IsBankOperationPending(actor), Is.True,
                    "The stable ProfileId block must survive disconnect/load of the same identity.");
                Assert.That(PrivateSetContains(
                    pdaSystem,
                    "_bankTransferRetryBlockedProfiles",
                    oldPdaRetryBlock), Is.True,
                    "The PDA retry block must survive reconnect of the same ProfileId.");
            });

            await OpenPdaAndLoadBankIdentity(pair, pdaSystem, entMan, actor, testMap.GridCoords, session);
            await InvokePrivateAsync(
                pdaSystem,
                "HandleBankTransferPreviewAsync",
                senderPda,
                senderPdaComponent,
                preview);
            Assert.That(PrivateDictionaryContainsKey(
                pdaSystem,
                "_pendingBankTransferConfirmations",
                senderPda), Is.True,
                "A real pending preview must exist when the delete/recreate invalidation runs.");

            // Archive the durable profile, then create a distinct profile with the
            // exact same name in the same slot. Slot/name alone must not resurrect
            // any PDA account, recovery, pending operation, or async load result.
            await db.SaveCharacterSlotAsync(session.UserId, null, slot);
            await db.SaveCharacterSlotAsync(session.UserId, replacement!, slot);
            var newProfileId = await db.GetCharacterIdAsync(session.UserId, slot);
            Assert.That(newProfileId, Is.Not.Null);
            Assert.That(newProfileId, Is.Not.EqualTo(oldProfileId));

            var staleIdentityUpdate = await db.UpdateCharacterBankBalanceAsync(
                session.UserId,
                oldProfileId.Value,
                slot,
                replacement!.BankBalance,
                replacement.BankBalance + 1);
            var staleBalanceUpdate = await db.UpdateCharacterBankBalanceAsync(
                session.UserId,
                newProfileId!.Value,
                slot,
                replacement.BankBalance + 1,
                replacement.BankBalance + 2);
            var replacementBalance = await db.GetCharacterBankBalanceAsync(
                session.UserId,
                newProfileId.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(staleIdentityUpdate.Status,
                    Is.EqualTo(CharacterBankBalanceUpdateStatus.ProfileMissing));
                Assert.That(staleBalanceUpdate.Status,
                    Is.EqualTo(CharacterBankBalanceUpdateStatus.BalanceConflict));
                Assert.That(staleBalanceUpdate.CurrentBalance, Is.EqualTo(replacement.BankBalance));
                Assert.That(replacementBalance, Is.EqualTo(replacement.BankBalance));
            });

            Task? refreshTask = null;
            await server.WaitPost(() =>
                refreshTask = preferences.RefreshPreferencesAsync(session, default));
            await refreshTask!;

            Assert.Multiple(() =>
            {
                Assert.That(preferences.GetCharacterSlotGeneration(session.UserId, slot),
                    Is.GreaterThan(oldGeneration));
                Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out var currentProfileId),
                    Is.True);
                Assert.That(currentProfileId, Is.EqualTo(newProfileId.Value));
                Assert.That(HasActivePdaBankIdentity(
                    pdaSystem,
                    session.UserId,
                    slot,
                    oldProfileId.Value), Is.False);
                Assert.That(HasProfileIdentityKey(
                    pdaSystem,
                    "_registeredBankAccountIds",
                    oldProfileId.Value), Is.False);
                Assert.That(HasProfileIdentityKey(
                    pdaSystem,
                    "_bankTransferRecoveries",
                    oldProfileId.Value), Is.False);
                Assert.That(PrivateDictionaryContainsKey(
                    pdaSystem,
                    "_pendingBankTransferConfirmations",
                    senderPda), Is.False);
                Assert.That(PrivateSetContains(
                    bankSystem,
                    "_blockedBalanceMutations",
                    oldBlockedIdentity), Is.True,
                    "The fail-closed block must remain attached to the old identity for audit/safety.");
                Assert.That(bankSystem.IsBankOperationPending(actor), Is.False,
                    "A recreated profile in the same slot must not inherit the old identity's block.");
            });

            await OpenPdaAndLoadBankIdentity(pair, pdaSystem, entMan, actor, testMap.GridCoords, session);
            var newAccount = await db.GetPdaBankAccountByProfileIdAsync(newProfileId!.Value);
            var archivedRoute = await db.GetPdaBankAccountAsync(oldAccount!.Value.BankId);
            var replacementPdaRetryBlock = CreatePrivateNestedInstance(
                typeof(PdaSystem),
                "BankStableProfileIdentity",
                session.UserId,
                newProfileId.Value);

            Assert.Multiple(() =>
            {
                Assert.That(archivedRoute, Is.Null);
                Assert.That(newAccount, Is.Not.Null);
                Assert.That(newAccount!.Value.ProfileId, Is.EqualTo(newProfileId.Value));
                Assert.That(newAccount.Value.BankId, Is.Not.EqualTo(oldAccount.Value.BankId));
                Assert.That(HasActivePdaBankIdentity(
                    pdaSystem,
                    session.UserId,
                    slot,
                    newProfileId.Value), Is.True);
                Assert.That(PrivateSetContains(
                    pdaSystem,
                    "_bankTransferRetryBlockedProfiles",
                    replacementPdaRetryBlock), Is.False,
                    "A replacement ProfileId must not inherit the archived profile's PDA retry block.");
            });

            await InvokePrivateAsync(
                pdaSystem,
                "HandleBankTransferPreviewAsync",
                senderPda,
                senderPdaComponent,
                preview);
            var newGeneration = preferences.GetCharacterSlotGeneration(session.UserId, slot);
            Assert.That(newGeneration, Is.GreaterThan(0));
            var newPdaIdentity = GetActivePdaBankIdentity(
                pdaSystem,
                session.UserId,
                slot,
                newProfileId.Value);
            Assert.That(newPdaIdentity, Is.Not.Null);
            SetPrivateDictionaryEntry(
                pdaSystem,
                "_bankTransferRecoveries",
                newPdaIdentity!,
                new PdaBankTransferRecovery(
                    Guid.NewGuid(),
                    recipientProfile.Name,
                    recipientRegistration.Account!.Value.BankId,
                    500,
                    HumanoidCharacterProfile.SectorPioneerGrant));
            var newPdaRetryBlock = CreatePrivateNestedInstance(
                typeof(PdaSystem),
                "BankStableProfileIdentity",
                session.UserId,
                newProfileId.Value);
            AddPrivateSetValue(
                pdaSystem,
                "_bankTransferRetryBlockedProfiles",
                newPdaRetryBlock);
            var newPdaSlotKey = CreatePrivateNestedInstance(
                typeof(PdaSystem),
                "BankSlotKey",
                session.UserId,
                slot);
            var newLoadAttempt = CreatePrivateNestedInstance(
                typeof(PdaSystem),
                "BankLoadAttempt",
                4242L,
                newGeneration,
                replacement!.Name);
            var newLoadRetry = CreatePrivateNestedInstance(
                typeof(PdaSystem),
                "BankLoadRetry",
                newGeneration,
                TimeSpan.FromMinutes(1));
            SetPrivateDictionaryEntry(
                pdaSystem,
                "_bankStateLoadsInFlight",
                newPdaSlotKey,
                newLoadAttempt);
            SetPrivateDictionaryEntry(
                pdaSystem,
                "_bankStateRetryAfter",
                newPdaSlotKey,
                newLoadRetry);

            InvokePrivateVoid(
                pdaSystem,
                "OnCharacterSlotIdentityInvalidated",
                new CharacterSlotIdentityInvalidated(session.UserId, slot, newGeneration - 1));

            Assert.Multiple(() =>
            {
                Assert.That(HasActivePdaBankIdentity(
                    pdaSystem,
                    session.UserId,
                    slot,
                    newProfileId.Value), Is.True);
                Assert.That(HasProfileIdentityKey(
                    pdaSystem,
                    "_registeredBankAccountIds",
                    newProfileId.Value), Is.True);
                Assert.That(HasProfileIdentityKey(
                    pdaSystem,
                    "_bankTransferRecoveries",
                    newProfileId.Value), Is.True);
                Assert.That(PrivateDictionaryContainsKey(
                    pdaSystem,
                    "_pendingBankTransferConfirmations",
                    senderPda), Is.True);
                Assert.That(PrivateSetContains(
                    pdaSystem,
                    "_bankTransferRetryBlockedProfiles",
                    newPdaRetryBlock), Is.True,
                    "A late stale invalidation event must not purge newer-generation state.");
                Assert.That(PrivateDictionaryContainsKey(
                    pdaSystem,
                    "_bankStateLoadsInFlight",
                    newPdaSlotKey), Is.True);
                Assert.That(PrivateDictionaryContainsKey(
                    pdaSystem,
                    "_bankStateRetryAfter",
                    newPdaSlotKey), Is.True);
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task RefreshPublishesOnlyLatestSnapshotAfterActiveBankMutation()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        IDisposable? mutationLease = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var session = playerMan.GetSessionById(clientSession!.UserId);
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var db = server.ResolveDependency<IServerDbManager>();

            var slot = -1;
            var originalBalance = 0;
            var originalGeneration = 0L;
            await server.WaitPost(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                slot = cached.SelectedCharacterIndex;
                originalBalance = ((HumanoidCharacterProfile) cached.Characters[slot]).BankBalance;
                originalGeneration = preferences.GetCharacterSlotGeneration(session.UserId, slot);
                Assert.That(preferences.TryAcquireProfileMutation(
                    new[] { session.UserId },
                    out mutationLease), Is.True);
            });

            Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out var profileId), Is.True);
            Task? olderRefresh = null;
            Task? latestRefresh = null;
            await server.WaitPost(() =>
            {
                olderRefresh = preferences.RefreshPreferencesAsync(session, default);
                latestRefresh = preferences.RefreshPreferencesAsync(session, default);
            });
            Assert.Multiple(() =>
            {
                Assert.That(olderRefresh!.IsCompleted, Is.False);
                Assert.That(latestRefresh!.IsCompleted, Is.False);
            });

            var committedBalance = originalBalance + 321;
            var update = await db.UpdateCharacterBankBalanceAsync(
                session.UserId,
                profileId,
                slot,
                originalBalance,
                committedBalance);
            Assert.That(update.Status, Is.EqualTo(CharacterBankBalanceUpdateStatus.Success));

            await server.WaitPost(() =>
            {
                Assert.That(preferences.TryApplyPersistedBankBalance(
                    session.UserId,
                    slot,
                    profileId,
                    committedBalance), Is.True);
                mutationLease!.Dispose();
                mutationLease = null;
            });

            // Integration instances only advance while the harness pumps them.
            // Waiting on the raw tasks can therefore deadlock even though a real
            // server's continuous tick loop would grant both queued refreshes.
            await PoolManager.WaitUntil(
                server,
                () => olderRefresh!.IsCompleted && latestRefresh!.IsCompleted,
                maxTicks: 600);
            await Task.WhenAll(olderRefresh!, latestRefresh!).WaitAsync(TimeSpan.FromSeconds(5));

            var cachedBalance = 0;
            await server.WaitPost(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                cachedBalance = ((HumanoidCharacterProfile) cached.Characters[slot]).BankBalance;
            });
            Assert.Multiple(() =>
            {
                Assert.That(cachedBalance, Is.EqualTo(committedBalance));
                Assert.That(preferences.GetCharacterSlotGeneration(session.UserId, slot),
                    Is.EqualTo(originalGeneration + 1),
                    "Only the newest concurrent refresh request may publish and invalidate the slot.");
                Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out var refreshedProfileId),
                    Is.True);
                Assert.That(refreshedProfileId, Is.EqualTo(profileId));
            });
        }
        finally
        {
            if (mutationLease != null)
                await pair.Server.WaitPost(() => mutationLease!.Dispose());
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task OfflineBankMutationRejectsStaleBalanceAndReplacedProfile()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true
        });

        try
        {
            var server = pair.Server;
            var db = server.ResolveDependency<IServerDbManager>();
            var bankSystem = server.ResolveDependency<IEntityManager>().System<BankSystem>();
            var userId = new NetUserId(Guid.NewGuid());
            var originalProfile = NewBankProfile("Offline Exact Identity", 1_000);
            await db.InitPrefsAsync(userId, originalProfile, default);
            var originalSnapshot = await db.GetPlayerPreferencesSnapshotAsync(userId);
            Assert.That(originalSnapshot, Is.Not.Null);
            var slot = originalSnapshot!.Preferences.SelectedCharacterIndex;
            var originalProfileId = originalSnapshot.ProfileIdsBySlot[slot];
            var staleProfile = (HumanoidCharacterProfile) originalSnapshot.Preferences.Characters[slot];

            var concurrentUpdate = await db.UpdateCharacterBankBalanceAsync(
                userId,
                originalProfileId,
                slot,
                staleProfile.BankBalance,
                staleProfile.BankBalance + 10);
            Assert.That(concurrentUpdate.Status, Is.EqualTo(CharacterBankBalanceUpdateStatus.Success));
            Assert.That(await bankSystem.TryBankDepositOffline(
                    userId,
                    originalSnapshot.Preferences,
                    staleProfile,
                    originalProfileId,
                    100),
                Is.False,
                "A stale expected balance must not overwrite a newer durable balance.");
            Assert.That(await db.GetCharacterBankBalanceAsync(userId, originalProfileId, slot),
                Is.EqualTo(staleProfile.BankBalance + 10));

            await db.SaveCharacterSlotAsync(userId, null, slot);
            var replacementProfile = NewBankProfile(originalProfile.Name, 2_000);
            await db.SaveCharacterSlotAsync(userId, replacementProfile, slot);
            var replacementProfileId = await db.GetCharacterIdAsync(userId, slot);
            Assert.That(replacementProfileId, Is.Not.Null.And.Not.EqualTo(originalProfileId));

            Assert.That(await bankSystem.TryBankDepositOffline(
                    userId,
                    originalSnapshot.Preferences,
                    staleProfile,
                    originalProfileId,
                    100),
                Is.False,
                "An operation captured for archived P must not mutate replacement Q in the same slot/name.");
            var archivedBalance = await db.GetCharacterBankBalanceAsync(userId, originalProfileId, slot);
            var currentReplacementBalance = await db.GetCharacterBankBalanceAsync(
                userId,
                replacementProfileId!.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(archivedBalance, Is.Null);
                Assert.That(currentReplacementBalance, Is.EqualTo(replacementProfile.BankBalance));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task SpawnLoadoutDebitCapturedForArchivedProfileCannotChargeReplacement()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var session = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = session;
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var entityManager = server.ResolveDependency<IEntityManager>();
            var bank = entityManager.System<BankSystem>();
            var map = await pair.CreateTestMap();

            await SelectFreshBankProfile(server, session, "Archived debit fixture");

            var slot = -1;
            var capturedProfileId = 0;
            HumanoidCharacterProfile? capturedProfile = null;
            await server.WaitPost(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                slot = cached.SelectedCharacterIndex;
                capturedProfile = (HumanoidCharacterProfile) cached.Characters[slot];
                Assert.That(preferences.TryGetCharacterProfileId(
                    session.UserId,
                    slot,
                    out capturedProfileId), Is.True);
            });

            await db.SaveCharacterSlotAsync(session.UserId, null, slot);
            var replacement = NewBankProfile(capturedProfile!.Name, 2_000);
            await db.SaveCharacterSlotAsync(session.UserId, replacement, slot);
            var replacementProfileId = await db.GetCharacterIdAsync(session.UserId, slot);
            Assert.That(replacementProfileId, Is.Not.Null.And.Not.EqualTo(capturedProfileId));

            Task? refresh = null;
            await server.WaitPost(() =>
                refresh = preferences.RefreshPreferencesAsync(session, default));
            await refresh!;

            EntityUid spawned = default;
            await server.WaitPost(() =>
            {
                spawned = entityManager.SpawnEntity("MobHuman", map.GridCoords);
                AttachBankBody(entityManager, playerMan, session, spawned);
            });

            var finalized = false;
            var debited = await bank.TryBankWithdrawProfileAsync(
                session,
                spawned,
                slot,
                capturedProfileId,
                500,
                spendLongTerm: false,
                finalizeAfterCommit: () =>
                {
                    finalized = true;
                    return true;
                });

            var replacementBalance = await db.GetCharacterBankBalanceAsync(
                session.UserId,
                replacementProfileId!.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(debited, Is.False);
                Assert.That(finalized, Is.False);
                Assert.That(replacementBalance, Is.EqualTo(replacement.BankBalance));
                Assert.That(preferences.TryGetCharacterProfileId(
                    session.UserId,
                    slot,
                    out var activeProfileId), Is.True);
                Assert.That(activeProfileId, Is.EqualTo(replacementProfileId.Value));
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task PdaBankTransferByIdMissingRecipientShowsRegistrationHint()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var senderSession = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = senderSession;
            var entMan = server.ResolveDependency<IEntityManager>();
            var resourceManager = server.ResolveDependency<IResourceManager>();
            var bankSystem = entMan.System<BankSystem>();
            var pdaSystem = entMan.System<PdaSystem>();
            var testMap = await pair.CreateTestMap();

            resourceManager.UserData.CreateDir(new ResPath("/luam"));

            EntityUid sender = default;
            await server.WaitPost(() =>
            {
                sender = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = entMan.System<Content.Server.Mind.MindSystem>().CreateMind(senderSession.UserId, "LuaMBankAndPdaContractsTest");
                entMan.System<Content.Server.Mind.MindSystem>().TransferTo(mind, sender);
                playerMan.SetAttachedEntity(senderSession, sender);
                entMan.EnsureComponent<BankAccountComponent>(sender);
                bankSystem.SyncBankBalance(sender);
            });

            await pair.RunTicksSync(5);

            var (senderPda, pda) = await OpenPdaAndLoadBankIdentity(
                pair,
                pdaSystem,
                entMan,
                sender,
                testMap.GridCoords,
                senderSession);

            var transfer = new PdaBankTransferPreviewMessage("ZZ-99999", 5000)
            {
                UiKey = PdaUiKey.Key,
                Actor = sender,
                Entity = entMan.GetNetEntity(senderPda),
            };

            await InvokePrivateAsync(
                pdaSystem,
                "HandleBankTransferPreviewAsync",
                senderPda,
                pda,
                transfer);

            await pair.RunTicksSync(2);

            var pdaComponent = pda;

            await server.WaitAssertion(() =>
            {
                Assert.That(pdaComponent.LastBankTransferStatus, Does.Contain("банковский ID").Or.Contain("bank ID"));
                Assert.That(pdaComponent.LastBankTransferStatus, Does.Contain("открыть КПК").Or.Contain("open a PDA"));
                Assert.That(bankSystem.TryGetBalance(sender, out var balance), Is.True);
                Assert.That(balance, Is.EqualTo(75000));
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task PayrollStatusUsesAssignedJobAndReportsHourlyRate()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = serverSession;
            var entMan = server.ResolveDependency<IEntityManager>();
            var bankSystem = entMan.System<BankSystem>();
            var testMap = await pair.CreateTestMap();

            EntityUid worker = default;
            await server.WaitPost(() =>
            {
                worker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                playerMan.SetAttachedEntity(serverSession, worker);
                entMan.EnsureComponent<BankAccountComponent>(worker);
                var job = entMan.EnsureComponent<PlayerJobComponent>(worker);
                job.JobPrototype = "Passenger";
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(bankSystem.TryGetPayrollStatus(worker, serverSession, out var hourly, out var nextSeconds), Is.True);
                Assert.That(hourly, Is.EqualTo(BankSystem.PayrollMinimumHourly));
                Assert.That(nextSeconds, Is.InRange(1, (int) BankSystem.PayrollIntervalSeconds));
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task PayrollStatusFallsBackToEntityJobAfterPlayerDataRemoval()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        try
        {
            var server = pair.Server;
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var entMan = server.ResolveDependency<IEntityManager>();
            var bankSystem = entMan.System<BankSystem>();
            var testMap = await pair.CreateTestMap();

            var payrollAvailable = false;
            var hourly = 0;
            var nextSeconds = 0;

            await server.WaitPost(() =>
            {
                var userId = new NetUserId(new Guid("8ca34104-4a3d-4eb8-aed7-91abc94053b4"));
                var teardownSession = playerMan.CreateAndAddSession(userId, "PayrollTeardown");
                var worker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                playerMan.SetAttachedEntity(teardownSession, worker);
                entMan.EnsureComponent<BankAccountComponent>(worker);
                var job = entMan.EnsureComponent<PlayerJobComponent>(worker);
                job.JobPrototype = "Passenger";

                playerMan.RemoveSession(teardownSession, removeData: true);
                Assert.That(playerMan.HasPlayerData(userId), Is.False);

                try
                {
                    payrollAvailable = bankSystem.TryGetPayrollStatus(
                        worker,
                        teardownSession,
                        out hourly,
                        out nextSeconds);
                }
                finally
                {
                    // Restore the manager entry before detaching: unrelated detach
                    // observers expect player data to exist. The missing-data window
                    // under test ends immediately after the payroll query above.
                    var cleanupSession = playerMan.CreateAndAddSession(userId, "PayrollTeardown");
                    playerMan.SetAttachedEntity(teardownSession, null, true);
                    playerMan.RemoveSession(cleanupSession, removeData: true);
                    entMan.QueueDeleteEntity(worker);
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(payrollAvailable, Is.True);
                Assert.That(hourly, Is.EqualTo(BankSystem.PayrollMinimumHourly));
                Assert.That(nextSeconds, Is.InRange(1, (int) BankSystem.PayrollIntervalSeconds));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task PayrollDepositsHourlyRateWhenTimerElapses()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = serverSession;
            var entMan = server.ResolveDependency<IEntityManager>();
            var bankSystem = entMan.System<BankSystem>();
            var testMap = await pair.CreateTestMap();

            await SelectFreshBankProfile(server, serverSession, "Payroll fixture");

            EntityUid worker = default;
            await server.WaitPost(() =>
            {
                worker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                AttachBankBody(entMan, playerMan, serverSession, worker);
                entMan.EnsureComponent<BankAccountComponent>(worker);
                var job = entMan.EnsureComponent<PlayerJobComponent>(worker);
                job.JobPrototype = "Passenger";
                bankSystem.SyncBankBalance(worker);

                var timers = GetPrivateField<Dictionary<NetUserId, float>>(bankSystem, "_payrollTimers");
                timers[serverSession.UserId] = BankSystem.PayrollIntervalSeconds - 0.01f;
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(bankSystem.TryGetBalance(worker, out var balance), Is.True);
                Assert.That(balance, Is.EqualTo(HumanoidCharacterProfile.SectorPioneerGrant + BankSystem.PayrollMinimumHourly));
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task DurableBankDepositRollsBackBeforeWorldRetry()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            attachedSession = serverSession;
            var entMan = server.ResolveDependency<IEntityManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var bankSystem = entMan.System<BankSystem>();
            var testMap = await pair.CreateTestMap();

            await SelectFreshBankProfile(server, serverSession, "Deposit rollback fixture");

            EntityUid worker = default;
            await server.WaitPost(() =>
            {
                worker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                AttachBankBody(entMan, playerMan, serverSession, worker);
                entMan.EnsureComponent<BankAccountComponent>(worker);
                bankSystem.SyncBankBalance(worker);
            });

            await pair.RunTicksSync(2);

            Task<bool>? rejectedTask = null;
            await server.WaitPost(() =>
            {
                rejectedTask = bankSystem.TryBankDepositAsync(
                    worker,
                    5_000,
                    tax: false,
                    finalizeAfterCommit: () => false);
            });
            var rejected = await rejectedTask!;
            var afterRejected = await db.GetPlayerPreferencesAsync(serverSession.UserId, default);
            Assert.That(afterRejected, Is.Not.Null);
            var rejectedProfile = (HumanoidCharacterProfile) afterRejected!.SelectedCharacter!;

            var finalizerCalls = 0;
            Task<bool>? acceptedTask = null;
            await server.WaitPost(() =>
            {
                acceptedTask = bankSystem.TryBankDepositAsync(
                    worker,
                    5_000,
                    tax: false,
                    finalizeAfterCommit: () =>
                    {
                        finalizerCalls++;
                        return true;
                    });
            });
            var accepted = await acceptedTask!;
            var afterAccepted = await db.GetPlayerPreferencesAsync(serverSession.UserId, default);
            Assert.That(afterAccepted, Is.Not.Null);
            var acceptedProfile = (HumanoidCharacterProfile) afterAccepted!.SelectedCharacter!;

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.False);
                Assert.That(rejectedProfile.BankBalance, Is.EqualTo(HumanoidCharacterProfile.SectorPioneerGrant));
                Assert.That(bankSystem.IsBankOperationPending(worker), Is.False);
                Assert.That(accepted, Is.True);
                Assert.That(finalizerCalls, Is.EqualTo(1));
                Assert.That(acceptedProfile.BankBalance,
                    Is.EqualTo(HumanoidCharacterProfile.SectorPioneerGrant + 5_000));
                Assert.That(bankSystem.TryGetBalance(worker, out var runtimeBalance), Is.True);
                Assert.That(runtimeBalance,
                    Is.EqualTo(HumanoidCharacterProfile.SectorPioneerGrant + 5_000));
            });
        }
        finally
        {
            await DetachAttachedSession(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public void NewCharacterProfilesStartWithSectorPioneerGrant()
    {
        var profile = new HumanoidCharacterProfile();

        Assert.That(profile.BankBalance, Is.EqualTo(HumanoidCharacterProfile.SectorPioneerGrant));
        Assert.That(profile.BankBalance, Is.EqualTo(HumanoidCharacterProfile.DefaultBalance));
    }

    private static async Task<(EntityUid PdaUid, PdaComponent Pda)> OpenPdaAndLoadBankIdentity(
        Content.IntegrationTests.Pair.TestPair pair,
        PdaSystem pdaSystem,
        IEntityManager entMan,
        EntityUid actor,
        EntityCoordinates coordinates,
        ICommonSession session)
    {
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var preferences = pair.Server.ResolveDependency<IServerPreferencesManager>();
        var userDb = pair.Server.ResolveDependency<UserDbDataManager>();
        var playerManager = pair.Server.ResolveDependency<IPlayerManager>();

        // The preference cache becomes visible before every database-backed login
        // callback has completed. Wait for the authoritative lifecycle task so this
        // fixture cannot open the PDA against a half-loaded recycled session.
        await userDb.WaitLoadComplete(session).WaitAsync(TimeSpan.FromSeconds(10));

        // Other integration fixtures can persist a different selected profile while
        // the pooled server keeps its old preference cache. Reconcile the cache before
        // asking PDA registration to validate the slot's character name against DB.
        await preferences.RefreshPreferencesAsync(session, default);

        // Recycled connected pairs can still be catching up after a test manually
        // transfers the session to a fresh actor. Opening the PDA before the
        // session/actor/bank-account triad is authoritative races the asynchronous
        // PDA bank loader against stale login state and makes unrelated production
        // gates flaky.
        var actorReady = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await pair.Server.WaitPost(() =>
            {
                actorReady = session.AttachedEntity == actor &&
                             playerManager.TryGetSessionByEntity(actor, out var actorSession) &&
                             ReferenceEquals(actorSession, session) &&
                             entMan.HasComponent<BankAccountComponent>(actor);
            });

            if (actorReady)
                break;

            await pair.RunTicksSync(2);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        Assert.That(actorReady, Is.True, "PDA bank test actor was not fully attached before opening PDA.");

        var slot = -1;
        var profileId = -1;
        await pair.Server.WaitPost(() =>
        {
            slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
            Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out profileId), Is.True);
        });

        var databaseProfileId = await db.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(databaseProfileId, Is.EqualTo(profileId));

        EntityUid pdaUid = default;
        PdaComponent? pda = null;
        await pair.Server.WaitPost(() =>
        {
            pdaUid = entMan.SpawnEntity("PassengerPDA", coordinates);
            Assert.That(entMan.TryGetComponent(pdaUid, out pda), Is.True);
            Assert.That(pda, Is.Not.Null);
            pdaSystem.UpdatePdaUi(pdaUid, pda, actor);
        });

        // The load combines server-thread continuations with real asynchronous DB
        // work. Advancing ticks alone can outrun that I/O under the fixture's
        // normal parallel load, so give both schedulers a bounded chance to run.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await pair.RunTicksSync(2);
            await Task.Delay(TimeSpan.FromMilliseconds(10));

            var loaded = false;
            await pair.Server.WaitPost(() =>
            {
                loaded = HasActivePdaBankIdentity(pdaSystem, session.UserId, slot, profileId);
                if (!loaded)
                    pdaSystem.UpdatePdaUi(pdaUid, pda, actor);
            });

            if (loaded)
                return (pdaUid, pda!);
        }

        string diagnostic = string.Empty;
        await pair.Server.WaitPost(() =>
        {
            var playerManager = pair.Server.ResolveDependency<IPlayerManager>();
            var cached = preferences.TryGetCachedPreferences(session.UserId, out var currentPreferences);
            var selectedType = currentPreferences?.SelectedCharacter?.GetType().Name ?? "null";
            var attached = session.AttachedEntity?.ToString() ?? "null";
            var sessionMatchesActor = playerManager.TryGetSessionByEntity(actor, out var actorSession) &&
                                      ReferenceEquals(actorSession, session);
            var hasBank = entMan.HasComponent<BankAccountComponent>(actor);
            var loads = (System.Collections.IDictionary) GetPrivateField<object>(
                pdaSystem,
                "_bankStateLoadsInFlight");
            var retries = (System.Collections.IDictionary) GetPrivateField<object>(
                pdaSystem,
                "_bankStateRetryAfter");
            var active = (System.Collections.IDictionary) GetPrivateField<object>(
                pdaSystem,
                "_activeBankProfiles");
            var registered = (System.Collections.IDictionary) GetPrivateField<object>(
                pdaSystem,
                "_registeredBankAccountIds");

            diagnostic = $"cached={cached}, selected={selectedType}, attached={attached}, " +
                         $"actor={actor}, sessionMatchesActor={sessionMatchesActor}, hasBank={hasBank}, " +
                         $"generation={preferences.GetCharacterSlotGeneration(session.UserId, slot)}, " +
                         $"loads={loads.Count}, retries={retries.Count}, active={active.Count}, " +
                         $"registered={registered.Count}";
        });

        throw new AssertionException(
            $"PDA bank identity did not load for {session.UserId}:{slot}/{profileId}; {diagnostic}");
    }

    private static bool HasActivePdaBankIdentity(
        PdaSystem pdaSystem,
        NetUserId userId,
        int slot,
        int profileId)
    {
        return GetActivePdaBankIdentity(pdaSystem, userId, slot, profileId) != null;
    }

    private static object? GetActivePdaBankIdentity(
        PdaSystem pdaSystem,
        NetUserId userId,
        int slot,
        int profileId)
    {
        var identities = (System.Collections.IEnumerable) GetPrivateField<object>(
            pdaSystem,
            "_activeBankProfiles");
        foreach (var entry in identities)
        {
            var value = entry.GetType().GetProperty("Value")?.GetValue(entry);
            if (value == null)
                continue;

            var type = value.GetType();
            if (Equals(type.GetProperty("UserId")?.GetValue(value), userId) &&
                Equals(type.GetProperty("Slot")?.GetValue(value), slot) &&
                Equals(type.GetProperty("ProfileId")?.GetValue(value), profileId))
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasProfileIdentityKey(object instance, string fieldName, int profileId)
    {
        var entries = (System.Collections.IEnumerable) GetPrivateField<object>(instance, fieldName);
        foreach (var entry in entries)
        {
            var key = entry.GetType().GetProperty("Key")?.GetValue(entry) ?? entry;
            if (Equals(key.GetType().GetProperty("ProfileId")?.GetValue(key), profileId))
                return true;
        }

        return false;
    }

    private static bool PrivateDictionaryContainsKey(object instance, string fieldName, object key)
    {
        var dictionary = (System.Collections.IDictionary) GetPrivateField<object>(instance, fieldName);
        return dictionary.Contains(key);
    }

    private static object ArtificiallyBlockBankProfile(
        BankSystem bankSystem,
        NetUserId userId,
        int profileId)
    {
        var identityType = typeof(BankSystem).GetNestedType(
            "BankProfileIdentity",
            BindingFlags.NonPublic);
        Assert.That(identityType, Is.Not.Null);
        var identity = Activator.CreateInstance(
            identityType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { userId, profileId },
            culture: null);
        Assert.That(identity, Is.Not.Null);

        var blocked = GetPrivateField<object>(bankSystem, "_blockedBalanceMutations");
        var add = blocked.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
        Assert.That(add, Is.Not.Null);
        Assert.That(add!.Invoke(blocked, new[] { identity }), Is.EqualTo(true));
        return identity!;
    }

    private static bool PrivateSetContains(object instance, string fieldName, object value)
    {
        var set = GetPrivateField<object>(instance, fieldName);
        var contains = set.GetType().GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance);
        Assert.That(contains, Is.Not.Null);
        return (bool) contains!.Invoke(set, new[] { value })!;
    }

    private static void AddPrivateSetValue(object instance, string fieldName, object value)
    {
        var set = GetPrivateField<object>(instance, fieldName);
        var add = set.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
        Assert.That(add, Is.Not.Null);
        Assert.That(add!.Invoke(set, new[] { value }), Is.EqualTo(true));
    }

    private static void SetPrivateDictionaryEntry(object instance, string fieldName, object key, object value)
    {
        var dictionary = (System.Collections.IDictionary) GetPrivateField<object>(instance, fieldName);
        dictionary[key] = value;
    }

    private static void RemovePrivateDictionaryEntry(object instance, string fieldName, object key)
    {
        var dictionary = (System.Collections.IDictionary) GetPrivateField<object>(instance, fieldName);
        Assert.That(dictionary.Contains(key), Is.True, $"Missing key in private dictionary {fieldName}");
        dictionary.Remove(key);
    }

    private static void InvokePrivateVoid(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"Missing private instance method {instance.GetType().Name}.{methodName}");
        method!.Invoke(instance, args);
    }

    private static TResult InvokePrivate<TResult>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"Missing private instance method {instance.GetType().Name}.{methodName}");
        return (TResult) method!.Invoke(instance, args)!;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
        field!.SetValue(instance, value);
    }

    private static object CreatePrivateNestedInstance(Type owner, string nestedTypeName, params object[] args)
    {
        var nestedType = owner.GetNestedType(nestedTypeName, BindingFlags.NonPublic);
        Assert.That(nestedType, Is.Not.Null, $"Missing private nested type {owner.Name}.{nestedTypeName}");
        var instance = Activator.CreateInstance(
            nestedType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null);
        Assert.That(instance, Is.Not.Null, $"Could not create private nested type {owner.Name}.{nestedTypeName}");
        return instance!;
    }

    private static HumanoidCharacterProfile NewBankProfile(string name, int bankBalance)
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

    private static async Task SelectFreshBankProfile(
        RobustIntegrationTest.ServerIntegrationInstance server,
        ICommonSession session,
        string name)
    {
        var db = server.ResolveDependency<IServerDbManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var durable = await db.GetPlayerPreferencesSnapshotAsync(session.UserId);
        Assert.That(durable, Is.Not.Null);

        var slot = Enumerable.Range(0, 30)
            .First(candidate => !durable!.Preferences.Characters.ContainsKey(candidate));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await db.SaveCharacterSlotAsync(
            session.UserId,
            NewBankProfile($"{name} {suffix}",
                HumanoidCharacterProfile.SectorPioneerGrant),
            slot);
        await db.SaveSelectedCharacterIndexAsync(session.UserId, slot);

        Task? refresh = null;
        await server.WaitPost(() => refresh = preferences.RefreshPreferencesAsync(session, default));
        await refresh!;
        Assert.That(preferences.GetPreferences(session.UserId).SelectedCharacterIndex, Is.EqualTo(slot));
    }

    private static void AttachBankBody(
        IEntityManager entityManager,
        IPlayerManager playerManager,
        ICommonSession session,
        EntityUid body)
    {
        var minds = entityManager.System<Content.Server.Mind.MindSystem>();
        var mind = minds.TryGetMind(session.UserId, out var mindId, out var mindComponent)
            ? new Entity<Content.Shared.Mind.MindComponent>(mindId.Value, mindComponent)
            : minds.CreateMind(session.UserId, nameof(LuaMBankAndPdaContractsTest));
        minds.TransferTo(mind, body, createGhost: false, mind: mind.Comp);
        playerManager.SetAttachedEntity(session, body, true);
    }

    private static async Task DetachAttachedSession(
        Content.IntegrationTests.Pair.TestPair pair,
        ICommonSession? session)
    {
        if (session == null)
            return;

        await pair.Server.WaitPost(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(session, null, true);
        });
        await pair.RunTicksSync(2);
    }

    private static TResult InvokePrivateStatic<TResult>(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, $"Missing private static method {type.Name}.{methodName}");
        return (TResult) method!.Invoke(null, args)!;
    }

    private static async Task<object?> InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"Missing private instance method {instance.GetType().Name}.{methodName}");
        var task = (Task) method!.Invoke(instance, args)!;
        await task;
        return task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
    }

    private static T GetPrivateProperty<T>(object? instance, string propertyName)
    {
        Assert.That(instance, Is.Not.Null, $"Expected {propertyName} holder instance to exist");
        var property = instance!.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.That(property, Is.Not.Null, $"Missing private result property {propertyName}");
        return (T) property!.GetValue(instance)!;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
        return (T) field!.GetValue(instance)!;
    }

    [Virtual]
    public class FailingPreferenceWriteProxy : DispatchProxy
    {
        private readonly object _lock = new();
        private TaskCompletionSource<PlayerPreferencesSnapshot?>? _refresh;
        private PlayerPreferencesSnapshot? _snapshot;

        public IServerDbManager Inner { get; set; } = default!;

        public TaskCompletionSource<bool> WriteAttempted { get; private set; } = default!;
        public TaskCompletionSource<bool> RefreshReadStarted { get; private set; } = default!;

        public Task RefreshCompleted
        {
            get
            {
                lock (_lock)
                    return _refresh?.Task ?? Task.CompletedTask;
            }
        }

        public void Arm(PlayerPreferencesSnapshot snapshot)
        {
            lock (_lock)
            {
                _snapshot = snapshot;
                WriteAttempted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                RefreshReadStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _refresh = new TaskCompletionSource<PlayerPreferencesSnapshot?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseRefresh()
        {
            TaskCompletionSource<PlayerPreferencesSnapshot?>? refresh;
            PlayerPreferencesSnapshot? snapshot;
            lock (_lock)
            {
                refresh = _refresh;
                snapshot = _snapshot;
            }

            if (refresh != null && snapshot != null)
                refresh.TrySetResult(snapshot);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var methodName = targetMethod?.Name;
            if (methodName is nameof(IServerDbManager.SaveCharacterSlotAsync) or
                nameof(IServerDbManager.SaveSelectedCharacterIndexAsync) or
                nameof(IServerDbManager.DeleteSlotAndSetSelectedIndex))
            {
                WriteAttempted.TrySetResult(true);
                return Task.FromException(new IOException(
                    $"Injected ambiguous preference write failure in {methodName}."));
            }

            if (methodName == nameof(IServerDbManager.GetPlayerPreferencesSnapshotAsync))
            {
                RefreshReadStarted.TrySetResult(true);
                lock (_lock)
                    return _refresh!.Task;
            }

            return targetMethod!.Invoke(Inner, args);
        }
    }

}
