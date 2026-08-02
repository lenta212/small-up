using Content.Shared.ActionBlocker;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMIpcDeadShellRuntimeTest
{
    [Test]
    public async Task DeadIpcCanOnlySpeakWhileItsChassisRemainsInoperable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var blockers = entities.System<ActionBlockerSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var ipc = entities.SpawnEntity("MobIPC", map.GridCoords);
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(entities.HasComponent<DeadSpeechComponent>(ipc), Is.True);

            mobState.ChangeMobState(ipc, MobState.Dead);
            mobState.ChangeMobState(human, MobState.Dead);

            Assert.Multiple(() =>
            {
                Assert.That(blockers.CanSpeak(ipc), Is.True,
                    "The independent positronic brain must retain local and radio speech.");
                Assert.That(blockers.CanSpeak(human), Is.False,
                    "Dead speech must remain an IPC-specific exception.");
                Assert.That(blockers.CanMove(ipc), Is.False,
                    "The destroyed IPC chassis must not move.");
                Assert.That(blockers.CanInteract(ipc, human), Is.False,
                    "The destroyed IPC chassis must not interact with the world.");
            });
        });

        await pair.CleanReturnAsync();
    }
}
