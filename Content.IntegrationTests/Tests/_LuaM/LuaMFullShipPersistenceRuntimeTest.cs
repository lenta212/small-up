using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Server._NF.CryoSleep;
using Content.Server.Database;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Gravity;
using Content.Server.Lathe;
using Content.Server.Mind;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Spreader;
using Content.Server.Station.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Atmos;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Containers;
using Content.Shared.Gravity;
using Content.Shared.Power.Components;
using Content.Shared.Maps;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Station.Components;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMFullShipPersistenceRuntimeTest
{
    private static readonly ResPath SeedPath = new("/Maps/_LuaM/ShipGen/shipgen_seed.yml");
    private static readonly ResPath McChickenPath = new("/Maps/_Mono/Shuttles/mcchicken.yml");
    private const string ContainerOwnerName = "LuaM snapshot container owner";
    private const string ContainedItemName = "LuaM snapshot contained crowbar";
    private const string PlayerSlotOwnerName = "LuaM snapshot player slot owner";
    private const string PlayerInventoryItemName = "LuaM snapshot player inventory crowbar";
    private const string StrapName = "LuaM snapshot player strap";
    private const string StrappedMouseName = "LuaM snapshot strapped mouse";
    private const string StrappedInventoryItemName = "LuaM snapshot strapped inventory wrench";
    private const string LooseItemName = "LuaM snapshot loose wrench";
    private const string MouseName = "LuaM snapshot mouse";
    private const string ApcName = "LuaM snapshot APC";
    private const string BatteryName = "LuaM snapshot battery";
    private const string LegacyBodyName = "LuaM legacy v1 player body";
    private const string LegacyInventoryItemName = "LuaM legacy v1 player inventory";
    private const string LegacySlotOwnerName = "LuaM legacy v1 slot owner";
    private const string LegacyMindName = "LuaM legacy v1 mind";
    private const string VesselStationName = "LuaM snapshot runtime station root";
    private const string ContainerId = "LuaMFullShipPersistenceRuntimeContainer";
    private const string PlayerSlotId = "LuaMFullShipPersistenceRuntimePlayerSlot";
    private const string PlayerInventoryId = "LuaMFullShipPersistenceRuntimePlayerInventory";

    [Test]
    public void PortableReferenceSanitizerOnlyTouchesKnownComponentFields()
    {
        const string yaml = """
                            meta:
                              category: Grid
                              entityCount: 1
                            entities:
                            - proto: LuaMReferenceFixture
                              entities:
                              - uid: 1
                                components:
                                - type: ContainerContainer
                                  containers:
                                    slot:
                                      ent: invalid
                                      ents:
                                      - invalid
                                      - 7
                                - type: Strap
                                  buckledEntities:
                                  - invalid
                                  - 8
                                - type: UnrelatedFixture
                                  containers:
                                    ent: invalid
                                  buckledEntities:
                                  - invalid
                            """;

        Assert.That(
            LuaMFullShipPersistenceSystem.TryInspectAndSanitizeSerializedShipYaml(
                yaml,
                out var sanitized,
                out var entityCount,
                out var prototypeCounts,
                out var prototypeManifestHash,
                out var reason),
            Is.True,
            reason);

        var stream = new YamlStream();
        stream.Load(new StringReader(sanitized));
        var root = (YamlMappingNode) stream.Documents.Single().RootNode;
        var groups = (YamlSequenceNode) RequireYamlChild(root, "entities");
        var group = (YamlMappingNode) groups.Children.Single();
        var serializedEntities = (YamlSequenceNode) RequireYamlChild(group, "entities");
        var entity = (YamlMappingNode) serializedEntities.Children.Single();
        var components = (YamlSequenceNode) RequireYamlChild(entity, "components");
        var containerComponent = RequireComponent(components, "ContainerContainer");
        var strapComponent = RequireComponent(components, "Strap");
        var unrelatedComponent = RequireComponent(components, "UnrelatedFixture");

        var containers = (YamlMappingNode) RequireYamlChild(containerComponent, "containers");
        var slot = (YamlMappingNode) RequireYamlChild(containers, "slot");
        var containerEntities = (YamlSequenceNode) RequireYamlChild(slot, "ents");
        var strappedEntities = (YamlSequenceNode) RequireYamlChild(strapComponent, "buckledEntities");
        var unrelatedContainers = (YamlMappingNode) RequireYamlChild(unrelatedComponent, "containers");
        var unrelatedBuckled = (YamlSequenceNode) RequireYamlChild(unrelatedComponent, "buckledEntities");

        Assert.Multiple(() =>
        {
            Assert.That(entityCount, Is.EqualTo(1));
            Assert.That(prototypeCounts, Is.EquivalentTo(
                new Dictionary<string, int> { ["LuaMReferenceFixture"] = 1 }));
            Assert.That(prototypeManifestHash, Has.Length.EqualTo(64));
            Assert.That(((YamlScalarNode) RequireYamlChild(slot, "ent")).Value, Is.EqualTo("null"));
            Assert.That(containerEntities.Children.Select(node => ((YamlScalarNode) node).Value),
                Is.EqualTo(new[] { "7" }));
            Assert.That(strappedEntities.Children.Select(node => ((YamlScalarNode) node).Value),
                Is.EqualTo(new[] { "8" }));
            Assert.That(((YamlScalarNode) RequireYamlChild(unrelatedContainers, "ent")).Value,
                Is.EqualTo("invalid"));
            Assert.That(((YamlScalarNode) unrelatedBuckled.Children.Single()).Value,
                Is.EqualTo("invalid"));
        });
    }

    [Test]
    public void EntityCountLimitIsRejectedBeforeEntityYamlTraversal()
    {
        var yaml = $$"""
                     meta:
                       category: Grid
                       entityCount: {{LuaMShipPersistenceLimits.MaxEntityCount + 1}}
                     entities: []
                     """;

        Assert.That(
            LuaMFullShipPersistenceSystem.TryInspectAndSanitizeSerializedShipYaml(
                yaml,
                out _,
                out _,
                out _,
                out _,
                out var reason),
            Is.False);
        Assert.That(reason, Is.EqualTo("snapshot-yaml-metadata-invalid"));
    }

    [Test]
    public async Task RealMcChickenGridCapturesTwoRevisionsAndRestores()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var stations = entities.System<StationSystem>();
        var docking = entities.System<DockingSystem>();
        var shuttles = entities.System<ShuttleSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var metadata = entities.System<MetaDataSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid vesselStation = EntityUid.Invalid;

        try
        {
            LuaMFullShipSnapshot secondSnapshot = default!;
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                Assert.That(loader.TryLoadGrid(sourceMap, McChickenPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                sourceGrid = loaded!.Value.Owner;

                var gameMap = prototypes.Index<GameMapPrototype>("McChicken");
                vesselStation = stations.InitializeNewStation(gameMap.Stations["McChicken"], [sourceGrid]);
                metadata.SetEntityName(vesselStation, VesselStationName);
                entities.EnsureComponent<VesselComponent>(sourceGrid).VesselId = "McChicken";

                var externalGrid = mapManager.CreateGridEntity(sourceMap);
                transform.SetLocalPosition(externalGrid.Owner, new Vector2(100f, 0f));
                maps.SetTile(externalGrid.Owner, externalGrid.Comp, Vector2i.Zero, new Tile(1));
                var externalDock = entities.SpawnEntity(
                    "AirlockShuttle",
                    new EntityCoordinates(externalGrid.Owner, new Vector2(0.5f, 0.5f)));
                const string externalDockName = "LuaM snapshot external station dock";
                metadata.SetEntityName(externalDock, externalDockName);

                var shuttle = entities.GetComponent<ShuttleComponent>(sourceGrid);
                Assert.That(
                    shuttles.TryFTLDockAtDock(sourceGrid, shuttle, externalGrid.Owner, externalDock),
                    Is.True,
                    "The fixture must use the same geometry-checked docking path as a shipyard purchase.");
                var shipDock = docking.GetDocks(sourceGrid)
                    .Single(candidate => candidate.Comp.DockedWith == externalDock);
                Assert.That(shipDock.Comp.DockedWith, Is.EqualTo(externalDock));

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var firstSnapshot, out var firstReason),
                    Is.True,
                    firstReason);
                Assert.That(shipDock.Comp.DockedWith, Is.EqualTo(externalDock),
                    "Snapshot capture must restore the live station docking connection.");
                Assert.That(
                    Encoding.UTF8.GetString(firstSnapshot.Payload),
                    Does.Not.Contain(externalDockName),
                    "A portable ship snapshot must not include the station grid or its gate.");
                Assert.That(
                    Encoding.UTF8.GetString(firstSnapshot.Payload),
                    Does.Not.Contain(VesselStationName),
                    "A portable ship snapshot must not auto-include its null-space station root.");
                Assert.That(firstSnapshot.EntityCount, Is.GreaterThan(700));
                Assert.That(
                    persistence.TryGetSavedShipManifest(firstSnapshot, out var savedManifest, out var manifestReason),
                    Is.True,
                    manifestReason);
                Assert.Multiple(() =>
                {
                    Assert.That(savedManifest.EntityCount, Is.EqualTo(firstSnapshot.EntityCount));
                    Assert.That(savedManifest.PayloadSizeBytes, Is.EqualTo(firstSnapshot.PayloadSizeBytes));
                    Assert.That(savedManifest.PrototypeManifestHash, Is.EqualTo(firstSnapshot.PrototypeManifestHash));
                    Assert.That(savedManifest.Prototypes, Is.Not.Empty);
                    Assert.That(savedManifest.Prototypes.Sum(entry => entry.Count), Is.EqualTo(firstSnapshot.EntityCount));
                    Assert.That(savedManifest.Prototypes.Any(entry => entry.Id == "ComputerTabletopShuttle"), Is.True);
                });
                Assert.That(persistence.TryCommitSnapshotRevision(sourceGrid, firstSnapshot), Is.True);

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 2, out secondSnapshot, out var secondReason),
                    Is.True,
                    secondReason);
                Assert.That(shipDock.Comp.DockedWith, Is.EqualTo(externalDock),
                    "Every snapshot revision must restore the live station docking connection.");
                Assert.That(secondSnapshot.Revision, Is.EqualTo(2));

                stations.DeleteStation(vesselStation);
                entities.DeleteEntity(sourceGrid);
                maps.CreateMap(out targetMap);

                Assert.That(
                    persistence.TryRestoreSnapshot(secondSnapshot, targetMap, out var restored, out var restoreReason),
                    Is.True,
                    restoreReason);
                Assert.That(entities.GetComponent<LuaMShipIdentityComponent>(restored).SnapshotRevision, Is.EqualTo(2));
                Assert.That(
                    entities.HasComponent<StationMemberComponent>(restored),
                    Is.True,
                    "A restored registered vessel must replace its ignored live station reference with a new round-local membership.");
                Assert.That(
                    stations.GetOwningStation(restored),
                    Is.Not.Null.And.Not.EqualTo(vesselStation),
                    "A restored registered vessel must recreate its station root from its saved vessel prototype.");
                Assert.That(
                    docking.GetDocks(restored).All(candidate => !candidate.Comp.Docked),
                    Is.True,
                    "A restored ship must be free for placement at the gate selected by the player.");
                var restoredDockInfo = string.Join("; ", docking.GetDocks(restored).Select(candidate =>
                {
                    var xform = entities.GetComponent<TransformComponent>(candidate.Owner);
                    return $"{candidate.Owner}:{candidate.Comp.DockType}:anchored={xform.Anchored}:receiveOnly={candidate.Comp.ReceiveOnly}:docked={candidate.Comp.Docked}:grid={xform.GridUid}";
                }));
                var targetDockXform = entities.GetComponent<TransformComponent>(externalDock);
                Assert.That(
                    shuttles.TryFTLDockAtDock(
                        restored,
                        entities.GetComponent<ShuttleComponent>(restored),
                        externalGrid.Owner,
                        externalDock),
                    Is.True,
                    $"A restored parked ship must be callable to the exact player-selected gate. Restored docks: {restoredDockInfo}; target anchored={targetDockXform.Anchored}, docked={entities.GetComponent<DockingComponent>(externalDock).Docked}, type={entities.GetComponent<DockingComponent>(externalDock).DockType}");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    [Test]
    public async Task ActiveGravityGeneratorReconcilesAfterFullGridRestore()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid generator = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var grid = mapManager.CreateGridEntity(sourceMap);
                sourceGrid = grid.Owner;
                maps.SetTile(grid, grid, Vector2i.Zero, new Tile(1));

                generator = entities.SpawnEntity(
                    "GravityGeneratorMini",
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                entities.GetComponent<ApcPowerReceiverComponent>(generator).NeedsPower = false;

                // Seed the durable charged state directly; this test validates
                // snapshot reconciliation, not the asynchronous power network.
                var charge = entities.GetComponent<PowerChargeComponent>(generator);
                typeof(PowerChargeComponent).GetProperty(nameof(PowerChargeComponent.Charge))!
                    .SetValue(charge, charge.MaxCharge);
                typeof(PowerChargeComponent).GetProperty(nameof(PowerChargeComponent.Active))!
                    .SetValue(charge, true);
                typeof(GravityGeneratorComponent).GetProperty(nameof(GravityGeneratorComponent.GravityActive))!
                    .SetValue(entities.GetComponent<GravityGeneratorComponent>(generator), true);
                entities.GetComponent<GravityComponent>(sourceGrid).EnabledVV = true;
            });

            await server.WaitRunTicks(25);

            await server.WaitPost(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<PowerChargeComponent>(generator).Active, Is.True);
                    Assert.That(entities.GetComponent<GravityGeneratorComponent>(generator).GravityActive, Is.True);
                    Assert.That(entities.GetComponent<GravityComponent>(sourceGrid).Enabled, Is.True);
                });

                // Capture the production failure shape: the durable charge state
                // is active, but no always-powered prototype override is needed
                // to make it so after restore.
                entities.GetComponent<ApcPowerReceiverComponent>(generator).NeedsPower = true;
                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                entities.DeleteEntity(sourceGrid);
                sourceGrid = EntityUid.Invalid;
                maps.CreateMap(out targetMap);

                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredGenerator = RequirePrototypeDescendant(entities, restoredGrid, "GravityGeneratorMini");
                Assert.Multiple(() =>
                {
                    Assert.That(
                        entities.GetComponent<PowerChargeComponent>(restoredGenerator).Active,
                        Is.True,
                        "The snapshot must restore the durable charged-machine state that exposed the mismatch.");
                    Assert.That(
                        entities.GetComponent<GravityGeneratorComponent>(restoredGenerator).GravityActive,
                        Is.True,
                        "Component initialization must rebuild the runtime-only gravity-generator state.");
                    Assert.That(
                        entities.GetComponent<GravityComponent>(restoredGrid).Enabled,
                        Is.True,
                        "An active restored generator must immediately restore gravity on its grid.");
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    [Test]
    public async Task McChickenPersistentLockAndOffGridGuestRoundTrip()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var metadata = entities.System<MetaDataSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var consoleLocks = entities.System<ShuttleConsoleLockSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                Assert.That(loader.TryLoadGrid(sourceMap, McChickenPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                var sourceGrid = loaded!.Value.Owner;

                var shipId = persistence.GetOrAssignShipId(sourceGrid);
                var canonicalShipId = shipId.ToString("D");
                var gridDeed = entities.EnsureComponent<ShuttleDeedComponent>(sourceGrid);
                consoleLocks.SetDeedShipIdentity(sourceGrid, sourceGrid, null, gridDeed);

                var console = RequireShuttleConsole(entities, sourceGrid);
                var gridLock = entities.EnsureComponent<ShipGridLockComponent>(sourceGrid);
                var consoleLock = entities.EnsureComponent<ShuttleConsoleLockComponent>(console);
                consoleLocks.SetShuttleId(console, sourceGrid.ToString(), consoleLock);

                var cardGrid = mapManager.CreateGridEntity(sourceMap);
                maps.SetTile(cardGrid, Vector2i.Zero, new Tile(1));
                var cardCoordinates = new EntityCoordinates(cardGrid.Owner, new Vector2(0.5f, 0.5f));
                var ownerCard = entities.SpawnEntity("PassengerIDCard", cardCoordinates);
                var wrongCard = entities.SpawnEntity("PassengerIDCard", cardCoordinates);
                var guestCard = entities.SpawnEntity("PassengerIDCard", cardCoordinates);
                var onGridCard = entities.SpawnEntity(
                    "PassengerIDCard",
                    new EntityCoordinates(sourceGrid, Vector2.Zero));
                const string onGridCardName = "LuaM snapshot on-grid legacy deed";
                metadata.SetEntityName(onGridCard, onGridCardName);

                var ownerDeed = entities.EnsureComponent<ShuttleDeedComponent>(ownerCard);
                consoleLocks.SetDeedShipIdentity(ownerCard, sourceGrid, shipId, ownerDeed);
                var wrongShipId = Guid.NewGuid();
                var wrongDeed = entities.EnsureComponent<ShuttleDeedComponent>(wrongCard);
                consoleLocks.SetDeedShipIdentity(wrongCard, sourceGrid, wrongShipId, wrongDeed);
                var onGridDeed = entities.EnsureComponent<ShuttleDeedComponent>(onGridCard);
                consoleLocks.SetDeedShipIdentity(onGridCard, sourceGrid, null, onGridDeed);

                var guestAccess = entities.EnsureComponent<ShipGuestAccessComponent>(sourceGrid);
                guestAccess.GuestIdCards.Add(guestCard);

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);
                Assert.Multiple(() =>
                {
                    Assert.That(gridDeed.PersistentShipId, Is.EqualTo(canonicalShipId));
                    Assert.That(ownerDeed.PersistentShipId, Is.EqualTo(canonicalShipId));
                    Assert.That(ownerDeed.ShuttleUid, Is.EqualTo(sourceGrid));
                    Assert.That(onGridDeed.PersistentShipId, Is.EqualTo(canonicalShipId));
                    Assert.That(gridLock.ShuttleId, Is.EqualTo(canonicalShipId));
                    Assert.That(consoleLock.ShuttleId, Is.EqualTo(canonicalShipId));
                    Assert.That(guestAccess.GuestIdCards, Does.Contain(guestCard));
                });

                var canonicalPayload = Encoding.UTF8.GetString(snapshot.Payload);
                Assert.That(canonicalPayload, Does.Not.Contain("guestIdCards"));
                Assert.That(canonicalPayload, Does.Not.Contain("guestCyborgs"));

                // Model a payload written before stable lock binding existed.
                // The identity component stays authoritative while lock keys and
                // the grid deed deliberately contain legacy/non-GUID values.
                var legacyLines = canonicalPayload.Split('\n');
                var rewrittenLockKeys = 0;
                var rewrittenDeedKeys = 0;
                for (var i = 0; i < legacyLines.Length; i++)
                {
                    if (legacyLines[i].Contains("shuttleId:", StringComparison.Ordinal) &&
                        legacyLines[i].Contains(canonicalShipId, StringComparison.Ordinal))
                    {
                        legacyLines[i] = legacyLines[i].Replace(
                            canonicalShipId,
                            sourceGrid.ToString(),
                            StringComparison.Ordinal);
                        rewrittenLockKeys++;
                    }
                    else if (legacyLines[i].Contains("persistentShipId:", StringComparison.Ordinal) &&
                             legacyLines[i].Contains(canonicalShipId, StringComparison.Ordinal))
                    {
                        legacyLines[i] = legacyLines[i].Replace(
                            canonicalShipId,
                            $"legacy-{sourceGrid}",
                            StringComparison.Ordinal);
                        rewrittenDeedKeys++;
                    }
                }

                Assert.That(rewrittenLockKeys, Is.GreaterThan(0));
                Assert.That(rewrittenDeedKeys, Is.GreaterThan(0));
                var legacyPayload = Encoding.UTF8.GetBytes(string.Join('\n', legacyLines));
                snapshot = snapshot with
                {
                    Payload = legacyPayload,
                    PayloadSizeBytes = legacyPayload.Length,
                    PayloadHash = Convert.ToHexString(SHA256.HashData(legacyPayload)).ToLowerInvariant(),
                };

                entities.DeleteEntity(sourceGrid);
                Assert.That(entities.EntityExists(guestCard), Is.True,
                    "The off-grid guest card must not be pulled into or deleted with the ship graph.");
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredConsole = RequireShuttleConsole(entities, restoredGrid);
                var restoredGridDeed = entities.GetComponent<ShuttleDeedComponent>(restoredGrid);
                var restoredGridLock = entities.GetComponent<ShipGridLockComponent>(restoredGrid);
                var restoredConsoleLock = entities.GetComponent<ShuttleConsoleLockComponent>(restoredConsole);
                var restoredGuestAccess = entities.GetComponent<ShipGuestAccessComponent>(restoredGrid);
                var restoredOnGridCard = RequireNamedDescendant(entities, restoredGrid, onGridCardName);
                var restoredOnGridDeed = entities.GetComponent<ShuttleDeedComponent>(restoredOnGridCard);
                Assert.Multiple(() =>
                {
                    Assert.That(restoredGridDeed.ShuttleUid, Is.EqualTo(restoredGrid));
                    Assert.That(restoredGridDeed.PersistentShipId, Is.EqualTo(canonicalShipId));
                    Assert.That(restoredOnGridDeed.ShuttleUid, Is.EqualTo(restoredGrid));
                    Assert.That(restoredOnGridDeed.PersistentShipId, Is.EqualTo(canonicalShipId));
                    Assert.That(ownerDeed.ShuttleUid, Is.EqualTo(sourceGrid),
                        "Restore binding must not mutate an off-grid owner deed before placement succeeds.");
                    Assert.That(ownerDeed.PersistentShipId, Is.EqualTo(canonicalShipId));
                    Assert.That(restoredGridLock.ShuttleId, Is.EqualTo(canonicalShipId));
                    Assert.That(restoredConsoleLock.ShuttleId, Is.EqualTo(canonicalShipId));
                    Assert.That(restoredGuestAccess.GuestIdCards, Is.Empty);
                    Assert.That(restoredGuestAccess.GuestCyborgs, Is.Empty);
                });

                // A mismatched stable deed must not fall back to a deliberately
                // matching runtime UID. The real deed remains valid even though
                // its runtime UID still points at the deleted source grid.
                consoleLocks.SetDeedShipIdentity(wrongCard, restoredGrid, wrongShipId, wrongDeed);
                Assert.Multiple(() =>
                {
                    Assert.That(
                        SharedShuttleConsoleLockSystem.DeedsReferToSameShip(ownerDeed, restoredGridDeed),
                        Is.True);
                    Assert.That(
                        SharedShuttleConsoleLockSystem.DeedsReferToSameShip(wrongDeed, restoredGridDeed),
                        Is.False);
                });
                Assert.That(
                    consoleLocks.TryUnlock(
                        restoredConsole,
                        wrongCard,
                        restoredConsoleLock,
                        entities.GetComponent<IdCardComponent>(wrongCard)),
                    Is.False);
                Assert.That(consoleLocks.GetEffectiveLockState(restoredConsole, restoredConsoleLock), Is.True);
                Assert.That(
                    consoleLocks.TryUnlock(
                        restoredConsole,
                        ownerCard,
                        restoredConsoleLock,
                        entities.GetComponent<IdCardComponent>(ownerCard)),
                    Is.True);
                Assert.That(consoleLocks.GetEffectiveLockState(restoredConsole, restoredConsoleLock), Is.False);
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    [Test]
    public async Task LiveSpreaderQueueCapturesAndRestores()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid restoredGrid = EntityUid.Invalid;
        LuaMFullShipSnapshot snapshot = default!;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var grid = mapManager.CreateGridEntity(sourceMap);
                sourceGrid = grid.Owner;
                maps.SetTile(grid, Vector2i.Zero, new Tile(1));
                entities.EnsureComponent<SpreaderGridComponent>(sourceGrid);
                entities.RunMapInit(sourceGrid, entities.GetComponent<MetaDataComponent>(sourceGrid));
            });

            await pair.RunTicksSync(1);

            await server.WaitPost(() =>
            {
                var spreaderEntity = entities.SpawnEntity(
                    "Kudzu",
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                var edgeSpreader = entities.GetComponent<EdgeSpreaderComponent>(spreaderEntity);
                var spreader = entities.GetComponent<SpreaderGridComponent>(sourceGrid);
                spreader.SpreadQueues[edgeSpreader.Id].Clear();
                spreader.SpreadQueues[edgeSpreader.Id].Enqueue((spreaderEntity, edgeSpreader));
                Assert.That(
                    spreader.SpreadQueues.Values.Sum(queue => queue.Count),
                    Is.GreaterThan(0),
                    "The regression fixture must exercise a populated runtime spreader queue.");
                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                entities.DeleteEntity(sourceGrid);
                sourceGrid = EntityUid.Invalid;
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);
            });

            await pair.RunTicksSync(2);

            await server.WaitPost(() =>
            {
                var restoredSpreader = entities.GetComponent<SpreaderGridComponent>(restoredGrid);
                var activeSpreaderCount = 0;
                var query = entities.EntityQueryEnumerator<ActiveEdgeSpreaderComponent, TransformComponent>();
                while (query.MoveNext(out _, out _, out var xform))
                {
                    if (xform.GridUid == restoredGrid)
                        activeSpreaderCount++;
                }

                var payload = Encoding.UTF8.GetString(snapshot.Payload);
                var queueIndex = payload.IndexOf("spreadQueues", StringComparison.Ordinal);
                var queueExcerpt = queueIndex >= 0
                    ? payload.Substring(queueIndex, Math.Min(500, payload.Length - queueIndex))
                    : "spreadQueues field missing";
                Assert.That(
                    restoredSpreader.SpreadQueues.Values.Sum(queue => queue.Count),
                    Is.GreaterThan(0),
                    $"Restored spreader queues must be usable after map initialization. " +
                    $"Active spreaders on restored grid: {activeSpreaderCount}. Snapshot excerpt: {queueExcerpt}");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    [Test]
    public async Task LiveLatheQueueCapturesAndRestores()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var lathes = entities.System<LatheSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid restoredGrid = EntityUid.Invalid;
        LuaMFullShipSnapshot snapshot = default!;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var grid = mapManager.CreateGridEntity(sourceMap);
                sourceGrid = grid.Owner;
                maps.SetTile(grid, Vector2i.Zero, new Tile(1));

                var coordinates = new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f));
                var processor = entities.SpawnEntity("OreProcessor", coordinates);
                var actor = entities.SpawnEntity("MobMouse", coordinates);
                var lathe = entities.GetComponent<LatheComponent>(processor);
                var recipeId = lathes.GetAvailableRecipes(processor, lathe).First();
                var recipe = prototypes.Index<LatheRecipePrototype>(recipeId);
                lathe.Queue.Add(new LatheRecipeBatch(recipe, 1, 3, entities.GetNetEntity(actor)));

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                entities.DeleteEntity(sourceGrid);
                sourceGrid = EntityUid.Invalid;
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredProcessor = RequirePrototypeDescendant(entities, restoredGrid, "OreProcessor");
                var restoredLathe = entities.GetComponent<LatheComponent>(restoredProcessor);
                Assert.That(restoredLathe.Queue, Has.Count.EqualTo(1));
                Assert.Multiple(() =>
                {
                    Assert.That(restoredLathe.Queue[0].Recipe.ID, Is.EqualTo(recipeId.Id));
                    Assert.That(restoredLathe.Queue[0].ItemsPrinted, Is.EqualTo(1));
                    Assert.That(restoredLathe.Queue[0].ItemsRequested, Is.EqualTo(3));
                    Assert.That(restoredLathe.Queue[0].Actor, Is.Null,
                        "A restored queue must not retain a stale session entity reference.");
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    [Test]
    public async Task FullGridRoundTripPreservesShipStateWithoutPersistingBodies()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var session = server.PlayerMan.Sessions.Single();
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var atmosphere = entities.System<AtmosphereSystem>();
        var metadata = entities.System<MetaDataSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var buckle = entities.System<SharedBuckleSystem>();
        var apcs = entities.System<ApcSystem>();
        var batteries = entities.System<BatterySystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid restoredGrid = EntityUid.Invalid;
        EntityUid sourceGrid = EntityUid.Invalid;
        Tile hullTile = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                Assert.That(loader.TryLoadGrid(sourceMap, SeedPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                sourceGrid = loaded!.Value.Owner;
                var gridComponent = loaded.Value.Comp;

                hullTile = new Tile(tiles["FloorHullReinforced"].TileId);
                maps.SetTile(sourceGrid, gridComponent, new Vector2i(1, 0), hullTile);
                atmosphere.InvalidateTile(sourceGrid, Vector2i.Zero);
            });

            // Grid atmosphere is built from invalidated map tiles during normal
            // simulation. Mutating it before that would only mutate SpaceGas,
            // not state owned by the ship.
            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                var grid = sourceGrid;
                var gridComponent = entities.GetComponent<MapGridComponent>(grid);
                var coordinates = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));

                var mixture = atmosphere.GetTileMixture(grid, null, Vector2i.Zero);
                Assert.That(mixture, Is.Not.Null);
                Assert.That(mixture!.Immutable, Is.False, "The fixture must mutate ship-owned tile atmosphere.");
                mixture.Clear();
                mixture.SetMoles(Gas.Oxygen, 17.25f);
                mixture.SetMoles(Gas.Nitrogen, 31.5f);
                mixture.Temperature = 301.25f;

                var containerOwner = entities.SpawnEntity(null, coordinates);
                metadata.SetEntityName(containerOwner, ContainerOwnerName);
                var container = containers.EnsureContainer<Container>(containerOwner, ContainerId);
                var containedItem = entities.SpawnEntity("Crowbar", coordinates);
                metadata.SetEntityName(containedItem, ContainedItemName);
                Assert.That(containers.Insert(containedItem, container), Is.True);

                var looseItem = entities.SpawnEntity("Wrench", new EntityCoordinates(grid, new Vector2(1.25f, 0.75f)));
                metadata.SetEntityName(looseItem, LooseItemName);

                var mouse = entities.SpawnEntity("MobMouse", coordinates);
                metadata.SetEntityName(mouse, MouseName);
                Assert.That(entities.HasComponent<PlayerJobComponent>(mouse), Is.False,
                    "This body must exercise attach history independently of a station job.");
                Assert.That(server.PlayerMan.SetAttachedEntity(session, mouse, true), Is.True);
                Assert.That(server.PlayerMan.SetAttachedEntity(session, null, true), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.HasComponent<ActorComponent>(mouse), Is.False);
                    Assert.That(entities.HasComponent<PlayerJobComponent>(mouse), Is.False);
                    Assert.That(entities.HasComponent<LuaMPlayerControlledBodyComponent>(mouse), Is.True,
                        "PlayerAttachedEvent must leave durable round-local evidence after detach.");
                });

                var playerInventory = containers.EnsureContainer<Container>(mouse, PlayerInventoryId);
                var playerInventoryItem = entities.SpawnEntity("Crowbar", coordinates);
                metadata.SetEntityName(playerInventoryItem, PlayerInventoryItemName);
                Assert.That(containers.Insert(playerInventoryItem, playerInventory), Is.True);

                var playerSlotOwner = entities.SpawnEntity(null, coordinates);
                metadata.SetEntityName(playerSlotOwner, PlayerSlotOwnerName);
                var playerSlot = containers.EnsureContainer<ContainerSlot>(playerSlotOwner, PlayerSlotId);
                Assert.That(containers.Insert(mouse, playerSlot), Is.True);

                var strap = entities.SpawnEntity("Chair", coordinates);
                metadata.SetEntityName(strap, StrapName);
                var strappedMouse = entities.SpawnEntity("MobMouse", coordinates);
                metadata.SetEntityName(strappedMouse, StrappedMouseName);
                entities.EnsureComponent<PlayerJobComponent>(strappedMouse).JobPrototype = "Passenger";
                var strappedInventory = containers.EnsureContainer<Container>(strappedMouse, PlayerInventoryId);
                var strappedInventoryItem = entities.SpawnEntity("Wrench", coordinates);
                metadata.SetEntityName(strappedInventoryItem, StrappedInventoryItemName);
                Assert.That(containers.Insert(strappedInventoryItem, strappedInventory), Is.True);
                Assert.That(buckle.TryBuckle(strappedMouse, null, strap, popup: false), Is.True);

                Assert.That(
                    entities.GetComponent<MetaDataComponent>(mouse).EntityPrototype?.MapSavable,
                    Is.False,
                    "The fixture must exercise a mob prototype that normal map saving excludes.");

                var apc = entities.SpawnEntity("APCBasic", coordinates);
                metadata.SetEntityName(apc, ApcName);
                Assert.That(entities.HasComponent<ApcComponent>(apc), Is.True);
                apcs.ApcToggleBreaker(apc);
                Assert.That(entities.GetComponent<ApcComponent>(apc).MainBreakerEnabled, Is.False);

                var battery = entities.SpawnEntity(
                    "SubstationBasic",
                    new EntityCoordinates(grid, new Vector2(1.5f, 0.5f)));
                metadata.SetEntityName(battery, BatteryName);
                var batteryComponent = entities.GetComponent<BatteryComponent>(battery);
                batteries.SetCharge(battery, 432_123f, batteryComponent);
                Assert.That(batteryComponent.CurrentCharge, Is.EqualTo(432_123f).Within(0.01f));

                var shipId = persistence.GetOrAssignShipId(grid);
                Assert.That(shipId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(
                    persistence.TryCaptureSnapshot(grid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                Assert.Multiple(() =>
                {
                    Assert.That(snapshot.ShipId, Is.EqualTo(shipId));
                    Assert.That(snapshot.Revision, Is.EqualTo(1));
                    Assert.That(snapshot.PayloadSizeBytes, Is.EqualTo(snapshot.Payload.Length));
                    Assert.That(snapshot.PayloadHash, Has.Length.EqualTo(64));
                    Assert.That(snapshot.PrototypeManifestHash, Has.Length.EqualTo(64));
                    Assert.That(snapshot.EntityCount, Is.GreaterThan(5));
                    Assert.That(snapshot.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
                    Assert.That(
                        entities.GetComponent<LuaMShipIdentityComponent>(grid).SnapshotRevision,
                        Is.EqualTo(0),
                        "Capture must not advance the live revision before a DB commit.");
                    Assert.That(
                        entities.GetComponent<MetaDataComponent>(mouse).EntityPrototype?.MapSavable,
                        Is.False,
                        "Temporary MapSavable overrides must always be restored.");
                    Assert.That(
                        Encoding.UTF8.GetString(snapshot.Payload),
                        Does.Not.Contain("proto: MindBase"),
                        "Portable ship snapshots must not include nullspace mind entities.");
                    Assert.That(
                        Encoding.UTF8.GetString(snapshot.Payload),
                        Does.Not.Contain(MouseName),
                        "Portable ship snapshots must not include player bodies.");
                    Assert.That(
                        Encoding.UTF8.GetString(snapshot.Payload),
                        Does.Not.Contain(PlayerInventoryItemName),
                        "Inventory carried by a former player body must not become ship cargo.");
                    Assert.That(
                        Encoding.UTF8.GetString(snapshot.Payload),
                        Does.Not.Contain(StrappedMouseName),
                        "A buckled former player body must not become durable ship content.");
                    Assert.That(
                        Encoding.UTF8.GetString(snapshot.Payload),
                        Does.Not.Contain(StrappedInventoryItemName),
                        "Inventory on a buckled former player body must not become durable ship content.");
                    Assert.That(playerSlot.ContainedEntities, Is.EqualTo(new[] { mouse }),
                        "Capture must not mutate the live ship container.");
                    Assert.That(
                        entities.GetComponent<StrapComponent>(strap).BuckledEntities,
                        Does.Contain(strappedMouse),
                        "Capture must not unbuckle a live player.");
                });

                entities.DeleteEntity(grid);

                maps.CreateMap(out targetMap);

                var entityCountBeforeTamper = entities.GetEntities().Count();
                var corruptedPayload = (byte[])snapshot.Payload.Clone();
                corruptedPayload[^1] ^= 0x01;
                Assert.That(
                    persistence.TryRestoreSnapshot(
                        snapshot with { Payload = corruptedPayload },
                        targetMap,
                        out _,
                        out var corruptReason),
                    Is.False);
                Assert.That(corruptReason, Is.EqualTo("snapshot-payload-hash-mismatch"));
                Assert.That(entities.GetEntities().Count(), Is.EqualTo(entityCountBeforeTamper));

                var incompatibleFormat = snapshot with
                {
                    FormatVersion = LuaMFullShipPersistenceSystem.SnapshotFormatVersion + 1,
                };
                Assert.That(
                    persistence.TryRestoreSnapshot(incompatibleFormat, targetMap, out _, out var formatReason),
                    Is.False);
                Assert.That(
                    formatReason,
                    Is.EqualTo(
                        $"unsupported-snapshot-format-{LuaMFullShipPersistenceSystem.SnapshotFormatVersion + 1}"));

                var invalidManifest = snapshot with { PrototypeManifestHash = new string('0', 64) };
                Assert.That(
                    persistence.TryRestoreSnapshot(invalidManifest, targetMap, out _, out var manifestReason),
                    Is.False);
                Assert.That(manifestReason, Is.EqualTo("snapshot-payload-metadata-mismatch"));

                var wrongIdentity = snapshot with { ShipId = Guid.NewGuid() };
                Assert.That(
                    persistence.TryRestoreSnapshot(
                        wrongIdentity,
                        targetMap,
                        out _,
                        out var identityReason),
                    Is.False);
                Assert.That(identityReason, Is.EqualTo("restored-ship-identity-or-revision-mismatch"));
                Assert.That(
                    entities.GetEntities().Count(),
                    Is.EqualTo(entityCountBeforeTamper),
                    "A failed post-load validation must clean the grid and every auto-included entity.");

                Assert.That(
                    persistence.TryRestoreSnapshot(
                        snapshot,
                        targetMap,
                        out restoredGrid,
                        out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredIdentity = entities.GetComponent<LuaMShipIdentityComponent>(restoredGrid);
                Assert.Multiple(() =>
                {
                    Assert.That(restoredIdentity.ShipId, Is.EqualTo(shipId));
                    Assert.That(restoredIdentity.SnapshotRevision, Is.EqualTo(1));
                    Assert.That(
                        maps.GetTileRef(
                            restoredGrid,
                            entities.GetComponent<MapGridComponent>(restoredGrid),
                            new Vector2i(1, 0)).Tile.TypeId,
                        Is.EqualTo(hullTile.TypeId));
                });

                var restoredMixture = atmosphere.GetTileMixture(
                    restoredGrid,
                    null,
                    Vector2i.Zero);
                Assert.That(restoredMixture, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(restoredMixture!.GetMoles(Gas.Oxygen), Is.EqualTo(17.25f).Within(0.001f));
                    Assert.That(restoredMixture.GetMoles(Gas.Nitrogen), Is.EqualTo(31.5f).Within(0.001f));
                    Assert.That(restoredMixture.Temperature, Is.EqualTo(301.25f).Within(0.001f));
                });

                var restoredContainerOwner = RequireNamedDescendant(entities, restoredGrid, ContainerOwnerName);
                Assert.That(containers.TryGetContainer(restoredContainerOwner, ContainerId, out var restoredContainer), Is.True);
                Assert.That(restoredContainer, Is.Not.Null);
                Assert.That(restoredContainer!.ContainedEntities, Has.Count.EqualTo(1));
                Assert.That(
                    entities.GetComponent<MetaDataComponent>(restoredContainer.ContainedEntities[0]).EntityName,
                    Is.EqualTo(ContainedItemName));

                var restoredLooseItem = RequireNamedDescendant(entities, restoredGrid, LooseItemName);
                Assert.That(
                    entities.GetComponent<TransformComponent>(restoredLooseItem).LocalPosition,
                    Is.EqualTo(new Vector2(1.25f, 0.75f)));

                Assert.That(
                    FindNamedDescendants(entities, restoredGrid, MouseName),
                    Is.Empty,
                    "Restoring a shuttle must not restore any body that was aboard during capture.");
                Assert.That(
                    FindNamedDescendants(entities, restoredGrid, PlayerInventoryItemName),
                    Is.Empty,
                    "Restoring a shuttle must not restore inventory carried by an excluded body.");
                Assert.That(
                    FindNamedDescendants(entities, restoredGrid, StrappedMouseName),
                    Is.Empty,
                    "Restoring a shuttle must not restore a buckled former player body.");
                Assert.That(
                    FindNamedDescendants(entities, restoredGrid, StrappedInventoryItemName),
                    Is.Empty,
                    "Restoring a shuttle must not restore the buckled body's inventory.");

                var restoredPlayerSlotOwner =
                    RequireNamedDescendant(entities, restoredGrid, PlayerSlotOwnerName);
                Assert.That(
                    containers.TryGetContainer(
                        restoredPlayerSlotOwner,
                        PlayerSlotId,
                        out var restoredPlayerSlot),
                    Is.True);
                Assert.That(restoredPlayerSlot!.ContainedEntities, Is.Empty,
                    "A slot must not retain EntityUid.Invalid after its player body was excluded.");

                var restoredStrap = RequireNamedDescendant(entities, restoredGrid, StrapName);
                Assert.That(
                    entities.GetComponent<StrapComponent>(restoredStrap).BuckledEntities,
                    Is.Empty,
                    "A strap must not retain EntityUid.Invalid after its player body was excluded.");

                var restoredApc = RequireNamedDescendant(entities, restoredGrid, ApcName);
                Assert.That(entities.GetComponent<ApcComponent>(restoredApc).MainBreakerEnabled, Is.False);

                var restoredBattery = RequireNamedDescendant(entities, restoredGrid, BatteryName);
                Assert.That(
                    entities.GetComponent<BatteryComponent>(restoredBattery).CurrentCharge,
                    Is.EqualTo(432_123f).Within(0.01f));
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    [Test]
    public async Task LegacyV1SnapshotIsRejectedBeforeDeserialization()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var loader = entities.System<MapLoaderSystem>();
        var metadata = entities.System<MetaDataSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var minds = entities.System<MindSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap);
                maps.SetTile(sourceGrid, sourceGrid, Vector2i.Zero, new Tile(1));
                var coordinates = new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f));

                var slotOwner = entities.SpawnEntity(null, coordinates);
                metadata.SetEntityName(slotOwner, LegacySlotOwnerName);
                var slot = containers.EnsureContainer<ContainerSlot>(slotOwner, PlayerSlotId);

                var body = entities.SpawnEntity("MobMouse", coordinates);
                metadata.SetEntityName(body, LegacyBodyName);
                entities.EnsureComponent<PlayerJobComponent>(body).JobPrototype = "Passenger";
                var inventory = containers.EnsureContainer<Container>(body, PlayerInventoryId);
                var inventoryItem = entities.SpawnEntity("Crowbar", coordinates);
                metadata.SetEntityName(inventoryItem, LegacyInventoryItemName);
                Assert.That(containers.Insert(inventoryItem, inventory), Is.True);
                Assert.That(containers.Insert(body, slot), Is.True);

                var mind = minds.CreateMind(null, LegacyMindName);
                minds.TransferTo(mind, body, createGhost: false, mind: mind.Comp);

                var shipId = persistence.GetOrAssignShipId(sourceGrid);
                entities.GetComponent<LuaMShipIdentityComponent>(sourceGrid).SnapshotRevision = 1;

                var changedPrototypes = EnableMapSavingForTransformGraph(entities, sourceGrid);
                string legacyYaml;
                try
                {
                    using var writer = new StringWriter();
                    var options = SerializationOptions.Default with
                    {
                        MissingEntityBehaviour = MissingEntityBehaviour.IncludeNullspace,
                        EntityExceptionBehaviour = EntityExceptionBehaviour.Rethrow,
                        ErrorOnOrphan = true,
                        LogAutoInclude = null,
                    };
                    Assert.That(loader.TrySaveGrid(sourceGrid, writer, options), Is.True);
                    legacyYaml = writer.ToString();
                }
                finally
                {
                    foreach (var (prototype, mapSavable) in changedPrototypes)
                        prototype.MapSavable = mapSavable;
                }

                Assert.Multiple(() =>
                {
                    Assert.That(legacyYaml, Does.Contain(LegacyBodyName));
                    Assert.That(legacyYaml, Does.Contain(LegacyInventoryItemName));
                    Assert.That(legacyYaml, Does.Contain("proto: MindBase"));
                });

                var snapshot = BuildLegacySnapshot(legacyYaml, shipId, revision: 1);
                minds.TransferTo(mind, null, createGhost: false, mind: mind.Comp);
                entities.DeleteEntity(mind);
                entities.DeleteEntity(sourceGrid);

                maps.CreateMap(out targetMap);
                var entityCountBeforeRestore = entities.GetEntities().Count();
                Assert.That(
                    persistence.TryRestoreSnapshot(
                        snapshot,
                        targetMap,
                        out var restoredGrid,
                        out var restoreReason),
                    Is.False);

                Assert.Multiple(() =>
                {
                    Assert.That(snapshot.FormatVersion,
                        Is.EqualTo(LuaMFullShipPersistenceSystem.LegacySnapshotFormatVersion));
                    Assert.That(
                        LuaMFullShipPersistenceSystem.IsSupportedSnapshotFormatVersion(snapshot.FormatVersion),
                        Is.False);
                    Assert.That(restoredGrid, Is.EqualTo(EntityUid.Invalid));
                    Assert.That(restoreReason, Is.EqualTo("unsupported-snapshot-format-1"));
                    Assert.That(entities.GetEntities().Count(), Is.EqualTo(entityCountBeforeRestore),
                        "Unsupported legacy data must be rejected before the map loader creates any entities.");
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });

            pair.Kill();
        }
    }

    private static Dictionary<EntityPrototype, bool> EnableMapSavingForTransformGraph(
        IEntityManager entities,
        EntityUid root)
    {
        var changed = new Dictionary<EntityPrototype, bool>();
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);

        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !entities.EntityExists(uid))
                continue;

            var prototype = entities.GetComponent<MetaDataComponent>(uid).EntityPrototype;
            if (prototype is { MapSavable: false } && changed.TryAdd(prototype, prototype.MapSavable))
                prototype.MapSavable = true;

            var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        return changed;
    }

    private static LuaMFullShipSnapshot BuildLegacySnapshot(string yaml, Guid shipId, long revision)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode) stream.Documents.Single().RootNode;
        var meta = (YamlMappingNode) RequireYamlChild(root, "meta");
        var entityCount = int.Parse(
            ((YamlScalarNode) RequireYamlChild(meta, "entityCount")).Value!,
            System.Globalization.CultureInfo.InvariantCulture);

        var prototypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = (YamlSequenceNode) RequireYamlChild(root, "entities");
        foreach (var groupNode in groups.Children.Cast<YamlMappingNode>())
        {
            var prototype = ((YamlScalarNode) RequireYamlChild(groupNode, "proto")).Value ?? string.Empty;
            var entities = (YamlSequenceNode) RequireYamlChild(groupNode, "entities");
            prototypeCounts[prototype] =
                prototypeCounts.GetValueOrDefault(prototype) + entities.Children.Count;
        }

        Assert.That(prototypeCounts.Values.Sum(), Is.EqualTo(entityCount));
        var manifest = new StringBuilder();
        foreach (var (prototype, count) in prototypeCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            manifest.Append(prototype).Append('\t').Append(count).Append('\n');

        var payload = Encoding.UTF8.GetBytes(yaml);
        return new LuaMFullShipSnapshot(
            LuaMFullShipPersistenceSystem.LegacySnapshotFormatVersion,
            shipId,
            revision,
            DateTime.UtcNow,
            payload,
            payload.Length,
            ComputeSha256(payload),
            entityCount,
            ComputeSha256(Encoding.UTF8.GetBytes(manifest.ToString())),
            "legacy-v1-runtime-fixture",
            "legacy-v1-runtime-fixture");
    }

    private static YamlNode RequireYamlChild(YamlMappingNode mapping, string key)
    {
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is YamlScalarNode { Value: var value } && value == key)
                return valueNode;
        }

        Assert.Fail($"Missing YAML key '{key}'.");
        return null!;
    }

    private static YamlMappingNode RequireComponent(YamlSequenceNode components, string type)
    {
        foreach (var component in components.Children.Cast<YamlMappingNode>())
        {
            if (((YamlScalarNode) RequireYamlChild(component, "type")).Value == type)
                return component;
        }

        Assert.Fail($"Missing component '{type}'.");
        return null!;
    }

    private static string ComputeSha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static EntityUid RequireNamedDescendant(IEntityManager entities, EntityUid root, string name)
    {
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !entities.EntityExists(uid))
                continue;

            if (entities.GetComponent<MetaDataComponent>(uid).EntityName == name)
                return uid;

            var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        Assert.Fail($"Could not find restored descendant named '{name}'.");
        return EntityUid.Invalid;
    }

    private static IReadOnlyList<EntityUid> FindNamedDescendants(
        IEntityManager entities,
        EntityUid root,
        string name)
    {
        var result = new List<EntityUid>();
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !entities.EntityExists(uid))
                continue;

            if (entities.GetComponent<MetaDataComponent>(uid).EntityName == name)
                result.Add(uid);

            var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        return result;
    }

    private static EntityUid RequireShuttleConsole(IEntityManager entities, EntityUid grid)
    {
        var query = entities.EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == grid)
                return uid;
        }

        Assert.Fail($"Could not find a shuttle console on grid {grid}.");
        return EntityUid.Invalid;
    }

    private static EntityUid RequirePrototypeDescendant(IEntityManager entities, EntityUid root, string prototypeId)
    {
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !entities.EntityExists(uid))
                continue;

            if (entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototypeId)
                return uid;

            var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        Assert.Fail($"Could not find restored descendant with prototype '{prototypeId}'.");
        return EntityUid.Invalid;
    }
}
