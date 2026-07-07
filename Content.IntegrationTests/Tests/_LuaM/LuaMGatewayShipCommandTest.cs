using System.Collections.Generic;
using System.Linq;
using Content.Server.Gateway.Components;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMGatewayShipCommandTest
{
    [Test]
    public async Task GatewayShipCommandLoadsShipAndPlacesEnabledGateway()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var mapSystem = entMan.System<SharedMapSystem>();
        var mapIdValue = 42601;
        var mapId = new MapId(mapIdValue);

        await server.WaitPost(() =>
        {
            while (mapSystem.MapExists(mapId))
            {
                mapIdValue++;
                mapId = new MapId(mapIdValue);
            }
        });

        var beforeGateways = 0;
        var beforeGatewayUids = new HashSet<EntityUid>();
        await server.WaitAssertion(() =>
        {
            var gateways = entMan.AllComponents<GatewayComponent>().ToList();
            beforeGateways = gateways.Count;
            beforeGatewayUids = gateways.Select(component => component.Uid).ToHashSet();
        });

        await server.WaitPost(() =>
        {
            console.ExecuteCommand($"luam_gateway_ship --confirm {mapIdValue} Twilight 0 0 LuaM-SmokeShip");
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var gateways = entMan.AllComponents<GatewayComponent>().ToList();
            Assert.That(gateways, Has.Count.EqualTo(beforeGateways + 1));

            var shipGateway = gateways.Single(component => !beforeGatewayUids.Contains(component.Uid));
            Assert.That(shipGateway.Component.Enabled, Is.True);

            Assert.That(entMan.TryGetComponent<TransformComponent>(shipGateway.Uid, out var xform), Is.True);
            Assert.That(xform!.MapID, Is.EqualTo(mapId));
            Assert.That(xform.GridUid, Is.Not.Null);
            Assert.That(entMan.HasComponent<MapGridComponent>(xform.GridUid!.Value), Is.True);
        });

        await pair.CleanReturnAsync();
    }
}
