using Content.Server.Administration.Logs;
using System.Threading.Tasks;
using Content.Server._LuaM.Cryo;
using Content.Shared.Bed.Sleep;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared._NF.CCVar;
using Content.Shared.Players;
using Robust.Shared.Network;

namespace Content.Server._NF.CryoSleep;

public sealed partial class CryoSleepSystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private SleepingSystem _sleeping = default!;

    private void InitReturning()
    {
        SubscribeNetworkEvent<WakeupRequestMessage>(OnWakeupMessage);
        SubscribeNetworkEvent<GetStatusMessage>(OnGetStatusMessage);

        // PlayerJoinedLobby is deliberately not destructive. A durable snapshot
        // survives lobby transitions and round restarts. Ordinary character spawn
        // is fenced/discarded by LuaMDeepCryoPersistenceSystem instead.
    }

    private async void OnWakeupMessage(WakeupRequestMessage message, EntitySessionEventArgs session)
    {
        var entity = session.SenderSession.GetMind();
        var result = entity == null || !TryComp<MindComponent>(entity, out var mind)
            ? ReturnToBodyStatus.NotAGhost
            : await TryReturnToBody(mind);

        var msg = new WakeupRequestMessage.Response(result);
        RaiseNetworkEvent(msg, session.SenderSession);
    }

    public async void OnGetStatusMessage(GetStatusMessage message, EntitySessionEventArgs args)
    {
        var hasBody = await _deepCryo.HasStoredSnapshotAsync(args.SenderSession.UserId);
        var msg = new GetStatusMessage.Response(hasBody);
        RaiseNetworkEvent(msg, args.SenderSession);
    }

    /// <summary>
    /// Claims and consumes a profile-bound snapshot before exposing its body or
    /// inventory to the live map. The mind must possess a ghost unless forced.
    /// </summary>
    public async Task<ReturnToBodyStatus> TryReturnToBody(MindComponent mind, bool force = false)
    {
        if (!_configurationManager.GetCVar(NFCCVars.CryoReturnEnabled))
            return ReturnToBodyStatus.Disabled;

        var id = mind.UserId;
        if (id == null)
            return ReturnToBodyStatus.BodyMissing;

        if (!force && (mind.CurrentEntity is not { Valid: true } ghost || !HasComp<GhostComponent>(ghost)))
            return ReturnToBodyStatus.NotAGhost;

        var claim = await _deepCryo.ClaimRestoreAsync(id.Value);
        if (claim.Handle is not { } handle)
        {
            return claim.Status switch
            {
                LuaMDeepCryoClaimStatus.Busy => ReturnToBodyStatus.Occupied,
                LuaMDeepCryoClaimStatus.DatabaseFailure => ReturnToBodyStatus.BodyMissing,
                LuaMDeepCryoClaimStatus.Quarantined => ReturnToBodyStatus.BodyMissing,
                _ => ReturnToBodyStatus.BodyMissing,
            };
        }

        var body = handle.Body;
        if (!Exists(body))
        {
            await _deepCryo.AbortRestoreAsync(handle, "claimed-body-missing");
            return ReturnToBodyStatus.BodyMissing;
        }

        if (!TryReserveRestorePod(id.Value, body, handle.LeaseId, out var cryopod, out var cryoComp))
        {
            await _deepCryo.AbortRestoreAsync(handle, "no-safe-restore-pod");
            return ReturnToBodyStatus.NoCryopodAvailable;
        }

        // The reservation prevents another player entering this pod while the
        // durable CAS transition is in flight.
        if (!await _deepCryo.ConsumeRestoreAsync(handle))
        {
            ClearRestoreReservation(cryopod, handle.LeaseId);
            return ReturnToBodyStatus.BodyMissing;
        }

        var published = false;
        try
        {
            if (!Exists(body) || !Exists(cryopod) ||
                !TryComp<LuaMDeepCryoRestoreReservationComponent>(cryopod, out var reservation) ||
                reservation.LeaseId != handle.LeaseId ||
                !TryComp<CryoSleepComponent>(cryopod, out cryoComp) ||
                cryoComp.BodyContainer.ContainedEntity != null)
            {
                return ReturnToBodyStatus.Occupied;
            }

            // The active restore remains the player-level publication fence.
            // Drop the pod-level reservation immediately before this synchronous
            // insert so its insertion guard does not reject our own body.
            ClearRestoreReservation(cryopod, handle.LeaseId);
            if (!_container.Insert(body, cryoComp.BodyContainer))
                return ReturnToBodyStatus.Occupied;

            _storedBodies.Remove(id.Value);
            _mind.ControlMob(id.Value, body);
            published = true;

            // Returning means the player explicitly chose to wake up. Clear the
            // restored sleep state (and its action) and place them outside the pod.
            _sleeping.TryWaking(body, force: true);
            if (!EjectBody(cryopod, cryoComp, body))
            {
                Log.Error($"Restored deep-cryo body {ToPrettyString(body)} remained in {ToPrettyString(cryopod)}; forcing container removal.");
                _container.Remove(body, cryoComp.BodyContainer, force: true);
            }

            _popup.PopupEntity(Loc.GetString("cryopod-wake-up", ("entity", body)), body);
            RaiseLocalEvent(body, new CryosleepWakeUpEvent(cryopod, id), true);
            _adminLogger.Add(LogType.LateJoin, LogImpact.Medium, $"{id.Value} has returned from durable deep cryosleep!");
            return ReturnToBodyStatus.Success;
        }
        finally
        {
            if (!published && Exists(body))
                QueueDel(body);
            ClearRestoreReservation(cryopod, handle.LeaseId);
            _deepCryo.FinishRestorePublication(handle);
        }
    }

    private bool TryReserveRestorePod(
        NetUserId userId,
        EntityUid body,
        Guid leaseId,
        out EntityUid cryopod,
        out CryoSleepComponent cryoComp)
    {
        cryopod = EntityUid.Invalid;
        cryoComp = default!;

        // Same-round return prefers the original pod, provided this exact live
        // body is the one linked to it and the pod is still empty.
        if (_storedBodies.TryGetValue(userId, out var storedBody) &&
            storedBody is { } stored && stored.Body == body &&
            Exists(stored.Cryopod) &&
            TryComp<CryoSleepComponent>(stored.Cryopod, out var original) &&
            original.BodyContainer.ContainedEntity == null &&
            !HasComp<LuaMDeepCryoRestoreReservationComponent>(stored.Cryopod))
        {
            cryopod = stored.Cryopod;
            cryoComp = original;
            EnsureComp<LuaMDeepCryoRestoreReservationComponent>(cryopod).LeaseId = leaseId;
            return true;
        }

        var fallbackQuery = EntityQueryEnumerator<CryoSleepFallbackComponent, CryoSleepComponent>();
        while (fallbackQuery.MoveNext(out var candidate, out _, out var candidateCryo))
        {
            if (candidateCryo.BodyContainer.ContainedEntity != null ||
                HasComp<LuaMDeepCryoRestoreReservationComponent>(candidate))
                continue;

            cryopod = candidate;
            cryoComp = candidateCryo;
            EnsureComp<LuaMDeepCryoRestoreReservationComponent>(cryopod).LeaseId = leaseId;
            return true;
        }

        return false;
    }

    private void ClearRestoreReservation(EntityUid pod, Guid leaseId)
    {
        if (Exists(pod) && TryComp<LuaMDeepCryoRestoreReservationComponent>(pod, out var reservation) &&
            reservation.LeaseId == leaseId)
        {
            RemComp<LuaMDeepCryoRestoreReservationComponent>(pod);
        }
    }

    /// <summary>
    /// Drops only the same-process body cache. Durable state is intentionally not
    /// touched; this is used by local expiry/cleanup after a successful store.
    /// </summary>
    public void ResetCryosleepState(NetUserId id)
    {
        var body = _storedBodies.GetValueOrDefault(id, null);

        if (body != null && _storedBodies.Remove(id) && Exists(body.Value.Body) &&
            Transform(body.Value.Body).ParentUid == _storageMap)
        {
            QueueDel(body.Value.Body);
        }
    }

    public bool HasCryosleepingBody(NetUserId id)
    {
        return _storedBodies.ContainsKey(id);
    }
}
