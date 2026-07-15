#nullable enable

using System.Collections;
using System.Reflection;
using Content.Server._LuaM.Sector;
using Content.Server.Chat.V2;
using Content.Server.Mind;
using Content.Shared.CCVar;
using Content.Shared.Chat.V2.Repository;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMLocalChatAiReceiveTest
{
    [Test]
    public async Task LocalChatOnlyRoutesExplicitAiRequests()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, true);
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();
            var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnChatMessageCreated",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing private OnChatMessageCreated method");

            EntityUid speaker = default;

            await server.WaitPost(() =>
            {
                speaker = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMLocalChatAiReceiveTest");
                mindSystem.TransferTo(mind, speaker);
                playerMan.SetAttachedEntity(serverSession, speaker);

                GetPrivateList(director, "_pendingPersonalPressures").Clear();
            });

            await pair.RunTicksSync(2);

            await server.WaitPost(() =>
            {
                method!.Invoke(
                    director,
                    new object[] { new MessageCreatedEvent(new LocalChatCreatedEvent(speaker, "mission", 10f)) });
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(GetPrivateList(director, "_pendingPersonalPressures"), Has.Count.EqualTo(0));
            });

            await server.WaitPost(() =>
            {
                method!.Invoke(
                    director,
                    new object[] { new MessageCreatedEvent(new LocalChatCreatedEvent(speaker, "AI, mission", 10f)) });
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(GetPrivateList(director, "_pendingPersonalPressures"), Has.Count.EqualTo(1));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static IList GetPrivateList(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");

        return (IList) field!.GetValue(instance)!;
    }
}
