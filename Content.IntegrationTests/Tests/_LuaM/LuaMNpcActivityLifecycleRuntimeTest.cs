using System;
using Content.Server._LuaM.NPC;
using Content.Shared._LuaM.NPC;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMNpcActivityLifecycleSystem))]
public sealed class LuaMNpcActivityLifecycleRuntimeTest
{
    [Test]
    public async Task EntityCarrierUsesPrototypePolicyAndExpiresOnServerTick()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var lifecycle = entities.System<LuaMNpcActivityLifecycleSystem>();
        var map = await pair.CreateTestMap();

        EntityUid actor = default;
        uint replacementGeneration = 0;
        await server.WaitAssertion(() =>
        {
            actor = entities.SpawnEntity(null, map.MapCoords);
            var carrier = entities.EnsureComponent<LuaMNpcActivityComponent>(actor);
            carrier.RoleProfile = "LuaMSectorServiceWorkerLifecycle";

            Assert.That(
                lifecycle.BeginOrReplaceIntent(
                    actor,
                    "dispatching",
                    target: null,
                    destination: null,
                    out var first,
                    out var rejection),
                Is.True,
                rejection.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(first.Role, Is.EqualTo("sector-service-worker"));
                Assert.That(first.Activity, Is.EqualTo("dispatching"));
                Assert.That(first.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Active));
            });

            var firstGeneration = first.Generation;
            Assert.That(
                lifecycle.BeginOrReplaceIntent(
                    actor,
                    "dispatching",
                    target: null,
                    destination: null,
                    out var repeated,
                    out rejection),
                Is.True,
                rejection.ToString());
            Assert.That(repeated.Generation, Is.EqualTo(firstGeneration));

            Assert.That(
                lifecycle.BeginOrReplaceIntent(
                    actor,
                    "planning-route",
                    target: null,
                    destination: null,
                    out var replacement,
                    out rejection),
                Is.True,
                rejection.ToString());
            replacementGeneration = replacement.Generation;
            Assert.That(replacementGeneration, Is.GreaterThan(firstGeneration));

            Assert.That(
                lifecycle.Complete(actor, firstGeneration, out var staleSnapshot, out rejection),
                Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.StaleGeneration));
                Assert.That(staleSnapshot.Generation, Is.EqualTo(replacementGeneration));
                Assert.That(staleSnapshot.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Active));
            });

            carrier.Context.Deadline = timing.CurTime + TimeSpan.FromMilliseconds(1);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(lifecycle.GetSnapshot(actor, out var expired), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(expired.Generation, Is.EqualTo(replacementGeneration));
                Assert.That(expired.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Failed));
                Assert.That(expired.Failure, Is.EqualTo(LuaMNpcActivityLifecycleSystem.DeadlineExceededFailure));
                Assert.That(expired.Fallback, Is.EqualTo("standby"));
                Assert.That(
                    entities.GetComponent<LuaMNpcActivityComponent>(actor).LastLifecycleStatus,
                    Does.Contain("planning-route failed: deadline-exceeded"));
            });
        });

        await pair.CleanReturnAsync();
    }
}
