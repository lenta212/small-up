using System.Linq;
using Content.Server._LuaM.Sector;
using Content.Server.Mind;
using Content.Shared.Mind;
using Content.Shared.Pinpointer;
using Content.Shared.Teleportation.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Spawners;
using Robust.Server.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMSubspaceRiftTest
{
    [Test]
    public async Task ApplySubspaceRiftCreatesLinkedPortalPairAndPinpointer()
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
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var testMap = await pair.CreateTestMap();

            await server.WaitPost(() =>
            {
                var player = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMSubspaceRiftTest");
                mindSystem.TransferTo(mind, player);
                playerMan.SetAttachedEntity(serverSession, player);
            });

            await pair.RunTicksSync(5);

            var beforePortals = 0;
            var beforePinpointers = 0;
            await server.WaitAssertion(() =>
            {
                beforePortals = entMan.AllComponents<PortalComponent>().Count();
                beforePinpointers = entMan.AllComponents<PinpointerComponent>().Count();
            });

            string result = string.Empty;
            await server.WaitPost(() =>
            {
                result = director.ApplySubspaceRiftAroundTarget(
                    serverSession.UserId.ToString(),
                    "LuaMSubspaceRiftTest",
                    "stargate",
                    announce: false);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(result, Does.Contain("Subspace rift opened"));

                var portals = entMan.AllComponents<PortalComponent>().Select(component => component.Uid).ToList();
                Assert.That(portals, Has.Count.EqualTo(beforePortals + 2));

                var bluePortal = portals.Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "PortalGatewayBlue");
                var orangePortal = portals.Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "PortalGatewayOrange");

                Assert.That(entMan.HasComponent<TimedDespawnComponent>(bluePortal), Is.True);
                Assert.That(entMan.HasComponent<TimedDespawnComponent>(orangePortal), Is.True);
                Assert.That(entMan.GetComponent<TimedDespawnComponent>(bluePortal).Lifetime, Is.InRange(299f, 300f));
                Assert.That(entMan.GetComponent<TimedDespawnComponent>(orangePortal).Lifetime, Is.InRange(299f, 300f));

                var pinpointers = entMan.AllComponents<PinpointerComponent>().Select(component => component.Uid).ToList();
                Assert.That(pinpointers, Has.Count.EqualTo(beforePinpointers + 1));
                var pinpointerUid = pinpointers.Single(uid => entMan.GetComponent<PinpointerComponent>(uid).Target.HasValue);
                var pinpointer = entMan.GetComponent<PinpointerComponent>(pinpointerUid);

                Assert.That(pinpointer.IsActive, Is.True);
                Assert.That(pinpointer.Target, Is.EqualTo(bluePortal));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
