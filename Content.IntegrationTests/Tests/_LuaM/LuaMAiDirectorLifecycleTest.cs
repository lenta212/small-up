#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Content.Server._LuaM.Sector;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMAiDirectorLifecycleTest
{
    [Test]
    public async Task RunLevelAndCleanupEventsResetRoundScopedStateWithoutClearingRollingBudget()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() =>
            {
                var cooldowns = GetPrivateField<IDictionary>(director, "_nextPlayerWorldActionByUser");
                var rollingBudget = GetPrivateField<Queue<TimeSpan>>(director, "_gatewayBudgetWindow");
                var blockReasons = GetPrivateField<Queue<string>>(director, "_gatewayRecentBlockReasons");
                cooldowns.Clear();
                rollingBudget.Clear();
                rollingBudget.Enqueue(TimeSpan.Zero);

                SeedRoundState(director, cooldowns, blockReasons, session!.UserId, budgetUsed: 3, roundActive: true);
                entMan.EventBus.RaiseEvent(
                    EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound));
                AssertRoundState(director, cooldowns, rollingBudget, blockReasons, expectedRoundActive: false);

                SeedRoundState(director, cooldowns, blockReasons, session.UserId, budgetUsed: 5, roundActive: false);
                entMan.EventBus.RaiseEvent(
                    EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound));
                AssertRoundState(director, cooldowns, rollingBudget, blockReasons, expectedRoundActive: true);

                SeedRoundState(director, cooldowns, blockReasons, session.UserId, budgetUsed: 7, roundActive: true);
                entMan.EventBus.RaiseEvent(
                    EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.InRound));
                Assert.Multiple(() =>
                {
                    Assert.That(GetPrivateField<int>(director, "_gatewayBudgetRoundUsed"), Is.EqualTo(7));
                    Assert.That(GetPrivateField<bool>(director, "_gatewayBudgetRoundActive"), Is.True);
                    Assert.That(cooldowns, Has.Count.EqualTo(1));
                    Assert.That(rollingBudget, Has.Count.EqualTo(1));
                    Assert.That(GetPrivateField<int>(director, "_gatewayAuditProviderOutputBlocks"), Is.EqualTo(2));
                    Assert.That(GetPrivateField<string>(director, "_gatewayLastBlockCategory"), Is.EqualTo("local validation rejected"));
                    Assert.That(blockReasons, Has.Count.EqualTo(1));
                });

                entMan.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
                AssertRoundState(director, cooldowns, rollingBudget, blockReasons, expectedRoundActive: false);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static void SeedRoundState(
        LuaMSectorAiDirectorSystem director,
        IDictionary cooldowns,
        Queue<string> blockReasons,
        NetUserId userId,
        int budgetUsed,
        bool roundActive)
    {
        SetPrivateField(director, "_gatewayBudgetRoundUsed", budgetUsed);
        SetPrivateField(director, "_gatewayBudgetRoundActive", roundActive);
        cooldowns.Clear();
        cooldowns[userId] = TimeSpan.MaxValue;
        SetPrivateField(director, "_gatewayAuditProviderOutputBlocks", 2);
        SetPrivateField(director, "_gatewayBlockLocalValidations", 2);
        SetPrivateField(director, "_gatewayOutcomeSequence", 11L);
        SetPrivateField(director, "_gatewayLastBlockSequence", 11L);
        SetPrivateField(director, "_gatewayLastBlockCategory", "local validation rejected");
        SetPrivateField(director, "_gatewayLastRequestShape", new[] { "old round request" });
        SetPrivateField(director, "_gatewayRagSourceShape", new[] { "old round source" });
        blockReasons.Clear();
        blockReasons.Enqueue("old round rejection");
    }

    private static void AssertRoundState(
        LuaMSectorAiDirectorSystem director,
        IDictionary cooldowns,
        Queue<TimeSpan> rollingBudget,
        Queue<string> blockReasons,
        bool expectedRoundActive)
    {
        Assert.Multiple(() =>
        {
            Assert.That(GetPrivateField<int>(director, "_gatewayBudgetRoundUsed"), Is.Zero);
            Assert.That(GetPrivateField<bool>(director, "_gatewayBudgetRoundActive"), Is.EqualTo(expectedRoundActive));
            Assert.That(cooldowns, Is.Empty);
            Assert.That(rollingBudget, Has.Count.EqualTo(1));
            Assert.That(GetPrivateField<int>(director, "_gatewayAuditProviderOutputBlocks"), Is.Zero);
            Assert.That(GetPrivateField<int>(director, "_gatewayBlockLocalValidations"), Is.Zero);
            Assert.That(GetPrivateField<long>(director, "_gatewayOutcomeSequence"), Is.Zero);
            Assert.That(GetPrivateField<long>(director, "_gatewayLastBlockSequence"), Is.Zero);
            Assert.That(GetPrivateField<string>(director, "_gatewayLastBlockCategory"), Is.Empty);
            Assert.That(GetPrivateField<string[]>(director, "_gatewayLastRequestShape"), Is.Empty);
            Assert.That(GetPrivateField<string[]>(director, "_gatewayRagSourceShape"), Is.Empty);
            Assert.That(blockReasons, Is.Empty);
        });
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");
        return (T) field!.GetValue(instance)!;
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");
        field!.SetValue(instance, value);
    }
}
