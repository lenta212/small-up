#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Content.Server._NF.CryoSleep;
using Content.Server.Database;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Maps;
using Content.Shared.Power.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Moq;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShipPersistenceOrchestratorRuntimeTest
{
    private static readonly ResPath SeedPath = new("/Maps/_LuaM/ShipGen/shipgen_seed.yml");

    [Test]
    public async Task ActualSqliteLifecycleSurvivesOrchestratorRestartWithoutCopyingPlayerBody()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var database = server.ResolveDependency<IServerDbManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var batteries = entities.System<BatterySystem>();
        var consoleLocks = entities.System<ShuttleConsoleLockSystem>();
        var metadataSystem = entities.System<MetaDataSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
        var owner = pair.Client.Session!.UserId;
        var now = DateTime.UtcNow;
        MapId sourceMap = default;
        MapId restoreMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid retainedBody = EntityUid.Invalid;
        EntityUid restoredGrid = EntityUid.Invalid;
        Guid shipId = Guid.Empty;
        const string cargoName = "LuaM SQLite E2E durable cargo";
        const string machineName = "LuaM SQLite E2E charged machine";
        const string consoleName = "LuaM SQLite E2E secured console";
        const string bodyName = "LuaM SQLite E2E excluded player body";
        const string bodyItemName = "LuaM SQLite E2E retained body item";

        async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await server.WaitPost(() =>
            {
                _ = CompleteAsync();
                return;

                async Task CompleteAsync()
                {
                    try
                    {
                        completion.SetResult(await callback());
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                }
            });
            return await completion.Task;
        }

        try
        {
            EntityUid cargo = EntityUid.Invalid;
            EntityUid machine = EntityUid.Invalid;
            EntityUid console = EntityUid.Invalid;
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                Assert.That(loader.TryLoadGrid(sourceMap, SeedPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                sourceGrid = loaded!.Value.Owner;
                var coordinates = new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f));

                cargo = entities.SpawnEntity("Crowbar", coordinates);
                metadataSystem.SetEntityName(cargo, cargoName);

                machine = entities.SpawnEntity("SubstationBasic", coordinates);
                metadataSystem.SetEntityName(machine, machineName);

                console = entities.SpawnEntity("ComputerShuttle", coordinates);
                metadataSystem.SetEntityName(console, consoleName);

                retainedBody = entities.SpawnEntity("MobMouse", coordinates);
                metadataSystem.SetEntityName(retainedBody, bodyName);
                entities.EnsureComponent<PlayerJobComponent>(retainedBody).JobPrototype = "Passenger";
                var bodyInventory = containers.EnsureContainer<Container>(
                    retainedBody,
                    "luam-sqlite-e2e-body-inventory");
                var bodyItem = entities.SpawnEntity("Wrench", coordinates);
                metadataSystem.SetEntityName(bodyItem, bodyItemName);
                Assert.That(containers.Insert(bodyItem, bodyInventory), Is.True);
            });

            orchestrator.ConfigureForTesting(database, runtime, "sqlite-e2e-first-process");
            var registered = await RunOnServerAsync(() => orchestrator.RegisterAsync(
                sourceGrid,
                owner,
                new LuaMShipSnapshotMetadata(
                    "VesselTestPersistence",
                    "SQLite E2E Ship",
                    "SQL-E2E",
                    125_000,
                    false,
                    610),
                now));
            Assert.That(registered.Success, Is.True, registered.Reason);
            shipId = orchestrator.ActiveLeases.Single().ShipId;

            var diagnosticWithPassenger = await RunOnServerAsync(() => Task.FromResult(
                orchestrator.GetActiveShipDiagnostics(shipId).Single()));
            Assert.Multiple(() =>
            {
                Assert.That(diagnosticWithPassenger.GridExists, Is.True);
                Assert.That(diagnosticWithPassenger.MobStateEntityCount, Is.EqualTo(1));
            });

            var refusedEmergencySave = await RunOnServerAsync(() =>
                orchestrator.EmergencyStoreAndDeleteActiveShipAsync(
                    shipId,
                    "sqlite-e2e-test",
                    now.AddMilliseconds(500)));
            Assert.Multiple(() =>
            {
                Assert.That(refusedEmergencySave.Success, Is.False);
                Assert.That(
                    refusedEmergencySave.Status,
                    Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidState));
                Assert.That(refusedEmergencySave.Reason, Does.Contain("1 MobState"));
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1));
            });

            await server.WaitPost(() =>
            {
                batteries.SetCharge(machine, 432_123f, entities.GetComponent<BatteryComponent>(machine));
                var consoleLock = entities.GetComponent<ShuttleConsoleLockComponent>(console);
                Assert.That(consoleLock.ShuttleId, Is.EqualTo(shipId.ToString("D")));
                consoleLocks.SetShuttleId(console, string.Empty, consoleLock);
                Assert.That(entities.GetComponent<ShipGridLockComponent>(sourceGrid).Locked, Is.False);
            });

            var stored = await RunOnServerAsync(() => orchestrator.StoreAndDeactivateAsync(
                shipId,
                610,
                now.AddSeconds(1)));
            Assert.That(stored.Success, Is.True, stored.Reason);

            var durableStored = await database.GetLuaMShipSnapshotAsync(shipId, owner);
            Assert.Multiple(() =>
            {
                Assert.That(durableStored, Is.Not.Null);
                Assert.That(durableStored!.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
                Assert.That(durableStored.PayloadRevision, Is.EqualTo(2));
                Assert.That(durableStored.PayloadSizeBytes, Is.GreaterThan(0));
                Assert.That(orchestrator.ActiveLeases, Is.Empty);
            });

            await server.WaitPost(() =>
            {
                transforms.SetParent(retainedBody, maps.GetMap(sourceMap));
                entities.DeleteEntity(sourceGrid);
                sourceGrid = EntityUid.Invalid;
                Assert.That(entities.EntityExists(retainedBody), Is.True);
                maps.CreateMap(out restoreMap);
                orchestrator.ConfigureForTesting(database, runtime, "sqlite-e2e-restarted-process");
            });

            var restored = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                shipId,
                owner,
                611,
                restoreMap,
                now.AddSeconds(2),
                _ => true));
            Assert.That(restored.Success, Is.True, restored.Reason);
            restoredGrid = restored.Grid ?? EntityUid.Invalid;

            await server.WaitPost(() =>
            {
                var restoredCargo = FindNamedDescendants(entities, restoredGrid, cargoName).Single();
                var restoredMachine = FindNamedDescendants(entities, restoredGrid, machineName).Single();
                var restoredConsole = FindNamedDescendants(entities, restoredGrid, consoleName).Single();
                var restoredLock = entities.GetComponent<ShuttleConsoleLockComponent>(restoredConsole);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(restoredCargo), Is.True);
                    Assert.That(
                        entities.GetComponent<BatteryComponent>(restoredMachine).CurrentCharge,
                        Is.EqualTo(432_123f).Within(0.01f));
                    Assert.That(entities.GetComponent<ShipGridLockComponent>(restoredGrid).Locked, Is.False);
                    Assert.That(restoredLock.ShuttleId, Is.EqualTo(shipId.ToString("D")));
                    Assert.That(FindNamedDescendants(entities, restoredGrid, bodyName), Is.Empty);
                    Assert.That(FindNamedDescendants(entities, restoredGrid, bodyItemName), Is.Empty);
                    Assert.That(
                        entities.GetEntities()
                            .Count(uid => entities.EntityExists(uid) &&
                                          entities.GetComponent<MetaDataComponent>(uid).EntityName == bodyName),
                        Is.EqualTo(1),
                        "Only the retained live body may exist after restore.");
                });
            });

            var durableActive = await database.GetLuaMShipSnapshotAsync(shipId, owner);
            Assert.Multiple(() =>
            {
                Assert.That(durableActive, Is.Not.Null);
                Assert.That(durableActive!.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Active));
                Assert.That(durableActive.PayloadRevision, Is.EqualTo(2));
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1));
            });

            var diagnosticWithoutPassenger = await RunOnServerAsync(() => Task.FromResult(
                orchestrator.GetActiveShipDiagnostics(shipId).Single()));
            Assert.That(diagnosticWithoutPassenger.MobStateEntityCount, Is.Zero);

            var emergencyStored = await RunOnServerAsync(() =>
                orchestrator.EmergencyStoreAndDeleteActiveShipAsync(
                    shipId,
                    "sqlite-e2e-test",
                    now.AddSeconds(3)));
            Assert.That(emergencyStored.Success, Is.True, emergencyStored.Reason);
            Assert.That(orchestrator.ActiveLeases, Is.Empty);

            await server.WaitRunTicks(1);
            Assert.That(entities.EntityExists(restoredGrid), Is.False);
            restoredGrid = EntityUid.Invalid;

            var durableEmergencyStored = await database.GetLuaMShipSnapshotAsync(shipId, owner);
            Assert.Multiple(() =>
            {
                Assert.That(durableEmergencyStored, Is.Not.Null);
                Assert.That(durableEmergencyStored!.Status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
                Assert.That(durableEmergencyStored.PayloadRevision, Is.EqualTo(3));
            });

            var retired = await database.RetireLuaMShipSnapshotAsync(new LuaMShipSnapshotRetireRequest(
                Guid.NewGuid(),
                shipId,
                owner,
                durableEmergencyStored!.Revision,
                null,
                "sqlite e2e cleanup",
                now.AddSeconds(4)));
            Assert.That(retired.Success, Is.True, retired.Status.ToString());
            Assert.That(
                (await database.GetLuaMShipSnapshotAsync(shipId, owner))!.Status,
                Is.EqualTo(DbLuaMShipSnapshotStatus.Retired));
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(restoredGrid))
                    entities.DeleteEntity(restoredGrid);
                if (entities.EntityExists(sourceGrid))
                    entities.DeleteEntity(sourceGrid);
                if (entities.EntityExists(retainedBody))
                    entities.DeleteEntity(retainedBody);
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(restoreMap))
                    maps.DeleteMap(restoreMap);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task LegacySnapshotFormatIsQuarantinedInsteadOfReturnedToStored()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var shipId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var payload = "{}"u8.ToArray();
        var store = new LuaMShipSnapshotStoreRequest(
            shipId,
            owner,
            null,
            1,
            null,
            "VesselLegacyPersistence",
            "Legacy Format Ship",
            null,
            100_000,
            false,
            700,
            LuaMFullShipPersistenceSystem.LegacySnapshotFormatVersion,
            LuaMFullShipPersistenceSystem.LegacySnapshotFormatVersion,
            payload,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload.Length,
            1,
            "legacy-build",
            new string('a', 64),
            now);
        var stored = ToRecord(
            store,
            12,
            DbLuaMShipSnapshotStatus.Stored,
            null,
            null,
            null,
            null);
        MapId targetMap = default;
        Guid claimedLeaseId = default;

        database
            .Setup(db => db.GetLuaMShipSnapshotAsync(
                shipId,
                owner,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) =>
            {
                claimedLeaseId = request.LeaseId;
                var claimed = ToRecord(
                    store,
                    13,
                    DbLuaMShipSnapshotStatus.Restoring,
                    request.LeaseId,
                    request.ServerInstanceId,
                    request.RestoreRoundId,
                    1);
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    shipId,
                    13,
                    DbLuaMShipSnapshotStatus.Restoring,
                    request.LeaseId,
                    1,
                    claimed);
            });
        database
            .Setup(db => db.QuarantineLuaMShipSnapshotAsync(
                It.IsAny<LuaMShipSnapshotQuarantineRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipSnapshotQuarantineRequest request, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.ShipId, Is.EqualTo(shipId));
                    Assert.That(request.OwnerUserId, Is.EqualTo(owner));
                    Assert.That(request.ExpectedRevision, Is.EqualTo(13));
                    Assert.That(request.LeaseId, Is.EqualTo(claimedLeaseId));
                    Assert.That(request.Reason, Is.EqualTo("unsupported database snapshot format 1/1"));
                });
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    shipId,
                    14,
                    DbLuaMShipSnapshotStatus.Quarantined);
            });

        async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await server.WaitPost(() =>
            {
                _ = CompleteAsync();
                return;

                async Task CompleteAsync()
                {
                    try
                    {
                        completion.SetResult(await callback());
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                }
            });
            return await completion.Task;
        }

        try
        {
            await server.WaitPost(() => maps.CreateMap(out targetMap));
            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "legacy-quarantine-test");

            var result = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                shipId,
                owner,
                701,
                targetMap,
                now.AddSeconds(1)));

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.False);
                Assert.That(result.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidRequest));
                Assert.That(result.Reason, Is.EqualTo("unsupported database snapshot format 1/1"));
                Assert.That(orchestrator.ActiveLeases, Is.Empty);
            });
            database.Verify(
                db => db.AbortLuaMShipRestoreAsync(
                    It.IsAny<LuaMShipRestoreAbortRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task ActiveShipSavesAtRoundEndAndRestoresNextRound()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var firstRound = 77;
        var nextRound = 78;
        var now = DateTime.UtcNow;
        var initialLeaseId = Guid.Empty;
        var nextLeaseId = Guid.Empty;
        LuaMShipSnapshotStoreRequest? initialStore = null;
        LuaMShipSnapshotStoreRequest? roundEndStore = null;
        MapId sourceMap = default;
        MapId restoreMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;

        database
            .Setup(db => db.StoreLuaMShipSnapshotAsync(
                It.IsAny<LuaMShipSnapshotStoreRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipSnapshotStoreRequest request, CancellationToken _) =>
            {
                if (request.ExpectedRevision == null)
                {
                    initialStore = request;
                    return new(
                        LuaMShipPersistenceWriteStatus.Success,
                        request.ShipId,
                        0,
                        DbLuaMShipSnapshotStatus.Stored);
                }

                if (request.ExpectedRevision == 5)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(request.LeaseId, Is.EqualTo(nextLeaseId));
                        Assert.That(request.SourceRoundId, Is.EqualTo(nextRound));
                        Assert.That(request.PayloadRevision, Is.EqualTo(3));
                    });
                    return new(
                        LuaMShipPersistenceWriteStatus.Success,
                        request.ShipId,
                        6,
                        DbLuaMShipSnapshotStatus.Stored);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(2));
                    Assert.That(request.LeaseId, Is.EqualTo(initialLeaseId));
                    Assert.That(request.SourceRoundId, Is.EqualTo(firstRound));
                    Assert.That(request.PayloadRevision, Is.EqualTo(2));
                });

                roundEndStore = request;
                return new(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    3,
                    DbLuaMShipSnapshotStatus.Stored);
            });
        database
            .Setup(db => db.GetLuaMShipSnapshotAsync(
                It.IsAny<Guid>(),
                owner,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid shipId, NetUserId _, CancellationToken _) =>
            {
                Assert.That(roundEndStore, Is.Not.Null);
                return ToRecord(roundEndStore!, 3, DbLuaMShipSnapshotStatus.Stored, null, null, null, null);
            });
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) =>
            {
                if (request.ExpectedRevision == 0)
                {
                    initialLeaseId = request.LeaseId;
                    Assert.That(request.RestoreRoundId, Is.EqualTo(firstRound));
                    return new(
                        LuaMShipPersistenceWriteStatus.Success,
                        request.ShipId,
                        1,
                        DbLuaMShipSnapshotStatus.Restoring,
                        request.LeaseId,
                        0);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(3));
                    Assert.That(request.RestoreRoundId, Is.EqualTo(nextRound));
                    Assert.That(roundEndStore, Is.Not.Null);
                });

                nextLeaseId = request.LeaseId;
                return new(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    4,
                    DbLuaMShipSnapshotStatus.Restoring,
                    request.LeaseId,
                    1,
                    ToRecord(roundEndStore!, 4, DbLuaMShipSnapshotStatus.Restoring, request.LeaseId,
                        "orchestrator-round-transfer-test", nextRound, 1));
            });
        database
            .Setup(db => db.CompleteLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreCompleteRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreCompleteRequest request, CancellationToken _) =>
            {
                if (request.ExpectedRevision == 1)
                {
                    Assert.That(request.LeaseId, Is.EqualTo(initialLeaseId));
                    return new(
                        LuaMShipPersistenceWriteStatus.Success,
                        request.ShipId,
                        2,
                        DbLuaMShipSnapshotStatus.Active,
                        request.LeaseId,
                        0);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(4));
                    Assert.That(request.LeaseId, Is.EqualTo(nextLeaseId));
                });
                return new(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    5,
                    DbLuaMShipSnapshotStatus.Active,
                    request.LeaseId,
                    1);
            });

        try
        {
            async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
            {
                var resultTask = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                await server.WaitPost(() =>
                {
                    _ = CompleteAsync();
                    return;

                    async Task CompleteAsync()
                    {
                        try
                        {
                            resultTask.SetResult(await callback());
                        }
                        catch (Exception exception)
                        {
                            resultTask.SetException(exception);
                        }
                    }
                });
                return await resultTask.Task;
            }

            async Task RunOnServerActionAsync(Func<Task> callback)
            {
                var resultTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await server.WaitPost(() =>
                {
                    _ = CompleteAsync();
                    return;

                    async Task CompleteAsync()
                    {
                        try
                        {
                            await callback();
                            resultTask.SetResult();
                        }
                        catch (Exception exception)
                        {
                            resultTask.SetException(exception);
                        }
                    }
                });
                await resultTask.Task;
            }

            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                maps.CreateMap(out restoreMap);
                Assert.That(loader.TryLoadGrid(sourceMap, SeedPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                sourceGrid = loaded!.Value.Owner;
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(
                database.Object,
                runtime,
                "orchestrator-round-transfer-test");
            var metadata = new LuaMShipSnapshotMetadata(
                "VesselTestPersistence",
                "Round Transfer Test Ship",
                "RT-77",
                125_000,
                false,
                firstRound);
            var register = await RunOnServerAsync(() => orchestrator.RegisterAsync(
                sourceGrid,
                owner,
                metadata,
                now));

            Assert.Multiple(() =>
            {
                Assert.That(register.Success, Is.True, register.Reason);
                Assert.That(register.Revision, Is.EqualTo(2));
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1));
                Assert.That(orchestrator.ActiveLeases.Single().RegistryRevision, Is.EqualTo(2));
                Assert.That(orchestrator.ActiveLeases.Single().PayloadRevision, Is.EqualTo(1));
                Assert.That(initialStore, Is.Not.Null);
            });

            await RunOnServerActionAsync(() => orchestrator.SaveAllActiveShipsAsync(firstRound, now.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(orchestrator.ActiveLeases, Is.Empty);
                Assert.That(roundEndStore, Is.Not.Null);
                Assert.That(entities.EntityExists(sourceGrid), Is.True);
            });
            var roundEndEnvelope = JsonSerializer.Deserialize<LuaMFullShipSnapshot>(roundEndStore!.Payload);
            Assert.That(roundEndEnvelope, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(roundEndEnvelope!.Revision, Is.EqualTo(2));
                Assert.That(roundEndStore.PayloadRevision, Is.EqualTo(2));
                Assert.That(roundEndStore.PayloadHash, Is.EqualTo(
                    Convert.ToHexString(SHA256.HashData(roundEndStore.Payload)).ToLowerInvariant()));
            });

            await server.WaitPost(() => entities.DeleteEntity(sourceGrid));

            var restore = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                roundEndStore.ShipId,
                owner,
                nextRound,
                restoreMap,
                now.AddSeconds(20)));

            Assert.Multiple(() =>
            {
                Assert.That(restore.Success, Is.True, restore.Reason);
                Assert.That(restore.Revision, Is.EqualTo(5));
                Assert.That(restore.LeaseId, Is.EqualTo(nextLeaseId));
                Assert.That(restore.Grid, Is.Not.Null);
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1));
                Assert.That(orchestrator.ActiveLeases.Single().RegistryRevision, Is.EqualTo(5));
                Assert.That(orchestrator.ActiveLeases.Single().PayloadRevision, Is.EqualTo(2));
            });
            Assert.That(entities.EntityExists(restore.Grid!.Value), Is.True);
            Assert.That(entities.GetComponent<LuaMShipIdentityComponent>(restore.Grid.Value).SnapshotRevision,
                Is.EqualTo(2));

            await RunOnServerActionAsync(() => orchestrator.SaveAllActiveShipsAsync(nextRound, now.AddSeconds(30)));
            Assert.That(orchestrator.ActiveLeases, Is.Empty);

            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(sourceGrid))
                    entities.DeleteEntity(sourceGrid);
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(restoreMap))
                    maps.DeleteMap(restoreMap);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task FailedPlacementCanRetrySnapshotAndBackfillLegacyPayloadRevision()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var docking = entities.System<DockingSystem>();
        var shuttles = entities.System<ShuttleSystem>();
        var stations = entities.System<StationSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var lifecycleRevision = 430L;
        var status = DbLuaMShipSnapshotStatus.Stored;
        var leaseId = Guid.Empty;
        var abortCount = 0;
        var completeCount = 0;
        var releaseCount = 0;
        LuaMShipSnapshotStoreRequest? legacyStore = null;
        LuaMShipSnapshotStoreRequest? replacementStore = null;
        MapId sourceMap = default;
        MapId restoreMap = default;
        MapId targetMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid sourceStation = EntityUid.Invalid;
        EntityUid targetGrid = EntityUid.Invalid;
        EntityUid targetStation = EntityUid.Invalid;
        EntityUid targetGate = EntityUid.Invalid;
        EntityUid failedPlacementGrid = EntityUid.Invalid;
        EntityUid failedCompletionGrid = EntityUid.Invalid;
        EntityUid failedCompletionShipDock = EntityUid.Invalid;
        DockingComponent? failedCompletionShipDockComponent = null;
        EntityUid retainedPassenger = EntityUid.Invalid;
        EntityUid retainedItem = EntityUid.Invalid;
        MapCoordinates retainedPassengerCoordinates = default;
        Angle retainedPassengerRotation = default;
        MapCoordinates retainedItemCoordinates = default;
        Angle retainedItemRotation = default;
        EntityUid restoredGrid = EntityUid.Invalid;
        EntityUid restoredShipDock = EntityUid.Invalid;
        HashSet<EntityUid>? firstRestoreBaseline = null;
        HashSet<EntityUid>? secondRestoreBaseline = null;
        HashSet<EntityUid>? failedPlacementEntities = null;
        HashSet<EntityUid>? failedCompletionEntities = null;

        void AssertRestoreGraphRolledBack(
            IReadOnlySet<EntityUid>? failedEntities,
            IReadOnlySet<EntityUid>? restoreBaseline,
            string message)
        {
            Assert.That(failedEntities, Is.Not.Null);
            Assert.That(restoreBaseline, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(failedEntities!.All(uid => !entities.EntityExists(uid)), Is.True,
                    $"{message}: every entity created by deserialization must be gone.");
                Assert.That(entities.GetEntities().ToHashSet().SetEquals(restoreBaseline!), Is.True,
                    $"{message}: rollback must preserve the exact pre-existing entity set.");
            });
        }

        void AssertRetainedPassengerSurvived(string message)
        {
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(retainedPassenger), Is.True,
                    $"{message}: the retained passenger must survive rollback.");
                Assert.That(entities.EntityExists(retainedItem), Is.True,
                    $"{message}: the passenger's retained item must survive rollback.");
            });
            if (!entities.EntityExists(retainedPassenger) || !entities.EntityExists(retainedItem))
                return;

            var passengerTransform = entities.GetComponent<TransformComponent>(retainedPassenger);
            var passengerCoordinates = transforms.GetMapCoordinates(retainedPassenger, passengerTransform);
            var itemTransform = entities.GetComponent<TransformComponent>(retainedItem);
            var itemCoordinates = transforms.GetMapCoordinates(retainedItem, itemTransform);
            Assert.Multiple(() =>
            {
                Assert.That(passengerTransform.ParentUid, Is.EqualTo(maps.GetMap(targetMap)),
                    $"{message}: rollback must move the retained subtree off the deleted grid.");
                Assert.That(passengerCoordinates.MapId, Is.EqualTo(retainedPassengerCoordinates.MapId));
                Assert.That(passengerCoordinates.Position.X,
                    Is.EqualTo(retainedPassengerCoordinates.Position.X).Within(0.001f));
                Assert.That(passengerCoordinates.Position.Y,
                    Is.EqualTo(retainedPassengerCoordinates.Position.Y).Within(0.001f));
                Assert.That(transforms.GetWorldRotation(passengerTransform).Theta,
                    Is.EqualTo(retainedPassengerRotation.Theta).Within(0.001));
                Assert.That(itemTransform.ParentUid, Is.EqualTo(maps.GetMap(targetMap)),
                    $"{message}: the retained item must be ejected from the deleted container.");
                Assert.That(containers.IsEntityInContainer(retainedItem), Is.False,
                    $"{message}: the retained item must not keep stale container membership.");
                Assert.That(itemCoordinates.MapId, Is.EqualTo(retainedItemCoordinates.MapId));
                Assert.That(itemCoordinates.Position.X,
                    Is.EqualTo(retainedItemCoordinates.Position.X).Within(0.001f));
                Assert.That(itemCoordinates.Position.Y,
                    Is.EqualTo(retainedItemCoordinates.Position.Y).Within(0.001f));
                Assert.That(transforms.GetWorldRotation(itemTransform).Theta,
                    Is.EqualTo(retainedItemRotation.Theta).Within(0.001));
            });
        }

        database
            .Setup(db => db.GetLuaMShipSnapshotAsync(
                It.IsAny<Guid>(),
                owner,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid shipId, NetUserId _, CancellationToken _) =>
            {
                var durableStore = replacementStore ?? legacyStore;
                Assert.Multiple(() =>
                {
                    Assert.That(durableStore, Is.Not.Null);
                    Assert.That(shipId, Is.EqualTo(durableStore!.ShipId));
                    Assert.That(status, Is.AnyOf(
                        DbLuaMShipSnapshotStatus.Stored,
                        DbLuaMShipSnapshotStatus.Restoring,
                        DbLuaMShipSnapshotStatus.Active));
                });
                var leasePresent = status is DbLuaMShipSnapshotStatus.Restoring or
                    DbLuaMShipSnapshotStatus.Active;
                return ToRecord(
                    durableStore!,
                    lifecycleRevision,
                    status,
                    leasePresent ? leaseId : null,
                    leasePresent ? "legacy-retry-test" : null,
                    leasePresent ? 431 : null,
                    leasePresent ? 0 : null) with
                {
                    PayloadRevision = replacementStore == null ? null : durableStore!.PayloadRevision,
                };
            });
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(lifecycleRevision));
                    Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
                });
                leaseId = request.LeaseId;
                status = DbLuaMShipSnapshotStatus.Restoring;
                lifecycleRevision++;
                var durableStore = replacementStore ?? legacyStore!;
                var record = ToRecord(
                    durableStore,
                    lifecycleRevision,
                    status,
                    leaseId,
                    request.ServerInstanceId,
                    request.RestoreRoundId,
                    0) with
                {
                    PayloadRevision = replacementStore == null ? null : durableStore.PayloadRevision,
                };
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    leaseId,
                    0,
                    record);
            });
        database
            .Setup(db => db.AbortLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreAbortRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreAbortRequest request, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(lifecycleRevision));
                    Assert.That(request.LeaseId, Is.EqualTo(leaseId));
                    Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Restoring));
                });
                AssertRestoreGraphRolledBack(
                    failedPlacementEntities,
                    firstRestoreBaseline,
                    "Placement rollback before DB abort");
                abortCount++;
                status = DbLuaMShipSnapshotStatus.Stored;
                lifecycleRevision++;
                leaseId = Guid.Empty;
                var durableStore = replacementStore ?? legacyStore!;
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    Snapshot: ToRecord(durableStore, lifecycleRevision, status, null, null, null, null) with
                    {
                        PayloadRevision = replacementStore == null ? null : durableStore.PayloadRevision,
                    });
            });
        database
            .Setup(db => db.CompleteLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreCompleteRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreCompleteRequest request, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(lifecycleRevision));
                    Assert.That(request.LeaseId, Is.EqualTo(leaseId));
                    Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Restoring));
                });
                completeCount++;
                status = DbLuaMShipSnapshotStatus.Active;
                lifecycleRevision++;
                if (completeCount == 2)
                {
                    // Simulate a player and carried item entering the restored
                    // graph while the durable completion request is in flight.
                    // They predate deserialization and therefore must not be
                    // recursively deleted with the restored grid on lost reply.
                    Assert.That(failedCompletionGrid.Valid, Is.True);
                    transforms.SetParent(retainedPassenger, failedCompletionGrid);
                    var restoredContainer = containers.EnsureContainer<Container>(
                        failedCompletionGrid,
                        "restore-rollback-retained-item");
                    Assert.That(containers.Insert(retainedItem, restoredContainer), Is.True);
                    retainedPassengerCoordinates = transforms.GetMapCoordinates(retainedPassenger);
                    retainedPassengerRotation = transforms.GetWorldRotation(retainedPassenger);
                    retainedItemCoordinates = transforms.GetMapCoordinates(retainedItem);
                    retainedItemRotation = transforms.GetWorldRotation(retainedItem);
                    Assert.Multiple(() =>
                    {
                        Assert.That(entities.GetComponent<TransformComponent>(retainedPassenger).ParentUid,
                            Is.EqualTo(failedCompletionGrid));
                        Assert.That(entities.GetComponent<TransformComponent>(retainedItem).ParentUid,
                            Is.EqualTo(failedCompletionGrid));
                        Assert.That(containers.IsEntityInContainer(retainedItem), Is.True);
                    });
                    throw new InvalidOperationException(
                        "simulated lost response after the durable completion commit");
                }

                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    leaseId,
                    0);
            });
        database
            .Setup(db => db.ReleaseLuaMShipPresenceAsync(
                It.IsAny<LuaMShipPresenceReleaseRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipPresenceReleaseRequest request, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Active));
                    Assert.That(request.ExpectedRevision, Is.EqualTo(lifecycleRevision));
                    Assert.That(request.LeaseId, Is.EqualTo(leaseId));
                    Assert.That(entities.EntityExists(targetGate), Is.True);
                    Assert.That(entities.EntityExists(failedCompletionShipDock), Is.False);
                    Assert.That(failedCompletionShipDockComponent, Is.Not.Null);
                    Assert.That(failedCompletionShipDockComponent!.DockedWith, Is.Null,
                        "The owned side must be explicitly undocked before its entity is deleted.");
                    Assert.That(entities.GetComponent<DockingComponent>(targetGate).DockedWith, Is.Null,
                        "The pre-existing station gate must be released before DB rollback awaits.");
                });
                AssertRestoreGraphRolledBack(
                    failedCompletionEntities,
                    secondRestoreBaseline,
                    "Committed completion rollback before presence release");
                AssertRetainedPassengerSurvived("Committed completion rollback before presence release");

                releaseCount++;
                status = DbLuaMShipSnapshotStatus.Stored;
                lifecycleRevision++;
                leaseId = Guid.Empty;
                var durableStore = replacementStore ?? legacyStore!;
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    Snapshot: ToRecord(durableStore, lifecycleRevision, status, null, null, null, null) with
                    {
                        PayloadRevision = replacementStore == null ? null : durableStore.PayloadRevision,
                    });
            });
        database
            .Setup(db => db.StoreLuaMShipSnapshotAsync(
                It.IsAny<LuaMShipSnapshotStoreRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipSnapshotStoreRequest request, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Active));
                    Assert.That(request.ExpectedRevision, Is.EqualTo(lifecycleRevision));
                    Assert.That(request.PayloadRevision, Is.EqualTo(2));
                    Assert.That(request.LeaseId, Is.EqualTo(leaseId));
                });
                replacementStore = request;
                status = DbLuaMShipSnapshotStatus.Stored;
                lifecycleRevision++;
                leaseId = Guid.Empty;
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    Snapshot: ToRecord(request, lifecycleRevision, status, null, null, null, null));
            });

        try
        {
            async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
            {
                var resultTask = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                await server.WaitPost(() =>
                {
                    _ = CompleteAsync();
                    return;

                    async Task CompleteAsync()
                    {
                        try
                        {
                            resultTask.SetResult(await callback());
                        }
                        catch (Exception exception)
                        {
                            resultTask.SetException(exception);
                        }
                    }
                });
                return await resultTask.Task;
            }

            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                maps.CreateMap(out restoreMap);
                maps.CreateMap(out targetMap);
                Assert.That(loader.TryLoadGrid(sourceMap, SeedPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                sourceGrid = loaded!.Value.Owner;
                Assert.That(loader.TryLoadGrid(targetMap, SeedPath, out var targetLoaded), Is.True);
                Assert.That(targetLoaded, Is.Not.Null);
                targetGrid = targetLoaded!.Value.Owner;
                var gameMap = prototypes.Index<GameMapPrototype>("McChicken");
                sourceStation = stations.InitializeNewStation(
                    gameMap.Stations["McChicken"],
                    [sourceGrid],
                    "LuaM rollback fixture station");
                targetStation = stations.InitializeNewStation(
                    gameMap.Stations["McChicken"],
                    [targetGrid],
                    "LuaM rollback target station");
                entities.SpawnEntity(
                    "AirlockShuttle",
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                targetGate = entities.SpawnEntity(
                    "AirlockShuttle",
                    new EntityCoordinates(targetGrid, new Vector2(0.5f, 0.5f)));
                Assert.That(runtime.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var reason),
                    Is.True,
                    reason);
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                legacyStore = new LuaMShipSnapshotStoreRequest(
                    snapshot.ShipId,
                    owner,
                    null,
                    snapshot.Revision,
                    null,
                    "VesselTestPersistence",
                    "Legacy Retry Test Ship",
                    "LR-430",
                    125_000,
                    false,
                    430,
                    LuaMFullShipPersistenceSystem.SnapshotFormatVersion,
                    snapshot.FormatVersion,
                    payload,
                    Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                    payload.Length,
                    snapshot.EntityCount,
                    snapshot.SourceBuildVersion,
                    snapshot.PrototypeManifestHash,
                    now);
                entities.DeleteEntity(sourceGrid);
                entities.DeleteEntity(sourceStation);
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "legacy-retry-test");
            await server.WaitPost(() => firstRestoreBaseline = entities.GetEntities().ToHashSet());
            var first = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(1),
                restored =>
                {
                    failedPlacementGrid = restored;
                    failedPlacementEntities = entities.GetEntities()
                        .Where(uid => !firstRestoreBaseline!.Contains(uid))
                        .ToHashSet();
                    Assert.Multiple(() =>
                    {
                        Assert.That(entities.HasComponent<StationMemberComponent>(restored), Is.False,
                            "A portable snapshot must not retain its ignored live station membership.");
                        Assert.That(failedPlacementEntities!.Contains(failedPlacementGrid), Is.True);
                    });
                    transforms.SetLocalPosition(restored, new Vector2(20f, 0f));
                    return false;
                }));

            Assert.Multiple(() =>
            {
                Assert.That(first.Success, Is.False);
                Assert.That(first.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidState));
                Assert.That(abortCount, Is.EqualTo(1));
                Assert.That(lifecycleRevision, Is.EqualTo(432));
                Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            });

            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(failedPlacementGrid), Is.False,
                    "A rejected placement must not leak the partially moved restored grid.");
                Assert.That(failedPlacementEntities!.All(uid => !entities.EntityExists(uid)), Is.True,
                    "A rejected placement must leave no deserialized support entity residue.");
                Assert.That(entities.GetEntities().ToHashSet().SetEquals(firstRestoreBaseline!), Is.True,
                    "A rejected placement must preserve the exact pre-existing entity set.");
            });
            await pair.RunTicksSync(2);

            var second = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(2),
                restored =>
                {
                    Assert.That(entities.HasComponent<StationMemberComponent>(restored), Is.False,
                        "Retry must keep the portable hull detached from the source station root.");
                    return true;
                }));
            restoredGrid = second.Grid ?? EntityUid.Invalid;

            Assert.Multiple(() =>
            {
                Assert.That(second.Success, Is.True, second.Reason);
                Assert.That(second.Revision, Is.EqualTo(434));
                Assert.That(restoredGrid.Valid, Is.True);
                Assert.That(entities.HasComponent<StationMemberComponent>(restoredGrid), Is.False);
                Assert.That(abortCount, Is.EqualTo(1));
                Assert.That(completeCount, Is.EqualTo(1));
                Assert.That(lifecycleRevision, Is.EqualTo(434));
                Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Active));
                Assert.That(orchestrator.ActiveLeases.Single().RegistryRevision, Is.EqualTo(434));
                Assert.That(orchestrator.ActiveLeases.Single().PayloadRevision, Is.EqualTo(1));
            });

            var stored = await RunOnServerAsync(() => orchestrator.StoreAndDeactivateAsync(
                legacyStore!.ShipId,
                431,
                now.AddSeconds(3)));
            Assert.Multiple(() =>
            {
                Assert.That(stored.Success, Is.True, stored.Reason);
                Assert.That(stored.Revision, Is.EqualTo(435));
                Assert.That(replacementStore, Is.Not.Null);
                Assert.That(replacementStore!.PayloadRevision, Is.EqualTo(2));
                Assert.That(JsonSerializer.Deserialize<LuaMFullShipSnapshot>(replacementStore.Payload)!.Revision,
                    Is.EqualTo(2));
            });

            await server.WaitPost(() =>
            {
                entities.DeleteEntity(restoredGrid);
                restoredGrid = EntityUid.Invalid;
                retainedPassenger = entities.SpawnEntity(
                    "MobHuman",
                    new EntityCoordinates(targetGrid, new Vector2(0.25f, 0.25f)));
                retainedItem = entities.SpawnEntity(
                    "Crowbar",
                    new EntityCoordinates(retainedPassenger, new Vector2(0.1f, 0f)));
                secondRestoreBaseline = entities.GetEntities().ToHashSet();
            });
            var third = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(4),
                restored =>
                {
                    failedCompletionGrid = restored;
                    Assert.That(entities.HasComponent<StationMemberComponent>(restored), Is.False,
                        "A restored portable hull must not deserialize its old station root.");
                    Assert.That(entities.GetComponent<DockingComponent>(targetGate).Docked, Is.False,
                        "The exact target gate must be free before the real placement attempt.");
                    Assert.That(
                        shuttles.TryFTLDockAtDock(
                            restored,
                            entities.GetComponent<ShuttleComponent>(restored),
                            targetGrid,
                            targetGate),
                        Is.True,
                        "The completion-failure fixture must perform the real geometry-checked placement.");
                    var placedDock = docking.GetDocks(restored)
                        .Single(candidate => candidate.Comp.DockedWith == targetGate);
                    failedCompletionShipDock = placedDock.Owner;
                    failedCompletionShipDockComponent = placedDock.Comp;
                    Assert.That(
                        entities.GetComponent<DockingComponent>(targetGate).DockedWith,
                        Is.EqualTo(failedCompletionShipDock));
                    failedCompletionEntities = entities.GetEntities()
                        .Where(uid => !secondRestoreBaseline!.Contains(uid))
                        .ToHashSet();
                    Assert.Multiple(() =>
                    {
                        Assert.That(failedCompletionEntities.Contains(failedCompletionShipDock), Is.True,
                            "The dock being removed must belong to the exact restored entity set.");
                        Assert.That(failedCompletionEntities.Contains(targetGate), Is.False,
                            "The pre-existing target gate must remain outside restore ownership.");
                        Assert.That(failedCompletionEntities.Contains(retainedPassenger), Is.False,
                            "The pre-existing passenger must remain outside restore ownership.");
                        Assert.That(failedCompletionEntities.Contains(retainedItem), Is.False,
                            "The pre-existing carried item must remain outside restore ownership.");
                    });
                    return true;
                }));

            Assert.Multiple(() =>
            {
                Assert.That(third.Success, Is.False);
                Assert.That(third.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.UnknownOutcome));
                Assert.That(abortCount, Is.EqualTo(1));
                Assert.That(releaseCount, Is.EqualTo(1));
                Assert.That(completeCount, Is.EqualTo(2));
                Assert.That(lifecycleRevision, Is.EqualTo(438));
                Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            });
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(failedCompletionGrid), Is.False,
                    "A completion exception must delete the restored grid before rolling back the claim.");
                Assert.That(failedCompletionEntities!.All(uid => !entities.EntityExists(uid)), Is.True,
                    "A completion exception must leave no deserialized support entity residue.");
                Assert.That(failedCompletionShipDockComponent, Is.Not.Null);
                Assert.That(failedCompletionShipDockComponent!.DockedWith, Is.Null,
                    "Rollback must explicitly undock the owned port before deleting it.");
                Assert.That(entities.GetComponent<DockingComponent>(targetGate).DockedWith, Is.Null,
                    "Rollback must leave the external station gate reusable.");
                Assert.That(entities.GetEntities().ToHashSet().SetEquals(secondRestoreBaseline!), Is.True,
                    "A completion exception must preserve the exact pre-existing entity set.");
            });
            AssertRetainedPassengerSurvived("Completion exception result");
            await pair.RunTicksSync(2);

            var fourth = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(5),
                restored =>
                {
                    Assert.That(entities.HasComponent<StationMemberComponent>(restored), Is.False,
                        "Immediate retry must still be a station-independent portable hull.");
                    Assert.That(entities.GetComponent<DockingComponent>(targetGate).Docked, Is.False,
                        "The same station gate must be reusable immediately after rollback.");
                    Assert.That(
                        shuttles.TryFTLDockAtDock(
                            restored,
                            entities.GetComponent<ShuttleComponent>(restored),
                            targetGrid,
                            targetGate),
                        Is.True,
                        "Immediate retry must pass the same real docking geometry.");
                    restoredShipDock = docking.GetDocks(restored)
                        .Single(candidate => candidate.Comp.DockedWith == targetGate)
                        .Owner;
                    return true;
                }));
            restoredGrid = fourth.Grid ?? EntityUid.Invalid;

            Assert.Multiple(() =>
            {
                Assert.That(fourth.Success, Is.True, fourth.Reason);
                Assert.That(fourth.Revision, Is.EqualTo(440));
                Assert.That(restoredGrid.Valid, Is.True);
                Assert.That(entities.HasComponent<StationMemberComponent>(restoredGrid), Is.False);
                Assert.That(completeCount, Is.EqualTo(3));
                Assert.That(orchestrator.ActiveLeases.Single().RegistryRevision, Is.EqualTo(440));
                Assert.That(orchestrator.ActiveLeases.Single().PayloadRevision, Is.EqualTo(2));
                Assert.That(entities.GetComponent<LuaMShipIdentityComponent>(restoredGrid).SnapshotRevision,
                    Is.EqualTo(2));
                Assert.That(
                    entities.GetComponent<DockingComponent>(restoredShipDock).DockedWith,
                    Is.EqualTo(targetGate));
                Assert.That(
                    entities.GetComponent<DockingComponent>(targetGate).DockedWith,
                    Is.EqualTo(restoredShipDock));
            });
            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(restoredShipDock) &&
                    entities.TryGetComponent<DockingComponent>(restoredShipDock, out var restoredDock) &&
                    restoredDock.DockedWith == targetGate)
                {
                    docking.Undock((restoredShipDock, restoredDock));
                }
                if (entities.EntityExists(sourceGrid))
                    entities.DeleteEntity(sourceGrid);
                if (entities.EntityExists(sourceStation))
                    entities.DeleteEntity(sourceStation);
                if (entities.EntityExists(retainedPassenger))
                    entities.DeleteEntity(retainedPassenger);
                if (entities.EntityExists(retainedItem))
                    entities.DeleteEntity(retainedItem);
                if (entities.EntityExists(targetStation))
                    entities.DeleteEntity(targetStation);
                if (entities.EntityExists(failedPlacementGrid))
                    entities.DeleteEntity(failedPlacementGrid);
                if (entities.EntityExists(failedCompletionGrid))
                    entities.DeleteEntity(failedCompletionGrid);
                if (entities.EntityExists(restoredGrid))
                    entities.DeleteEntity(restoredGrid);
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(restoreMap))
                    maps.DeleteMap(restoreMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task RegistrationDurablyActivatesThePurchasedGrid()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var leaseId = Guid.Empty;
        var recoveryCount = 0;
        LuaMShipSnapshotStoreRequest? storedRequest = null;
        MapId mapId = default;
        EntityUid grid = EntityUid.Invalid;

        database
            .Setup(db => db.StoreLuaMShipSnapshotAsync(
                It.IsAny<LuaMShipSnapshotStoreRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<LuaMShipSnapshotStoreRequest, CancellationToken>((request, _) => storedRequest = request)
            .ReturnsAsync((LuaMShipSnapshotStoreRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                0,
                DbLuaMShipSnapshotStatus.Stored));
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.Is<LuaMShipRestoreClaimRequest>(request =>
                    request.OwnerUserId == owner &&
                    request.ExpectedRevision == 0 &&
                    request.RestoreRoundId == 42),
                It.IsAny<CancellationToken>()))
            .Callback<LuaMShipRestoreClaimRequest, CancellationToken>((request, _) => leaseId = request.LeaseId)
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                1,
                DbLuaMShipSnapshotStatus.Restoring,
                request.LeaseId,
                0));
        database
            .Setup(db => db.CompleteLuaMShipRestoreAsync(
                It.Is<LuaMShipRestoreCompleteRequest>(request =>
                    request.OwnerUserId == owner &&
                    request.ExpectedRevision == 1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreCompleteRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                2,
                DbLuaMShipSnapshotStatus.Active,
                request.LeaseId,
                0));
        database
            .Setup(db => db.RenewLuaMShipLeaseAsync(
                It.Is<LuaMShipLeaseRenewRequest>(request =>
                    request.OwnerUserId == owner &&
                    request.ExpectedSnapshotRevision == 2 &&
                    request.LeaseId == leaseId &&
                    request.ExpectedLeaseRevision == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipLeaseRenewRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.LeaseConflict,
                request.ShipId,
                request.ExpectedSnapshotRevision,
                DbLuaMShipSnapshotStatus.Active,
                request.LeaseId,
                request.ExpectedLeaseRevision));
        database
            .Setup(db => db.ReleaseLuaMShipPresenceAsync(
                It.Is<LuaMShipPresenceReleaseRequest>(request =>
                    request.OwnerUserId == owner &&
                    request.ExpectedRevision == 2 &&
                    request.LeaseId == leaseId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipPresenceReleaseRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.UnknownOutcome,
                request.ShipId,
                3,
                DbLuaMShipSnapshotStatus.Stored));
        database
            .Setup(db => db.GetLuaMShipSnapshotAsync(
                It.IsAny<Guid>(),
                owner,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ToRecord(
                    storedRequest!,
                    3,
                    DbLuaMShipSnapshotStatus.Stored,
                    null,
                    null,
                    null,
                    null));
        database
            .Setup(db => db.RecoverExpiredLuaMShipLeasesAsync(
                It.IsAny<DateTime>(),
                100,
                It.IsAny<CancellationToken>()))
            .Callback(() => recoveryCount++)
            .ReturnsAsync(1);

        try
        {
            async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                await server.WaitPost(() =>
                {
                    _ = CompleteAsync();
                    return;

                    async Task CompleteAsync()
                    {
                        try
                        {
                            completion.SetResult(await callback());
                        }
                        catch (Exception exception)
                        {
                            completion.SetException(exception);
                        }
                    }
                });
                return await completion.Task;
            }

            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                Assert.That(loader.TryLoadGrid(mapId, SeedPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                grid = loaded!.Value.Owner;
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(
                database.Object,
                runtime,
                "orchestrator-runtime-test");
            var result = await RunOnServerAsync(() => orchestrator.RegisterAsync(
                grid,
                owner,
                new LuaMShipSnapshotMetadata(
                    "VesselTestPersistence",
                    "Purchased Test Ship",
                    "PT-42",
                    125_000,
                    false,
                    42),
                now));

            Assert.That(result, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(result!.Success, Is.True, result.Reason);
                Assert.That(result.Revision, Is.EqualTo(2));
                Assert.That(result.LeaseId, Is.EqualTo(leaseId));
                Assert.That(result.Grid, Is.EqualTo(grid));
                Assert.That(storedRequest, Is.Not.Null);
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1));
                Assert.That(orchestrator.ActiveLeases.Single().RegistryRevision, Is.EqualTo(2));
                Assert.That(orchestrator.ActiveLeases.Single().PayloadRevision, Is.EqualTo(1));
                Assert.That(entities.GetComponent<LuaMShipIdentityComponent>(grid).SnapshotRevision, Is.EqualTo(1));
            });

            var envelope = JsonSerializer.Deserialize<LuaMFullShipSnapshot>(storedRequest!.Payload);
            Assert.That(envelope, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(envelope!.Revision, Is.EqualTo(1));
                Assert.That(storedRequest.PayloadRevision, Is.EqualTo(1));
                Assert.That(storedRequest.PayloadSizeBytes, Is.EqualTo(storedRequest.Payload.Length));
                Assert.That(storedRequest.PayloadHash, Is.EqualTo(
                    Convert.ToHexString(SHA256.HashData(storedRequest.Payload)).ToLowerInvariant()));
            });

            var transientRenew = await RunOnServerAsync(() => orchestrator.RenewAsync(
                storedRequest.ShipId,
                now.AddSeconds(1)));
            Assert.Multiple(() =>
            {
                Assert.That(transientRenew.Success, Is.False);
                Assert.That(transientRenew.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.LeaseConflict));
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1),
                    "A transient renew failure must keep the live grid tracked for retry and cleanup.");
            });

            await server.WaitPost(() => entities.DeleteEntity(grid));
            var missingGrid = await RunOnServerAsync(() => orchestrator.RenewAsync(
                storedRequest.ShipId,
                now.AddSeconds(2)));
            Assert.Multiple(() =>
            {
                Assert.That(missingGrid.Success, Is.True, missingGrid.Reason);
                Assert.That(missingGrid.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.AlreadyProcessed));
                Assert.That(orchestrator.ActiveLeases, Is.Empty);
            });

            await RunOnServerAsync(async () =>
            {
                await orchestrator.MaintainLeasesAsync(now.AddMinutes(6));
                return true;
            });
            Assert.That(recoveryCount, Is.EqualTo(1),
                "Periodic maintenance must recover orphan leases even when no ship is active in memory.");
            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(grid))
                    entities.DeleteEntity(grid);
                if (maps.MapExists(mapId))
                    maps.DeleteMap(mapId);
            });
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task FailedLiveCaptureStaysTrackedUntilFinalCleanupReleasesPresence()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var leaseId = Guid.Empty;
        var releaseCount = 0;
        LuaMShipSnapshotStoreRequest? initialStore = null;
        MapId mapId = default;
        EntityUid grid = EntityUid.Invalid;
        EntityUid duplicate = EntityUid.Invalid;

        database
            .Setup(db => db.StoreLuaMShipSnapshotAsync(
                It.Is<LuaMShipSnapshotStoreRequest>(request => request.ExpectedRevision == null),
                It.IsAny<CancellationToken>()))
            .Callback<LuaMShipSnapshotStoreRequest, CancellationToken>((request, _) => initialStore = request)
            .ReturnsAsync((LuaMShipSnapshotStoreRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                0,
                DbLuaMShipSnapshotStatus.Stored));
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<LuaMShipRestoreClaimRequest, CancellationToken>((request, _) => leaseId = request.LeaseId)
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                1,
                DbLuaMShipSnapshotStatus.Restoring,
                request.LeaseId,
                0));
        database
            .Setup(db => db.CompleteLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreCompleteRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreCompleteRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                2,
                DbLuaMShipSnapshotStatus.Active,
                request.LeaseId,
                0));
        database
            .Setup(db => db.ReleaseLuaMShipPresenceAsync(
                It.Is<LuaMShipPresenceReleaseRequest>(request =>
                    request.ExpectedRevision == 2 &&
                    request.LeaseId == leaseId),
                It.IsAny<CancellationToken>()))
            .Callback(() => releaseCount++)
            .ReturnsAsync((LuaMShipPresenceReleaseRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                3,
                DbLuaMShipSnapshotStatus.Stored,
                Snapshot: ToRecord(
                    initialStore!,
                    3,
                    DbLuaMShipSnapshotStatus.Stored,
                    null,
                    null,
                    null,
                    null)));

        try
        {
            async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                await server.WaitPost(() =>
                {
                    _ = CompleteAsync();
                    return;

                    async Task CompleteAsync()
                    {
                        try
                        {
                            completion.SetResult(await callback());
                        }
                        catch (Exception exception)
                        {
                            completion.SetException(exception);
                        }
                    }
                });
                return await completion.Task;
            }

            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                Assert.That(loader.TryLoadGrid(mapId, SeedPath, out var loaded), Is.True);
                Assert.That(loader.TryLoadGrid(mapId, SeedPath, out var duplicateLoaded), Is.True);
                grid = loaded!.Value.Owner;
                duplicate = duplicateLoaded!.Value.Owner;
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "final-cleanup-test");
            var registered = await RunOnServerAsync(() => orchestrator.RegisterAsync(
                grid,
                owner,
                new LuaMShipSnapshotMetadata(
                    "VesselTestPersistence",
                    "Final Cleanup Test Ship",
                    "FC-90",
                    125_000,
                    false,
                    90),
                now));
            Assert.That(registered.Success, Is.True, registered.Reason);

            await server.WaitPost(() =>
            {
                var identity = entities.EnsureComponent<LuaMShipIdentityComponent>(duplicate);
                identity.ShipId = initialStore!.ShipId;
                identity.SnapshotRevision = initialStore.PayloadRevision;
            });

            var manualStore = await RunOnServerAsync(() => orchestrator.StoreAndDeactivateAsync(
                initialStore!.ShipId,
                90,
                now.AddSeconds(1)));
            Assert.Multiple(() =>
            {
                Assert.That(manualStore.Success, Is.False);
                Assert.That(manualStore.Reason, Does.Contain("duplicate-active-ship-identity"));
                Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1),
                    "A failed manual capture of a live grid must remain retryable.");
                Assert.That(releaseCount, Is.Zero);
            });

            await RunOnServerAsync(async () =>
            {
                await orchestrator.FinalizeRoundCleanupAsync(90, now.AddSeconds(2));
                return true;
            });
            Assert.Multiple(() =>
            {
                Assert.That(releaseCount, Is.EqualTo(1));
                Assert.That(orchestrator.ActiveLeases, Is.Empty,
                    "Final cleanup must release the lease after the repeated capture failure.");
            });
            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(grid))
                    entities.DeleteEntity(grid);
                if (entities.EntityExists(duplicate))
                    entities.DeleteEntity(duplicate);
                if (maps.MapExists(mapId))
                    maps.DeleteMap(mapId);
            });
            await pair.CleanReturnAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegistrationClaimFailureRetiresOrReturnsUnknownWhenTheDurableRowIsUnreadable(
        bool unreadableAfterStore)
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var durableStatus = DbLuaMShipSnapshotStatus.Stored;
        LuaMShipSnapshotStoreRequest? initialStore = null;
        MapId mapId = default;
        EntityUid grid = EntityUid.Invalid;

        database
            .Setup(db => db.StoreLuaMShipSnapshotAsync(
                It.IsAny<LuaMShipSnapshotStoreRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<LuaMShipSnapshotStoreRequest, CancellationToken>((request, _) => initialStore = request)
            .ReturnsAsync((LuaMShipSnapshotStoreRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.Success,
                request.ShipId,
                0,
                DbLuaMShipSnapshotStatus.Stored));
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) => new(
                LuaMShipPersistenceWriteStatus.LeaseConflict,
                request.ShipId,
                0,
                DbLuaMShipSnapshotStatus.Stored));
        database
            .Setup(db => db.GetLuaMShipSnapshotAsync(
                It.IsAny<Guid>(),
                owner,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => unreadableAfterStore
                ? null
                : ToRecord(
                    initialStore!,
                    0,
                    durableStatus,
                    null,
                    null,
                    null,
                    null));
        if (!unreadableAfterStore)
        {
            database
                .Setup(db => db.RetireLuaMShipSnapshotAsync(
                    It.Is<LuaMShipSnapshotRetireRequest>(request =>
                        request.OwnerUserId == owner &&
                        request.ExpectedRevision == 0 &&
                        request.LeaseId == null),
                    It.IsAny<CancellationToken>()))
                .Callback(() => durableStatus = DbLuaMShipSnapshotStatus.Retired)
                .ReturnsAsync((LuaMShipSnapshotRetireRequest request, CancellationToken _) => new(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    1,
                    DbLuaMShipSnapshotStatus.Retired));
        }

        try
        {
            async Task<T> RunOnServerAsync<T>(Func<Task<T>> callback)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                await server.WaitPost(() =>
                {
                    _ = CompleteAsync();
                    return;

                    async Task CompleteAsync()
                    {
                        try
                        {
                            completion.SetResult(await callback());
                        }
                        catch (Exception exception)
                        {
                            completion.SetException(exception);
                        }
                    }
                });
                return await completion.Task;
            }

            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                Assert.That(loader.TryLoadGrid(mapId, SeedPath, out var loaded), Is.True);
                grid = loaded!.Value.Owner;
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "registration-compensation-test");
            var result = await RunOnServerAsync(() => orchestrator.RegisterAsync(
                grid,
                owner,
                new LuaMShipSnapshotMetadata(
                    "VesselTestPersistence",
                    "Compensation Test Ship",
                    "RC-91",
                    125_000,
                    false,
                    91),
                now));

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.False);
                Assert.That(result.Status, Is.EqualTo(unreadableAfterStore
                    ? LuaMShipPersistenceWriteStatus.UnknownOutcome
                    : LuaMShipPersistenceWriteStatus.LeaseConflict));
                Assert.That(durableStatus, Is.EqualTo(unreadableAfterStore
                        ? DbLuaMShipSnapshotStatus.Stored
                        : DbLuaMShipSnapshotStatus.Retired),
                    unreadableAfterStore
                        ? "An unreadable post-store state must fail closed instead of authorizing a refund."
                        : "A post-store registration failure must not leave a callable Stored row.");
                Assert.That(orchestrator.ActiveLeases, Is.Empty);
                Assert.That(entities.EntityExists(grid), Is.True,
                    "The caller still owns live-grid cleanup after durable compensation succeeds.");
            });
            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(grid))
                    entities.DeleteEntity(grid);
                if (maps.MapExists(mapId))
                    maps.DeleteMap(mapId);
            });
            await pair.CleanReturnAsync();
        }
    }

    private static LuaMShipSnapshotRecord ToRecord(
        LuaMShipSnapshotStoreRequest request,
        long revision,
        DbLuaMShipSnapshotStatus status,
        Guid? leaseId,
        string? leaseServerInstanceId,
        int? leaseRoundId,
        long? leaseRevision)
        => new(
            request.ShipId,
            request.OwnerUserId,
            revision,
            request.PayloadRevision,
            status,
            request.VesselPrototypeId,
            request.ShipName,
            request.ShipNameSuffix,
            request.PurchasePrice,
            request.PurchasedWithVoucher,
            request.SchemaVersion,
            request.FormatVersion,
            request.Payload,
            request.PayloadHash,
            request.PayloadSizeBytes,
            request.EntityCount,
            request.SourceBuildVersion,
            request.PrototypeManifestHash,
            request.SourceRoundId,
            leaseRoundId,
            request.StoredAtUtc,
            request.StoredAtUtc,
            request.StoredAtUtc,
            leaseRoundId == null ? null : request.StoredAtUtc,
            null,
            null,
            null,
            null,
            null,
            leaseId,
            leaseServerInstanceId,
            leaseRoundId,
            leaseRevision,
            leaseId == null ? null : request.StoredAtUtc.AddMinutes(5));

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
}
