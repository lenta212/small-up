#nullable enable

using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Content.Server.Database;
using Content.Server._LuaM.ShipPersistence;
using Moq;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShipPersistenceOrchestratorRuntimeTest
{
    private static readonly ResPath SeedPath = new("/Maps/_LuaM/ShipGen/shipgen_seed.yml");

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
    public async Task FailedPlacementCanRetryLegacySnapshotAndBackfillPayloadRevision()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
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
        LuaMShipSnapshotStoreRequest? legacyStore = null;
        LuaMShipSnapshotStoreRequest? replacementStore = null;
        MapId sourceMap = default;
        MapId restoreMap = default;
        EntityUid sourceGrid = EntityUid.Invalid;
        EntityUid failedPlacementGrid = EntityUid.Invalid;
        EntityUid failedCompletionGrid = EntityUid.Invalid;
        EntityUid restoredGrid = EntityUid.Invalid;

        database
            .Setup(db => db.GetLuaMShipSnapshotAsync(
                It.IsAny<Guid>(),
                owner,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid shipId, NetUserId _, CancellationToken _) =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(legacyStore, Is.Not.Null);
                    Assert.That(shipId, Is.EqualTo(legacyStore!.ShipId));
                    Assert.That(status, Is.AnyOf(
                        DbLuaMShipSnapshotStatus.Stored,
                        DbLuaMShipSnapshotStatus.Restoring));
                });
                var restoring = status == DbLuaMShipSnapshotStatus.Restoring;
                return ToRecord(
                    legacyStore!,
                    lifecycleRevision,
                    status,
                    restoring ? leaseId : null,
                    restoring ? "legacy-retry-test" : null,
                    restoring ? 431 : null,
                    restoring ? 0 : null) with
                {
                    PayloadRevision = null,
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
                var record = ToRecord(
                    legacyStore!,
                    lifecycleRevision,
                    status,
                    leaseId,
                    request.ServerInstanceId,
                    request.RestoreRoundId,
                    0) with
                {
                    PayloadRevision = null,
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
                abortCount++;
                status = DbLuaMShipSnapshotStatus.Stored;
                lifecycleRevision++;
                leaseId = Guid.Empty;
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    Snapshot: ToRecord(legacyStore!, lifecycleRevision, status, null, null, null, null) with
                    {
                        PayloadRevision = null,
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
                if (completeCount == 1)
                    throw new InvalidOperationException("simulated completion transport failure");
                status = DbLuaMShipSnapshotStatus.Active;
                lifecycleRevision++;
                return new LuaMShipPersistenceWriteResult(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    lifecycleRevision,
                    status,
                    leaseId,
                    0);
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
                Assert.That(loader.TryLoadGrid(sourceMap, SeedPath, out var loaded), Is.True);
                Assert.That(loaded, Is.Not.Null);
                sourceGrid = loaded!.Value.Owner;
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
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "legacy-retry-test");
            var first = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(1),
                restored =>
                {
                    failedPlacementGrid = restored;
                    transforms.SetLocalPosition(restored, new Vector2(20f, 0f));
                    throw new InvalidOperationException("simulated placement transaction failure");
                }));

            Assert.Multiple(() =>
            {
                Assert.That(first.Success, Is.False);
                Assert.That(first.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidState));
                Assert.That(abortCount, Is.EqualTo(1));
                Assert.That(lifecycleRevision, Is.EqualTo(432));
                Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            });

            await pair.RunTicksSync(2);
            Assert.That(entities.EntityExists(failedPlacementGrid), Is.False,
                "A placement callback exception must not leak the partially moved restored grid.");
            var second = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(2),
                restored =>
                {
                    failedCompletionGrid = restored;
                    return true;
                }));

            Assert.Multiple(() =>
            {
                Assert.That(second.Success, Is.False);
                Assert.That(second.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.UnknownOutcome));
                Assert.That(abortCount, Is.EqualTo(2));
                Assert.That(lifecycleRevision, Is.EqualTo(434));
                Assert.That(status, Is.EqualTo(DbLuaMShipSnapshotStatus.Stored));
            });

            await pair.RunTicksSync(2);
            Assert.That(entities.EntityExists(failedCompletionGrid), Is.False,
                "A completion exception must delete the restored grid before rolling back the claim.");
            var third = await RunOnServerAsync(() => orchestrator.RestoreClaimAsync(
                legacyStore!.ShipId,
                owner,
                431,
                restoreMap,
                now.AddSeconds(3),
                _ => true));
            restoredGrid = third.Grid ?? EntityUid.Invalid;

            Assert.Multiple(() =>
            {
                Assert.That(third.Success, Is.True, third.Reason);
                Assert.That(third.Revision, Is.EqualTo(436));
                Assert.That(restoredGrid.Valid, Is.True);
                Assert.That(completeCount, Is.EqualTo(2));
                Assert.That(orchestrator.ActiveLeases.Single().RegistryRevision, Is.EqualTo(436));
                Assert.That(orchestrator.ActiveLeases.Single().PayloadRevision, Is.EqualTo(1));
                Assert.That(entities.GetComponent<LuaMShipIdentityComponent>(restoredGrid).SnapshotRevision,
                    Is.EqualTo(1));
            });

            var stored = await RunOnServerAsync(() => orchestrator.StoreAndDeactivateAsync(
                legacyStore!.ShipId,
                431,
                now.AddSeconds(4)));
            Assert.Multiple(() =>
            {
                Assert.That(stored.Success, Is.True, stored.Reason);
                Assert.That(stored.Revision, Is.EqualTo(437));
                Assert.That(replacementStore, Is.Not.Null);
                Assert.That(replacementStore!.PayloadRevision, Is.EqualTo(2));
                Assert.That(JsonSerializer.Deserialize<LuaMFullShipSnapshot>(replacementStore.Payload)!.Revision,
                    Is.EqualTo(2));
            });
            database.VerifyAll();
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(sourceGrid))
                    entities.DeleteEntity(sourceGrid);
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
}
