using System.Globalization;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.Ghost;
using Content.Server.Hands.Systems;
using Content.Server.Inventory;
using Content.Server.Popups;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.StationRecords;
using Content.Server.StationRecords.Systems;
using Content.Server._LuaM.Cryo;
using Content.Shared.Access.Systems;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.Chat;
using Content.Shared.Climbing.Systems;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Hands.Components;
using Content.Shared.Mind.Components;
using Content.Shared.StationRecords;
using Content.Shared.UserInterface;
using Robust.Server.Audio;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Bed.Cryostorage;

/// <inheritdoc/>
public sealed partial class CryostorageSystem : SharedCryostorageSystem
{
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private ChatSystem _chatSystem = default!;
    [Dependency] private ClimbSystem _climb = default!;
    [Dependency] private ContainerSystem _container = default!;
    [Dependency] private GhostSystem _ghostSystem = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private ServerInventorySystem _inventory = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private StationJobsSystem _stationJobs = default!;
    [Dependency] private StationRecordsSystem _stationRecords = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private LuaMDeepCryoPersistenceSystem _deepCryo = default!;

    private readonly HashSet<EntityUid> _durableStoreFinalizing = new();
    private readonly Dictionary<EntityUid, PendingUpstreamStoreControlFence> _pendingStoreControlFences = new();

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CryostorageComponent, BeforeActivatableUIOpenEvent>(OnBeforeUIOpened);
        SubscribeLocalEvent<CryostorageComponent, CryostorageRemoveItemBuiMessage>(OnRemoveItemBuiMessage);
        SubscribeLocalEvent<CryostorageComponent, ContainerIsRemovingAttemptEvent>(OnDeepCryoRemoveAttempt);
        SubscribeLocalEvent<LuaMDeepCryoRestoreReservationComponent, ContainerIsInsertingAttemptEvent>(OnDeepCryoInsertAttempt);
        SubscribeLocalEvent<LuaMDeepCryoRestoreReservationComponent, ContainerIsRemovingAttemptEvent>(OnDeepCryoReservedRemoveAttempt);

        SubscribeLocalEvent<CryostorageContainedComponent, PlayerSpawnCompleteEvent>(OnPlayerSpawned);
        SubscribeLocalEvent<CryostorageContainedComponent, MindRemovedMessage>(OnMindRemoved);
        SubscribeLocalEvent<CryostorageContainedComponent, EntityTerminatingEvent>(OnContainedTerminating);

        _playerManager.PlayerStatusChanged += PlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _playerManager.PlayerStatusChanged -= PlayerStatusChanged;
    }

    private void OnBeforeUIOpened(Entity<CryostorageComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        UpdateCryostorageUIState(ent);
    }

    private void OnRemoveItemBuiMessage(Entity<CryostorageComponent> ent, ref CryostorageRemoveItemBuiMessage args)
    {
        var (_, comp) = ent;
        var attachedEntity = args.Actor;
        var cryoContained = GetEntity(args.StoredEntity);

        if (!comp.StoredPlayers.Contains(cryoContained) || !IsInPausedMap(cryoContained))
            return;

        // Persistent inventory is part of the hashed snapshot. Taking an item
        // without a corresponding durable mutation would mint a duplicate on wake.
        if (_deepCryo.IsPersistentBody(cryoContained))
        {
            _popup.PopupEntity(Loc.GetString("cryostorage-popup-access-denied"), attachedEntity, attachedEntity);
            return;
        }

        if (!HasComp<HandsComponent>(attachedEntity))
            return;

        if (!_accessReader.IsAllowed(attachedEntity, ent))
        {
            _popup.PopupEntity(Loc.GetString("cryostorage-popup-access-denied"), attachedEntity, attachedEntity);
            return;
        }

        EntityUid? entity = null;
        if (args.Type == CryostorageRemoveItemBuiMessage.RemovalType.Hand)
        {
            if (_hands.TryGetHand(cryoContained, args.Key, out var hand))
                entity = hand.HeldEntity;
        }
        else
        {
            if (_inventory.TryGetSlotContainer(cryoContained, args.Key, out var slot, out _))
                entity = slot.ContainedEntity;
        }

        if (entity == null)
            return;

        AdminLog.Add(LogType.Action, LogImpact.High,
            $"{ToPrettyString(attachedEntity):player} removed item {ToPrettyString(entity)} from cryostorage-contained player " +
            $"{ToPrettyString(cryoContained):player}, stored in cryostorage {ToPrettyString(ent)}");

        _container.TryRemoveFromContainer(entity.Value);
        _transform.SetCoordinates(entity.Value, Transform(attachedEntity).Coordinates);
        _hands.PickupOrDrop(attachedEntity, entity.Value);
        UpdateCryostorageUIState(ent);
    }

    private void OnDeepCryoInsertAttempt(
        Entity<LuaMDeepCryoRestoreReservationComponent> ent,
        ref ContainerIsInsertingAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnDeepCryoReservedRemoveAttempt(
        Entity<LuaMDeepCryoRestoreReservationComponent> ent,
        ref ContainerIsRemovingAttemptEvent args)
    {
        // This component is also used by Frontier CryoSleep pods, which do not
        // carry CryostorageComponent. Fence every ordinary ContainerSystem
        // removal until the exact durable ACK or rollback clears the reservation.
        args.Cancel();
    }

    private void OnDeepCryoRemoveAttempt(
        Entity<CryostorageComponent> ent,
        ref ContainerIsRemovingAttemptEvent args)
    {
        if (args.Container.ID == ent.Comp.ContainerId &&
            (HasComp<LuaMDeepCryoRestoreReservationComponent>(ent.Owner) ||
             (!_durableStoreFinalizing.Contains(args.EntityUid) &&
              _deepCryo.IsStorePending(args.EntityUid))))
        {
            args.Cancel();
        }
    }

    private void UpdateCryostorageUIState(Entity<CryostorageComponent> ent)
    {
        var state = new CryostorageBuiState(GetAllContainedData(ent));
        _ui.SetUiState(ent.Owner, CryostorageUIKey.Key, state);
    }

    private void OnPlayerSpawned(Entity<CryostorageContainedComponent> ent, ref PlayerSpawnCompleteEvent args)
    {
        // if you spawned into cryostorage, we're not gonna round-remove you.
        ent.Comp.GracePeriodEndTime = null;
    }

    private void OnMindRemoved(Entity<CryostorageContainedComponent> ent, ref MindRemovedMessage args)
    {
        var comp = ent.Comp;

        if (!TryComp<CryostorageComponent>(comp.Cryostorage, out var cryostorageComponent))
            return;

        if (comp.GracePeriodEndTime != null)
            comp.GracePeriodEndTime = Timing.CurTime + cryostorageComponent.NoMindGracePeriod;
        comp.AllowReEnteringBody = false;
        comp.UserId = args.Mind.Comp.UserId;
    }

    private void OnContainedTerminating(
        Entity<CryostorageContainedComponent> ent,
        ref EntityTerminatingEvent args)
    {
        _pendingStoreControlFences.Remove(ent.Owner);
    }

    private void PlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.Session.AttachedEntity is not { } entity)
            return;

        if (!TryComp<CryostorageContainedComponent>(entity, out var containedComponent))
            return;

        if (args.NewStatus is SessionStatus.Disconnected or SessionStatus.Zombie)
        {
            containedComponent.AllowReEnteringBody = true;
            var delay = CompOrNull<CryostorageComponent>(containedComponent.Cryostorage)?.NoMindGracePeriod ?? TimeSpan.Zero;
            containedComponent.GracePeriodEndTime = Timing.CurTime + delay;
            containedComponent.UserId = args.Session.UserId;
        }
        else if (args.NewStatus == SessionStatus.InGame)
        {
            HandleCryostorageReconnection((entity, containedComponent));
        }
    }

    public void HandleEnterCryostorage(Entity<CryostorageContainedComponent> ent, NetUserId? userId)
    {
        if (_deepCryo.IsStorePending(ent.Owner))
            return;

        _pendingStoreControlFences.Remove(ent.Owner);
        var controlUserId = userId ?? ent.Comp.UserId;
        if (controlUserId == null && _deepCryo.TryGetIdentity(ent.Owner, out var identityUserId))
            controlUserId = identityUserId;

        if (controlUserId is { } stableUserId)
        {
            var controlFence = new PendingUpstreamStoreControlFence(stableUserId);
            _pendingStoreControlFences[ent.Owner] = controlFence;
            if (!TryFenceUpstreamStoreControl(ent.Owner, controlFence))
            {
                Log.Error($"Refused durable cryostorage store for {ToPrettyString(ent.Owner)}: control-fence-failed");
                RestoreUpstreamStoreControl(ent.Owner);
                ent.Comp.GracePeriodEndTime = Timing.CurTime + TimeSpan.FromMinutes(1);
                Dirty(ent.Owner, ent.Comp);
                return;
            }
        }

        if (!_deepCryo.TryBeginStore(
                ent.Owner,
                ent.Comp.Cryostorage ?? EntityUid.Invalid,
                controlUserId,
                LuaMDeepCryoSource.UpstreamCryostorage,
                completion =>
                {
                    if (!completion.Success)
                    {
                        Log.Error($"Deep cryostorage store failed for {ToPrettyString(ent.Owner)}: {completion.FailureReason}");
                        RestoreUpstreamStoreControl(ent.Owner);
                        if (TryComp<CryostorageContainedComponent>(ent.Owner, out var retryContained))
                        {
                            retryContained.GracePeriodEndTime = Timing.CurTime + TimeSpan.FromMinutes(1);
                            Dirty(ent.Owner, retryContained);
                        }
                        return;
                    }

                    if (!FinishUpstreamStoreControlFence(ent.Owner))
                    {
                        throw new InvalidOperationException(
                            $"Cryostorage control fence could not be finalized after durable store for {ent.Owner}.");
                    }

                    if (!Exists(ent.Owner) ||
                        !TryComp<CryostorageContainedComponent>(ent.Owner, out var current) ||
                        current.Cryostorage == null)
                    {
                        throw new InvalidOperationException(
                            $"Cryostorage body changed after durable store for {ent.Owner}.");
                    }

                    if (!FinalizeEnterCryostorage((ent.Owner, current), controlUserId))
                    {
                        throw new InvalidOperationException(
                            $"Cryostorage body could not be finalized after durable store for {ent.Owner}.");
                    }

                    if (controlUserId != null &&
                        _playerManager.TryGetSessionById(controlUserId.Value, out var session) &&
                        session.Status == SessionStatus.InGame)
                    {
                        // StoreCore removes its pending fence after this callback
                        // returns. Retry on the next tick so a reconnect that
                        // happened during the DB write is not lost behind it.
                        Timer.Spawn(TimeSpan.Zero, () =>
                        {
                            if (Exists(ent.Owner) &&
                                TryComp<CryostorageContainedComponent>(ent.Owner, out var reconnected))
                            {
                                HandleCryostorageReconnection((ent.Owner, reconnected));
                            }
                        });
                    }
                },
                out var reason))
        {
            Log.Error($"Refused durable cryostorage store for {ToPrettyString(ent.Owner)}: {reason}");
            if (!_deepCryo.IsPresenceSuspended(ent.Owner))
            {
                RestoreUpstreamStoreControl(ent.Owner);
                ent.Comp.GracePeriodEndTime = Timing.CurTime + TimeSpan.FromMinutes(1);
                Dirty(ent.Owner, ent.Comp);
            }
        }
    }

    private bool FinalizeEnterCryostorage(Entity<CryostorageContainedComponent> ent, NetUserId? userId)
    {
        var comp = ent.Comp;
        var cryostorageEnt = ent.Comp.Cryostorage;

        var station = _station.GetOwningStation(ent);
        var name = Name(ent.Owner);

        if (!TryComp<CryostorageComponent>(cryostorageEnt, out var cryostorageComponent))
            return false;

        // if we have a session, we use that to add back in all the job slots the player had.
        if (userId != null)
        {
            foreach (var uniqueStation in _station.GetStationsSet())
            {
                if (!TryComp<StationJobsComponent>(uniqueStation, out var stationJobs))
                    continue;

                if (!_stationJobs.TryGetPlayerJobs(uniqueStation, userId.Value, out var jobs, stationJobs))
                    continue;

                foreach (var job in jobs)
                {
                    _stationJobs.TryAdjustJobSlot(uniqueStation, job, 1, clamp: true);
                }

                _stationJobs.TryRemovePlayerJobs(uniqueStation, userId.Value, stationJobs);
            }
        }

        _audio.PlayPvs(cryostorageComponent.RemoveSound, ent);

        EnsurePausedMap();
        if (PausedMap == null)
        {
            Log.Error("CryoSleep map was unexpectedly null");
            return false;
        }

        if (!CryoSleepRejoiningEnabled || !comp.AllowReEnteringBody)
        {
            if (userId != null && Mind.TryGetMind(userId.Value, out var mind) &&
                HasComp<CryostorageContainedComponent>(mind.Value.Comp.CurrentEntity))
            {
                _ghostSystem.OnGhostAttempt(mind.Value, false);
            }
        }

        comp.AllowReEnteringBody = false;
        _durableStoreFinalizing.Add(ent.Owner);
        try
        {
            _transform.SetParent(ent, PausedMap.Value);
        }
        finally
        {
            _durableStoreFinalizing.Remove(ent.Owner);
        }
        cryostorageComponent.StoredPlayers.Add(ent);
        Dirty(ent, comp);
        UpdateCryostorageUIState((cryostorageEnt.Value, cryostorageComponent));
        AdminLog.Add(LogType.Action, LogImpact.High, $"{ToPrettyString(ent):player} was entered into cryostorage inside of {ToPrettyString(cryostorageEnt.Value)}");

        if (!TryComp<StationRecordsComponent>(station, out var stationRecords))
            return true;

        var jobName = Loc.GetString("earlyleave-cryo-job-unknown");
        var recordId = _stationRecords.GetRecordByName(station.Value, name);
        if (recordId != null)
        {
            var key = new StationRecordKey(recordId.Value, station.Value);
            if (_stationRecords.TryGetRecord<GeneralStationRecord>(key, out var entry, stationRecords))
                jobName = entry.JobTitle;

            _stationRecords.RemoveRecord(key, stationRecords);
        }

        _chatSystem.DispatchStationAnnouncement(station.Value,
            Loc.GetString(
                "earlyleave-cryo-announcement",
                ("character", name),
                ("entity", ent.Owner), // gender things for supporting downstreams with other languages
                ("job", CultureInfo.CurrentCulture.TextInfo.ToTitleCase(jobName))
            ), Loc.GetString("earlyleave-cryo-sender"),
            playDefaultSound: false
        );
        return true;
    }

    private async void HandleCryostorageReconnection(Entity<CryostorageContainedComponent> entity)
    {
        var (uid, comp) = entity;
        if (_deepCryo.IsStorePending(uid))
        {
            var pendingUserId = comp.UserId;
            if (pendingUserId == null && _deepCryo.TryGetIdentity(uid, out var identityUserId))
                pendingUserId = identityUserId;

            if (pendingUserId is { } stableUserId)
            {
                if (!_pendingStoreControlFences.TryGetValue(uid, out var controlFence))
                {
                    controlFence = new PendingUpstreamStoreControlFence(stableUserId);
                    _pendingStoreControlFences[uid] = controlFence;
                }

                if (!TryFenceUpstreamStoreControl(uid, controlFence))
                    Log.Error($"Deep cryostorage reconnect could not revoke control from pending store body {ToPrettyString(uid)}.");
            }

            return;
        }

        if (!CryoSleepRejoiningEnabled || !IsInPausedMap(uid))
            return;

        if (_deepCryo.IsPersistentBody(uid))
        {
            var userId = comp.UserId;
            if (userId == null && _deepCryo.TryGetIdentity(uid, out var identityUser))
                userId = identityUser;
            if (userId == null)
                return;

            var unpublishedCoordinates = Transform(uid).Coordinates;
            var unpublishedGracePeriod = comp.GracePeriodEndTime;
            if (!TryFenceUpstreamWakeControl(userId.Value, uid, out var controlFence))
            {
                Log.Error($"Deep cryostorage could not revoke control from {ToPrettyString(uid)} before durable wake; refusing publication.");
                return;
            }

            var claim = await _deepCryo.ClaimRestoreAsync(userId.Value);
            if (claim.Handle is not { } handle)
                return;
            if (handle.Body != uid)
            {
                await _deepCryo.AbortRestoreAsync(handle, "upstream-live-body-mismatch");
                return;
            }

            if (comp.Cryostorage is not { } reservedCryo ||
                !Exists(reservedCryo) ||
                !TryComp<CryostorageComponent>(reservedCryo, out _))
            {
                await _deepCryo.AbortRestoreAsync(handle, "upstream-original-cryo-missing");
                return;
            }

            EnsureComp<LuaMDeepCryoRestoreReservationComponent>(reservedCryo).LeaseId = handle.LeaseId;
            var consume = await _deepCryo.ConsumeRestoreAsync(handle, FinalizeDetachedTerminal);
            if (consume.Status == LuaMDeepCryoConsumeStatus.Busy)
            {
                Log.Warning($"Ignored duplicate deep cryostorage consume for {ToPrettyString(uid)}; the original publication owner retains its receipt and reservation.");
                return;
            }
            if (consume.Status == LuaMDeepCryoConsumeStatus.Failed)
            {
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                return;
            }
            if (!_deepCryo.TryResolvePublicationReceipt(handle, consume.Receipt, out var receipt))
            {
                Log.Error($"Deep cryostorage consume for {ToPrettyString(uid)} returned {consume.Status} without its internally retained publication receipt; retaining its body, reservation, and fence.");
                return;
            }
            if (consume.Status == LuaMDeepCryoConsumeStatus.Indeterminate)
            {
                if (!await _deepCryo.RollbackUnpublishedRestoreAsync(
                        receipt,
                        "upstream-complete-outcome-indeterminate",
                        () => ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId)))
                    Log.Error($"Deep cryostorage completion for {ToPrettyString(uid)} remains indeterminate; body and reservation stay unpublished.");

                return;
            }

            var published = false;
            var authorized = false;
            var terminalResolved = false;
            var attachmentConfirmed = false;
            var publicationFailure = "upstream-publication-failed-before-control";
            try
            {
                if (!Exists(uid))
                {
                    publicationFailure = "upstream-body-missing-after-consume";
                    return;
                }

                if (!IsUpstreamWakeControlFenced(userId.Value, uid, controlFence))
                {
                    publicationFailure = "upstream-control-fence-lost-before-authorization";
                    return;
                }

                var authorization = await _deepCryo.AuthorizeRestorePublicationAsync(receipt);
                if (authorization != LuaMDeepCryoAuthorizationStatus.Authorized)
                {
                    terminalResolved = authorization == LuaMDeepCryoAuthorizationStatus.Terminal;
                    publicationFailure = "upstream-authorization-rejected-before-exposure";
                    return;
                }

                authorized = true;
                if (!_deepCryo.IsRestorePublicationGenerationCurrent(receipt))
                {
                    publicationFailure = "upstream-round-ended-after-authorization";
                    return;
                }

                // The active restore still fences fresh spawn. Remove the pod
                // reservation immediately before the synchronous insertion so
                // the reservation's own insert guard permits this body.
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                if (comp.Cryostorage is not { } currentCryo ||
                    currentCryo != reservedCryo ||
                    TerminatingOrDeleted(currentCryo) ||
                    !TryComp<CryostorageComponent>(currentCryo, out _))
                {
                    publicationFailure = "upstream-cryo-missing-after-consume";
                    return;
                }

                published = FinalizeCryostorageReconnection(
                    entity,
                    deleteOnMissing: false,
                    requireExactContainment: true);
                if (!published)
                {
                    publicationFailure = "upstream-cryo-containment-unconfirmed-after-authorization";
                    if (!_deepCryo.FenceRestorePublicationBody(handle))
                        Log.Error($"Deep cryostorage could not move failed AUTH containment for {ToPrettyString(uid)} into its strict nullspace fence.");
                }
                else
                {
                    Mind.ControlMob(userId.Value, uid);
                    attachmentConfirmed = IsUpstreamPublicationAttachmentConfirmed(userId.Value, uid);
                }
            }
            catch (Exception e)
            {
                publicationFailure = "upstream-publication-exception-before-completion";
                Log.Error($"Deep cryostorage publication failed for {ToPrettyString(uid)}: {e}");
            }
            finally
            {
                if (!terminalResolved && !_deepCryo.IsRestorePublicationGenerationCurrent(receipt))
                    terminalResolved = true;

                if (terminalResolved)
                {
                    // Round cleanup already resolved the exact rollback or
                    // authorized quarantine and invoked its fence callback.
                }
                else if (published && attachmentConfirmed)
                {
                    // The player is attached to this live body already. Keep the
                    // cryostorage physically closed until the exact durable ACK.
                    if (Exists(reservedCryo))
                        EnsureComp<LuaMDeepCryoRestoreReservationComponent>(reservedCryo).LeaseId = handle.LeaseId;
                    if (!await _deepCryo.AcknowledgeRestorePublicationAsync(
                            receipt,
                            () => ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId)))
                    {
                        Log.Error($"Deep cryostorage ACK for {ToPrettyString(uid)} is pending; body remains physically fenced.");
                    }
                }
                else if (authorized)
                {
                    // AUTH is the durable point of no return. A failed insertion
                    // or ambiguous attachment may never reopen this payload.
                    if (Exists(reservedCryo))
                        EnsureComp<LuaMDeepCryoRestoreReservationComponent>(reservedCryo).LeaseId = handle.LeaseId;

                    if (!await _deepCryo.QuarantineAuthorizedPublicationAsync(
                            receipt,
                            "upstream-authorized-publication-unconfirmed",
                            FinalizeAuthorizedFailure))
                    {
                        Log.Error($"Deep cryostorage authorized failure quarantine for {ToPrettyString(uid)} is pending; body remains physically fenced.");
                    }
                }
                else if (!TryRestoreUnpublishedCryostorageBody(
                             uid,
                             comp,
                             reservedCryo,
                             unpublishedCoordinates,
                             unpublishedGracePeriod))
                {
                    // The body may have reached the live world before the fault.
                    // Retain the durable fence instead of reopening a second copy.
                    if (Exists(reservedCryo))
                        EnsureComp<LuaMDeepCryoRestoreReservationComponent>(reservedCryo).LeaseId = handle.LeaseId;
                    Log.Error($"Deep cryostorage could not prove {ToPrettyString(uid)} unpublished after a publication fault; retaining its body, reservation, and fence.");
                }
                else if (!await _deepCryo.RollbackUnpublishedRestoreAsync(
                             receipt,
                             publicationFailure,
                             () => ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId)))
                {
                    if (Exists(reservedCryo))
                        EnsureComp<LuaMDeepCryoRestoreReservationComponent>(reservedCryo).LeaseId = handle.LeaseId;
                    Log.Error($"Deep cryostorage rollback for {ToPrettyString(uid)} is unresolved; retaining the unpublished body and publication fence.");
                }
            }

            return;

            void FinalizeAuthorizedFailure()
            {
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                if (Exists(uid))
                    QueueDel(uid);
            }

            void FinalizeDetachedTerminal()
            {
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                if (_deepCryo.IsExactRestoreBody(handle))
                    QueueDel(uid);
            }
        }

        FinalizeCryostorageReconnection(entity);
    }

    private bool TryFenceUpstreamStoreControl(
        EntityUid body,
        PendingUpstreamStoreControlFence state)
    {
        _playerManager.TryGetSessionById(state.UserId, out var session);
        if (!Mind.TryGetMind(state.UserId, out var mind))
        {
            if (session?.AttachedEntity == body)
            {
                state.ReturnBodyOnFailure = true;
                _playerManager.SetAttachedEntity(session, null);
            }

            return IsUpstreamStoreControlFenced(state.UserId, body);
        }

        var current = mind.Value.Comp.CurrentEntity;
        if (current != body)
        {
            if (session?.AttachedEntity == body)
            {
                state.ReturnBodyOnFailure = true;
                _playerManager.SetAttachedEntity(session, current);
            }

            if (current is not { } visitor || !TryComp<GhostComponent>(visitor, out var ghost))
                return false;

            if (state.ReturnGhost == null)
            {
                state.ReturnGhost = visitor;
                state.PreviousCanReturn = ghost.CanReturnToBody;
            }

            _ghostSystem.SetCanReturnToBody(visitor, false, ghost);

            return IsUpstreamStoreControlFenced(state.UserId, body);
        }

        state.ReturnBodyOnFailure = true;
        _ghostSystem.SpawnGhost(
            (mind.Value.Owner, mind.Value.Comp),
            spawnPosition: null,
            canReturn: false);
        return IsUpstreamStoreControlFenced(state.UserId, body);
    }

    private bool IsUpstreamStoreControlFenced(NetUserId userId, EntityUid body)
    {
        if (Mind.TryGetMind(userId, out var mind) && mind.Value.Comp.CurrentEntity == body)
            return false;

        return !_playerManager.TryGetSessionById(userId, out var session) ||
               session.AttachedEntity != body;
    }

    private void RestoreUpstreamStoreControl(EntityUid body)
    {
        if (!_pendingStoreControlFences.Remove(body, out var state))
            return;

        if (state.ReturnBodyOnFailure && Exists(body))
        {
            Mind.ControlMob(state.UserId, body);
            return;
        }

        if (state.ReturnGhost is { } returnGhost &&
            Exists(returnGhost) &&
            TryComp<GhostComponent>(returnGhost, out var ghost))
        {
            _ghostSystem.SetCanReturnToBody(returnGhost, state.PreviousCanReturn, ghost);
        }
    }

    private bool FinishUpstreamStoreControlFence(EntityUid body)
    {
        if (!_pendingStoreControlFences.TryGetValue(body, out var state))
            return true;

        if (Mind.TryGetMind(state.UserId, out var mind) &&
            mind.Value.Comp.CurrentEntity is { } controlGhost)
        {
            if (!Exists(controlGhost) || !TryComp<GhostComponent>(controlGhost, out var ghost))
            {
                return false;
            }

            _ghostSystem.SetCanReturnToBody(controlGhost, false, ghost);
            if (mind.Value.Comp.OwnedEntity == body && mind.Value.Comp.VisitingEntity == controlGhost)
            {
                Mind.TransferTo(mind.Value.Owner, controlGhost, mind: mind.Value.Comp);
            }

            if (mind.Value.Comp.OwnedEntity != controlGhost ||
                mind.Value.Comp.CurrentEntity != controlGhost ||
                mind.Value.Comp.VisitingEntity != null)
            {
                return false;
            }
        }
        else if (Mind.TryGetMind(state.UserId, out var unresolvedMind) &&
                 unresolvedMind.Value.Comp.CurrentEntity == body)
        {
            return false;
        }

        if (!IsUpstreamStoreControlFenced(state.UserId, body))
            return false;

        _pendingStoreControlFences.Remove(body);
        return true;
    }

    private bool TryFenceUpstreamWakeControl(NetUserId userId, EntityUid body, out EntityUid controlFence)
    {
        controlFence = EntityUid.Invalid;
        if (!Mind.TryGetMind(userId, out var mind) ||
            !_playerManager.TryGetSessionById(userId, out var session))
        {
            return false;
        }

        if (mind.Value.Comp.OwnedEntity != body ||
            mind.Value.Comp.CurrentEntity != body ||
            session.AttachedEntity != body)
        {
            if (mind.Value.Comp.OwnedEntity is { } owned &&
                mind.Value.Comp.CurrentEntity is { } current &&
                owned == current &&
                current != body &&
                session.AttachedEntity == current &&
                Exists(current))
            {
                controlFence = current;
                return true;
            }

            return false;
        }

        var ghost = _ghostSystem.SpawnGhost(
            (mind.Value.Owner, mind.Value.Comp),
            spawnPosition: null,
            canReturn: false);
        if (ghost is not { } isolated)
            return false;

        controlFence = isolated;
        return IsUpstreamWakeControlFenced(userId, body, controlFence);
    }

    private bool IsUpstreamWakeControlFenced(NetUserId userId, EntityUid body, EntityUid controlFence)
    {
        return controlFence.Valid &&
               Exists(controlFence) &&
               Mind.TryGetMind(userId, out var mind) &&
               mind.Value.Comp.OwnedEntity == controlFence &&
               mind.Value.Comp.CurrentEntity == controlFence &&
               mind.Value.Comp.OwnedEntity != body &&
               mind.Value.Comp.CurrentEntity != body &&
               _playerManager.TryGetSessionById(userId, out var session) &&
               session.AttachedEntity == controlFence &&
               session.AttachedEntity != body;
    }

    private bool IsUpstreamPublicationAttachmentConfirmed(NetUserId userId, EntityUid body)
    {
        return Mind.TryGetMind(userId, out var mind) &&
               mind.Value.Comp.OwnedEntity == body &&
               mind.Value.Comp.CurrentEntity == body &&
               _playerManager.TryGetSessionById(userId, out var session) &&
               session.AttachedEntity == body;
    }

    private bool FinalizeCryostorageReconnection(
        Entity<CryostorageContainedComponent> entity,
        bool deleteOnMissing = true,
        bool requireExactContainment = false)
    {
        var (uid, comp) = entity;

        // how did you destroy these? they're indestructible.
        if (comp.Cryostorage is not { } cryostorage ||
            TerminatingOrDeleted(cryostorage) ||
            !TryComp<CryostorageComponent>(cryostorage, out var cryostorageComponent))
        {
            if (deleteOnMissing)
                QueueDel(entity);
            return false;
        }

        var cryoXform = Transform(cryostorage);
        _transform.SetParent(uid, cryoXform.ParentUid);
        _transform.SetCoordinates(uid, cryoXform.Coordinates);
        var inserted = _container.TryGetContainer(
                           cryostorage,
                           cryostorageComponent.ContainerId,
                           out var container) &&
                       _container.Insert(uid, container, cryoXform);
        if (requireExactContainment &&
            (!inserted ||
             !_container.TryGetContainingContainer((uid, null, null), out var containing) ||
             containing.Owner != cryostorage ||
             containing.ID != cryostorageComponent.ContainerId))
        {
            return false;
        }

        if (!inserted)
        {
            _climb.ForciblySetClimbing(uid, cryostorage);
        }

        comp.GracePeriodEndTime = null;
        cryostorageComponent.StoredPlayers.Remove(uid);
        AdminLog.Add(LogType.Action, LogImpact.High, $"{ToPrettyString(entity):player} re-entered the game from cryostorage {ToPrettyString(cryostorage)}");
        UpdateCryostorageUIState((cryostorage, cryostorageComponent));
        return true;
    }

    private bool TryRestoreUnpublishedCryostorageBody(
        EntityUid uid,
        CryostorageContainedComponent contained,
        EntityUid cryostorage,
        EntityCoordinates originalCoordinates,
        TimeSpan? originalGracePeriod)
    {
        if (!Exists(uid))
            return true;

        try
        {
            if (_container.TryGetContainingContainer((uid, null, null), out var currentContainer))
                _container.Remove(uid, currentContainer, reparent: false, force: true);

            _transform.SetCoordinates(uid, originalCoordinates);
            if (!IsInPausedMap(uid) || Transform(uid).Coordinates.EntityId != originalCoordinates.EntityId)
                return false;

            contained.GracePeriodEndTime = originalGracePeriod;
            Dirty(uid, contained);
            if (TryComp<CryostorageComponent>(cryostorage, out var cryostorageComponent))
            {
                cryostorageComponent.StoredPlayers.Add(uid);
                UpdateCryostorageUIState((cryostorage, cryostorageComponent));
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to restore unpublished deep cryostorage body {ToPrettyString(uid)} to its paused location: {e}");
            return false;
        }
    }

    private void ClearUpstreamRestoreReservation(EntityUid pod, Guid leaseId)
    {
        if (Exists(pod) && TryComp<LuaMDeepCryoRestoreReservationComponent>(pod, out var reservation) &&
            reservation.LeaseId == leaseId)
        {
            RemComp<LuaMDeepCryoRestoreReservationComponent>(pod);
        }
    }

    protected override void OnInsertedContainer(Entity<CryostorageComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        var (uid, comp) = ent;
        if (args.Container.ID != comp.ContainerId)
            return;

        base.OnInsertedContainer(ent, ref args);

        var locKey = CryoSleepRejoiningEnabled
            ? "cryostorage-insert-message-temp"
            : "cryostorage-insert-message-permanent";

        var msg = Loc.GetString(locKey, ("time", comp.GracePeriod.TotalMinutes));
        if (TryComp<ActorComponent>(args.Entity, out var actor))
            _chatManager.ChatMessageToOne(ChatChannel.Server, msg, msg, uid, false, actor.PlayerSession.Channel);
    }

    private List<CryostorageContainedPlayerData> GetAllContainedData(Entity<CryostorageComponent> ent)
    {
        var data = new List<CryostorageContainedPlayerData>();
        data.EnsureCapacity(ent.Comp.StoredPlayers.Count);

        foreach (var contained in ent.Comp.StoredPlayers)
        {
            data.Add(GetContainedData(contained));
        }

        return data;
    }

    private CryostorageContainedPlayerData GetContainedData(EntityUid uid)
    {
        var data = new CryostorageContainedPlayerData();
        data.PlayerName = Name(uid);
        data.PlayerEnt = GetNetEntity(uid);

        var enumerator = _inventory.GetSlotEnumerator(uid);
        while (enumerator.NextItem(out var item, out var slotDef))
        {
            data.ItemSlots.Add(slotDef.Name, Name(item));
        }

        foreach (var hand in _hands.EnumerateHands(uid))
        {
            if (hand.HeldEntity == null)
                continue;

            data.HeldItems.Add(hand.Name, Name(hand.HeldEntity.Value));
        }

        return data;
    }

    private sealed class PendingUpstreamStoreControlFence
    {
        public NetUserId UserId { get; }
        public bool ReturnBodyOnFailure { get; set; }
        public EntityUid? ReturnGhost { get; set; }
        public bool PreviousCanReturn { get; set; }

        public PendingUpstreamStoreControlFence(NetUserId userId)
        {
            UserId = userId;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<CryostorageContainedComponent>();
        while (query.MoveNext(out var uid, out var containedComp))
        {
            if (_deepCryo.IsPersistentBody(uid))
                continue;

            if (containedComp.GracePeriodEndTime == null)
                continue;

            if (Timing.CurTime < containedComp.GracePeriodEndTime)
                continue;

            Mind.TryGetMind(uid, out _, out var mindComp);
            var id = mindComp?.UserId ?? containedComp.UserId;
            HandleEnterCryostorage((uid, containedComp), id);
        }
    }
}
