using Content.Shared.Construction.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMMatterSynthesizerPrototypeTest
{
    [TestCase("LaserDrill")]
    [TestCase("StationLaserDrill")]
    public async Task MatterSynthesizerCanBeUnanchoredAndAnchored(string prototypeId)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var prototype = prototypes.Index<EntityPrototype>(prototypeId);
            Assert.That(
                prototype.TryGetComponent<AnchorableComponent>(out var anchorable, components),
                Is.True,
                $"{prototypeId} must use the standard wrench anchoring interaction.");
            Assert.That(
                (anchorable.Flags & AnchorableFlags.Unanchorable) != 0,
                Is.True,
                $"{prototypeId} must be removable with a wrench.");
            Assert.That(
                (anchorable.Flags & AnchorableFlags.Anchorable) != 0,
                Is.True,
                $"{prototypeId} must be installable again after moving it.");
        });

        await pair.CleanReturnAsync();
    }
}
