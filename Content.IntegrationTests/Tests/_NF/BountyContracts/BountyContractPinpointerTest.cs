using System.Linq;
using Content.Server._NF.BountyContracts;
using Content.Server._NF.SectorServices;
using Content.Server.Mind;
using Content.Shared._NF.BountyContracts;
using Content.Shared.MassMedia.Components;
using Content.Shared.Pinpointer;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._NF.BountyContracts;

[TestFixture]
public sealed class BountyContractPinpointerTest
{
    [Test]
    public async Task AcceptingRouteContractIssuesActivePinpointer()
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
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var entMan = server.ResolveDependency<IEntityManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var mindSystem = entMan.System<MindSystem>();
            var bountyContracts = entMan.System<BountyContractSystem>();
            var metaData = entMan.System<MetaDataSystem>();
            var mapSystem = entMan.System<SharedMapSystem>();

            var testMap = await pair.CreateTestMap();
            EntityUid actor = default;
            EntityUid host = default;
            var beforePinpointers = 0;
            uint contractId = 0;

            await server.WaitPost(() =>
            {
                host = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                entMan.AddComponent<StationSectorServiceHostComponent>(host);
                entMan.AddComponent<SectorNewsComponent>(host);
                entMan.AddComponent<BountyContractDataComponent>(host);

                metaData.SetEntityName(testMap.Grid, "Test Contract Vessel");

                actor = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(clientSession!.UserId, "BountyContractPinpointerTest");
                mindSystem.TransferTo(mind, actor);
                playerMan.SetAttachedEntity(clientSession, actor);
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                beforePinpointers = entMan.AllComponents<PinpointerComponent>().Count();
            });

            await server.WaitPost(() =>
            {
                var contract = bountyContracts.TryCreateGeneratedBountyContract(
                    "Distress",
                    BountyContractCategory.Service,
                    "Test Contract",
                    30000,
                    host,
                    vessel: "Test Contract Vessel",
                    description: "Fly to the vessel and close the task.");

                Assert.That(contract, Is.Not.Null);
                contractId = contract!.ContractId;
            });

            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                Assert.That(bountyContracts.TrySetBountyContractAccepted(host, actor, contractId, accepted: true), Is.True);
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                var pinpointers = entMan.AllComponents<PinpointerComponent>().Select(component => component.Uid).ToList();
                Assert.That(pinpointers, Has.Count.EqualTo(beforePinpointers + 1));

                var pinpointerUid = pinpointers.Single(uid => entMan.GetComponent<PinpointerComponent>(uid).Target.HasValue);
                var pinpointer = entMan.GetComponent<PinpointerComponent>(pinpointerUid);

                Assert.That(pinpointer.IsActive, Is.True);
                Assert.That(pinpointer.Target.HasValue, Is.True);
                Assert.That(pinpointer.Target!.Value == testMap.Grid.Owner, Is.True);
            });

            await server.WaitPost(() =>
            {
                mapSystem.DeleteMap(testMap.MapId);
                entMan.DeleteEntity(host);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
