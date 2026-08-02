using System.Linq;
using Content.Server._LuaM.Sector;
using Content.Server._NF.Bank;
using Content.Server._NF.BountyContracts;
using Content.Server._NF.SectorServices;
using Content.Server.Mind;
using Content.Shared._LuaM.Sector;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.MassMedia.Components;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMSectorContractObjectiveSystem))]
public sealed class LuaMSectorContractObjectiveTest
{
    private static readonly ResPath SectorMemoryPath = new("/luam/sector_memory.json");
    private static readonly ProtoId<LuaMSectorStoryPrototype> SilentTowStory = new("LuaMSectorStorySilentTow");

    [Test]
    public async Task AcceptedStoryCreatesExactItemAndPaysOnlyItsOwnerOnce()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var entMan = server.ResolveDependency<IEntityManager>();
            var resources = server.ResolveDependency<IResourceManager>();
            var players = server.ResolveDependency<IPlayerManager>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var maps = entMan.System<SharedMapSystem>();
            var transforms = entMan.System<SharedTransformSystem>();
            var minds = entMan.System<MindSystem>();
            var stories = entMan.System<LuaMSectorStorySystem>();
            var bounties = entMan.System<BountyContractSystem>();
            var evidenceSystem = entMan.System<LuaMSectorEvidenceSystem>();
            var bank = entMan.System<BankSystem>();

            var mapId = MapId.Nullspace;
            EntityUid host = default;
            EntityUid actor = default;

            await server.WaitPost(() =>
            {
                if (resources.UserData.Exists(SectorMemoryPath))
                    resources.UserData.Delete(SectorMemoryPath);
                SectorNewsComponent.Articles.Clear();

                maps.CreateMap(out mapId);
                host = entMan.SpawnEntity(null, new MapCoordinates(default, mapId));
                entMan.AddComponent<StationSectorServiceHostComponent>(host);
                entMan.AddComponent<SectorNewsComponent>(host);
                entMan.AddComponent<LuaMSectorLeadReportComponent>(host);

                actor = entMan.SpawnEntity("MobHuman", new MapCoordinates(default, mapId));
                entMan.EnsureComponent<BankAccountComponent>(actor);
                var mind = minds.CreateMind(session!.UserId, nameof(LuaMSectorContractObjectiveTest));
                minds.TransferTo(mind, actor);
                players.SetAttachedEntity(session, actor);
                bank.SyncBankBalance(actor);
            });

            await pair.RunTicksSync(10);

            uint contractId = default;
            int reward = default;
            int balanceBefore = default;
            await server.WaitAssertion(() =>
            {
                foreach (var story in prototypes.EnumeratePrototypes<LuaMSectorStoryPrototype>())
                {
                    if (story.ContractCollection == null ||
                        story.ID == LuaMSectorStorySystem.RescueAfterActionStoryId)
                        continue;

                    Assert.That(story.ContractObjectivePrototype, Is.Not.Null,
                        $"Contract story {story.ID} must declare a physical objective.");
                    Assert.That(prototypes.HasIndex<EntityPrototype>(story.ContractObjectivePrototype!.Value), Is.True,
                        $"Contract story {story.ID} references a missing objective prototype.");
                }

                Assert.That(stories.TryResetMemory(deletePersisted: true), Is.True);
                Assert.That(bank.TrySectorDeposit(
                    SectorBankAccount.Frontier,
                    100_000,
                    LedgerEntryType.TickingIncome), Is.True);
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                Assert.That(stories.TryGetActiveContractId(SilentTowStory, out contractId), Is.True);
                var contract = bounties.GetContracts("Distress")
                    .Single(entry => entry.ContractId == contractId);
                reward = contract.Reward;
                Assert.That(bank.TryGetBalance(actor, out balanceBefore), Is.True);
                Assert.That(bounties.TrySetBountyContractAccepted(host, actor, contractId, true), Is.True);
            });

            await pair.RunTicksSync(2);

            EntityUid objective = default;
            EntityUid unsignedCopy = default;
            await server.WaitAssertion(() =>
            {
                var objectives = entMan.AllComponents<LuaMSectorContractObjectiveComponent>()
                    .Where(component => component.Component.ContractId == contractId)
                    .ToList();
                Assert.That(objectives, Has.Count.EqualTo(1));
                objective = objectives.Single().Uid;

                var evidence = entMan.GetComponent<LuaMSectorEvidenceComponent>(objective);
                Assert.That(evidence.ContractId, Is.EqualTo(contractId));
                Assert.That(evidence.AuthorizedActor, Is.EqualTo(actor));
                Assert.That(evidence.ResolveStory, Is.True);
                Assert.That(evidence.RequireSectorTerminal, Is.True);

                var prototype = entMan.GetComponent<MetaDataComponent>(objective).EntityPrototype;
                Assert.That(prototype, Is.Not.Null);
                unsignedCopy = entMan.SpawnEntity(prototype!.ID, entMan.GetComponent<TransformComponent>(actor).Coordinates);
            });

            Assert.That(await evidenceSystem.TryFileEvidenceAndPayAsync(unsignedCopy, actor), Is.False,
                "An unsigned prototype copy must not replace the generated contract objective.");

            await server.WaitPost(() =>
            {
                var remoteCoordinates = new MapCoordinates(new System.Numerics.Vector2(20, 20), mapId);
                transforms.SetMapCoordinates(actor, remoteCoordinates);
                transforms.SetMapCoordinates(objective, remoteCoordinates);
            });

            Assert.That(await evidenceSystem.TryFileEvidenceAndPayAsync(objective, actor), Is.False,
                "A contract objective must not pay before terminal proximity is validated.");
            await server.WaitAssertion(() =>
            {
                Assert.That(bank.TryGetBalance(actor, out var remoteBalance), Is.True);
                Assert.That(remoteBalance, Is.EqualTo(balanceBefore));
                Assert.That(bounties.GetContracts("Distress").Any(entry => entry.ContractId == contractId), Is.True);
            });

            await server.WaitPost(() =>
            {
                transforms.SetMapCoordinates(actor, new MapCoordinates(default, mapId));
                transforms.SetCoordinates(objective, entMan.GetComponent<TransformComponent>(actor).Coordinates);
            });

            var frontierBalance = 0;
            await server.WaitAssertion(() =>
            {
                Assert.That(bank.TryGetBalance(SectorBankAccount.Frontier, out frontierBalance), Is.True);
                Assert.That(frontierBalance, Is.GreaterThan(0));
                Assert.That(bank.TrySectorWithdraw(
                    SectorBankAccount.Frontier,
                    frontierBalance,
                    LedgerEntryType.StationWithdrawalOther), Is.True);
            });

            Assert.That(await evidenceSystem.TryFileEvidenceAndPayAsync(objective, actor), Is.False,
                "An unfunded contract must remain retryable without crediting the character.");
            await server.WaitAssertion(() =>
            {
                Assert.That(bank.TryGetBalance(actor, out var unfundedBalance), Is.True);
                Assert.That(unfundedBalance, Is.EqualTo(balanceBefore));
                Assert.That(bounties.GetContracts("Distress").Any(entry => entry.ContractId == contractId), Is.True);
                Assert.That(entMan.Deleted(objective), Is.False);
                Assert.That(bank.TrySectorDeposit(
                    SectorBankAccount.Frontier,
                    frontierBalance,
                    LedgerEntryType.StationDepositOther), Is.True);
            });

            Assert.That(await evidenceSystem.TryFileEvidenceAndPayAsync(objective, actor), Is.True);
            await pair.RunTicksSync(3);

            await server.WaitAssertion(() =>
            {
                Assert.That(bank.TryGetBalance(actor, out var balanceAfter), Is.True);
                Assert.That(balanceAfter, Is.EqualTo(balanceBefore + reward));
                Assert.That(bounties.GetContracts("Distress").Any(entry => entry.ContractId == contractId), Is.False);
                Assert.That(stories.TryGetSectorMemory(out var records, out _, out _), Is.True);
                Assert.That(records.Single(entry => entry.Story == "LuaMSectorStorySilentTow").Resolved, Is.True);
                Assert.That(entMan.Deleted(objective), Is.True);
            });

            Assert.That(await evidenceSystem.TryFileEvidenceAndPayAsync(objective, actor), Is.False);
            await server.WaitAssertion(() =>
            {
                Assert.That(bank.TryGetBalance(actor, out var finalBalance), Is.True);
                Assert.That(finalBalance, Is.EqualTo(balanceBefore + reward), "The same objective must never pay twice.");
            });

            await server.WaitPost(() => maps.DeleteMap(mapId));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
