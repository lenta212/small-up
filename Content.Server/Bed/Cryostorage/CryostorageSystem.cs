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

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CryostorageComponent, BeforeActivatableUIOpenEvent>(OnBeforeUIOpened);
        SubscribeLocalEvent<CryostorageComponent, CryostorageRemoveItemBuiMessage>(OnRemoveItemBuiMessage);
        SubscribeLocalEvent<CryostorageComponent, ContainerIsRemovingAttemptEvent>(OnDeepCryoRemoveAttempt);
        SubscribeLocalEvent<LuaMDeepCryoRestoreReservationComponent, ContainerIsInsertingAttemptEvent>(OnDeepCryoInsertAttempt);

        SubscribeLocalEvent<CryostorageContainedComponent, PlayerSpawnCompleteEvent>(OnPlayerSpawned);
        SubscribeLocalEvent<CryostorageContainedComponent, MindRemovedMessage>(OnMindRemoved);

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

    private void OnDeepCryoRemoveAttempt(
        Entity<CryostorageComponent> ent,
        ref ContainerIsRemovingAttemptEvent args)
    {
        if (args.Container.ID == ent.Comp.ContainerId &&
            !_durableStoreFinalizing.Contains(args.EntityUid) &&
            _deepCryo.IsStorePending(args.EntityUid))
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

        if (!_deepCryo.TryBeginStore(
                ent.Owner,
                ent.Comp.Cryostorage ?? EntityUid.Invalid,
                userId,
                LuaMDeepCryoSource.UpstreamCryostorage,
                completion =>
                {
                    if (!completion.Success)
                    {
                        Log.Error($"Deep cryostorage store failed for {ToPrettyString(ent.Owner)}: {completion.FailureReason}");
                        if (TryComp<CryostorageContainedComponent>(ent.Owner, out var retryContained))
                        {
                            retryContained.GracePeriodEndTime = Timing.CurTime + TimeSpan.FromMinutes(1);
                            Dirty(ent.Owner, retryContained);
                        }
                        return;
                    }

                    if (!Exists(ent.Owner) ||
                        !TryComp<CryostorageContainedComponent>(ent.Owner, out var current) ||
                        current.Cryostorage == null)
                    {
                        Log.Error($"Cryostorage body changed after durable store for {ent.Owner}.");
                        return;
                    }

                    FinalizeEnterCryostorage((ent.Owner, current), userId);

                    if (userId != null &&
                        _playerManager.TryGetSessionById(userId.Value, out var session) &&
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
            ent.Comp.GracePeriodEndTime = Timing.CurTime + TimeSpan.FromMinutes(1);
            Dirty(ent.Owner, ent.Comp);
        }
    }

    private void FinalizeEnterCryostorage(Entity<CryostorageContainedComponent> ent, NetUserId? userId)
    {
        var comp = ent.Comp;
        var cryostorageEnt = ent.Comp.Cryostorage;

        var station = _station.GetOwningStation(ent);
        var name = Name(ent.Owner);

        if (!TryComp<CryostorageComponent>(cryostorageEnt, out var cryostorageComponent))
            return;

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
            return;
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
            return;

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
    }

    private async void HandleCryostorageReconnection(Entity<CryostorageContainedComponent> entity)
    {
        var (uid, comp) = entity;
        if (!CryoSleepRejoiningEnabled || !IsInPausedMap(uid))
            return;

        if (_deepCryo.IsStorePending(uid))
            return;

        if (_deepCryo.IsPersistentBody(uid))
        {
            var userId = comp.UserId;
            if (userId == null && _deepCryo.TryGetIdentity(uid, out var identityUser))
                userId = identityUser;
            if (userId == null)
                return;

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
            if (!await _deepCryo.ConsumeRestoreAsync(handle))
            {
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                return;
            }

            var published = false;
            try
            {
                if (!Exists(uid))
                    return;

                // The active restore still fences fresh spawn. Remove the pod
                // reservation immediately before the synchronous insertion so
                // the reservation's own insert guard permits this body.
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                published = FinalizeCryostorageReconnection(entity);
            }
            finally
            {
                if (!published && Exists(uid))
                    QueueDel(uid);
                ClearUpstreamRestoreReservation(reservedCryo, handle.LeaseId);
                _deepCryo.FinishRestorePublication(handle);
            }

            return;
        }

        FinalizeCryostorageReconnection(entity);
    }

    private bool FinalizeCryostorageReconnection(Entity<CryostorageContainedComponent> entity)
    {
        var (uid, comp) = entity;

        // how did you destroy these? they're indestructible.
        if (comp.Cryostorage is not { } cryostorage ||
            TerminatingOrDeleted(cryostorage) ||
            !TryComp<CryostorageComponent>(cryostorage, out var cryostorageComponent))
        {
            QueueDel(entity);
            return false;
        }

        var cryoXform = Transform(cryostorage);
        _transform.SetParent(uid, cryoXform.ParentUid);
        _transform.SetCoordinates(uid, cryoXform.Coordinates);
        if (!_container.TryGetContainer(cryostorage, cryostorageComponent.ContainerId, out var container) ||
            !_container.Insert(uid, container, cryoXform))
        {
            _climb.ForciblySetClimbing(uid, cryostorage);
        }

        comp.GracePeriodEndTime = null;
        cryostorageComponent.StoredPlayers.Remove(uid);
        AdminLog.Add(LogType.Action, LogImpact.High, $"{ToPrettyString(entity):player} re-entered the game from cryostorage {ToPrettyString(cryostorage)}");
        UpdateCryostorageUIState((cryostorage, cryostorageComponent));
        return true;
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
