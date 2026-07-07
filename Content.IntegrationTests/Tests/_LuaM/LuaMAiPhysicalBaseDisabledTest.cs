using System.Collections.Generic;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMAiPhysicalBaseDisabledTest
{
    [Test]
    public async Task PhysicalAiBaseEntitiesAreDisabledAndCleanedUp()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<MapSystem>();
        var logisticsSystem = entManager.System<LuaMAiLogisticsShipSystem>();
        var supplyDropSystem = entManager.System<LuaMAiSupplyDropSystem>();

        EntityUid aiShip = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(LuaMAiPhysicalBaseFeature.Enabled, Is.False);

            mapSystem.CreateMap(out var mapId);
            entManager.SpawnEntity("LuaMAiBaseBeacon", new MapCoordinates(Vector2.Zero, mapId));
            entManager.SpawnEntity("LuaMAiBaseZoneMarker", new MapCoordinates(new Vector2(1, 0), mapId));
            entManager.SpawnEntity("LuaMAiSupplyDrop", new MapCoordinates(new Vector2(2, 0), mapId));
            entManager.SpawnEntity("LuaMAiDroneTrace", new MapCoordinates(new Vector2(3, 0), mapId));
            entManager.SpawnEntity("LuaMAiMiningDrone", new MapCoordinates(new Vector2(4, 0), mapId));

            aiShip = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(5, 0), mapId));
            var logistics = entManager.AddComponent<LuaMAiLogisticsShipComponent>(aiShip);
            logistics.CrewRoleManifest = new List<string> { "repair", "guard" };
            logistics.NextCycle = TimeSpan.FromTicks(1);

            var crewReport = logisticsSystem.EnsureCrewForShip(aiShip, logistics);
            Assert.That(crewReport, Is.EqualTo(LuaMAiPhysicalBaseFeature.DisabledReason));
            Assert.That(logistics.CrewRoleManifest, Is.Empty);

            Assert.That(supplyDropSystem.TrySpawnForLatestTrade(
                "integration-test",
                out var dropUid,
                out var dropSummary), Is.False);
            Assert.That(dropUid, Is.EqualTo(EntityUid.Invalid));
            Assert.That(dropSummary, Is.EqualTo(LuaMAiPhysicalBaseFeature.DisabledReason));
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(CountLiveComponents<LuaMAiBaseAnchorComponent>(), Is.EqualTo(0));
            Assert.That(CountLiveComponents<LuaMAiBaseZoneComponent>(), Is.EqualTo(0));
            Assert.That(CountLiveComponents<LuaMAiSupplyDropComponent>(), Is.EqualTo(0));
            Assert.That(CountLiveComponents<LuaMAiDroneTraceComponent>(), Is.EqualTo(0));
            Assert.That(CountLiveComponents<LuaMAiMiningDroneComponent>(), Is.EqualTo(0));

            var logistics = entManager.GetComponent<LuaMAiLogisticsShipComponent>(aiShip);
            Assert.That(logistics.Cycles, Is.EqualTo(0));
            Assert.That(logistics.CrewSpawnAttempts, Is.EqualTo(0));
        });

        await pair.CleanReturnAsync();

        int CountLiveComponents<T>() where T : IComponent
        {
            var count = 0;
            var query = entManager.EntityQueryEnumerator<T>();
            while (query.MoveNext(out var uid, out _))
            {
                if (!entManager.TryGetComponent<MetaDataComponent>(uid, out var meta) ||
                    meta.EntityLifeStage >= EntityLifeStage.Terminating)
                {
                    continue;
                }

                count++;
            }

            return count;
        }
    }

    [Test]
    public void AiLogisticsCrewProfileHelpersRemainAvailable()
    {
        Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("builder", "Baeg"),
            Is.EqualTo(new[] { "repair", "repair", "logistics", "guard" }));
        Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("miner", "Baeg"),
            Is.EqualTo(new[] { "miner", "miner", "logistics", "repair", "scout" }));
        Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("builder", "Baeg", "medical-followup", "medicine"),
            Is.EqualTo(new[] { "repair", "repair", "logistics", "guard", "medic" }));
        Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("builder", "Baeg", "extraction", "ore"),
            Is.EqualTo(new[] { "repair", "repair", "logistics", "guard", "miner" }));
        Assert.That(LuaMAiLogisticsShipSystem.BuildCrewManifest("builder", "Baeg", "crew-support", "food"),
            Is.EqualTo(new[] { "repair", "repair", "logistics", "guard", "service" }));

        var baegProfile = LuaMAiLogisticsShipSystem.BuildCrewProfile("builder", "Baeg");
        Assert.That(baegProfile.ProfileId, Is.EqualTo("builder:light-shuttle"));
        Assert.That(baegProfile.ManifestSource, Is.EqualTo("profile:builder:light-shuttle"));
        Assert.That(baegProfile.Summary, Does.Contain("repair-first"));
        Assert.That(baegProfile.StationPlan, Has.Count.EqualTo(4));

        var behaviorProfile = LuaMAiLogisticsShipSystem.BuildCrewProfile("builder", "Baeg", "medical-followup", "medicine");
        Assert.That(behaviorProfile.ProfileId, Is.EqualTo("builder:light-shuttle:medical-followup:medicine"));
        Assert.That(behaviorProfile.Summary, Does.Contain("behavior doctrine medical-followup focused on medicine"));
    }
}
