using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server.NPC.Pathfinding;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(PathfindingSystem))]
public sealed class LuaMPathfindingCancellationTest
{
    [Test]
    public async Task CanceledQueuedRequestCompletesAsCanceledAndDoesNotBlockNextRoute()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var pathfinding = entities.System<PathfindingSystem>();
        var map = await pair.CreateTestMap();
        using var cancellation = new CancellationTokenSource();

        Task<PathResultEvent> canceledTask = default!;
        EntityUid agent = default;
        EntityCoordinates start = default;
        await server.WaitAssertion(() =>
        {
            start = new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f));
            agent = entities.SpawnEntity("MobHuman", start);
            var end = new EntityCoordinates(map.Grid.Owner, new Vector2(30.5f, 0.5f));
            canceledTask = pathfinding.GetPath(agent, start, end, 0f, cancellation.Token);
            cancellation.Cancel();
        });

        await pair.RunTicksSync(2);

        Assert.That(canceledTask, Is.Not.Null);
        Assert.That(canceledTask.IsCanceled, Is.True);
        Assert.CatchAsync<OperationCanceledException>(async () => await canceledTask);

        Task<PathResultEvent> nextTask = default!;
        await server.WaitAssertion(() =>
        {
            nextTask = pathfinding.GetPath(agent, start, start, 0f, CancellationToken.None);
        });

        await pair.RunTicksSync(2);

        Assert.That(nextTask, Is.Not.Null);
        Assert.That(nextTask.IsCompletedSuccessfully, Is.True);
        Assert.That((await nextTask).Result, Is.EqualTo(PathResult.Path));

        await pair.CleanReturnAsync();
    }
}
