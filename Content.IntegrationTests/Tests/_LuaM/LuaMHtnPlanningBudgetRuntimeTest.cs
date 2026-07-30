using System;
using System.Linq;
using System.Reflection;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Shared.NPC;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Serilog.Events;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(HTNSystem))]
public sealed class LuaMHtnPlanningBudgetRuntimeTest
{
    private const string InjectedPlanningFailure = "LuaM injected planning failure";

    [Test]
    public async Task FaultedPlanningJobsRespectMaxUpdatesBudget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var htnSystem = entities.System<HTNSystem>();
        var map = await pair.CreateTestMap();
        var expectedFaultLogs = 0;

        bool JudgeExpectedPlanningFailure(string sawmillName, LogEvent message)
        {
            if (sawmillName != "system.htn" ||
                !message.RenderMessage().Contains(InjectedPlanningFailure, StringComparison.Ordinal))
            {
                return false;
            }

            expectedFaultLogs++;
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedPlanningFailure;
        try
        {
            await server.WaitAssertion(() =>
            {
                var npcs = new[]
                {
                    CreateFaultedNpc(entities, prototypes, map.MapCoords),
                    CreateFaultedNpc(entities, prototypes, map.MapCoords),
                };

                var count = 0;
                htnSystem.UpdateNPC(ref count, maxUpdates: 1, frameTime: 0.1f);

                Assert.Multiple(() =>
                {
                    Assert.That(npcs.Count(uid => entities.HasComponent<HTNComponent>(uid)), Is.EqualTo(1),
                        "Only one faulted planner may be contained when npc.max_updates is 1.");
                    Assert.That(npcs.Count(uid => entities.HasComponent<ActiveNPCComponent>(uid)), Is.EqualTo(1),
                        "Containing a faulted planner must also sleep exactly that NPC.");
                    Assert.That(expectedFaultLogs, Is.EqualTo(1));
                });

                htnSystem.UpdateNPC(ref count, maxUpdates: 1, frameTime: 0.1f);
                Assert.Multiple(() =>
                {
                    Assert.That(npcs.Any(uid => entities.HasComponent<HTNComponent>(uid)), Is.False,
                        "The remaining faulted planner must be processed on the next budget slice.");
                    Assert.That(npcs.Any(uid => entities.HasComponent<ActiveNPCComponent>(uid)), Is.False);
                    Assert.That(expectedFaultLogs, Is.EqualTo(2));
                });

                foreach (var npc in npcs)
                    entities.DeleteEntity(npc);
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedPlanningFailure;
            await pair.CleanReturnAsync();
        }
    }

    private static EntityUid CreateFaultedNpc(
        IEntityManager entities,
        IPrototypeManager prototypes,
        MapCoordinates coordinates)
    {
        var uid = entities.SpawnEntity(null, coordinates);
        var htn = entities.AddComponent<HTNComponent>(uid);
        if (!entities.HasComponent<ActiveNPCComponent>(uid))
            entities.AddComponent<ActiveNPCComponent>(uid);

        var job = new HTNPlanJob(
            maxTime: 0,
            protoManager: prototypes,
            rootTask: null!,
            blackboard: new NPCBlackboard(),
            branchTraversal: null);
        var exceptionField = job.GetType().BaseType?.GetField(
            "<Exception>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(exceptionField, Is.Not.Null);
        exceptionField!.SetValue(job, new InvalidOperationException(InjectedPlanningFailure));
        htn.PlanningJob = job;
        return uid;
    }
}
