#nullable enable

using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMBoundUiRangeValidationTest
{
    private enum TestUiKey : byte
    {
        Main,
    }

    [Test]
    public async Task ServerCancelsValidatedBoundUiMessagesOutsideCanonicalRange()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Dirty = true,
        });

        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var userInterfaces = entities.System<SharedUserInterfaceSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var actor = entities.SpawnEntity(null, map.MapCoords);
            var target = entities.SpawnEntity(
                null,
                new MapCoordinates(new Vector2(1f, 0f), map.MapId));

            userInterfaces.SetUi(
                target,
                TestUiKey.Main,
                new InterfaceData(string.Empty, interactionRange: 2f, requireInputValidation: true));

            var nearbyAttempt = new BoundUserInterfaceMessageAttempt(
                actor,
                target,
                TestUiKey.Main,
                new OpenBoundInterfaceMessage());
            entities.EventBus.RaiseEvent(EventSource.Local, nearbyAttempt);

            Assert.That(
                nearbyAttempt.Cancelled,
                Is.False,
                "A validated BUI message inside its canonical interaction range must remain allowed.");

            entities.System<SharedTransformSystem>().SetMapCoordinates(
                target,
                new MapCoordinates(new Vector2(3f, 0f), map.MapId));

            var remoteAttempt = new BoundUserInterfaceMessageAttempt(
                actor,
                target,
                TestUiKey.Main,
                new OpenBoundInterfaceMessage());
            entities.EventBus.RaiseEvent(EventSource.Local, remoteAttempt);

            Assert.That(
                remoteAttempt.Cancelled,
                Is.True,
                "The server must reject a validated BUI message outside its canonical interaction range.");
        });

        await pair.CleanReturnAsync();
    }
}
