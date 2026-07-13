using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.SectorServices;
using Content.Shared.CCVar;
using Content.Shared._LuaM.Sector;
using Content.Shared.MassMedia.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMSectorTrafficSystem))]
public sealed class LuaMSectorTrafficInterceptTest
{
    private static readonly ResPath SectorMemoryDirectory = new("/luam");
    private static readonly ResPath SectorMemoryPath = SectorMemoryDirectory / "sector_memory.json";

    [Test]
    public async Task CargoContactBecomesRecoverableContractAndClosesThroughEvidence()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var transformSystem = entManager.System<SharedTransformSystem>();
        var stories = entManager.System<LuaMSectorStorySystem>();
        var traffic = entManager.System<LuaMSectorTrafficSystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();
        var evidenceSystem = entManager.System<LuaMSectorEvidenceSystem>();
        var leadReports = entManager.System<LuaMSectorLeadReportSystem>();

        MapId mapId = default;
        var mapCreated = false;
        var oldDynamicEventsEnabled = true;
        var oldMaxActiveSites = 0;
        EntityUid user = default;
        EntityUid cargoContact = default;
        EntityUid recoveryUid = default;
        EntityUid orphanRecovery = default;
        LuaMSectorStoryRecord interceptedRecord = null;
        Vector2 interceptPosition = default;

        try
        {
            await server.WaitPost(() =>
            {
                oldDynamicEventsEnabled = server.CfgMan.GetCVar(CCVars.LuaMDynamicEventsEnabled);
                oldMaxActiveSites = server.CfgMan.GetCVar(CCVars.LuaMDynamicEventsMaxActiveSites);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsEnabled, true);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsMaxActiveSites, 4);

                if (resources.UserData.Exists(SectorMemoryPath))
                    resources.UserData.Delete(SectorMemoryPath);
                SectorNewsComponent.Articles.Clear();

                mapSystem.CreateMap(out mapId);
                mapCreated = true;
                var host = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
                entManager.AddComponent<StationSectorServiceHostComponent>(host);
                entManager.AddComponent<SectorNewsComponent>(host);
                user = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                Assert.That(stories.TryResetMemory(deletePersisted: true), Is.True);
            });

            await server.WaitPost(() =>
            {
                var contacts = traffic.EnsureTrafficForMap(
                    mapId,
                    new[] { Vector2.Zero },
                    LuaMSectorTrafficSystem.HardMaxContacts);
                cargoContact = contacts.Single(uid =>
                    entManager.GetComponent<LuaMSectorTrafficContactComponent>(uid).Profile ==
                    LuaMSectorTrafficProfile.Cargo);
                interceptPosition = entManager.GetComponent<TransformComponent>(cargoContact).LocalPosition;

                Assert.That(traffic.TryInterceptContact(
                    cargoContact,
                    user,
                    out interceptedRecord,
                    out var error,
                    ignorePlayerGate: true,
                    ignoreRadarGate: true), Is.True, error);
                Assert.That(interceptedRecord, Is.Not.Null);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entManager.EntityExists(cargoContact), Is.False,
                        "The ambient blip must be consumed instead of coexisting with its generated task.");
                    Assert.That(interceptedRecord!.ContractName, Is.Not.Empty);
                    Assert.That(
                        stories.GetStatusSnapshot().Hazards.Any(hazard =>
                            hazard.Story == interceptedRecord.Story && !hazard.Resolved),
                        Is.True,
                        "Intercepting must enter the existing bounded runtime-lead/contract ledger.");
                    Assert.That(entManager.AllComponents<LuaMDynamicEventDebrisComponent>(), Is.Empty,
                        "A lightweight radar interception must not load a debris grid or its NPC contacts.");
                });

                var recoveries = entManager.AllComponents<LuaMSectorTrafficRecoveryComponent>().ToList();
                Assert.That(recoveries, Has.Count.EqualTo(1),
                    "One cargo contact may create exactly one lightweight recoverable object.");
                recoveryUid = recoveries.Single().Uid;

                var recovery = recoveries.Single().Component;
                var site = entManager.GetComponent<LuaMDynamicEventSiteObjectComponent>(recoveryUid);
                var evidence = entManager.GetComponent<LuaMSectorEvidenceComponent>(recoveryUid);
                var xform = entManager.GetComponent<TransformComponent>(recoveryUid);
                Assert.Multiple(() =>
                {
                    Assert.That(recovery.StoryId, Is.EqualTo(interceptedRecord!.Story.ToString()));
                    Assert.That(recovery.ExpiresAt, Is.GreaterThan(TimeSpan.Zero));
                    Assert.That(site.TemplateId, Is.EqualTo("courier-handoff"));
                    Assert.That(site.SiteObjectKind, Is.EqualTo("sealed-cargo"));
                    Assert.That(site.DirectSubmissionAllowed, Is.False,
                        "The recovery item must be carried to the terminal, not submitted at its spawn point.");
                    Assert.That(evidence.Story, Is.EqualTo(interceptedRecord.Story));
                    Assert.That(evidence.ResolveStory, Is.True);
                    Assert.That(evidence.RequireSectorTerminal, Is.True);
                    Assert.That(xform.MapID, Is.EqualTo(mapId));
                    Assert.That(xform.GridUid, Is.Null,
                        "Recovered cargo is an item at the intercept point, never an NPC ship or grid.");
                    Assert.That(Vector2.Distance(xform.LocalPosition, interceptPosition), Is.LessThan(2f));
                });

                Assert.That(
                    entManager.AllComponents<LuaMDynamicEventSiteObjectComponent>().Select(entry => entry.Uid),
                    Is.EquivalentTo(new[] { recoveryUid }),
                    "Traffic interception must not spawn a generic site note that bypasses cargo recovery.");

                var markerEntry = entManager.AllComponents<LuaMDynamicEventMarkerComponent>()
                    .Single(entry => entry.Component.Story == interceptedRecord!.Story);
                Assert.That(markerEntry.Component.DirectSubmissionAllowed, Is.False);
                Assert.That(
                    dynamicEvents.TrySubmitMarkerTask(markerEntry.Uid, user, out _),
                    Is.False,
                    "Clicking the route marker must not bypass physical recovery and delivery.");
                Assert.That(
                    dynamicEvents.TrySubmitSiteTask(recoveryUid, user, out _),
                    Is.False,
                    "Clicking the cargo itself at the intercept point must not resolve the story.");

                Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out _), Is.True);
                Assert.That(dynamicEvents.TryPingActiveRouteMarker(user, out _), Is.True);
                Assert.That(
                    dynamicEvents.TryPrintMarkerFieldPacket(markerEntry.Uid, user, out var fieldPacket),
                    Is.True);
                Assert.That(entManager.HasComponent<LuaMSectorEvidenceComponent>(fieldPacket), Is.False,
                    "A generic stabilized field packet must not replace the intercepted cargo delivery.");
            });

            await server.WaitPost(() =>
            {
                transformSystem.SetMapCoordinates(recoveryUid, new MapCoordinates(Vector2.Zero, mapId));
                Assert.That(evidenceSystem.TryFileEvidence(recoveryUid, user), Is.False,
                    "Recovered traffic evidence must not close remotely without a physical sector terminal.");

                var terminal = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
                entManager.AddComponent<LuaMSectorLeadReportComponent>(terminal);
                Assert.That(
                    leadReports.TryPrintRuntimeClosureReport(terminal, user, out _),
                    Is.False,
                    "The generic printable closure report must not bypass a pending intercept recovery.");
                Assert.That(evidenceSystem.TryFileEvidence(recoveryUid, user), Is.True,
                    "Recovering, delivering, and filing cargo must use the existing terminal evidence path.");
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(
                    stories.GetStatusSnapshot().Hazards.Single(hazard =>
                        hazard.Story == interceptedRecord!.Story).Resolved,
                    Is.True);
                Assert.That(entManager.EntityExists(recoveryUid), Is.False,
                    "Resolved intercept recoveries must be removed immediately to bound long-round leftovers.");
                Assert.That(
                    entManager.AllComponents<LuaMSectorTrafficContactComponent>().Count(),
                    Is.LessThanOrEqualTo(LuaMSectorTrafficSystem.HardMaxContacts));
            });

            await server.WaitPost(() =>
            {
                orphanRecovery = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
                var orphan = entManager.AddComponent<LuaMSectorTrafficRecoveryComponent>(orphanRecovery);
                orphan.StoryId = interceptedRecord!.Story.ToString();
                orphan.ExpiresAt = TimeSpan.FromHours(1);
                Assert.That(traffic.HasPendingRecovery(orphan.StoryId), Is.True);
                Assert.That(stories.TryResetMemory(deletePersisted: true), Is.True);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entManager.EntityExists(orphanRecovery), Is.False,
                    "A memory reset must remove orphaned intercept recoveries immediately.");
                Assert.That(traffic.HasPendingRecovery(interceptedRecord!.Story.ToString()), Is.False);
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                dynamicEvents.CleanupAllDynamicMarkers();
                stories.TryResetMemory(deletePersisted: true);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsEnabled, oldDynamicEventsEnabled);
                server.CfgMan.SetCVar(CCVars.LuaMDynamicEventsMaxActiveSites, oldMaxActiveSites);

                if (mapCreated)
                    mapSystem.DeleteMap(mapId);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
    }
}
