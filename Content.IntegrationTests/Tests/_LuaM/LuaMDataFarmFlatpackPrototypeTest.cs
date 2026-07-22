using Content.Shared.Construction.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMDataFarmFlatpackPrototypeTest
{
    [Test]
    public async Task DataFarmBoardsCanBeInsertedIntoFlatpacker()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            AssertFlatpackableBoard(prototypes, components, "DataFarmResearchCircuitboard", "DatafarmResearch");
            AssertFlatpackableBoard(prototypes, components, "DataFarmCryptoCircuitboard", "DatafarmCrypto");
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertFlatpackableBoard(
        IPrototypeManager prototypes,
        IComponentFactory components,
        string boardId,
        string machineId)
    {
        var board = prototypes.Index<EntityPrototype>(boardId);
        Assert.That(board.TryGetComponent<MachineBoardComponent>(out var machineBoard, components), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(machineBoard.Prototype, Is.EqualTo(machineId));
            Assert.That(machineBoard.Flatpackable, Is.True,
                $"{boardId} must be accepted by FlatpackCreator insertion validation.");
        });
    }
}
