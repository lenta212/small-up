using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Antag.Components;
using Content.Server.Database;
using Content.Server.Dragon;
using Content.Server.GameTicking;
using Content.Server.Ghost;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Humanoid;
using Content.Server.IdentityManagement;
using Content.Server.KillTracking;
using Content.Server.Mind;
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
using Content.Shared.Ghost;
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
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared;
using Robust.Server.Player;

namespace Content.Server._LuaM.Cryo;

/// <summary>
/// Durable, profile-bound deep-cryo coordinator shared by both cryo implementations.
/// The database owns lifecycle/CAS state; entity markers are only main-thread fences.
/// </summary>
public sealed class LuaMDeepCryoPersistenceSystem : EntitySystem
{
    public const int PayloadFormatVersion = 1;

    private static readonly TimeSpan RestoreLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RestorePublicationSafetyWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PlayablePresenceLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlayablePresenceRenewLead = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PublicationRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ForbiddenPersistedEntityPrototypes = new(StringComparer.Ordinal)
    {
        "ActionOpenUplinkImplant",
        "ActionTurnUndead",
        "UplinkImplant",
    };

    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPlayerManager _players = default!;
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
    [Dependency] private MindSystem _minds = default!;
    [Dependency] private GhostSystem _ghost = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<CharacterKey, EntityUid> _liveBodies = new();
    private readonly Dictionary<CharacterKey, Guid> _activeRestores = new();
    private readonly Dictionary<CharacterKey, LuaMDeepCryoRestoreHandle> _restoreHandles = new();
    private readonly HashSet<CharacterKey> _consumeStarted = new();
    private readonly HashSet<CharacterKey> _authorizedPublications = new();
    private readonly Dictionary<CharacterKey, LuaMDeepCryoPublicationReceipt> _publicationReceipts = new();
    private readonly Dictionary<CharacterKey, PendingPublicationOperation> _pendingPublicationOperations = new();
    private readonly Dictionary<CharacterKey, PendingAuthorizationOperation> _pendingAuthorizationOperations = new();
    private readonly Dictionary<CharacterKey, PendingRestoreAbort> _pendingRestoreAborts = new();
    private readonly Dictionary<Guid, DetachedRoundSettlement> _detachedRoundSettlements = new();
    private readonly Dictionary<Guid, TerminalCleanupRegistration> _terminalCleanupRegistrations = new();
    private readonly HashSet<Guid> _inFlightClaimLeases = new();
    private readonly Dictionary<Guid, PendingStoreOperation> _pendingStores = new();
    private readonly Dictionary<CharacterKey, Guid> _pendingStoreKeys = new();
    private readonly Dictionary<CharacterKey, PendingPresenceRenewal> _pendingPresenceRenewals = new();
    private readonly Dictionary<Guid, PendingPresenceRelease> _pendingPresenceReleases = new();
    private readonly HashSet<Guid> _acknowledgedQuarantineLeases = new();
    private readonly Dictionary<Guid, LuaMDeepCryoQuarantineAcknowledgedPublicationRequest>
        _acknowledgedQuarantineRequests = new();
    private readonly Dictionary<Guid, LuaMCharacterPresenceRenewRequest>
        _acknowledgedQuarantineRenewals = new();
    private readonly Dictionary<Guid, PendingConsumedSpawnTicket> _consumedSpawnLifecycleTickets = new();
    private readonly Dictionary<EntityUid, CharacterKey> _unboundSpawnBodiesPendingDisposal = new();
    private readonly Dictionary<EntityUid, SuspendedPresenceBody> _suspendedPresenceBodies = new();
    private readonly HashSet<CharacterKey> _discardPending = new();
    private readonly Dictionary<NetUserId, SpawnLifecycleTicket> _spawnLifecycleTickets = new();
    private Func<bool>? _freshSpawnReadyHookForTests;
    private readonly Dictionary<string, Dictionary<string, int>> _canonicalRootActions = new(StringComparer.Ordinal);
    private readonly string _serverInstanceId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private TimeSpan _nextLeaseRecovery;
    private TimeSpan _nextPresenceMaintenance;
    private bool _leaseRecoveryRunning;
    private int _roundCleanupGeneration;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnPlayerBeforeSpawn);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<LuaMDeepCryoPendingComponent, ContainerGettingRemovedAttemptEvent>(OnPendingStoreRemovalAttempt);
        SubscribeLocalEvent<LuaMDeepCryoIdentityComponent, EntityTerminatingEvent>(OnPresenceBodyTerminating);

        _nextLeaseRecovery = _timing.CurTime;
        RecoverExpiredLeases();
    }

    private void OnPendingStoreRemovalAttempt(
        EntityUid uid,
        LuaMDeepCryoPendingComponent component,
        ContainerGettingRemovedAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _roundCleanupGeneration++;
        foreach (var ticket in _spawnLifecycleTickets.Values.ToArray())
            RetainConsumedSpawnTicket(ticket);
        _spawnLifecycleTickets.Clear();

        var presenceQuery = EntityQueryEnumerator<LuaMDeepCryoIdentityComponent>();
        while (presenceQuery.MoveNext(out var body, out var identity))
        {
            if (identity.PresenceLeaseId == Guid.Empty ||
                identity.PresencePhase != DbLuaMCharacterPresencePhase.Playable)
            {
                continue;
            }

            var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
            if (_pendingStoreKeys.ContainsKey(key) ||
                IsPresenceOwnedByActiveRestore(key, identity.PresenceLeaseId))
                continue;

            BeginPresenceRelease(body, identity, "round-cleanup");
        }

        // Detach the old generation synchronously. Its exact durable operations
        // continue below in a lease-keyed queue, but no old continuation may keep
        // a same-key restore busy or mutate state created by the next round.
        foreach (var (key, handle) in _restoreHandles.ToArray())
        {
            if (!_activeRestores.TryGetValue(key, out var activeLease) || activeLease != handle.LeaseId)
                continue;

            _publicationReceipts.TryGetValue(key, out var receipt);
            _pendingAuthorizationOperations.TryGetValue(key, out var pendingAuthorization);
            _pendingPublicationOperations.TryGetValue(key, out var pendingPublication);
            _pendingRestoreAborts.TryGetValue(key, out var pendingAbort);

            if (_pendingPresenceRenewals.TryGetValue(key, out var pendingRenewal) &&
                pendingRenewal.Request.LeaseId == handle.LeaseId)
            {
                pendingRenewal.ReservedForRestoreSettlement = true;
                if (!pendingRenewal.Running)
                    _pendingPresenceRenewals.Remove(key);
            }

            if (pendingAuthorization != null)
                pendingAuthorization.Detached = true;
            if (pendingPublication != null)
                pendingPublication.Detached = true;
            if (pendingAbort != null)
                pendingAbort.Detached = true;

            var settlement = CreateDetachedRoundSettlement(
                handle,
                receipt,
                pendingAuthorization,
                pendingPublication,
                pendingAbort);
            _detachedRoundSettlements[handle.LeaseId] = settlement;

            DetachRestoreState(handle, receipt, pendingAuthorization, pendingPublication, pendingAbort);
            DeleteDetachedRoundBody(handle);
        }

        foreach (var pendingStore in _pendingStores.Values)
        {
            pendingStore.Detached = true;
            pendingStore.NextAttempt = _timing.CurTime;
            if (_liveBodies.TryGetValue(pendingStore.Key, out var liveBody) && liveBody == pendingStore.Body)
                _liveBodies.Remove(pendingStore.Key);
            if (Exists(pendingStore.Body))
                QueueDel(pendingStore.Body);
        }

        foreach (var settlement in _detachedRoundSettlements.Values.ToArray())
        {
            if (!settlement.Running && settlement.NextAttempt <= _timing.CurTime)
                RetryDetachedRoundSettlement(settlement);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_leaseRecoveryRunning && _timing.CurTime >= _nextLeaseRecovery)
            RecoverExpiredLeases();

        // Crossing the renewal lead is an authority boundary, not ordinary
        // retry work. Check it every tick so a body cannot remain playable for
        // up to the one-second maintenance cadence after it becomes due.
        SchedulePresenceRenewals();
        EnforceSuspendedPresenceFences();

        // Durable retries do not need frame precision. A one-second cadence
        // prevents allocation-heavy dictionary snapshots on every server tick.
        if (_timing.CurTime < _nextPresenceMaintenance)
            return;

        _nextPresenceMaintenance = _timing.CurTime + TimeSpan.FromSeconds(1);
        RetryPendingPublicationOperations();
        RetryPendingAuthorizationOperations();
        RetryPendingRestoreAborts();
        RetryDetachedRoundSettlements();
        RetryPendingStores();
        RetryConsumedSpawnTickets();
        RetryUnboundSpawnBodyDisposals();
        RetryPendingPresenceRenewals();
        RetryPendingPresenceReleases();
    }

    private void EnforceSuspendedPresenceFences()
    {
        foreach (var (body, state) in _suspendedPresenceBodies.ToArray())
        {
            if (!Exists(body))
            {
                _suspendedPresenceBodies.Remove(body);
                continue;
            }

            // The identity can be missing precisely because a legacy or partial
            // body reached the fail-closed path. Retain the immutable key captured
            // when the fence was created so every tick can finish detaching the
            // session/mind and moving the body to nullspace.
            SuspendPresenceBody(body, state.Key);
        }
    }

    private void SchedulePresenceRenewals()
    {
        var now = DateTime.UtcNow;
        var query = EntityQueryEnumerator<LuaMDeepCryoIdentityComponent>();
        while (query.MoveNext(out var body, out var identity))
        {
            if (identity.PresenceLeaseId == Guid.Empty ||
                identity.PresencePhase != DbLuaMCharacterPresencePhase.Playable ||
                identity.LifecycleRevision < 0 ||
                identity.PresenceLeaseRevision < 0 ||
                identity.PresenceLeaseExpiresAtUtc > now + PlayablePresenceRenewLead ||
                _acknowledgedQuarantineLeases.Contains(identity.PresenceLeaseId))
            {
                continue;
            }

            var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
            if (_pendingStoreKeys.ContainsKey(key) ||
                _pendingPresenceRenewals.ContainsKey(key) ||
                _pendingPresenceReleases.Values.Any(pending =>
                    pending.Key == key &&
                    pending.Request.LeaseId == identity.PresenceLeaseId))
            {
                continue;
            }

            var renewedAtUtc = now;
            var request = new LuaMCharacterPresenceRenewRequest(
                Guid.NewGuid(),
                key.UserId,
                key.ProfileId,
                key.Slot,
                identity.PresenceLeaseId,
                identity.PresenceLeaseRevision,
                identity.LifecycleRevision,
                renewedAtUtc,
                renewedAtUtc + PlayablePresenceLeaseDuration);
            var pending = new PendingPresenceRenewal(
                body,
                key,
                request,
                identity.PresenceSnapshotId,
                _timing.CurTime);
            _pendingPresenceRenewals.Add(key, pending);
            // Fence immediately at the renewal lead boundary. The DB await may
            // stall past expiry; no outstanding task is allowed to leave a
            // reclaimable old-token body exposed meanwhile.
            if (!SuspendPresenceBody(body, key))
                Log.Error($"Could not fully suspend {key} before its playable-presence renewal; retaining the renewal fence and retry state.");
            RetryPendingPresenceRenewal(pending);
        }
    }

    private void RetryPendingPresenceRenewals()
    {
        foreach (var pending in _pendingPresenceRenewals.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryPendingPresenceRenewal(pending);
        }
    }

    private async void RetryPendingPresenceRenewal(PendingPresenceRenewal pending)
    {
        try
        {
            await ExecutePendingPresenceRenewalAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            SuspendPresenceBody(pending.Body, pending.Key);
            Log.Error($"Unexpected playable-presence renewal failure for {pending.Key}: {e}");
        }
    }

    private async Task ExecutePendingPresenceRenewalAsync(PendingPresenceRenewal pending)
    {
        if (pending.Running ||
            !_pendingPresenceRenewals.TryGetValue(pending.Key, out var retained) ||
            retained != pending)
        {
            return;
        }

        pending.Running = true;
        try
        {
            LuaMCharacterPresenceAuthorityRecord? confirmed = null;
            LuaMCharacterPresenceAuthorityRecord? current = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await _db.RenewLuaMCharacterPresenceAsync(pending.Request);
                    current = result.Authority;
                    if (IsConfirmedPresenceRenewal(current, pending))
                    {
                        confirmed = current;
                        break;
                    }
                    if (result.Status != LuaMDeepCryoWriteStatus.UnknownOutcome &&
                        current != null && current.LeaseId != pending.Request.LeaseId)
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Playable-presence renewal attempt {attempt + 1} failed for {pending.Key}: {e}");
                }
            }

            if (confirmed == null)
            {
                try
                {
                    current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                        pending.Key.UserId,
                        pending.Key.ProfileId,
                        pending.Key.Slot);
                    if (IsConfirmedPresenceRenewal(current, pending))
                        confirmed = current;
                }
                catch (Exception e)
                {
                    Log.Error($"Playable-presence renewal re-read failed for {pending.Key}: {e}");
                }
            }

            if (!_pendingPresenceRenewals.TryGetValue(pending.Key, out retained) || retained != pending)
                return;

            if (pending.ReservedForRestoreSettlement)
            {
                if (confirmed != null &&
                    Exists(pending.Body) &&
                    TryComp<LuaMDeepCryoIdentityComponent>(pending.Body, out var settlementIdentity) &&
                    settlementIdentity.PresenceLeaseId == pending.Request.LeaseId &&
                    settlementIdentity.LifecycleRevision == pending.Request.ExpectedAuthorityLifecycleRevision)
                {
                    settlementIdentity.PresenceLeaseRevision = confirmed.Revision;
                    settlementIdentity.PresenceLeaseExpiresAtUtc = confirmed.ExpiresAtUtc;
                    settlementIdentity.PresencePhase = confirmed.Phase;
                    settlementIdentity.PresenceSnapshotId = confirmed.SnapshotId;
                }

                SuspendPresenceBody(pending.Body, pending.Key);
                _pendingPresenceRenewals.Remove(pending.Key);
                WakeAcknowledgedQuarantineSettlement(pending.Key, pending.Request.LeaseId);
                return;
            }

            if (confirmed != null)
            {
                if (pending.ReleaseAfterCompletion)
                {
                    _pendingPresenceRenewals.Remove(pending.Key);
                    EnqueuePresenceRelease(pending.Key, confirmed, "body-terminated-after-renewal");
                    ClearPresenceAuthorityIfMatches(pending.Body, confirmed.LeaseId);
                    return;
                }

                if (!Exists(pending.Body) ||
                    !TryComp<LuaMDeepCryoIdentityComponent>(pending.Body, out var identity) ||
                    identity.PresenceLeaseId != pending.Request.LeaseId ||
                    identity.LifecycleRevision != pending.Request.ExpectedAuthorityLifecycleRevision)
                {
                    _pendingPresenceRenewals.Remove(pending.Key);
                    EnqueuePresenceRelease(pending.Key, confirmed, "renewed-body-missing-or-rebound");
                    return;
                }

                identity.PresenceLeaseRevision = confirmed.Revision;
                identity.PresenceLeaseExpiresAtUtc = confirmed.ExpiresAtUtc;
                identity.PresencePhase = confirmed.Phase;
                identity.PresenceSnapshotId = confirmed.SnapshotId;
                if (!TryGetCurrentCharacterKey(pending.Key.UserId, out var selected) ||
                    selected != pending.Key)
                {
                    // Preference/session generations are only local cache
                    // fences. They are not durable proof that this exact
                    // playable token or its sole body was superseded. Keep the
                    // renewed body suspended and finish this immutable renewal;
                    // the scheduler may create a later lease-extension cycle
                    // even while the selected identity remains unavailable.
                    SuspendPresenceBody(pending.Body, pending.Key);
                    _pendingPresenceRenewals.Remove(pending.Key);
                    return;
                }

                // Disconnect and preference refresh invalidate local slot
                // generations without changing durable authority. Once the
                // exact token and CharacterKey have both been verified, rebind
                // the cache generation instead of releasing and deleting the
                // only body.
                identity.SlotGeneration = _preferences.GetCharacterSlotGeneration(
                    pending.Key.UserId,
                    pending.Key.Slot);

                if (!HasSafePlayablePresenceWindow(confirmed))
                {
                    // The exact renew committed, but its reply arrived too late
                    // to expose safely. A new immutable renewal cycle will start
                    // while this body remains suspended.
                    _pendingPresenceRenewals.Remove(pending.Key);
                    return;
                }

                if (_suspendedPresenceBodies.ContainsKey(pending.Body) &&
                    IsRestorePublicationActive(pending.Key))
                {
                    // ACK still owns this publication target. Renewal may
                    // extend the exact token, but cannot bypass receipt and
                    // canPublish validation by restoring the body itself.
                    _pendingPresenceRenewals.Remove(pending.Key);
                    return;
                }

                if (_suspendedPresenceBodies.ContainsKey(pending.Body) &&
                    !RestoreSuspendedPresenceBody(pending.Body, pending.Key))
                {
                    Log.Error($"Renewed playable body for {pending.Key} could not be restored from its fail-closed location; retaining it suspended.");
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return;
                }

                _pendingPresenceRenewals.Remove(pending.Key);
                return;
            }

            // A foreign token (or authoritative absence) proves this local body
            // stale. Unknown DB state never does: retain the sole body suspended
            // and exact-replay the immutable renewal request.
            if (await CanProveStalePresenceAsync(pending, current))
            {
                if (DisposeStalePresenceBody(pending.Body, pending.Key, pending.Request.LeaseId))
                    _pendingPresenceRenewals.Remove(pending.Key);
                else
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            SuspendPresenceBody(pending.Body, pending.Key);
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
        }
        finally
        {
            pending.Running = false;
        }
    }

    private static bool IsConfirmedPresenceRenewal(
        LuaMCharacterPresenceAuthorityRecord? authority,
        PendingPresenceRenewal pending)
    {
        return IsConfirmedPresenceRenewal(
            authority,
            pending.Key,
            pending.Request,
            pending.SnapshotId);
    }

    private static bool IsConfirmedPresenceRenewal(
        LuaMCharacterPresenceAuthorityRecord? authority,
        CharacterKey key,
        LuaMCharacterPresenceRenewRequest request,
        long? snapshotId)
    {
        return IsExactPlayablePresence(
                   authority,
                   key,
                   request.LeaseId,
                   request.ExpectedAuthorityLifecycleRevision,
                   snapshotId) &&
               authority!.Revision == request.ExpectedLeaseRevision + 1 &&
               authority.RenewedAtUtc.ToUniversalTime() == request.RenewedAtUtc.ToUniversalTime() &&
               authority.ExpiresAtUtc.ToUniversalTime() == request.LeaseExpiresAtUtc.ToUniversalTime();
    }

    private async Task<bool> CanProveStalePresenceAsync(
        PendingPresenceRenewal pending,
        LuaMCharacterPresenceAuthorityRecord? observedAuthority)
    {
        var request = pending.Request;
        if (observedAuthority is { } foreign &&
            foreign.ProfileId == pending.Key.ProfileId &&
            foreign.LeaseId != request.LeaseId &&
            foreign.AuthorityLifecycleRevision > request.ExpectedAuthorityLifecycleRevision)
        {
            return true;
        }

        try
        {
            var current = await _db.GetLuaMDeepCryoStorePreconditionAsync(
                pending.Key.UserId,
                pending.Key.ProfileId,
                pending.Key.Slot);
            if (current == null)
                return false;

            if (current.Authority is { } currentForeign)
            {
                return currentForeign.ProfileId == pending.Key.ProfileId &&
                       currentForeign.LeaseId != request.LeaseId &&
                       currentForeign.AuthorityLifecycleRevision >
                       request.ExpectedAuthorityLifecycleRevision;
            }

            return current.LifecycleRevision > request.ExpectedAuthorityLifecycleRevision &&
                   current.ActiveSnapshot is
                   {
                       Status: DbLuaMDeepCryoSnapshotStatus.Stored or
                       DbLuaMDeepCryoSnapshotStatus.Quarantined,
                   };
        }
        catch
        {
            return false;
        }
    }

    private void OnPresenceBodyTerminating(
        EntityUid body,
        LuaMDeepCryoIdentityComponent identity,
        ref EntityTerminatingEvent args)
    {
        _suspendedPresenceBodies.Remove(body);
        if (identity.PresenceLeaseId == Guid.Empty ||
            identity.PresencePhase != DbLuaMCharacterPresencePhase.Playable)
        {
            return;
        }

        var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
        if (_pendingStoreKeys.ContainsKey(key) ||
            IsPresenceOwnedByActiveRestore(key, identity.PresenceLeaseId))
            return;

        BeginPresenceRelease(body, identity, "playable-body-terminated");
    }

    private void BeginPresenceRelease(
        EntityUid body,
        LuaMDeepCryoIdentityComponent identity,
        string reason)
    {
        var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
        if (IsPresenceOwnedByActiveRestore(key, identity.PresenceLeaseId))
            return;

        if (_pendingPresenceRenewals.TryGetValue(key, out var renewal) &&
            renewal.Request.LeaseId == identity.PresenceLeaseId)
        {
            renewal.ReleaseAfterCompletion = true;
            ClearPresenceAuthority(identity);
            return;
        }

        var authority = new LuaMCharacterPresenceAuthorityRecord(
            key.ProfileId,
            identity.PresenceSnapshotId,
            identity.PresenceLeaseId,
            identity.PresencePhase,
            _serverInstanceId,
            _gameTicker.RoundId,
            identity.PresenceLeaseExpiresAtUtc,
            identity.PresenceLeaseExpiresAtUtc,
            identity.PresenceLeaseExpiresAtUtc,
            identity.PresenceLeaseRevision,
            identity.LifecycleRevision);
        EnqueuePresenceRelease(key, authority, reason);
        ClearPresenceAuthority(identity);
    }

    private bool IsPresenceOwnedByActiveRestore(CharacterKey key, Guid leaseId)
    {
        if (leaseId == Guid.Empty)
            return false;

        if (_activeRestores.TryGetValue(key, out var activeLease) && activeLease == leaseId)
            return true;

        return _detachedRoundSettlements.TryGetValue(leaseId, out var detached) &&
               detached.Handle.Key == key;
    }

    private void EnqueuePresenceRelease(
        CharacterKey key,
        LuaMCharacterPresenceAuthorityRecord authority,
        string reason,
        EntityUid bodyToDispose = default)
    {
        var releasedAtUtc = DateTime.UtcNow;
        var request = new LuaMCharacterPresenceReleaseRequest(
            Guid.NewGuid(),
            key.UserId,
            key.ProfileId,
            key.Slot,
            authority.LeaseId,
            authority.Phase,
            authority.SnapshotId,
            authority.Revision,
            authority.AuthorityLifecycleRevision,
            LimitReason(reason),
            releasedAtUtc);
        var pending = new PendingPresenceRelease(
            key,
            request,
            reason,
            bodyToDispose,
            _timing.CurTime);
        _pendingPresenceReleases.TryAdd(request.OperationId, pending);
    }

    private void RetryPendingPresenceReleases()
    {
        foreach (var pending in _pendingPresenceReleases.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryPendingPresenceRelease(pending);
        }
    }

    private async void RetryPendingPresenceRelease(PendingPresenceRelease pending)
    {
        try
        {
            await ExecutePendingPresenceReleaseAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            Log.Error($"Unexpected playable-presence release failure for {pending.Key}: {e}");
        }
    }

    private async Task ExecutePendingPresenceReleaseAsync(PendingPresenceRelease pending)
    {
        var request = pending.Request;
        if (pending.Running ||
            !_pendingPresenceReleases.TryGetValue(request.OperationId, out var retained) ||
            retained != pending)
        {
            return;
        }

        pending.Running = true;
        try
        {
            LuaMCharacterPresenceAuthorityRecord? current = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await _db.ReleaseLuaMCharacterPresenceAsync(request);
                    current = result.Authority;
                    if (result.Success ||
                        result.Status != LuaMDeepCryoWriteStatus.UnknownOutcome &&
                        IsDefinitiveForeignPresence(current, request))
                    {
                        if (!TryFinalizePresenceRelease(pending))
                            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                        return;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Playable-presence release attempt {attempt + 1} failed for {pending.Key}: {e}");
                }
            }

            try
            {
                current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                    pending.Key.UserId,
                    pending.Key.ProfileId,
                    pending.Key.Slot);
                if (current == null || IsDefinitiveForeignPresence(current, request))
                {
                    if (!TryFinalizePresenceRelease(pending))
                        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error($"Playable-presence release re-read failed for {pending.Key}: {e}");
            }

            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
        }
        finally
        {
            pending.Running = false;
        }
    }

    private static bool IsDefinitiveForeignPresence(
        LuaMCharacterPresenceAuthorityRecord? authority,
        LuaMCharacterPresenceReleaseRequest request)
    {
        return authority != null &&
               authority.ProfileId == request.ProfileId &&
               authority.LeaseId != request.LeaseId &&
               authority.AuthorityLifecycleRevision > request.ExpectedAuthorityLifecycleRevision;
    }

    private bool TryFinalizePresenceRelease(PendingPresenceRelease pending)
    {
        if (pending.BodyToDispose != default &&
            !DisposeStalePresenceBody(
                pending.BodyToDispose,
                pending.Key,
                pending.Request.LeaseId))
        {
            return false;
        }

        _pendingPresenceReleases.Remove(pending.Request.OperationId);
        return true;
    }

    private bool SuspendPresenceBody(EntityUid body, CharacterKey key)
    {
        if (!Exists(body))
            return false;
        if (!_suspendedPresenceBodies.TryGetValue(body, out var state))
        {
            var xform = Transform(body);
            EntityUid? containerOwner = null;
            string? containerId = null;
            if (_containers.TryGetContainingContainer((body, null, null), out var container))
            {
                containerOwner = container.Owner;
                containerId = container.ID;
            }

            state = new SuspendedPresenceBody(
                key,
                xform.Coordinates,
                _transform.GetMapCoordinates(body, xform),
                xform.LocalRotation,
                containerOwner,
                containerId);
            _suspendedPresenceBodies.Add(body, state);
            EnsureComp<LuaMDeepCryoPresenceSuspendedComponent>(body);
        }
        else
        {
            key = state.Key;
        }

        var frontierStoreOwnsControl =
            TryComp<LuaMDeepCryoPendingComponent>(body, out var storeFence) &&
            storeFence.Source == LuaMDeepCryoSource.FrontierCryoSleep;
        if (!frontierStoreOwnsControl &&
            _pendingStoreKeys.TryGetValue(key, out var storeOperationId) &&
            _pendingStores.TryGetValue(storeOperationId, out var storeOperation) &&
            storeOperation.Body == body &&
            storeOperation.Source == LuaMDeepCryoSource.FrontierCryoSleep)
        {
            frontierStoreOwnsControl = true;
        }

        if (frontierStoreOwnsControl)
        {
            state.PreserveUpstreamGhostControl = true;
        }

        try
        {
            // A reconnect can already have attached this account's mind to a
            // newer body. Presence maintenance for the old body must never
            // fence or rewrite that unrelated control state.
            if (_minds.TryGetMind(key.UserId, out var mind) && mind.Value.Comp.OwnedEntity == body)
            {
                var current = mind.Value.Comp.CurrentEntity;
                if (current == body)
                {
                    state.RestoreBodyControl = true;
                    state.ControlGhost = _ghost.SpawnGhost(
                        (mind.Value.Owner, mind.Value.Comp),
                        spawnPosition: null,
                        canReturn: false);
                }
                else if (current is { } ghostUid && TryComp<GhostComponent>(ghostUid, out var ghost))
                {
                    // Frontier creates its own temporary ghost fence before it
                    // starts Store. It owns the pre-store return permission and
                    // restores it from its callback after a proven no-commit
                    // result. Do not snapshot its intentionally false interim
                    // value, or a later presence restore can overwrite that
                    // callback's exact prior value.
                    if (!state.PreserveUpstreamGhostControl && !state.ControlGhostStateCaptured)
                    {
                        state.ControlGhost = ghostUid;
                        state.PreviousCanReturn = ghost.CanReturnToBody;
                        state.ControlGhostStateCaptured = true;
                    }

                    if (!state.PreserveUpstreamGhostControl)
                        _ghost.SetCanReturnToBody(ghostUid, false, ghost);
                }
            }
            if (_players.TryGetSessionById(key.UserId, out var session) && session.AttachedEntity == body)
            {
                state.RestoreBodyControl = true;
                _players.SetAttachedEntity(session, null);
            }

            if (_containers.TryGetContainingContainer((body, null, null), out var currentContainer))
            {
                _containers.Remove(body, currentContainer, reparent: false, force: true);
            }

            // Detaching does not resolve a map and is valid even while a map is
            // being torn down. SetMapCoordinates(nullspace) can dereference a
            // removed map for an already orphaned body.
            _transform.SetParent(body, EntityUid.Invalid);
            var mindFenced = !_minds.TryGetMind(key.UserId, out var currentMind) ||
                             currentMind.Value.Comp.CurrentEntity != body;
            var sessionFenced = !_players.TryGetSessionById(key.UserId, out var attached) ||
                                attached.AttachedEntity != body;
            return Transform(body).MapID == MapId.Nullspace && mindFenced && sessionFenced;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to suspend playable body for {key}: {e}");
            return false;
        }
    }

    private bool RestoreSuspendedPresenceBody(EntityUid body, CharacterKey key)
    {
        if (!_suspendedPresenceBodies.TryGetValue(body, out var state))
            return true;
        if (!Exists(body))
        {
            _suspendedPresenceBodies.Remove(body);
            return false;
        }

        try
        {
            var restoredLocation = false;
            if (state.ContainerOwner is { } owner &&
                state.ContainerId is { } containerId &&
                Exists(owner) &&
                _containers.TryGetContainer(owner, containerId, out var container))
            {
                restoredLocation = _containers.Insert(body, container);
            }

            if (!restoredLocation)
            {
                if (state.OriginalCoordinates.IsValid(EntityManager))
                    _transform.SetCoordinates(body, state.OriginalCoordinates);
                else
                    _transform.SetMapCoordinates(body, state.OriginalMapCoordinates);
            }

            _transform.SetLocalRotation(body, state.OriginalRotation);
            if (Transform(body).MapID == MapId.Nullspace)
                return false;

            if (state.RestoreBodyControl)
            {
                _minds.ControlMob(key.UserId, body);
            }
            else if (!state.PreserveUpstreamGhostControl &&
                     state.ControlGhost is { } ghostUid &&
                     Exists(ghostUid) &&
                     TryComp<GhostComponent>(ghostUid, out var ghost))
            {
                _ghost.SetCanReturnToBody(ghostUid, state.PreviousCanReturn, ghost);
            }

            RemComp<LuaMDeepCryoPresenceSuspendedComponent>(body);
            _suspendedPresenceBodies.Remove(body);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to restore renewed playable body for {key}: {e}");
            SuspendPresenceBody(body, state.Key);
            return false;
        }
    }

    private bool DisposeStalePresenceBody(EntityUid body, CharacterKey key, Guid leaseId)
    {
        if (!Exists(body))
        {
            _suspendedPresenceBodies.Remove(body);
            return true;
        }

        if (!SuspendPresenceBody(body, key))
            return false;
        if (TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity) &&
            identity.PresenceLeaseId == leaseId)
        {
            ClearPresenceAuthority(identity);
        }
        try
        {
            EntityManager.DeleteEntity(body);
        }
        catch (Exception e)
        {
            Log.Error($"Could not delete stale suspended playable body for {key}: {e}");
            return false;
        }

        return !Exists(body);
    }

    private PendingConsumedSpawnTicket RetainConsumedSpawnTicket(
        SpawnLifecycleTicket ticket,
        EntityUid body = default,
        bool restoreOnPublish = false)
    {
        if (_consumedSpawnLifecycleTickets.TryGetValue(ticket.LeaseId, out var existing))
            return existing;

        var pending = new PendingConsumedSpawnTicket(ticket, body, restoreOnPublish, _timing.CurTime);
        _consumedSpawnLifecycleTickets.Add(ticket.LeaseId, pending);
        return pending;
    }

    private void RetryConsumedSpawnTickets()
    {
        foreach (var pending in _consumedSpawnLifecycleTickets.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryConsumedSpawnTicket(pending);
        }
    }

    private async void RetryConsumedSpawnTicket(PendingConsumedSpawnTicket pending)
    {
        try
        {
            await ExecuteConsumedSpawnTicketAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            if (pending.Body != default)
                SuspendPresenceBody(pending.Body, pending.Ticket.Key);
            Log.Error($"Unexpected consumed fresh-spawn settlement failure for {pending.Ticket.Key}: {e}");
        }
    }

    private async Task ExecuteConsumedSpawnTicketAsync(PendingConsumedSpawnTicket pending)
    {
        var ticket = pending.Ticket;
        if (pending.Running ||
            !_consumedSpawnLifecycleTickets.TryGetValue(ticket.LeaseId, out var retained) ||
            retained != pending)
        {
            return;
        }

        // A different body/control path won while this fresh-spawn publication
        // was awaiting the database. Do not let an old ticket compete with its
        // database work; retain it fail-closed for later exact settlement.
        if (!IsConsumedSpawnTicketControlCurrent(pending))
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        pending.Running = true;
        try
        {
            var current = await ResolveConsumedSpawnAuthorityAsync(ticket);
            if (current == null || IsDefinitiveForeignSpawnAuthority(current, ticket))
            {
                if (!FinishConsumedSpawnTicket(pending, deleteBody: true))
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            if (ticket.PublishAttempted)
            {
                LuaMCharacterPresenceWriteResult result;
                try
                {
                    result = await _db.PublishLuaMCharacterPresenceAsync(ticket.PublishRequest!);
                    if (IsExactPlayablePresence(
                            result.Authority,
                            ticket.Key,
                            ticket.LeaseId,
                            ticket.Authority!.AuthorityLifecycleRevision,
                            snapshotId: null))
                    {
                        current = result.Authority!;
                    }
                    else
                    {
                        current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                            ticket.Key.UserId,
                            ticket.Key.ProfileId,
                            ticket.Key.Slot);
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Consumed fresh-spawn Publish replay failed for {ticket.Key}: {e}");
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return;
                }

                if (current == null || IsDefinitiveForeignSpawnAuthority(current, ticket))
                {
                    if (!FinishConsumedSpawnTicket(pending, deleteBody: true))
                        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return;
                }

                if (IsExactPlayablePresence(
                        current,
                        ticket.Key,
                        ticket.LeaseId,
                        ticket.Authority!.AuthorityLifecycleRevision,
                        snapshotId: null))
                {
                    ticket.PublishedAuthority = current;
                    if (pending.RestoreOnPublish &&
                        Exists(pending.Body) &&
                        TryGetCurrentCharacterKey(ticket.Key.UserId, out var selected) &&
                        selected == ticket.Key &&
                        ticket.SlotGeneration == _preferences.GetCharacterSlotGeneration(ticket.Key.UserId, ticket.Key.Slot) &&
                        TryValidateCanonicalBody(pending.Body, ticket.Key, out _, out _, out _))
                    {
                        if (!HasSafePlayablePresenceWindow(current))
                        {
                            BindPresenceIdentity(pending.Body, ticket.Key, ticket.SlotGeneration, current);
                            SuspendPresenceBody(pending.Body, ticket.Key);
                            _consumedSpawnLifecycleTickets.Remove(ticket.LeaseId);
                            return;
                        }

                        BindPlayableIdentity(pending.Body, ticket.Key, ticket.SlotGeneration, current);
                        if (RestoreSuspendedPresenceBody(pending.Body, ticket.Key))
                        {
                            _consumedSpawnLifecycleTickets.Remove(ticket.LeaseId);
                            return;
                        }

                        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                        return;
                    }

                    await ReleaseConsumedSpawnAuthorityAsync(pending, current);
                    return;
                }

                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            await ReleaseConsumedSpawnAuthorityAsync(pending, current);
        }
        finally
        {
            pending.Running = false;
        }
    }

    private async Task<LuaMCharacterPresenceAuthorityRecord?> ResolveConsumedSpawnAuthorityAsync(
        SpawnLifecycleTicket ticket)
    {
        if (ticket.Authority != null)
        {
            return await _db.GetLuaMCharacterPresenceAuthorityAsync(
                ticket.Key.UserId,
                ticket.Key.ProfileId,
                ticket.Key.Slot);
        }

        LuaMCharacterPresenceWriteResult result;
        if (ticket.ReserveRequest != null)
            result = await _db.ReserveLuaMCharacterPresenceAsync(ticket.ReserveRequest);
        else
            result = await _db.ReclaimLuaMCharacterPresenceAsync(ticket.ReclaimRequest!);

        if (TryAcceptFreshSpawnAuthority(ticket, result.Authority))
            return result.Authority;
        return await _db.GetLuaMCharacterPresenceAuthorityAsync(
            ticket.Key.UserId,
            ticket.Key.ProfileId,
            ticket.Key.Slot);
    }

    private async Task ReleaseConsumedSpawnAuthorityAsync(
        PendingConsumedSpawnTicket pending,
        LuaMCharacterPresenceAuthorityRecord authority)
    {
        var ticket = pending.Ticket;
        var request = ticket.GetOrCreateSettlementRelease(authority, "consumed-spawn-ticket-settlement");
        try
        {
            _ = await _db.ReleaseLuaMCharacterPresenceAsync(request);
            var current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                ticket.Key.UserId,
                ticket.Key.ProfileId,
                ticket.Key.Slot);
            if (current == null || IsDefinitiveForeignSpawnAuthority(current, ticket))
            {
                if (!FinishConsumedSpawnTicket(pending, deleteBody: true))
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Consumed fresh-spawn Release replay failed for {ticket.Key}: {e}");
        }

        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
    }

    private bool FinishConsumedSpawnTicket(PendingConsumedSpawnTicket pending, bool deleteBody)
    {
        if (!deleteBody || pending.Body == default || !Exists(pending.Body))
        {
            _consumedSpawnLifecycleTickets.Remove(pending.Ticket.LeaseId);
            return true;
        }

        if (!SuspendPresenceBody(pending.Body, pending.Ticket.Key))
            return false;
        ClearPresenceAuthority(pending.Body);
        try
        {
            EntityManager.DeleteEntity(pending.Body);
        }
        catch (Exception e)
        {
            Log.Error($"Could not delete consumed fresh-spawn body for {pending.Ticket.Key}: {e}");
            return false;
        }

        if (Exists(pending.Body))
            return false;

        _consumedSpawnLifecycleTickets.Remove(pending.Ticket.LeaseId);
        return true;
    }

    private void RetainUnboundSpawnBodyForDisposal(EntityUid body, NetUserId userId)
    {
        if (!Exists(body))
            return;

        var key = TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity)
            ? new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot)
            // This path deliberately handles a failed identity lookup. Do not
            // retry it while disposing the same unbound SpawnComplete body.
            : new CharacterKey(userId, 0, 0);
        ClearPresenceAuthority(body);
        // An unbound spawn has no durable lease that can safely authorize even
        // one tick of exposure. Delete it before returning from SpawnComplete.
        EntityManager.DeleteEntity(body);
        if (!Exists(body))
            return;

        _unboundSpawnBodiesPendingDisposal.TryAdd(body, key);
        TryFinalizeUnboundSpawnBodyDisposal(body, key);
    }

    private void RetryUnboundSpawnBodyDisposals()
    {
        foreach (var (body, key) in _unboundSpawnBodiesPendingDisposal.ToArray())
        {
            TryFinalizeUnboundSpawnBodyDisposal(body, key);
        }
    }

    private bool TryFinalizeUnboundSpawnBodyDisposal(EntityUid body, CharacterKey key)
    {
        if (Exists(body) && !DisposeStalePresenceBody(body, key, Guid.Empty))
            return false;

        _unboundSpawnBodiesPendingDisposal.Remove(body);
        return true;
    }

    private void RetryPendingStores()
    {
        foreach (var pending in _pendingStores.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryPendingStore(pending);
        }
    }

    private async void RetryPendingStore(PendingStoreOperation pending)
    {
        try
        {
            await ExecutePendingStoreAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            Log.Error($"Unexpected deep-cryo store retry failure for {pending.Key}: {e}");
        }
    }

    private void RetryDetachedRoundSettlements()
    {
        foreach (var settlement in _detachedRoundSettlements.Values.ToArray())
        {
            if (!settlement.Running && settlement.NextAttempt <= _timing.CurTime)
                RetryDetachedRoundSettlement(settlement);
        }
    }

    private async void RetryDetachedRoundSettlement(DetachedRoundSettlement settlement)
    {
        try
        {
            await ExecuteDetachedRoundSettlementAsync(settlement);
        }
        catch (Exception e)
        {
            settlement.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            Log.Error($"Unexpected detached deep-cryo settlement failure for {settlement.Handle.Key}: {e}");
        }
    }

    private DetachedRoundSettlement CreateDetachedRoundSettlement(
        LuaMDeepCryoRestoreHandle handle,
        LuaMDeepCryoPublicationReceipt? receipt,
        PendingAuthorizationOperation? pendingAuthorization,
        PendingPublicationOperation? pendingPublication,
        PendingRestoreAbort? pendingAbort)
    {
        _terminalCleanupRegistrations.TryGetValue(handle.LeaseId, out var terminalCleanup);
        if (pendingPublication != null)
        {
            var kind = pendingPublication.Kind switch
            {
                PendingPublicationKind.Acknowledge => DetachedSettlementKind.Acknowledge,
                PendingPublicationKind.Quarantine => DetachedSettlementKind.Quarantine,
                _ => DetachedSettlementKind.Rollback,
            };
            return new DetachedRoundSettlement(
                handle,
                receipt!,
                null,
                pendingAuthorization,
                terminalCleanup,
                kind,
                _timing.CurTime,
                pendingPublication.AcknowledgementCommitted,
                pendingPublication.AcknowledgementCompensated,
                pendingPublication.AcknowledgedAuthority);
        }

        if (pendingAuthorization != null)
        {
            return new DetachedRoundSettlement(
                handle,
                receipt!,
                null,
                pendingAuthorization,
                terminalCleanup,
                DetachedSettlementKind.ResolveAuthorization,
                _timing.CurTime);
        }

        if (receipt != null && _authorizedPublications.Contains(handle.Key))
        {
            return new DetachedRoundSettlement(
                handle,
                receipt,
                null,
                null,
                terminalCleanup,
                DetachedSettlementKind.Quarantine,
                _timing.CurTime);
        }

        if (pendingAbort != null)
        {
            return new DetachedRoundSettlement(
                handle,
                receipt,
                pendingAbort.Request,
                null,
                terminalCleanup,
                DetachedSettlementKind.Abort,
                _timing.CurTime);
        }

        if (receipt != null)
        {
            return new DetachedRoundSettlement(
                handle,
                receipt,
                null,
                null,
                terminalCleanup,
                DetachedSettlementKind.Rollback,
                _timing.CurTime);
        }

        var abortRequest = new LuaMDeepCryoAbortRequest(
            Guid.NewGuid(),
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            handle.Revision,
            handle.LeaseId,
            "round-cleanup-before-publication-prepare",
            DateTime.UtcNow);
        return new DetachedRoundSettlement(
            handle,
            null,
            abortRequest,
            null,
            terminalCleanup,
            DetachedSettlementKind.Abort,
            _timing.CurTime);
    }

    private void DetachRestoreState(
        LuaMDeepCryoRestoreHandle handle,
        LuaMDeepCryoPublicationReceipt? receipt,
        PendingAuthorizationOperation? pendingAuthorization,
        PendingPublicationOperation? pendingPublication,
        PendingRestoreAbort? pendingAbort)
    {
        var key = handle.Key;
        if (_activeRestores.TryGetValue(key, out var lease) && lease == handle.LeaseId)
            _activeRestores.Remove(key);
        if (_restoreHandles.TryGetValue(key, out var retainedHandle) && retainedHandle.LeaseId == handle.LeaseId)
            _restoreHandles.Remove(key);
        if (receipt != null &&
            _publicationReceipts.TryGetValue(key, out var retainedReceipt) &&
            retainedReceipt == receipt)
        {
            _publicationReceipts.Remove(key);
        }

        if (pendingAuthorization != null &&
            _pendingAuthorizationOperations.TryGetValue(key, out var retainedAuthorization) &&
            retainedAuthorization == pendingAuthorization)
        {
            _pendingAuthorizationOperations.Remove(key);
        }

        if (pendingPublication != null &&
            _pendingPublicationOperations.TryGetValue(key, out var retainedPublication) &&
            retainedPublication == pendingPublication)
        {
            _pendingPublicationOperations.Remove(key);
        }

        if (pendingAbort != null &&
            _pendingRestoreAborts.TryGetValue(key, out var retainedAbort) &&
            retainedAbort == pendingAbort)
        {
            _pendingRestoreAborts.Remove(key);
        }

        _consumeStarted.Remove(key);
        _authorizedPublications.Remove(key);
        _terminalCleanupRegistrations.Remove(handle.LeaseId);
    }

    private void DeleteDetachedRoundBody(LuaMDeepCryoRestoreHandle handle)
    {
        if (_liveBodies.TryGetValue(handle.Key, out var liveBody))
        {
            _liveBodies.Remove(handle.Key);
            if (Exists(liveBody))
            {
                ClearPresenceAuthority(liveBody);
                QueueDel(liveBody);
            }
        }

        if (handle.Body != EntityUid.Invalid && handle.Body != liveBody && Exists(handle.Body))
        {
            ClearPresenceAuthority(handle.Body);
            QueueDel(handle.Body);
        }
    }

    private async Task ExecuteDetachedRoundSettlementAsync(DetachedRoundSettlement settlement)
    {
        if (settlement.Running ||
            !_detachedRoundSettlements.TryGetValue(settlement.Handle.LeaseId, out var current) ||
            current != settlement)
        {
            return;
        }

        settlement.Running = true;
        try
        {
            var terminal = false;
            switch (settlement.Kind)
            {
                case DetachedSettlementKind.Abort:
                    terminal = await TryAbortDetachedClaimAsync(settlement.Handle, settlement.AbortRequest!);
                    break;
                case DetachedSettlementKind.Rollback:
                    terminal = await TryRollbackPreparedPublicationAsync(
                            settlement.Receipt!,
                            "round-cleanup-before-publication-authorization") != null;
                    break;
                case DetachedSettlementKind.ResolveAuthorization:
                {
                    var outcome = await TryAuthorizePreparedPublicationAsync(settlement.Receipt!);
                    if (outcome == AuthorizationAttemptStatus.Authorized)
                        settlement.Kind = DetachedSettlementKind.Quarantine;
                    else if (outcome == AuthorizationAttemptStatus.Rejected)
                        settlement.Kind = DetachedSettlementKind.Rollback;
                    else if (outcome == AuthorizationAttemptStatus.Terminal)
                        terminal = true;
                    break;
                }
                case DetachedSettlementKind.Acknowledge:
                {
                    if (settlement.AcknowledgementCompensated)
                    {
                        terminal = true;
                        break;
                    }

                    var acknowledgement = await TrySettlePublicationAcknowledgementAsync(
                        settlement.Receipt!,
                        settlement.AcknowledgementCommitted,
                        settlement.AcknowledgedAuthority,
                        () => false,
                        detached: true);
                    settlement.AcknowledgementCommitted = acknowledgement.AcknowledgementCommitted;
                    settlement.AcknowledgedAuthority = acknowledgement.Authority;
                    settlement.AcknowledgementCompensated =
                        acknowledgement.Status == AcknowledgementSettlementStatus.Compensated;
                    terminal = settlement.AcknowledgementCompensated;
                    break;
                }
                case DetachedSettlementKind.Quarantine:
                    terminal = await TryQuarantineAuthorizedPublicationAsync(
                        settlement.Receipt!,
                        "round-cleanup-after-publication-authorization");
                    break;
            }

            if (!terminal && settlement.Kind != DetachedSettlementKind.Acknowledge)
                terminal = await IsDetachedRoundSettlementTerminalAsync(settlement);

            if (!terminal)
            {
                settlement.NextAttempt = settlement.Kind == DetachedSettlementKind.ResolveAuthorization ||
                                         _inFlightClaimLeases.Contains(settlement.Handle.LeaseId)
                    ? _timing.CurTime + PublicationRetryInterval
                    : _timing.CurTime;
                return;
            }

            _detachedRoundSettlements.Remove(settlement.Handle.LeaseId);
            _acknowledgedQuarantineLeases.Remove(settlement.Handle.LeaseId);
            _acknowledgedQuarantineRequests.Remove(settlement.Handle.LeaseId);
            _acknowledgedQuarantineRenewals.Remove(settlement.Handle.LeaseId);
            InvokeTerminalCleanup(settlement.TerminalCleanup, settlement.Handle.Key);
            settlement.PendingAuthorization?.Completion.TrySetResult(LuaMDeepCryoAuthorizationStatus.Terminal);
        }
        finally
        {
            settlement.Running = false;
        }
    }

    private async Task<bool> TryAbortDetachedClaimAsync(
        LuaMDeepCryoRestoreHandle handle,
        LuaMDeepCryoAbortRequest request)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.AbortLuaMDeepCryoRestoreAsync(request);
                if (IsConfirmedAbortedRestore(result, handle, handle.Revision + 1))
                    return true;
            }
            catch (Exception e)
            {
                Log.Error($"Detached deep-cryo abort attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        return false;
    }

    private void InvokeTerminalCleanup(
        TerminalCleanupRegistration? registration,
        CharacterKey key)
    {
        if (registration == null || !registration.TryTake(out var callback))
            return;

        try
        {
            callback?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error($"Detached deep-cryo terminal cleanup failed for {key}: {e}");
        }
    }

    private async Task<bool> IsDetachedRoundSettlementTerminalAsync(DetachedRoundSettlement settlement)
    {
        // Capture this before the await. A delayed CLAIM can commit immediately
        // after the re-read returns its pre-CLAIM Stored row.
        var claimWasInFlight = _inFlightClaimLeases.Contains(settlement.Handle.LeaseId);
        try
        {
            var current = await _db.GetLuaMDeepCryoStorePreconditionAsync(
                settlement.Handle.Key.UserId,
                settlement.Handle.Key.ProfileId,
                settlement.Handle.Key.Slot);
            if (current == null)
                return false;
            if (IsDefinitiveForeignRestoreAuthority(current.Authority, settlement.Handle))
                return true;

            var snapshot = current.ActiveSnapshot;
            if (snapshot == null ||
                snapshot.Id != settlement.Handle.SnapshotId ||
                snapshot.LeaseId != null)
            {
                return false;
            }

            if (claimWasInFlight &&
                snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Stored &&
                snapshot.Revision == settlement.Handle.Revision - 1)
            {
                // This is only the provisional CLAIM precondition. The in-flight
                // exact operation can still turn it into Restoring after cleanup.
                return false;
            }

            return settlement.Kind switch
            {
                DetachedSettlementKind.Abort =>
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Stored &&
                    (snapshot.Revision == settlement.Handle.Revision - 1 ||
                     snapshot.Revision == settlement.Handle.Revision + 1),
                DetachedSettlementKind.Rollback =>
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Stored &&
                    settlement.Receipt != null &&
                    snapshot.Revision == settlement.Receipt.PreparedRevision + 1,
                DetachedSettlementKind.ResolveAuthorization or DetachedSettlementKind.Quarantine =>
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined &&
                    settlement.Receipt != null &&
                    snapshot.Revision == settlement.Receipt.AuthorizedRevision + 1,
                DetachedSettlementKind.Acknowledge =>
                    snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined &&
                    settlement.Receipt != null &&
                    snapshot.Revision == settlement.Receipt.AuthorizedRevision + 2,
                _ => false,
            };
        }
        catch (Exception e)
        {
            Log.Error($"Detached deep-cryo settlement re-read failed for {settlement.Handle.Key}: {e}");
            return false;
        }
    }

    private void RetryPendingRestoreAborts()
    {
        foreach (var pending in _pendingRestoreAborts.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryPendingRestoreAbort(pending);
        }
    }

    private async void RetryPendingRestoreAbort(PendingRestoreAbort pending)
    {
        try
        {
            await ExecutePendingRestoreAbortAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            Log.Error($"Unexpected deep-cryo abort retry failure for {pending.Handle.Key}: {e}");
        }
    }

    private void RetryPendingAuthorizationOperations()
    {
        foreach (var pending in _pendingAuthorizationOperations.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryPendingAuthorizationOperation(pending);
        }
    }

    private async void RetryPendingAuthorizationOperation(PendingAuthorizationOperation pending)
    {
        try
        {
            await ExecutePendingAuthorizationOperationAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            Log.Error($"Unexpected deep-cryo authorization retry failure for {pending.Receipt.Handle.Key}: {e}");
        }
    }

    private void RetryPendingPublicationOperations()
    {
        foreach (var pending in _pendingPublicationOperations.Values.ToArray())
        {
            if (!pending.Running && pending.NextAttempt <= _timing.CurTime)
                RetryPendingPublicationOperation(pending);
        }
    }

    private async void RetryPendingPublicationOperation(PendingPublicationOperation pending)
    {
        try
        {
            await ExecutePendingPublicationOperationAsync(pending);
        }
        catch (Exception e)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            Log.Error($"Unexpected deep-cryo publication retry failure for {pending.Receipt.Handle.Key}: {e}");
        }
    }

    private async void RecoverExpiredLeases()
    {
        if (_leaseRecoveryRunning)
            return;

        _leaseRecoveryRunning = true;
        _nextLeaseRecovery = _timing.CurTime + TimeSpan.FromMinutes(1);
        try
        {
            // Protect only leases still owned by this round. Detached leases must
            // remain recoverable: their exact settlement re-reads the terminal
            // no-lease state instead of keeping an old generation alive forever.
            var protectedLeases = _activeRestores.Values.Distinct().ToArray();
            var recovered = await _db.RecoverExpiredLuaMDeepCryoLeasesAsync(
                DateTime.UtcNow,
                protectedLeases);
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
        // SpawnComplete cannot wait for durable publication. Keep the body fenced
        // until the existing async settlement queue proves playable authority.
        var hasTicket = _spawnLifecycleTickets.Remove(ev.Player.UserId, out var ticket);
        if (!hasTicket || ticket == null ||
            !TryGetCurrentCharacterKey(ev.Player.UserId, out var key) ||
            ticket.Key != key ||
            ticket.SlotGeneration != _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot) ||
            !Exists(ev.Mob) || !TryValidateCanonicalBody(ev.Mob, key, out _, out _, out _))
        {
            Log.Error($"Could not bind exact deep-cryo lifecycle authority to {ev.Player.UserId} after spawn.");
            if (ticket != null)
            {
                if (Exists(ev.Mob))
                {
                    ClearPresenceAuthority(ev.Mob);
                    EntityManager.DeleteEntity(ev.Mob);
                }
                RetainConsumedSpawnTicket(ticket);
            }
            else
            {
                RetainUnboundSpawnBodyForDisposal(ev.Mob, ev.Player.UserId);
            }
            return;
        }

        ticket.PublishAttempted = true;
        BindPresenceIdentity(ev.Mob, key, ticket.SlotGeneration, ticket.Authority!);
        if (!SuspendPresenceBody(ev.Mob, key))
            Log.Error($"Could not fully suspend fresh-spawn publication for {key}; retaining its exact durable settlement fence.");
        RetainConsumedSpawnTicket(ticket, ev.Mob, restoreOnPublish: true);
    }

    /// <summary>
    /// PlayerBeforeSpawn cannot await a durable authority transition. It handles
    /// this attempt and retries normal spawning only after FreshReserved is proven.
    /// </summary>
    private void OnPlayerBeforeSpawn(PlayerBeforeSpawnEvent ev)
    {
        if (!TryGetCurrentCharacterKey(ev.Player.UserId, out var key))
        {
            Log.Error($"Blocked fresh spawn for {ev.Player.UserId}: stable deep-cryo identity is unresolved.");
            BlockFreshSpawnToObserver(ev);
            return;
        }

        var slotGeneration = _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot);
        if (_spawnLifecycleTickets.TryGetValue(ev.Player.UserId, out var existing))
        {
            if (existing.Key == key && existing.SlotGeneration == slotGeneration && existing.Authority != null)
                return;
            BlockFreshSpawnToObserver(ev);
            return;
        }

        if (_pendingStoreKeys.ContainsKey(key) || IsRestorePublicationActive(key) ||
            _detachedRoundSettlements.Values.Any(settlement => settlement.Handle.Key == key) ||
            !_discardPending.Add(key))
        {
            BlockFreshSpawnToObserver(ev);
            return;
        }

        BlockFreshSpawnToObserver(ev);
        PrepareFreshSpawnAsync(new PendingFreshSpawnRequest(
            ev.Player, key, slotGeneration, ev.JobId, ev.Station));
    }

    private bool IsConsumedSpawnTicketControlCurrent(PendingConsumedSpawnTicket pending)
    {
        if (!_players.TryGetSessionById(pending.Ticket.Key.UserId, out var session))
            return true;
        if (pending.Body == default)
            return session.AttachedEntity == null;
        if (session.AttachedEntity == pending.Body)
            return true;

        return _suspendedPresenceBodies.TryGetValue(pending.Body, out var suspension) &&
               suspension.ControlGhost == session.AttachedEntity;
    }

    private void BlockFreshSpawnToObserver(PlayerBeforeSpawnEvent ev)
    {
        ev.Handled = true;
        // The ticker deliberately does not create a mob for a handled spawn.
        // Keep the player attached to an observer while the durable authority
        // sequence runs, rather than leaving the session without an entity.
        if (ev.Player.AttachedEntity == null)
            _gameTicker.JoinAsObserver(ev.Player);
    }

    private async void PrepareFreshSpawnAsync(PendingFreshSpawnRequest pending)
    {
        var key = pending.Key;
        try
        {
            var precondition = await _db.GetLuaMDeepCryoStorePreconditionAsync(key.UserId, key.ProfileId, key.Slot);
            if (precondition == null)
            {
                Log.Error($"Blocked fresh spawn for {key}: active profile lifecycle is unavailable.");
                return;
            }

            if (precondition.ActiveSnapshot is { Status: DbLuaMDeepCryoSnapshotStatus.Stored } snapshot)
            {
                var discarded = await _db.DiscardLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoDiscardRequest(
                    Guid.NewGuid(), key.UserId, key.ProfileId, key.Slot, snapshot.Id, snapshot.Revision,
                    snapshot.LeaseId, DateTime.UtcNow));
                if (!discarded.Success && discarded.Status != LuaMDeepCryoWriteStatus.NotFound)
                {
                    Log.Error($"Blocked fresh spawn for {key}: durable cryo discard returned {discarded.Status}.");
                    return;
                }
                ForgetLiveBody(key, deleteBody: true);
                precondition = await _db.GetLuaMDeepCryoStorePreconditionAsync(key.UserId, key.ProfileId, key.Slot);
            }

            if (precondition == null || precondition.ActiveSnapshot != null ||
                !TryGetCurrentCharacterKey(key.UserId, out var current) || current != key ||
                pending.SlotGeneration != _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot))
            {
                Log.Error($"Blocked fresh spawn for {key}: exact playable lifecycle epoch is unavailable.");
                return;
            }

            var now = DateTime.UtcNow;
            var leaseId = Guid.NewGuid();
            SpawnLifecycleTicket ticket;
            if (precondition.Authority is { } authority)
            {
                if (authority.Phase is not (DbLuaMCharacterPresencePhase.FreshReserved or DbLuaMCharacterPresencePhase.Playable) || authority.ExpiresAtUtc > now)
                {
                    Log.Warning($"Blocked fresh spawn for {key}: durable presence authority {authority.Phase} is active.");
                    return;
                }
                ticket = new SpawnLifecycleTicket(key, pending.SlotGeneration, leaseId, reclaimRequest:
                    new LuaMCharacterPresenceReclaimRequest(Guid.NewGuid(), key.UserId, key.ProfileId, key.Slot,
                        authority.LeaseId, leaseId, authority.Phase, authority.SnapshotId, authority.Revision,
                        authority.AuthorityLifecycleRevision, _serverInstanceId, _gameTicker.RoundId, now,
                        now + PlayablePresenceLeaseDuration));
            }
            else
            {
                ticket = new SpawnLifecycleTicket(key, pending.SlotGeneration, leaseId, reserveRequest:
                    new LuaMCharacterPresenceReserveRequest(Guid.NewGuid(), key.UserId, key.ProfileId, key.Slot,
                        leaseId, _serverInstanceId, _gameTicker.RoundId, now, now + PlayablePresenceLeaseDuration,
                        precondition.LifecycleRevision));
            }

            _spawnLifecycleTickets[key.UserId] = ticket;
            var authorityResult = await ResolveConsumedSpawnAuthorityAsync(ticket);
            if (!TryAcceptFreshSpawnAuthority(ticket, authorityResult) ||
                !TryGetCurrentCharacterKey(key.UserId, out current) || current != key ||
                pending.SlotGeneration != _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot))
            {
                _spawnLifecycleTickets.Remove(key.UserId);
                RetainConsumedSpawnTicket(ticket);
                return;
            }

            if (_freshSpawnReadyHookForTests?.Invoke() != true)
                _gameTicker.MakeJoinGame(pending.Player, pending.Station, pending.JobId);
        }
        catch (Exception e)
        {
            Log.Error($"Blocked fresh spawn for {key}: durable authority preparation failed: {e}");
        }
        finally
        {
            _discardPending.Remove(key);
        }
    }

    private static bool IsDefinitiveForeignSpawnAuthority(
        LuaMCharacterPresenceAuthorityRecord? authority,
        SpawnLifecycleTicket ticket)
    {
        var expectedEpoch = ticket.Authority?.AuthorityLifecycleRevision ??
                            ticket.ReserveRequest?.ExpectedLifecycleRevision ??
                            ticket.ReclaimRequest!.ExpectedAuthorityLifecycleRevision;
        return authority != null &&
               authority.ProfileId == ticket.Key.ProfileId &&
               authority.LeaseId != ticket.LeaseId &&
               authority.AuthorityLifecycleRevision > expectedEpoch;
    }

    private bool TryAcceptFreshSpawnAuthority(
        SpawnLifecycleTicket ticket,
        LuaMCharacterPresenceAuthorityRecord? authority)
    {
        if (authority is not
            {
                Phase: DbLuaMCharacterPresencePhase.FreshReserved,
                SnapshotId: null,
            } ||
            authority.ProfileId != ticket.Key.ProfileId ||
            authority.LeaseId != ticket.LeaseId ||
            authority.AuthorityLifecycleRevision < 0)
        {
            return false;
        }

        if (ticket.Authority != null &&
            ticket.Authority.AuthorityLifecycleRevision != authority.AuthorityLifecycleRevision)
        {
            return false;
        }

        ticket.BindReservation(authority, PlayablePresenceLeaseDuration);
        return true;
    }

    public bool IsPersistentBody(EntityUid body)
    {
        return HasComp<LuaMDeepCryoPendingComponent>(body) ||
               HasComp<LuaMDeepCryoStoredComponent>(body) ||
               HasComp<LuaMDeepCryoPresenceSuspendedComponent>(body);
    }

    public bool IsPresenceSuspended(EntityUid body)
    {
        return HasComp<LuaMDeepCryoPresenceSuspendedComponent>(body);
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

    public bool IsExactRestoreBody(LuaMDeepCryoRestoreHandle handle)
    {
        return Exists(handle.Body) &&
               TryComp<LuaMDeepCryoIdentityComponent>(handle.Body, out var identity) &&
               identity.UserId == handle.Key.UserId &&
               identity.ProfileId == handle.Key.ProfileId &&
               identity.Slot == handle.Key.Slot &&
               TryComp<LuaMDeepCryoStoredComponent>(handle.Body, out var stored) &&
               stored.UserId == handle.Key.UserId &&
               stored.ProfileId == handle.Key.ProfileId &&
               stored.Slot == handle.Key.Slot &&
               stored.SnapshotId == handle.SnapshotId &&
               stored.Revision == handle.Revision;
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
            if (requestedUser is { } unresolvedUser &&
                TryGetCurrentCharacterKey(unresolvedUser, out var unresolvedKey))
            {
                SuspendPresenceBody(body, unresolvedKey);
            }
            reason = "stable-character-identity-missing";
            return false;
        }

        if (requestedUser != null && requestedUser.Value != identity.UserId)
        {
            reason = "body-owner-mismatch";
            return false;
        }

        var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
        if (_pendingStoreKeys.ContainsKey(key) ||
            _pendingPresenceRenewals.ContainsKey(key) ||
            IsRestorePublicationActive(key) ||
            _discardPending.Contains(key))
        {
            reason = "character-lifecycle-operation-pending";
            return false;
        }
        if (HasComp<LuaMDeepCryoPresenceSuspendedComponent>(body))
        {
            reason = "playable-presence-suspended";
            return false;
        }
        if (!TryValidateCanonicalBody(body, key, out _, out _, out reason))
            return false;

        if (identity.LifecycleRevision < 0 ||
            identity.PresenceLeaseId == Guid.Empty ||
            identity.PresenceLeaseRevision < 0 ||
            identity.PresencePhase != DbLuaMCharacterPresencePhase.Playable)
        {
            // A playable body without a database-bound authority epoch can
            // never be safely stored: re-baselining here could accept a stale
            // cross-server copy after another lifecycle has already completed.
            Log.Error($"Deep-cryo Store rejected unbound lifecycle authority for {key}; retaining the body fail-closed for recovery.");
            SuspendPresenceBody(body, key);
            reason = "stable-character-lifecycle-unbound";
            return false;
        }

        // Copy the body-bound epoch before serialization. Payload capture may
        // be expensive and a competing server can complete Store-to-ACK while
        // it runs; the immutable request must still CAS against the old epoch.
        var expectedLifecycleRevision = identity.LifecycleRevision;
        var expectedPresenceLeaseId = identity.PresenceLeaseId;

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

        var build = _configuration.GetCVar(CVars.BuildVersion);
        if (string.IsNullOrWhiteSpace(build))
            build = "unknown";
        if (build.Length > LuaMDeepCryoLimits.MaxBuildVersionLength)
            build = build[..LuaMDeepCryoLimits.MaxBuildVersionLength];

        var request = new LuaMDeepCryoStoreRequest(
            pending.OperationId,
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
            DateTime.UtcNow,
            expectedLifecycleRevision,
            expectedPresenceLeaseId);
        var operation = new PendingStoreOperation(
            body,
            pod,
            key,
            identity.SlotGeneration,
            request,
            source,
            completion,
            _roundCleanupGeneration,
            _timing.CurTime);
        _pendingStores.Add(request.OperationId, operation);
        _pendingStoreKeys.Add(key, request.OperationId);
        RetryPendingStore(operation);
        return true;
    }

    private async Task ExecutePendingStoreAsync(PendingStoreOperation pending)
    {
        if (pending.Running ||
            !_pendingStores.TryGetValue(pending.OperationId, out var retained) ||
            retained != pending)
        {
            return;
        }

        pending.Running = true;
        try
        {
            if (pending.NoCommitCallbackFailed &&
                !pending.Detached &&
                pending.RoundCleanupGeneration == _roundCleanupGeneration &&
                Exists(pending.Body))
            {
                await MaintainFailedNoCommitCallbackFenceAsync(pending);
                return;
            }

            // If that one-shot callback deleted the body before throwing, the
            // immutable captured payload is now the only recoverable state.
            // Fall through to exact Store replay; the missing-body validation
            // path can only finalize silently and can never invoke the callback
            // a second time.
            if (pending.NoCommitCallbackFailed && !Exists(pending.Body))
            {
                pending.NoCommitCallbackFailed = false;
                pending.NoCommitFailureReason = null;
            }

            if (pending.NoCommitFailureReason != null && Exists(pending.Body))
            {
                await ResolveProvenNoCommitStoreFailureAsync(
                    pending,
                    pending.NoCommitFailureReason);
                return;
            }

            if (pending.CallbackFailed)
            {
                // A one-shot success or proven-no-commit callback already failed.
                // Retries never touch it again or depend on DB health; they only
                // neutralize its possibly partially published live body.
                if (!TryDeleteInvalidCommittedStoreBody(pending))
                {
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return;
                }

                FinalizeInvalidCommittedStore(pending);
                return;
            }

            var request = pending.Request;

            LuaMDeepCryoWriteResult? result = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var candidate = await _db.StoreLuaMDeepCryoSnapshotAsync(request);
                    if (candidate.Status == LuaMDeepCryoWriteStatus.UnknownOutcome)
                    {
                        Log.Error($"Deep-cryo store attempt {attempt + 1} for {pending.Key} returned an unknown outcome; replaying its exact operation proof.");
                        continue;
                    }

                    if (candidate.Status == LuaMDeepCryoWriteStatus.RevisionConflict &&
                        !IsAuthoritativeStoreConflict(candidate, pending))
                    {
                        // A cross-server Store winner can race us between the
                        // initial active-snapshot read and the commit. Never run
                        // the caller's world rollback from a proofless CAS result:
                        // an authoritative snapshot may already own the character.
                        // Exact replay (and later retries) must first recover the
                        // winner's active snapshot proof.
                        Log.Error($"Deep-cryo store attempt {attempt + 1} for {pending.Key} returned a revision conflict without authoritative snapshot proof; keeping the body fenced and re-reading.");
                        continue;
                    }

                    result = candidate;
                    break;
                }
                catch (Exception e)
                {
                    Log.Error($"Deep-cryo store attempt {attempt + 1} failed for {pending.Key}; replaying its exact operation proof: {e}");
                }
            }

            if (result == null ||
                result.Success && (result.SnapshotId == null || result.Revision == null))
            {
                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            if (!_pendingStores.TryGetValue(pending.OperationId, out retained) || retained != pending)
                return;

            var confirmedCurrentStore = IsConfirmedCurrentStore(result, pending);
            if (result.Success && !confirmedCurrentStore)
            {
                // The immutable Store proof is real, but the snapshot has since
                // advanced. That later lifecycle owns authority; never finalize
                // the historical live body or invoke its old caller callback.
                ClearPresenceAuthorityIfMatches(pending.Body, pending.Request.ExpectedPresenceLeaseId);
                if (!TryFinalizeSilentStore(pending))
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            if (IsAuthoritativeStoreConflict(result, pending))
            {
                // The requested Store did not commit, but the database proved
                // that another durable lifecycle already owns this character.
                // Returning the fenced body through a failure callback would
                // expose a second authoritative copy, so dispose it silently.
                ClearPresenceAuthorityIfMatches(pending.Body, pending.Request.ExpectedPresenceLeaseId);
                Log.Error($"Deep-cryo store for {pending.Key} proved an authoritative lifecycle conflict " +
                          $"with snapshot evidence {result.SnapshotId} in {result.SnapshotStatus}; deleting the conflicting live body.");
                if (!TryFinalizeSilentStore(pending))
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            if (pending.Detached || pending.RoundCleanupGeneration != _roundCleanupGeneration)
            {
                if (confirmedCurrentStore)
                {
                    if (!TryFinalizeSilentStore(pending))
                        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                }
                else
                {
                    // Round cleanup will flush the live entity, but this process
                    // retains its immutable payload, epoch and CharacterKey fence.
                    // A proven no-commit result is not authority to forget the
                    // only captured character state; keep exact resolution alive.
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                }

                return;
            }

            if (!TryRevalidatePendingBody(pending, out var invalidReason))
            {
                if (result.Success)
                {
                    // The exact Store is now the sole durable authority. Never
                    // depend on a second DB mutation to make an escaped live body
                    // safe: destroy it synchronously and suppress the rollback
                    // callback that would otherwise reattach/eject a duplicate.
                    if (!TryDeleteInvalidCommittedStoreBody(pending))
                    {
                        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                        Log.Error($"Deep-cryo committed store for {pending.Key} could not synchronously delete its invalid live body; retaining the character fence.");
                        return;
                    }

                    FinalizeInvalidCommittedStore(pending);
                    return;
                }

                await ResolveProvenNoCommitStoreFailureAsync(pending, invalidReason);
                return;
            }

            if (!result.Success || result.SnapshotId is not { } snapshotId || result.Revision is not { } revision)
            {
                await ResolveProvenNoCommitStoreFailureAsync(
                    pending,
                    $"database-store-{result.Status}");
                return;
            }

            // The body is still fenced with the exact token that committed Store.
            // Clear its local playable authority only after this revalidation.
            ClearPresenceAuthorityIfMatches(pending.Body, pending.Request.ExpectedPresenceLeaseId);
            var stored = EnsureComp<LuaMDeepCryoStoredComponent>(pending.Body);
            stored.UserId = pending.Key.UserId;
            stored.ProfileId = pending.Key.ProfileId;
            stored.Slot = pending.Key.Slot;
            stored.SnapshotId = snapshotId;
            stored.Revision = revision;
            _liveBodies[pending.Key] = pending.Body;

            var completionSucceeded = false;
            try
            {
                pending.Completion(new LuaMDeepCryoStoreCompletion(true, snapshotId, revision, null));

                if (Exists(pending.Body) &&
                    TryComp<LuaMDeepCryoPendingComponent>(pending.Body, out var current) &&
                    current.OperationId == pending.OperationId)
                {
                    RemComp<LuaMDeepCryoPendingComponent>(pending.Body);
                }

                completionSucceeded = true;
            }
            catch (Exception e)
            {
                pending.CallbackFailed = true;
                // The durable snapshot is authoritative. If finalization fails,
                // synchronously prove the live authority gone before releasing
                // the character key. QueueDel alone leaves a same-tick duplicate.
                Log.Error($"Deep-cryo post-store callback failed for {pending.Key}; deleting fenced live body synchronously: {e}");
                if (!TryDeleteInvalidCommittedStoreBody(pending))
                {
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    Log.Error($"Deep-cryo post-store callback failure for {pending.Key} could not delete its live body; retaining the character fence.");
                    return;
                }

                if (_liveBodies.TryGetValue(pending.Key, out var liveBody) && liveBody == pending.Body)
                    _liveBodies.Remove(pending.Key);
            }
            finally
            {
                if (completionSucceeded || !Exists(pending.Body))
                {
                    // Caller finalization is part of the same character lifecycle.
                    // Release the key only after callback success or synchronous
                    // proof that its partially published body is gone.
                    RemovePendingStore(pending);
                }
            }
        }
        finally
        {
            pending.Running = false;
        }
    }

    private void FinalizeInvalidCommittedStore(PendingStoreOperation pending)
    {
        if (Exists(pending.Body))
            throw new InvalidOperationException("Committed deep-cryo body remained live after synchronous deletion proof.");

        RemovePendingStore(pending);
        if (_liveBodies.TryGetValue(pending.Key, out var liveBody) && liveBody == pending.Body)
            _liveBodies.Remove(pending.Key);
    }

    private bool TryDeleteInvalidCommittedStoreBody(PendingStoreOperation pending)
    {
        if (!Exists(pending.Body))
            return true;

        try
        {
            EntityManager.DeleteEntity(pending.Body);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo synchronous duplicate-body deletion failed for {pending.Key}: {e}");
            return false;
        }

        return !Exists(pending.Body);
    }

    private bool TryFinalizeSilentStore(PendingStoreOperation pending)
    {
        if (!TryDeleteInvalidCommittedStoreBody(pending))
            return false;

        FinalizeInvalidCommittedStore(pending);
        return true;
    }

    private async Task ResolveProvenNoCommitStoreFailureAsync(
        PendingStoreOperation pending,
        string reason)
    {
        pending.NoCommitFailureReason ??= reason;
        LuaMDeepCryoStorePrecondition? current;
        try
        {
            current = await _db.GetLuaMDeepCryoStorePreconditionAsync(
                pending.Key.UserId,
                pending.Key.ProfileId,
                pending.Key.Slot);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo no-commit proof read failed for {pending.Key}; keeping the body fenced: {e}");
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        if (pending.Detached || pending.RoundCleanupGeneration != _roundCleanupGeneration)
        {
            // The round will flush the entity, but the captured payload and
            // immutable epoch are still the only recoverable character state.
            // Keep the operation/key and let the detached exact Store settle it.
            pending.NextAttempt = _timing.CurTime;
            return;
        }

        if (current == null)
        {
            SuspendPresenceBody(pending.Body, pending.Key);
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        if (!HasExactNoCommitStoreAuthority(current, pending))
        {
            if (CanProveNoCommitBodySuperseded(current, pending))
            {
                Log.Error($"Deep-cryo Store failure for {pending.Key} observed a durable newer lifecycle; deleting the stale fenced body.");
                if (!TryFinalizeSilentStore(pending))
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            }
            else
            {
                // Absence, a lone revision mismatch, or an otherwise ambiguous
                // read can never justify destroying the sole retained body.
                SuspendPresenceBody(pending.Body, pending.Key);
                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            }
            return;
        }

        if (!_pendingStores.TryGetValue(pending.OperationId, out var retained) || retained != pending)
            return;

        var playableAuthority = current.Authority!;
        if (!HasSafePlayablePresenceWindow(playableAuthority))
        {
            SuspendPresenceBody(pending.Body, pending.Key);
            var renewed = await TryRenewNoCommitStorePresenceAsync(pending, playableAuthority);
            if (renewed == null)
                return;

            playableAuthority = renewed;
            if (!HasSafePlayablePresenceWindow(playableAuthority))
            {
                // This exact cycle committed, but its reply arrived too late.
                // Start a new immutable renewal from the confirmed revision.
                pending.NoCommitRenewRequest = null;
                pending.NextAttempt = _timing.CurTime;
                return;
            }
        }

        if (!Exists(pending.Body) ||
            !TryComp<LuaMDeepCryoIdentityComponent>(pending.Body, out var identity) ||
            identity.PresenceLeaseId != playableAuthority.LeaseId ||
            identity.LifecycleRevision != pending.Request.ExpectedLifecycleRevision)
        {
            Log.Error($"Deep-cryo proven no-commit body for {pending.Key} lost its exact local identity; retaining the durable presence fence without invoking rollback.");
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        BindPresenceIdentity(
            pending.Body,
            pending.Key,
            pending.SlotGeneration,
            playableAuthority);
        if (_suspendedPresenceBodies.ContainsKey(pending.Body) &&
            !RestoreSuspendedPresenceBody(pending.Body, pending.Key))
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        try
        {
            FinishFailedStore(
                pending.Body,
                pending.OperationId,
                pending.Completion,
                pending.NoCommitFailureReason!);
            // The exact lifecycle revision is unchanged and no active snapshot
            // exists, so this attempt is proven not to have created authority.
            // Hold the CharacterKey fence through the caller's synchronous
            // control rollback, then release it.
            RemovePendingStore(pending);
        }
        catch (Exception e)
        {
            pending.NoCommitCallbackFailed = true;
            Log.Error($"Deep-cryo no-commit rollback callback failed for {pending.Key}; retaining its sole body and lifecycle fence without repeating the callback: {e}");
            if (Exists(pending.Body))
            {
                SuspendPresenceBody(pending.Body, pending.Key);
                var bodyFence = EnsureComp<LuaMDeepCryoPendingComponent>(pending.Body);
                bodyFence.OperationId = pending.OperationId;
                bodyFence.Pod = pending.Pod;
                bodyFence.Source = pending.Source;
            }
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
        }
    }

    private static bool HasExactNoCommitStoreAuthority(
        LuaMDeepCryoStorePrecondition current,
        PendingStoreOperation pending)
    {
        return current.LifecycleRevision == pending.Request.ExpectedLifecycleRevision &&
               (current.ActiveSnapshot == null ||
                current.ActiveSnapshot is
                {
                    Status: DbLuaMDeepCryoSnapshotStatus.Consumed,
                } consumed &&
                current.Authority?.SnapshotId == consumed.Id) &&
               IsExactPlayablePresence(
                   current.Authority,
                   pending.Key,
                   pending.Request.ExpectedPresenceLeaseId,
                   pending.Request.ExpectedLifecycleRevision,
                   current.Authority?.SnapshotId);
    }

    private static bool CanProveNoCommitBodySuperseded(
        LuaMDeepCryoStorePrecondition current,
        PendingStoreOperation pending)
    {
        if (current.Authority is { } foreign &&
            foreign.ProfileId == pending.Key.ProfileId &&
            foreign.LeaseId != pending.Request.ExpectedPresenceLeaseId &&
            foreign.AuthorityLifecycleRevision > pending.Request.ExpectedLifecycleRevision)
        {
            return true;
        }

        return current.LifecycleRevision > pending.Request.ExpectedLifecycleRevision &&
               current.ActiveSnapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Stored or
                   DbLuaMDeepCryoSnapshotStatus.Quarantined,
               };
    }

    private async Task MaintainFailedNoCommitCallbackFenceAsync(PendingStoreOperation pending)
    {
        SuspendPresenceBody(pending.Body, pending.Key);

        LuaMDeepCryoStorePrecondition? current;
        try
        {
            current = await _db.GetLuaMDeepCryoStorePreconditionAsync(
                pending.Key.UserId,
                pending.Key.ProfileId,
                pending.Key.Slot);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo failed-callback presence read failed for {pending.Key}: {e}");
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        if (current == null)
        {
            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            return;
        }

        if (!HasExactNoCommitStoreAuthority(current, pending))
        {
            if (CanProveNoCommitBodySuperseded(current, pending))
            {
                if (!TryFinalizeSilentStore(pending))
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            }
            else
            {
                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
            }
            return;
        }

        var authority = current.Authority!;
        if (!HasSafePlayablePresenceWindow(authority))
        {
            var renewed = await TryRenewNoCommitStorePresenceAsync(pending, authority);
            if (renewed == null)
                return;

            authority = renewed;
            if (!HasSafePlayablePresenceWindow(authority))
            {
                pending.NoCommitRenewRequest = null;
                pending.NextAttempt = _timing.CurTime;
                return;
            }
        }

        if (Exists(pending.Body) &&
            TryComp<LuaMDeepCryoIdentityComponent>(pending.Body, out var identity) &&
            identity.PresenceLeaseId == pending.Request.ExpectedPresenceLeaseId &&
            identity.LifecycleRevision == pending.Request.ExpectedLifecycleRevision)
        {
            BindPresenceIdentity(
                pending.Body,
                pending.Key,
                pending.SlotGeneration,
                authority);
            SuspendPresenceBody(pending.Body, pending.Key);
        }

        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
    }

    private async Task<LuaMCharacterPresenceAuthorityRecord?> TryRenewNoCommitStorePresenceAsync(
        PendingStoreOperation pending,
        LuaMCharacterPresenceAuthorityRecord current)
    {
        var request = pending.NoCommitRenewRequest;
        if (request == null)
        {
            var renewedAtUtc = DateTime.UtcNow;
            request = new LuaMCharacterPresenceRenewRequest(
                Guid.NewGuid(),
                pending.Key.UserId,
                pending.Key.ProfileId,
                pending.Key.Slot,
                current.LeaseId,
                current.Revision,
                current.AuthorityLifecycleRevision,
                renewedAtUtc,
                renewedAtUtc + PlayablePresenceLeaseDuration);
            pending.NoCommitRenewRequest = request;
            pending.NoCommitRenewSnapshotId = current.SnapshotId;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.RenewLuaMCharacterPresenceAsync(request);
                if (IsConfirmedPresenceRenewal(
                        result.Authority,
                        pending.Key,
                        request,
                        pending.NoCommitRenewSnapshotId))
                {
                    return result.Authority;
                }
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo no-commit presence renewal attempt {attempt + 1} failed for {pending.Key}: {e}");
            }
        }

        try
        {
            var authority = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                pending.Key.UserId,
                pending.Key.ProfileId,
                pending.Key.Slot);
            if (IsConfirmedPresenceRenewal(
                    authority,
                    pending.Key,
                    request,
                    pending.NoCommitRenewSnapshotId))
            {
                return authority;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo no-commit presence renewal re-read failed for {pending.Key}: {e}");
        }

        pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
        return null;
    }

    private void RemovePendingStore(PendingStoreOperation pending)
    {
        if (_pendingStores.TryGetValue(pending.OperationId, out var retained) && retained == pending)
            _pendingStores.Remove(pending.OperationId);
        if (_pendingStoreKeys.TryGetValue(pending.Key, out var operationId) &&
            operationId == pending.OperationId)
        {
            _pendingStoreKeys.Remove(pending.Key);
        }
    }

    private static bool IsConfirmedCurrentStore(
        LuaMDeepCryoWriteResult result,
        PendingStoreOperation pending)
    {
        return result.Success &&
               result.SnapshotId is { } snapshotId &&
               result.Revision is { } revision &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Stored &&
               result.LeaseId == null &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                   LeaseId: null,
               } snapshot &&
               snapshot.Id == snapshotId &&
               snapshot.Revision == revision &&
               snapshot.UserId == pending.Key.UserId &&
               snapshot.ProfileId == pending.Key.ProfileId &&
               snapshot.Slot == pending.Key.Slot;
    }

    private static bool IsAuthoritativeStoreConflict(
        LuaMDeepCryoWriteResult result,
        PendingStoreOperation pending)
    {
        if (result.Success)
            return false;

        if (result.Authority is { } foreign &&
            foreign.ProfileId == pending.Key.ProfileId &&
            foreign.LeaseId != pending.Request.ExpectedPresenceLeaseId &&
            foreign.AuthorityLifecycleRevision > pending.Request.ExpectedLifecycleRevision)
        {
            return true;
        }

        // LifecycleConflict can carry a newest historical row after ACK. It is
        // diagnostic context, not proof that this specific row is the current
        // replacement. Re-read the atomic precondition before deleting.
        if (result.Status == LuaMDeepCryoWriteStatus.LifecycleConflict)
            return false;

        return result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Stored or
                   DbLuaMDeepCryoSnapshotStatus.Quarantined,
               } snapshot &&
               snapshot.Id == result.SnapshotId &&
               snapshot.Revision == result.Revision &&
               snapshot.UserId == pending.Key.UserId &&
               snapshot.ProfileId == pending.Key.ProfileId &&
               snapshot.Slot == pending.Key.Slot;
    }

    private bool TryRevalidatePendingBody(PendingStoreOperation operation, out string reason)
    {
        var body = operation.Body;
        var pod = operation.Pod;
        var key = operation.Key;
        var operationId = operation.OperationId;
        reason = "body-disappeared-after-store";
        if (!Exists(body) || !Exists(pod))
            return false;

        if (!TryComp<LuaMDeepCryoPendingComponent>(body, out var pendingComponent) ||
            pendingComponent.OperationId != operationId || pendingComponent.Pod != pod)
        {
            reason = "store-fence-changed";
            return false;
        }

        if (!IsBodyInExpectedPodContainer(body, pod, pendingComponent.Source))
        {
            reason = "body-left-pod-before-store-commit";
            return false;
        }

        if (!TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity) ||
            identity.UserId != key.UserId || identity.ProfileId != key.ProfileId || identity.Slot != key.Slot ||
            identity.LifecycleRevision != operation.Request.ExpectedLifecycleRevision ||
            identity.PresenceLeaseId != operation.Request.ExpectedPresenceLeaseId ||
            identity.PresencePhase != DbLuaMCharacterPresencePhase.Playable)
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

    public async Task<bool> HasStoredSnapshotAsync(NetUserId userId)
    {
        if (!TryGetCurrentCharacterKey(userId, out var key))
            return false;

        if (_pendingStoreKeys.ContainsKey(key))
            return true;
        if (_liveBodies.TryGetValue(key, out var live) && Exists(live))
            return true;

        try
        {
            var snapshot = await _db.GetLuaMDeepCryoSnapshotAsync(key.UserId, key.ProfileId, key.Slot);
            return snapshot?.Status == DbLuaMDeepCryoSnapshotStatus.Stored ||
                   snapshot is
                   {
                       Status: DbLuaMDeepCryoSnapshotStatus.Consumed,
                       LeaseId: not null,
                   };
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
        var claimGeneration = _roundCleanupGeneration;
        if (!TryGetCurrentCharacterKey(userId, out var key))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);
        if (!TryGetCanonicalProfile(key, out var profile, out var expectedPrototype, out var profileReason))
        {
            Log.Error($"Cannot resolve canonical deep-cryo profile for {key}: {profileReason}");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);
        }

        if (_pendingStoreKeys.ContainsKey(key) ||
            _discardPending.Contains(key) ||
            IsRestorePublicationActive(key))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);

        LuaMDeepCryoStorePrecondition? claimPrecondition;
        try
        {
            claimPrecondition = await _db.GetLuaMDeepCryoStorePreconditionAsync(
                key.UserId,
                key.ProfileId,
                key.Slot);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to read deep-cryo snapshot for {key}: {e}");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.DatabaseFailure);
        }

        if (claimPrecondition == null)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.IdentityInvalid);
        if (claimPrecondition.Authority != null)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        var snapshot = claimPrecondition.ActiveSnapshot;
        if (snapshot == null)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Missing);
        if (snapshot.Status == DbLuaMDeepCryoSnapshotStatus.Quarantined)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Quarantined);
        if (snapshot.Status != DbLuaMDeepCryoSnapshotStatus.Stored)
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        if (claimPrecondition.LifecycleRevision > long.MaxValue - 5 ||
            snapshot.Revision == long.MaxValue)
        {
            Log.Error($"Refused deep-cryo Claim for {key}: lifecycle or snapshot revision cannot safely reach terminal ACK.");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        }
        if (claimGeneration != _roundCleanupGeneration || _pendingStoreKeys.ContainsKey(key))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);

        var retainedSnapshot = snapshot with { Payload = snapshot.Payload.ToArray() };
        var leaseId = Guid.NewGuid();
        if (!TryStartRestorePublication(key, leaseId))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        var provisionalHandle = new LuaMDeepCryoRestoreHandle(
            key,
            snapshot.Id,
            snapshot.Revision + 1,
            leaseId,
            EntityUid.Invalid,
            false,
            claimPrecondition.LifecycleRevision,
            retainedSnapshot);
        _restoreHandles[key] = provisionalHandle;

        var claimedAt = DateTime.UtcNow;
        var claimRequest = new LuaMDeepCryoClaimRequest(
            Guid.NewGuid(),
            key.UserId,
            key.ProfileId,
            key.Slot,
            snapshot.Id,
            snapshot.Revision,
            _gameTicker.RoundId,
            leaseId,
            _serverInstanceId,
            claimedAt,
            claimedAt + RestoreLeaseDuration,
            claimPrecondition.LifecycleRevision);
        LuaMDeepCryoWriteResult? claim;
        _inFlightClaimLeases.Add(leaseId);
        try
        {
            claim = await ResolveRestoreClaimAsync(key, claimRequest);
        }
        finally
        {
            _inFlightClaimLeases.Remove(leaseId);
            if (_detachedRoundSettlements.TryGetValue(leaseId, out var detached))
                detached.NextAttempt = _timing.CurTime;
        }

        if (claim == null)
        {
            if (claimGeneration == _roundCleanupGeneration && IsActiveRestoreOwner(provisionalHandle))
                await AbortRestoreAsync(provisionalHandle, "restore-claim-outcome-indeterminate");
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.DatabaseFailure);
        }

        if (claimGeneration != _roundCleanupGeneration || !IsActiveRestoreOwner(provisionalHandle))
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);

        if (!claim.Success || claim.Revision is not { } claimedRevision)
        {
            if (claim.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Restoring &&
                claim.SnapshotId == snapshot.Id &&
                claim.Revision == provisionalHandle.Revision &&
                claim.LeaseId == leaseId)
            {
                await AbortRestoreAsync(provisionalHandle, "restore-claim-proof-indeterminate");
                return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.DatabaseFailure);
            }

            RemoveRestoreClaimOwner(key, leaseId);
            return LuaMDeepCryoClaimResult.Failed(claim.Status == LuaMDeepCryoWriteStatus.Quarantined
                ? LuaMDeepCryoClaimStatus.Quarantined
                : LuaMDeepCryoClaimStatus.Busy);
        }

        provisionalHandle = provisionalHandle with { Revision = claimedRevision };
        _restoreHandles[key] = provisionalHandle;
        if (claimGeneration != _roundCleanupGeneration)
        {
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        }

        if (claim.Snapshot is not { } claimedSnapshot ||
            claimedSnapshot.Status != DbLuaMDeepCryoSnapshotStatus.Restoring ||
            claimedSnapshot.Revision != claimedRevision ||
            claimedSnapshot.LeaseId != leaseId ||
            claim.LeaseId != leaseId ||
            !IsExactRestoreClaimPresence(
                claim.Authority,
                key,
                snapshot.Id,
                leaseId,
                checked(claimPrecondition.LifecycleRevision + 4)))
        {
            RemoveRestoreClaimOwner(key, leaseId);
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Busy);
        }

        if (!TryGetCurrentCharacterKey(userId, out var current) || current != key)
        {
            var invalidHandle = new LuaMDeepCryoRestoreHandle(
                key,
                snapshot.Id,
                claimedRevision,
                leaseId,
                EntityUid.Invalid,
                false,
                claimPrecondition.LifecycleRevision,
                retainedSnapshot);
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
                RemoveRestoreClaimOwner(key, leaseId);
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
            RemoveRestoreClaimOwner(key, leaseId);
            if (_liveBodies.TryGetValue(key, out var retainedBody) && retainedBody == body)
                _liveBodies.Remove(key);
            if (Exists(body))
                QueueDel(body);
            return LuaMDeepCryoClaimResult.Failed(LuaMDeepCryoClaimStatus.Quarantined);
        }

        var claimAuthority = claim.Authority!;
        BindRestoredIdentity(body, key, snapshot.Id, claimedRevision, claimAuthority);
        var handle = new LuaMDeepCryoRestoreHandle(
            key,
            snapshot.Id,
            claimedRevision,
            leaseId,
            body,
            deserialized,
            claimPrecondition.LifecycleRevision,
            claimedSnapshot with { Payload = claimedSnapshot.Payload.ToArray() },
            claimAuthority);
        _restoreHandles[key] = handle;
        return new LuaMDeepCryoClaimResult(LuaMDeepCryoClaimStatus.Success, handle);
    }

    private async Task<LuaMDeepCryoWriteResult?> ResolveRestoreClaimAsync(
        CharacterKey key,
        LuaMDeepCryoClaimRequest request)
    {
        LuaMDeepCryoWriteResult claim;
        try
        {
            claim = await _db.ClaimLuaMDeepCryoRestoreAsync(request);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo claim outcome requires exact replay for {key}: {e}");
            try
            {
                claim = await _db.ClaimLuaMDeepCryoRestoreAsync(request);
            }
            catch (Exception replayException)
            {
                Log.Error($"Deep-cryo claim remains unknown for {key}; retaining its fence for exact abort: {replayException}");
                return null;
            }
        }

        if (claim.Status != LuaMDeepCryoWriteStatus.UnknownOutcome)
            return claim;

        try
        {
            claim = await _db.ClaimLuaMDeepCryoRestoreAsync(request);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo returned claim outcome remains unknown for {key}; retaining its fence for exact abort: {e}");
            return null;
        }

        if (claim.Status != LuaMDeepCryoWriteStatus.UnknownOutcome)
            return claim;

        Log.Error($"Deep-cryo returned claim outcome remains unknown for {key}; retaining its fence for exact abort.");
        return null;
    }

    /// <summary>
    /// Consumes the durable snapshot before the caller inserts the body into a pod,
    /// transfers control, or otherwise exposes its inventory to the live world.
    /// </summary>
    public async Task<LuaMDeepCryoConsumeResult> ConsumeRestoreAsync(
        LuaMDeepCryoRestoreHandle handle,
        Action? onDetachedTerminalConfirmed = null)
    {
        // Consume is one-shot for an active restore. Reject duplicates before
        // allocating operation IDs or touching the retained publication receipt;
        // a second caller must never be able to compensate the owner's PREPARE.
        if (_consumeStarted.Contains(handle.Key) || _publicationReceipts.ContainsKey(handle.Key))
            return LuaMDeepCryoConsumeResult.Busy;

        // A stale or fabricated handle has no authority to abort the current
        // owner, release its fence, or delete a body that may already be live.
        if (!IsActiveRestoreOwner(handle))
            return LuaMDeepCryoConsumeResult.Failed;

        if (!TryValidateRestoreHandle(handle))
        {
            await AbortRestoreAsync(handle, "restore-handle-or-profile-invalid");
            return LuaMDeepCryoConsumeResult.Failed;
        }

        var operationTime = DateTime.UtcNow;
        if (!_consumeStarted.Add(handle.Key))
            return LuaMDeepCryoConsumeResult.Busy;

        var completionOperationId = Guid.NewGuid();
        var authorizationOperationId = Guid.NewGuid();
        var rollbackOperationId = Guid.NewGuid();
        var acknowledgeOperationId = Guid.NewGuid();
        var quarantineOperationId = Guid.NewGuid();
        var abortOperationId = Guid.NewGuid();
        var expectedPreparedRevision = handle.Revision + 1;
        var receipt = new LuaMDeepCryoPublicationReceipt(
            handle,
            completionOperationId,
            authorizationOperationId,
            rollbackOperationId,
            acknowledgeOperationId,
            quarantineOperationId,
            abortOperationId,
            expectedPreparedRevision,
            expectedPreparedRevision + 1,
            operationTime,
            operationTime,
            operationTime,
            operationTime,
            operationTime,
            operationTime,
            _roundCleanupGeneration,
            operationTime + PlayablePresenceLeaseDuration);
        _terminalCleanupRegistrations[handle.LeaseId] =
            new TerminalCleanupRegistration(onDetachedTerminalConfirmed);
        _publicationReceipts[handle.Key] = receipt;
        var request = new LuaMDeepCryoCompleteRequest(
            completionOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            handle.Revision,
            handle.LeaseId,
            receipt.CompletedAtUtc);
        LuaMDeepCryoWriteResult result;
        try
        {
            result = await _db.CompleteLuaMDeepCryoRestoreAsync(request);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo completion outcome requires exact replay for {handle.Key}: {e}");
            try
            {
                // Reusing the exact operation id resolves a commit-then-throw via
                // the immutable operation proof instead of guessing and aborting.
                result = await _db.CompleteLuaMDeepCryoRestoreAsync(request);
            }
            catch (Exception replayException)
            {
                Log.Error($"Deep-cryo completion remains unknown for {handle.Key}; keeping publication fenced: {replayException}");
                return new LuaMDeepCryoConsumeResult(LuaMDeepCryoConsumeStatus.Indeterminate, receipt);
            }
        }

        if (result.Status == LuaMDeepCryoWriteStatus.UnknownOutcome)
        {
            try
            {
                // A returned UnknownOutcome only describes a current row. Replay
                // the exact immutable operation before accepting PREPARE.
                result = await _db.CompleteLuaMDeepCryoRestoreAsync(request);
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo returned completion outcome could not be resolved for {handle.Key}: {e}");
            }
        }

        if (receipt.RoundCleanupGeneration != _roundCleanupGeneration ||
            !TryValidatePublicationReceipt(receipt))
        {
            return new LuaMDeepCryoConsumeResult(LuaMDeepCryoConsumeStatus.Indeterminate, receipt);
        }

        var committed = IsConfirmedCompletedRestore(result, handle, expectedPreparedRevision);

        if (!committed)
        {
            if (result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
                result.SnapshotId == handle.SnapshotId &&
                result.Revision == expectedPreparedRevision)
            {
                return new LuaMDeepCryoConsumeResult(LuaMDeepCryoConsumeStatus.Indeterminate, receipt);
            }

            await AbortRestoreAsync(handle, $"restore-complete-{result.Status}");
            return LuaMDeepCryoConsumeResult.Failed;
        }

        // Keep the same-process body cache and Stored marker until control is
        // actually exposed. An unpublished completion can still be compensated
        // without reconstructing or losing the only live body.
        return new LuaMDeepCryoConsumeResult(
            LuaMDeepCryoConsumeStatus.Success,
            receipt);
    }

    public async Task<bool> AcknowledgeRestorePublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        Action? onConfirmed = null)
    {
        if (!TryValidatePublicationReceipt(receipt) ||
            !_authorizedPublications.Contains(receipt.Handle.Key))
        {
            Log.Error($"Refused deep-cryo publication acknowledgement for {receipt.Handle.Key}: receipt does not match the active completion proof.");
            return false;
        }

        var pending = GetOrCreatePendingPublicationOperation(
            receipt,
            PendingPublicationKind.Acknowledge,
            reason: null,
            onConfirmed);
        if (pending == null)
            return false;

        return await ExecutePendingPublicationOperationAsync(pending);
    }

    /// <summary>
    /// Durably crosses the point of no return. Callers must not expose, insert,
    /// or transfer control to the body until this exact AUTH is confirmed.
    /// </summary>
    public Task<LuaMDeepCryoAuthorizationStatus> AuthorizeRestorePublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt)
    {
        if (receipt.RoundCleanupGeneration != _roundCleanupGeneration)
            return Task.FromResult(LuaMDeepCryoAuthorizationStatus.Terminal);
        if (!TryValidatePublicationReceipt(receipt))
            return Task.FromResult(LuaMDeepCryoAuthorizationStatus.Rejected);
        if (_authorizedPublications.Contains(receipt.Handle.Key))
            return Task.FromResult(LuaMDeepCryoAuthorizationStatus.Authorized);

        var key = receipt.Handle.Key;
        if (_pendingAuthorizationOperations.TryGetValue(key, out var existing))
        {
            if (existing.Receipt != receipt)
                return Task.FromResult(LuaMDeepCryoAuthorizationStatus.Rejected);

            return existing.Completion.Task;
        }

        var pending = new PendingAuthorizationOperation(
            receipt,
            _timing.CurTime);
        _pendingAuthorizationOperations.Add(key, pending);
        RetryPendingAuthorizationOperation(pending);
        return pending.Completion.Task;
    }

    public bool IsRestorePublicationGenerationCurrent(LuaMDeepCryoPublicationReceipt receipt)
    {
        return TryValidatePublicationReceipt(receipt) &&
               receipt.RoundCleanupGeneration == _roundCleanupGeneration;
    }

    private async Task ExecutePendingAuthorizationOperationAsync(PendingAuthorizationOperation pending)
    {
        var receipt = pending.Receipt;
        var key = receipt.Handle.Key;
        if (pending.Running || pending.Detached ||
            !_pendingAuthorizationOperations.TryGetValue(key, out var current) ||
            current != pending ||
            !TryValidatePublicationReceipt(receipt) ||
            receipt.RoundCleanupGeneration != _roundCleanupGeneration)
        {
            return;
        }

        pending.Running = true;
        try
        {
            var outcome = await TryAuthorizePreparedPublicationAsync(receipt);
            if (pending.Detached ||
                receipt.RoundCleanupGeneration != _roundCleanupGeneration ||
                !_pendingAuthorizationOperations.TryGetValue(key, out current) ||
                current != pending ||
                !TryValidatePublicationReceipt(receipt))
            {
                return;
            }

            switch (outcome)
            {
                case AuthorizationAttemptStatus.Authorized:
                    _authorizedPublications.Add(key);
                    _pendingAuthorizationOperations.Remove(key);
                    pending.Completion.TrySetResult(LuaMDeepCryoAuthorizationStatus.Authorized);
                    return;

                case AuthorizationAttemptStatus.Rejected:
                    _pendingAuthorizationOperations.Remove(key);
                    pending.Completion.TrySetResult(LuaMDeepCryoAuthorizationStatus.Rejected);
                    return;

                case AuthorizationAttemptStatus.Terminal:
                    _terminalCleanupRegistrations.TryGetValue(
                        receipt.Handle.LeaseId,
                        out var terminalCleanup);
                    _pendingAuthorizationOperations.Remove(key);
                    FinishRestorePublication(receipt.Handle);
                    InvokeTerminalCleanup(terminalCleanup, key);
                    pending.Completion.TrySetResult(LuaMDeepCryoAuthorizationStatus.Terminal);
                    return;

                case AuthorizationAttemptStatus.Indeterminate:
                    break;
            }

            pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
        }
        finally
        {
            pending.Running = false;
        }
    }

    private async Task<AuthorizationAttemptStatus> TryAuthorizePreparedPublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt)
    {
        var handle = receipt.Handle;
        var request = new LuaMDeepCryoAuthorizePublicationRequest(
            receipt.AuthorizationOperationId,
            receipt.CompletionOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            receipt.PreparedRevision,
            handle.LeaseId,
            receipt.AuthorizedAtUtc);
        var exactAuthorizedSeen = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.AuthorizeLuaMDeepCryoPublicationAsync(request);
                if (IsAuthoritativeAuthorizedQuarantineState(result, receipt))
                    return AuthorizationAttemptStatus.Terminal;
                if (IsExactAuthorizedPublication(result, receipt))
                {
                    if (!HasSafeRestorePublicationWindow(result.Authority, DateTime.UtcNow))
                    {
                        Log.Error($"Deep-cryo AUTH for {handle.Key} exhausted its safe RestoreClaim window before exposure; quarantining without publishing.");
                        return await TryQuarantineAuthorizedPublicationAsync(
                            receipt,
                            "authorization-safety-window-exhausted")
                            ? AuthorizationAttemptStatus.Terminal
                            : AuthorizationAttemptStatus.Indeterminate;
                    }

                    // Replay the immutable AUTH once before exposure. The replay
                    // returns current state, so a recovery quarantine that won
                    // after the first reply becomes terminal here.
                    if (exactAuthorizedSeen)
                        return AuthorizationAttemptStatus.Authorized;
                    exactAuthorizedSeen = true;
                    continue;
                }
                if (IsDefinitivePreAuthorizationRejection(result, receipt))
                    return AuthorizationAttemptStatus.Rejected;
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo publication authorization attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        Log.Error($"Deep-cryo publication authorization remains unresolved for {handle.Key}; retaining the PREPARE fence without exposing its body.");
        return AuthorizationAttemptStatus.Indeterminate;
    }

    private static bool IsDefinitivePreAuthorizationRejection(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return !result.Success &&
               result.Status != LuaMDeepCryoWriteStatus.UnknownOutcome &&
               result.SnapshotId == receipt.Handle.SnapshotId &&
               result.Revision == receipt.PreparedRevision &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
               result.LeaseId == receipt.Handle.LeaseId;
    }

    public bool TryResolvePublicationReceipt(
        LuaMDeepCryoRestoreHandle handle,
        LuaMDeepCryoPublicationReceipt? candidate,
        out LuaMDeepCryoPublicationReceipt receipt)
    {
        if (candidate is null ||
            !_publicationReceipts.TryGetValue(handle.Key, out var retained) ||
            retained.Handle != handle)
        {
            receipt = default!;
            return false;
        }

        if (candidate != retained)
        {
            receipt = default!;
            return false;
        }

        receipt = retained;
        return true;
    }

    private bool TryValidatePublicationReceipt(LuaMDeepCryoPublicationReceipt receipt)
    {
        var handle = receipt.Handle;
        return _activeRestores.TryGetValue(handle.Key, out var lease) &&
               lease == handle.LeaseId &&
               _publicationReceipts.TryGetValue(handle.Key, out var retained) &&
               retained == receipt;
    }

    /// <summary>
    /// Reopens a durably consumed snapshot only when the exact completion proof
    /// belongs to this still-unpublished restore. A deserialized duplicate is
    /// deleted only after Stored is confirmed; a same-process live body remains
    /// in the paused cache for a later retry.
    /// </summary>
    public async Task<bool> RollbackUnpublishedRestoreAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        string reason,
        Action? onConfirmed = null)
    {
        if (!TryValidatePublicationReceipt(receipt) ||
            _authorizedPublications.Contains(receipt.Handle.Key))
            return false;

        var pending = GetOrCreatePendingPublicationOperation(
            receipt,
            PendingPublicationKind.Rollback,
            LimitReason(reason),
            onConfirmed);
        return pending != null && await ExecutePendingPublicationOperationAsync(pending);
    }

    public async Task<bool> QuarantineAuthorizedPublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        string reason,
        Action? onConfirmed = null)
    {
        if (!TryValidatePublicationReceipt(receipt) ||
            !_authorizedPublications.Contains(receipt.Handle.Key))
        {
            return false;
        }

        var pending = GetOrCreatePendingPublicationOperation(
            receipt,
            PendingPublicationKind.Quarantine,
            LimitReason(reason),
            onConfirmed);
        return pending != null && await ExecutePendingPublicationOperationAsync(pending);
    }

    private PendingPublicationOperation? GetOrCreatePendingPublicationOperation(
        LuaMDeepCryoPublicationReceipt receipt,
        PendingPublicationKind kind,
        string? reason,
        Action? onConfirmed)
    {
        var key = receipt.Handle.Key;
        if (_pendingPublicationOperations.TryGetValue(key, out var existing))
        {
            if (existing.Receipt != receipt || existing.Kind != kind || existing.Reason != reason)
                return null;
            existing.OnConfirmed ??= onConfirmed;
            return existing;
        }

        var pending = new PendingPublicationOperation(receipt, kind, reason, onConfirmed, _timing.CurTime);
        _pendingPublicationOperations.Add(key, pending);
        return pending;
    }

    private async Task<bool> ExecutePendingPublicationOperationAsync(PendingPublicationOperation pending)
    {
        var key = pending.Receipt.Handle.Key;
        if (pending.Running || pending.Detached ||
            !_pendingPublicationOperations.TryGetValue(key, out var current) ||
            current != pending ||
            !TryValidatePublicationReceipt(pending.Receipt) ||
            pending.Receipt.RoundCleanupGeneration != _roundCleanupGeneration)
        {
            return false;
        }

        pending.Running = true;
        try
        {
            long? storedRevision = null;
            var acknowledgementStatus = AcknowledgementSettlementStatus.Pending;
            bool confirmed;
            switch (pending.Kind)
            {
                case PendingPublicationKind.Acknowledge:
                {
                    if (pending.AcknowledgementCompensated)
                    {
                        acknowledgementStatus = AcknowledgementSettlementStatus.Compensated;
                    }
                    else
                    {
                        var settlement = await TrySettlePublicationAcknowledgementAsync(
                            pending.Receipt,
                            pending.AcknowledgementCommitted,
                            pending.AcknowledgedAuthority,
                            () => CanPublishAcknowledgedBody(pending),
                            detached: false);
                        pending.AcknowledgementCommitted = settlement.AcknowledgementCommitted;
                        pending.AcknowledgedAuthority = settlement.Authority;
                        pending.AcknowledgementCompensated =
                            settlement.Status == AcknowledgementSettlementStatus.Compensated;
                        acknowledgementStatus = settlement.Status;
                    }

                    confirmed = acknowledgementStatus != AcknowledgementSettlementStatus.Pending;
                    break;
                }
                case PendingPublicationKind.Rollback:
                    confirmed = (storedRevision = await TryRollbackPreparedPublicationAsync(
                        pending.Receipt,
                        pending.Reason!)) != null;
                    break;
                case PendingPublicationKind.Quarantine:
                    confirmed = await TryQuarantineAuthorizedPublicationAsync(
                        pending.Receipt,
                        pending.Reason!);
                    break;
                default:
                    confirmed = false;
                    break;
            }

            if (pending.Detached ||
                pending.Receipt.RoundCleanupGeneration != _roundCleanupGeneration ||
                !_pendingPublicationOperations.TryGetValue(key, out current) ||
                current != pending ||
                !TryValidatePublicationReceipt(pending.Receipt))
            {
                return false;
            }

            if (!confirmed)
            {
                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return false;
            }

            if (acknowledgementStatus == AcknowledgementSettlementStatus.Compensated)
            {
                // ACK either never ran and AUTH was quarantined, or ACK committed
                // and the original snapshot was quarantined under its exact
                // playable presence token. Only that durable proof permits body
                // destruction and fence release.
                if (!TryDeleteInvalidAcknowledgedBody(pending.Receipt.Handle))
                {
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return false;
                }

                _terminalCleanupRegistrations.TryGetValue(
                    pending.Receipt.Handle.LeaseId,
                    out var terminalCleanup);
                _pendingPublicationOperations.Remove(key);
                if (_liveBodies.TryGetValue(key, out var compensatedBody) &&
                    compensatedBody == pending.Receipt.Handle.Body)
                {
                    _liveBodies.Remove(key);
                }

                FinishRestorePublication(pending.Receipt.Handle);
                InvokeTerminalCleanup(terminalCleanup, key);
                return false;
            }

            if (pending.Kind is PendingPublicationKind.Acknowledge or PendingPublicationKind.Quarantine)
            {
                if (_liveBodies.TryGetValue(key, out var liveBody) && liveBody == pending.Receipt.Handle.Body)
                    _liveBodies.Remove(key);
                if (Exists(pending.Receipt.Handle.Body))
                    RemComp<LuaMDeepCryoStoredComponent>(pending.Receipt.Handle.Body);
            }
            else
            {
                if (!RestoreUnpublishedBody(pending.Receipt.Handle, storedRevision!.Value))
                {
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return false;
                }
            }

            _pendingPublicationOperations.Remove(key);
            FinishRestorePublication(pending.Receipt.Handle);
            try
            {
                pending.OnConfirmed?.Invoke();
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo publication completion callback failed for {key}: {e}");
            }

            if (pending.Kind == PendingPublicationKind.Quarantine && Exists(pending.Receipt.Handle.Body))
                QueueDel(pending.Receipt.Handle.Body);

            return true;
        }
        finally
        {
            pending.Running = false;
        }
    }

    private bool CanPublishAcknowledgedBody(PendingPublicationOperation pending)
    {
        var key = pending.Receipt.Handle.Key;
        return !pending.Detached &&
               pending.Receipt.RoundCleanupGeneration == _roundCleanupGeneration &&
               _pendingPublicationOperations.TryGetValue(key, out var current) &&
               current == pending &&
               TryValidatePublicationReceipt(pending.Receipt);
    }

    private async Task<AcknowledgementSettlementResult> TrySettlePublicationAcknowledgementAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        bool acknowledgementCommitted,
        LuaMCharacterPresenceAuthorityRecord? acknowledgedAuthority,
        Func<bool> canPublish,
        bool detached)
    {
        var handle = receipt.Handle;
        var invalidReason = "publication-generation-detached";
        var targetValid = canPublish() &&
                           TryValidateAcknowledgementTarget(receipt, out invalidReason);
        if (!acknowledgementCommitted && !targetValid && !detached)
        {
            Log.Error($"Refused deep-cryo ACK for {handle.Key}: publication target is invalid ({invalidReason}).");
            if (await TryQuarantineAuthorizedPublicationAsync(
                    receipt,
                    LimitReason($"ack-target-invalid-before-ack:{invalidReason}")))
            {
                return new AcknowledgementSettlementResult(
                    AcknowledgementSettlementStatus.Compensated,
                    false,
                    null);
            }

            // AUTH quarantine can lose a CAS to an ACK continuation whose result
            // has not reached this caller yet. Resolve the immutable ACK below;
            // if it committed, quarantine that exact acknowledged publication.
        }

        var request = new LuaMDeepCryoAcknowledgePublicationRequest(
            receipt.AcknowledgeOperationId,
            receipt.AuthorizationOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            receipt.AuthorizedRevision,
            handle.LeaseId,
            receipt.AcknowledgedAtUtc,
            receipt.PlayableLeaseExpiresAtUtc);

        if (!acknowledgementCommitted)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await _db.AcknowledgeLuaMDeepCryoPublicationAsync(request);
                    if (IsConfirmedPublicationAcknowledgement(result, receipt, out var authority))
                    {
                        acknowledgementCommitted = true;
                        acknowledgedAuthority = authority;
                        break;
                    }
                    if (IsAuthoritativeAuthorizedQuarantineState(result, receipt))
                    {
                        return new AcknowledgementSettlementResult(
                            AcknowledgementSettlementStatus.Compensated,
                            false,
                            null);
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Deep-cryo publication acknowledgement attempt {attempt + 1} failed for {handle.Key}: {e}");
                }
            }

            if (!acknowledgementCommitted)
            {
                if (await IsAuthorizedPublicationQuarantinedAsync(receipt))
                {
                    return new AcknowledgementSettlementResult(
                        AcknowledgementSettlementStatus.Compensated,
                        false,
                        null);
                }

                Log.Error($"Deep-cryo publication acknowledgement remains unresolved for {handle.Key}; retaining its durable lease and physical fence.");
                return new AcknowledgementSettlementResult(
                    AcknowledgementSettlementStatus.Pending,
                    false,
                    acknowledgedAuthority);
            }
        }

        if (_acknowledgedQuarantineLeases.Contains(handle.LeaseId))
        {
            // The first post-ACK quarantine attempt is a one-way decision. Its
            // commit may be hidden behind a failed reply and failed re-read, so
            // a later locally valid target must never be published from the old
            // cached Playable authority.
            var quarantined = await TryQuarantineAcknowledgedPublicationAsync(
                receipt,
                acknowledgedAuthority ?? handle.PresenceAuthority!,
                "acknowledged-publication-compensation-retry");
            return new AcknowledgementSettlementResult(
                quarantined
                    ? AcknowledgementSettlementStatus.Compensated
                    : AcknowledgementSettlementStatus.Pending,
                true,
                acknowledgedAuthority);
        }

        if (!IsExactPlayablePresence(
                acknowledgedAuthority,
                handle.Key,
                handle.LeaseId,
                checked(handle.ExpectedLifecycleRevision + 4),
                handle.SnapshotId) ||
            !HasSafePlayablePresenceWindow(acknowledgedAuthority))
        {
            try
            {
                var current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                    handle.Key.UserId,
                    handle.Key.ProfileId,
                    handle.Key.Slot);
                if (IsExactPlayablePresence(
                        current,
                        handle.Key,
                        handle.LeaseId,
                        checked(handle.ExpectedLifecycleRevision + 4),
                        handle.SnapshotId))
                {
                    acknowledgedAuthority = current;
                }
                else if (IsDefinitiveForeignAcknowledgedPresence(current, handle))
                {
                    return new AcknowledgementSettlementResult(
                        AcknowledgementSettlementStatus.Compensated,
                        true,
                        null);
                }
                else
                {
                    // Bare absence and equal/lower foreign epochs are not proof
                    // that the acknowledged body was safely superseded.
                    acknowledgedAuthority = null;
                }
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo acknowledged presence re-read failed for {handle.Key}: {e}");
            }
        }

        if (acknowledgedAuthority == null)
        {
            if (await IsAcknowledgedPublicationQuarantinedAsync(receipt))
            {
                return new AcknowledgementSettlementResult(
                    AcknowledgementSettlementStatus.Compensated,
                    true,
                    null);
            }

            return new AcknowledgementSettlementResult(
                AcknowledgementSettlementStatus.Pending,
                true,
                null);
        }

        if (detached)
        {
            var quarantined = await TryQuarantineAcknowledgedPublicationAsync(
                receipt,
                acknowledgedAuthority,
                "round-cleanup-after-ack");
            return new AcknowledgementSettlementResult(
                quarantined
                    ? AcknowledgementSettlementStatus.Compensated
                    : AcknowledgementSettlementStatus.Pending,
                true,
                acknowledgedAuthority);
        }

        if (!HasSafePlayablePresenceWindow(acknowledgedAuthority))
        {
            if (Exists(handle.Body) && TryValidateAcknowledgementTarget(receipt, out _))
            {
                var identity = Comp<LuaMDeepCryoIdentityComponent>(handle.Body);
                BindPresenceIdentity(
                    handle.Body,
                    handle.Key,
                    identity.SlotGeneration,
                    acknowledgedAuthority);
                SuspendPresenceBody(handle.Body, handle.Key);
            }

            return new AcknowledgementSettlementResult(
                AcknowledgementSettlementStatus.Pending,
                true,
                acknowledgedAuthority);
        }

        if (canPublish() && TryBindAcknowledgedLifecycleEpoch(receipt, acknowledgedAuthority, out _))
        {
            if (_suspendedPresenceBodies.ContainsKey(handle.Body) &&
                !RestoreSuspendedPresenceBody(handle.Body, handle.Key))
            {
                return new AcknowledgementSettlementResult(
                    AcknowledgementSettlementStatus.Pending,
                    true,
                    acknowledgedAuthority);
            }

            return new AcknowledgementSettlementResult(
                AcknowledgementSettlementStatus.Published,
                true,
                acknowledgedAuthority);
        }

        TryValidateAcknowledgementTarget(receipt, out invalidReason);
        Log.Error($"Exact deep-cryo ACK for {handle.Key} committed after its playable target became invalid; quarantining the original payload under its exact presence token.");
        var durableCompensation = await TryQuarantineAcknowledgedPublicationAsync(
            receipt,
            acknowledgedAuthority,
            LimitReason($"ack-target-invalid-after-ack:{invalidReason}"));
        return new AcknowledgementSettlementResult(
            durableCompensation
                ? AcknowledgementSettlementStatus.Compensated
                : AcknowledgementSettlementStatus.Pending,
            true,
            acknowledgedAuthority);
    }

    private bool TryBindAcknowledgedLifecycleEpoch(
        LuaMDeepCryoPublicationReceipt receipt,
        LuaMCharacterPresenceAuthorityRecord authority,
        out string reason)
    {
        if (!TryValidateAcknowledgementTarget(receipt, out reason))
            return false;

        var handle = receipt.Handle;
        var identity = Comp<LuaMDeepCryoIdentityComponent>(handle.Body);
        if (!IsExactPlayablePresence(
                authority,
                handle.Key,
                handle.LeaseId,
                checked(handle.ExpectedLifecycleRevision + 4),
                handle.SnapshotId))
        {
            reason = "ack-playable-authority-invalid";
            return false;
        }
        if (!HasSafePlayablePresenceWindow(authority))
        {
            reason = "ack-playable-authority-expiring";
            return false;
        }

        BindPresenceIdentity(
            handle.Body,
            handle.Key,
            identity.SlotGeneration,
            authority);
        return true;
    }

    internal static bool IsDefinitiveForeignAcknowledgedPresence(
        LuaMCharacterPresenceAuthorityRecord? authority,
        LuaMDeepCryoRestoreHandle handle)
    {
        return authority != null &&
               authority.ProfileId == handle.Key.ProfileId &&
               authority.LeaseId != handle.LeaseId &&
               handle.ExpectedLifecycleRevision <= long.MaxValue - 4 &&
               authority.AuthorityLifecycleRevision >
               checked(handle.ExpectedLifecycleRevision + 4);
    }

    private bool TryValidateAcknowledgementTarget(
        LuaMDeepCryoPublicationReceipt receipt,
        out string reason)
    {
        var handle = receipt.Handle;
        reason = "ack-target-invalid";
        if (handle.ExpectedLifecycleRevision < 0 ||
            handle.ExpectedLifecycleRevision > long.MaxValue - 5)
        {
            reason = "lifecycle-overflow";
            return false;
        }

        if (!IsExactRestoreClaimPresence(
                handle.PresenceAuthority,
                handle.Key,
                handle.SnapshotId,
                handle.LeaseId,
                checked(handle.ExpectedLifecycleRevision + 4)))
        {
            reason = "retained-claim-presence-invalid";
            return false;
        }

        if (!Exists(handle.Body))
        {
            reason = "body-missing";
            return false;
        }

        if (!TryGetCurrentCharacterKey(handle.Key.UserId, out var current) || current != handle.Key)
        {
            reason = "character-key-changed";
            return false;
        }

        if (!TryComp<LuaMDeepCryoIdentityComponent>(handle.Body, out var identity) ||
            identity.UserId != handle.Key.UserId ||
            identity.ProfileId != handle.Key.ProfileId ||
            identity.Slot != handle.Key.Slot ||
            identity.PresenceLeaseId != handle.LeaseId ||
            identity.PresenceSnapshotId != handle.SnapshotId ||
            !(identity.LifecycleRevision == -1 &&
              identity.PresencePhase == DbLuaMCharacterPresencePhase.RestoreClaim ||
              identity.LifecycleRevision == handle.ExpectedLifecycleRevision + 4 &&
              identity.PresencePhase == DbLuaMCharacterPresencePhase.Playable))
        {
            reason = "body-identity-invalid";
            return false;
        }

        if (!TryComp<LuaMDeepCryoStoredComponent>(handle.Body, out var stored) ||
            stored.UserId != handle.Key.UserId ||
            stored.ProfileId != handle.Key.ProfileId ||
            stored.Slot != handle.Key.Slot ||
            stored.SnapshotId != handle.SnapshotId ||
            stored.Revision != handle.Revision)
        {
            reason = "body-snapshot-marker-invalid";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private async Task<bool> TryQuarantineAcknowledgedPublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        LuaMCharacterPresenceAuthorityRecord authority,
        string reason)
    {
        var handle = receipt.Handle;
        if (await IsAcknowledgedPublicationQuarantinedAsync(receipt))
            return true;

        if (IsPresenceOwnedByActiveRestore(handle.Key, handle.LeaseId))
            _acknowledgedQuarantineLeases.Add(handle.LeaseId);
        if (_pendingPresenceRenewals.TryGetValue(handle.Key, out var renewal) &&
            renewal.Request.LeaseId == handle.LeaseId)
        {
            renewal.ReservedForRestoreSettlement = true;
            if (renewal.Running)
                return false;

            _pendingPresenceRenewals.Remove(handle.Key);
        }

        LuaMCharacterPresenceAuthorityRecord? current;
        try
        {
            current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo acknowledged quarantine authority re-read failed for {handle.Key}: {e}");
            return false;
        }

        if (IsDefinitiveForeignAcknowledgedPresence(current, handle))
            return true;
        if (!IsExactPlayablePresence(
                current,
                handle.Key,
                handle.LeaseId,
                checked(handle.ExpectedLifecycleRevision + 4),
                handle.SnapshotId))
        {
            return await ResolveAcknowledgedQuarantineTerminalAsync(receipt);
        }

        authority = current!;
        if (_acknowledgedQuarantineRequests.TryGetValue(handle.LeaseId, out var retainedRequest))
        {
            if (await TryReplayAcknowledgedQuarantineAsync(receipt, retainedRequest))
                return true;

            try
            {
                current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                    handle.Key.UserId,
                    handle.Key.ProfileId,
                    handle.Key.Slot);
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo acknowledged quarantine post-attempt authority re-read failed for {handle.Key}: {e}");
                return false;
            }

            if (IsDefinitiveForeignAcknowledgedPresence(current, handle))
                return true;
            if (!IsExactPlayablePresence(
                    current,
                    handle.Key,
                    handle.LeaseId,
                    checked(handle.ExpectedLifecycleRevision + 4),
                    handle.SnapshotId))
            {
                return await ResolveAcknowledgedQuarantineTerminalAsync(receipt);
            }

            authority = current!;
            if (authority.Revision < retainedRequest.ExpectedPresenceRevision)
                return false;
            if (authority.Revision == retainedRequest.ExpectedPresenceRevision)
            {
                var renewed = await TryRenewAcknowledgedQuarantineLeaseAsync(receipt, authority);
                if (renewed == null)
                    return await ResolveAcknowledgedQuarantineTerminalAsync(receipt);
                authority = renewed;
            }

            // An exact same-token higher revision (including our maintenance
            // renewal) proves the old quarantine request did not commit. Only
            // now may a new operation identity be based on the newer revision.
            _acknowledgedQuarantineRequests.Remove(handle.LeaseId);
            _acknowledgedQuarantineRenewals.Remove(handle.LeaseId);
        }

        if (!HasSafePlayablePresenceWindow(authority))
        {
            var renewed = await TryRenewAcknowledgedQuarantineLeaseAsync(receipt, authority);
            if (renewed == null)
                return await ResolveAcknowledgedQuarantineTerminalAsync(receipt);
            authority = renewed;
        }

        var quarantinedAtUtc = DateTime.UtcNow;
        if (authority.RenewedAtUtc.ToUniversalTime() > quarantinedAtUtc)
            quarantinedAtUtc = authority.RenewedAtUtc.ToUniversalTime();
        var request = new LuaMDeepCryoQuarantineAcknowledgedPublicationRequest(
            Guid.NewGuid(),
            receipt.AcknowledgeOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            receipt.AuthorizedRevision + 1,
            handle.LeaseId,
            authority.Revision,
            authority.AuthorityLifecycleRevision,
            reason,
            quarantinedAtUtc);
        _acknowledgedQuarantineRequests[handle.LeaseId] = request;
        return await TryReplayAcknowledgedQuarantineAsync(receipt, request);
    }

    private async Task<bool> TryReplayAcknowledgedQuarantineAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        LuaMDeepCryoQuarantineAcknowledgedPublicationRequest request)
    {
        var handle = receipt.Handle;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(request);
                if (IsConfirmedAcknowledgedPublicationQuarantine(result, receipt))
                    return true;
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo acknowledged quarantine attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        return await ResolveAcknowledgedQuarantineTerminalAsync(receipt);
    }

    private async Task<bool> ResolveAcknowledgedQuarantineTerminalAsync(
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return await IsAcknowledgedPublicationQuarantinedAsync(receipt);
    }

    private async Task<LuaMCharacterPresenceAuthorityRecord?> TryRenewAcknowledgedQuarantineLeaseAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        LuaMCharacterPresenceAuthorityRecord authority)
    {
        var handle = receipt.Handle;
        if (!_acknowledgedQuarantineRenewals.TryGetValue(handle.LeaseId, out var request))
        {
            var renewedAtUtc = DateTime.UtcNow;
            if (authority.RenewedAtUtc.ToUniversalTime() > renewedAtUtc)
                renewedAtUtc = authority.RenewedAtUtc.ToUniversalTime();
            request = new LuaMCharacterPresenceRenewRequest(
                Guid.NewGuid(),
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot,
                handle.LeaseId,
                authority.Revision,
                authority.AuthorityLifecycleRevision,
                renewedAtUtc,
                renewedAtUtc + PlayablePresenceLeaseDuration);
            _acknowledgedQuarantineRenewals.Add(handle.LeaseId, request);
        }

        LuaMCharacterPresenceAuthorityRecord? current = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.RenewLuaMCharacterPresenceAsync(request);
                current = result.Authority;
                if (IsConfirmedPresenceRenewal(current, handle.Key, request, handle.SnapshotId))
                {
                    _acknowledgedQuarantineRenewals.Remove(handle.LeaseId);
                    return current;
                }
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo acknowledged quarantine lease maintenance attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        try
        {
            current = await _db.GetLuaMCharacterPresenceAuthorityAsync(
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot);
            if (IsConfirmedPresenceRenewal(current, handle.Key, request, handle.SnapshotId) ||
                IsExactPlayablePresence(
                    current,
                    handle.Key,
                    handle.LeaseId,
                    request.ExpectedAuthorityLifecycleRevision,
                    handle.SnapshotId) &&
                current!.Revision > request.ExpectedLeaseRevision)
            {
                _acknowledgedQuarantineRenewals.Remove(handle.LeaseId);
                return current;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo acknowledged quarantine lease maintenance re-read failed for {handle.Key}: {e}");
        }

        return null;
    }

    private void WakeAcknowledgedQuarantineSettlement(CharacterKey key, Guid leaseId)
    {
        if (_pendingPublicationOperations.TryGetValue(key, out var publication) &&
            publication.Receipt.Handle.LeaseId == leaseId)
        {
            publication.NextAttempt = _timing.CurTime;
        }

        if (_detachedRoundSettlements.TryGetValue(leaseId, out var detached))
            detached.NextAttempt = _timing.CurTime;
    }

    private async Task<bool> IsAcknowledgedPublicationQuarantinedAsync(
        LuaMDeepCryoPublicationReceipt receipt)
    {
        try
        {
            var current = await _db.GetLuaMDeepCryoSnapshotAsync(
                receipt.Handle.Key.UserId,
                receipt.Handle.Key.ProfileId,
                receipt.Handle.Key.Slot);
            return current is
                   {
                       Status: DbLuaMDeepCryoSnapshotStatus.Quarantined,
                       LeaseId: null,
                   } &&
                   current.Id == receipt.Handle.SnapshotId &&
                   current.Revision == receipt.AuthorizedRevision + 2;
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo acknowledged quarantine re-read failed for {receipt.Handle.Key}: {e}");
            return false;
        }
    }

    private async Task<bool> IsAuthorizedPublicationQuarantinedAsync(
        LuaMDeepCryoPublicationReceipt receipt)
    {
        try
        {
            var current = await _db.GetLuaMDeepCryoSnapshotAsync(
                receipt.Handle.Key.UserId,
                receipt.Handle.Key.ProfileId,
                receipt.Handle.Key.Slot);
            return current is
                   {
                       Status: DbLuaMDeepCryoSnapshotStatus.Quarantined,
                       LeaseId: null,
                   } &&
                   current.Id == receipt.Handle.SnapshotId &&
                   current.Revision == receipt.AuthorizedRevision + 1;
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo authorized quarantine re-read failed for {receipt.Handle.Key}: {e}");
            return false;
        }
    }

    private bool TryDeleteInvalidAcknowledgedBody(LuaMDeepCryoRestoreHandle handle)
    {
        if (!Exists(handle.Body))
            return true;

        try
        {
            EntityManager.DeleteEntity(handle.Body);
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo ACK compensation could not synchronously delete the invalid body for {handle.Key}: {e}");
            return false;
        }

        return !Exists(handle.Body);
    }

    private async Task<long?> TryRollbackPreparedPublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        string reason)
    {
        var handle = receipt.Handle;
        var expectedStoredRevision = receipt.PreparedRevision + 1;
        var request = new LuaMDeepCryoRollbackPublicationRequest(
            receipt.RollbackOperationId,
            receipt.CompletionOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            receipt.PreparedRevision,
            handle.LeaseId,
            reason,
            receipt.RolledBackAtUtc);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.RollbackLuaMDeepCryoPublicationAsync(request);
                if (IsConfirmedPublicationRollback(result, receipt))
                    return expectedStoredRevision;
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo unpublished restore rollback attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        try
        {
            var current = await _db.GetLuaMDeepCryoSnapshotAsync(
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot);
            if (current is { Status: DbLuaMDeepCryoSnapshotStatus.Stored, LeaseId: null } &&
                current.Id == handle.SnapshotId &&
                current.Revision == expectedStoredRevision)
            {
                return expectedStoredRevision;
            }

            if (current is { Status: DbLuaMDeepCryoSnapshotStatus.Restoring } &&
                current.Id == handle.SnapshotId &&
                current.Revision == handle.Revision &&
                current.LeaseId == handle.LeaseId &&
                await TryAbortConfirmedUncommittedRestoreAsync(receipt))
            {
                return handle.Revision + 1;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo unpublished restore rollback re-read failed for {handle.Key}: {e}");
        }

        Log.Error($"Deep-cryo restore for {handle.Key} remains unpublished with an unresolved durable outcome; retaining its body and publication fence.");
        return null;
    }

    private async Task<bool> TryQuarantineAuthorizedPublicationAsync(
        LuaMDeepCryoPublicationReceipt receipt,
        string reason)
    {
        var handle = receipt.Handle;
        var request = new LuaMDeepCryoQuarantineAuthorizedPublicationRequest(
            receipt.QuarantineOperationId,
            receipt.AuthorizationOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            receipt.AuthorizedRevision,
            handle.LeaseId,
            reason,
            receipt.QuarantinedAtUtc);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(request);
                if (IsConfirmedPublicationQuarantine(result, receipt))
                    return true;
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo authorized quarantine attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        if (await IsAuthorizedPublicationQuarantinedAsync(receipt))
            return true;

        Log.Error($"Deep-cryo authorized publication quarantine remains unresolved for {handle.Key}; retaining its durable and physical fences.");
        return false;
    }

    private async Task<bool> TryAbortConfirmedUncommittedRestoreAsync(LuaMDeepCryoPublicationReceipt receipt)
    {
        var handle = receipt.Handle;
        var expectedStoredRevision = handle.Revision + 1;
        var request = new LuaMDeepCryoAbortRequest(
            receipt.AbortOperationId,
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            handle.Revision,
            handle.LeaseId,
            "restore-complete-confirmed-uncommitted",
            receipt.AbortedAtUtc);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _db.AbortLuaMDeepCryoRestoreAsync(request);
                if (IsConfirmedAbortedRestore(result, handle, expectedStoredRevision))
                    return true;
            }
            catch (Exception e)
            {
                Log.Error($"Deep-cryo exact abort attempt {attempt + 1} failed for {handle.Key}: {e}");
            }
        }

        try
        {
            var current = await _db.GetLuaMDeepCryoSnapshotAsync(
                handle.Key.UserId,
                handle.Key.ProfileId,
                handle.Key.Slot);
            return current is
                   {
                       Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                       LeaseId: null,
                   } &&
                   current.Id == handle.SnapshotId &&
                   current.Revision == expectedStoredRevision;
        }
        catch (Exception e)
        {
            Log.Error($"Deep-cryo exact abort re-read failed for {handle.Key}: {e}");
            return false;
        }
    }

    private static bool IsConfirmedAbortedRestore(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoRestoreHandle handle,
        long expectedStoredRevision)
    {
        // UnknownOutcome only proves the current snapshot state, not that this
        // exact CompleteRestore operation produced it. A concurrent discard can
        // reach the same Consumed revision, so only an immutable operation replay
        // (AlreadyProcessed) or the direct success is sufficient publication proof.
        return result.Success &&
               result.SnapshotId == handle.SnapshotId &&
               result.Revision == expectedStoredRevision &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Stored &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                   LeaseId: null,
               } snapshot &&
               snapshot.Id == handle.SnapshotId &&
               snapshot.Revision == expectedStoredRevision;
    }

    private bool RestoreUnpublishedBody(LuaMDeepCryoRestoreHandle handle, long storedRevision)
    {
        if (handle.Deserialized)
        {
            if (_liveBodies.TryGetValue(handle.Key, out var liveBody) && liveBody == handle.Body)
                _liveBodies.Remove(handle.Key);
            return DisposeStalePresenceBody(handle.Body, handle.Key, handle.LeaseId);
        }

        if (!Exists(handle.Body))
            return true;

        var stored = EnsureComp<LuaMDeepCryoStoredComponent>(handle.Body);
        stored.UserId = handle.Key.UserId;
        stored.ProfileId = handle.Key.ProfileId;
        stored.Slot = handle.Key.Slot;
        stored.SnapshotId = handle.SnapshotId;
        stored.Revision = storedRevision;
        _liveBodies[handle.Key] = handle.Body;
        return true;
    }

    private static bool IsConfirmedPublicationRollback(
        LuaMDeepCryoWriteResult? result,
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return result != null &&
               result.Success &&
               result.SnapshotId == receipt.Handle.SnapshotId &&
               result.Revision == receipt.PreparedRevision + 1 &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Stored &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                   LeaseId: null,
               } snapshot &&
               snapshot.Id == receipt.Handle.SnapshotId &&
               snapshot.Revision == receipt.PreparedRevision + 1;
    }

    private static bool IsConfirmedPublicationAcknowledgement(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoPublicationReceipt receipt,
        out LuaMCharacterPresenceAuthorityRecord? authority)
    {
        authority = result.Authority;
        var handle = receipt.Handle;
        return result.Success &&
               result.SnapshotId == handle.SnapshotId &&
               result.Revision == receipt.AuthorizedRevision + 1 &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
               result.LeaseId == handle.LeaseId &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Consumed,
                   LeaseId: var snapshotLease,
               } snapshot &&
               snapshotLease == handle.LeaseId &&
               snapshot.Id == handle.SnapshotId &&
               snapshot.Revision == receipt.AuthorizedRevision + 1 &&
               IsExactPlayablePresence(
                   authority,
                   handle.Key,
                   handle.LeaseId,
                   checked(handle.ExpectedLifecycleRevision + 4),
                   handle.SnapshotId);
    }

    private static bool IsConfirmedAcknowledgedPublicationQuarantine(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return result.Success &&
               result.SnapshotId == receipt.Handle.SnapshotId &&
               result.Revision == receipt.AuthorizedRevision + 2 &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined &&
               result.Authority == null &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Quarantined,
                   LeaseId: null,
               } snapshot &&
               snapshot.Id == receipt.Handle.SnapshotId &&
               snapshot.Revision == receipt.AuthorizedRevision + 2;
    }

    private static bool IsConfirmedPublicationQuarantine(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return result.Success &&
               result.SnapshotId == receipt.Handle.SnapshotId &&
               result.Revision == receipt.AuthorizedRevision + 1 &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Quarantined,
                   LeaseId: null,
               } snapshot &&
               snapshot.Id == receipt.Handle.SnapshotId &&
               snapshot.Revision == receipt.AuthorizedRevision + 1;
    }

    private static bool IsAuthoritativeAuthorizedQuarantineState(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return result.SnapshotId == receipt.Handle.SnapshotId &&
               result.Revision == receipt.AuthorizedRevision + 1 &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Quarantined &&
               result.Authority == null &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Quarantined,
                   LeaseId: null,
               } snapshot &&
               snapshot.Id == receipt.Handle.SnapshotId &&
               snapshot.Revision == receipt.AuthorizedRevision + 1;
    }

    private static bool IsExactAuthorizedPublication(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoPublicationReceipt receipt)
    {
        return result.Success &&
               result.SnapshotId == receipt.Handle.SnapshotId &&
               result.Revision == receipt.AuthorizedRevision &&
               result.LeaseId == receipt.Handle.LeaseId &&
               IsExactRestoreClaimPresence(
                   result.Authority,
                   receipt.Handle.Key,
                   receipt.Handle.SnapshotId,
                   receipt.Handle.LeaseId,
                   checked(receipt.Handle.ExpectedLifecycleRevision + 4)) &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Consumed,
                   LeaseId: var snapshotLease,
               } snapshot &&
               snapshotLease == receipt.Handle.LeaseId &&
               snapshot.Id == receipt.Handle.SnapshotId &&
               snapshot.Revision == receipt.AuthorizedRevision;
    }

    private static bool IsConfirmedCompletedRestore(
        LuaMDeepCryoWriteResult result,
        LuaMDeepCryoRestoreHandle handle,
        long expectedPreparedRevision)
    {
        return result.Success &&
               result.SnapshotId == handle.SnapshotId &&
               result.Revision == expectedPreparedRevision &&
               result.LeaseId == handle.LeaseId &&
               result.SnapshotStatus == DbLuaMDeepCryoSnapshotStatus.Consumed &&
               result.Snapshot is
               {
                   Status: DbLuaMDeepCryoSnapshotStatus.Consumed,
                   LeaseId: var snapshotLease,
               } snapshot &&
               snapshotLease == handle.LeaseId &&
               snapshot.Id == handle.SnapshotId &&
               snapshot.Revision == expectedPreparedRevision;
    }

    /// <summary>
    /// Releases the wake-vs-fresh-spawn fence only after the caller has either
    /// published the restored body and transferred control or safely disposed it.
    /// </summary>
    public void FinishRestorePublication(LuaMDeepCryoRestoreHandle handle)
    {
        if (_activeRestores.TryGetValue(handle.Key, out var lease) && lease == handle.LeaseId)
        {
            _acknowledgedQuarantineLeases.Remove(handle.LeaseId);
            _acknowledgedQuarantineRequests.Remove(handle.LeaseId);
            _acknowledgedQuarantineRenewals.Remove(handle.LeaseId);
            _activeRestores.Remove(handle.Key);
            _restoreHandles.Remove(handle.Key);
            _consumeStarted.Remove(handle.Key);
            _authorizedPublications.Remove(handle.Key);
            _publicationReceipts.Remove(handle.Key);
            _pendingPublicationOperations.Remove(handle.Key);
            _pendingAuthorizationOperations.Remove(handle.Key);
            _pendingRestoreAborts.Remove(handle.Key);
            _terminalCleanupRegistrations.Remove(handle.LeaseId);
        }
    }

    internal bool TryStartRestorePublication(CharacterKey key, Guid leaseId)
    {
        return !_pendingStoreKeys.ContainsKey(key) &&
               _activeRestores.TryAdd(key, leaseId);
    }

    internal bool IsRestorePublicationActive(CharacterKey key)
    {
        return _activeRestores.ContainsKey(key);
    }

    internal bool FenceRestorePublicationBody(LuaMDeepCryoRestoreHandle handle)
    {
        return IsActiveRestoreOwner(handle) &&
               SuspendPresenceBody(handle.Body, handle.Key);
    }

    public Task AbortRestoreAsync(LuaMDeepCryoRestoreHandle handle, string reason)
    {
        if (!IsActiveRestoreOwner(handle))
            return Task.CompletedTask;

        if (_pendingRestoreAborts.TryGetValue(handle.Key, out var existing))
        {
            if (existing.Handle != handle)
                return Task.CompletedTask;

            return ExecutePendingRestoreAbortAsync(existing);
        }

        var request = new LuaMDeepCryoAbortRequest(
            Guid.NewGuid(),
            handle.Key.UserId,
            handle.Key.ProfileId,
            handle.Key.Slot,
            handle.SnapshotId,
            handle.Revision,
            handle.LeaseId,
            LimitReason(reason),
            DateTime.UtcNow);
        var pending = new PendingRestoreAbort(handle, request, _timing.CurTime);
        _pendingRestoreAborts.Add(handle.Key, pending);
        return ExecutePendingRestoreAbortAsync(pending);
    }

    private async Task ExecutePendingRestoreAbortAsync(PendingRestoreAbort pending)
    {
        var handle = pending.Handle;
        if (pending.Running || pending.Detached ||
            !_pendingRestoreAborts.TryGetValue(handle.Key, out var current) ||
            current != pending ||
            !IsActiveRestoreOwner(handle))
        {
            return;
        }

        pending.Running = true;
        var confirmed = false;
        long? storedRevision = null;
        var terminalWithoutStoredBody = false;
        try
        {
            for (var attempt = 0; attempt < 2 && !confirmed; attempt++)
            {
                try
                {
                    var result = await _db.AbortLuaMDeepCryoRestoreAsync(pending.Request);
                    confirmed = IsConfirmedAbortedRestore(result, handle, handle.Revision + 1);
                    if (confirmed)
                        storedRevision = handle.Revision + 1;
                    if (!confirmed)
                        Log.Error($"Deep-cryo abort attempt {attempt + 1} for {handle.Key} returned {result.Status} without exact Stored proof.");
                }
                catch (Exception e)
                {
                    Log.Error($"Deep-cryo abort attempt {attempt + 1} failed for {handle.Key}: {e}");
                }
            }

            if (!confirmed)
            {
                try
                {
                    var precondition = await _db.GetLuaMDeepCryoStorePreconditionAsync(
                        handle.Key.UserId,
                        handle.Key.ProfileId,
                        handle.Key.Slot);
                    var snapshot = precondition?.ActiveSnapshot;
                    if (precondition?.Authority == null &&
                        snapshot is { Status: DbLuaMDeepCryoSnapshotStatus.Stored, LeaseId: null } &&
                        snapshot.Id == handle.SnapshotId &&
                        (snapshot.Revision == handle.Revision - 1 ||
                         snapshot.Revision == handle.Revision + 1))
                    {
                        storedRevision = snapshot.Revision;
                    }
                    else if (snapshot is { Status: DbLuaMDeepCryoSnapshotStatus.Quarantined, LeaseId: null } &&
                             snapshot.Id == handle.SnapshotId &&
                             snapshot.Revision >= handle.Revision)
                    {
                        terminalWithoutStoredBody = true;
                    }
                    else if (IsDefinitiveForeignRestoreAuthority(precondition?.Authority, handle))
                    {
                        terminalWithoutStoredBody = true;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Deep-cryo abort terminal-state re-read failed for {handle.Key}: {e}");
                }
            }

            if (pending.Detached ||
                !_pendingRestoreAborts.TryGetValue(handle.Key, out current) ||
                current != pending ||
                !IsActiveRestoreOwner(handle))
            {
                return;
            }

            if (!confirmed && storedRevision == null && !terminalWithoutStoredBody)
            {
                SuspendPresenceBody(handle.Body, handle.Key);
                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            if (storedRevision != null)
            {
                if (!RestoreUnpublishedBody(handle, storedRevision.Value))
                {
                    pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                    return;
                }
            }
            else if (!DisposeStalePresenceBody(handle.Body, handle.Key, handle.LeaseId))
            {
                pending.NextAttempt = _timing.CurTime + PublicationRetryInterval;
                return;
            }

            _pendingRestoreAborts.Remove(handle.Key);
            _activeRestores.Remove(handle.Key);
            _restoreHandles.Remove(handle.Key);
            _consumeStarted.Remove(handle.Key);
            _authorizedPublications.Remove(handle.Key);
            _publicationReceipts.Remove(handle.Key);
            _pendingPublicationOperations.Remove(handle.Key);
            _pendingAuthorizationOperations.Remove(handle.Key);
            _terminalCleanupRegistrations.Remove(handle.LeaseId);
            if (terminalWithoutStoredBody &&
                _liveBodies.TryGetValue(handle.Key, out var liveBody) &&
                liveBody == handle.Body)
            {
                _liveBodies.Remove(handle.Key);
            }
        }
        finally
        {
            pending.Running = false;
        }
    }

    internal static bool IsDefinitiveForeignRestoreAuthority(
        LuaMCharacterPresenceAuthorityRecord? authority,
        LuaMDeepCryoRestoreHandle handle)
    {
        if (authority == null ||
            authority.ProfileId != handle.Key.ProfileId ||
            authority.LeaseId == handle.LeaseId ||
            handle.ExpectedLifecycleRevision > long.MaxValue - 4)
        {
            return false;
        }

        return authority.AuthorityLifecycleRevision >
               checked(handle.ExpectedLifecycleRevision + 4);
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
        if (!IsActiveRestoreOwner(handle))
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

    private bool IsActiveRestoreOwner(LuaMDeepCryoRestoreHandle handle)
    {
        return _activeRestores.TryGetValue(handle.Key, out var lease) && lease == handle.LeaseId;
    }

    private void RemoveRestoreClaimOwner(CharacterKey key, Guid leaseId)
    {
        if (_activeRestores.TryGetValue(key, out var activeLease) && activeLease == leaseId)
            _activeRestores.Remove(key);
        if (_restoreHandles.TryGetValue(key, out var handle) && handle.LeaseId == leaseId)
            _restoreHandles.Remove(key);
    }

    private void BindRestoredIdentity(
        EntityUid body,
        CharacterKey key,
        long snapshotId,
        long revision,
        LuaMCharacterPresenceAuthorityRecord authority)
    {
        var identity = EnsureComp<LuaMDeepCryoIdentityComponent>(body);
        identity.UserId = key.UserId;
        identity.ProfileId = key.ProfileId;
        identity.Slot = key.Slot;
        identity.SlotGeneration = _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot);
        identity.LifecycleRevision = -1;
        identity.PresenceLeaseId = authority.LeaseId;
        identity.PresencePhase = authority.Phase;
        identity.PresenceSnapshotId = authority.SnapshotId;
        identity.PresenceLeaseRevision = authority.Revision;
        identity.PresenceLeaseExpiresAtUtc = authority.ExpiresAtUtc;

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
        var cryostorageAttachments = RemoveTransientCryostorageAttachments(root);

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

            // The payload must not contain round-local operations, but the live
            // graph still needs an empty capability if the later database write
            // fails and the player is returned from cryo.
            RestoreDoAfterCapability(doAfterOwners);
            RestoreTransientCryostorageAttachments(cryostorageAttachments);
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

    /// <summary>
    /// Cryostorage containment points at a round-local pod outside the body graph.
    /// Keep it on the live body for the upstream finalizer, but never serialize it.
    /// </summary>
    private List<(EntityUid Entity, bool AllowReEnteringBody, TimeSpan? GracePeriodEndTime, EntityUid? Cryostorage, NetUserId? UserId)>
        RemoveTransientCryostorageAttachments(EntityUid root)
    {
        var attachments = new List<(EntityUid, bool, TimeSpan?, EntityUid?, NetUserId?)>();
        var stack = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);

            if (!TryComp<CryostorageContainedComponent>(uid, out var contained))
                continue;

            attachments.Add((uid, contained.AllowReEnteringBody, contained.GracePeriodEndTime, contained.Cryostorage, contained.UserId));
            // Keep the live component in place: upstream Store validates it both
            // before and after capture. Only null its external pod reference so
            // the durable graph cannot include a round-local cryostorage entity.
            contained.Cryostorage = null;
            Dirty(uid, contained);
        }

        return attachments;
    }

    private void RestoreTransientCryostorageAttachments(
        IEnumerable<(EntityUid Entity, bool AllowReEnteringBody, TimeSpan? GracePeriodEndTime, EntityUid? Cryostorage, NetUserId? UserId)> attachments)
    {
        foreach (var (entity, allowReEnteringBody, gracePeriodEndTime, cryostorage, userId) in attachments)
        {
            if (!Exists(entity))
                continue;

            if (!TryComp<CryostorageContainedComponent>(entity, out var contained))
                continue;
            contained.AllowReEnteringBody = allowReEnteringBody;
            contained.GracePeriodEndTime = gracePeriodEndTime;
            contained.Cryostorage = cryostorage;
            contained.UserId = userId;
            Dirty(entity, contained);
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
            RestorePrototypeDoAfterCapabilities(root, expectedPrototype);
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

    /// <summary>
    /// Restores the empty, prototype-owned capability after round-local do-after
    /// operations have been removed from the complete persisted graph.
    /// </summary>
    private void RestorePrototypeDoAfterCapabilities(EntityUid root, EntityPrototype expectedRootPrototype)
    {
        var stack = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !Exists(uid))
                continue;

            var prototype = uid == root
                ? expectedRootPrototype
                : MetaData(uid).EntityPrototype;
            if (prototype != null && prototype.TryGetComponent<DoAfterComponent>(out _, _componentFactory))
                EnsureComp<DoAfterComponent>(uid);

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
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

    private void BindPlayableIdentity(
        EntityUid body,
        CharacterKey key,
        long slotGeneration,
        LuaMCharacterPresenceAuthorityRecord authority)
    {
        if (!IsExactPlayablePresence(
                authority,
                key,
                authority.LeaseId,
                authority.AuthorityLifecycleRevision,
                authority.SnapshotId) ||
            !HasSafePlayablePresenceWindow(authority))
        {
            throw new InvalidOperationException($"Invalid playable presence authority for {key}.");
        }

        BindPresenceIdentity(body, key, slotGeneration, authority);
    }

    private void BindPresenceIdentity(
        EntityUid body,
        CharacterKey key,
        long slotGeneration,
        LuaMCharacterPresenceAuthorityRecord authority)
    {
        var identity = EnsureComp<LuaMDeepCryoIdentityComponent>(body);
        identity.UserId = key.UserId;
        identity.ProfileId = key.ProfileId;
        identity.Slot = key.Slot;
        identity.SlotGeneration = slotGeneration;
        identity.LifecycleRevision = authority.Phase == DbLuaMCharacterPresencePhase.Playable
            ? authority.AuthorityLifecycleRevision
            : -1;
        identity.PresenceLeaseId = authority.LeaseId;
        identity.PresencePhase = authority.Phase;
        identity.PresenceSnapshotId = authority.SnapshotId;
        identity.PresenceLeaseRevision = authority.Revision;
        identity.PresenceLeaseExpiresAtUtc = authority.ExpiresAtUtc;
    }

    private void ClearPresenceAuthority(EntityUid body)
    {
        if (!TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity))
            return;

        ClearPresenceAuthority(identity);
    }

    private void ClearPresenceAuthorityIfMatches(EntityUid body, Guid leaseId)
    {
        if (!TryComp<LuaMDeepCryoIdentityComponent>(body, out var identity) ||
            identity.PresenceLeaseId != leaseId)
        {
            return;
        }

        var key = new CharacterKey(identity.UserId, identity.ProfileId, identity.Slot);
        if (_pendingPresenceRenewals.TryGetValue(key, out var renewal) &&
            renewal.Request.LeaseId == leaseId)
        {
            _pendingPresenceRenewals.Remove(key);
        }

        ClearPresenceAuthority(identity);
    }

    private static void ClearPresenceAuthority(LuaMDeepCryoIdentityComponent identity)
    {
        identity.LifecycleRevision = -1;
        identity.PresenceLeaseId = Guid.Empty;
        identity.PresencePhase = default;
        identity.PresenceSnapshotId = null;
        identity.PresenceLeaseRevision = -1;
        identity.PresenceLeaseExpiresAtUtc = default;
    }

    private static bool IsExactPlayablePresence(
        LuaMCharacterPresenceAuthorityRecord? authority,
        CharacterKey key,
        Guid leaseId,
        long authorityLifecycleRevision,
        long? snapshotId)
    {
        return authority is
               {
                   Phase: DbLuaMCharacterPresencePhase.Playable,
                   Revision: >= 0,
               } &&
               authority.ProfileId == key.ProfileId &&
               authority.LeaseId == leaseId &&
               authority.SnapshotId == snapshotId &&
               authority.AuthorityLifecycleRevision == authorityLifecycleRevision;
    }

    private static bool HasSafePlayablePresenceWindow(
        LuaMCharacterPresenceAuthorityRecord? authority)
    {
        return HasSafePlayablePresenceWindow(authority, DateTime.UtcNow);
    }

    internal static bool HasSafePlayablePresenceWindow(
        LuaMCharacterPresenceAuthorityRecord? authority,
        DateTime nowUtc)
    {
        return authority != null &&
               NormalizeUtc(authority.ExpiresAtUtc) >
               NormalizeUtc(nowUtc) + PlayablePresenceRenewLead;
    }

    internal static bool HasSafeRestorePublicationWindow(
        LuaMCharacterPresenceAuthorityRecord? authority,
        DateTime nowUtc)
    {
        return authority != null &&
               NormalizeUtc(authority.ExpiresAtUtc) >
               nowUtc.ToUniversalTime() + RestorePublicationSafetyWindow;
    }

    // SQLite materializes UTC timestamps as Unspecified. Treat those database
    // values as UTC rather than converting them through the host local zone.
    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static bool IsExactRestoreClaimPresence(
        LuaMCharacterPresenceAuthorityRecord? authority,
        CharacterKey key,
        long snapshotId,
        Guid leaseId,
        long authorityLifecycleRevision)
    {
        return authority is
               {
                   Phase: DbLuaMCharacterPresencePhase.RestoreClaim,
                   Revision: >= 0,
               } &&
               authority.ProfileId == key.ProfileId &&
               authority.SnapshotId == snapshotId &&
               authority.LeaseId == leaseId &&
               authority.AuthorityLifecycleRevision == authorityLifecycleRevision;
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

    private enum PendingPublicationKind : byte
    {
        Rollback,
        Acknowledge,
        Quarantine,
    }

    private enum AcknowledgementSettlementStatus : byte
    {
        Pending,
        Published,
        Compensated,
    }

    private readonly record struct AcknowledgementSettlementResult(
        AcknowledgementSettlementStatus Status,
        bool AcknowledgementCommitted,
        LuaMCharacterPresenceAuthorityRecord? Authority);

    private enum AuthorizationAttemptStatus : byte
    {
        Authorized,
        Rejected,
        Indeterminate,
        Terminal,
    }

    private enum DetachedSettlementKind : byte
    {
        Abort,
        Rollback,
        ResolveAuthorization,
        Acknowledge,
        Quarantine,
    }

    private sealed class PendingAuthorizationOperation
    {
        public LuaMDeepCryoPublicationReceipt Receipt { get; }
        public TaskCompletionSource<LuaMDeepCryoAuthorizationStatus> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }
        public bool Detached { get; set; }

        public PendingAuthorizationOperation(
            LuaMDeepCryoPublicationReceipt receipt,
            TimeSpan nextAttempt)
        {
            Receipt = receipt;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class PendingRestoreAbort
    {
        public LuaMDeepCryoRestoreHandle Handle { get; }
        public LuaMDeepCryoAbortRequest Request { get; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }
        public bool Detached { get; set; }

        public PendingRestoreAbort(
            LuaMDeepCryoRestoreHandle handle,
            LuaMDeepCryoAbortRequest request,
            TimeSpan nextAttempt)
        {
            Handle = handle;
            Request = request;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class PendingPublicationOperation
    {
        public LuaMDeepCryoPublicationReceipt Receipt { get; }
        public PendingPublicationKind Kind { get; }
        public string? Reason { get; }
        public Action? OnConfirmed { get; set; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }
        public bool Detached { get; set; }
        public bool AcknowledgementCommitted { get; set; }
        public bool AcknowledgementCompensated { get; set; }
        public LuaMCharacterPresenceAuthorityRecord? AcknowledgedAuthority { get; set; }

        public PendingPublicationOperation(
            LuaMDeepCryoPublicationReceipt receipt,
            PendingPublicationKind kind,
            string? reason,
            Action? onConfirmed,
            TimeSpan nextAttempt)
        {
            Receipt = receipt;
            Kind = kind;
            Reason = reason;
            OnConfirmed = onConfirmed;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class DetachedRoundSettlement
    {
        public LuaMDeepCryoRestoreHandle Handle { get; }
        public LuaMDeepCryoPublicationReceipt? Receipt { get; }
        public LuaMDeepCryoAbortRequest? AbortRequest { get; }
        public PendingAuthorizationOperation? PendingAuthorization { get; }
        public TerminalCleanupRegistration? TerminalCleanup { get; }
        public DetachedSettlementKind Kind { get; set; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }
        public bool AcknowledgementCommitted { get; set; }
        public bool AcknowledgementCompensated { get; set; }
        public LuaMCharacterPresenceAuthorityRecord? AcknowledgedAuthority { get; set; }

        public DetachedRoundSettlement(
            LuaMDeepCryoRestoreHandle handle,
            LuaMDeepCryoPublicationReceipt? receipt,
            LuaMDeepCryoAbortRequest? abortRequest,
            PendingAuthorizationOperation? pendingAuthorization,
            TerminalCleanupRegistration? terminalCleanup,
            DetachedSettlementKind kind,
            TimeSpan nextAttempt,
            bool acknowledgementCommitted = false,
            bool acknowledgementCompensated = false,
            LuaMCharacterPresenceAuthorityRecord? acknowledgedAuthority = null)
        {
            Handle = handle;
            Receipt = receipt;
            AbortRequest = abortRequest;
            PendingAuthorization = pendingAuthorization;
            TerminalCleanup = terminalCleanup;
            Kind = kind;
            NextAttempt = nextAttempt;
            AcknowledgementCommitted = acknowledgementCommitted;
            AcknowledgementCompensated = acknowledgementCompensated;
            AcknowledgedAuthority = acknowledgedAuthority;
        }
    }

    private sealed class TerminalCleanupRegistration
    {
        private Action? _callback;
        public bool Invoked { get; private set; }

        public TerminalCleanupRegistration(Action? callback)
        {
            _callback = callback;
        }

        public bool TryTake(out Action? callback)
        {
            callback = null;
            if (Invoked)
                return false;

            Invoked = true;
            callback = _callback;
            _callback = null;
            return true;
        }
    }

    private sealed class PendingStoreOperation
    {
        public EntityUid Body { get; }
        public EntityUid Pod { get; }
        public CharacterKey Key { get; }
        public long SlotGeneration { get; }
        public Guid OperationId => Request.OperationId;
        public LuaMDeepCryoStoreRequest Request { get; }
        public LuaMDeepCryoSource Source { get; }
        public Action<LuaMDeepCryoStoreCompletion> Completion { get; }
        public int RoundCleanupGeneration { get; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }
        public bool Detached { get; set; }
        public bool CallbackFailed { get; set; }
        public bool NoCommitCallbackFailed { get; set; }
        public string? NoCommitFailureReason { get; set; }
        public LuaMCharacterPresenceRenewRequest? NoCommitRenewRequest { get; set; }
        public long? NoCommitRenewSnapshotId { get; set; }

        public PendingStoreOperation(
            EntityUid body,
            EntityUid pod,
            CharacterKey key,
            long slotGeneration,
            LuaMDeepCryoStoreRequest request,
            LuaMDeepCryoSource source,
            Action<LuaMDeepCryoStoreCompletion> completion,
            int roundCleanupGeneration,
            TimeSpan nextAttempt)
        {
            Body = body;
            Pod = pod;
            Key = key;
            SlotGeneration = slotGeneration;
            Request = request;
            Source = source;
            Completion = completion;
            RoundCleanupGeneration = roundCleanupGeneration;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class PendingPresenceRenewal
    {
        public EntityUid Body { get; }
        public CharacterKey Key { get; }
        public LuaMCharacterPresenceRenewRequest Request { get; }
        public long? SnapshotId { get; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }
        public bool ReleaseAfterCompletion { get; set; }
        public bool ReservedForRestoreSettlement { get; set; }

        public PendingPresenceRenewal(
            EntityUid body,
            CharacterKey key,
            LuaMCharacterPresenceRenewRequest request,
            long? snapshotId,
            TimeSpan nextAttempt)
        {
            Body = body;
            Key = key;
            Request = request;
            SnapshotId = snapshotId;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class PendingPresenceRelease
    {
        public CharacterKey Key { get; }
        public LuaMCharacterPresenceReleaseRequest Request { get; }
        public string Reason { get; }
        public EntityUid BodyToDispose { get; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }

        public PendingPresenceRelease(
            CharacterKey key,
            LuaMCharacterPresenceReleaseRequest request,
            string reason,
            EntityUid bodyToDispose,
            TimeSpan nextAttempt)
        {
            Key = key;
            Request = request;
            Reason = reason;
            BodyToDispose = bodyToDispose;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class PendingConsumedSpawnTicket
    {
        public SpawnLifecycleTicket Ticket { get; }
        public EntityUid Body { get; }
        public bool RestoreOnPublish { get; }
        public TimeSpan NextAttempt { get; set; }
        public bool Running { get; set; }

        public PendingConsumedSpawnTicket(
            SpawnLifecycleTicket ticket,
            EntityUid body,
            bool restoreOnPublish,
            TimeSpan nextAttempt)
        {
            Ticket = ticket;
            Body = body;
            RestoreOnPublish = restoreOnPublish;
            NextAttempt = nextAttempt;
        }
    }

    private sealed class SuspendedPresenceBody
    {
        public CharacterKey Key { get; }
        public EntityCoordinates OriginalCoordinates { get; }
        public MapCoordinates OriginalMapCoordinates { get; }
        public Angle OriginalRotation { get; }
        public EntityUid? ContainerOwner { get; }
        public string? ContainerId { get; }
        public EntityUid? ControlGhost { get; set; }
        public bool RestoreBodyControl { get; set; }
        public bool PreviousCanReturn { get; set; }
        public bool ControlGhostStateCaptured { get; set; }
        public bool PreserveUpstreamGhostControl { get; set; }

        public SuspendedPresenceBody(
            CharacterKey key,
            EntityCoordinates originalCoordinates,
            MapCoordinates originalMapCoordinates,
            Angle originalRotation,
            EntityUid? containerOwner,
            string? containerId)
        {
            Key = key;
            OriginalCoordinates = originalCoordinates;
            OriginalMapCoordinates = originalMapCoordinates;
            OriginalRotation = originalRotation;
            ContainerOwner = containerOwner;
            ContainerId = containerId;
        }
    }

    private readonly record struct PendingFreshSpawnRequest(
        ICommonSession Player,
        CharacterKey Key,
        long SlotGeneration,
        string? JobId,
        EntityUid Station);

    private sealed class SpawnLifecycleTicket
    {
        public CharacterKey Key { get; }
        public long SlotGeneration { get; }
        public Guid LeaseId { get; }
        public LuaMCharacterPresenceReserveRequest? ReserveRequest { get; }
        public LuaMCharacterPresenceReclaimRequest? ReclaimRequest { get; }
        public LuaMCharacterPresenceAuthorityRecord? Authority { get; private set; }
        public LuaMCharacterPresencePublishRequest? PublishRequest { get; private set; }
        public LuaMCharacterPresenceReleaseRequest? FreshReleaseRequest { get; private set; }
        public LuaMCharacterPresenceReleaseRequest? SettlementReleaseRequest { get; private set; }
        public LuaMCharacterPresenceAuthorityRecord? PublishedAuthority { get; set; }
        public bool PublishAttempted { get; set; }

        public SpawnLifecycleTicket(
            CharacterKey key,
            long slotGeneration,
            Guid leaseId,
            LuaMCharacterPresenceReserveRequest? reserveRequest = null,
            LuaMCharacterPresenceReclaimRequest? reclaimRequest = null)
        {
            Key = key;
            SlotGeneration = slotGeneration;
            LeaseId = leaseId;
            ReserveRequest = reserveRequest;
            ReclaimRequest = reclaimRequest;
        }

        public void BindReservation(
            LuaMCharacterPresenceAuthorityRecord authority,
            TimeSpan playableLeaseDuration)
        {
            Authority ??= authority;
            if (PublishRequest != null)
                return;

            var publishedAtUtc = DateTime.UtcNow;
            PublishRequest = new LuaMCharacterPresencePublishRequest(
                Guid.NewGuid(),
                Key.UserId,
                Key.ProfileId,
                Key.Slot,
                authority.LeaseId,
                authority.Revision,
                authority.AuthorityLifecycleRevision,
                publishedAtUtc,
                publishedAtUtc + playableLeaseDuration);

            var releasedAtUtc = DateTime.UtcNow;
            FreshReleaseRequest = new LuaMCharacterPresenceReleaseRequest(
                Guid.NewGuid(),
                Key.UserId,
                Key.ProfileId,
                Key.Slot,
                authority.LeaseId,
                DbLuaMCharacterPresencePhase.FreshReserved,
                null,
                authority.Revision,
                authority.AuthorityLifecycleRevision,
                "fresh-spawn-ticket-abandoned",
                releasedAtUtc);
        }

        public LuaMCharacterPresenceReleaseRequest GetOrCreateSettlementRelease(
            LuaMCharacterPresenceAuthorityRecord authority,
            string reason)
        {
            SettlementReleaseRequest ??= new LuaMCharacterPresenceReleaseRequest(
                Guid.NewGuid(),
                Key.UserId,
                Key.ProfileId,
                Key.Slot,
                authority.LeaseId,
                authority.Phase,
                authority.SnapshotId,
                authority.Revision,
                authority.AuthorityLifecycleRevision,
                reason,
                DateTime.UtcNow);
            return SettlementReleaseRequest;
        }
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
    bool Deserialized,
    long ExpectedLifecycleRevision,
    LuaMDeepCryoSnapshotRecord? RetainedSnapshot = null,
    LuaMCharacterPresenceAuthorityRecord? PresenceAuthority = null);

public sealed record LuaMDeepCryoPublicationReceipt(
    LuaMDeepCryoRestoreHandle Handle,
    Guid CompletionOperationId,
    Guid AuthorizationOperationId,
    Guid RollbackOperationId,
    Guid AcknowledgeOperationId,
    Guid QuarantineOperationId,
    Guid AbortOperationId,
    long PreparedRevision,
    long AuthorizedRevision,
    DateTime CompletedAtUtc,
    DateTime AuthorizedAtUtc,
    DateTime RolledBackAtUtc,
    DateTime AcknowledgedAtUtc,
    DateTime QuarantinedAtUtc,
    DateTime AbortedAtUtc,
    int RoundCleanupGeneration,
    DateTime PlayableLeaseExpiresAtUtc);

public enum LuaMDeepCryoAuthorizationStatus : byte
{
    Authorized,
    Rejected,
    Terminal,
}

public readonly record struct LuaMDeepCryoConsumeResult(
    LuaMDeepCryoConsumeStatus Status,
    LuaMDeepCryoPublicationReceipt? Receipt)
{
    public static LuaMDeepCryoConsumeResult Failed => new(LuaMDeepCryoConsumeStatus.Failed, null);
    public static LuaMDeepCryoConsumeResult Busy => new(LuaMDeepCryoConsumeStatus.Busy, null);
}

public enum LuaMDeepCryoConsumeStatus : byte
{
    Success,
    Failed,
    Indeterminate,
    Busy,
}

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
