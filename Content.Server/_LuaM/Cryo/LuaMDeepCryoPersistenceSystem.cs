using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Antag.Components;
using Content.Server.Database;
using Content.Server.Dragon;
using Content.Server.GameTicking;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Humanoid;
using Content.Server.IdentityManagement;
using Content.Server.KillTracking;
using Content.Server.PDA.Ringer;
using Content.Server.Preferences.Managers;
using Content.Server.RandomMetadata;
using Content.Server.Revolutionary.Components;
using Content.Server.StationRecords;
using Content.Server.Traitor.Components;
using Content.Server.Traitor.Uplink;
using Content.Server.Zombies;
using Content.Server._DV.Mail.Components;
using Content.Server._NF.CryoSleep;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Actions;
using Content.Shared.Antag;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Organ;
using Content.Shared.Clothing.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.DetailExaminable;
using Content.Shared.DoAfter;
using Content.Shared.GameTicking;
using Content.Shared.Ghost.Roles.Components;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.IdentityManagement.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Ninja.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.NukeOps;
using Content.Shared.Preferences;
using Content.Shared.RatKing;
using Content.Shared.Revolutionary.Components;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.StatusIcon;
using Content.Shared.Store.Components;
using Content.Shared.Zombies;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared;

namespace Content.Server._LuaM.Cryo;

/// <summary>
/// Durable, profile-bound deep-cryo coordinator shared by both cryo implementations.
/// The database owns lifecycle/CAS state; entity markers are only main-thread fences.
/// </summary>
public sealed class LuaMDeepCryoPersistenceSystem : EntitySystem
{
    public const int PayloadFormatVersion = 1;

    private static readonly TimeSpan RestoreLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ForbiddenPersistedEntityPrototypes = new(StringComparer.Ordinal)
    {
        "ActionOpenUplinkImplant",
        "ActionTurnUndead",
        "UplinkImplant",
    };

    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private ActionContainerSystem _actionContainers = default!;
    [Dependency] private SharedAccessSystem _access = default!;
    [Dependency] private SharedIdCardSystem _idCards = default!;
    [Dependency] private NpcFactionSystem _npcFactions = default!;
    [Dependency] private HumanoidAppearanceSystem _humanoidAppearance = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<CharacterKey, EntityUid> _liveBodies = new();
    private readonly Dictionary<CharacterKey, Guid> _activeRestores = new();
    private readonly HashSet<CharacterKey> _discardPending = new();
    private readonly Dictionary<string, Dictionary<string, int>> _canonicalRootActions = new(StringComparer.Ordinal);
    private readonly string _serverInstanceId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private TimeSpan _nextLeaseRecovery;
    private bool _leaseRecoveryRunning;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnPlayerBeforeSpawn);

        _nextLeaseRecovery = _timing.CurTime;
        RecoverExpiredLeases();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_leaseRecoveryRunning && _timing.CurTime >= _nextLeaseRecovery)
            RecoverExpiredLeases();
    }

    private async void RecoverExpiredLeases()
    {
        if (_leaseRecoveryRunning)
            return;

        _leaseRecoveryRunning = true;
        _nextLeaseRecovery = _timing.CurTime + TimeSpan.FromMinutes(1);
        try
        {
            var recovered = await _db.RecoverExpiredLuaMDeepCryoLeasesAsync(DateTime.UtcNow);
            if (recovered > 0)
                Log.Info($"Recovered {recovered} expired deep-cryo restore lease(s).");
        }
        catch (Exception e)
        {
            Log.Error($"Unable to recover expired deep-cryo restore leases: {e}");
        }
        finally
        {
            _leaseRecoveryRunning = false;
        }
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!TryGetCurrentCharacterKey(ev.Player.UserId, out var key))
        {
            Log.Error($"Could not bind stable deep-cryo identity to {ev.Player.UserId} after spawn.");
            return;
        }

        var identity = EnsureComp<LuaMDeepCryoIdentityComponent>(ev.Mob);
        identity.UserId = key.UserId;
        identity.ProfileId = key.ProfileId;
        identity.Slot = key.Slot;
        identity.SlotGeneration = _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot);
    }

    /// <summary>
    /// A normal character spawn is an explicit choice not to wake the stored body.
    /// This handler blocks briefly on the DB worker and fails closed so a wake cannot
    /// race a fresh body into existence. Spawn events cannot themselves be awaited.
    /// </summary>
    private void OnPlayerBeforeSpawn(PlayerBeforeSpawnEvent ev)
    {
        if (!TryGetCurrentCharacterKey(ev.Player.UserId, out var key))
            return;

        // Keep fresh-spawn and wake mutually exclusive through the entire
        // publication window. The durable row is already Consumed after
        // ConsumeRestoreAsync, so checking the database alone is not enough.
        if (IsRestorePublicationActive(key) || !_discardPending.Add(key))
        {
            ev.Handled = true;
            return;
        }

        try
        {
            var snapshot = Task.Run(() => _db.GetLuaMDeepCryoSnapshotAsync(
                    key.UserId,
                    key.ProfileId,
                    key.Slot))
                .GetAwaiter()
                .GetResult();

            if (snapshot == null)
                return;

            var result = Task.Run(() => _db.DiscardLuaMDeepCryoSnapshotAsync(
                    new LuaMDeepCryoDiscardRequest(
                        Guid.NewGuid(),
                        key.UserId,
                        key.ProfileId,
                        key.Slot,
                        snapshot.Id,
                        snapshot.Revision,
                        snapshot.LeaseId,
                        DateTime.UtcNow)))
                .GetAwaiter()
                .GetResult();

            if (result.Success || result.Status == LuaMDeepCryoWriteStatus.NotFound)
            {
                ForgetLiveBody(key, deleteBody: true);
                return;
            }

            Log.Error($"Blocked fresh spawn for {key}: durable cryo discard returned {result.Status}.");
            ev.Handled = true;
        }
        catch (Exception e)
        {
            Log.Error($"Blocked fresh spawn for {key}: durable cryo discard failed: {e}");
            ev.Handled = true;
        }
        finally
        {
            _discardPending.Remove(key);
        }
    }

    public bool IsPersistentBody(EntityUid body)
    {
        return HasComp<LuaMDeepCryoPendingComponent>(body) ||
               HasComp<LuaMDeepCryoStoredComponent>(body);
    }

    public bool IsStorePending(EntityUid body)
    {
        return HasComp<LuaMDeepCryoPendingComponent>(body);
    }

    public bool TryGetIdentity(EntityUid body, out NetUserId userId)
    {
        if (TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity))
        {
            userId = identity.UserId;
            return true;
        }

        userId = default;
        return false;
    }

    /// <summary>
    /// Starts durable storage. The caller must leave the body in <paramref name="pod"/>
    /// until completion. The callback runs on the main thread after body, pod and
    /// stable identity have been revalidated.
    /// </summary>
    public bool TryBeginStore(
        EntityUid body,
        EntityUid pod,
        NetUserId? requestedUser,
        LuaMDeepCryoSource source,
        Action<LuaMDeepCryoStoreCompletion> completion,
        out string reason)
    {
        reason = string.Empty;

        if (Deleted(body) || Deleted(pod))
        {
            reason = "body-or-pod-deleted";
            return false;
        }

        if (!IsBodyInExpectedPodContainer(body, pod, source))
        {
            reason = "body-not-contained-by-pod";
            return false;
        }

        if (HasComp<LuaMDeepCryoPendingComponent>(body))
        {
            reason = "store-already-pending";
            return false;
        }

        if (HasComp<LuaMDeepCryoStoredComponent>(body))
        {
            reason = "body-already-durably-stored";
            return false;
        }

        if (!TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity))
        {
            reason = "stable-character-identity-missing";
            return false;
        }

        if (requestedUser != null && requestedUser.Value != identity.UserId)
        {
            reason = "body-owner-mismatch";
            return false;
        }

        var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
        if (!TryValidateCanonicalBody(body, key, out _, out _, out reason))
            return false;

        if (!TryCapturePayload(body, out var payload, out reason))
            return false;

        if (!IsBodyInExpectedPodContainer(body, pod, source))
        {
            reason = "body-left-pod-during-capture";
            return false;
        }

        var pending = EnsureComp<LuaMDeepCryoPendingComponent>(body);
        pending.OperationId = Guid.NewGuid();
        pending.Pod = pod;
        pending.Source = source;

        StoreCore(body, pod, key, pending.OperationId, payload, completion);
        return true;
    }

    private async void StoreCore(
        EntityUid body,
        EntityUid pod,
        CharacterKey key,
        Guid operationId,
        LuaMDeepCryoPayload payload,
        Action<LuaMDeepCryoStoreCompletion> completion)
    {
        LuaMDeepCryoWriteResult result;
        try
        {
            var build = _configuration.GetCVar(CVars.BuildVersion);
            if (string.IsNullOrWhiteSpace(build))
                build = "unknown";
            if (build.Length > LuaMDeepCryoLimits.MaxBuildVersionLength)
                build = build[..LuaMDeepCryoLimits.MaxBuildVersionLength];

            result = await _db.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
                operationId,
                key.UserId,
                key.ProfileId,
                key.Slot,
                _gameTicker.RoundId,
                PayloadFormatVersion,
                payload.Bytes,
                payload.Hash,
                payload.Bytes.Length,
                payload.EntityCount,
                build,
                payload.PrototypeManifestHash,
                DateTime.UtcNow));
        }
        catch (Exception e)
        {
            FinishFailedStore(body, operationId, completion, $"database-exception: {e.Message}");
            return;
        }

        if (!TryRevalidatePendingBody(body, pod, key, operationId, out var invalidReason))
        {
            var durableCopyNeutralized = true;
            if (result.Success && result.SnapshotId is { } orphanId && result.Revision is { } orphanRevision)
            {
                durableCopyNeutralized = await QuarantineOrphanedStore(
                    key,
                    orphanId,
                    orphanRevision,
                    invalidReason);
            }

            // If the committed copy could not be quarantined, retaining a body
            // that already escaped its pod would permit two usable copies. Keep
            // the durable copy and fail closed on the untrusted live one.
            if (!durableCopyNeutralized && Exists(body))
                QueueDel(body);

            FinishFailedStore(body, operationId, completion, invalidReason);
            return;
        }

        if (!result.Success || result.SnapshotId is not { } snapshotId || result.Revision is not { } revision)
        {
            FinishFailedStore(body, operationId, completion, $"database-store-{result.Status}");
            return;
        }

        var stored = EnsureComp<LuaMDeepCryoStoredComponent>(body);
        stored.UserId = key.UserId;
        stored.ProfileId = key.ProfileId;
        stored.Slot = key.Slot;
        stored.SnapshotId = snapshotId;
        stored.Revision = revision;
        _liveBodies[key] = body;

        var completionSucceeded = false;
        try
        {
            completion(new LuaMDeepCryoStoreCompletion(true, snapshotId, revision, null));
            completionSucceeded = true;
        }
        catch (Exception e)
        {
            // The durable snapshot is already authoritative. If finalization
            // fails, never expose the still-live body as a second usable copy.
            // Keep its pending fence until deletion and force future restores
            // to materialize from the committed snapshot.
            Log.Error($"Deep-cryo post-store callback failed for {key}; deleting fenced live body: {e}");
            _liveBodies.Remove(key);
            if (Exists(body))
                QueueDel(body);
        }
        finally
        {
            if (completionSucceeded &&
                Exists(body) &&
                TryComp<LuaMDeepCryoPendingComponent>(body, out var current) &&
                current.OperationId == operationId)
            {
                RemComp<LuaMDeepCryoPendingComponent>(body);
            }
        }
    }

    private bool TryRevalidatePendingBody(
        EntityUid body,
        EntityUid pod,
        CharacterKey key,
        Guid operationId,
        out string reason)
    {
        reason = "body-disappeared-after-store";
        if (!Exists(body) || !Exists(pod))
            return false;

        if (!TryComp<LuaMDeepCryoPendingComponent>(body, out var pending) ||
            pending.OperationId != operationId || pending.Pod != pod)
        {
            reason = "store-fence-changed";
            return false;
        }

        if (!IsBodyInExpectedPodContainer(body, pod, pending.Source))
        {
            reason = "body-left-pod-before-store-commit";
            return false;
        }

        if (!TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity) ||
            identity.UserId != key.UserId || identity.ProfileId != key.ProfileId || identity.Slot != key.Slot)
        {
            reason = "stable-character-identity-changed";
            return false;
        }

        return true;
    }

    private bool IsBodyInExpectedPodContainer(
        EntityUid body,
        EntityUid pod,
        LuaMDeepCryoSource source)
    {
        if (!Exists(body) || !Exists(pod) || Transform(body).ParentUid != pod)
            return false;

        return source switch
        {
            LuaMDeepCryoSource.FrontierCryoSleep =>
                TryComp<CryoSleepComponent>(pod, out var frontier) && frontier.BodyContainer.Contains(body),
            LuaMDeepCryoSource.UpstreamCryostorage =>
                TryComp<CryostorageComponent>(pod, out var upstream) &&
                TryComp<CryostorageContainedComponent>(body, out var contained) &&
                contained.Cryostorage == pod &&
                _containers.TryGetContainer(pod, upstream.ContainerId, out var container) &&
                container.Contains(body),
            _ => false,
        };
    }

    private void FinishFailedStore(
        EntityUid body,
        Guid operationId,
        Action<LuaMDeepCryoStoreCompletion> completion,
        string reason)
    {
        if (Exists(body) && TryComp<LuaMDeepCryoPendingComponent>(body, out var pending) &&
            pending.OperationId == operationId)
        {
            RemComp<LuaMDeepCryoPendingComponent>(body);
        }

        completion(new LuaMDeepCryoStoreCompletion(false, null, null, reason));
    }

    private async Task<bool> QuarantineOrphanedStore(
        CharacterKey key,
        long snapshotId,
        long revision,
        string reason)
    {
        try
        {
            var result = await _db.QuarantineLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoQuarantineRequest(
                Guid.NewGuid(),
                key.UserId,
                key.ProfileId,
                key.Slot,
                snapshotId,
                revision,
                null,
                LimitReason(reason),
                DateTime.UtcNow));
            if (result.Success)
                return true;

            Log.Error($"Failed to quarantine orphaned deep-cryo store for {key}: {result.Status}");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to quarantine orphaned deep-cryo store for {key}: {e}");
        }

        return false;
    }

    public async Task<bool> HasStoredSnapshotAsync(NetUserId userId)
    {
        if (!TryGetCurrentCharacterKey(userId, out var key))
            return false;

        if (_liveBodies.TryGetValue(key, out var live) && Exists(live))
            return true;

        try
        {
            var snapshot = await _db.GetLuaMDeepCryoSnapshotAsync(key.UserId, key.ProfileId, key.Slot);
            return snapshot?.Status == DbLuaMDeepCryoSnapshotStatus.Stored;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to query deep-cryo status for {key}: {e}");
            return false;
        }
    }

    /// <summary>
    /// CAS-claims a snapshot and materializes it only in nullspace. No item or body
    /// is exposed to the world until <see cref="ConsumeRestoreAsync"/> succeeds.
    /// </summary>
    public async Task<LuaMDeepCryoClaimResult> ClaimRestoreAsync(NetUserId userId)
    {
        if (!TryGetCurrentCharacterKey(userId, out var key))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);
        if (!TryGetCanonicalProfile(key, out var profile, out var expectedPrototype, out var profileReason))
        {
            Log.Error($"Cannot resolve canonical deep-cryo profile for {key}: {profileReason}");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);
        }

        if (_discardPending.Contains(key) || _activeRestores.ContainsKey(key))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);

        LuaMDeepCryoSnapshotRecord? snapshot;
        try
        {
            if (!await _db.ValidateLuaMDeepCryoProfileAsync(key.UserId, key.ProfileId, key.Slot))
                return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);

            snapshot = await _db.GetLuaMDeepCryoSnapshotAsync(key.UserId, key.ProfileId, key.Slot);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to read deep-cryo snapshot for {key}: {e}");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.DatabaseFailure);
        }

        if (snapshot == null)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Missing);
        if (snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Quarantined);
        if (snapshot.Status != DbLuaMDeepCryoSnapshotStatus.Stored)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);

        var leaseId = Guid.NewGuid();
        if (!TryStartRestorePublication(key, leaseId))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);

        LuaMDeepCryoWriteResult claim;
        try
        {
            var now = DateTime.UtcNow;
            claim = await _db.ClaimLuaMDeepCryoRestoreAsync(new LuaMDeepCryoClaimRequest(
                Guid.NewGuid(),
                key.UserId,
                key.ProfileId,
                key.Slot,
                snapshot.Id,
                snapshot.Revision,
                _gameTicker.RoundId,
                leaseId,
                _serverInstanceId,
                now,
                now + RestoreLeaseDuration));
        }
        catch (Exception e)
        {
            _activeRestores.Remove(key);
            Log.Error($"Failed to claim deep-cryo snapshot for {key}: {e}");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.DatabaseFailure);
        }

        if (!claim.Success || claim.Revision is not { } claimedRevision)
        {
            _activeRestores.Remove(key);
            return LuaMDeepCryoClaimResult.Failed(claim.Status == LuaMDeepCryoWriteStatus.Quarantined
                ? LuaMDeepCryoClaimStatus.Quarantined
                : LuaMDeepCryoClaimStatus.Busy);
        }

        if (claim.Snapshot is not { } claimedSnapshot ||
            claimedSnapshot.Status != DbLuaMDeepCryoSnapshotStatus.Restoring ||
            claimedSnapshot.Revision != claimedRevision ||
            claimedSnapshot.LeaseId != leaseId ||
            claim.LeaseId != leaseId)
        {
            _activeRestores.Remove(key);
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        }

        if (!TryGetCurrentCharacterKey(userId, out var current) || current != key)
        {
            var invalidHandle = new LuaMDeepCryoRestoreHandle(
                key, snapshot.Id, claimedRevision, leaseId, EntityUid.Invalid, false);
            await AbortRestoreAsync(invalidHandle, "profile-changed-during-restore-claim");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);
        }

        EntityUid body;
        var deserialized = false;
        if (_liveBodies.TryGetValue(key, out var liveBody) &&
            Exists(liveBody) &&
            TryComp<LuaMDeepCryoStoredComponent>(liveBody, out var stored) &&
            stored.SnapshotId == snapshot.Id)
        {
            body = liveBody;
        }
        else
        {
            if (!TryLoadPayload(claimedSnapshot, out body, out var loadReason))
            {
                await QuarantineRestore(key, snapshot.Id, claimedRevision, leaseId, loadReason);
                _activeRestores.Remove(key);
                return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Quarantined);
            }

            deserialized = true;
        }

        if (!TryValidateCanonicalBody(
                body,
                key,
                out _,
                out _,
                out var canonicalReason) ||
            !TryApplyCanonicalAuthority(
                body,
                profile,
                expectedPrototype,
                preserveLiveAttachment: !deserialized && HasComp<CryostorageContainedComponent>(body),
                out canonicalReason))
        {
            await QuarantineRestore(key, snapshot.Id, claimedRevision, leaseId, canonicalReason);
            _activeRestores.Remove(key);
            _liveBodies.Remove(key);
            if (Exists(body))
                QueueDel(body);
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Quarantined);
        }

        BindRestoredIdentity(body, key, snapshot.Id, claimedRevision);
        var handle = new LuaMDeepCryoRestoreHandle(
            key,
            snapshot.Id,
            claimedRevision,
            leaseId,
            body,
            deserialized);
        return new LuaMDeepCryoClaimResult(LuaMDeepCryoClaimStatus.Success, handle);
    }

    /// <summary>
    /// Consumes the durable snapshot before the caller inserts the body into a pod,
    /// transfers control, or otherwise exposes its inventory to the live world.
    /// </summary>
    public async Task<bool> ConsumeRestoreAsync(LuaMDeepCryoRestoreHandle handle)
    {
        if (!TryValidateRestoreHandle(handle))
        {
            await AbortRestoreAsync(handle, "restore-handle-or-profile-invalid");
            return false;
        }

        try
        {
            var result = await _db.CompleteLuaMDeepCryoRestoreAsync(new LuaMDeepCryoCompleteRequest(
                Guid.NewGuid(),
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot,
                handle.SnapshotId,
                handle.Revision,
                handle.LeaseId,
                DateTime.UtcNow));

            if (!result.Success)
            {
                await AbortRestoreAsync(handle, $"restore-complete-{result.Status}");
                return false;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo completion outcome is unknown for {handle.Key}: {e}");
            await AbortRestoreAsync(handle, "restore-complete-unknown-outcome");
            return false;
        }

        _liveBodies.Remove(handle.Key);
        if (Exists(handle.Body))
            RemComp<LuaMDeepCryoStoredComponent>(handle.Body);
        return true;
    }

    /// <summary>
    /// Releases the wake-vs-fresh-spawn fence only after the caller has either
    /// published the restored body and transferred control or safely disposed it.
    /// </summary>
    public void FinishRestorePublication(LuaMDeepCryoRestoreHandle handle)
    {
        if (_activeRestores.TryGetValue(handle.Key, out var lease) && lease == handle.LeaseId)
            _activeRestores.Remove(handle.Key);
    }

    internal bool TryStartRestorePublication(CharacterKey key, Guid leaseId)
    {
        return _activeRestores.TryAdd(key, leaseId);
    }

    internal bool IsRestorePublicationActive(CharacterKey key)
    {
        return _activeRestores.ContainsKey(key);
    }

    public async Task AbortRestoreAsync(LuaMDeepCryoRestoreHandle handle, string reason)
    {
        try
        {
            await _db.AbortLuaMDeepCryoRestoreAsync(new LuaMDeepCryoAbortRequest(
                Guid.NewGuid(),
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot,
                handle.SnapshotId,
                handle.Revision,
                handle.LeaseId,
                LimitReason(reason),
                DateTime.UtcNow));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to abort deep-cryo restore for {handle.Key}: {e}");
        }
        finally
        {
            _activeRestores.Remove(handle.Key);
            if (handle.Deserialized && Exists(handle.Body))
                QueueDel(handle.Body);
        }
    }

    private async Task QuarantineRestore(
        CharacterKey key,
        long snapshotId,
        long revision,
        Guid leaseId,
        string reason)
    {
        try
        {
            await _db.QuarantineLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoQuarantineRequest(
                Guid.NewGuid(),
                key.UserId,
                key.ProfileId,
                key.Slot,
                snapshotId,
                revision,
                leaseId,
                LimitReason(reason),
                DateTime.UtcNow));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to quarantine corrupt deep-cryo snapshot for {key}: {e}");
        }
    }

    private bool TryValidateRestoreHandle(LuaMDeepCryoRestoreHandle handle)
    {
        if (!_activeRestores.TryGetValue(handle.Key, out var lease) || lease != handle.LeaseId)
            return false;
        if (!Exists(handle.Body))
            return false;
        if (!TryGetCurrentCharacterKey(handle.Key.UserId, out var current) || current != handle.Key)
            return false;
        return TryComp<LuaMDeepCryoIdentityComponent>(handle.Body, out var identity) &&
               identity.UserId == handle.Key.UserId &&
               identity.ProfileId == handle.Key.ProfileId &&
               identity.Slot == handle.Key.Slot;
    }

    private void BindRestoredIdentity(
        EntityUid body,
        CharacterKey key,
        long snapshotId,
        long revision)
    {
        var identity = EnsureComp<LuaMDeepCryoIdentityComponent>(body);
        identity.UserId = key.UserId;
        identity.ProfileId = key.ProfileId;
        identity.Slot = key.Slot;
        identity.SlotGeneration = _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot);

        var stored = EnsureComp<LuaMDeepCryoStoredComponent>(body);
        stored.UserId = key.UserId;
        stored.ProfileId = key.ProfileId;
        stored.Slot = key.Slot;
        stored.SnapshotId = snapshotId;
        stored.Revision = revision;
    }

    internal bool TryCapturePayload(EntityUid root, out LuaMDeepCryoPayload payload, out string reason)
    {
        payload = default;
        if (!TryDetachRegeneratedRuntimeChildren(root, out var detached, out var runtimeChildren, out reason))
            return false;

        var doAfterOwners = RemoveTransientCaptureState(root);

        void SkipRegeneratedRuntimeChildren(Entity<MetaDataComponent> ent, ref bool serializable)
        {
            if (runtimeChildren.Contains(ent.Owner))
                serializable = false;
        }

        _mapLoader.OnIsSerializable += SkipRegeneratedRuntimeChildren;
        var captured = false;
        try
        {
            captured = TryCaptureSerializablePayload(root, runtimeChildren, out payload, out reason);
        }
        finally
        {
            _mapLoader.OnIsSerializable -= SkipRegeneratedRuntimeChildren;
            if (!TryRestoreRegeneratedRuntimeChildren(detached, out var restoreReason))
            {
                payload = default;
                reason = restoreReason;
                captured = false;
            }

            if (!captured)
                RestoreDoAfterCapability(doAfterOwners);
        }

        return captured;
    }

    /// <summary>
    /// Active do-afters are round-local control flow and may reference the cryo pod
    /// or another entity outside the persisted body graph. They must never enter a
    /// durable snapshot. In particular, the cryo do-after is still present while
    /// its completion event starts the DB-first store.
    /// </summary>
    private List<EntityUid> RemoveTransientCaptureState(EntityUid root)
    {
        var doAfterOwners = new List<EntityUid>();
        var stack = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            var childEnumerator = Transform(uid).ChildEnumerator;
            while (childEnumerator.MoveNext(out var child))
                stack.Push(child);

            if (HasComp<DoAfterComponent>(uid))
                doAfterOwners.Add(uid);

            RemComp<DoAfterComponent>(uid);
            RemComp<ActiveDoAfterComponent>(uid);
        }

        return doAfterOwners;
    }

    /// <summary>
    /// A rejected snapshot must not permanently strip a living body of the
    /// component required to start later do-afters. The old operations remain
    /// cancelled because they can reference round-local entities such as the pod.
    /// </summary>
    private void RestoreDoAfterCapability(IEnumerable<EntityUid> owners)
    {
        foreach (var uid in owners)
        {
            if (Exists(uid))
                EnsureComp<DoAfterComponent>(uid);
        }
    }

    private bool TryCaptureSerializablePayload(
        EntityUid root,
        IReadOnlySet<EntityUid> runtimeChildren,
        out LuaMDeepCryoPayload payload,
        out string reason)
    {
        payload = default;
        if (!TryInspectGraph(root, out var count, out var manifest, out reason, runtimeChildren))
            return false;

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var options = SerializationOptions.Default with
        {
            Category = FileCategory.Entity,
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
            ErrorOnOrphan = false,
            LogAutoInclude = null,
        };

        // Player prototypes (including MobHuman) intentionally use `save: false`
        // for normal map saves. Deep cryo is an explicit, bounded serializer, so
        // temporarily opt only the prototypes in this synchronous graph into the
        // map serializer and restore their global flags before returning.
        var temporarilySavable = EnableDeepCryoSerialization(root);
        var saved = false;
        try
        {
            saved = _mapLoader.TrySaveEntity(root, writer, options);
        }
        finally
        {
            foreach (var prototype in temporarilySavable)
                prototype.MapSavable = false;
        }

        if (!saved)
        {
            reason = "entity-serialization-failed";
            return false;
        }

        var bytes = StrictUtf8.GetBytes(writer.ToString());
        if (bytes.Length == 0 || bytes.Length > LuaMDeepCryoLimits.MaxPayloadBytes)
        {
            reason = "snapshot-payload-size-out-of-range";
            return false;
        }

        payload = new LuaMDeepCryoPayload(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            count,
            manifest);
        return true;
    }

    internal bool TryLoadPayload(
        LuaMDeepCryoSnapshotRecord snapshot,
        out EntityUid body,
        out string reason)
    {
        body = EntityUid.Invalid;
        reason = string.Empty;

        if (snapshot.FormatVersion != PayloadFormatVersion)
        {
            reason = $"unsupported-format-{snapshot.FormatVersion}";
            return false;
        }

        if (snapshot.PayloadSizeBytes != snapshot.Payload.Length ||
            snapshot.PayloadSizeBytes is <= 0 or > LuaMDeepCryoLimits.MaxPayloadBytes ||
            snapshot.EntityCount is <= 0 or > LuaMDeepCryoLimits.MaxEntityCount)
        {
            reason = "snapshot-caps-invalid";
            return false;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(snapshot.Payload)).ToLowerInvariant();
        if (!FixedHashEquals(actualHash, snapshot.PayloadHash))
        {
            reason = "snapshot-payload-hash-mismatch";
            return false;
        }

        string yaml;
        try
        {
            yaml = StrictUtf8.GetString(snapshot.Payload);
        }
        catch (DecoderFallbackException)
        {
            reason = "snapshot-is-not-utf8";
            return false;
        }

        using var reader = new StringReader(yaml);
        if (!_mapLoader.TryLoadEntity(reader, $"deep-cryo:{snapshot.Id}", out var loaded) || loaded == null)
        {
            reason = "snapshot-deserialization-or-prototype-validation-failed";
            return false;
        }

        body = loaded.Value.Owner;
        if (!TryInspectGraph(body, out var count, out var manifest, out reason) ||
            count != snapshot.EntityCount ||
            !FixedHashEquals(manifest, snapshot.PrototypeManifestHash))
        {
            if (Exists(body))
                QueueDel(body);
            body = EntityUid.Invalid;
            if (string.IsNullOrEmpty(reason))
                reason = "snapshot-entity-graph-or-manifest-mismatch";
            return false;
        }

        SanitizeRestoredGraph(body, preserveLiveAttachment: false);
        return true;
    }

    private bool TryInspectGraph(
        EntityUid root,
        out int entityCount,
        out string prototypeManifestHash,
        out string reason,
        IReadOnlySet<EntityUid>? ignoredRuntimeChildren = null)
    {
        entityCount = 0;
        prototypeManifestHash = string.Empty;
        reason = string.Empty;

        var stack = new Stack<(EntityUid Entity, int Depth)>();
        var visited = new HashSet<EntityUid>();
        var prototypes = new List<string>();
        stack.Push((root, 0));

        while (stack.TryPop(out var current))
        {
            if (!visited.Add(current.Entity))
                continue;
            if (!Exists(current.Entity))
            {
                reason = "snapshot-graph-contains-deleted-entity";
                return false;
            }
            if (current.Entity != root &&
                (ignoredRuntimeChildren?.Contains(current.Entity) == true ||
                 IsRegeneratedRuntimeChild(current.Entity)))
                continue;
            if (current.Depth > 64)
            {
                reason = "snapshot-container-depth-exceeded";
                return false;
            }
            if (++entityCount > LuaMDeepCryoLimits.MaxEntityCount)
            {
                reason = "snapshot-entity-count-exceeded";
                return false;
            }
            if (current.Entity != root &&
                HasComp<MobStateComponent>(current.Entity) &&
                !IsInstalledNeuralCore(root, current.Entity))
            {
                reason = "snapshot-contains-additional-organic-body";
                return false;
            }
            if (current.Entity == root && TryGetForbiddenRootReason(root, out reason))
                return false;

            var prototype = MetaData(current.Entity).EntityPrototype?.ID;
            if (string.IsNullOrWhiteSpace(prototype))
            {
                reason = $"snapshot-entity-prototype-missing:{prototype ?? "<runtime>"}:{ToPrettyString(current.Entity)}";
                return false;
            }
            if (!_prototypes.HasIndex<EntityPrototype>(prototype))
            {
                reason = $"snapshot-entity-prototype-missing:{prototype}:{ToPrettyString(current.Entity)}";
                return false;
            }
            prototypes.Add(prototype);

            var children = Transform(current.Entity).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push((child, current.Depth + 1));
        }

        prototypes.Sort(StringComparer.Ordinal);
        var manifestBytes = StrictUtf8.GetBytes(string.Join('\n', prototypes));
        prototypeManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// IPCs and borgs contain a MobState-bearing positronic brain as part of their
    /// own body. Only accept it when the body system or borg chassis proves that
    /// the neural core is installed in this exact root; a brain or mob carried in
    /// inventory remains a separate passenger and is rejected above.
    /// </summary>
    private bool IsInstalledNeuralCore(EntityUid root, EntityUid nested)
    {
        if (TryComp<OrganComponent>(nested, out var organ) && organ.Body == root)
            return true;

        return TryComp<BorgChassisComponent>(root, out var chassis) &&
               chassis.BrainEntity == nested &&
               HasComp<BorgBrainComponent>(nested);
    }

    private void SanitizeRestoredGraph(EntityUid root, bool preserveLiveAttachment)
    {
        var stack = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            var children = new List<EntityUid>();
            var childEnumerator = Transform(uid).ChildEnumerator;
            while (childEnumerator.MoveNext(out var child))
                children.Add(child);

            var prototype = MetaData(uid).EntityPrototype?.ID;
            if (uid != root && prototype != null && ForbiddenPersistedEntityPrototypes.Contains(prototype))
            {
                Del(uid);
                continue;
            }

            RemComp<LuaMDeepCryoIdentityComponent>(uid);
            RemComp<LuaMDeepCryoPendingComponent>(uid);
            RemComp<LuaMDeepCryoStoredComponent>(uid);
            RemComp<PlayerJobComponent>(uid);
            RemComp<UplinkComponent>(uid);
            RemComp<RingerUplinkComponent>(uid);
            RemComp<StoreComponent>(uid);
            RemComp<FactionExceptionComponent>(uid);
            RemComp<FactionExceptionTrackerComponent>(uid);
            RemComp<DoAfterComponent>(uid);
            RemComp<ActiveDoAfterComponent>(uid);
            if (!preserveLiveAttachment || uid != root)
                RemComp<CryostorageContainedComponent>(uid);

            // Removing the old-round bookkeeping is safer than retaining a
            // serialized AlreadyMember flag. Equipment can be re-evaluated by
            // the new round without inheriting a faction grant.
            RemComp<FactionClothingComponent>(uid);

            if (HasComp<KillTrackerComponent>(uid))
            {
                RemComp<KillTrackerComponent>(uid);
                EnsureComp<KillTrackerComponent>(uid);
            }

            if (TryComp<AccessComponent>(uid, out var access))
            {
                _access.TrySetTags(uid, Array.Empty<ProtoId<AccessLevelPrototype>>(), access);
                _access.SetAccessEnabled(uid, false, access);
            }

            if (TryComp<IdCardComponent>(uid, out var idCard))
            {
                _idCards.TryChangeJobTitle(uid, null, idCard);
                idCard.JobTitle = null;
                idCard.JobDepartments = new();
                idCard.CompanyName = "None";
                if (_prototypes.TryIndex<JobIconPrototype>("JobIconUnknown", out var unknownIcon))
                    _idCards.TryChangeJobIcon(uid, unknownIcon, idCard);
                Dirty(uid, idCard);
            }

            foreach (var child in children)
                stack.Push(child);
        }

        // Round- and role-derived authority belongs to the old round, even for
        // a same-process wake where the body entity itself is reused.
        RemComp<NukeOperativeComponent>(root);
        RemComp<RevolutionaryComponent>(root);
        RemComp<HeadRevolutionaryComponent>(root);
        RemComp<PendingZombieComponent>(root);
        RemComp<InitialInfectedComponent>(root);
        RemComp<ZombifyOnDeathComponent>(root);
        RemComp<IncurableZombieComponent>(root);
        RemComp<AutoTraitorComponent>(root);
        RemComp<SpaceNinjaComponent>(root);
        RemComp<PacifiedComponent>(root);
        RemComp<CommandStaffComponent>(root);
        RemComp<RandomMetadataComponent>(root);
        RemComp<GhostRoleComponent>(root);
        RemComp<GhostTakeoverAvailableComponent>(root);
        RemComp<GhostRoleMobSpawnerComponent>(root);
        RemComp<GhostRoleAntagSpawnerComponent>(root);
        RemComp<ShowAntagIconsComponent>(root);
        RemComp<SpecialSectorStationRecordComponent>(root);
        RemComp<MailDisabledComponent>(root);

        // Missing external references serialize as EntityUid.Invalid. Replacing the
        // container guarantees no stale mind/session survives even if a future
        // serializer starts retaining nullable invalid references differently.
        if (!preserveLiveAttachment && HasComp<MindContainerComponent>(root))
        {
            RemComp<MindContainerComponent>(root);
            EnsureComp<MindContainerComponent>(root);
        }

        if (TryComp<DamageableComponent>(root, out var damageable))
            _damageable.SetDamage(root, damageable, new DamageSpecifier());
        if (TryComp<MobStateComponent>(root, out var mobState))
            _mobState.ChangeMobState(root, MobState.Alive, mobState);

        var sleeping = EnsureComp<SleepingComponent>(root);
        // Canonical action rebuilding removes round-local action entities. Never
        // retain a network reference to the old wake action across deep cryo.
        sleeping.WakeAction = null;
        sleeping.CooldownEnd = TimeSpan.FromSeconds(5);
        Dirty(root, sleeping);
    }

    internal bool TryApplyCanonicalAuthority(
        EntityUid root,
        HumanoidCharacterProfile profile,
        EntityPrototype expectedPrototype,
        bool preserveLiveAttachment,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            SanitizeRestoredGraph(root, preserveLiveAttachment);
            ResetCanonicalFactions(root, expectedPrototype);
            if (!TryResetCanonicalRootActions(root, expectedPrototype, out reason))
                return false;

            _humanoidAppearance.LoadProfile(root, profile);
            _metaData.SetEntityName(root, profile.Name);
            if (string.IsNullOrWhiteSpace(profile.FlavorText))
            {
                RemComp<DetailExaminableComponent>(root);
            }
            else
            {
                EnsureComp<DetailExaminableComponent>(root).Content = profile.FlavorText;
            }

            _identity.QueueIdentityUpdate(root);
            return true;
        }
        catch (Exception e)
        {
            reason = $"canonical-authority-reset-failed:{e.GetType().Name}";
            Log.Error($"Failed to reset deep-cryo authority for {ToPrettyString(root)}: {e}");
            return false;
        }
    }

    private void ResetCanonicalFactions(EntityUid root, EntityPrototype expectedPrototype)
    {
        if (!expectedPrototype.TryGetComponent<NpcFactionMemberComponent>(out var canonical, _componentFactory))
        {
            RemComp<NpcFactionMemberComponent>(root);
            return;
        }

        RemComp<NpcFactionMemberComponent>(root);
        var factions = EnsureComp<NpcFactionMemberComponent>(root);
        _npcFactions.AddFactions(
            (root, factions),
            new HashSet<ProtoId<NpcFactionPrototype>>(canonical.Factions));
    }

    private bool TryResetCanonicalRootActions(
        EntityUid root,
        EntityPrototype expectedPrototype,
        out string reason)
    {
        reason = string.Empty;
        if (!TryGetCanonicalRootActions(expectedPrototype, out var canonical, out reason))
            return false;

        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        if (TryComp<ActionsContainerComponent>(root, out var actions))
        {
            foreach (var action in new List<EntityUid>(actions.Container.ContainedEntities))
            {
                var actionPrototype = MetaData(action).EntityPrototype?.ID;
                var keep = actionPrototype != null &&
                           canonical.TryGetValue(actionPrototype, out var allowed) &&
                           kept.GetValueOrDefault(actionPrototype) < allowed;
                if (!keep)
                {
                    Del(action);
                    continue;
                }

                kept[actionPrototype!] = kept.GetValueOrDefault(actionPrototype!) + 1;
            }
        }

        foreach (var (prototype, count) in canonical)
        {
            for (var i = kept.GetValueOrDefault(prototype); i < count; i++)
            {
                if (_actionContainers.AddAction(root, prototype) == null)
                {
                    reason = $"canonical-action-rebuild-failed:{prototype}";
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryGetCanonicalRootActions(
        EntityPrototype expectedPrototype,
        out Dictionary<string, int> actions,
        out string reason)
    {
        reason = string.Empty;
        if (_canonicalRootActions.TryGetValue(expectedPrototype.ID, out actions!))
            return true;

        var template = EntityUid.Invalid;
        try
        {
            template = Spawn(expectedPrototype.ID, MapCoordinates.Nullspace);
            actions = new Dictionary<string, int>(StringComparer.Ordinal);
            if (TryComp<ActionsContainerComponent>(template, out var container))
            {
                foreach (var action in container.Container.ContainedEntities)
                {
                    var prototype = MetaData(action).EntityPrototype?.ID;
                    if (prototype == null || ForbiddenPersistedEntityPrototypes.Contains(prototype))
                        continue;
                    actions[prototype] = actions.GetValueOrDefault(prototype) + 1;
                }
            }

            _canonicalRootActions[expectedPrototype.ID] = actions;
            return true;
        }
        catch (Exception e)
        {
            actions = new Dictionary<string, int>(StringComparer.Ordinal);
            reason = $"canonical-action-template-failed:{e.GetType().Name}";
            return false;
        }
        finally
        {
            if (Exists(template))
                Del(template);
        }
    }

    private bool TryValidateCanonicalBody(
        EntityUid body,
        CharacterKey key,
        out HumanoidCharacterProfile profile,
        out EntityPrototype expectedPrototype,
        out string reason)
    {
        if (!TryGetCanonicalProfile(key, out profile, out expectedPrototype, out reason))
            return false;
        if (!Exists(body))
        {
            reason = "canonical-body-missing";
            return false;
        }

        var actualPrototype = MetaData(body).EntityPrototype?.ID;
        if (!string.Equals(actualPrototype, expectedPrototype.ID, StringComparison.Ordinal))
        {
            reason = $"canonical-body-prototype-mismatch:{actualPrototype ?? "<runtime>"}";
            return false;
        }

        return !TryGetForbiddenRootReason(body, out reason);
    }

    private bool TryGetCanonicalProfile(
        CharacterKey key,
        out HumanoidCharacterProfile profile,
        out EntityPrototype expectedPrototype,
        out string reason)
    {
        profile = default!;
        expectedPrototype = default!;
        reason = string.Empty;
        if (!_preferences.TryGetCachedPreferences(key.UserId, out var preferences) ||
            !preferences.Characters.TryGetValue(key.Slot, out var character) ||
            character is not HumanoidCharacterProfile humanoid)
        {
            reason = "canonical-profile-missing-or-not-humanoid";
            return false;
        }

        if (!_prototypes.TryIndex<SpeciesPrototype>(humanoid.Species, out var species) ||
            !_prototypes.TryIndex<EntityPrototype>(species.Prototype.Id, out var canonicalPrototype) ||
            canonicalPrototype == null)
        {
            reason = $"canonical-species-prototype-missing:{humanoid.Species}";
            return false;
        }

        profile = humanoid;
        expectedPrototype = canonicalPrototype;
        return true;
    }

    private bool TryGetForbiddenRootReason(EntityUid root, out string reason)
    {
        reason = string.Empty;
        if (HasComp<ZombieComponent>(root))
            reason = "irreversible-zombie-body";
        else if (HasComp<DragonComponent>(root))
            reason = "irreversible-dragon-body";
        else if (HasComp<RatKingComponent>(root) || HasComp<RatKingServantComponent>(root))
            reason = "irreversible-rat-king-body";
        else if (HasComp<GhostRoleMobSpawnerComponent>(root) ||
                 HasComp<GhostRoleAntagSpawnerComponent>(root))
            reason = "ghost-role-spawner-body";

        return reason.Length != 0;
    }

    private List<EntityPrototype> EnableDeepCryoSerialization(EntityUid root)
    {
        var changed = new List<EntityPrototype>();
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<EntityUid>();
        var stack = new Stack<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            if (MetaData(uid).EntityPrototype is { MapSavable: false } prototype &&
                changedIds.Add(prototype.ID))
            {
                changed.Add(prototype);
            }

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }

        foreach (var prototype in changed)
            prototype.MapSavable = true;

        return changed;
    }

    private bool TryDetachRegeneratedRuntimeChildren(
        EntityUid root,
        out List<(EntityUid Entity, BaseContainer Container)> detached,
        out HashSet<EntityUid> runtimeChildren,
        out string reason)
    {
        detached = new List<(EntityUid, BaseContainer)>();
        runtimeChildren = new HashSet<EntityUid>();
        reason = string.Empty;
        var recognized = new List<(EntityUid Entity, BaseContainer Container)>();
        var visited = new HashSet<EntityUid>();
        var stack = new Stack<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            if (uid != root && MetaData(uid).EntityPrototype == null)
            {
                var parent = Transform(uid).ParentUid;
                var isIdentity = TryComp<IdentityComponent>(parent, out var identity) &&
                                 identity.IdentityEntitySlot.Contains(uid);
                if (!_containers.TryGetContainingContainer((uid, null, null), out var container))
                {
                    reason = $"unsupported-runtime-child:{ToPrettyString(uid)}";
                    return false;
                }

                var containerId = container.ID;
                var isGeneratedSolution = HasComp<SolutionComponent>(uid) &&
                                          HasComp<SolutionContainerManagerComponent>(container.Owner) &&
                                          container.Owner == parent &&
                                          containerId.StartsWith("solution@", StringComparison.Ordinal);
                if (!isIdentity && !isGeneratedSolution)
                {
                    reason = $"unsupported-runtime-child:{ToPrettyString(uid)}";
                    return false;
                }

                var runtimeDescendants = Transform(uid).ChildEnumerator;
                if (runtimeDescendants.MoveNext(out _))
                {
                    reason = $"unsupported-runtime-child-with-descendants:{ToPrettyString(uid)}";
                    return false;
                }

                runtimeChildren.Add(uid);
                recognized.Add((uid, container));
                continue;
            }

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }

        foreach (var runtime in recognized)
        {
            // Keep the transform relationship intact so capture never moves the
            // generated entity through nullspace or a grid. Emptying the slot is
            // still required so the serialized ContainerManager can regenerate it.
            var removed = false;
            try
            {
                removed = _containers.Remove(runtime.Entity, runtime.Container, reparent: false, force: true);
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo capture could not detach runtime child {runtime.Entity}: {e}");
            }

            if (removed)
            {
                detached.Add(runtime);
                continue;
            }

            reason = $"runtime-child-detach-failed:{ToPrettyString(runtime.Entity)}";
            if (!TryRestoreRegeneratedRuntimeChildren(detached, out var restoreReason))
                reason = restoreReason;
            detached.Clear();
            runtimeChildren.Clear();
            return false;
        }

        return true;
    }

    private bool TryRestoreRegeneratedRuntimeChildren(
        List<(EntityUid Entity, BaseContainer Container)> detached,
        out string reason)
    {
        reason = string.Empty;
        var restoredAll = true;
        foreach (var (entity, container) in detached)
        {
            var owner = container.Owner;
            var containerId = container.ID;
            string? failure = null;

            if (!Exists(entity) || !Exists(owner))
            {
                failure = "entity-or-owner-deleted";
            }
            else if (!_containers.TryGetContainer(owner, containerId, out var current) ||
                     !ReferenceEquals(current, container))
            {
                failure = "container-replaced";
            }
            else if (container.Contains(entity))
            {
                if (Transform(entity).ParentUid == owner)
                    continue;
                failure = "contained-with-wrong-transform-parent";
            }
            else if (container.ContainedEntities.Count != 0)
            {
                failure = "container-no-longer-empty";
            }
            else
            {
                try
                {
                    if (!_containers.Insert(entity, container, force: true) ||
                        !container.Contains(entity) ||
                        Transform(entity).ParentUid != owner)
                    {
                        failure = "insert-or-verification-failed";
                    }
                }
                catch (Exception e)
                {
                    failure = $"insert-exception:{e.GetType().Name}";
                }
            }

            if (failure == null)
                continue;

            if (restoredAll)
                reason = $"runtime-child-restore-failed:{entity}";
            restoredAll = false;
            Log.Error($"Deep-cryo capture could not restore runtime child {entity} " +
                      $"to container {containerId} on {owner}: {failure}.");
        }

        return restoredAll;
    }

    private bool IsRegeneratedRuntimeChild(EntityUid uid)
    {
        if (!Exists(uid) || MetaData(uid).EntityPrototype != null)
            return false;

        if (HasComp<SolutionComponent>(uid))
            return true;

        var parent = Transform(uid).ParentUid;
        return TryComp<IdentityComponent>(parent, out var identity) &&
               identity.IdentityEntitySlot.Contains(uid);
    }

    private bool TryGetCurrentCharacterKey(NetUserId userId, out CharacterKey key)
    {
        key = default;
        if (!_preferences.TryGetCachedPreferences(userId, out var preferences))
            return false;

        var slot = preferences.SelectedCharacterIndex;
        if (slot < 0 || !_preferences.TryGetCharacterProfileId(userId, slot, out var profileId))
            return false;

        key = new CharacterKey(userId, profileId, slot);
        return true;
    }

    private void ForgetLiveBody(CharacterKey key, bool deleteBody)
    {
        if (!_liveBodies.Remove(key, out var body) || !Exists(body))
            return;

        RemComp<LuaMDeepCryoStoredComponent>(body);
        if (deleteBody)
            QueueDel(body);
    }

    private static bool FixedHashEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
    }

    private static string LimitReason(string reason)
    {
        return reason.Length <= LuaMDeepCryoLimits.MaxReasonLength
            ? reason
            : reason[..LuaMDeepCryoLimits.MaxReasonLength];
    }

    public readonly record struct CharacterKey(NetUserId UserId, int ProfileId, int Slot);
}

public readonly record struct LuaMDeepCryoPayload(
    byte[] Bytes,
    string Hash,
    int EntityCount,
    string PrototypeManifestHash);

public readonly record struct LuaMDeepCryoStoreCompletion(
    bool Success,
    long? SnapshotId,
    long? Revision,
    string? FailureReason);

public sealed record LuaMDeepCryoRestoreHandle(
    LuaMDeepCryoPersistenceSystem.CharacterKey Key,
    long SnapshotId,
    long Revision,
    Guid LeaseId,
    EntityUid Body,
    bool Deserialized);

public readonly record struct LuaMDeepCryoClaimResult(
    LuaMDeepCryoClaimStatus Status,
    LuaMDeepCryoRestoreHandle? Handle)
{
    public static LuaMDeepCryoClaimResult Failed(LuaMDeepCryoClaimStatus status) => new(status, null);
}

public enum LuaMDeepCryoClaimStatus : byte
{
    Success,
    Missing,
    Busy,
    IdentityInvalid,
    Quarantined,
    DatabaseFailure,
}
