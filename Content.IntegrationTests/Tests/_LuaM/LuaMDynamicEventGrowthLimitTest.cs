using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.SectorServices;
using Content.Shared.CCVar;
using Content.Shared.MassMedia.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMDynamicEventGrowthLimitTest
{
    private static readonly ResPath SectorMemoryDirectory = new("/luam");
    private static readonly ResPath SectorMemoryPath = SectorMemoryDirectory / "sector_memory.json";

    [Test]
    public async Task DynamicEventsRespectEnableSwitchAndActiveSiteLimit()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var mapSystem = entManager.System<SharedMapSystem>();

        var oldEnabled = true;
        var oldMaxActiveSites = 0;
        LuaMSectorStoryRecord firstRecord = null;
        MapId testMapId = default;
        MapCoordinates testCoordinates = default;
        MapCoordinates secondCoordinates = default;
        MapCoordinates replacementCoordinates = default;

        try
        {
            await server.WaitPost(() =>
            {
                oldEnabled = server.CfgMan.GetCVar(CCVars.LuaMDynamicEventsEnabled);
                oldMaxActiveSites = server.CfgMan.GetCVar(CCVars.LuaMDynamicEventsMaxActiveSites);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsEnabled, false);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsMaxActiveSites, 2);

                if (resources.UserData.Exists(SectorMemoryPath))
                    resources.UserData.Delete(SectorMemoryPath);
                SectorNewsComponent.Articles.Clear();

                mapSystem.CreateMap(out testMapId);
                testCoordinates = new MapCoordinates(Vector2.Zero, testMapId);
                secondCoordinates = new MapCoordinates(new Vector2(50, 0), testMapId);
                replacementCoordinates = new MapCoordinates(new Vector2(100, 0), testMapId);
                var serviceHost = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
                entManager.AddComponent<StationSectorServiceHostComponent>(serviceHost);
                entManager.AddComponent<SectorNewsComponent>(serviceHost);
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
            });

            await server.WaitPost(() =>
            {
                Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                    "integration-test-disabled",
                    out _,
                    out var disabledError,
                    templateId: "quiet-distress",
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: testCoordinates), Is.False);
                Assert.That(disabledError, Does.Contain("отключены конфигурацией"));

                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsEnabled, true);

                Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                    "integration-test-first",
                    out firstRecord,
                    out var firstError,
                    templateId: "quiet-distress",
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: testCoordinates), Is.True, firstError);

                Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                    "integration-test-second",
                    out _,
                    out var secondError,
                    templateId: "field-repair",
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: secondCoordinates), Is.True, secondError);

                Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                    "integration-test-over-limit",
                    out _,
                    out var limitError,
                    templateId: "black-box-echo",
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: testCoordinates), Is.False);
                Assert.That(limitError, Does.Contain("2/2"));

                var proposal = new LuaMSectorAiEventProposal { TemplateId = "quiet-distress" };
                Assert.That(dynamicEvents.TryGenerateDynamicEventFromAiProposal(
                    proposal,
                    "integration-test-ai-over-limit",
                    out _,
                    out var aiLimitError,
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: testCoordinates), Is.False);
                Assert.That(aiLimitError, Does.Contain("2/2"));

                Assert.That(firstRecord, Is.Not.Null);
                Assert.That(storySystem.TryResolveStory(firstRecord!.Story, "integration-test", "site closed"), Is.True);
            });

            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                    "integration-test-after-close",
                    out _,
                    out var replacementError,
                    templateId: "black-box-echo",
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: replacementCoordinates), Is.True, replacementError);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var activeStoryIds = storySystem.GetStatusSnapshot().Hazards
                    .Where(hazard => !hazard.Resolved &&
                                     hazard.Story.ToString().StartsWith(
                                         LuaMSectorStorySystem.RuntimeDistressStoryPrefix,
                                         StringComparison.Ordinal))
                    .Select(hazard => hazard.Story)
                    .Distinct()
                    .ToList();
                Assert.That(activeStoryIds, Has.Count.EqualTo(2));
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                dynamicEvents.CleanupAllDynamicMarkers();
                storySystem.TryResetMemory(deletePersisted: true);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsMaxActiveSites, oldMaxActiveSites);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsEnabled, oldEnabled);
            });

            await pair.RunTicksSync(30);
            await pair.CleanReturnAsync();
        }
    }
}
