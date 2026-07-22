#nullable enable

using Content.Server._Mono.MonoCoins;
using Content.Server.Database;
using Content.Shared.Preferences;
using NUnit.Framework;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMMonoCoinsTransferPersistenceTest
{
    [Test]
    public async Task AtomicTransferIsConservativeReplayableAndAcknowledgedIdempotently()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var sender = NewUserId();
        var recipient = NewUserId();
        var operationId = Guid.NewGuid();

        await SeedAsync(db, sender, "Mono Sender", 1_000L);
        await SeedAsync(db, recipient, "Mono Recipient", 2_000L);

        var first = await db.TransferMonoCoinsAsync(sender, recipient, 400L, operationId);
        var replay = await db.TransferMonoCoinsAsync(sender, recipient, 400L, operationId);
        var changed = await db.TransferMonoCoinsAsync(sender, recipient, 401L, operationId);
        var pending = await db.GetUnacknowledgedMonoCoinsTransferAsync(sender);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(MonoCoinsTransferStatus.Success));
            Assert.That(first.AlreadyProcessed, Is.False);
            Assert.That(replay.Status, Is.EqualTo(MonoCoinsTransferStatus.Success));
            Assert.That(replay.AlreadyProcessed, Is.True);
            Assert.That(changed.Status, Is.EqualTo(MonoCoinsTransferStatus.OperationConflict));
            Assert.That(changed.AlreadyProcessed, Is.True);
            Assert.That(pending?.OperationId, Is.EqualTo(operationId));
            Assert.That(pending?.SenderBalanceBefore, Is.EqualTo(1_000L));
            Assert.That(pending?.SenderBalanceAfter, Is.EqualTo(600L));
            Assert.That(pending?.RecipientBalanceBefore, Is.EqualTo(2_000L));
            Assert.That(pending?.RecipientBalanceAfter, Is.EqualTo(2_400L));
        });

        Assert.That(await db.GetMonoCoinsAsync(sender), Is.EqualTo(600L));
        Assert.That(await db.GetMonoCoinsAsync(recipient), Is.EqualTo(2_400L));
        Assert.That(await db.AcknowledgeMonoCoinsTransferAsync(sender, operationId), Is.True);
        Assert.That(await db.AcknowledgeMonoCoinsTransferAsync(sender, operationId), Is.True,
            "Acknowledgement retry must be idempotent after an uncertain response.");
        Assert.That(await db.GetUnacknowledgedMonoCoinsTransferAsync(sender), Is.Null);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManagerRecoversCommittedCommandUnderOriginalOperationIdWithoutDoubleDebit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var coins = pair.Server.ResolveDependency<MonoCoinsManager>();
        var sender = NewUserId();
        var recipient = NewUserId();
        var committedOperationId = Guid.NewGuid();

        await SeedAsync(db, sender, "Recovery Sender", 900L);
        await SeedAsync(db, recipient, "Recovery Recipient", 100L);

        // Simulate process death after COMMIT and before command acknowledgement.
        var committed = await db.TransferMonoCoinsAsync(
            sender,
            recipient,
            250L,
            committedOperationId);
        Assert.That(committed.Success, Is.True);

        var recovered = await coins.TransferMonoCoinsAsync(
            sender,
            recipient,
            250L,
            Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(recovered.Status, Is.EqualTo(MonoCoinsTransferStatus.Success));
            Assert.That(recovered.AlreadyProcessed, Is.True);
            Assert.That(recovered.OperationId, Is.EqualTo(committedOperationId));
            Assert.That(recovered.SenderBalance, Is.EqualTo(650L));
            Assert.That(recovered.RecipientBalance, Is.EqualTo(350L));
        });
        Assert.That(await db.GetMonoCoinsAsync(sender), Is.EqualTo(650L));
        Assert.That(await db.GetMonoCoinsAsync(recipient), Is.EqualTo(350L));

        Assert.That(
            await coins.AcknowledgeMonoCoinsTransferAsync(sender, recovered.OperationId),
            Is.True);
        var next = await coins.TransferMonoCoinsAsync(
            sender,
            recipient,
            50L,
            Guid.NewGuid());
        Assert.Multiple(() =>
        {
            Assert.That(next.Success, Is.True);
            Assert.That(next.SenderBalance, Is.EqualTo(600L));
            Assert.That(next.RecipientBalance, Is.EqualTo(400L));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PendingOutcomeAndRecipientOverflowNeverLeaveAPartialDebit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var sender = NewUserId();
        var recipient = NewUserId();
        var firstOperationId = Guid.NewGuid();

        await SeedAsync(db, sender, "Fence Sender", 1_000L);
        await SeedAsync(db, recipient, "Fence Recipient", 500L);

        var first = await db.TransferMonoCoinsAsync(sender, recipient, 100L, firstOperationId);
        var fenced = await db.TransferMonoCoinsAsync(
            sender,
            recipient,
            200L,
            Guid.NewGuid());
        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.True);
            Assert.That(fenced.Status, Is.EqualTo(MonoCoinsTransferStatus.PendingOperation));
            Assert.That(fenced.OperationId, Is.EqualTo(firstOperationId));
        });
        Assert.That(await db.GetMonoCoinsAsync(sender), Is.EqualTo(900L));
        Assert.That(await db.GetMonoCoinsAsync(recipient), Is.EqualTo(600L));

        Assert.That(await db.AcknowledgeMonoCoinsTransferAsync(sender, firstOperationId), Is.True);
        await db.SetMonoCoinsAsync(recipient, long.MaxValue - 5L);
        var overflow = await db.TransferMonoCoinsAsync(
            sender,
            recipient,
            10L,
            Guid.NewGuid());
        Assert.Multiple(() =>
        {
            Assert.That(overflow.Status, Is.EqualTo(MonoCoinsTransferStatus.RecipientOverflow));
            Assert.That(overflow.SenderBalance, Is.EqualTo(900L));
            Assert.That(overflow.RecipientBalance, Is.EqualTo(long.MaxValue - 5L));
        });
        Assert.That(await db.GetMonoCoinsAsync(sender), Is.EqualTo(900L));
        Assert.That(await db.GetMonoCoinsAsync(recipient), Is.EqualTo(long.MaxValue - 5L));

        await pair.CleanReturnAsync();
    }

    private static async Task SeedAsync(
        IServerDbManager db,
        NetUserId userId,
        string name,
        long coins)
    {
        await db.InitPrefsAsync(userId, NewProfile(name), default);
        await db.SetMonoCoinsAsync(userId, coins);
    }

    private static HumanoidCharacterProfile NewProfile(string name)
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
        };
    }

    private static NetUserId NewUserId()
    {
        return new NetUserId(Guid.NewGuid());
    }
}
