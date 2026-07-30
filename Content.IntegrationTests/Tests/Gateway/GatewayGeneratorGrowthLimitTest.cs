using System.Linq;
using System.Numerics;
using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Shared.CCVar;
using Content.Shared.Gateway;
using Content.Shared.Mind;
using Content.Shared.Teleportation.Components;
using Content.Shared.Teleportation.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Gateway;

[TestFixture]
[NonParallelizable]
public sealed class GatewayGeneratorGrowthLimitTest
{
    [Test]
    public async Task GeneratorRotatesOpenedWorldOnlyAfterOccupantsPropertyAndGraceClear()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var generatorSystem = entManager.System<GatewayGeneratorSystem>();
        var gatewaySystem = entManager.System<GatewaySystem>();
        var linkedSystem = entManager.System<LinkedEntitySystem>();
        var mindSystem = entManager.System<SharedMindSystem>();

        var oldEnabled = false;
        var oldMax = 0;
        var oldTtl = 0f;
        var oldOpenedTtl = 0f;
        var oldEmptyGrace = 0f;
        EntityUid generatorUid = default;
        EntityUid expiredMapUid = default;
        EntityUid loadedMapUid = default;
        EntityUid protectedBodyUid = default;
        EntityUid propertyGridUid = default;
        EntityUid mindUid = default;
        EntityUid sourceGatewayUid = default;
        EntityUid returnGatewayUid = default;
        EntityUid gatewayActorUid = default;
        EntityUid replacementMapUid = default;
        EntityUid shutdownProtectedBodyUid = default;
        EntityUid shutdownMindUid = default;
        var sourceMapId = MapId.Nullspace;

        try
        {
            await server.WaitPost(() =>
            {
                oldEnabled = server.CfgMan.GetCVar(CCVars.GatewayGeneratorEnabled);
                oldMax = server.CfgMan.GetCVar(CCVars.GatewayGeneratorMaxDestinations);
                oldTtl = server.CfgMan.GetCVar(CCVars.GatewayGeneratorDestinationTtl);
                oldOpenedTtl = server.CfgMan.GetCVar(CCVars.GatewayGeneratorOpenedDestinationTtl);
                oldEmptyGrace = server.CfgMan.GetCVar(CCVars.GatewayGeneratorEmptyGrace);

                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, false);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorMaxDestinations, 1);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorDestinationTtl, 1f);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorOpenedDestinationTtl, 1f);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEmptyGrace, 1f);

                generatorUid = entManager.SpawnEntity(null, MapCoordinates.Nullspace);
                var generator = entManager.AddComponent<GatewayGeneratorComponent>(generatorUid);

                expiredMapUid = mapSystem.CreateMap(out _);
                var expired = entManager.AddComponent<GatewayGeneratorDestinationComponent>(expiredMapUid);
                expired.Generator = generatorUid;
                expired.GeneratedAt = timing.CurTime - TimeSpan.FromSeconds(2);
                generator.Generated.Add(expiredMapUid);

                loadedMapUid = mapSystem.CreateMap(out var loadedMapId);
                var loaded = entManager.AddComponent<GatewayGeneratorDestinationComponent>(loadedMapUid);
                loaded.Generator = generatorUid;
                loaded.GeneratedAt = timing.CurTime - TimeSpan.FromSeconds(2);
                loaded.Loaded = true;
                loaded.Profile = "GatewayVerdant";
                loaded.OpenedAt = timing.CurTime - TimeSpan.FromSeconds(2);
                loaded.RetireAt = timing.CurTime - TimeSpan.FromSeconds(1);
                loaded.RotationState = GatewayDestinationRotationState.Scheduled;
                generator.Generated.Add(loadedMapUid);

                var sourceMapUid = mapSystem.CreateMap(out sourceMapId);
                sourceGatewayUid = entManager.SpawnEntity(
                    "Gateway",
                    new EntityCoordinates(sourceMapUid, Vector2.Zero));
                gatewayActorUid = entManager.SpawnEntity(
                    null,
                    new EntityCoordinates(sourceMapUid, new Vector2(2f, 0f)));
                returnGatewayUid = entManager.SpawnEntity(
                    "Gateway",
                    new EntityCoordinates(loadedMapUid, Vector2.One));
                loaded.Gateway = returnGatewayUid;
                gatewaySystem.SetEnabled(sourceGatewayUid, true);
                gatewaySystem.SetEnabled(returnGatewayUid, true);
                entManager.EventBus.RaiseLocalEvent(
                    sourceGatewayUid,
                    new GatewayOpenPortalMessage(entManager.GetNetEntity(returnGatewayUid))
                    {
                        Actor = gatewayActorUid,
                    });
                Assert.Multiple(() =>
                {
                    Assert.That(entManager.HasComponent<PortalComponent>(sourceGatewayUid), Is.True);
                    Assert.That(entManager.HasComponent<PortalComponent>(returnGatewayUid), Is.True);
                    Assert.That(linkedSystem.GetLink(sourceGatewayUid, out var sourceLink), Is.True);
                    Assert.That(sourceLink, Is.EqualTo(returnGatewayUid));
                    Assert.That(linkedSystem.GetLink(returnGatewayUid, out var returnLink), Is.True);
                    Assert.That(returnLink, Is.EqualTo(sourceGatewayUid));
                });

                protectedBodyUid = entManager.SpawnEntity(
                    null,
                    new EntityCoordinates(loadedMapUid, Vector2.Zero));
                mindUid = mindSystem.CreateMind(null).Owner;
                mindSystem.TransferTo(mindUid, protectedBodyUid);
                propertyGridUid = mapManager.CreateGridEntity(loadedMapId).Owner;

                Assert.That(generatorSystem.CleanupExpiredDestinations(generatorUid, generator), Is.EqualTo(1));
                Assert.That(generator.Generated, Is.EquivalentTo(new[] { loadedMapUid }));
                Assert.That(
                    loaded.RotationState,
                    Is.EqualTo(GatewayDestinationRotationState.WaitingForClearance));

                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, true);
                Assert.That(generatorSystem.TryGenerateDestination(generatorUid, generator), Is.False,
                    "A protected loaded destination must still consume the hard capacity limit.");

                mindSystem.WipeMind(mindUid);
                entManager.DeleteEntity(protectedBodyUid);

                Assert.That(generatorSystem.CleanupExpiredDestinations(generatorUid, generator), Is.EqualTo(0),
                    "A recoverable additional grid must independently block world rotation.");
                Assert.That(
                    loaded.RotationState,
                    Is.EqualTo(GatewayDestinationRotationState.WaitingForClearance));

                entManager.DeleteEntity(propertyGridUid);
                Assert.That(generatorSystem.CleanupExpiredDestinations(generatorUid, generator), Is.EqualTo(0),
                    "An empty destination must survive its continuous empty grace period.");
                Assert.That(
                    loaded.RotationState,
                    Is.EqualTo(GatewayDestinationRotationState.EmptyGracePeriod));

                loaded.EmptySince = timing.CurTime - TimeSpan.FromSeconds(2);
                Assert.That(generatorSystem.CleanupExpiredDestinations(generatorUid, generator), Is.EqualTo(1));
                Assert.That(generator.Generated, Is.Empty);
                Assert.Multiple(() =>
                {
                    Assert.That(entManager.HasComponent<PortalComponent>(sourceGatewayUid), Is.False);
                    Assert.That(entManager.HasComponent<PortalComponent>(returnGatewayUid), Is.False);
                    Assert.That(linkedSystem.GetLink(sourceGatewayUid, out _), Is.False);
                    Assert.That(linkedSystem.GetLink(returnGatewayUid, out _), Is.False);
                });
                Assert.That(generatorSystem.TryGenerateDestination(generatorUid, generator), Is.True,
                    "Safe retirement must release the hard-cap slot for a replacement profile.");
                Assert.That(generator.Generated, Has.Count.EqualTo(1));
                replacementMapUid = generator.Generated.Single();
                var generating = entManager.GetComponent<GatewayGeneratorDestinationComponent>(
                    replacementMapUid);
                Assert.Multiple(() =>
                {
                    Assert.That(
                        generating.GenerationState,
                        Is.EqualTo(GatewayDestinationGenerationState.Generating));
                    Assert.That(generating.DungeonBoundsValidated, Is.False);
                });
            });

            await pair.RunTicksSync(600);

            await server.WaitAssertion(() =>
            {
                Assert.That(entManager.EntityExists(expiredMapUid), Is.False,
                    "An expired unopened destination map should be deleted.");
                Assert.That(entManager.EntityExists(loadedMapUid), Is.False,
                    "An expired opened destination should rotate after all safeguards clear.");

                var replacement = entManager.GetComponent<GatewayGeneratorDestinationComponent>(
                    replacementMapUid);
                Assert.Multiple(() =>
                {
                    Assert.That(replacement.Profile.Id, Is.Not.Empty);
                    Assert.That(replacement.Address, Does.StartWith("GW-"));
                    Assert.That(
                        replacement.GenerationState,
                        Is.EqualTo(GatewayDestinationGenerationState.Ready));
                    Assert.That(replacement.DungeonBoundsValidated, Is.True);
                });
            });

            await server.WaitPost(() =>
            {
                shutdownProtectedBodyUid = entManager.SpawnEntity(
                    null,
                    new EntityCoordinates(replacementMapUid, Vector2.Zero));
                shutdownMindUid = mindSystem.CreateMind(null).Owner;
                mindSystem.TransferTo(shutdownMindUid, shutdownProtectedBodyUid);

                entManager.DeleteEntity(generatorUid);
                var orphaned = entManager.GetComponent<GatewayGeneratorDestinationComponent>(
                    replacementMapUid);
                Assert.Multiple(() =>
                {
                    Assert.That(orphaned.Orphaned, Is.True);
                    Assert.That(orphaned.Generator, Is.EqualTo(EntityUid.Invalid));
                    Assert.That(
                        orphaned.RotationState,
                        Is.EqualTo(GatewayDestinationRotationState.WaitingForClearance));
                    Assert.That(generatorSystem.CleanupFailedAndOrphanedDestinations(), Is.EqualTo(0));
                    Assert.That(entManager.EntityExists(replacementMapUid), Is.True,
                        "Removing a generator must not delete a destination with a controlled body.");
                });
            });

            await pair.RunTicksSync(5);
            await server.WaitAssertion(() =>
            {
                Assert.That(entManager.EntityExists(replacementMapUid), Is.True);
            });

            await server.WaitPost(() =>
            {
                mindSystem.WipeMind(shutdownMindUid);
                entManager.DeleteEntity(shutdownProtectedBodyUid);

                var orphaned = entManager.GetComponent<GatewayGeneratorDestinationComponent>(
                    replacementMapUid);
                orphaned.EmptySince = timing.CurTime - TimeSpan.FromSeconds(2);
                Assert.That(generatorSystem.CleanupFailedAndOrphanedDestinations(), Is.EqualTo(1));
            });

            await pair.RunTicksSync(5);
            await server.WaitAssertion(() =>
            {
                Assert.That(entManager.EntityExists(replacementMapUid), Is.False,
                    "An orphaned destination should retire only after it is empty and the grace period elapsed.");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, false);
                if (entManager.EntityExists(generatorUid))
                    entManager.DeleteEntity(generatorUid);

                if (sourceMapId != MapId.Nullspace && mapSystem.MapExists(sourceMapId))
                    mapSystem.DeleteMap(sourceMapId);

                server.CfgMan.SetCVar(CCVars.GatewayGeneratorMaxDestinations, oldMax);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorDestinationTtl, oldTtl);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorOpenedDestinationTtl, oldOpenedTtl);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEmptyGrace, oldEmptyGrace);
                server.CfgMan.SetCVar(CCVars.GatewayGeneratorEnabled, oldEnabled);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
    }
}
