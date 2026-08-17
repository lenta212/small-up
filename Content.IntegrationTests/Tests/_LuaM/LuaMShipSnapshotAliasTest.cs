#nullable enable
using Content.Server._LuaM.ShipPersistence;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipSnapshotAliasTest
{
    [Test]
    public async Task LegacyAliasesTargetExistingPrototypes()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var proto = server.ResolveDependency<IPrototypeManager>();

        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    foreach (var (legacyId, currentId) in
                        LuaMFullShipPersistenceSystem.LegacySnapshotPrototypeAliases)
                    {
                        Assert.That(
                            proto.HasIndex<EntityPrototype>(currentId),
                            Is.True,
                            $"Ship snapshot legacy alias '{legacyId}' targets missing entity '{currentId}'.");
                    }
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
