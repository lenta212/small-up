using Content.Server.Access.Systems;
using Content.Server.Popups;
using Content.Server.Radio.EntitySystems;
using Content.Server._NF.Bank;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.ShuttleRecords;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Chat; // Einstein Engines - Languages
using Content.Shared.Ghost;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Network;
using Content.Shared.Radio;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Database;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Maps;
using Content.Shared.StationRecords;
using Content.Shared.Station.Components;
using Content.Server.Chat.Systems;
using Content.Server.Mind;
using Content.Server.Preferences.Managers;
using Content.Server.StationRecords;
using Content.Server.StationRecords.Systems;
using Content.Shared.Database;
using Content.Shared.Preferences;
using static Content.Shared._NF.Shipyard.Components.ShuttleDeedComponent;
using Content.Server.Shuttles.Components;
using Content.Server._NF.Station.Components;
using Content.Server._LuaM.ShipPersistence;
using System.Text.RegularExpressions;
using Content.Server._Mono.Shipyard;
using Content.Server.Shuttles.Systems;
using Content.Shared.UserInterface;
using Robust.Shared.Audio.Systems;
using Content.Shared.Access;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.ShuttleRecords;
using Content.Server.StationEvents.Components;
using Content.Shared._Mono.Company;
using Content.Shared.Forensics.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Player;
using Robust.Server.Player;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard;
using Content.Shared.Tag;
using Robust.Shared.Timing;
using Robust.Shared.Map;
using System.Threading.Tasks;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private AccessSystem _accessSystem = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IServerPreferencesManager _prefManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private IdCardSystem _idSystem = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private StationRecordsSystem _records = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private EntityManager _entityManager = default!;
    [Dependency] private ShuttleRecordsSystem _shuttleRecordsSystem = default!;
    [Dependency] private ShuttleConsoleLockSystem _shuttleConsoleLock = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private TagSystem _tagSystem = default!;
    [Dependency] private IServerDbManager _serverDb = default!;

    private static readonly ProtoId<TagPrototype> CrewedShuttleTag = "CrewedShuttle";
    private static readonly ProtoId<AccessLevelPrototype> PersistentShipCaptainAccess = "Captain";
    private static readonly ProtoId<AccessLevelPrototype>[] PersistentShipCaptainAccessLevels =
        [PersistentShipCaptainAccess];
    private static readonly ProtoId<AccessLevelPrototype>[] PersistentShipSecurityAccessLevels =
        [PersistentShipCaptainAccess, "Security", "Brig"];
    private static readonly Regex DeedRegex = new(@"\s*\([^()]*\)");

    public void InitializeConsole()
    {

    }

    private void OnPurchaseMessage(
        EntityUid shipyardConsoleUid,
        ShipyardConsoleComponent component,
        ShipyardConsolePurchaseMessage args)
    {
        _ = ObservePurchaseMessageAsync(shipyardConsoleUid, args);
    }

    private async Task ObservePurchaseMessageAsync(
        EntityUid shipyardConsoleUid,
        ShipyardConsolePurchaseMessage args)
    {
        try
        {
            await HandlePurchaseMessageAsync(shipyardConsoleUid, args);
        }
        catch (Exception exception)
        {
            _sawmill.Error(
                $"Unhandled durable shuttle purchase failure at entity {shipyardConsoleUid}: {exception}");
            try
            {
                if (args.Actor is { Valid: true } player &&
                    Exists(player) &&
                    Exists(shipyardConsoleUid) &&
                    TryComp<ShipyardConsoleComponent>(shipyardConsoleUid, out var component))
                {
                    ConsolePopup(player, Loc.GetString("shipyard-console-purchase-bank-failed"));
                    PlayDenySound(player, shipyardConsoleUid, component);
                }
            }
            catch (Exception reportingException)
            {
                _sawmill.Error(
                    $"Could not report durable shuttle purchase failure: {reportingException}");
            }
        }
    }

    private async Task HandlePurchaseMessageAsync(
        EntityUid shipyardConsoleUid,
        ShipyardConsolePurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } player ||
            !_player.TryGetSessionByEntity(player, out var playerSession))
        {
            return;
        }

        if (!TryValidateShuttlePurchase(
                shipyardConsoleUid,
                player,
                args,
                expectedTargetId: null,
                expectedStationUid: null,
                expectedQuote: null,
                out var initialContext,
                out var initialFailure))
        {
            ShowShuttlePurchaseFailure(shipyardConsoleUid, player, initialFailure);
            return;
        }

        var targetId = initialContext.TargetId;
        var userId = playerSession.UserId;
        if (!TryReserveShuttlePurchase(
                userId,
                targetId,
                out var recoveryRequired,
                out var purchaseReservationId))
        {
            ShowShuttlePurchaseFailure(
                shipyardConsoleUid,
                player,
                Loc.GetString(recoveryRequired
                    ? "shipyard-console-purchase-recovery-required"
                    : "shipyard-console-purchase-bank-pending"));
            return;
        }

        var releaseReservation = true;
        var finalizationState = new ShuttlePurchaseFinalizationState();
        var creationAttempted = false;
        var finalizationAttempted = false;
        var finalizationRecoveryRequired = false;
        string? finalizationFailure = null;

        try
        {
            async Task<bool> FinalizeAfterCommit()
            {
                finalizationAttempted = true;
                if (!TryValidateShuttlePurchase(
                        shipyardConsoleUid,
                        player,
                        args,
                        targetId,
                        initialContext.StationUid,
                        initialContext.Quote,
                        out var finalContext,
                        out finalizationFailure))
                {
                    return false;
                }

                try
                {
                    creationAttempted = true;
                    var finalized = await TryCreatePurchasedShuttleAsync(
                        shipyardConsoleUid,
                        player,
                        args,
                        finalContext,
                        purchaseReservationId,
                        finalizationState);
                    finalizationFailure = finalizationState.Failure;
                    if (!finalized && finalizationState.StagedShuttleUid != null)
                    {
                        if (await TryCleanupFailedShuttlePurchaseAsync(
                                targetId,
                                finalizationState.StagedShuttleUid,
                                creationAttempted))
                        {
                            finalizationState.StagedShuttleUid = null;
                            return false;
                        }

                        // Cleanup could not prove that the staged grid and deed
                        // were removed, so keep the debit for reconciliation.
                        finalizationRecoveryRequired = true;
                        return true;
                    }

                    return finalized;
                }
                catch (ShuttlePurchaseRecoveryRequiredException exception)
                {
                    _sawmill.Error(
                        $"CRITICAL: shuttle purchase requires reconciliation for user {userId}, target {targetId}, " +
                        $"shuttle {finalizationState.StagedShuttleUid}: {exception}");
                    finalizationFailure = exception.Message;
                    finalizationRecoveryRequired = true;
                    return true;
                }
                catch (Exception exception)
                {
                    _sawmill.Error(
                        $"Shuttle purchase finalizer failed for user {userId}, target {targetId}, shuttle {finalizationState.StagedShuttleUid}: {exception}");

                    if (!finalizationState.TargetDeedPublished &&
                        await TryCleanupFailedShuttlePurchaseAsync(
                            targetId,
                            finalizationState.StagedShuttleUid,
                            creationAttempted))
                    {
                        finalizationFailure = Loc.GetString("shipyard-console-purchase-creation-failed");
                        return false;
                    }

                    // The deed may already be published or cleanup could not
                    // prove that every staged entity was removed. Keep the debit
                    // and lock retries for administrator reconciliation.
                    finalizationRecoveryRequired = true;
                    return true;
                }
            }

            bool committed;
            if (initialContext.Quote.VoucherUsed)
            {
                committed = await FinalizeAfterCommit();
            }
            else
            {
                committed = await _bank.TryBankWithdrawAsync(
                    player,
                    initialContext.Quote.Price,
                    finalizeAfterCommit: FinalizeAfterCommit);
            }

            if (!committed)
            {
                ShowShuttlePurchaseFailure(
                    shipyardConsoleUid,
                    player,
                    finalizationAttempted && finalizationFailure != null
                        ? finalizationFailure
                        : Loc.GetString("shipyard-console-purchase-bank-failed"));
                return;
            }

            if (finalizationRecoveryRequired || finalizationState.StagedShuttleUid == null)
            {
                releaseReservation = false;
                BlockShuttlePurchase(userId, targetId, purchaseReservationId);
                ShowShuttlePurchaseFailure(
                    shipyardConsoleUid,
                    player,
                    Loc.GetString("shipyard-console-purchase-recovery-required"));
                return;
            }

            // The bank component is projected only after the callback returns.
            // Refresh once more with the confirmed durable balance.
            try
            {
                if (TryComp<ShipyardConsoleComponent>(shipyardConsoleUid, out var currentComponent))
                    RefreshOpenStates(shipyardConsoleUid, currentComponent, (ShipyardConsoleUiKey)args.UiKey);
            }
            catch (Exception exception)
            {
                _sawmill.Error(
                    $"Committed shuttle purchase for {finalizationState.StagedShuttleUid} but could not refresh its console UI: {exception}");
            }
        }
        catch (BankMutationRollbackException exception)
        {
            releaseReservation = false;
            BlockShuttlePurchase(userId, targetId, purchaseReservationId);
            _sawmill.Error(
                $"CRITICAL: keeping purchase reservation for user {userId}, target {targetId} after bank ambiguity: {exception}");
            ShowShuttlePurchaseFailure(
                shipyardConsoleUid,
                player,
                Loc.GetString("shipyard-console-purchase-recovery-required"));
        }
        catch (Exception exception)
        {
            // Once settlement starts, an unexpected exception is not proof that
            // the debit or world finalizer failed. Fail closed instead of making
            // an automatic retry possible.
            releaseReservation = false;
            BlockShuttlePurchase(userId, targetId, purchaseReservationId);
            _sawmill.Error(
                $"CRITICAL: shuttle purchase outcome is unknown for user {userId}, target {targetId}: {exception}");
            ShowShuttlePurchaseFailure(
                shipyardConsoleUid,
                player,
                Loc.GetString("shipyard-console-purchase-recovery-required"));
        }
        finally
        {
            if (releaseReservation)
                ReleaseShuttlePurchase(userId, targetId, purchaseReservationId);
        }
    }

    private bool TryValidateShuttlePurchase(
        EntityUid shipyardConsoleUid,
        EntityUid player,
        ShipyardConsolePurchaseMessage args,
        EntityUid? expectedTargetId,
        EntityUid? expectedStationUid,
        ShipyardPurchaseQuote? expectedQuote,
        out ShipyardPurchaseContext context,
        out string failure)
    {
        context = default!;
        failure = Loc.GetString("shipyard-console-purchase-changed");

        if (Deleted(shipyardConsoleUid) ||
            Deleted(player) ||
            !TryComp<ShipyardConsoleComponent>(shipyardConsoleUid, out var component))
        {
            return false;
        }

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId ||
            expectedTargetId != null && targetId != expectedTargetId)
        {
            failure = expectedTargetId == null
                ? Loc.GetString("shipyard-console-no-idcard")
                : Loc.GetString("shipyard-console-purchase-changed");
            return false;
        }

        TryComp<IdCardComponent>(targetId, out var idCard);
        TryComp<ShipyardVoucherComponent>(targetId, out var voucher);
        if (idCard == null && voucher == null)
        {
            failure = Loc.GetString("shipyard-console-no-idcard");
            return false;
        }

        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            failure = Loc.GetString("shipyard-console-already-deeded");
            return false;
        }

        if (TryComp<AccessReaderComponent>(shipyardConsoleUid, out var accessReader) &&
            !_access.IsAllowed(player, shipyardConsoleUid, accessReader))
        {
            failure = Loc.GetString("comms-console-permission-denied");
            return false;
        }

        if (!_prototypeManager.TryIndex<VesselPrototype>(args.Vessel, out var vessel) ||
            vessel.Price <= 0)
        {
            failure = Loc.GetString("shipyard-console-invalid-vessel", ("vessel", args.Vessel));
            return false;
        }

        if (!GetAvailableShuttles(shipyardConsoleUid, targetId: targetId).available.Contains(vessel.ID))
        {
            failure = Loc.GetString("shipyard-console-invalid-vessel", ("vessel", args.Vessel));
            _adminLogger.Add(
                LogType.Action,
                LogImpact.Medium,
                $"Player entity {player} tried to purchase unavailable vessel {vessel.ID}.");
            return false;
        }

        if (_station.GetOwningStation(shipyardConsoleUid) is not { Valid: true } stationUid ||
            expectedStationUid != null && stationUid != expectedStationUid)
        {
            failure = expectedStationUid == null
                ? Loc.GetString("shipyard-console-invalid-station")
                : Loc.GetString("shipyard-console-purchase-changed");
            return false;
        }

        var targetGrid = TryComp<StationDataComponent>(stationUid, out var selectedStationData)
            ? _station.GetLargestGrid((stationUid, selectedStationData))
            : null;
        var selectedGate = GetEntity(args.Gate);
        if (targetGrid == null ||
            !TryComp<DockingComponent>(selectedGate, out var selectedDock) ||
            Transform(selectedGate).GridUid != targetGrid ||
            selectedDock.Docked)
        {
            failure = "Выбранные ворота заняты или больше недоступны.";
            return false;
        }

        if (!TryComp<BankAccountComponent>(player, out _))
        {
            failure = Loc.GetString("shipyard-console-no-bank");
            return false;
        }

        var voucherUsed = voucher != null;
        if (voucher != null)
        {
            if (voucher.DestroyOnEmpty && voucher.RedemptionsLeft <= 0)
            {
                failure = Loc.GetString("shipyard-console-no-voucher-redemptions");
                return false;
            }

            if (voucher.ConsoleType != (ShipyardConsoleUiKey)args.UiKey)
            {
                failure = Loc.GetString("shipyard-console-invalid-voucher-type");
                return false;
            }

            if (_timing.CurTime < voucher.NextBuyAt)
            {
                var remaining = voucher.NextBuyAt - _timing.CurTime;
                failure = Loc.GetString(
                    "ship-voucher-cooldown-active",
                    ("remainingTime", Math.Round(remaining.TotalMinutes)));
                return false;
            }
        }

        var quote = new ShipyardPurchaseQuote(
            vessel.ID.ToString(),
            vessel.Price,
            vessel.ShuttlePath.ToString(),
            voucherUsed);
        if (expectedQuote != null && expectedQuote != quote)
        {
            failure = Loc.GetString("shipyard-console-purchase-changed");
            return false;
        }

        context = new ShipyardPurchaseContext(
            component,
            targetId,
            idCard,
            voucher,
            vessel,
            stationUid,
            selectedGate,
            quote);
        failure = string.Empty;
        return true;
    }

    private async Task<bool> TryCreatePurchasedShuttleAsync(
        EntityUid shipyardConsoleUid,
        EntityUid player,
        ShipyardConsolePurchaseMessage args,
        ShipyardPurchaseContext context,
        Guid purchaseReservationId,
        ShuttlePurchaseFinalizationState state)
    {
        state.Failure = null;
        var component = context.Component;
        var targetId = context.TargetId;
        var idCard = context.IdCard;
        var voucher = context.Voucher;
        var vessel = context.Vessel;
        var station = context.StationUid;
        var name = vessel.Name;

        if (!TryPurchaseShuttle(station, vessel.ShuttlePath, out var shuttleUidOut, context.SelectedGate))
        {
            state.Failure = Loc.GetString("shipyard-console-purchase-creation-failed");
            return false;
        }

        var shuttleUid = shuttleUidOut.Value;
        state.StagedShuttleUid = shuttleUid;
        if (!_entityManager.TryGetComponent<ShuttleComponent>(shuttleUid, out var shuttle))
        {
            if (!await TryCleanupFailedShuttlePurchaseAsync(
                    targetId,
                    state.StagedShuttleUid,
                    creationAttempted: true))
                throw new InvalidOperationException($"Could not clean invalid staged shuttle {shuttleUid}.");

            state.StagedShuttleUid = null;
            state.Failure = Loc.GetString("shipyard-console-purchase-creation-failed");
            return false;
        }

        var ev = new AttemptShipyardShuttlePurchaseEvent(shuttleUid, args.Actor, vessel);
        RaiseLocalEvent(ref ev);

        if (ev.Cancelled)
        {
            if (!await TryCleanupFailedShuttlePurchaseAsync(
                    targetId,
                    state.StagedShuttleUid,
                    creationAttempted: true))
                throw new InvalidOperationException($"Could not clean cancelled staged shuttle {shuttleUid}.");

            state.StagedShuttleUid = null;
            state.Failure = Loc.GetString(ev.CancelReason);
            return false;
        }

        var voucherUsed = context.Quote.VoucherUsed;

        // Add company information to the shuttle from the ID card or voucher
        string? companyName = null;

        // First try to get company from ID card
        if (TryComp<IdCardComponent>(targetId, out var idCardCompany) &&
            !string.IsNullOrEmpty(idCardCompany.CompanyName))
        {
            companyName = idCardCompany.CompanyName;
        }
        // If no ID card company, try to get from voucher
        else if (TryComp<ShipyardVoucherComponent>(targetId, out var voucherCompany) &&
                 !string.IsNullOrEmpty(voucherCompany.CompanyName))
        {
            companyName = voucherCompany.CompanyName;
        }

        // Apply company to ship if we found one
        if (!string.IsNullOrEmpty(companyName))
        {
            var shipCompany = EnsureComp<CompanyComponent>(shuttleUid);
            shipCompany.CompanyName = companyName;
            Dirty(shuttleUid, shipCompany);
        }

        EntityUid? shuttleStation = null;
        // setting up any stations if we have a matching game map prototype to allow late joins directly onto the vessel
        if (_prototypeManager.TryIndex<GameMapPrototype>(vessel.ID, out var stationProto))
        {
            List<EntityUid> gridUids = new()
            {
                shuttleUid
            };
            shuttleStation = _station.InitializeNewStation(stationProto.Stations[vessel.ID], gridUids);
            name = Name(shuttleStation.Value);

            var vesselInfo = EnsureComp<ExtraShuttleInformationComponent>(shuttleStation.Value);
            vesselInfo.Vessel = vessel.ID;
        }

        // Add FTLLockComponent to the shuttle with Enabled set to true
        // We need to use the ShuttleConsoleSystem to properly set the Enabled property
        EnsureComp<FTLLockComponent>(shuttleUid);

        // Get the ShuttleConsoleSystem which has proper access to modify FTLLockComponent.Enabled
        var shuttleConsoleSystem = Get<ShuttleConsoleSystem>();
        var dockedEntities = new List<NetEntity>();
        shuttleConsoleSystem.ToggleFTLLock(shuttleUid, dockedEntities, true);

        var shuttleOwner = Name(player).Trim();
        var deedShuttle = EnsureComp<ShuttleDeedComponent>(shuttleUid);
        AssignShuttleDeedProperties(deedShuttle, shuttleUid, name, shuttleOwner, voucherUsed, voucherUsed ? targetId.ToString() : null);
        deedShuttle.PersistentShipId = null;
        Dirty(shuttleUid, deedShuttle);

        // Lock all shuttle consoles on the ship to this deed
        var shuttleConsoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (shuttleConsoleQuery.MoveNext(out var consoleUid, out _, out var transform))
        {
            // Only process consoles on the purchased ship
            if (transform.GridUid != shuttleUid)
                continue;

            // Add lock component and set the shuttle ID
            var lockComp = EnsureComp<ShuttleConsoleLockComponent>(consoleUid);
            _shuttleConsoleLock.SetShuttleId(consoleUid, shuttleUid.ToString(), lockComp);

            // Log for debugging
            Log.Debug("Locked shuttle console {0} to shuttle {1} for deed holder {2}", consoleUid, shuttleUid, targetId);
        }

        // Revalidate the exact reservation and clean target immediately before
        // publishing. This closes purchase-vs-call interleavings while the bank
        // settlement callback pumps asynchronous continuations.
        if (!_player.TryGetSessionByEntity(player, out var currentSession) ||
            !IsShuttlePurchaseReservationHeld(
                currentSession.UserId,
                targetId,
                purchaseReservationId) ||
            !TryComp<ShipyardConsoleComponent>(shipyardConsoleUid, out var currentComponent) ||
            currentComponent.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId ||
            HasComp<ShuttleDeedComponent>(targetId))
        {
            state.Failure = Loc.GetString("shipyard-console-purchase-changed");
            return false;
        }

        // Publish the deed before any mutations outside the staged shuttle and
        // its newly-created station. From this point onward failures are
        // fail-closed: keep the debit and block retries for reconciliation.
        var deedID = EnsureComp<ShuttleDeedComponent>(targetId);
        AssignShuttleDeedProperties(
            deedID,
            shuttleUid,
            name,
            shuttleOwner,
            voucherUsed,
            voucherUsed ? targetId.ToString() : null);
        deedID.DeedHolder = targetId;
        deedID.PersistentShipId = null;
        Dirty(targetId, deedID);
        state.TargetDeedPublished = true;

        if (voucher != null)
        {
            voucher.NextBuyAt = _timing.CurTime + voucher.Cooldown;
            if (voucher.DestroyOnEmpty)
                voucher.RedemptionsLeft--;
            Dirty(targetId, voucher);
        }

        var persistentAccess = EnsureComp<PersistentShipyardAccessComponent>(shuttleUid);
        persistentAccess.GrantedLevels.Clear();
        persistentAccess.GrantedLevels.UnionWith(component.NewAccessLevels);
        GrantShipAccessLevels(targetId, persistentAccess.GrantedLevels);

        if (!voucherUsed && !string.IsNullOrEmpty(component.NewJobTitle))
            _idSystem.TryChangeJobTitle(targetId, component.NewJobTitle, idCard, player);

        // Register ship ownership for auto-deletion when owner is offline too long
        // We need to get the player's session from their entity
        if (TryComp<ActorComponent>(player, out var actorComp) && actorComp.PlayerSession != null)
        {
            _shipOwnership.RegisterShipOwnership(shuttleUid, actorComp.PlayerSession);
        }

        // The following block of code is entirely to do with trying to sanely handle moving records from station to station.
        // it is ass.
        // This probably shouldnt be messed with further until station records themselves become more robust
        // and not entirely dependent upon linking ID card entity to station records key lookups
        // its just bad

        var stationList = EntityQueryEnumerator<StationRecordsComponent>();

        if (TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
                && shuttleStation != null
                && keyStorage.Key != null)
        {
            bool recSuccess = false;
            while (stationList.MoveNext(out var stationUid, out var stationRecComp))
            {
                if (!_records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
                    continue;

                //_records.RemoveRecord(keyStorage.Key.Value);
                _records.AddRecordEntry(shuttleStation.Value, record);
                recSuccess = true;
                break;
            }

            if (!recSuccess &&
                _mind.TryGetMind(player, out var mindUid, out var mindComp)
                && _prefManager.GetPreferencesOrNull(mindComp.UserId)?.SelectedCharacter is HumanoidCharacterProfile profile)
            {
                TryComp<FingerprintComponent>(player, out var fingerprintComponent);
                TryComp<DnaComponent>(player, out var dnaComponent);
                TryComp<StationRecordsComponent>(shuttleStation, out var stationRec);
                _records.CreateGeneralRecord(shuttleStation.Value, targetId, profile.Name, profile.Age, profile.Species, profile.Gender, $"Captain", fingerprintComponent!.Fingerprint, dnaComponent!.DNA, profile, stationRec!);
            }
        }
        if (shuttleStation != null)
            _records.Synchronize(shuttleStation.Value);
        _records.Synchronize(station);

        EntityManager.AddComponents(shuttleUid, vessel.AddComponents);

        // Add ship access control
        AddShipAccessToEntities(shuttleUid);

        // Ensure cleanup on ship sale
        EnsureComp<LinkedLifecycleGridParentComponent>(shuttleUid);

        var vesselStore = EnsureComp<VesselComponent>(shuttleUid);
        vesselStore.VesselId = vessel.ID;

        EnsureComp<TagComponent>(shuttleUid);
        _tagSystem.TryAddTags(shuttleUid, vessel.Tags);

        var requiresCrew = vessel.RequireCrew ||
                           vessel.Classes.Contains(VesselClass.Capital) ||
                           _tagSystem.HasTag(shuttleUid, CrewedShuttleTag);
        if (requiresCrew)
            EnsureComp<CrewedShuttleComponent>(shuttleUid);

        string? persistentId = null;
        if (!voucherUsed)
        {
            if (!TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership))
            {
                state.Failure = "Persistent ship registration requires authoritative ownership.";
                return false;
            }

            var registration = await RegisterPurchasedShipAsync(
                shuttleUid,
                ownership.OwnerUserId,
                vessel.ID,
                deedShuttle,
                vessel.Price);
            if (!registration.Success)
            {
                _sawmill.Error(
                    $"Persistent ship registration failed for shuttle {shuttleUid}, owner {ownership.OwnerUserId}: " +
                    $"{registration.Status}: {registration.Reason}");
                state.Failure =
                    $"Persistent ship registration failed: {registration.Reason ?? registration.Status.ToString()}";

                if (registration.Status == LuaMShipPersistenceWriteStatus.UnknownOutcome)
                    throw new ShuttlePurchaseRecoveryRequiredException(state.Failure);

                return false;
            }

            var persistentIdentity = Comp<LuaMShipIdentityComponent>(shuttleUid);
            if (!_shuttleConsoleLock.TryBindPersistentShipSecurity(
                    shuttleUid,
                    persistentIdentity.ShipId,
                    out var securityBindingFailure))
            {
                _sawmill.Error(
                    $"Persistent ship security binding failed for shuttle {shuttleUid}, " +
                    $"ship {persistentIdentity.ShipId}: {securityBindingFailure}");
                state.Failure = $"Persistent ship security binding failed: {securityBindingFailure}";
                return false;
            }

            persistentId = persistentIdentity.ShipId.ToString("D");
            deedShuttle.PersistentShipId = persistentId;
            deedID.PersistentShipId = persistentId;
            Dirty(shuttleUid, deedShuttle);
            Dirty(targetId, deedID);
        }

        var sellValue = 0;
        if (!voucherUsed)
        {
            sellValue = (int)_pricing.AppraiseGrid(shuttleUid, LacksPreserveOnSaleComp);
            sellValue = CalculateShipResaleValue((shipyardConsoleUid, component), sellValue);
        }

        SendPurchaseMessage(shipyardConsoleUid, player, name, component.ShipyardChannel, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendPurchaseMessage(shipyardConsoleUid, player, name, secretChannel, secret: true);

        // Mono
        _entityManager.System<ShipyardDirectionSystem>().SendShipDirectionMessage(player, shuttleUid);

        PlayConfirmSound(player, shipyardConsoleUid, component);
        if (voucherUsed)
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to purchase shuttle {ToPrettyString(shuttleUid)} with a voucher via {ToPrettyString(shipyardConsoleUid)}");
        else
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to purchase shuttle {ToPrettyString(shuttleUid)} for {vessel.Price} credits via {ToPrettyString(shipyardConsoleUid)}");

        // Adding the record to the shuttle records system makes them eligible to be copied.
        // Can be set on the component of the shipyard.
        if (component.CanTransferDeed)
        {
            _shuttleRecordsSystem.AddRecord(
                new ShuttleRecord(
                    name: deedShuttle.ShuttleName ?? "",
                    suffix: deedShuttle.ShuttleNameSuffix ?? "",
                    ownerName: shuttleOwner,
                    entityUid: _entityManager.GetNetEntity(shuttleUid),
                    purchasedWithVoucher: voucherUsed,
                    purchasePrice: (uint)vessel.Price,
                    persistentShipId: persistentId
                )
            );
        }

        var purchaseEv = new ShipyardShuttlePurchaseEvent(shuttleUid, player); // Mono: half of this shit could be an event.
        RaiseLocalEvent(purchaseEv);
        return true;
    }

    private Task<LuaMShipOrchestrationResult> RegisterPurchasedShipAsync(
        EntityUid shuttleUid,
        Robust.Shared.Network.NetUserId ownerUserId,
        string vesselPrototypeId,
        ShuttleDeedComponent deed,
        int purchasePrice)
    {
        var metadata = new LuaMShipSnapshotMetadata(
            vesselPrototypeId,
            deed.ShuttleName ?? Name(shuttleUid),
            deed.ShuttleNameSuffix,
            purchasePrice,
            deed.PurchasedWithVoucher,
            _gameTicker.RoundId);
        return _shipPersistence.RegisterAsync(shuttleUid, ownerUserId, metadata, DateTime.UtcNow);
    }

    private async Task<bool> TryCleanupFailedShuttlePurchaseAsync(
        EntityUid targetId,
        EntityUid? stagedShuttleUid,
        bool creationAttempted)
    {
        if (!creationAttempted)
            return true;

        if (stagedShuttleUid is not { Valid: true } shuttleUid)
            return false;

        try
        {
            if (TryComp<LuaMShipIdentityComponent>(shuttleUid, out var identity) &&
                identity.ShipId != Guid.Empty &&
                _shipPersistence.ActiveLeases.Any(active =>
                    active.ShipId == identity.ShipId && active.Grid == shuttleUid))
            {
                var retirement = await _shipPersistence.RetireAsync(
                    identity.ShipId,
                    "failed shipyard purchase cleanup",
                    DateTime.UtcNow);
                if (!retirement.Success)
                {
                    _sawmill.Error(
                        $"Could not retire persistent ship {identity.ShipId} before cleaning failed purchase " +
                        $"grid {shuttleUid}: {retirement.Status}: {retirement.Reason}");
                    return false;
                }
            }

            if (TryComp<ShuttleDeedComponent>(targetId, out var targetDeed) &&
                (targetDeed.ShuttleUid == null || targetDeed.ShuttleUid == shuttleUid))
            {
                RemComp<ShuttleDeedComponent>(targetId);
            }

            if (!Deleted(shuttleUid))
            {
                if (_station.GetOwningStation(shuttleUid) is { Valid: true } shuttleStation)
                    _station.DeleteStation(shuttleStation);

                Del(shuttleUid);
            }

            return Deleted(shuttleUid);
        }
        catch (Exception exception)
        {
            _sawmill.Error(
                $"Could not clean failed staged shuttle {shuttleUid} for target {targetId}: {exception}");
            return false;
        }
    }

    private void ShowShuttlePurchaseFailure(
        EntityUid consoleUid,
        EntityUid player,
        string message)
    {
        if (!Exists(player))
            return;

        ConsolePopup(player, message);
        if (TryComp<ShipyardConsoleComponent>(consoleUid, out var component))
            PlayDenySound(player, consoleUid, component);
    }

    private sealed record ShipyardPurchaseQuote(
        string VesselId,
        int Price,
        string ShuttlePath,
        bool VoucherUsed);

    private sealed record ShipyardPurchaseContext(
        ShipyardConsoleComponent Component,
        EntityUid TargetId,
        IdCardComponent? IdCard,
        ShipyardVoucherComponent? Voucher,
        VesselPrototype Vessel,
        EntityUid StationUid,
        EntityUid SelectedGate,
        ShipyardPurchaseQuote Quote);

    private sealed class ShuttlePurchaseFinalizationState
    {
        public EntityUid? StagedShuttleUid;
        public bool TargetDeedPublished;
        public string? Failure;
    }

    private readonly record struct PersistentDeedAuthorization(
        bool Authorized,
        NetUserId ActorUserId,
        Guid? PersistentShipId);

    private sealed class ShuttlePurchaseRecoveryRequiredException(string message) : Exception(message);

    private void TryParseShuttleName(ShuttleDeedComponent deed, string name)
    {
        // The logic behind this is: if a name part fits the requirements, it is the required part. Otherwise it's the name.
        // This may cause problems but ONLY when renaming a ship. It will still display properly regardless of this.
        var nameParts = name.Split(' ');

        var hasSuffix = nameParts.Length > 1 && nameParts.Last().Length < MaxSuffixLength && nameParts.Last().Contains('-');
        deed.ShuttleNameSuffix = hasSuffix ? nameParts.Last() : null;
        deed.ShuttleName = String.Join(" ", nameParts.SkipLast(hasSuffix ? 1 : 0));
    }

    public void OnSellMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSellMessage args)
    {
        _ = ObserveSellMessageAsync(uid, args);
    }

    private async Task ObserveSellMessageAsync(EntityUid uid, ShipyardConsoleSellMessage args)
    {
        try
        {
            await HandleSellMessageAsync(uid, args);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Unhandled durable ship sale failure at entity {uid}: {exception}");
            try
            {
                if (args.Actor is { Valid: true } player &&
                    Exists(player) &&
                    Exists(uid) &&
                    TryComp<ShipyardConsoleComponent>(uid, out var component))
                {
                    ConsolePopup(player, Loc.GetString("shipyard-console-sale-bank-failed"));
                    PlayDenySound(player, uid, component);
                }
            }
            catch (Exception reportingException)
            {
                _sawmill.Error($"Could not report durable ship sale failure: {reportingException}");
            }
        }
    }

    private async Task HandleSellMessageAsync(EntityUid uid, ShipyardConsoleSellMessage args)
    {
        if (args.Actor is not { Valid: true } player ||
            !TryComp<ShipyardConsoleComponent>(uid, out var component))
        {
            return;
        }

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        TryComp<IdCardComponent>(targetId, out var idCard);
        TryComp<ShipyardVoucherComponent>(targetId, out var voucher);
        if (idCard is null && voucher is null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        var authorization = await AuthorizePersistentDeedForActorAsync(player, targetId, deed);
        if (!authorization.Authorized)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-owner-denied"));
            if (component != null)
                PlayDenySound(player, uid, component);
            return;
        }
        var deedOwnerUserId = authorization.ActorUserId;
        var persistentShipId = authorization.PersistentShipId;

        if (deed.ShuttleUid is not { Valid: true } shuttleUid || Deleted(shuttleUid))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        var voucherUsed = deed.PurchasedWithVoucher;
        if (!TryComp<BankAccountComponent>(player, out _))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (_station.GetOwningStation(uid) is not { Valid: true } stationUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return;
        }

        var uiKey = (ShipyardConsoleUiKey)args.UiKey;
        var disableSaleQuery = GetEntityQuery<ShipyardSellConditionComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();
        var disableSaleMsg = FindDisableShipyardSaleObjects(
            shuttleUid,
            uiKey,
            disableSaleQuery,
            xformQuery);
        if (disableSaleMsg != null)
        {
            ConsolePopup(player, Loc.GetString(disableSaleMsg));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryReserveDeedMutation(
                targetId,
                out var deedRecoveryRequired,
                out var deedReservationId))
        {
            ConsolePopup(
                player,
                Loc.GetString(deedRecoveryRequired
                    ? "shipyard-console-sale-recovery-required"
                    : "shipyard-console-call-pending"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryReserveShuttleSale(shuttleUid, out var recoveryRequired))
        {
            ReleaseDeedMutation(targetId, deedReservationId);
            ConsolePopup(
                player,
                Loc.GetString(recoveryRequired
                    ? "shipyard-console-sale-recovery-required"
                    : "shipyard-console-sale-bank-pending"));
            PlayDenySound(player, uid, component);
            return;
        }

        var releaseReservation = true;
        var blockDeedReservation = false;
        try
        {
            var saleResult = TryAppraiseShuttleSale(stationUid, shuttleUid, out var appraisal);
            if (saleResult.Error != ShipyardSaleError.Success)
            {
                ShowShipyardSaleError(player, uid, component, saleResult);
                return;
            }

            var quote = BuildShipyardSaleQuote(component, appraisal, voucherUsed);
            var shuttleName = ToPrettyString(shuttleUid);
            GeneralStationRecord? recordToCopy = null;
            if (_station.GetOwningStation(shuttleUid) is { Valid: true } shuttleStation &&
                TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage) &&
                keyStorage.Key != null &&
                keyStorage.Key.Value.OriginStation == shuttleStation &&
                _records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
            {
                recordToCopy = record;
            }

            var finalizeAttempted = false;
            var finalizationRecoveryRequired = false;
            string? soldOwner = null;
            string? soldName = null;
            ShipyardSaleResult finalValidation = default;

            async Task<bool> FinalizeAfterCommit()
            {
                finalizeAttempted = true;
                var currentOwnerRecords = await GetOwnerShipRecordsAsync(deedOwnerUserId);
                if (!IsDeedMutationReservationHeld(targetId, deedReservationId) ||
                    !TryValidateReservedShuttleSale(
                        uid,
                        player,
                        targetId,
                        stationUid,
                        shuttleUid,
                        deedOwnerUserId,
                        persistentShipId,
                        uiKey,
                        voucherUsed,
                        quote,
                        currentOwnerRecords,
                        out soldOwner,
                        out soldName,
                        out var finalAppraisal,
                        out finalValidation))
                {
                    return false;
                }

                try
                {
                    if (persistentShipId is { } shipId)
                    {
                        var retirement = await _shipPersistence.RetireAsync(
                            shipId,
                            "shipyard sale",
                            DateTime.UtcNow);
                        if (!retirement.Success)
                        {
                            _sawmill.Error(
                                $"Persistent ship retirement failed for shuttle {shuttleUid}: {retirement.Reason ?? retirement.Status.ToString()}");
                            return false;
                        }
                    }

                    // Remove the deed first so a partially completed world
                    // finalization can never be retried as another valid sale.
                    RemComp<ShuttleDeedComponent>(targetId);
                    FinalizeAppraisedShuttleSale(shuttleUid, uid, finalAppraisal);
                }
                catch (Exception exception)
                {
                    // Validation has completed and irreversible world mutation
                    // may already have started. Keep the durable payout and lock
                    // the shuttle for administrator reconciliation instead of
                    // attempting a bank rollback with an unknowable world state.
                    finalizationRecoveryRequired = true;
                    _sawmill.Error(
                        $"CRITICAL: ship sale finalization for shuttle {shuttleUid} requires recovery: {exception}");
                }

                return true;
            }

            bool committed;
            if (voucherUsed || quote.NetPayout <= 0)
            {
                committed = await FinalizeAfterCommit();
            }
            else
            {
                committed = await _bank.TryBankDepositAsync(
                    player,
                    quote.NetPayout,
                    tax: false,
                    finalizeAfterCommit: FinalizeAfterCommit);
            }

            if (!committed)
            {
                if (finalizeAttempted && finalValidation.Error != ShipyardSaleError.Success)
                {
                    ShowShipyardSaleError(player, uid, component, finalValidation);
                }
                else
                {
                    ConsolePopup(player, Loc.GetString("shipyard-console-sale-bank-failed"));
                    PlayDenySound(player, uid, component);
                }

                return;
            }

            if (finalizationRecoveryRequired)
            {
                releaseReservation = false;
                blockDeedReservation = true;
                BlockShuttleSale(shuttleUid);
                ConsolePopup(player, Loc.GetString("shipyard-console-sale-recovery-required"));
                if (TryComp<ShipyardConsoleComponent>(uid, out var recoveryComponent))
                    PlayDenySound(player, uid, recoveryComponent);
                return;
            }

            // The player payout is durable and the shuttle/deed are finalized at
            // this point. Sector ledgers are intentionally credited afterwards.
            foreach (var tax in quote.Taxes)
            {
                if (tax.Amount <= 0)
                    continue;

                if (!_bank.TrySectorDeposit(tax.Account, tax.Amount, LedgerEntryType.ShipyardTax))
                {
                    _sawmill.Error(
                        $"Could not credit {tax.Amount} shipyard tax to {tax.Account} after selling {shuttleUid}");
                }
            }

            if (recordToCopy != null)
            {
                _records.AddRecordEntry(stationUid, recordToCopy);
                _records.Synchronize(stationUid);
            }

            if (!TryComp<ShipyardConsoleComponent>(uid, out var currentComponent))
                return;

            PlayConfirmSound(player, uid, currentComponent);
            SendSellMessage(uid, soldOwner, soldName ?? shuttleName, currentComponent.ShipyardChannel, player, secret: false);
            if (currentComponent.SecretShipyardChannel is { } secretChannel)
                SendSellMessage(uid, soldOwner, soldName ?? shuttleName, secretChannel, player, secret: true);

            if (voucherUsed)
            {
                _adminLogger.Add(
                    LogType.ShipYardUsage,
                    LogImpact.Low,
                    $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to sell {shuttleName} (purchased with voucher) via {ToPrettyString(uid)}");
            }
            else
            {
                _adminLogger.Add(
                    LogType.ShipYardUsage,
                    LogImpact.Low,
                    $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to sell {shuttleName} for {quote.NetPayout} credits via {ToPrettyString(uid)}");
            }

            if (TryComp<ShipyardVoucherComponent>(targetId, out var currentVoucher) &&
                currentVoucher.RedemptionsLeft <= 0 &&
                currentVoucher.DestroyOnEmpty)
            {
                QueueDel(targetId);
            }

            if (TryComp<ShipyardConsoleComponent>(uid, out var refreshedComponent))
                RefreshOpenStates(uid, refreshedComponent, uiKey);
        }
        catch (BankMutationRollbackException exception)
        {
            // The payout may still be committed. Keep the shuttle reservation so
            // no retry can duplicate it; an administrator must reconcile the bank
            // account before process restart or explicit administrator recovery.
            releaseReservation = false;
            blockDeedReservation = true;
            BlockShuttleSale(shuttleUid);
            _sawmill.Error(
                $"CRITICAL: keeping sale reservation for {shuttleUid} after payout rollback failure: {exception}");
            ConsolePopup(player, Loc.GetString("shipyard-console-sale-recovery-required"));
            PlayDenySound(player, uid, component);
        }
        finally
        {
            if (releaseReservation)
                ReleaseShuttleSale(shuttleUid);

            if (blockDeedReservation)
                BlockDeedMutation(targetId, deedReservationId);
            else
                ReleaseDeedMutation(targetId, deedReservationId);
        }
    }

    private bool TryValidateReservedShuttleSale(
        EntityUid consoleUid,
        EntityUid player,
        EntityUid targetId,
        EntityUid stationUid,
        EntityUid shuttleUid,
        NetUserId deedOwnerUserId,
        Guid? persistentShipId,
        ShipyardConsoleUiKey uiKey,
        bool voucherUsed,
        ShipyardSaleQuote expectedQuote,
        IReadOnlyList<LuaMShipRegistryRecord> ownerRecords,
        out string? soldOwner,
        out string? soldName,
        out int finalAppraisal,
        out ShipyardSaleResult failure)
    {
        soldOwner = null;
        soldName = null;
        finalAppraisal = 0;
        failure = default;

        if (Deleted(consoleUid) ||
            Deleted(targetId) ||
            Deleted(shuttleUid) ||
            !TryComp<ShipyardConsoleComponent>(consoleUid, out var currentComponent) ||
            currentComponent.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId ||
            _station.GetOwningStation(consoleUid) != stationUid ||
            !TryComp<ShuttleDeedComponent>(targetId, out var currentDeed) ||
            currentDeed.ShuttleUid != shuttleUid ||
            currentDeed.PurchasedWithVoucher != voucherUsed)
        {
            failure.Error = ShipyardSaleError.InvalidShip;
            return false;
        }

        if (persistentShipId is { } shipId &&
            (!_player.TryGetSessionByEntity(player, out var currentSession) ||
             currentSession.UserId != deedOwnerUserId ||
             !TryComp<LuaMShipIdentityComponent>(shuttleUid, out var identity) ||
             identity.ShipId != shipId ||
             !TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership) ||
             ownership.OwnerUserId != deedOwnerUserId ||
             !ownerRecords.Any(record => record.ShipId == shipId)))
        {
            failure.Error = ShipyardSaleError.InvalidShip;
            return false;
        }

        var disableSaleMsg = FindDisableShipyardSaleObjects(
            shuttleUid,
            uiKey,
            GetEntityQuery<ShipyardSellConditionComponent>(),
            GetEntityQuery<TransformComponent>());
        if (disableSaleMsg != null)
        {
            failure.Error = ShipyardSaleError.MessageOverwritten;
            failure.OverwrittenMessage = disableSaleMsg;
            return false;
        }

        failure = TryAppraiseShuttleSale(stationUid, shuttleUid, out finalAppraisal);
        if (failure.Error != ShipyardSaleError.Success)
            return false;

        var currentQuote = BuildShipyardSaleQuote(currentComponent, finalAppraisal, voucherUsed);
        if (!ShipyardSaleQuotesMatch(expectedQuote, currentQuote))
        {
            failure.Error = ShipyardSaleError.MessageOverwritten;
            failure.OverwrittenMessage = "shipyard-console-sale-changed";
            return false;
        }

        soldOwner = currentDeed.ShuttleOwner;
        soldName = GetFullName(currentDeed);
        return true;
    }

    private ShipyardSaleQuote BuildShipyardSaleQuote(
        ShipyardConsoleComponent component,
        int appraisal,
        bool voucherUsed)
    {
        if (voucherUsed)
            return new ShipyardSaleQuote(appraisal, 0, 0, []);

        var grossPayout = component.IgnoreBaseSaleRate
            ? appraisal
            : (int)(appraisal * _baseSaleRate);
        var taxes = component.TaxAccounts
            .Select(entry => new ShipyardSaleTax(
                entry.Key,
                CalculateSalesTax(grossPayout, entry.Value)))
            .ToArray();
        var totalTax = taxes.Sum(tax => (long)tax.Amount);
        var netPayout = (int)Math.Max(0L, (long)grossPayout - totalTax);
        return new ShipyardSaleQuote(appraisal, grossPayout, netPayout, taxes);
    }

    private static bool ShipyardSaleQuotesMatch(ShipyardSaleQuote expected, ShipyardSaleQuote current)
    {
        return expected.Appraisal == current.Appraisal &&
               expected.GrossPayout == current.GrossPayout &&
               expected.NetPayout == current.NetPayout &&
               expected.Taxes.SequenceEqual(current.Taxes);
    }

    private void ShowShipyardSaleError(
        EntityUid player,
        EntityUid consoleUid,
        ShipyardConsoleComponent component,
        ShipyardSaleResult result)
    {
        var message = result.Error switch
        {
            ShipyardSaleError.Undocked => Loc.GetString("shipyard-console-sale-not-docked"),
            ShipyardSaleError.OrganicsAboard => Loc.GetString(
                "shipyard-console-sale-organic-aboard",
                ("name", result.OrganicName ?? "Somebody")),
            ShipyardSaleError.InvalidShip => Loc.GetString("shipyard-console-sale-invalid-ship"),
            ShipyardSaleError.MessageOverwritten when result.OverwrittenMessage != null =>
                Loc.GetString(result.OverwrittenMessage),
            _ => Loc.GetString(
                "shipyard-console-sale-unknown-reason",
                ("reason", result.Error.ToString())),
        };

        ConsolePopup(player, message);
        PlayDenySound(player, consoleUid, component);
    }

    private readonly record struct ShipyardSaleTax(SectorBankAccount Account, int Amount);

    private sealed record ShipyardSaleQuote(
        int Appraisal,
        int GrossPayout,
        int NetPayout,
        ShipyardSaleTax[] Taxes);

    /// <summary>
    /// Checks if a player is currently on the unassign cooldown and returns the remaining time.
    /// </summary>
    private TimeSpan? GetRemainingCooldownTime(EntityUid player)
    {
        if (!TryComp<ShipyardUnassignCooldownComponent>(player, out var cooldown))
            return null;

        var currentTime = _timing.CurTime;
        if (currentTime >= cooldown.NextUnassignTime)
            return null;

        return cooldown.NextUnassignTime - currentTime;
    }

    private void OnConsoleUIOpened(EntityUid uid, ShipyardConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (!component.Initialized)
            return;

        if (args.Actor is not { Valid: true } player)
            return;

        RefreshStateForActor(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
    }

    private void ConsolePopup(EntityUid uid, string text)
    {
        _popup.PopupEntity(text, uid);
    }

    private void SendPurchaseMessage(EntityUid uid, EntityUid player, string name, string shipyardChannel, bool secret)
    {
        var channel = _prototypeManager.Index<RadioChannelPrototype>(shipyardChannel);

        if (secret)
        {
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-docking-secret"), InGameICChatType.Speak, true);
        }
        else
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-docking", ("owner", player), ("vessel", name)), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-docking", ("owner", player!), ("vessel", name)), InGameICChatType.Speak, true);
        }
    }

    private void SendSellMessage(EntityUid uid, string? player, string name, string shipyardChannel, EntityUid seller, bool secret)
    {
        var channel = _prototypeManager.Index<RadioChannelPrototype>(shipyardChannel);

        if (secret)
        {
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-leaving-secret"), InGameICChatType.Speak, true);
        }
        else
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-leaving", ("owner", player!), ("vessel", name!), ("player", seller)), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-leaving", ("owner", player!), ("vessel", name!), ("player", seller)), InGameICChatType.Speak, true);
        }
    }

    private void PlayDenySound(EntityUid playerUid, EntityUid consoleUid, ShipyardConsoleComponent component)
    {
        _audio.PlayEntity(component.ErrorSound, playerUid, consoleUid);
    }

    private void PlayConfirmSound(EntityUid playerUid, EntityUid consoleUid, ShipyardConsoleComponent component)
    {
        _audio.PlayEntity(component.ConfirmSound, playerUid, consoleUid);
    }

    private void OnItemSlotChanged(EntityUid uid, ShipyardConsoleComponent component, ContainerModifiedMessage args)
    {
        if (!component.Initialized)
            return;

        if (args.Container.ID != component.TargetIdSlot.ID)
            return;

        if (!TryComp<ActivatableUIComponent>(uid, out var uiComp) || uiComp.Key == null)
            return;

        RefreshOpenStates(uid, component, (ShipyardConsoleUiKey)uiComp.Key);
    }

    /// <summary>
    /// Looks for a living, sapient being aboard a particular entity.
    /// </summary>
    /// <param name="uid">The entity to search (e.g. a shuttle, a station)</param>
    /// <param name="mobQuery">A query to get the MobState from an entity</param>
    /// <param name="xformQuery">A query to get the transform component of an entity</param>
    /// <returns>The name of the sapient being if one was found, null otherwise.</returns>
    public string? FoundOrganics(EntityUid uid, EntityQuery<MobStateComponent> mobQuery, EntityQuery<TransformComponent> xformQuery)
    {
        var xform = xformQuery.GetComponent(uid);
        var childEnumerator = xform.ChildEnumerator;

        while (childEnumerator.MoveNext(out var child))
        {
            // Ghosts don't stop a ship sale.
            if (HasComp<GhostComponent>(child))
                continue;

            // Check if we have a player entity that's either still around or alive and may come back
            if (_player.TryGetSessionByEntity(child, out var session)
                || _mind.TryGetMind(child, out var mind, out var mindComp)
                    && !_mind.IsCharacterDeadPhysically(mindComp))
            {
                return Name(child);
            }
            else
            {
                var charName = FoundOrganics(child, mobQuery, xformQuery);
                if (charName != null)
                    return charName;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks for any entities marked as preventing sale on a shuttle
    /// </summary>
    /// <param name="shuttle">The entity to search (e.g. a shuttle, a station)</param>
    /// <param name="key">The UI key of the current shipyard console. Used to see if the shipyard should ignore this check</param>
    /// <param name="disableSaleQuery">A query to get any marked objects from an entity</param>
    /// <param name="xformQuery">A query to get the transform component of an entity</param>
    /// <returns>The reason that a shuttle should be blocked from sale, null otherwise.</returns>
    public string? FindDisableShipyardSaleObjects(EntityUid shuttle, ShipyardConsoleUiKey key, EntityQuery<ShipyardSellConditionComponent> disableSaleQuery, EntityQuery<TransformComponent> xformQuery)
    {
        var xform = xformQuery.GetComponent(shuttle);
        var childEnumerator = xform.ChildEnumerator;

        while (childEnumerator.MoveNext(out var child))
        {
            if (disableSaleQuery.TryGetComponent(child, out var disableSale)
                && disableSale.BlockSale is true
                && !disableSale.AllowedShipyardTypes.Contains(key))
            {
                return disableSale.Reason ?? "shipyard-console-fallback-prevent-sale";
            }
        }

        return null;
    }

    private struct IDShipAccesses
    {
        public IReadOnlyCollection<ProtoId<AccessLevelPrototype>> Tags;
        public IReadOnlyCollection<ProtoId<AccessGroupPrototype>> Groups;
    }

    /// <summary>
    ///   Returns all shuttle prototype IDs the given shipyard console can offer.
    /// </summary>
    public (List<string> available, List<string> unavailable) GetAvailableShuttles(EntityUid uid, ShipyardConsoleUiKey? key = null,
        ShipyardListingComponent? listing = null, EntityUid? targetId = null)
    {
        var available = new List<string>();
        var unavailable = new List<string>();

        if (key == null && TryComp<UserInterfaceComponent>(uid, out var ui))
        {
            // Try to find a ui key that is an instance of the shipyard console ui key
            foreach (var (k, v) in ui.Actors)
            {
                if (k is ShipyardConsoleUiKey shipyardKey)
                {
                    key = shipyardKey;
                    break;
                }
            }
        }

        // No listing provided, try to get the current one from the console being used as a default.
        if (listing is null)
            TryComp(uid, out listing);

        IDShipAccesses accesses;
        bool initialHasAccess = true;
        var voucherAllowed = new HashSet<ProtoId<VesselPrototype>>(); // Mono - this line and everything related
        // Construct access set from input type (voucher or ID card)
        if (TryComp<ShipyardVoucherComponent>(targetId, out var voucher))
        {
            voucherAllowed = voucher.Vessels;
            if (voucher.ConsoleType == key)
            {
                accesses.Tags = voucher.Access;
                accesses.Groups = voucher.AccessGroups;
                // if we're not access-based we must be vessel-based instead
                initialHasAccess = voucher.Access.Any() || voucher.AccessGroups.Any();
            }
            else
            {
                accesses.Tags = new HashSet<ProtoId<AccessLevelPrototype>>();
                accesses.Groups = new HashSet<ProtoId<AccessGroupPrototype>>();
                initialHasAccess = false;
            }
        }

        else if (TryComp<AccessComponent>(targetId, out var accessComponent))
        {
            accesses.Tags = accessComponent.Tags;
            accesses.Groups = accessComponent.Groups;
        }
        else
        {
            accesses.Tags = new HashSet<ProtoId<AccessLevelPrototype>>();
            accesses.Groups = new HashSet<ProtoId<AccessGroupPrototype>>();
        }

        foreach (var vessel in _prototypeManager.EnumeratePrototypes<VesselPrototype>())
        {
            bool hasAccess = initialHasAccess;
            // If the vessel needs access to be bought, check the user's access.
            if (!string.IsNullOrEmpty(vessel.Access))
            {
                hasAccess = false;
                // Check tags
                if (accesses.Tags.Contains(vessel.Access))
                    hasAccess = true;

                // Check each group if we haven't found access already.
                if (!hasAccess)
                {
                    foreach (var groupId in accesses.Groups)
                    {
                        var groupProto = _prototypeManager.Index(groupId);
                        if (groupProto?.Tags.Contains(vessel.Access) ?? false)
                        {
                            hasAccess = true;
                            break;
                        }
                    }
                }
            }

            // Check that the listing contains the shuttle or that the shuttle is in the group that the console is looking for
            if (listing?.Shuttles.Contains(vessel.ID) ?? false ||
                key != null && key != ShipyardConsoleUiKey.Custom &&
                vessel.Group == key)
            {
                // if not purchasable, only allow it if voucher says so
                if (vessel.Purchasable && hasAccess || voucherAllowed.Contains(vessel.ID))
                    available.Add(vessel.ID);
                else
                    unavailable.Add(vessel.ID);
            }
        }

        return (available, unavailable);
    }

    private void RefreshOpenStates(
        EntityUid uid,
        ShipyardConsoleComponent component,
        ShipyardConsoleUiKey uiKey)
    {
        foreach (var actor in _ui.GetActors(uid, uiKey).ToArray())
        {
            if (actor.Valid && Exists(actor))
                RefreshStateForActor(uid, component, actor, uiKey);
        }
    }

    private void RefreshStateForActor(
        EntityUid uid,
        ShipyardConsoleComponent component,
        EntityUid player,
        ShipyardConsoleUiKey uiKey)
    {
        _ = ObserveRefreshStateForActorAsync(uid, player, uiKey);
    }

    private async Task ObserveRefreshStateForActorAsync(
        EntityUid uid,
        EntityUid player,
        ShipyardConsoleUiKey uiKey)
    {
        try
        {
            await RefreshStateForActorAsync(uid, player, uiKey);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Could not refresh persistent shipyard state for {player} at {uid}: {exception}");
        }
    }

    private async Task RefreshStateForActorAsync(
        EntityUid uid,
        EntityUid player,
        ShipyardConsoleUiKey uiKey)
    {
        if (!_player.TryGetSessionByEntity(player, out var session))
            return;

        var ownerUserId = session.UserId;
        var records = await GetOwnerShipRecordsAsync(ownerUserId);
        if (Deleted(uid) ||
            Deleted(player) ||
            !_player.TryGetSessionByEntity(player, out session) ||
            session.UserId != ownerUserId ||
            !TryComp<ShipyardConsoleComponent>(uid, out var component))
        {
            return;
        }

        var storedShips = records
            .Where(IsCallableStoredShip)
            .OrderBy(record => GetShipDisplayName(record), StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.ShipId)
            .Select(record => new ShipyardStoredShipInfo(
                record.ShipId,
                GetShipDisplayName(record),
                record.VesselPrototypeId))
            .ToList();

        var targetId = component.TargetIdSlot.ContainerSlot?.ContainedEntity;
        var persistentTargetUsable = targetId is { Valid: true } card && HasComp<IdCardComponent>(card);
        TryComp<ShuttleDeedComponent>(targetId, out var deed);

        Guid? boundShipId = null;
        var deedOwned = deed != null &&
                        IsDeedOwnedByActor(session.UserId, deed, records, out boundShipId);
        if (deedOwned &&
            targetId is { Valid: true } deedUid &&
            boundShipId is { } persistentShipId)
        {
            TryRebindDeedToActivePersistentShuttle(
                deedUid,
                deed!,
                session.UserId,
                persistentShipId);
        }

        var activeDeed = deed?.ShuttleUid is { Valid: true } shuttle && !Deleted(shuttle);
        var parkedDeed = deed != null &&
                         !activeDeed &&
                         Guid.TryParse(deed.PersistentShipId, out _);
        var deedMutationInFlight = IsDeedMutationInFlight(targetId);

        var sellValue = 0;
        if (deedOwned && activeDeed && deed!.ShuttleUid is { } activeShuttle)
        {
            sellValue = (int)_pricing.AppraiseGrid(activeShuttle, LacksPreserveOnSaleComp);
            sellValue = CalculateShipResaleValue((uid, component), sellValue);
        }

        var accessGranted = !TryComp<AccessReaderComponent>(uid, out var accessReader) ||
                            _access.IsAllowed(player, uid, accessReader);
        var balance = TryComp<BankAccountComponent>(player, out var bank) ? bank.Balance : 0;
        TryComp<ShipyardVoucherComponent>(targetId, out var voucher);
        var freeListings = voucher != null;
        var canUnassign = deedOwned &&
                          !deedMutationInFlight &&
                          GetRemainingCooldownTime(player) == null &&
                          (voucher == null || voucher.CanBeUnassigned == true);
        var gates = GetShipyardGates(uid, out var stationGrid);
        var newState = new ShipyardConsoleInterfaceState(
            balance,
            accessGranted,
            deed != null ? GetFullName(deed) : null,
            sellValue,
            targetId.HasValue,
            targetId is { Valid: true } targetCard ? GetNetEntity(targetCard) : null,
            (byte)uiKey,
            GetAvailableShuttles(uid, uiKey, targetId: targetId),
            uiKey.ToString(),
            freeListings,
            CalculateSellRate(uid),
            gates,
            stationGrid is { Valid: true } grid ? GetNetEntity(grid) : null,
            parkedDeed,
            storedShips,
            deedOwned ? boundShipId : null,
            deed != null && !deedOwned,
            deedOwned && activeDeed && !deedMutationInFlight,
            deedOwned && activeDeed && !deedMutationInFlight,
            canUnassign,
            persistentTargetUsable &&
            deedOwned &&
            activeDeed &&
            boundShipId != null &&
            !deedMutationInFlight,
            persistentTargetUsable &&
            !activeDeed &&
            (deed == null || deedOwned) &&
            storedShips.Count > 0 &&
            !deedMutationInFlight,
            deedMutationInFlight);

        _ui.ServerSendUiMessage(uid, uiKey, new ShipyardConsoleStateMessage(newState), player);
    }

    private async Task<IReadOnlyList<LuaMShipRegistryRecord>> GetOwnerShipRecordsAsync(NetUserId ownerUserId)
    {
        try
        {
            return await _serverDb.GetLuaMShipSnapshotsByOwnerAsync(ownerUserId);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Could not read persistent ship registry for {ownerUserId}: {exception}");
            return Array.Empty<LuaMShipRegistryRecord>();
        }
    }

    private static bool IsCallableStoredShip(LuaMShipRegistryRecord record)
    {
        return record.Status == DbLuaMShipSnapshotStatus.Stored &&
               record.LeaseId == null &&
               record.QuarantinedAtUtc == null &&
               record.RetiredAtUtc == null;
    }

    private static string GetShipDisplayName(LuaMShipRegistryRecord record)
    {
        return string.IsNullOrWhiteSpace(record.ShipNameSuffix)
            ? record.ShipName
            : $"{record.ShipName} {record.ShipNameSuffix}";
    }

    private bool IsDeedOwnedByActor(
        NetUserId actorUserId,
        ShuttleDeedComponent deed,
        IReadOnlyList<LuaMShipRegistryRecord> ownerRecords,
        out Guid? persistentShipId)
    {
        persistentShipId = null;

        Guid shipId;
        if (string.IsNullOrEmpty(deed.PersistentShipId))
        {
            // A legacy deed is only a bearer deed when it does not point at a
            // live persistent grid. Old copied deeds omitted PersistentShipId,
            // so recover the canonical identity from the grid before checking
            // both the durable owner and live ownership component.
            if (deed.ShuttleUid is not { Valid: true } liveShuttle ||
                Deleted(liveShuttle) ||
                !TryComp<LuaMShipIdentityComponent>(liveShuttle, out var liveIdentity))
            {
                return true;
            }

            if (liveIdentity.ShipId == Guid.Empty)
                return false;

            shipId = liveIdentity.ShipId;
        }
        else if (!Guid.TryParse(deed.PersistentShipId, out shipId) || shipId == Guid.Empty)
        {
            // A present but malformed identity can never fall back to bearer
            // authorization.
            return false;
        }

        if (!ownerRecords.Any(record => record.ShipId == shipId))
        {
            return false;
        }

        persistentShipId = shipId;
        if (deed.ShuttleUid is not { Valid: true } shuttle || Deleted(shuttle))
            return true;

        return TryComp<LuaMShipIdentityComponent>(shuttle, out var identity) &&
               identity.ShipId == shipId &&
               TryComp<ShipOwnershipComponent>(shuttle, out var ownership) &&
               ownership.OwnerUserId == actorUserId;
    }

    private async Task<PersistentDeedAuthorization> AuthorizePersistentDeedForActorAsync(
        EntityUid player,
        EntityUid deedUid,
        ShuttleDeedComponent deed)
    {
        if (!_player.TryGetSessionByEntity(player, out var session))
            return default;

        var actorUserId = session.UserId;
        var records = await GetOwnerShipRecordsAsync(actorUserId);
        if (Deleted(player) ||
            Deleted(deedUid) ||
            !_player.TryGetSessionByEntity(player, out session) ||
            session.UserId != actorUserId ||
            !TryComp<ShuttleDeedComponent>(deedUid, out var currentDeed) ||
            currentDeed != deed ||
            !IsDeedOwnedByActor(actorUserId, deed, records, out var persistentShipId))
        {
            return default;
        }

        if (persistentShipId is { } shipId)
        {
            TryRebindDeedToActivePersistentShuttle(
                deedUid,
                deed,
                session.UserId,
                shipId);
        }

        return new(true, actorUserId, persistentShipId);
    }

    private bool TryRebindDeedToActivePersistentShuttle(
        EntityUid deedUid,
        ShuttleDeedComponent deed,
        NetUserId ownerUserId,
        Guid shipId)
    {
        var active = _shipPersistence.ActiveLeases.FirstOrDefault(lease =>
            lease.ShipId == shipId && lease.OwnerUserId == ownerUserId);
        if (active == null ||
            Deleted(active.Grid) ||
            !TryComp<LuaMShipIdentityComponent>(active.Grid, out var identity) ||
            identity.ShipId != shipId ||
            !TryComp<ShipOwnershipComponent>(active.Grid, out var ownership) ||
            ownership.OwnerUserId != ownerUserId)
        {
            return false;
        }

        var canonicalShipId = shipId.ToString("D");
        if (deed.ShuttleUid == active.Grid &&
            string.Equals(deed.PersistentShipId, canonicalShipId, StringComparison.Ordinal))
        {
            return true;
        }

        deed.ShuttleUid = active.Grid;
        deed.PersistentShipId = canonicalShipId;
        Dirty(deedUid, deed);
        return true;
    }

    private List<ShipyardGateInfo> GetShipyardGates(EntityUid consoleUid, out EntityUid? stationGrid)
    {
        var result = new List<ShipyardGateInfo>();
        stationGrid = null;
        if (_station.GetOwningStation(consoleUid) is not { Valid: true } station ||
            !TryComp<StationDataComponent>(station, out var stationData) ||
            _station.GetLargestGrid((station, stationData)) is not { } grid)
        {
            return result;
        }

        stationGrid = grid;

        foreach (var gate in _docking.GetDocks(grid)
                     .Where(gate => (gate.Comp.DockType & DockType.Airlock) != DockType.None)
                     .OrderBy(gate => gate.Comp.Name ?? Name(gate.Owner)))
        {
            var name = gate.Comp.Name == null ? Name(gate.Owner) : Loc.GetString(gate.Comp.Name);
            var position = _transform.GetRelativePosition(
                Transform(gate.Owner),
                grid,
                GetEntityQuery<TransformComponent>());
            result.Add(new ShipyardGateInfo(GetNetEntity(gate.Owner), name, !gate.Comp.Docked, position));
        }

        return result;
    }

    #region Deed Assignment
    void AssignShuttleDeedProperties(ShuttleDeedComponent deed, EntityUid? shuttleUid, string? shuttleName, string? shuttleOwner, bool purchasedWithVoucher, string? purchaseVoucherUid = null)
    {
        deed.ShuttleUid = shuttleUid;
        TryParseShuttleName(deed, shuttleName!);
        deed.ShuttleOwner = shuttleOwner;
        deed.PurchasedWithVoucher = purchasedWithVoucher;
        deed.PurchaseVoucherUid = purchaseVoucherUid;
    }

    private void OnInitDeedSpawner(EntityUid uid, StationDeedSpawnerComponent component, MapInitEvent args)
    {
        if (!HasComp<IdCardComponent>(uid)) // Test if the deed on an ID
            return;

        var xform = Transform(uid); // Get the grid the card is on
        if (xform.GridUid == null)
            return;

        if (!TryComp<ShuttleDeedComponent>(xform.GridUid.Value, out var shuttleDeed) || !TryComp<ShuttleComponent>(xform.GridUid.Value, out var shuttle) || !HasComp<TransformComponent>(xform.GridUid.Value) || shuttle == null || ShipyardMap == null)
            return;

        var output = DeedRegex.Replace($"{shuttleDeed.ShuttleOwner}", ""); // Removes content inside parentheses along with parentheses and a preceding space
        _idSystem.TryChangeFullName(uid, output); // Update the card with owner name

        var deedID = EnsureComp<ShuttleDeedComponent>(uid);
        AssignShuttleDeedProperties(deedID, shuttleDeed.ShuttleUid, shuttleDeed.ShuttleName, shuttleDeed.ShuttleOwner, shuttleDeed.PurchasedWithVoucher, shuttleDeed.PurchaseVoucherUid);

        var persistentShipId = shuttleDeed.PersistentShipId;
        if (TryComp<LuaMShipIdentityComponent>(xform.GridUid.Value, out var identity) &&
            identity.ShipId != Guid.Empty)
        {
            persistentShipId = identity.ShipId.ToString("D");
        }

        deedID.PersistentShipId = persistentShipId;
        Dirty(uid, deedID);
    }
    #endregion

    #region Ship Pricing
    // Calculates the sell rate of a given shipyard console
    private float CalculateSellRate(Entity<ShipyardConsoleComponent?> console)
    {
        if (!Resolve(console, ref console.Comp))
            return 0.0f;

        var taxRate = 0.0f;
        foreach (var taxAccount in console.Comp.TaxAccounts)
        {
            taxRate += taxAccount.Value;
        }
        taxRate = 1.0f - taxRate;  // Return the value minus the taxes

        if (console.Comp.IgnoreBaseSaleRate)
            return taxRate;
        else
            return _baseSaleRate * taxRate;
    }

    private int CalculateShipResaleValue(Entity<ShipyardConsoleComponent?> console, int baseAppraisal)
    {
        if (!Resolve(console, ref console.Comp))
            return 0;

        int resaleValue = baseAppraisal;
        if (!console.Comp.IgnoreBaseSaleRate)
            resaleValue = (int)(_baseSaleRate * resaleValue);
        var unBalanceTaxedResaleValue = resaleValue - CalculateTotalSalesTax(console.Comp, resaleValue);
        return unBalanceTaxedResaleValue;
    }

    // Calculates total sales tax over all accounts.
    private int CalculateTotalSalesTax(ShipyardConsoleComponent component, int sellValue)
    {
        int salesTax = 0;
        foreach (var (account, taxCoeff) in component.TaxAccounts)
            salesTax += CalculateSalesTax(sellValue, taxCoeff);
        return salesTax;
    }

    // Calculates sales tax for a particular account.
    private int CalculateSalesTax(int sellValue, float taxRate)
    {
        if (float.IsFinite(taxRate) && taxRate > 0f)
            return (int)(sellValue * taxRate);
        return 0;
    }
    #endregion Ship Pricing

    public void OnRenameMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRenameMessage args)
    {
        _ = ObserveRenameMessageAsync(uid, args);
    }

    private async Task ObserveRenameMessageAsync(EntityUid uid, ShipyardConsoleRenameMessage args)
    {
        try
        {
            await HandleRenameMessageAsync(uid, args);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Unhandled persistent ship rename failure at entity {uid}: {exception}");
        }
    }

    private async Task HandleRenameMessageAsync(EntityUid uid, ShipyardConsoleRenameMessage args)
    {
        if (args.Actor is not { Valid: true } player ||
            !TryComp<ShipyardConsoleComponent>(uid, out var component))
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        var authorization = await AuthorizePersistentDeedForActorAsync(player, targetId, deed);
        if (!authorization.Authorized ||
            Deleted(uid) ||
            !TryComp<ShipyardConsoleComponent>(uid, out component) ||
            component.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-owner-denied"));
            if (component != null)
                PlayDenySound(player, uid, component);
            return;
        }

        // Validate the new name
        var newName = args.NewName.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            ConsolePopup(player, "Ship name cannot be empty.");
            PlayDenySound(player, uid, component);
            return;
        }

        if (newName.Length > ShuttleDeedComponent.MaxNameLength)
        {
            ConsolePopup(player, $"Ship name cannot exceed {ShuttleDeedComponent.MaxNameLength} characters.");
            PlayDenySound(player, uid, component);
            return;
        }

        // Get the old name for logging
        var oldName = GetFullName(deed);

        // Rename the ship using the existing method
        if (TryRenameShuttle(targetId, deed, newName, deed.ShuttleNameSuffix))
        {
            ConsolePopup(player, $"Ship renamed to '{GetFullName(deed)}'");
            PlayConfirmSound(player, uid, component);

            RefreshOpenStates(uid, component, (ShipyardConsoleUiKey)args.UiKey);

            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low,
                $"{ToPrettyString(player):actor} renamed ship from '{oldName}' to '{GetFullName(deed)}' via {ToPrettyString(uid)}");
        }
        else
        {
            ConsolePopup(player, "Failed to rename ship.");
            PlayDenySound(player, uid, component);
        }
    }

    public void OnUnassignDeedMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleUnassignDeedMessage args)
    {
        _ = ObserveUnassignDeedMessageAsync(uid, args);
    }

    private async Task ObserveUnassignDeedMessageAsync(EntityUid uid, ShipyardConsoleUnassignDeedMessage args)
    {
        try
        {
            await HandleUnassignDeedMessageAsync(uid, args);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Unhandled persistent deed unassign failure at entity {uid}: {exception}");
        }
    }

    private async Task HandleUnassignDeedMessageAsync(
        EntityUid uid,
        ShipyardConsoleUnassignDeedMessage args)
    {
        if (args.Actor is not { Valid: true } player ||
            !TryComp<ShipyardConsoleComponent>(uid, out var component))
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) ||
            deed.ShuttleUid is not { Valid: true } && !Guid.TryParse(deed.PersistentShipId, out _))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        var authorization = await AuthorizePersistentDeedForActorAsync(player, targetId, deed);
        if (!authorization.Authorized ||
            Deleted(uid) ||
            !TryComp<ShipyardConsoleComponent>(uid, out component) ||
            component.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-owner-denied"));
            if (component != null)
                PlayDenySound(player, uid, component);
            return;
        }

        if (TryComp<ShipyardVoucherComponent>(targetId, out var voucher) && voucher.CanBeUnassigned != true) // Mono: If voucher is not allowed to unassign deeds, fail.
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-unassign"));
            PlayDenySound(player, uid, component);
            return;
        } // end mono

        // Check if the player is on cooldown
        var cooldown = EnsureComp<ShipyardUnassignCooldownComponent>(player);
        var currentTime = _timing.CurTime;

        if (currentTime < cooldown.NextUnassignTime)
        {
            // Calculate remaining time
            var timeRemaining = cooldown.NextUnassignTime - currentTime;
            var hoursRemaining = (int)timeRemaining.TotalHours;
            var minutesRemaining = (int)timeRemaining.TotalMinutes % 60;

            // Display cooldown message
            var cooldownMessage = Loc.GetString(
                "shipyard-console-unassign-cooldown",
                ("hours", hoursRemaining),
                ("minutes", minutesRemaining)
            );
            ConsolePopup(player, cooldownMessage);
            PlayDenySound(player, uid, component);
            return;
        }

        // Get the name of the ship before we remove the component
        var shipName = GetFullName(deed);

        // Remove the deed component from the ID card
        RemComp<ShuttleDeedComponent>(targetId);

        // Set the cooldown
        cooldown.NextUnassignTime = currentTime + cooldown.CooldownDuration;

        ConsolePopup(player, Loc.GetString("shipyard-console-deed-unassigned"));
        PlayConfirmSound(player, uid, component);

        RefreshOpenStates(uid, component, (ShipyardConsoleUiKey)args.UiKey);

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low,
            $"{ToPrettyString(player):actor} unassigned deed for ship '{shipName}' from {ToPrettyString(targetId)} via {ToPrettyString(uid)}");
    }

    private void OnParkShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleParkMessage args)
    {
        _ = ObserveParkShipMessageAsync(uid, args);
    }

    private async Task ObserveParkShipMessageAsync(EntityUid uid, ShipyardConsoleParkMessage args)
    {
        try
        {
            await HandleParkShipMessageAsync(uid, args);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Unhandled persistent ship parking failure at entity {uid}: {exception}");
        }
    }

    private async Task HandleParkShipMessageAsync(EntityUid uid, ShipyardConsoleParkMessage args)
    {
        if (args.Actor is not { Valid: true } player ||
            !TryComp<ShipyardConsoleComponent>(uid, out var component))
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId ||
            !HasComp<IdCardComponent>(targetId) ||
            !TryComp<ShuttleDeedComponent>(targetId, out var deed))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        var authorization = await AuthorizePersistentDeedForActorAsync(player, targetId, deed);
        if (!authorization.Authorized ||
            Deleted(uid) ||
            !TryComp<ShipyardConsoleComponent>(uid, out component) ||
            component.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId ||
            deed.ShuttleUid is not { Valid: true } shuttle ||
            Deleted(shuttle) ||
            !TryComp<LuaMShipIdentityComponent>(shuttle, out var identity) ||
            identity.ShipId == Guid.Empty ||
            !TryComp<ShipOwnershipComponent>(shuttle, out var ownership) ||
            ownership.OwnerUserId != authorization.ActorUserId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-owner-denied"));
            if (component != null)
                PlayDenySound(player, uid, component);
            return;
        }

        if (_station.GetOwningStation(uid) is not { Valid: true } station ||
            !TryComp<StationDataComponent>(station, out var stationData) ||
            _station.GetLargestGrid((station, stationData)) is not { } stationGrid ||
            !_docking.AreGridsDocked(shuttle, stationGrid))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-park-docked"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryReservePersistentShipCall(identity.ShipId, targetId, out var reservationId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-call-pending"));
            PlayDenySound(player, uid, component);
            return;
        }

        RefreshOpenStates(uid, component, (ShipyardConsoleUiKey)args.UiKey);
        try
        {
            EvacuateCrewForParking(shuttle, Transform(uid).Coordinates);

            var result = await _shipPersistence.StoreAndDeactivateAsync(
                identity.ShipId,
                _gameTicker.RoundId,
                DateTime.UtcNow);
            if (!result.Success)
            {
                ConsolePopup(player, Loc.GetString(
                    "shipyard-console-park-failed",
                    ("reason", result.Reason ?? result.Status.ToString())));
                PlayDenySound(player, uid, component);
                return;
            }

            if (!IsPersistentShipCallReservationHeld(identity.ShipId, targetId, reservationId) ||
                component.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId ||
                !TryComp<ShuttleDeedComponent>(targetId, out var currentDeed) ||
                currentDeed != deed)
            {
                // The durable store succeeded, so discard the now-inactive
                // physical grid without overwriting a card that changed state.
                QueueDeletePersistentShipWithStation(shuttle);
                ConsolePopup(player, Loc.GetString("shipyard-console-purchase-changed"));
                PlayDenySound(player, uid, component);
                return;
            }

            deed.PersistentShipId = identity.ShipId.ToString("D");
            deed.ShuttleUid = null;
            Dirty(targetId, deed);
            QueueDeletePersistentShipWithStation(shuttle);
            ConsolePopup(player, Loc.GetString("shipyard-console-park-success"));
            PlayConfirmSound(player, uid, component);
        }
        finally
        {
            ReleasePersistentShipCall(identity.ShipId, targetId, reservationId);
            if (Exists(uid) && TryComp<ShipyardConsoleComponent>(uid, out var refreshedComponent))
                RefreshOpenStates(uid, refreshedComponent, (ShipyardConsoleUiKey)args.UiKey);
        }
    }

    private void EvacuateCrewForParking(EntityUid shuttle, EntityCoordinates destination)
    {
        var crew = new List<EntityUid>();
        var pending = new Stack<EntityUid>();
        pending.Push(shuttle);

        while (pending.TryPop(out var current))
        {
            var children = Transform(current).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                pending.Push(child);
                if (HasComp<MobStateComponent>(child))
                    crew.Add(child);
            }
        }

        foreach (var mob in crew)
        {
            if (TryComp<Content.Shared.Buckle.Components.BuckleComponent>(mob, out var buckle) && buckle.Buckled)
                _buckle.Unbuckle(mob, user: null);

            _container.TryRemoveFromContainer(mob, force: true);
            _transform.SetCoordinates(mob, destination);
            _transform.AttachToGridOrMap(mob);
        }
    }

    private void OnCallShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleCallMessage args)
    {
        _ = ObserveCallShipMessageAsync(uid, args);
    }

    private async Task ObserveCallShipMessageAsync(EntityUid uid, ShipyardConsoleCallMessage args)
    {
        try
        {
            await HandleCallShipMessageAsync(uid, args);
        }
        catch (Exception exception)
        {
            _sawmill.Error($"Unhandled persistent ship call failure at entity {uid}: {exception}");
        }
    }

    private void QueueDeletePersistentShipWithStation(EntityUid shuttle)
    {
        var owningStation = _station.GetOwningStation(shuttle);
        if (TryGetDeletablePersistentVesselStation(shuttle, out var shuttleStation))
        {
            _station.DeleteStation(shuttleStation);
        }
        else if (owningStation is { Valid: true })
        {
            _sawmill.Warning(
                $"Refused to delete unproven station {owningStation.Value} while removing persistent ship grid {shuttle}.");
        }

        QueueDel(shuttle);
    }

    internal bool TryGetDeletablePersistentVesselStation(EntityUid shuttle, out EntityUid station)
    {
        station = EntityUid.Invalid;
        if (!TryComp<VesselComponent>(shuttle, out var vessel) ||
            _station.GetOwningStation(shuttle) is not { Valid: true } owningStation ||
            !TryComp<StationDataComponent>(owningStation, out var stationData) ||
            stationData.Grids.Count != 1 ||
            _station.GetLargestGrid((owningStation, stationData)) != shuttle ||
            !TryComp<ExtraShuttleInformationComponent>(owningStation, out var vesselInfo) ||
            vesselInfo.Vessel != vessel.VesselId)
        {
            return false;
        }

        var foundShuttle = false;
        var memberQuery = EntityQueryEnumerator<StationMemberComponent>();
        while (memberQuery.MoveNext(out var memberGrid, out var member))
        {
            if (member.Station != owningStation)
                continue;
            if (memberGrid != shuttle)
                return false;

            foundShuttle = true;
        }

        if (!foundShuttle)
            return false;

        station = owningStation;
        return true;
    }

    private async Task HandleCallShipMessageAsync(EntityUid uid, ShipyardConsoleCallMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        ShipyardConsoleComponent? component = null;
        if (!_player.TryGetSessionByEntity(player, out var session) ||
            !TryComp(uid, out component) ||
            component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId ||
            !HasComp<IdCardComponent>(targetId) ||
            _station.GetOwningStation(uid) is not { Valid: true } station ||
            !TryComp<StationDataComponent>(station, out var stationData) ||
            _station.GetLargestGrid((station, stationData)) is not { } stationGrid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-stored-ship"));
            if (component != null)
                PlayDenySound(player, uid, component);
            return;
        }

        var ownerUserId = session.UserId;
        var records = await GetOwnerShipRecordsAsync(ownerUserId);
        if (Deleted(uid) ||
            Deleted(player) ||
            Deleted(targetId) ||
            !_player.TryGetSessionByEntity(player, out session) ||
            session.UserId != ownerUserId ||
            !TryComp<ShipyardConsoleComponent>(uid, out component) ||
            component.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId ||
            _station.GetOwningStation(uid) != station)
        {
            return;
        }

        var stored = records.SingleOrDefault(record =>
            record.ShipId == args.ShipId && IsCallableStoredShip(record));
        TryComp<ShuttleDeedComponent>(targetId, out var existingDeed);
        var existingDeedAuthorized = existingDeed == null ||
                                     IsDeedOwnedByActor(
                                         session.UserId,
                                         existingDeed,
                                         records,
                                         out _);
        if (stored == null ||
            !existingDeedAuthorized ||
            existingDeed?.ShuttleUid is { Valid: true } existingShuttle &&
            !Deleted(existingShuttle))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-stored-ship"));
            PlayDenySound(player, uid, component);
            RefreshStateForActor(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        var selectedGate = GetEntity(args.Gate);
        if (!TryComp<DockingComponent>(selectedGate, out var dock) ||
            (dock.DockType & DockType.Airlock) == DockType.None ||
            dock.Docked ||
            Transform(selectedGate).GridUid != stationGrid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-gate-invalid"));
            PlayDenySound(player, uid, component);
            RefreshStateForActor(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        var expectedCardState = new PersistentShipCallCardState(
            existingDeed != null,
            existingDeed?.ShuttleUid,
            existingDeed?.PersistentShipId);
        if (!TryReservePersistentShipCall(stored.ShipId, targetId, out var reservationId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-call-pending"));
            PlayDenySound(player, uid, component);
            RefreshStateForActor(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        RefreshOpenStates(uid, component, (ShipyardConsoleUiKey)args.UiKey);
        var usedProximityFallback = false;
        try
        {
            _map.CreateMap(out var restoreMap);
            _map.SetPaused(restoreMap, false);
            LuaMShipOrchestrationResult result;
            try
            {
                result = await _shipPersistence.RestoreClaimAsync(
                    stored.ShipId,
                    session.UserId,
                    _gameTicker.RoundId,
                    restoreMap,
                    DateTime.UtcNow,
                    restored =>
                    {
                        if (!IsPersistentShipCallTargetCurrent(
                                uid,
                                player,
                                session.UserId,
                                targetId,
                                stored.ShipId,
                                reservationId,
                                expectedCardState))
                        {
                            _sawmill.Error(
                                $"Persistent ship call placement aborted for {stored.ShipId}: target card state changed.");
                            return false;
                        }

                        if (!TryComp<ShuttleComponent>(restored, out var shuttle))
                        {
                            _sawmill.Error(
                                $"Persistent ship call placement failed for {stored.ShipId}: restored grid has no ShuttleComponent.");
                            return false;
                        }

                        if (_shuttle.TryFTLDockAtDockOrPlaceNearbyIfDockless(
                                restored,
                                shuttle,
                                stationGrid,
                                selectedGate))
                        {
                            if (!Comp<DockingComponent>(selectedGate).Docked)
                            {
                                _sawmill.Warning(
                                    $"Persistent ship {stored.ShipId} has no usable airlock and was placed near selected gate {selectedGate}.");
                            }

                            return true;
                        }

                        // Some hulls cannot geometrically mate with a particular
                        // gate even when it is free. Deliver them to a collision-
                        // checked proximity slot instead of trapping the owner in
                        // a permanent call/rollback loop.
                        if (_shuttle.TryFTLProximity(restored, stationGrid))
                        {
                            usedProximityFallback = true;
                            _sawmill.Warning(
                                $"Persistent ship {stored.ShipId} could not dock at gate {selectedGate}; " +
                                $"restored grid {restored} was placed safely near station grid {stationGrid}.");
                            return true;
                        }

                        _sawmill.Error(
                            $"Persistent ship call placement failed for {stored.ShipId}: selected gate {selectedGate} " +
                            $"could not dock restored grid {restored}, and no safe proximity placement was available.");
                        return false;
                    });
            }
            finally
            {
                if (_map.MapExists(restoreMap))
                    _map.DeleteMap(restoreMap);
            }

            if (!result.Success || result.Grid == null)
            {
                _sawmill.Error(
                    $"Persistent ship call failed for ship {stored.ShipId}, owner {session.UserId}, gate {selectedGate}: {result.Status}: {result.Reason}");
                ConsolePopup(player, Loc.GetString(
                    "shipyard-console-call-failed",
                    ("reason", result.Reason ?? result.Status.ToString())));
                PlayDenySound(player, uid, component);
                return;
            }

            if (!IsPersistentShipCallTargetCurrent(
                    uid,
                    player,
                    session.UserId,
                    targetId,
                    stored.ShipId,
                    reservationId,
                    expectedCardState))
            {
                var rollback = await _shipPersistence.StoreAndDeactivateAsync(
                    stored.ShipId,
                    _gameTicker.RoundId,
                    DateTime.UtcNow);
                if (rollback.Success)
                {
                    QueueDeletePersistentShipWithStation(result.Grid.Value);
                }
                else
                {
                    _sawmill.Error(
                        $"CRITICAL: restored ship {stored.ShipId} lost its target card and could not be returned to storage: " +
                        $"{rollback.Status}: {rollback.Reason}");
                }

                ConsolePopup(player, Loc.GetString("shipyard-console-purchase-changed"));
                PlayDenySound(player, uid, component);
                return;
            }

            var deed = EnsureComp<ShuttleDeedComponent>(targetId);
            AssignShuttleDeedProperties(
                deed,
                result.Grid.Value,
                stored.ShipName,
                Name(player).Trim(),
                stored.PurchasedWithVoucher);
            deed.ShuttleNameSuffix = stored.ShipNameSuffix;
            deed.DeedHolder = targetId;
            deed.PersistentShipId = stored.ShipId.ToString("D");
            Dirty(targetId, deed);
            TryComp<PersistentShipyardAccessComponent>(result.Grid.Value, out var persistentAccess);
            ShipyardConsoleUiKey? legacyShipyardGroup = null;
            if (TryComp<VesselComponent>(result.Grid.Value, out var restoredVessel) &&
                _prototypeManager.TryIndex(restoredVessel.VesselId, out var restoredVesselPrototype))
            {
                legacyShipyardGroup = restoredVesselPrototype.Group;
            }

            GrantShipAccessLevels(
                targetId,
                ResolvePersistentShipAccessLevels(persistentAccess?.GrantedLevels, legacyShipyardGroup));

            var successLocId = GetPersistentShipCallSuccessLocId(usedProximityFallback);
            if (usedProximityFallback)
            {
                ConsolePopup(player, Loc.GetString(successLocId));
            }
            else
            {
                var gateName = dock.Name == null ? Name(selectedGate) : Loc.GetString(dock.Name);
                ConsolePopup(player, Loc.GetString(successLocId, ("gate", gateName)));
            }

            PlayConfirmSound(player, uid, component);
        }
        finally
        {
            ReleasePersistentShipCall(stored.ShipId, targetId, reservationId);
            if (Exists(uid) && TryComp<ShipyardConsoleComponent>(uid, out var refreshedComponent))
                RefreshOpenStates(uid, refreshedComponent, (ShipyardConsoleUiKey)args.UiKey);
        }
    }

    internal static string GetPersistentShipCallSuccessLocId(bool usedProximityFallback)
    {
        return usedProximityFallback
            ? "shipyard-console-call-success-nearby"
            : "shipyard-console-call-success";
    }

    /// <summary>
    /// Adds access levels when a deed is published onto an ID card without
    /// replacing access already held by that card.
    /// </summary>
    private void GrantShipAccessLevels(
        EntityUid targetId,
        IEnumerable<ProtoId<AccessLevelPrototype>> accessLevels)
    {
        if (!TryComp<AccessComponent>(targetId, out var access))
            return;

        var newAccess = access.Tags.ToHashSet();
        newAccess.UnionWith(accessLevels);
        _accessSystem.TrySetTags(targetId, newAccess, access);
    }

    internal static IReadOnlyCollection<ProtoId<AccessLevelPrototype>> ResolvePersistentShipAccessLevels(
        IReadOnlyCollection<ProtoId<AccessLevelPrototype>>? persistedLevels,
        ShipyardConsoleUiKey? legacyShipyardGroup = null)
    {
        if (persistedLevels != null)
            return persistedLevels;

        // Snapshots written before PersistentShipyardAccessComponent existed
        // can still recover the deterministic grant from their vessel group.
        return legacyShipyardGroup == ShipyardConsoleUiKey.Security
            ? PersistentShipSecurityAccessLevels
            : PersistentShipCaptainAccessLevels;
    }

    private bool IsPersistentShipCallTargetCurrent(
        EntityUid consoleUid,
        EntityUid player,
        NetUserId ownerUserId,
        EntityUid targetId,
        Guid shipId,
        Guid reservationId,
        PersistentShipCallCardState expectedState)
    {
        if (!IsPersistentShipCallReservationHeld(shipId, targetId, reservationId) ||
            Deleted(consoleUid) ||
            Deleted(player) ||
            Deleted(targetId) ||
            !_player.TryGetSessionByEntity(player, out var currentSession) ||
            currentSession.UserId != ownerUserId ||
            !TryComp<ShipyardConsoleComponent>(consoleUid, out var currentComponent) ||
            currentComponent.TargetIdSlot.ContainerSlot?.ContainedEntity != targetId)
        {
            return false;
        }

        var hasCurrentDeed = TryComp<ShuttleDeedComponent>(targetId, out var currentDeed);
        return hasCurrentDeed == expectedState.HasDeed &&
               (!hasCurrentDeed ||
                currentDeed!.ShuttleUid == expectedState.ShuttleUid &&
                string.Equals(
                    currentDeed.PersistentShipId,
                    expectedState.PersistentShipId,
                    StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct PersistentShipCallCardState(
        bool HasDeed,
        EntityUid? ShuttleUid,
        string? PersistentShipId);
}
