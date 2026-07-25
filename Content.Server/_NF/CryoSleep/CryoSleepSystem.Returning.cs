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
        var retainedEpisodePod = EntityUid.Invalid;
        var retainedEpisodeRevision = 0L;
        if (_storedBodies.TryGetValue(id.Value, out var retainedEpisode) &&
            retainedEpisode is { } episode &&
            episode.Body == body &&
            episode.SnapshotId == handle.SnapshotId)
        {
            retainedEpisodePod = episode.Cryopod;
            retainedEpisodeRevision = episode.Revision;
        }

        if (!Exists(body))
        {
            await _deepCryo.AbortRestoreAsync(handle, "claimed-body-missing");
            return ReturnToBodyStatus.BodyMissing;
        }
        var unpublishedCoordinates = Transform(body).Coordinates;

        if (!TryReserveRestorePod(id.Value, body, handle.LeaseId, out var cryopod, out var cryoComp))
        {
            await _deepCryo.AbortRestoreAsync(handle, "no-safe-restore-pod");
            return ReturnToBodyStatus.NoCryopodAvailable;
        }

        // The reservation prevents another player entering this pod while the
        // durable CAS transition is in flight.
        var consume = await _deepCryo.ConsumeRestoreAsync(handle, FinalizeDetachedTerminal);
        if (consume.Status == LuaMDeepCryoConsumeStatus.Busy)
        {
            Log.Warning($"Ignored duplicate deep-cryo consume for {id.Value}; the original publication owner retains its receipt and reservation.");
            return ReturnToBodyStatus.BodyMissing;
        }
        if (consume.Status == LuaMDeepCryoConsumeStatus.Failed)
        {
            ClearRestoreReservation(cryopod, handle.LeaseId);
            return ReturnToBodyStatus.BodyMissing;
        }
        if (!_deepCryo.TryResolvePublicationReceipt(handle, consume.Receipt, out var receipt))
        {
            Log.Error($"Deep-cryo consume for {id.Value} returned {consume.Status} without its internally retained publication receipt; retaining its body, reservation, and fence.");
            return ReturnToBodyStatus.BodyMissing;
        }
        if (consume.Status == LuaMDeepCryoConsumeStatus.Indeterminate)
        {
            if (!await _deepCryo.RollbackUnpublishedRestoreAsync(
                    receipt,
                    "frontier-complete-outcome-indeterminate",
                    RestoreUnpublishedWorldState))
                Log.Error($"Deep-cryo completion for {id.Value} remains indeterminate; body and reservation stay unpublished.");

            return ReturnToBodyStatus.BodyMissing;
        }

        var acknowledged = false;
        var authorized = false;
        var terminalResolved = false;
        var controlConfirmed = false;
        var publicationFailure = "frontier-publication-failed-before-control";
        try
        {
            if (!Exists(body) || !Exists(cryopod) ||
                !TryComp<LuaMDeepCryoRestoreReservationComponent>(cryopod, out var reservation) ||
                reservation.LeaseId != handle.LeaseId ||
                !TryComp<CryoSleepComponent>(cryopod, out cryoComp) ||
                cryoComp.BodyContainer.ContainedEntity != null)
            {
                publicationFailure = "frontier-pod-invalid-after-consume";
                return ReturnToBodyStatus.Occupied;
            }

            // The active restore remains the player-level publication fence.
            // Drop the pod-level reservation immediately before this synchronous
            // insert so its insertion guard does not reject our own body.
            ClearRestoreReservation(cryopod, handle.LeaseId);
            if (!_container.Insert(body, cryoComp.BodyContainer))
            {
                publicationFailure = "frontier-body-insert-failed-after-consume";
                return ReturnToBodyStatus.Occupied;
            }

            // Keep the body physically closed while the durable ACK is pending.
            EnsureComp<LuaMDeepCryoRestoreReservationComponent>(cryopod).LeaseId = handle.LeaseId;

            var authorization = await _deepCryo.AuthorizeRestorePublicationAsync(receipt);
            if (authorization != LuaMDeepCryoAuthorizationStatus.Authorized)
            {
                terminalResolved = authorization == LuaMDeepCryoAuthorizationStatus.Terminal;
                publicationFailure = "frontier-authorization-rejected-before-exposure";
                return ReturnToBodyStatus.BodyMissing;
            }

            authorized = true;
            if (!_deepCryo.IsRestorePublicationGenerationCurrent(receipt))
            {
                publicationFailure = "frontier-round-ended-after-authorization";
                return ReturnToBodyStatus.BodyMissing;
            }

            try
            {
                _mind.ControlMob(id.Value, body);
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo control transfer threw for {id.Value}: {e}");
            }

            controlConfirmed = mind.OwnedEntity == body &&
                               mind.CurrentEntity == body &&
                               _player.TryGetSessionById(id.Value, out var playerSession) &&
                               playerSession.AttachedEntity == body;
            if (!controlConfirmed)
            {
                Log.Error($"Deep-cryo control transfer for {id.Value} could not be proven complete; retaining the prepared lease and physical fence.");
                return ReturnToBodyStatus.BodyMissing;
            }

            acknowledged = await _deepCryo.AcknowledgeRestorePublicationAsync(receipt, FinalizeAcknowledgedWake);
            if (!acknowledged)
            {
                Log.Error($"Deep-cryo ACK for {id.Value} is pending; body remains closed in its reserved pod.");
                return ReturnToBodyStatus.BodyMissing;
            }

            return ReturnToBodyStatus.Success;
        }
        finally
        {
            if (!terminalResolved && !_deepCryo.IsRestorePublicationGenerationCurrent(receipt))
                terminalResolved = true;

            if (terminalResolved)
            {
                // The pending AUTH was resolved by round cleanup and its exact
                // rollback/quarantine callback already handled the body fence.
            }
            else if (controlConfirmed)
            {
                if (!acknowledged && Exists(cryopod))
                    EnsureComp<LuaMDeepCryoRestoreReservationComponent>(cryopod).LeaseId = handle.LeaseId;
            }
            else if (authorized)
            {
                // AUTH is the durable point of no return. If live attachment was
                // not proven, retain the payload for operators instead of ACKing
                // a missing body or reopening it to Stored.
                if (Exists(cryopod))
                    EnsureComp<LuaMDeepCryoRestoreReservationComponent>(cryopod).LeaseId = handle.LeaseId;

                if (!await _deepCryo.QuarantineAuthorizedPublicationAsync(
                        receipt,
                        "frontier-authorized-publication-unconfirmed",
                        FinalizeAuthorizedFailure))
                {
                    Log.Error($"Deep-cryo authorized failure quarantine for {id.Value} is pending; body remains physically fenced.");
                }
            }
            else if (!await _deepCryo.RollbackUnpublishedRestoreAsync(
                         receipt,
                         publicationFailure,
                         RestoreUnpublishedWorldState))
            {
                if (Exists(cryopod))
                    EnsureComp<LuaMDeepCryoRestoreReservationComponent>(cryopod).LeaseId = handle.LeaseId;
                Log.Error($"Deep-cryo rollback for {id.Value} is unresolved; retaining the unpublished body and publication fence.");
            }
        }

        void RestoreUnpublishedWorldState()
        {
            if (!handle.Deserialized && Exists(body))
            {
                if (_container.TryGetContainingContainer((body, null, null), out var currentContainer))
                    _container.Remove(body, currentContainer, reparent: false, force: true);
                Transform(body).Coordinates = unpublishedCoordinates;
            }

            if (retainedEpisodePod.Valid &&
                UpdateStoredBodyEpisodeRevision(
                    id.Value,
                    body,
                    retainedEpisodePod,
                    handle.SnapshotId,
                    retainedEpisodeRevision,
                    receipt.PreparedRevision + 1))
            {
                retainedEpisodeRevision = receipt.PreparedRevision + 1;
            }

            ClearRestoreReservation(cryopod, handle.LeaseId);
        }

        void FinalizeAuthorizedFailure()
        {
            ForgetRetainedEpisode();
            ClearRestoreReservation(cryopod, handle.LeaseId);
            if (Exists(body))
                QueueDel(body);
        }

        void FinalizeDetachedTerminal()
        {
            ForgetRetainedEpisode();
            ClearRestoreReservation(cryopod, handle.LeaseId);
            if (_deepCryo.IsExactRestoreBody(handle))
                QueueDel(body);
        }

        void FinalizeAcknowledgedWake()
        {
            ForgetRetainedEpisode();
            ClearRestoreReservation(cryopod, handle.LeaseId);
            if (!Exists(body) || !Exists(cryopod) || !TryComp<CryoSleepComponent>(cryopod, out var currentCryo))
                return;

            _sleeping.TryWaking(body, force: true);
            if (!EjectBody(cryopod, currentCryo, body))
            {
                Log.Error($"Restored deep-cryo body {ToPrettyString(body)} remained in {ToPrettyString(cryopod)}; forcing container removal.");
                _container.Remove(body, currentCryo.BodyContainer, force: true);
            }

            _popup.PopupEntity(Loc.GetString("cryopod-wake-up", ("entity", body)), body);
            RaiseLocalEvent(body, new CryosleepWakeUpEvent(cryopod, id), true);
            _adminLogger.Add(LogType.LateJoin, LogImpact.Medium, $"{id.Value} has returned from durable deep cryosleep!");
        }

        void ForgetRetainedEpisode()
        {
            if (retainedEpisodePod.Valid)
            {
                RemoveStoredBodyEpisode(
                    id.Value,
                    body,
                    retainedEpisodePod,
                    handle.SnapshotId,
                    retainedEpisodeRevision);
            }
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

    private bool RemoveStoredBodyEpisode(
        NetUserId id,
        EntityUid expectedBody,
        EntityUid expectedPod,
        long expectedSnapshotId,
        long expectedRevision)
    {
        if (!_storedBodies.TryGetValue(id, out var retained) ||
            retained is not { } episode ||
            episode.Body != expectedBody ||
            episode.Cryopod != expectedPod ||
            episode.SnapshotId != expectedSnapshotId ||
            episode.Revision != expectedRevision)
        {
            return false;
        }

        return _storedBodies.Remove(id);
    }

    private bool UpdateStoredBodyEpisodeRevision(
        NetUserId id,
        EntityUid expectedBody,
        EntityUid expectedPod,
        long expectedSnapshotId,
        long expectedRevision,
        long updatedRevision)
    {
        if (!_storedBodies.TryGetValue(id, out var retained) ||
            retained is not { } episode ||
            episode.Body != expectedBody ||
            episode.Cryopod != expectedPod ||
            episode.SnapshotId != expectedSnapshotId ||
            episode.Revision != expectedRevision)
        {
            return false;
        }

        episode.Revision = updatedRevision;
        _storedBodies[id] = episode;
        return true;
    }

    /// <summary>
    /// Drops only the same-process body cache. Durable state is intentionally not
    /// touched; this is used by local expiry/cleanup after a successful store.
    /// </summary>
    public void ResetCryosleepState(
        NetUserId id,
        EntityUid expectedBody,
        long expectedSnapshotId,
        long expectedRevision)
    {
        var body = _storedBodies.GetValueOrDefault(id, null);

        if (body != null &&
            body.Value.Body == expectedBody &&
            body.Value.SnapshotId == expectedSnapshotId &&
            body.Value.Revision == expectedRevision &&
            _storedBodies.Remove(id) &&
            Exists(body.Value.Body) &&
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
