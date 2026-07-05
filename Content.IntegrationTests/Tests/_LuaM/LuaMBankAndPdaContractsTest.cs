#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NF.CryoSleep;
using Content.Server.PDA;
using Content.Server.Preferences.Managers;
using Content.Server._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Preferences;
using Content.Shared.PDA;
using NUnit.Framework;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Server.Player;
using Robust.Shared.Utility;

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

    [Test]
    public async Task PdaBankTransferByIdWorksForOfflineRecipient()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
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

            var recipientBankId = InvokePrivateStatic<string>(
                typeof(PdaSystem),
                "BuildPdaBankAccountId",
                recipientProfile.Name,
                "Receiver",
                recipientId.ToString());

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

            Assert.That(entMan.TryGetComponent(sender, out BankAccountComponent? bankAccount), Is.True);
            Assert.That(bankAccount, Is.Not.Null);

            var formattedRecipientBankId = $"{recipientBankId[..2].ToLowerInvariant()}-{recipientBankId[2..4]} {recipientBankId[4..]}";
            var result = await InvokePrivateAsync(
                pdaSystem,
                "TryRegisteredBankTransfer",
                sender,
                formattedRecipientBankId,
                5000);

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
    public async Task PdaBankTransferByIdMissingRecipientShowsRegistrationHint()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
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

            EntityUid senderPda = default;
            PdaComponent? pda = null;
            await server.WaitPost(() =>
            {
                senderPda = entMan.SpawnEntity("PassengerPDA", testMap.GridCoords);
                Assert.That(entMan.TryGetComponent(senderPda, out pda), Is.True);
                Assert.That(pda, Is.Not.Null);
            });

            var transfer = new PdaBankTransferMessage("ZZ-99999", 5000)
            {
                UiKey = PdaUiKey.Key,
                Actor = sender,
                Entity = entMan.GetNetEntity(senderPda),
            };

            await InvokePrivateAsync(
                pdaSystem,
                "HandleBankTransferMessageAsync",
                senderPda,
                pda!,
                transfer);

            await pair.RunTicksSync(2);

            var pdaComponent = pda ?? throw new InvalidOperationException("PDA component was not spawned.");

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
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
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
    public async Task PayrollDepositsHourlyRateWhenTimerElapses()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
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
    public void NewCharacterProfilesStartWithSectorPioneerGrant()
    {
        var profile = new HumanoidCharacterProfile();

        Assert.That(profile.BankBalance, Is.EqualTo(HumanoidCharacterProfile.SectorPioneerGrant));
        Assert.That(profile.BankBalance, Is.EqualTo(HumanoidCharacterProfile.DefaultBalance));
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

}
