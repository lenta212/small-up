using System.IO;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipPersistenceLifecycleContractTest
{
    [Test]
    public void CreditPurchaseRegistersPersistentShipBeforeFinalizerSucceeds()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var method = Slice(
            source,
            "private async Task<bool> TryCreatePurchasedShuttleAsync(",
            "private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("string? persistentId = null;"));
            Assert.That(method, Does.Contain("if (!voucherUsed)"));
            Assert.That(method, Does.Contain("var registration = await RegisterPurchasedShipAsync("));
            Assert.That(method, Does.Not.Contain("_taskManager.BlockWaitOnTask("));
            Assert.That(method.IndexOf("if (!voucherUsed)", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("RegisterPurchasedShipAsync(", StringComparison.Ordinal)));
            Assert.That(method.IndexOf("RegisterPurchasedShipAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("TryBindPersistentShipSecurity(", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void VoucherPurchaseCreatesAnEphemeralShipOutsideThePersistentRegistry()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var method = Slice(
            source,
            "private async Task<bool> TryCreatePurchasedShuttleAsync(",
            "private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(");
        var persistentBranch = Slice(method, "string? persistentId = null;", "var sellValue = 0;");

        Assert.Multiple(() =>
        {
            Assert.That(persistentBranch, Does.Contain("if (!voucherUsed)"));
            Assert.That(persistentBranch, Does.Contain("RegisterPurchasedShipAsync("));
            Assert.That(persistentBranch, Does.Contain("TryBindPersistentShipSecurity("));
            Assert.That(method, Does.Contain("deedShuttle.PersistentShipId = null;"));
            Assert.That(method, Does.Contain("deedID.PersistentShipId = null;"));
            Assert.That(method, Does.Contain("persistentShipId: persistentId"));
        });
    }

    [Test]
    public void SaleRetiresPersistentShipBeforeWorldDeletion()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var sale = source[source.IndexOf("var saleResult = TryAppraiseShuttleSale", StringComparison.Ordinal)..];
        var method = Slice(sale, "async Task<bool> FinalizeAfterCommit()", "bool committed;");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("if (persistentShipId is { } shipId)"));
            Assert.That(method, Does.Contain("await _shipPersistence.RetireAsync("));
            Assert.That(method, Does.Not.Contain("_taskManager.BlockWaitOnTask("));
            Assert.That(method.IndexOf("_shipPersistence.RetireAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("RemComp<ShuttleDeedComponent>", StringComparison.Ordinal)));
            Assert.That(method.IndexOf("_shipPersistence.RetireAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("FinalizeAppraisedShuttleSale(", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ParkingEvacuatesCrewThenDeletesTheStoredGrid()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var method = Slice(
            source,
            "private async Task HandleParkShipMessageAsync(",
            "private void EvacuateCrewForParking(");
        var deletion = Slice(
            source,
            "private void QueueDeletePersistentShipWithStation(",
            "internal bool TryGetDeletablePersistentVesselStation(");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("EvacuateCrewForParking("));
            Assert.That(method, Does.Not.Contain("FoundOrganics("));
            Assert.That(method, Does.Contain("StoreAndDeactivateAsync("));
            Assert.That(method, Does.Contain("deed.ShuttleUid = null;"));
            Assert.That(method, Does.Contain("QueueDeletePersistentShipWithStation(shuttle);"));
            Assert.That(method, Does.Not.Contain("QueueDel(shuttle);"));
            Assert.That(method.IndexOf("EvacuateCrewForParking(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("StoreAndDeactivateAsync(", StringComparison.Ordinal)));
            Assert.That(method.IndexOf("StoreAndDeactivateAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("QueueDeletePersistentShipWithStation(shuttle);", StringComparison.Ordinal)));
            Assert.That(deletion, Does.Contain(
                "TryGetDeletablePersistentVesselStation(shuttle, out var shuttleStation)"));
            Assert.That(deletion, Does.Contain("_station.DeleteStation(shuttleStation);"));
            Assert.That(deletion, Does.Contain("QueueDel(shuttle);"));
            Assert.That(
                deletion.IndexOf("TryGetDeletablePersistentVesselStation(", StringComparison.Ordinal),
                Is.LessThan(deletion.IndexOf("_station.DeleteStation(shuttleStation);", StringComparison.Ordinal)));
            Assert.That(deletion.IndexOf("_station.DeleteStation(shuttleStation);", StringComparison.Ordinal),
                Is.LessThan(deletion.IndexOf("QueueDel(shuttle);", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ParkedDeedStillPreventsASecondPurchaseOnTheSameCard()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var validation = Slice(
            source,
            "private bool TryValidateShuttlePurchase(",
            "private async Task<bool> TryCreatePurchasedShuttleAsync(");

        Assert.That(validation, Does.Contain("HasComp<ShuttleDeedComponent>(targetId)"));
    }

    [Test]
    public void ShipyardStateIsActorTargetedAndDoesNotMutateTheInsertedCard()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var opened = Slice(source, "private void OnConsoleUIOpened(", "private void ConsolePopup(");
        var slotChanged = Slice(source, "private void OnItemSlotChanged(", "public string? FoundOrganics(");
        var refresh = Slice(
            source,
            "private async Task RefreshStateForActorAsync(",
            "private async Task<IReadOnlyList<LuaMShipRegistryRecord>> GetOwnerShipRecordsAsync(");

        Assert.Multiple(() =>
        {
            Assert.That(opened, Does.Contain("RefreshStateForActor("));
            Assert.That(slotChanged, Does.Contain("RefreshOpenStates("));
            Assert.That(opened + slotChanged, Does.Not.Contain("EnsureComp<ShuttleDeedComponent>"));
            Assert.That(opened + slotChanged, Does.Not.Contain("RemComp<ShuttleDeedComponent>"));
            Assert.That(source, Does.Not.Contain("TryRecoverStoredShipDeed"));
            Assert.That(source, Does.Not.Contain("_ui.SetUiState("));
            Assert.That(refresh, Does.Contain("await GetOwnerShipRecordsAsync(ownerUserId)"));
            Assert.That(refresh, Does.Contain("new ShipyardConsoleStateMessage(newState), player"));
        });
    }

    [Test]
    public void ShipCallUsesTheOwnersExplicitlySelectedStoredShip()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var events = ReadSource("Content.Shared/_NF/Shipyard/Events/ShipyardConsoleParkMessage.cs");
        var call = source[source.IndexOf("private async Task HandleCallShipMessageAsync(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(events, Does.Contain("public Guid ShipId;"));
            Assert.That(call, Does.Contain("record.ShipId == args.ShipId"));
            Assert.That(call, Does.Contain("!HasComp<IdCardComponent>(targetId)"));
            Assert.That(call, Does.Contain("IsCallableStoredShip(record)"));
            Assert.That(call, Does.Contain("(dock.DockType & DockType.Airlock) == DockType.None"));
            Assert.That(call, Does.Contain("RestoreClaimAsync("));
            Assert.That(call, Does.Contain("stored.ShipId,"));
            Assert.That(call, Does.Contain("TryReservePersistentShipCall(stored.ShipId, targetId, out var reservationId)"));
            Assert.That(call, Does.Contain("TryFTLDockAtDockOrPlaceNearbyIfDockless("));
            Assert.That(call, Does.Contain("deed.PersistentShipId = stored.ShipId.ToString(\"D\");"));
            Assert.That(call.IndexOf("if (!result.Success || result.Grid == null)", StringComparison.Ordinal),
                Is.LessThan(call.IndexOf("EnsureComp<ShuttleDeedComponent>(targetId)", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ShipCallUsesADedicatedClickableDockMap()
    {
        var menu = ReadSource("Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml.cs");
        var map = ReadSource("Content.Client/_NF/Shipyard/UI/ShipyardDockMapControl.cs");
        var window = ReadSource("Content.Client/_NF/Shipyard/UI/ShipyardDockSelectionWindow.xaml.cs");
        var boundUi = ReadSource("Content.Client/_NF/Shipyard/BUI/ShipyardConsoleBoundUserInterface.cs");
        var state = ReadSource("Content.Shared/_NF/Shipyard/BUI/ShipyardConsoleInterfaceState.cs");

        Assert.Multiple(() =>
        {
            Assert.That(menu, Does.Contain("CallShipButton.OnPressed += _ => OpenDockSelection();"));
            Assert.That(menu, Does.Contain("window.OpenCentered();"));
            Assert.That(menu, Does.Contain("OnCallShip?.Invoke(shipId, gate);"));
            Assert.That(map, Does.Contain("EngineKeyFunctions.UIClick"));
            Assert.That(map, Does.Contain("GateSelected?.Invoke(gate);"));
            Assert.That(window, Does.Contain("gate is not { Available: true }"));
            Assert.That(boundUi, Does.Contain("private void CallShip(Guid shipId, NetEntity gate)"));
            Assert.That(state, Does.Contain("public readonly NetEntity? StationGrid;"));
            Assert.That(state, Does.Contain("Vector2 Position"));
        });
    }

    [Test]
    public void PersistentShipManagementRequiresTheOwnerAtActionTime()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var sale = Slice(source, "private async Task HandleSellMessageAsync(", "private bool TryValidateReservedShuttleSale(");
        var finalSale = Slice(source, "private bool TryValidateReservedShuttleSale(", "private ShipyardSaleQuote BuildShipyardSaleQuote(");
        var rename = Slice(
            source,
            "private async Task HandleRenameMessageAsync(",
            "public void OnUnassignDeedMessage(");
        var unassign = Slice(
            source,
            "private async Task HandleUnassignDeedMessageAsync(",
            "private void OnParkShipMessage(");
        var park = Slice(
            source,
            "private async Task HandleParkShipMessageAsync(",
            "private void EvacuateCrewForParking(");

        Assert.Multiple(() =>
        {
            Assert.That(sale, Does.Contain("await AuthorizePersistentDeedForActorAsync("));
            Assert.That(rename, Does.Contain("await AuthorizePersistentDeedForActorAsync("));
            Assert.That(unassign, Does.Contain("await AuthorizePersistentDeedForActorAsync("));
            Assert.That(park, Does.Contain("await AuthorizePersistentDeedForActorAsync("));
            Assert.That(park, Does.Contain("!HasComp<IdCardComponent>(targetId)"));
            Assert.That(park, Does.Contain("ownership.OwnerUserId != authorization.ActorUserId"));
            Assert.That(finalSale, Does.Contain("currentSession.UserId != deedOwnerUserId"));
            Assert.That(finalSale, Does.Contain("ownership.OwnerUserId != deedOwnerUserId"));
            Assert.That(sale, Does.Contain("await GetOwnerShipRecordsAsync(deedOwnerUserId)"));
        });
    }

    [Test]
    public void CopiedPersistentDeedCannotFallBackToLegacyBearerAuthorization()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var ownership = Slice(source,
            "private bool IsDeedOwnedByActor(",
            "private async Task<PersistentDeedAuthorization> AuthorizePersistentDeedForActorAsync(");
        var authorization = Slice(source,
            "private async Task<PersistentDeedAuthorization> AuthorizePersistentDeedForActorAsync(",
            "private List<ShipyardGateInfo> GetShipyardGates(");

        Assert.Multiple(() =>
        {
            Assert.That(ownership, Does.Contain("string.IsNullOrEmpty(deed.PersistentShipId)"));
            Assert.That(ownership, Does.Contain(
                "TryComp<LuaMShipIdentityComponent>(liveShuttle, out var liveIdentity)"));
            Assert.That(ownership, Does.Contain("shipId = liveIdentity.ShipId;"));
            Assert.That(ownership, Does.Contain("!ownerRecords.Any(record => record.ShipId == shipId)"));
            Assert.That(ownership, Does.Contain("ownership.OwnerUserId == actorUserId"));
            Assert.That(ownership, Does.Contain("shipId == Guid.Empty"));
            Assert.That(authorization, Does.Not.Contain(
                "if (string.IsNullOrWhiteSpace(deed.PersistentShipId))"));
            Assert.That(authorization, Does.Contain("await GetOwnerShipRecordsAsync(actorUserId)"));
        });
    }

    [Test]
    public void DeedCopyPathsPreserveTheCanonicalPersistentShipId()
    {
        var recordModel = ReadSource("Content.Shared/_NF/ShuttleRecords/ShuttleRecord.cs");
        var recordsSystem = ReadSource("Content.Server/_NF/ShuttleRecords/ShuttleRecordsSystem.Console.cs");
        var shipyard = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var purchase = Slice(shipyard,
            "private async Task<bool> TryCreatePurchasedShuttleAsync(",
            "private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(");
        var recordCopy = Slice(recordsSystem,
            "private void AssignShuttleDeedProperties(ShuttleRecord shuttleRecord, EntityUid targetId)",
            "public static uint GetTransactionCost(");
        var spawnerCopy = Slice(shipyard,
            "private void OnInitDeedSpawner(",
            "#endregion");

        Assert.Multiple(() =>
        {
            Assert.That(recordModel, Does.Contain("string? persistentShipId = null"));
            Assert.That(recordModel, Does.Contain("public string? PersistentShipId { get; set; }"));
            Assert.That(purchase, Does.Contain("persistentShipId: persistentId"));
            Assert.That(recordCopy, Does.Contain("LuaMShipIdentityComponent"));
            Assert.That(recordCopy, Does.Contain("identity.ShipId.ToString(\"D\")"));
            Assert.That(recordCopy, Does.Contain("deed.PersistentShipId = persistentShipId;"));
            Assert.That(spawnerCopy, Does.Contain("shuttleDeed.PersistentShipId"));
            Assert.That(spawnerCopy, Does.Contain("identity.ShipId.ToString(\"D\")"));
            Assert.That(spawnerCopy, Does.Contain("deedID.PersistentShipId = persistentShipId;"));
        });
    }

    [Test]
    public void ParkedPersistentDeedCanBeUnassignedFromTheCard()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var method = Slice(
            source,
            "private async Task HandleUnassignDeedMessageAsync(",
            "private void OnParkShipMessage(");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("Guid.TryParse(deed.PersistentShipId, out _)"));
            Assert.That(method, Does.Contain("RemComp<ShuttleDeedComponent>(targetId)"));
            Assert.That(method.IndexOf("Guid.TryParse(deed.PersistentShipId, out _)", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("RemComp<ShuttleDeedComponent>(targetId)", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ShipyardUiPreservesTheSelectedGateAcrossStateRefreshes()
    {
        var source = ReadSource("Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml.cs");
        var method = source[source.IndexOf("public void UpdateState(ShipyardConsoleInterfaceState state)", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("var targetCardChanged = _targetCardContext != state.TargetCard;"));
            Assert.That(method, Does.Contain("var previouslySelectedShip = targetCardChanged ? null : SelectedStoredShipId;"));
            Assert.That(method, Does.Contain("_targetCardContext = state.TargetCard;"));
            Assert.That(method, Does.Contain("ship.ShipId == previousShip"));
            Assert.That(method, Does.Contain("var previouslySelectedGate = SelectedGate;"));
            Assert.That(method, Does.Contain("gate.Entity == selectedGate"));
            Assert.That(method, Does.Contain("_gates.FindIndex(gate => gate.Available)"));
            Assert.That(method.IndexOf("var previouslySelectedGate = SelectedGate;", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("_gates = state.Gates;", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void PurchaseRebindsConsoleSecurityToThePersistentShipId()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var method = Slice(
            source,
            "private async Task<bool> TryCreatePurchasedShuttleAsync(",
            "private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("TryBindPersistentShipSecurity("));
            Assert.That(method.IndexOf("RegisterPurchasedShipAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("TryBindPersistentShipSecurity(", StringComparison.Ordinal)));
            Assert.That(method.IndexOf("TryBindPersistentShipSecurity(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("deedID.PersistentShipId = persistentId;", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void FailedPurchaseRetiresAnActivePersistentShipBeforeDeletingItsGrid()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var method = Slice(
            source,
            "private async Task<bool> TryCleanupFailedShuttlePurchaseAsync(",
            "private void ShowShuttlePurchaseFailure(");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("_shipPersistence.ActiveLeases.Any("));
            Assert.That(method, Does.Contain("_shipPersistence.RetireAsync("));
            Assert.That(method, Does.Contain("if (!retirement.Success)"));
            Assert.That(method.IndexOf("_shipPersistence.RetireAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("RemComp<ShuttleDeedComponent>(targetId)", StringComparison.Ordinal)));
            Assert.That(method.IndexOf("_shipPersistence.RetireAsync(", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf("Del(shuttleUid);", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ParkingCapturesTheNextSnapshotRevision()
    {
        var source = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs");
        var method = Slice(source, "public async Task<LuaMShipOrchestrationResult> StoreAndDeactivateAsync(",
            "public async Task<LuaMShipOrchestrationResult> RetireAsync(");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("active.PayloadRevision + 1"));
            Assert.That(method, Does.Contain("active.RegistryRevision,"));
            Assert.That(method, Does.Contain("active.LeaseId,"));
        });
    }

    [Test]
    public void CleanPurchaseFailureRollsBackInsteadOfBlockingRetries()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var finalizer = Slice(source, "async Task<bool> FinalizeAfterCommit()", "bool committed;");

        Assert.Multiple(() =>
        {
            Assert.That(finalizer, Does.Contain("await TryCleanupFailedShuttlePurchaseAsync("));
            Assert.That(finalizer, Does.Contain("finalizationState.StagedShuttleUid = null;"));
            Assert.That(finalizer.IndexOf("finalizationState.StagedShuttleUid = null;", StringComparison.Ordinal),
                Is.LessThan(finalizer.IndexOf("finalizationRecoveryRequired = true;", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void UnknownRegistrationOutcomeKeepsThePurchaseForRecovery()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var creation = Slice(source,
            "private async Task<bool> TryCreatePurchasedShuttleAsync(",
            "private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(");
        var finalizer = Slice(source, "async Task<bool> FinalizeAfterCommit()", "bool committed;");
        var recoveryCatch = Slice(finalizer,
            "catch (ShuttlePurchaseRecoveryRequiredException exception)",
            "catch (Exception exception)");

        Assert.Multiple(() =>
        {
            Assert.That(creation, Does.Contain(
                "registration.Status == LuaMShipPersistenceWriteStatus.UnknownOutcome"));
            Assert.That(creation, Does.Contain(
                "throw new ShuttlePurchaseRecoveryRequiredException(state.Failure);"));
            Assert.That(recoveryCatch, Does.Contain("finalizationRecoveryRequired = true;"));
            Assert.That(recoveryCatch, Does.Contain("return true;"));
            Assert.That(recoveryCatch, Does.Not.Contain("TryCleanupFailedShuttlePurchaseAsync("));
        });
    }

    [Test]
    public void ShipSnapshotDoesNotIncludeRuntimeNullspaceEntities()
    {
        var source = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMFullShipPersistenceSystem.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MissingEntityBehaviour.Ignore"));
            Assert.That(source, Does.Not.Contain("MissingEntityBehaviour.IncludeNullspace"));
            Assert.That(source, Does.Not.Contain("MissingEntityBehaviour.AutoInclude"));
        });
    }

    [Test]
    public void SnapshotLimitsApplyBeforeSerializationAndDeserialization()
    {
        var runtime = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMFullShipPersistenceSystem.cs");
        var orchestrator = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs");
        var capture = Slice(
            runtime,
            "requiredEntities = CollectTransformGraph(grid);",
            "if (!ContainsRequiredGraph(");
        var validation = Slice(
            runtime,
            "private bool TryValidateSnapshot(",
            "public bool TryGetSavedShipManifest(");
        var decode = Slice(
            orchestrator,
            "private static bool TryDecode(",
            "private static LuaMShipSnapshotMetadata MetadataFrom(");

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("private sealed class Utf8SizeLimitedTextWriter"));
            Assert.That(capture, Does.Contain("requiredEntities.Count > LuaMShipPersistenceLimits.MaxEntityCount"));
            Assert.That(
                capture.IndexOf(
                    "requiredEntities.Count > LuaMShipPersistenceLimits.MaxEntityCount",
                    StringComparison.Ordinal),
                Is.LessThan(capture.IndexOf("_mapLoader.TrySaveGrid(", StringComparison.Ordinal)));
            Assert.That(validation, Does.Contain(
                "snapshot.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes"));
            Assert.That(validation, Does.Contain(
                "snapshot.EntityCount > LuaMShipPersistenceLimits.MaxEntityCount"));
            Assert.That(
                validation.IndexOf(
                    "snapshot.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxSnapshotPayloadBytes",
                    StringComparison.Ordinal),
                Is.LessThan(validation.IndexOf("StrictUtf8.GetString(snapshot.Payload)", StringComparison.Ordinal)));
            Assert.That(decode, Does.Contain(
                "stored.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxPayloadBytes"));
            Assert.That(decode, Does.Contain(
                "stored.EntityCount > LuaMShipPersistenceLimits.MaxEntityCount"));
            Assert.That(
                decode.IndexOf(
                    "stored.PayloadSizeBytes > LuaMShipPersistenceLimits.MaxPayloadBytes",
                    StringComparison.Ordinal),
                Is.LessThan(decode.IndexOf(
                    "JsonSerializer.Deserialize<LuaMFullShipSnapshot>",
                    StringComparison.Ordinal)));
        });
    }

    [Test]
    public void PersistentShipCallFallsBackToSafeProximityPlacement()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");

        Assert.That(source, Does.Contain("_shuttle.TryFTLProximity(restored, stationGrid)"));
    }

    [Test]
    public void ShipyardDatabaseWorkDoesNotBlockTheGameThread()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Not.Contain("_taskManager.BlockWaitOnTask("));
            Assert.That(source, Does.Contain("await _serverDb.GetLuaMShipSnapshotsByOwnerAsync("));
            Assert.That(source, Does.Contain("return _shipPersistence.RegisterAsync("));
            Assert.That(source, Does.Contain("await _shipPersistence.StoreAndDeactivateAsync("));
            Assert.That(source, Does.Contain("await _shipPersistence.RestoreClaimAsync("));
            Assert.That(source, Does.Contain("await _shipPersistence.RetireAsync("));
        });
    }

    [Test]
    public void EmergencySaveRequiresAnExactConfirmationAndRefusesMobs()
    {
        var command = ReadSource("Content.Server/_LuaM/Administration/LuaMShipPersistenceCommands.cs");
        var orchestrator = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs");
        var emergency = Slice(
            orchestrator,
            "private async Task<LuaMShipOrchestrationResult> EmergencyStoreAndDeleteActiveShipCoreAsync(",
            "private int CountMobStateEntities(");

        Assert.Multiple(() =>
        {
            Assert.That(command, Does.Contain("confirmedShipId != shipId"));
            Assert.That(command, Does.Contain("EmergencyStoreAndDeleteActiveShipAsync("));
            Assert.That(emergency, Does.Contain("if (mobCount > 0)"));
            Assert.That(emergency, Does.Contain("await StoreAndDeactivateCoreAsync("));
            Assert.That(emergency, Does.Contain("QueueDel(active.Grid);"));
            Assert.That(
                emergency.IndexOf("if (mobCount > 0)", StringComparison.Ordinal),
                Is.LessThan(emergency.IndexOf("StoreAndDeactivateCoreAsync(", StringComparison.Ordinal)));
            Assert.That(
                emergency.IndexOf("StoreAndDeactivateCoreAsync(", StringComparison.Ordinal),
                Is.LessThan(emergency.IndexOf("QueueDel(active.Grid);", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void InvalidDurablePayloadIsQuarantinedWithoutReturningItToStored()
    {
        var source = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs");
        var decodeFailure = Slice(
            source,
            "var claimed = claim.Snapshot;",
            "if (!_runtime.TryBeginRestoreSnapshot(");

        Assert.Multiple(() =>
        {
            Assert.That(decodeFailure, Does.Contain("if (!TryDecode("));
            Assert.That(decodeFailure, Does.Contain("await QuarantineClaimedSnapshotAsync("));
            Assert.That(decodeFailure, Does.Not.Contain("AbortOrQuarantineAsync("));
        });
    }

    [Test]
    public void OrchestratorOwnsRestoreLeaseRenewAndRoundEndSaveHooks()
    {
        var source = ReadSource("Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("SubscribeLocalEvent<PlayerSpawnCompleteEvent>"));
            Assert.That(source, Does.Contain("RecoverExpiredLuaMShipLeasesAsync("));
            Assert.That(source, Does.Contain("RestoreClaimAsync("));
            Assert.That(source, Does.Contain("RenewLuaMShipLeaseAsync("));
            Assert.That(source, Does.Contain("SubscribeLocalEvent<GameRunLevelChangedEvent>"));
            Assert.That(source, Does.Contain("SubscribeLocalEvent<RoundRestartCleanupEvent>"));
            Assert.That(source, Does.Contain("SaveAllActiveShipsAsync("));
            Assert.That(source, Does.Contain("_taskManager.BlockWaitOnTask("));
        });
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
