#nullable enable

using System.IO;
using System.Linq;
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
public sealed class LuaMShipMaintenanceApiTest
{
    private static readonly ResPath SeedPath = new("/Maps/_LuaM/ShipGen/shipgen_seed.yml");

    [Test]
    public void ApiRequiresAuthenticationAndRejectsSessionsBeforeStartingTheBarrier()
    {
        var source = ReadSource("Content.Server/Administration/ServerApi.cs");
        var handler = Slice(
            source,
            "private async Task ActionMaintenanceShipSave(IStatusHandlerContext context)",
            "    #endregion");
        var sessionGuard = handler.IndexOf("if (_playerManager.Sessions.Any())", StringComparison.Ordinal);
        var barrierCall = handler.IndexOf("SaveActiveShipsForMaintenanceAsync(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "RegisterHandler(HttpMethod.Post, ShipMaintenanceSavePath, ActionMaintenanceShipSave);"));
            Assert.That(source, Does.Contain(
                "private const string ShipMaintenanceSavePath = \"/admin/actions/maintenance/ship-save\";"));
            Assert.That(sessionGuard, Is.GreaterThanOrEqualTo(0));
            Assert.That(sessionGuard, Is.LessThan(barrierCall),
                "Every session must be rejected before ship persistence can freeze or save anything.");
            Assert.That(handler, Does.Contain("rejectedForActiveSessions = true;"));
            Assert.That(handler, Does.Contain("ShipMaintenanceSaveResponse.Unavailable(createdAtUtc)"));
            Assert.That(handler, Does.Contain("HttpStatusCode.Conflict"));
            Assert.That(handler, Does.Contain("RunOnMainThread(async () =>"));
            Assert.That(handler, Does.Not.Contain("RestartRound("));
            Assert.That(handler, Does.Not.Contain("Shutdown("));
        });
    }

    [Test]
    public void ReceiptContractIsStructuredAndContainsNoShipOrOwnerData()
    {
        var properties = typeof(LuaMShipMaintenanceSaveReceipt)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(LuaMShipMaintenanceSaveReceipt.CurrentSchemaVersion, Is.EqualTo(1));
            Assert.That(properties, Is.EquivalentTo(new[]
            {
                "SchemaVersion",
                "Ok",
                "Busy",
                "BarrierId",
                "CreatedAtUtc",
                "Attempted",
                "Saved",
                "Failed",
                "ActiveRemaining",
                "Frozen",
            }));
            Assert.That(properties, Has.None.Matches<string>(name =>
                name.Contains("Owner", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Payload", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Lease", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ShipName", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Reason", StringComparison.OrdinalIgnoreCase)));
        });
    }

    [Test]
    public async Task BarrierWaitsForInFlightWorkRejectsConcurrencyAndFreezesNewMutations()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource<int>();
        var nowUtc = new DateTime(2030, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        database
            .Setup(db => db.RecoverExpiredLuaMShipLeasesAsync(
                nowUtc,
                100,
                It.IsAny<CancellationToken>()))
            .Returns((DateTime _, int _, CancellationToken _) =>
            {
                recoveryStarted.TrySetResult();
                return releaseRecovery.Task;
            });

        try
        {
            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "maintenance-barrier-test");

            Task? leaseMaintenance = null;
            await server.WaitPost(() =>
            {
                leaseMaintenance = orchestrator.MaintainLeasesAsync(nowUtc);
            });
            await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task<LuaMShipMaintenanceSaveReceipt>? firstBarrier = null;
            await server.WaitPost(() =>
            {
                firstBarrier = orchestrator.SaveActiveShipsForMaintenanceAsync(42, nowUtc);
            });

            Assert.Multiple(() =>
            {
                Assert.That(firstBarrier, Is.Not.Null);
                Assert.That(firstBarrier!.IsCompleted, Is.False,
                    "The barrier must wait for a lifecycle operation that was already in flight.");
                Assert.That(orchestrator.MaintenanceFrozen, Is.True,
                    "Freeze must be visible before the in-flight lifecycle operation drains.");
            });

            Task<LuaMShipMaintenanceSaveReceipt>? concurrentBarrier = null;
            await server.WaitPost(() =>
            {
                concurrentBarrier = orchestrator.SaveActiveShipsForMaintenanceAsync(42, nowUtc);
            });
            var busyReceipt = await concurrentBarrier!.WaitAsync(TimeSpan.FromSeconds(10));

            Task<LuaMShipOrchestrationResult>? blockedMutation = null;
            await server.WaitPost(() =>
            {
                blockedMutation = orchestrator.StoreAndDeactivateAsync(Guid.NewGuid(), 42, nowUtc);
            });
            var blockedResult = await blockedMutation!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(busyReceipt.SchemaVersion, Is.EqualTo(1));
                Assert.That(busyReceipt.Ok, Is.False);
                Assert.That(busyReceipt.Busy, Is.True);
                Assert.That(busyReceipt.BarrierId, Is.EqualTo(Guid.Empty));
                Assert.That(busyReceipt.CreatedAtUtc, Is.EqualTo(nowUtc));
                Assert.That(busyReceipt.Frozen, Is.True);
                Assert.That(blockedResult.Success, Is.False);
                Assert.That(blockedResult.Status, Is.EqualTo(LuaMShipPersistenceWriteStatus.InvalidState));
                Assert.That(blockedResult.Reason, Does.Contain("maintenance"));
            });

            await server.WaitPost(() => releaseRecovery.SetResult(0));
            await server.WaitRunTicks(1);
            await leaseMaintenance!.WaitAsync(TimeSpan.FromSeconds(10));
            var completedReceipt = await firstBarrier!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(completedReceipt.SchemaVersion, Is.EqualTo(1));
                Assert.That(completedReceipt.Ok, Is.True);
                Assert.That(completedReceipt.Busy, Is.False);
                Assert.That(completedReceipt.BarrierId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(completedReceipt.CreatedAtUtc, Is.EqualTo(nowUtc));
                Assert.That(completedReceipt.Attempted, Is.Zero);
                Assert.That(completedReceipt.Saved, Is.Zero);
                Assert.That(completedReceipt.Failed, Is.Zero);
                Assert.That(completedReceipt.ActiveRemaining, Is.Zero);
                Assert.That(completedReceipt.Frozen, Is.True);
                Assert.That(orchestrator.MaintenanceFrozen, Is.True,
                    "The successful barrier must remain valid until this process is stopped.");
            });
            database.VerifyAll();
        }
        finally
        {
            releaseRecovery.TrySetResult(0);
            pair.Kill();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ActiveShipBarrierReportsExactStoreOutcomeAndKeepsFailuresActive(bool storeSucceeds)
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<SharedMapSystem>();
        var runtime = entities.System<LuaMFullShipPersistenceSystem>();
        var database = new Mock<IServerDbManager>(MockBehavior.Strict);
        var owner = new NetUserId(Guid.NewGuid());
        var registeredAtUtc = new DateTime(2030, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var barrierAtUtc = registeredAtUtc.AddMinutes(1);
        var leaseId = Guid.Empty;
        var barrierStoreObserved = false;
        MapId mapId = default;
        EntityUid grid = EntityUid.Invalid;

        database
            .Setup(db => db.StoreLuaMShipSnapshotAsync(
                It.IsAny<LuaMShipSnapshotStoreRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipSnapshotStoreRequest request, CancellationToken _) =>
            {
                if (request.ExpectedRevision == null)
                {
                    return new(
                        LuaMShipPersistenceWriteStatus.Success,
                        request.ShipId,
                        0,
                        DbLuaMShipSnapshotStatus.Stored);
                }

                barrierStoreObserved = true;
                Assert.Multiple(() =>
                {
                    Assert.That(request.ExpectedRevision, Is.EqualTo(2));
                    Assert.That(request.LeaseId, Is.EqualTo(leaseId));
                    Assert.That(request.PayloadRevision, Is.EqualTo(2));
                    Assert.That(request.SourceRoundId, Is.EqualTo(42));
                    Assert.That(request.StoredAtUtc, Is.EqualTo(barrierAtUtc));
                });
                return storeSucceeds
                    ? new(
                        LuaMShipPersistenceWriteStatus.Success,
                        request.ShipId,
                        3,
                        DbLuaMShipSnapshotStatus.Stored)
                    : new(
                        LuaMShipPersistenceWriteStatus.RevisionConflict,
                        request.ShipId,
                        2,
                        DbLuaMShipSnapshotStatus.Active,
                        request.LeaseId,
                        0);
            });
        database
            .Setup(db => db.ClaimLuaMShipRestoreAsync(
                It.IsAny<LuaMShipRestoreClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LuaMShipRestoreClaimRequest request, CancellationToken _) =>
            {
                leaseId = request.LeaseId;
                return new(
                    LuaMShipPersistenceWriteStatus.Success,
                    request.ShipId,
                    1,
                    DbLuaMShipSnapshotStatus.Restoring,
                    request.LeaseId,
                    0);
            });
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
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                Assert.That(loader.TryLoadGrid(mapId, SeedPath, out var loaded), Is.True);
                grid = loaded!.Value.Owner;
            });

            var orchestrator = entities.System<LuaMShipPersistenceOrchestrator>();
            orchestrator.ConfigureForTesting(database.Object, runtime, "maintenance-active-ship-test");
            var registration = await RunOnServerAsync(() => orchestrator.RegisterAsync(
                grid,
                owner,
                new LuaMShipSnapshotMetadata(
                    "VesselTestPersistence",
                    "Maintenance Test Ship",
                    "MT-42",
                    125_000,
                    false,
                    41),
                registeredAtUtc));
            Assert.That(registration.Success, Is.True, registration.Reason);
            Assert.That(orchestrator.ActiveLeases, Has.Count.EqualTo(1));

            var receipt = await RunOnServerAsync(() =>
                orchestrator.SaveActiveShipsForMaintenanceAsync(42, barrierAtUtc));

            Assert.Multiple(() =>
            {
                Assert.That(barrierStoreObserved, Is.True);
                Assert.That(receipt.Ok, Is.EqualTo(storeSucceeds));
                Assert.That(receipt.Busy, Is.False);
                Assert.That(receipt.BarrierId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(receipt.CreatedAtUtc, Is.EqualTo(barrierAtUtc));
                Assert.That(receipt.Attempted, Is.EqualTo(1));
                Assert.That(receipt.Saved, Is.EqualTo(storeSucceeds ? 1 : 0));
                Assert.That(receipt.Failed, Is.EqualTo(storeSucceeds ? 0 : 1));
                Assert.That(receipt.ActiveRemaining, Is.EqualTo(storeSucceeds ? 0 : 1));
                Assert.That(receipt.Frozen, Is.True);
                Assert.That(orchestrator.ActiveLeases.Count, Is.EqualTo(storeSucceeds ? 0 : 1),
                    "A failed durable store must leave the live ship tracked as active.");
                Assert.That(entities.EntityExists(grid), Is.True,
                    "The maintenance barrier saves/deactivates persistence state but never deletes the grid itself.");
            });
            database.VerifyAll();
        }
        finally
        {
            pair.Kill();
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing start marker: {startMarker}");
        Assert.That(end, Is.GreaterThan(start), $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            var gitMarker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate repository root from test output directory.");
        return string.Empty;
    }
}
