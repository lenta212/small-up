using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Shared.CCVar;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Gateway;

[TestFixture]
[NonParallelizable]
public sealed class GatewayGeneratorGrowthLimitTest
{
    [Test]
    public async Task GeneratorCapsDestinationsAndExpiresOnlyUnopenedMaps()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var generatorSystem = entManager.System<GatewayGeneratorSystem>();

        var oldEnabled = false;
        var oldMax = 0;
        var oldTtl = 0f;
        EntityUid generatorUid = default;
        EntityUid expiredMapUid = default;
        EntityUid loadedMapUid = default;

        try
        {
            await server.WaitPost(() =>
            {
                oldEnabled = server.CfgMan.GetCVar(CCVars.GatewayGeneratorEnabled);
                oldMax = server.CfgMan.GetCVar(CCVars.GatewayGeneratorMaxDestinations);
                oldTtl = server.CfgMan.GetCVar(CCVars.GatewayGeneratorDestinationTtl);

                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, false);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorMaxDestinations, 1);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorDestinationTtl, 1f);

                generatorUid = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
                var generator = entManager.AddComponent<GatewayGeneratorComponent>(generatorUid);

                expiredMapUid = mapSystem.CreateMap(out _);
                var expired = entManager.AddComponent<GatewayGeneratorDestinationComponent>(expiredMapUid);
                expired.Generator = generatorUid;
                expired.GeneratedAt = timing.CurTime - TimeSpan.FromSeconds(2);
                generator.Generated.Add(expiredMapUid);

                loadedMapUid = mapSystem.CreateMap(out _);
                var loaded = entManager.AddComponent<GatewayGeneratorDestinationComponent>(loadedMapUid);
                loaded.Generator = generatorUid;
                loaded.GeneratedAt = timing.CurTime - TimeSpan.FromSeconds(2);
                loaded.Loaded = true;
                generator.Generated.Add(loadedMapUid);

                Assert.That(generatorSystem.CleanupExpiredDestinations(generatorUid, generator), Is.EqualTo(1));
                Assert.That(generator.Generated, Is.EquivalentTo(new[] { loadedMapUid }));

                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, true);
                Assert.That(generatorSystem.TryGenerateDestination(generatorUid, generator), Is.False,
                    "A loaded destination must still consume the hard capacity limit.");
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entManager.EntityExists(expiredMapUid), Is.False,
                    "An expired unopened destination map should be deleted.");
                Assert.That(entManager.EntityExists(loadedMapUid), Is.True,
                    "TTL cleanup must not delete an opened destination map.");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, false);
                if (entManager.EntityExists(generatorUid))
                    entManager.DeleteEntity(generatorUid);

                server.CfgMan.SetCVar(CCVars.GatewayGeneratorMaxDestinations, oldMax);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorDestinationTtl, oldTtl);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, oldEnabled);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
    }
}
