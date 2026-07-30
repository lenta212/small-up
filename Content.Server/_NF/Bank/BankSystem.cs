using System.Linq;
using System.Threading;
using Content.Server._NF.CryoSleep;
using Content.Server._Mono.MonoCoins;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.GameTicking;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Chat;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Player;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Content.Shared._Mono.CCVar; // Mono
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.Events;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Players;
using Robust.Server.Player;
using Robust.Shared.Network;

namespace Content.Server._NF.Bank;

/// <summary>
/// A durable bank mutation has an outcome that cannot safely be retried, either
/// because its initial commit could not be classified or because a required
/// compensating rollback failed. Callers must keep their world reservation and
/// prevent an automatic retry.
/// </summary>
public sealed class BankMutationRollbackException : Exception
{
    public BankMutationRollbackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed partial class BankSystem : SharedBankSystem
{
    public const float PayrollIntervalSeconds = 3600f;
    public const int PayrollMinimumHourly = 75000;
    public const int PayrollSpecialistHourly = 100000;
    public const int PayrollHazardHourly = 125000;
    public const int PayrollOfficerHourly = 150000;
    public const int PayrollCommandHourly = 200000;
    public const int PayrollCentralCommandHourly = 250000;

    private static readonly Dictionary<string, int> PayrollByJob = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Passenger"] = PayrollMinimumHourly,
        ["Contractor"] = PayrollMinimumHourly,
        ["Janitor"] = PayrollMinimumHourly,
        ["Bartender"] = PayrollMinimumHourly,
        ["Botanist"] = PayrollMinimumHourly,
        ["Chef"] = PayrollMinimumHourly,
        ["Reporter"] = PayrollMinimumHourly,
        ["Psychologist"] = PayrollMinimumHourly,
        ["Boxer"] = PayrollMinimumHourly,
        ["Zookeeper"] = PayrollMinimumHourly,
        ["Pilot"] = PayrollSpecialistHourly,
        ["CargoTechnician"] = PayrollSpecialistHourly,
        ["StationEngineer"] = PayrollSpecialistHourly,
        ["AtmosphericTechnician"] = PayrollSpecialistHourly,
        ["TechnicalAssistant"] = PayrollMinimumHourly,
        ["Scientist"] = PayrollSpecialistHourly,
        ["MedicalDoctor"] = PayrollSpecialistHourly,
        ["Chemist"] = PayrollSpecialistHourly,
        ["Paramedic"] = PayrollSpecialistHourly,
        ["MdMedic"] = PayrollHazardHourly,
        ["SalvageSpecialist"] = PayrollHazardHourly,
        ["Mercenary"] = PayrollHazardHourly,
        ["SecurityGuard"] = PayrollHazardHourly,
        ["Deputy"] = PayrollHazardHourly,
        ["NFDetective"] = PayrollHazardHourly,
        ["Brigmedic"] = PayrollHazardHourly,
        ["Cadet"] = PayrollSpecialistHourly,
        ["Bailiff"] = PayrollOfficerHourly,
        ["SeniorOfficer"] = PayrollOfficerHourly,
        ["Sergeant"] = PayrollOfficerHourly,
        ["Sheriff"] = PayrollCommandHourly,
        ["StationRepresentative"] = PayrollOfficerHourly,
        ["Stc"] = PayrollOfficerHourly,
        ["Quartermaster"] = PayrollCommandHourly,
        ["ChiefEngineer"] = PayrollCommandHourly,
        ["ChiefMedicalOfficer"] = PayrollCommandHourly,
        ["ResearchDirector"] = PayrollCommandHourly,
        ["HeadOfPersonnel"] = PayrollCommandHourly,
        ["Captain"] = PayrollCentralCommandHourly,
        ["CentralCommandOfficial"] = PayrollCentralCommandHourly,
        ["ERTLeader"] = PayrollCentralCommandHourly,
        ["ERTSecurity"] = PayrollCentralCommandHourly,
        ["ERTMedical"] = PayrollCentralCommandHourly,
        ["ERTEngineer"] = PayrollCentralCommandHourly,
        ["USSPRifleman"] = PayrollHazardHourly,
        ["USSPMedic"] = PayrollHazardHourly,
        ["USSPCorporal"] = PayrollOfficerHourly,
        ["USSPSergeant"] = PayrollOfficerHourly,
        ["USSPCommissar"] = PayrollCommandHourly,
        ["TsfEngineer"] = PayrollHazardHourly,
        ["TsfBorg"] = PayrollHazardHourly,
        ["PdvBorg"] = PayrollHazardHourly,
        ["Pirate"] = PayrollHazardHourly,
        ["PirateFirstMate"] = PayrollOfficerHourly,
        ["PirateCaptain"] = PayrollCommandHourly,
        ["Prisoner"] = 0,
    };

    [Dependency] private IServerPreferencesManager _prefsManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private MonoCoinsManager _coins = default!;
    [Dependency] private SharedJobSystem _job = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private GameTicker _gameTicker = default!;

    private ISawmill _log = default!;
    private readonly Dictionary<NetUserId, float> _payrollTimers = new();
    private readonly HashSet<NetUserId> _payrollMissingJobWarnings = new();
    private readonly Dictionary<NetUserId, PayrollDepositOwner> _payrollDepositsInFlight = new();
    private long _payrollGeneration;
    private readonly object _balanceMutationLock = new();
    private readonly HashSet<BankSlotKey> _activeBalanceMutations = new();
    private readonly HashSet<BankProfileIdentity> _blockedBalanceMutations = new();

    private readonly record struct BankSlotKey(NetUserId UserId, int Slot);

    private readonly record struct BankProfileIdentity(NetUserId UserId, int ProfileId);

    private readonly record struct PayrollDepositOwner(long Generation, Guid Token);

    internal enum ProfileSaveFailureOutcome
    {
        ConfirmedOriginal,
        ConfirmedMutation,
        Unknown,
    }

    private readonly record struct ProfileSaveFailureResolution(
        ProfileSaveFailureOutcome Outcome,
        Exception? VerificationFailure = null);

    private sealed class ExactBalanceUpdateRejectedException : InvalidOperationException
    {
        public CharacterBankBalanceUpdateResult Result { get; }

        public ExactBalanceUpdateRejectedException(
            NetUserId userId,
            int profileId,
            int slot,
            CharacterBankBalanceUpdateResult result)
            : base(
                $"Exact bank balance update failed for {userId}/{profileId} in slot {slot}: " +
                $"{result.Status} (current: {result.CurrentBalance?.ToString() ?? "missing"}).")
        {
            Result = result;
        }
    }

    internal static ProfileSaveFailureOutcome ClassifyProfileSaveFailure(
        int? persistedBalance,
        int originalBalance,
        int mutatedBalance)
    {
        if (persistedBalance == mutatedBalance)
            return ProfileSaveFailureOutcome.ConfirmedMutation;

        if (persistedBalance == originalBalance)
            return ProfileSaveFailureOutcome.ConfirmedOriginal;

        return ProfileSaveFailureOutcome.Unknown;
    }

    private sealed class BalanceMutationLease : IDisposable
    {
        private BankSystem? _owner;
        private readonly BankSlotKey[] _slots;
        private readonly BankProfileIdentity[] _identities;
        private readonly IDisposable _profileMutationLease;

        public BalanceMutationLease(
            BankSystem owner,
            BankSlotKey[] slots,
            BankProfileIdentity[] identities,
            IDisposable profileMutationLease)
        {
            _owner = owner;
            _slots = slots;
            _identities = identities;
            _profileMutationLease = profileMutationLease;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null)
                return;

            owner.ReleaseBalanceMutation(_slots);
            _profileMutationLease.Dispose();
        }

        public BankProfileIdentity GetIdentity(NetUserId userId, int profileId)
        {
            foreach (var identity in _identities)
            {
                if (identity.UserId == userId && identity.ProfileId == profileId)
                    return identity;
            }

            throw new InvalidOperationException(
                $"Balance mutation lease does not own stable profile {userId}/{profileId}.");
        }

        public bool TryGetIdentity(NetUserId userId, out BankProfileIdentity identity)
        {
            foreach (var candidate in _identities)
            {
                if (candidate.UserId == userId)
                {
                    identity = candidate;
                    return true;
                }
            }

            identity = default;
            return false;
        }
    }

    private bool TryAcquireBalanceMutation(
        NetUserId userId,
        int slot,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        var slotKey = new BankSlotKey(userId, slot);
        var identity = TryGetBankProfileIdentity(slotKey);
        if (identity == null &&
            (!_playerManager.TryGetSessionById(userId, out var session) ||
             ServerPreferencesManager.ShouldStorePrefs(session.Channel.AuthType)))
        {
            lease = null;
            return false;
        }

        return TryAcquireBalanceMutation(
            slotKey,
            identity,
            null,
            null,
            out lease);
    }

    private bool TryAcquireBalanceMutation(
        NetUserId userId,
        int slot,
        int profileId,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        return TryAcquireBalanceMutation(
            new BankSlotKey(userId, slot),
            new BankProfileIdentity(userId, profileId),
            null,
            null,
            out lease);
    }

    private bool TryAcquireBalanceMutation(
        NetUserId firstUserId,
        int firstSlot,
        int firstProfileId,
        NetUserId secondUserId,
        int secondSlot,
        int secondProfileId,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        return TryAcquireBalanceMutation(
            new BankSlotKey(firstUserId, firstSlot),
            new BankProfileIdentity(firstUserId, firstProfileId),
            new BankSlotKey(secondUserId, secondSlot),
            new BankProfileIdentity(secondUserId, secondProfileId),
            out lease);
    }

    private bool TryAcquireBalanceMutation(
        BankSlotKey first,
        BankProfileIdentity? firstIdentity,
        BankSlotKey? second,
        BankProfileIdentity? secondIdentity,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        var identities = new List<BankProfileIdentity>(2);
        if (firstIdentity is { } stableFirst)
            identities.Add(stableFirst);
        if (secondIdentity is { } stableSecond && stableSecond != firstIdentity)
            identities.Add(stableSecond);

        lock (_balanceMutationLock)
        {
            if (_activeBalanceMutations.Contains(first) ||
                IsBalanceMutationBlocked(first, firstIdentity) ||
                second is { } secondKey &&
                secondKey != first &&
                (_activeBalanceMutations.Contains(secondKey) ||
                 IsBalanceMutationBlocked(secondKey, secondIdentity)))
            {
                lease = null;
                return false;
            }

            var mutationUserIds = second is { } mutationSecond && mutationSecond.UserId != first.UserId
                ? new[] { first.UserId, mutationSecond.UserId }
                : new[] { first.UserId };
            if (!_prefsManager.TryAcquireProfileMutation(mutationUserIds, out var profileMutationLease))
            {
                lease = null;
                return false;
            }
            var slots = second is { } distinct && distinct != first
                ? new[] { first, distinct }
                : new[] { first };
            try
            {
                foreach (var slot in slots)
                    _activeBalanceMutations.Add(slot);

                lease = new BalanceMutationLease(
                    this,
                    slots,
                    identities.ToArray(),
                    profileMutationLease);
                return true;
            }
            catch
            {
                foreach (var slot in slots)
                    _activeBalanceMutations.Remove(slot);
                profileMutationLease.Dispose();
                throw;
            }
        }
    }

    private BankProfileIdentity? TryGetBankProfileIdentity(BankSlotKey slot)
    {
        return _prefsManager.TryGetCharacterProfileId(slot.UserId, slot.Slot, out var profileId)
            ? new BankProfileIdentity(slot.UserId, profileId)
            : null;
    }

    private bool IsBalanceMutationBlocked(BankSlotKey slot, BankProfileIdentity? identity)
    {
        if (identity is { } stableIdentity)
            return _blockedBalanceMutations.Contains(stableIdentity);

        // Missing sidecar state is unusual (guests excepted). If this user has a
        // stable fail-closed block, do not let a synchronous legacy mutation bypass
        // it until Load/Refresh/Create resolves the current ProfileId.
        return _blockedBalanceMutations.Any(blocked => blocked.UserId == slot.UserId);
    }

    private void ReleaseBalanceMutation(IEnumerable<BankSlotKey> keys)
    {
        lock (_balanceMutationLock)
        {
            foreach (var key in keys)
                _activeBalanceMutations.Remove(key);
        }
    }

    private void BlockBalanceMutation(
        BalanceMutationLease lease,
        NetUserId userId,
        int slot,
        string operation,
        Exception exception)
    {
        BlockBalanceMutation(
            lease,
            userId,
            slot,
            $"failed {operation} compensation: {exception}");
    }

    private void BlockBalanceMutation(
        BalanceMutationLease lease,
        NetUserId userId,
        int slot,
        string reason)
    {
        if (lease.TryGetIdentity(userId, out var identity))
        {
            lock (_balanceMutationLock)
                _blockedBalanceMutations.Add(identity);

            _log.Error($"CRITICAL: bank profile {userId}/{identity.ProfileId} (slot {slot}) blocked: {reason}");
            return;
        }

        _log.Error($"CRITICAL: unresolved runtime-only bank slot {userId}:{slot} could not be stably blocked: {reason}");
    }

    private void BlockBalanceMutationProfile(
        BalanceMutationLease lease,
        NetUserId userId,
        int profileId,
        int slot,
        string reason)
    {
        var identity = lease.GetIdentity(userId, profileId);
        lock (_balanceMutationLock)
            _blockedBalanceMutations.Add(identity);

        _log.Error($"CRITICAL: bank profile {userId}/{profileId} (slot {slot}) blocked: {reason}");
    }

    /// <summary>
    /// Returns whether the selected character cannot safely start another bank
    /// mutation. Resolution failures are treated as pending so callers can fail
    /// closed before performing related world-side effects.
    /// </summary>
    public bool IsBankOperationPending(EntityUid mobUid)
    {
        if (!_playerManager.TryGetSessionByEntity(mobUid, out var session) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs) ||
            !prefs.Characters.TryGetValue(prefs.SelectedCharacterIndex, out var character) ||
            character is not HumanoidCharacterProfile)
        {
            return true;
        }

        var slot = new BankSlotKey(session.UserId, prefs.SelectedCharacterIndex);
        var identity = TryGetBankProfileIdentity(slot);
        if (identity == null && ServerPreferencesManager.ShouldStorePrefs(session.Channel.AuthType))
            return true;

        lock (_balanceMutationLock)
        {
            return _activeBalanceMutations.Contains(slot) || IsBalanceMutationBlocked(slot, identity);
        }
    }

    private async Task ReconcileProfileAfterSaveFailureAsync(
        ICommonSession session,
        int slot,
        string operation,
        bool allowRefresh = true,
        int? expectedProfileId = null)
    {
        try
        {
            var profileId = expectedProfileId;
            if (profileId == null)
            {
                if (!_prefsManager.TryGetCharacterProfileId(session.UserId, slot, out var currentProfileId))
                    return;

                profileId = currentProfileId;
            }

            var persistedBalance = await _db.GetCharacterBankBalanceAsync(
                session.UserId,
                profileId.Value,
                slot,
                CancellationToken.None);
            if (persistedBalance is { } balance)
            {
                _prefsManager.TryApplyPersistedBankBalance(session.UserId, slot, profileId.Value, balance);
            }
            else if (allowRefresh)
            {
                // The exact profile disappeared. Reload the current identity rather
                // than projecting the failed old mutation onto a replacement.
                await _prefsManager.RefreshPreferencesAsync(session, CancellationToken.None);
            }

            if (session.AttachedEntity is { Valid: true } attached)
                SyncBankBalance(attached);
        }
        catch (Exception exception)
        {
            _log.Error($"{operation} preference reconciliation failed for {session.UserId}: {exception}");
        }
    }

    private async Task<ProfileSaveFailureResolution> ResolveInitialProfileSaveFailureAsync(
        ICommonSession session,
        BalanceMutationLease lease,
        int slot,
        int originalBalance,
        int mutatedBalance,
        string operation)
    {
        int? persistedBalance;
        if (!lease.TryGetIdentity(session.UserId, out var identity))
        {
            return new ProfileSaveFailureResolution(
                ProfileSaveFailureOutcome.Unknown,
                new InvalidOperationException($"No stable profile identity exists for {session.UserId}:{slot}."));
        }

        try
        {
            persistedBalance = await _db.GetCharacterBankBalanceAsync(
                session.UserId,
                identity.ProfileId,
                slot,
                CancellationToken.None);
            if (persistedBalance == null)
            {
                // An archived/replaced exact identity cannot have updated the new
                // occupant of this slot.
                return new ProfileSaveFailureResolution(ProfileSaveFailureOutcome.ConfirmedOriginal);
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Could not verify persisted balance after {operation} save failure for {session.UserId}:{slot}: {exception}");
            return new ProfileSaveFailureResolution(
                ProfileSaveFailureOutcome.Unknown,
                exception);
        }

        var outcome = ClassifyProfileSaveFailure(
            persistedBalance,
            originalBalance,
            mutatedBalance);

        try
        {
            if (!_prefsManager.TryApplyPersistedBankBalance(
                    session.UserId,
                    slot,
                    identity.ProfileId,
                    persistedBalance.Value))
            {
                _log.Warning($"Skipped verified balance projection for stale profile " +
                             $"{session.UserId}/{identity.ProfileId} after {operation} save failure.");
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Could not reconcile verified balance after {operation} save failure for {session.UserId}:{slot}: {exception}");
            return new ProfileSaveFailureResolution(
                ProfileSaveFailureOutcome.Unknown,
                exception);
        }

        try
        {
            if (session.AttachedEntity is { Valid: true } attached)
                SyncBankBalance(attached);
        }
        catch (Exception exception)
        {
            // The authoritative balance and cache are already reconciled. A stale
            // component/UI projection cannot make a database retry safe or unsafe.
            _log.Error($"Could not project verified balance after {operation} save failure for {session.UserId}:{slot}: {exception}");
        }

        if (outcome != ProfileSaveFailureOutcome.Unknown)
            return new ProfileSaveFailureResolution(outcome);

        var unexpectedBalance = new InvalidOperationException(
            $"Fresh balance {persistedBalance.Value} matched neither original {originalBalance} nor mutation {mutatedBalance} for {session.UserId}:{slot} after {operation} save failure.");
        _log.Error(unexpectedBalance.Message);
        return new ProfileSaveFailureResolution(outcome, unexpectedBalance);
    }

    private async Task<ProfileSaveFailureResolution> ResolveOfflineProfileSaveFailureAsync(
        NetUserId userId,
        BalanceMutationLease lease,
        int slot,
        int originalBalance,
        int mutatedBalance,
        string operation)
    {
        if (!lease.TryGetIdentity(userId, out var identity))
        {
            return new ProfileSaveFailureResolution(
                ProfileSaveFailureOutcome.Unknown,
                new InvalidOperationException($"No stable profile identity exists for {userId}:{slot}."));
        }

        int? persistedBalance;
        try
        {
            persistedBalance = await _db.GetCharacterBankBalanceAsync(
                userId,
                identity.ProfileId,
                slot,
                CancellationToken.None);
            if (persistedBalance == null)
                return new ProfileSaveFailureResolution(ProfileSaveFailureOutcome.ConfirmedOriginal);
        }
        catch (Exception exception)
        {
            _log.Error($"Could not verify exact balance after {operation} failure for {userId}:{slot}: {exception}");
            return new ProfileSaveFailureResolution(ProfileSaveFailureOutcome.Unknown, exception);
        }

        var outcome = ClassifyProfileSaveFailure(
            persistedBalance,
            originalBalance,
            mutatedBalance);
        if (outcome != ProfileSaveFailureOutcome.Unknown)
            return new ProfileSaveFailureResolution(outcome);

        var unexpectedBalance = new InvalidOperationException(
            $"Fresh balance {persistedBalance.Value} matched neither original {originalBalance} nor mutation " +
            $"{mutatedBalance} for {userId}:{slot} after {operation} failure.");
        _log.Error(unexpectedBalance.Message);
        return new ProfileSaveFailureResolution(outcome, unexpectedBalance);
    }

    private void TryProjectVerifiedOfflineBalance(
        BalanceMutationLease lease,
        NetUserId userId,
        int slot,
        int balance,
        string operation)
    {
        try
        {
            if (!TryProjectBankBalance(lease, userId, slot, balance) &&
                _prefsManager.TryGetCharacterProfileId(userId, slot, out var currentProfileId) &&
                lease.TryGetIdentity(userId, out var identity) &&
                currentProfileId == identity.ProfileId)
            {
                _log.Warning($"Could not project verified offline {operation} balance for {userId}:{slot}.");
            }

            if (_playerManager.TryGetSessionById(userId, out var session) &&
                session.AttachedEntity is { Valid: true } attached)
            {
                SyncBankBalance(attached);
            }
        }
        catch (Exception exception)
        {
            // The fresh exact read already proved the durable result. Runtime
            // projection failure must not make a committed operation retryable.
            _log.Error($"Could not project verified offline {operation} for {userId}:{slot}: {exception}");
        }
    }

    private async Task PersistBankBalanceAsync(
        BalanceMutationLease lease,
        NetUserId userId,
        int slot,
        int expectedBalance,
        int newBalance,
        CancellationToken cancel = default)
    {
        if (!lease.TryGetIdentity(userId, out var identity))
        {
            if (!TryProjectBankBalance(lease, userId, slot, newBalance))
                throw new InvalidOperationException($"Could not apply runtime-only bank balance for {userId}:{slot}.");
            return;
        }

        var result = await _db.UpdateCharacterBankBalanceAsync(
            userId,
            identity.ProfileId,
            slot,
            expectedBalance,
            newBalance,
            cancel);
        if (!result.Success)
            throw new ExactBalanceUpdateRejectedException(userId, identity.ProfileId, slot, result);

        if (!TryProjectBankBalance(lease, userId, slot, newBalance))
        {
            _log.Warning($"Committed bank balance was not projected for stale profile " +
                         $"{userId}/{identity.ProfileId} in slot {slot}.");
        }
    }

    private bool TryProjectBankBalance(
        BalanceMutationLease lease,
        NetUserId userId,
        int slot,
        int balance)
    {
        return lease.TryGetIdentity(userId, out var identity)
            ? _prefsManager.TryApplyPersistedBankBalance(userId, slot, identity.ProfileId, balance)
            : _prefsManager.TryApplyPersistedBankBalance(userId, slot, balance);
    }

    private static int? GetLeaseProfileIdOrNull(BalanceMutationLease lease, NetUserId userId)
    {
        return lease.TryGetIdentity(userId, out var identity)
            ? identity.ProfileId
            : null;
    }

    /// <summary>
    /// Revalidates every piece of mutable runtime context a mob-bound world
    /// finalizer depends on. This is intentionally synchronous: callers invoke the
    /// finalizer immediately after a successful check, without another await where
    /// disconnect/profile teardown could interleave.
    /// </summary>
    private bool IsCurrentMobFinalizerContext(
        EntityUid mobUid,
        ICommonSession session,
        int slot,
        BalanceMutationLease lease)
    {
        if (!Exists(mobUid) ||
            session.AttachedEntity != mobUid ||
            !_playerManager.TryGetSessionById(session.UserId, out var currentSession) ||
            !ReferenceEquals(currentSession, session) ||
            !_playerManager.TryGetSessionByEntity(mobUid, out var actorSession) ||
            !ReferenceEquals(actorSession, session) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs) ||
            prefs.SelectedCharacterIndex != slot ||
            !prefs.Characters.TryGetValue(slot, out var character) ||
            character is not HumanoidCharacterProfile)
        {
            return false;
        }

        if (!lease.TryGetIdentity(session.UserId, out var identity))
            return !ServerPreferencesManager.ShouldStorePrefs(session.Channel.AuthType);

        return _prefsManager.TryGetCharacterProfileId(session.UserId, slot, out var currentProfileId) &&
               currentProfileId == identity.ProfileId;
    }

    /// <summary>
    /// Spawn-time purchases deliberately run before a mob is attached, so their
    /// stable context is the live session plus the exact slot/ProfileId pair.
    /// </summary>
    private bool IsCurrentProfileMutationContext(
        ICommonSession session,
        int slot,
        int expectedProfileId,
        BalanceMutationLease lease)
    {
        return _playerManager.TryGetSessionById(session.UserId, out var currentSession) &&
               ReferenceEquals(currentSession, session) &&
               lease.TryGetIdentity(session.UserId, out var identity) &&
               identity.ProfileId == expectedProfileId &&
               _prefsManager.TryGetCharacterProfileId(session.UserId, slot, out var currentProfileId) &&
               currentProfileId == expectedProfileId &&
               _prefsManager.TryGetCachedPreferences(session.UserId, out var prefs) &&
               prefs.SelectedCharacterIndex == slot &&
               prefs.Characters.TryGetValue(slot, out var character) &&
               character is HumanoidCharacterProfile;
    }

    private bool IsCurrentProfileFinalizerContext(
        EntityUid expectedEntity,
        ICommonSession session,
        int slot,
        int expectedProfileId,
        BalanceMutationLease lease)
    {
        return Exists(expectedEntity) &&
               session.AttachedEntity == expectedEntity &&
               _playerManager.TryGetSessionByEntity(expectedEntity, out var actorSession) &&
               ReferenceEquals(actorSession, session) &&
               IsCurrentProfileMutationContext(session, slot, expectedProfileId, lease);
    }

    private bool HandleDefiniteInitialBalanceRejection(
        Exception exception,
        BalanceMutationLease lease,
        NetUserId userId,
        int slot)
    {
        if (exception is not ExactBalanceUpdateRejectedException rejected)
            return false;

        if (rejected.Result.CurrentBalance is { } currentBalance)
            TryProjectBankBalance(lease, userId, slot, currentBalance);

        return true;
    }

    private static Exception CreateMonoCoinsMutationFailure(
        string operation,
        MonoCoinsBalanceUpdateResult result)
    {
        return new InvalidOperationException(
            $"{operation} was rejected: {result.Status} " +
            $"(current: {result.CurrentBalance?.ToString() ?? "missing"}).",
            result.Failure);
    }

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("bank");
        InitializeATM();
        InitializeStationATM();

        SubscribeLocalEvent<BankAccountComponent, PreferencesLoadedEvent>(OnPreferencesLoaded); // For late-add bank accounts
        SubscribeLocalEvent<BankAccountComponent, ComponentInit>(OnInit); // For late-add bank accounts
        SubscribeLocalEvent<BankAccountComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<BankAccountComponent, PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerLobbyJoin);
        SubscribeLocalEvent<SectorBankComponent, ComponentInit>(OnSectorInit);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnCleanup);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdateSectorBanks(frameTime);
        UpdatePayroll(frameTime);
    }

    public void OnCleanup(RoundRestartCleanupEvent _)
    {
        CleanupLedger();
        _payrollGeneration = unchecked(_payrollGeneration + 1);
        _payrollTimers.Clear();
        _payrollMissingJobWarnings.Clear();
        _payrollDepositsInFlight.Clear();
    }

    private void UpdatePayroll(float frameTime)
    {
        var activePlayers = new HashSet<NetUserId>();
        var query = EntityQueryEnumerator<BankAccountComponent, ActorComponent, MobStateComponent>();
        while (query.MoveNext(out var uid, out _, out var actor, out var mobState))
        {
            var session = actor.PlayerSession;
            activePlayers.Add(session.UserId);

            if (mobState.CurrentState is not (MobState.Alive or MobState.Critical) ||
                !TryGetPayrollHourly(uid, session, out var hourly) ||
                hourly <= 0)
            {
                continue;
            }

            _payrollMissingJobWarnings.Remove(session.UserId);

            EnsurePayrollTimerStarted(session);
            var elapsed = _payrollTimers.GetValueOrDefault(session.UserId) + frameTime;
            if (elapsed < PayrollIntervalSeconds)
            {
                _payrollTimers[session.UserId] = elapsed;
                continue;
            }

            var payoutCount = (int) MathF.Floor(elapsed / PayrollIntervalSeconds);
            var payout = (int) Math.Min((long) hourly * payoutCount, int.MaxValue);

            _payrollTimers[session.UserId] = elapsed;
            if (IsBankOperationPending(uid) || _payrollDepositsInFlight.ContainsKey(session.UserId))
                continue;

            var owner = new PayrollDepositOwner(_payrollGeneration, Guid.NewGuid());
            _payrollDepositsInFlight.Add(session.UserId, owner);

            _ = ObservePayrollDepositAsync(
                uid,
                session,
                payout,
                payoutCount,
                hourly,
                owner);
        }

        foreach (var userId in _payrollTimers.Keys.Where(userId => !activePlayers.Contains(userId)).ToArray())
        {
            _payrollTimers.Remove(userId);
            _payrollMissingJobWarnings.Remove(userId);
        }
    }

    private async Task ObservePayrollDepositAsync(
        EntityUid uid,
        ICommonSession session,
        int payout,
        int payoutCount,
        int hourly,
        PayrollDepositOwner owner)
    {
        try
        {
            if (!await TryBankDepositCoreAsync(
                    uid,
                    payout,
                    tax: false,
                    finalizeAfterCommit: null,
                    enforceDepositCVar: false))
                return;

            if (!IsCurrentPayrollDeposit(session.UserId, owner))
                return;

            if (_payrollTimers.TryGetValue(session.UserId, out var elapsed))
            {
                _payrollTimers[session.UserId] = Math.Max(
                    0f,
                    elapsed - payoutCount * PayrollIntervalSeconds);
            }

            if (TryGetBalance(session, out var newBalance))
            {
                NotifyPayrollReceived(session, payout, newBalance, hourly);
                _log.Info($"{uid} received payroll {payout}; new balance {newBalance}");
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Payroll deposit failed for {session.UserId}: {exception}");
        }
        finally
        {
            ReleasePayrollDeposit(session.UserId, owner);
        }
    }

    private bool IsCurrentPayrollDeposit(NetUserId userId, PayrollDepositOwner owner)
    {
        return owner.Generation == _payrollGeneration &&
               _payrollDepositsInFlight.TryGetValue(userId, out var currentOwner) &&
               currentOwner == owner;
    }

    private void ReleasePayrollDeposit(NetUserId userId, PayrollDepositOwner owner)
    {
        if (IsCurrentPayrollDeposit(userId, owner))
            _payrollDepositsInFlight.Remove(userId);
    }

    private void EnsurePayrollTimerStarted(ICommonSession session)
    {
        if (_payrollTimers.ContainsKey(session.UserId))
            return;

        if (_gameTicker.RunLevel == GameRunLevel.InRound)
        {
            var roundSeconds = Math.Max(0f, (float) _gameTicker.RoundDuration().TotalSeconds);
            _payrollTimers[session.UserId] = roundSeconds % PayrollIntervalSeconds;
            return;
        }

        _payrollTimers[session.UserId] = 0f;
    }

    public bool TryGetPayrollStatus(EntityUid mobUid, ICommonSession session, out int hourly, out int nextSeconds)
    {
        hourly = 0;
        nextSeconds = 0;

        if (TryComp<MobStateComponent>(mobUid, out var mobState) &&
            mobState.CurrentState is not (MobState.Alive or MobState.Critical))
        {
            return false;
        }

        if (!TryGetPayrollHourly(mobUid, session, out hourly) || hourly <= 0)
            return false;

        EnsurePayrollTimerStarted(session);
        var elapsed = _payrollTimers.GetValueOrDefault(session.UserId);
        nextSeconds = (int) MathF.Ceiling(Math.Max(0f, PayrollIntervalSeconds - elapsed));
        return true;
    }

    private bool TryGetPayrollHourly(EntityUid mobUid, ICommonSession session, out int hourly)
    {
        hourly = 0;
        var contentData = _playerManager.TryGetPlayerData(session.UserId, out var playerData)
            ? playerData.ContentData()
            : null;
        string? jobId = null;

        if (contentData?.Mind != null &&
            _job.MindTryGetJobId(contentData.Mind.Value, out var mindJobId) &&
            mindJobId is { } resolvedMindJob)
        {
            jobId = resolvedMindJob.Id;
        }
        else if (TryComp<PlayerJobComponent>(mobUid, out var playerJob) &&
                 playerJob.JobPrototype is { } resolvedPlayerJob)
        {
            jobId = resolvedPlayerJob.Id;
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            if (_payrollMissingJobWarnings.Add(session.UserId))
                _log.Warning($"Payroll skipped for {session.UserId}: no job id found on mind or player entity {mobUid}.");
            return false;
        }

        hourly = GetPayrollHourly(jobId);
        return hourly > 0;
    }

    private void NotifyPayrollReceived(ICommonSession session, int payout, int newBalance, int hourly)
    {
        var message = Loc.GetString(
            "bank-payroll-received",
            ("amount", BankSystemExtensions.ToSpesoString(payout)),
            ("balance", BankSystemExtensions.ToSpesoString(newBalance)),
            ("hourly", BankSystemExtensions.ToSpesoString(hourly)));

        _chatManager.ChatMessageToOne(
            ChatChannel.Notifications,
            message,
            message,
            EntityUid.Invalid,
            false,
            session.Channel);
    }

    private static int GetPayrollHourly(string jobId)
    {
        if (PayrollByJob.TryGetValue(jobId, out var payroll))
            return payroll;

        if (ContainsAny(jobId, "Captain", "CentralCommand", "ERT", "DeathSquad"))
            return PayrollCentralCommandHourly;

        if (ContainsAny(jobId, "Chief", "Head", "Director", "Quartermaster", "Sheriff", "Commissar", "Commander"))
            return PayrollCommandHourly;

        if (ContainsAny(jobId, "Senior", "Sergeant", "Bailiff", "Warden", "FirstMate", "Corporal"))
            return PayrollOfficerHourly;

        if (ContainsAny(jobId, "Security", "Deputy", "Detective", "Mercenary", "Salvage", "Pirate", "Rifleman", "Medic", "Brigmedic", "Engineer"))
            return PayrollHazardHourly;

        if (ContainsAny(jobId, "Doctor", "Medical", "Scientist", "Chemist", "Paramedic", "Atmospheric", "Cargo", "Pilot", "StationRepresentative", "Stc"))
            return PayrollSpecialistHourly;

        if (ContainsAny(jobId, "Prisoner", "Borg"))
            return 0;

        return PayrollMinimumHourly;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Durably removes money from the selected character. Unlike the legacy
    /// synchronous API, a successful result is returned only after the profile save
    /// has completed. This overload only performs the durable debit; it does not
    /// coordinate any world-side value materialization.
    /// </summary>
    public Task<bool> TryBankWithdrawAsync(EntityUid mobUid, int amount)
    {
        return TryBankWithdrawCoreAsync(mobUid, amount, null);
    }

    /// <summary>
    /// Durably debits a profile and invokes a synchronous world finalizer while
    /// retaining the same mutation lease. A failed finalizer restores the original
    /// balance durably before returning false. This is exception-atomic within one
    /// live process, not crash-atomic: termination after the debit commits and
    /// before the callback completes can leave a permanent debit. Callers that
    /// materialize value need a durable operation id plus domain recovery.
    /// </summary>
    public Task<bool> TryBankWithdrawAsync(
        EntityUid mobUid,
        int amount,
        Func<bool> finalizeAfterCommit)
    {
        ArgumentNullException.ThrowIfNull(finalizeAfterCommit);
        return TryBankWithdrawCoreAsync(
            mobUid,
            amount,
            () => Task.FromResult(finalizeAfterCommit()));
    }

    /// <summary>
    /// Durably debits a profile and awaits an asynchronous world finalizer while
    /// retaining the same mutation lease. A failed finalizer restores the original
    /// balance durably before returning false.
    /// </summary>
    public Task<bool> TryBankWithdrawAsync(
        EntityUid mobUid,
        int amount,
        Func<Task<bool>> finalizeAfterCommit)
    {
        ArgumentNullException.ThrowIfNull(finalizeAfterCommit);
        return TryBankWithdrawCoreAsync(mobUid, amount, finalizeAfterCommit);
    }

    private async Task<bool> TryBankWithdrawCoreAsync(
        EntityUid mobUid,
        int amount,
        Func<Task<bool>>? finalizeAfterCommit)
    {
        if (amount <= 0)
        {
            _log.Info($"TryBankWithdrawAsync: {amount} is invalid from Uid {mobUid}");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out _))
        {
            _log.Info($"TryBankWithdrawAsync: {mobUid} has no bank account");
            return false;
        }

        if (HasComp<IronmanComponent>(mobUid))
        {
            _log.Info($"TryBankWithdrawAsync: {mobUid} is blocked from withdrawals (Ironman)");
            return false;
        }

        if (!_playerManager.TryGetSessionByEntity(mobUid, out var session) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs) ||
            prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            _log.Info($"TryBankWithdrawAsync: {mobUid} has no usable cached profile");
            return false;
        }

        var slot = prefs.IndexOfCharacter(profile);
        if (slot < 0 || !TryAcquireBalanceMutation(session.UserId, slot, out var lease))
            return false;

        try
        {
            if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
                currentPrefs.SelectedCharacterIndex != slot ||
                !currentPrefs.Characters.TryGetValue(slot, out var currentCharacter) ||
                currentCharacter is not HumanoidCharacterProfile currentProfile ||
                currentProfile.BankBalance < amount)
            {
                return false;
            }

            var newBalance = currentProfile.BankBalance - amount;
            try
            {
                await PersistBankBalanceAsync(
                    lease,
                    session.UserId,
                    slot,
                    currentProfile.BankBalance,
                    newBalance);
            }
            catch (Exception exception)
            {
                if (HandleDefiniteInitialBalanceRejection(
                        exception,
                        lease,
                        session.UserId,
                        slot))
                {
                    return false;
                }

                var resolution = await ResolveInitialProfileSaveFailureAsync(
                    session,
                    lease,
                    slot,
                    currentProfile.BankBalance,
                    newBalance,
                    "durable withdrawal");
                if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedOriginal)
                {
                    _log.Error($"Durable bank withdrawal did not commit for {session.UserId}:{slot}: {exception}");
                    return false;
                }

                if (resolution.Outcome == ProfileSaveFailureOutcome.Unknown)
                {
                    var ambiguousOutcome = resolution.VerificationFailure == null
                        ? exception
                        : new AggregateException(exception, resolution.VerificationFailure);
                    BlockBalanceMutation(
                        lease,
                        session.UserId,
                        slot,
                        $"initial durable withdrawal save outcome is unknown: {ambiguousOutcome}");
                    throw new BankMutationRollbackException(
                        $"Initial withdrawal outcome is unknown for {session.UserId}:{slot}.",
                        ambiguousOutcome);
                }

                _log.Warning($"Durable bank withdrawal save threw after a confirmed commit for {session.UserId}:{slot}; continuing finalization: {exception}");
            }

            if (finalizeAfterCommit != null)
            {
                var finalized = false;
                if (!IsCurrentMobFinalizerContext(mobUid, session, slot, lease))
                {
                    _log.Warning($"Skipping committed withdrawal finalizer for stale context " +
                                 $"{session.UserId}:{slot}; compensating the durable debit.");
                }
                else
                {
                    try
                    {
                        finalized = await finalizeAfterCommit();
                    }
                    catch (Exception exception)
                    {
                        _log.Error($"Committed withdrawal finalizer failed for {session.UserId}:{slot}: {exception}");
                    }
                }

                if (!finalized)
                {
                    try
                    {
                        await PersistBankBalanceAsync(
                            lease,
                            session.UserId,
                            slot,
                            newBalance,
                            currentProfile.BankBalance);
                    }
                    catch (Exception exception)
                    {
                        _log.Error($"CRITICAL: withdrawal rollback failed for {session.UserId}:{slot}: {exception}");
                        BlockBalanceMutation(lease, session.UserId, slot, "withdrawal rollback", exception);
                        await ReconcileProfileAfterSaveFailureAsync(
                            session,
                            slot,
                            "Durable withdrawal rollback",
                            allowRefresh: false,
                            expectedProfileId: GetLeaseProfileIdOrNull(lease, session.UserId));
                        throw new BankMutationRollbackException(
                            $"Withdrawal rollback failed for {session.UserId}:{slot}.",
                            exception);
                    }

                    try
                    {
                        if (TryComp<BankAccountComponent>(mobUid, out var rolledBackBank))
                        {
                            rolledBackBank.Balance = currentProfile.BankBalance;
                            Dirty(mobUid, rolledBackBank);
                        }

                        RaiseLocalEvent(new BalanceChangedEvent(session, currentProfile.BankBalance));
                    }
                    catch (Exception exception)
                    {
                        _log.Error($"Could not project rolled-back withdrawal for {session.UserId}:{slot}: {exception}");
                    }

                    return false;
                }
            }

            try
            {
                if (TryComp<BankAccountComponent>(mobUid, out var bank))
                {
                    bank.Balance = newBalance;
                    Dirty(mobUid, bank);
                }

                RaiseLocalEvent(new BalanceChangedEvent(session, newBalance));
                _log.Info($"{mobUid} durably withdrew {amount}");
            }
            catch (Exception exception)
            {
                // The profile save is authoritative. Runtime projection failures
                // must not make callers repeat an already committed withdrawal.
                _log.Error($"Could not project committed withdrawal for {session.UserId}:{slot}: {exception}");
            }

            return true;
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Durably pays for value that is prepared while a character is being spawned.
    /// The operation may start before the new mob is attached, but the supplied
    /// finalizer runs only after the exact profile debit (and optional long-term
    /// debit) is authoritative and the expected mob is the session's current
    /// attachment, while the same profile mutation lease is still held. Like the
    /// entity overload, this is not crash-atomic across the database/world boundary.
    /// </summary>
    public async Task<bool> TryBankWithdrawProfileAsync(
        ICommonSession session,
        EntityUid expectedEntity,
        int expectedSlot,
        int expectedProfileId,
        int amount,
        bool spendLongTerm,
        Func<bool> finalizeAfterCommit)
    {
        ArgumentNullException.ThrowIfNull(finalizeAfterCommit);
        if (amount <= 0 || !Exists(expectedEntity))
            return false;

        if (expectedSlot < 0 ||
            expectedProfileId <= 0 ||
            !_prefsManager.TryGetCharacterProfileId(
                session.UserId,
                expectedSlot,
                out var activeProfileId) ||
            activeProfileId != expectedProfileId ||
            spendLongTerm && _coins.IsMonoCoinsMutationBlocked(session.UserId) ||
            !TryAcquireBalanceMutation(
                session.UserId,
                expectedSlot,
                expectedProfileId,
                out var lease))
        {
            return false;
        }

        var slot = expectedSlot;

        try
        {
            if (!_prefsManager.TryGetCharacterProfileId(session.UserId, slot, out activeProfileId) ||
                activeProfileId != expectedProfileId ||
                !_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
                !currentPrefs.Characters.TryGetValue(slot, out var currentCharacter) ||
                currentCharacter is not HumanoidCharacterProfile currentProfile)
            {
                return false;
            }

            var longTermBefore = spendLongTerm
                ? await _coins.GetMonoCoinsBalanceAsync(session.UserId)
                : 0L;
            if (longTermBefore < 0L ||
                longTermBefore < amount &&
                currentProfile.BankBalance < amount - longTermBefore)
            {
                return false;
            }

            var longTermToSpend = spendLongTerm
                ? Math.Min(longTermBefore, amount)
                : 0L;
            var sectorToSpend = amount - (int) longTermToSpend;
            var newBalance = currentProfile.BankBalance - sectorToSpend;

            try
            {
                // A no-op CAS is intentional when long-term currency covers the
                // whole purchase: it still revalidates the exact active ProfileId.
                await PersistBankBalanceAsync(
                    lease,
                    session.UserId,
                    slot,
                    currentProfile.BankBalance,
                    newBalance);
            }
            catch (Exception exception)
            {
                if (HandleDefiniteInitialBalanceRejection(
                        exception,
                        lease,
                        session.UserId,
                        slot))
                {
                    return false;
                }

                var resolution = await ResolveInitialProfileSaveFailureAsync(
                    session,
                    lease,
                    slot,
                    currentProfile.BankBalance,
                    newBalance,
                    "spawn loadout withdrawal");
                if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedOriginal)
                    return false;

                if (resolution.Outcome == ProfileSaveFailureOutcome.Unknown)
                {
                    var ambiguousOutcome = resolution.VerificationFailure == null
                        ? exception
                        : new AggregateException(exception, resolution.VerificationFailure);
                    BlockBalanceMutation(
                        lease,
                        session.UserId,
                        slot,
                        $"spawn loadout withdrawal outcome is unknown: {ambiguousOutcome}");
                    throw new BankMutationRollbackException(
                        $"Spawn loadout withdrawal outcome is unknown for {session.UserId}:{slot}.",
                        ambiguousOutcome);
                }
            }

            // The sector debit is already authoritative. Revalidate the exact
            // session/profile immediately before entering the second durable
            // currency domain. A stale spawn must never consume long-term funds.
            if (!IsCurrentProfileMutationContext(
                    session,
                    slot,
                    expectedProfileId,
                    lease))
            {
                _log.Warning($"Spawn loadout context became stale after sector debit for " +
                             $"{session.UserId}/{expectedProfileId} in slot {slot}; compensating.");
                try
                {
                    await PersistBankBalanceAsync(
                        lease,
                        session.UserId,
                        slot,
                        newBalance,
                        currentProfile.BankBalance);
                }
                catch (Exception exception)
                {
                    BlockBalanceMutation(
                        lease,
                        session.UserId,
                        slot,
                        "stale spawn context sector compensation",
                        exception);
                    throw new BankMutationRollbackException(
                        $"Stale spawn context compensation failed for {session.UserId}:{slot}.",
                        exception);
                }

                return false;
            }

            if (longTermToSpend > 0)
            {
                var expectedLongTermAfter = longTermBefore - longTermToSpend;
                MonoCoinsBalanceUpdateResult debitResult;
                try
                {
                    debitResult = await _coins.UpdateMonoCoinsBalanceExactAsync(
                        session.UserId,
                        longTermBefore,
                        expectedLongTermAfter);
                }
                catch (Exception exception)
                {
                    debitResult = new MonoCoinsBalanceUpdateResult(
                        MonoCoinsBalanceUpdateStatus.UnknownOutcome,
                        Failure: exception);
                }
                if (!debitResult.Success && debitResult.DefinitelyNotCommitted)
                {
                    try
                    {
                        await PersistBankBalanceAsync(
                            lease,
                            session.UserId,
                            slot,
                            newBalance,
                            currentProfile.BankBalance);
                    }
                    catch (Exception rollbackException)
                    {
                        var failure = new AggregateException(
                            CreateMonoCoinsMutationFailure("spawn loadout long-term debit", debitResult),
                            rollbackException);
                        BlockBalanceMutation(lease, session.UserId, slot, "spawn loadout debit rollback", failure);
                        _coins.BlockMonoCoinsMutations(
                            session.UserId,
                            "Spawn loadout sector compensation failed after a rejected MonoCoins debit",
                            failure);
                        throw new BankMutationRollbackException(
                            $"Spawn loadout debit rollback failed for {session.UserId}:{slot}.",
                            failure);
                    }

                    return false;
                }

                if (!debitResult.Success)
                {
                    var failure = CreateMonoCoinsMutationFailure(
                        "spawn loadout long-term debit",
                        debitResult);
                    BlockBalanceMutation(lease, session.UserId, slot, "ambiguous spawn loadout long-term debit", failure);
                    _coins.BlockMonoCoinsMutations(
                        session.UserId,
                        "Spawn loadout long-term debit outcome is unknown",
                        failure);
                    throw new BankMutationRollbackException(
                        $"Spawn loadout long-term debit outcome is unknown for {session.UserId}:{slot}.",
                        failure);
                }
            }

            var finalized = false;
            if (!IsCurrentProfileFinalizerContext(
                    expectedEntity,
                    session,
                    slot,
                    expectedProfileId,
                    lease))
            {
                _log.Warning($"Skipping spawn loadout finalizer for stale exact profile " +
                             $"{session.UserId}/{expectedProfileId} in slot {slot}; compensating.");
            }
            else
            {
                try
                {
                    finalized = finalizeAfterCommit();
                }
                catch (Exception exception)
                {
                    _log.Error($"Spawn loadout finalizer failed for {session.UserId}:{slot}: {exception}");
                }
            }

            if (!finalized)
            {
                var rollbackFailures = new List<Exception>();
                if (longTermToSpend > 0)
                {
                    try
                    {
                        var restoreResult = await _coins.UpdateMonoCoinsBalanceExactAsync(
                            session.UserId,
                            longTermBefore - longTermToSpend,
                            longTermBefore);
                        if (!restoreResult.Success)
                        {
                            rollbackFailures.Add(CreateMonoCoinsMutationFailure(
                                "spawn loadout long-term rollback",
                                restoreResult));
                        }
                    }
                    catch (Exception exception)
                    {
                        rollbackFailures.Add(exception);
                    }
                }

                try
                {
                    await PersistBankBalanceAsync(
                        lease,
                        session.UserId,
                        slot,
                        newBalance,
                        currentProfile.BankBalance);
                }
                catch (Exception exception)
                {
                    rollbackFailures.Add(exception);
                }

                if (rollbackFailures.Count > 0)
                {
                    var failure = rollbackFailures.Count == 1
                        ? rollbackFailures[0]
                        : new AggregateException(rollbackFailures);
                    BlockBalanceMutation(lease, session.UserId, slot, "spawn loadout finalizer rollback", failure);
                    if (longTermToSpend > 0)
                    {
                        _coins.BlockMonoCoinsMutations(
                            session.UserId,
                            "Spawn loadout composite finalizer compensation failed",
                            failure);
                    }
                    throw new BankMutationRollbackException(
                        $"Spawn loadout finalizer rollback failed for {session.UserId}:{slot}.",
                        failure);
                }

                return false;
            }

            if (session.AttachedEntity is { Valid: true } attached)
            {
                SyncBankBalance(attached);
                RaiseLocalEvent(new BalanceChangedEvent(session, newBalance));
            }

            return true;
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Durably adds money to the selected character. The returned task completes
    /// only after the authoritative profile save, while holding the same per-profile
    /// lease used by PDA transfers and all other bank mutations.
    /// </summary>
    public Task<bool> TryBankDepositAsync(EntityUid mobUid, int amount, bool tax = true)
    {
        return TryBankDepositCoreAsync(mobUid, amount, tax, null, enforceDepositCVar: true);
    }

    /// <summary>
    /// Durably credits a deposit and runs a synchronous world finalizer
    /// while the same profile lease is still held. If finalization fails, the
    /// original profile balance is restored durably before the lease is released.
    /// This is intended for irreversible exchanges such as a ship sale.
    /// </summary>
    public Task<bool> TryBankDepositAsync(
        EntityUid mobUid,
        int amount,
        bool tax,
        Func<bool> finalizeAfterCommit)
    {
        ArgumentNullException.ThrowIfNull(finalizeAfterCommit);
        return TryBankDepositCoreAsync(
            mobUid,
            amount,
            tax,
            () => Task.FromResult(finalizeAfterCommit()),
            enforceDepositCVar: true);
    }

    /// <summary>
    /// Durably credits a deposit and awaits an asynchronous world finalizer while
    /// retaining the same mutation lease. A failed finalizer restores the original
    /// balance durably before returning false.
    /// </summary>
    public Task<bool> TryBankDepositAsync(
        EntityUid mobUid,
        int amount,
        bool tax,
        Func<Task<bool>> finalizeAfterCommit)
    {
        ArgumentNullException.ThrowIfNull(finalizeAfterCommit);
        return TryBankDepositCoreAsync(
            mobUid,
            amount,
            tax,
            finalizeAfterCommit,
            enforceDepositCVar: true);
    }

    private async Task<bool> TryBankDepositCoreAsync(
        EntityUid mobUid,
        int amount,
        bool tax,
        Func<Task<bool>>? finalizeAfterCommit,
        bool enforceDepositCVar)
    {
        if (enforceDepositCVar && !_cfg.GetCVar(MonoCVars.DepositEnabled))
        {
            _log.Info("TryBankDepositAsync: DepositEnabled cvar is disabled.");
            return false;
        }

        if (amount <= 0)
        {
            _log.Info($"TryBankDepositAsync: {amount} is invalid from Uid {mobUid}");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out _) ||
            !_playerManager.TryGetSessionByEntity(mobUid, out var session) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs) ||
            prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            _log.Info($"TryBankDepositAsync: {mobUid} has no usable bank profile");
            return false;
        }

        var slot = prefs.IndexOfCharacter(profile);
        if (slot < 0 || !TryAcquireBalanceMutation(session.UserId, slot, out var lease))
            return false;

        try
        {
            if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
                currentPrefs.SelectedCharacterIndex != slot ||
                !currentPrefs.Characters.TryGetValue(slot, out var currentCharacter) ||
                currentCharacter is not HumanoidCharacterProfile currentProfile)
            {
                return false;
            }

            var toSector = amount;
            var toLongTerm = 0;
            if (tax)
                GetTaxedDepositAmount(amount, currentProfile.BankBalance, out toSector, out toLongTerm);

            if (toSector <= 0 ||
                currentProfile.BankBalance > int.MaxValue - toSector ||
                toLongTerm > 0 && _coins.IsMonoCoinsMutationBlocked(session.UserId))
            {
                return false;
            }

            var newBalance = currentProfile.BankBalance + toSector;
            try
            {
                await PersistBankBalanceAsync(
                    lease,
                    session.UserId,
                    slot,
                    currentProfile.BankBalance,
                    newBalance);
            }
            catch (Exception exception)
            {
                if (HandleDefiniteInitialBalanceRejection(
                        exception,
                        lease,
                        session.UserId,
                        slot))
                {
                    return false;
                }

                var resolution = await ResolveInitialProfileSaveFailureAsync(
                    session,
                    lease,
                    slot,
                    currentProfile.BankBalance,
                    newBalance,
                    "durable deposit");
                if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedOriginal)
                {
                    _log.Error($"Durable bank deposit did not commit for {session.UserId}:{slot}: {exception}");
                    return false;
                }

                if (resolution.Outcome == ProfileSaveFailureOutcome.Unknown)
                {
                    var ambiguousOutcome = resolution.VerificationFailure == null
                        ? exception
                        : new AggregateException(exception, resolution.VerificationFailure);
                    BlockBalanceMutation(
                        lease,
                        session.UserId,
                        slot,
                        $"initial durable deposit save outcome is unknown: {ambiguousOutcome}");
                    throw new BankMutationRollbackException(
                        $"Initial deposit outcome is unknown for {session.UserId}:{slot}.",
                        ambiguousOutcome);
                }

                _log.Warning($"Durable bank deposit save threw after a confirmed commit for {session.UserId}:{slot}; continuing finalization: {exception}");
            }

            long? savingsBalanceBefore = null;
            long? savingsBalanceAfter = null;

            async Task RollbackCommittedDepositAsync(bool savingsWereCredited)
            {
                Exception? rollbackFailure = null;
                if (savingsWereCredited &&
                    savingsBalanceBefore is { } longTermBefore &&
                    savingsBalanceAfter is { } longTermAfter)
                {
                    try
                    {
                        var result = await _coins.UpdateMonoCoinsBalanceExactAsync(
                            session.UserId,
                            longTermAfter,
                            longTermBefore);
                        if (!result.Success)
                        {
                            rollbackFailure = CreateMonoCoinsMutationFailure(
                                "durable deposit savings rollback",
                                result);
                        }
                    }
                    catch (Exception exception)
                    {
                        rollbackFailure = exception;
                    }
                }

                try
                {
                    await PersistBankBalanceAsync(
                        lease,
                        session.UserId,
                        slot,
                        newBalance,
                        currentProfile.BankBalance);
                }
                catch (Exception exception)
                {
                    rollbackFailure = rollbackFailure == null
                        ? exception
                        : new AggregateException(rollbackFailure, exception);
                }

                if (rollbackFailure != null)
                {
                    BlockBalanceMutation(lease, session.UserId, slot, "deposit rollback", rollbackFailure);
                    if (toLongTerm > 0)
                    {
                        _coins.BlockMonoCoinsMutations(
                            session.UserId,
                            "Durable deposit composite compensation failed",
                            rollbackFailure);
                    }
                    await ReconcileProfileAfterSaveFailureAsync(
                        session,
                        slot,
                        "Durable deposit rollback",
                        allowRefresh: false,
                        expectedProfileId: GetLeaseProfileIdOrNull(lease, session.UserId));
                    throw new BankMutationRollbackException(
                        $"Deposit rollback failed for {session.UserId}:{slot}.",
                        rollbackFailure);
                }

                try
                {
                    if (TryComp<BankAccountComponent>(mobUid, out var rolledBackBank))
                    {
                        rolledBackBank.Balance = currentProfile.BankBalance;
                        Dirty(mobUid, rolledBackBank);
                    }

                    RaiseLocalEvent(new BalanceChangedEvent(session, currentProfile.BankBalance));
                }
                catch (Exception exception)
                {
                    _log.Error($"Could not project rolled-back deposit for {session.UserId}:{slot}: {exception}");
                }
            }

            var savingsCredited = false;
            if (toLongTerm > 0)
            {
                try
                {
                    savingsBalanceBefore = await _coins.GetMonoCoinsBalanceAsync(session.UserId);
                    savingsBalanceAfter = checked(savingsBalanceBefore.Value + toLongTerm);
                }
                catch (Exception exception)
                {
                    _log.Warning($"Durable deposit could not prepare its exact savings credit for " +
                                 $"{session.UserId}:{slot}: {exception}");
                    await RollbackCommittedDepositAsync(savingsWereCredited: false);
                    return false;
                }

                MonoCoinsBalanceUpdateResult creditResult;
                try
                {
                    creditResult = await _coins.UpdateMonoCoinsBalanceExactAsync(
                        session.UserId,
                        savingsBalanceBefore.Value,
                        savingsBalanceAfter.Value);
                }
                catch (Exception exception)
                {
                    creditResult = new MonoCoinsBalanceUpdateResult(
                        MonoCoinsBalanceUpdateStatus.UnknownOutcome,
                        Failure: exception);
                }
                if (creditResult.Success)
                {
                    savingsCredited = true;
                }
                else if (creditResult.DefinitelyNotCommitted)
                {
                    await RollbackCommittedDepositAsync(savingsWereCredited: false);
                    return false;
                }
                else
                {
                    var exception = CreateMonoCoinsMutationFailure(
                        "durable deposit savings credit",
                        creditResult);
                    _log.Error($"Durable bank deposit savings update failed for {session.UserId}:{slot}: {exception}");

                    // MonoCoins updates are not transactional with the character
                    // profile save. An ambiguous outcome must never be retried
                    // automatically. Restore the sector profile best-effort, then
                    // block both durable mutation domains even if it succeeds.
                    Exception ambiguousOutcome = exception;
                    try
                    {
                        await PersistBankBalanceAsync(
                            lease,
                            session.UserId,
                            slot,
                            newBalance,
                            currentProfile.BankBalance);
                    }
                    catch (Exception rollbackException)
                    {
                        ambiguousOutcome = new AggregateException(exception, rollbackException);
                    }

                    BlockBalanceMutation(
                        lease,
                        session.UserId,
                        slot,
                        "ambiguous savings credit",
                        ambiguousOutcome);
                    _coins.BlockMonoCoinsMutations(
                        session.UserId,
                        "Durable deposit savings credit outcome is unknown",
                        ambiguousOutcome);
                    await ReconcileProfileAfterSaveFailureAsync(
                        session,
                        slot,
                        "Ambiguous durable deposit savings credit",
                        allowRefresh: false,
                        expectedProfileId: GetLeaseProfileIdOrNull(lease, session.UserId));
                    throw new BankMutationRollbackException(
                        $"Savings credit outcome is unknown for {session.UserId}:{slot}.",
                        ambiguousOutcome);
                }
            }

            if (finalizeAfterCommit != null)
            {
                var finalized = false;
                if (!IsCurrentMobFinalizerContext(mobUid, session, slot, lease))
                {
                    _log.Warning($"Skipping committed deposit finalizer for stale context " +
                                 $"{session.UserId}:{slot}; compensating the durable credit.");
                }
                else
                {
                    try
                    {
                        finalized = await finalizeAfterCommit();
                    }
                    catch (Exception exception)
                    {
                        _log.Error($"Committed deposit finalizer failed for {session.UserId}:{slot}: {exception}");
                    }
                }

                if (!finalized)
                {
                    await RollbackCommittedDepositAsync(savingsCredited);
                    return false;
                }
            }

            try
            {
                if (TryComp<BankAccountComponent>(mobUid, out var bank))
                {
                    bank.Balance = newBalance;
                    Dirty(mobUid, bank);
                }

                RaiseLocalEvent(new BalanceChangedEvent(session, newBalance));
                _log.Info($"{mobUid} durably deposited {amount} (sector: {toSector}, savings: {toLongTerm})");
            }
            catch (Exception exception)
            {
                // Persistence succeeded; callers must not retry due to a runtime
                // projection failure.
                _log.Error($"Could not project committed deposit for {session.UserId}:{slot}: {exception}");
            }

            return true;
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Attempts to remove money from a character's bank account.
    /// This should always be used instead of attempting to modify the BankAccountComponent directly.
    /// When successful, the entity's BankAccountComponent will be updated with their current balance.
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is attached to, typically the player controlled mob</param>
    /// <param name="amount">The integer amount of which to decrease the bank account</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public bool TryBankWithdraw(EntityUid mobUid, int amount)
    {
        _log.Warning($"Rejected legacy synchronous bank withdrawal for {mobUid}; use {nameof(TryBankWithdrawAsync)}.");
        return false;
    }

    /// <summary>
    /// Attempts to add money to a character's bank account. This should always be used instead of attempting to modify the bankaccountcomponent directly
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is connected to, typically the player controlled mob</param>
    /// <param name="amount">The amount of spesos to remove from the bank account</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public bool TryBankDeposit(EntityUid mobUid, int amount, bool tax = true)
    {
        _log.Warning($"Rejected legacy synchronous bank deposit for {mobUid}; use {nameof(TryBankDepositAsync)}.");
        return false;
    }

    /// <summary>
    /// Atomically transfers money from the selected character attached to
    /// <paramref name="fromUid"/> to a registered character account. The database
    /// commit is authoritative; cache, components, and events are updated only after
    /// both the debit and credit have committed together.
    /// </summary>
    public async Task<CharacterBankTransferResult> TryBankTransferPersistedAsync(
        EntityUid fromUid,
        int expectedSenderProfileId,
        NetUserId recipientUserId,
        int expectedRecipientProfileId,
        int recipientSlot,
        string expectedRecipientName,
        int amount,
        Guid operationId,
        CancellationToken cancel = default)
    {
        if (operationId == Guid.Empty)
            return new CharacterBankTransferResult(CharacterBankTransferStatus.OperationConflict);

        if (amount <= 0)
            return new CharacterBankTransferResult(CharacterBankTransferStatus.InvalidAmount);

        if (expectedSenderProfileId <= 0)
            return new CharacterBankTransferResult(CharacterBankTransferStatus.SenderNotFound);

        if (expectedRecipientProfileId <= 0)
            return new CharacterBankTransferResult(CharacterBankTransferStatus.RecipientNotFound);

        if (!TryComp<BankAccountComponent>(fromUid, out var fromBank) ||
            !_playerManager.TryGetSessionByEntity(fromUid, out var fromSession) ||
            !_prefsManager.TryGetCachedPreferences(fromSession.UserId, out var fromPrefs) ||
            fromPrefs.SelectedCharacter is not HumanoidCharacterProfile fromProfile)
        {
            return new CharacterBankTransferResult(CharacterBankTransferStatus.SenderNotFound);
        }

        if (HasComp<IronmanComponent>(fromUid))
            return new CharacterBankTransferResult(CharacterBankTransferStatus.IronmanBlocked);

        var fromSlot = fromPrefs.IndexOfCharacter(fromProfile);
        if (fromSlot < 0)
            return new CharacterBankTransferResult(CharacterBankTransferStatus.SenderNotFound);

        if (expectedRecipientProfileId == expectedSenderProfileId)
        {
            return new CharacterBankTransferResult(
                CharacterBankTransferStatus.SameAccount,
                fromProfile.BankBalance);
        }

        if (!TryAcquireBalanceMutation(
                fromSession.UserId,
                fromSlot,
                expectedSenderProfileId,
                recipientUserId,
                recipientSlot,
                expectedRecipientProfileId,
                out var lease))
        {
            return new CharacterBankTransferResult(
                CharacterBankTransferStatus.Conflict,
                fromProfile.BankBalance);
        }

        try
        {
            if (!_prefsManager.TryGetCachedPreferences(fromSession.UserId, out var currentFromPrefs) ||
                currentFromPrefs.SelectedCharacterIndex != fromSlot ||
                !currentFromPrefs.Characters.TryGetValue(fromSlot, out var currentFromCharacter) ||
                currentFromCharacter is not HumanoidCharacterProfile currentFromProfile)
            {
                return new CharacterBankTransferResult(CharacterBankTransferStatus.SenderNotFound);
            }

            var recipientIsCached = _prefsManager.TryGetCachedPreferences(recipientUserId, out var recipientPrefs);
            recipientPrefs ??= await _db.GetPlayerPreferencesAsync(recipientUserId, cancel);
            if (recipientPrefs == null ||
                !recipientPrefs.Characters.TryGetValue(recipientSlot, out var recipientCharacter) ||
                recipientCharacter is not HumanoidCharacterProfile recipientProfile ||
                !recipientProfile.Name.Equals(expectedRecipientName, StringComparison.Ordinal))
            {
                return new CharacterBankTransferResult(
                    CharacterBankTransferStatus.RecipientNotFound,
                    currentFromProfile.BankBalance);
            }

            if (recipientIsCached &&
                recipientPrefs.SelectedCharacterIndex == recipientSlot &&
                _playerManager.TryGetSessionById(recipientUserId, out var onlineRecipient) &&
                onlineRecipient.AttachedEntity is { Valid: true } onlineRecipientEntity &&
                HasComp<IronmanComponent>(onlineRecipientEntity))
            {
                return new CharacterBankTransferResult(
                    CharacterBankTransferStatus.IronmanBlocked,
                    currentFromProfile.BankBalance,
                    recipientProfile.BankBalance);
            }

            var senderProfileId = await _db.GetCharacterIdAsync(fromSession.UserId, fromSlot, cancel);
            if (senderProfileId != expectedSenderProfileId)
            {
                return new CharacterBankTransferResult(
                    CharacterBankTransferStatus.SenderNotFound,
                    currentFromProfile.BankBalance,
                    recipientProfile.BankBalance);
            }

            var recipientProfileId = await _db.GetCharacterIdAsync(recipientUserId, recipientSlot, cancel);
            if (recipientProfileId != expectedRecipientProfileId)
            {
                return new CharacterBankTransferResult(
                    CharacterBankTransferStatus.RecipientNotFound,
                    currentFromProfile.BankBalance,
                    recipientProfile.BankBalance);
            }

            var result = await _db.TransferCharacterBankBalanceAsync(
                fromSession.UserId,
                expectedSenderProfileId,
                recipientUserId,
                expectedRecipientProfileId,
                amount,
                operationId,
                cancel);
            if (!result.Success)
            {
                if (result.Status == CharacterBankTransferStatus.UnknownOutcome)
                {
                    // Neither profile may accept another absolute balance write
                    // while the transfer outcome is unknown. A later stale save
                    // could otherwise overwrite a COMMIT that actually succeeded.
                    BlockBalanceMutationProfile(
                        lease,
                        fromSession.UserId,
                        expectedSenderProfileId,
                        fromSlot,
                        "atomic transfer COMMIT outcome could not be verified");
                    BlockBalanceMutationProfile(
                        lease,
                        recipientUserId,
                        expectedRecipientProfileId,
                        recipientSlot,
                        "atomic transfer COMMIT outcome could not be verified");

                    return await RefreshUnknownTransferOutcomeAsync(
                        fromUid,
                        fromSession,
                        fromSlot,
                        recipientUserId,
                        recipientSlot,
                        expectedSenderProfileId,
                        expectedRecipientProfileId,
                        result);
                }

                return result;
            }

            // The database commit above is authoritative. Everything below is a
            // best-effort projection of the committed balances into runtime state.
            try
            {
                if (!_prefsManager.TryApplyPersistedBankBalance(
                        fromSession.UserId,
                        fromSlot,
                        expectedSenderProfileId,
                        result.SenderBalance) &&
                    _prefsManager.TryGetCharacterProfileId(fromSession.UserId, fromSlot, out var currentSenderProfileId) &&
                    currentSenderProfileId == expectedSenderProfileId)
                {
                    _log.Error($"Could not apply committed bank balance to sender cache {fromSession.UserId}:{fromSlot}");
                }
            }
            catch (Exception exception)
            {
                _log.Error($"Could not update sender cache after committed bank transfer: {exception}");
            }

            try
            {
                var recipientApplied = _prefsManager.TryApplyPersistedBankBalance(
                    recipientUserId,
                    recipientSlot,
                    expectedRecipientProfileId,
                    result.RecipientBalance);
                if (!recipientApplied &&
                    _prefsManager.TryGetCharacterProfileId(recipientUserId, recipientSlot, out var currentRecipientProfileId) &&
                    currentRecipientProfileId == expectedRecipientProfileId &&
                    _prefsManager.TryGetCachedPreferences(recipientUserId, out _))
                {
                    _log.Error($"Could not apply committed bank balance to recipient cache {recipientUserId}:{recipientSlot}");
                }
            }
            catch (Exception exception)
            {
                _log.Error($"Could not update recipient cache after committed bank transfer: {exception}");
            }

            try
            {
                if (_prefsManager.TryGetCharacterProfileId(fromSession.UserId, fromSlot, out var projectedSenderProfileId) &&
                    projectedSenderProfileId == expectedSenderProfileId &&
                    _playerManager.TryGetSessionByEntity(fromUid, out var currentSenderSession) &&
                    ReferenceEquals(currentSenderSession, fromSession))
                {
                    fromBank.Balance = result.SenderBalance;
                    Dirty(fromUid, fromBank);
                    RaiseLocalEvent(new BalanceChangedEvent(fromSession, result.SenderBalance));
                }
            }
            catch (Exception exception)
            {
                _log.Error($"Could not update sender runtime state after committed bank transfer: {exception}");
            }

            try
            {
                if (_playerManager.TryGetSessionById(recipientUserId, out var recipientSession) &&
                    recipientSession.AttachedEntity is { Valid: true } recipientEntity &&
                    _prefsManager.TryGetCharacterProfileId(recipientUserId, recipientSlot, out var projectedRecipientProfileId) &&
                    projectedRecipientProfileId == expectedRecipientProfileId &&
                    _prefsManager.TryGetCachedPreferences(recipientUserId, out var updatedRecipientPrefs) &&
                    updatedRecipientPrefs.SelectedCharacterIndex == recipientSlot)
                {
                    if (TryComp<BankAccountComponent>(recipientEntity, out var recipientBank))
                    {
                        recipientBank.Balance = result.RecipientBalance;
                        Dirty(recipientEntity, recipientBank);
                    }

                    RaiseLocalEvent(new BalanceChangedEvent(recipientSession, result.RecipientBalance));
                }
            }
            catch (Exception exception)
            {
                _log.Error($"Could not update recipient runtime state after committed bank transfer: {exception}");
            }

            try
            {
                _log.Info($"{fromUid} atomically transferred {amount} to {recipientUserId}:{recipientSlot}");
            }
            catch
            {
                // Logging must not turn a committed transfer into a reported failure.
            }

            return new CharacterBankTransferResult(
                CharacterBankTransferStatus.Success,
                result.SenderBalance,
                result.RecipientBalance,
                operationId,
                result.AlreadyProcessed);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task<CharacterBankTransferResult> RefreshUnknownTransferOutcomeAsync(
        EntityUid fromUid,
        ICommonSession fromSession,
        int fromSlot,
        NetUserId recipientUserId,
        int recipientSlot,
        int expectedSenderProfileId,
        int expectedRecipientProfileId,
        CharacterBankTransferResult originalResult)
    {
        var senderBalance = originalResult.SenderBalance;
        var recipientBalance = originalResult.RecipientBalance;
        var senderBalanceLoaded = false;
        var recipientBalanceLoaded = false;

        try
        {
            var exactSenderBalance = await _db.GetCharacterBankBalanceAsync(
                fromSession.UserId,
                expectedSenderProfileId,
                fromSlot,
                CancellationToken.None);
            if (exactSenderBalance is { } currentSenderBalance)
            {
                senderBalance = currentSenderBalance;
                senderBalanceLoaded = true;
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Could not reload sender after unknown bank transfer outcome: {exception}");
        }

        try
        {
            var exactRecipientBalance = await _db.GetCharacterBankBalanceAsync(
                recipientUserId,
                expectedRecipientProfileId,
                recipientSlot,
                CancellationToken.None);
            if (exactRecipientBalance is { } currentRecipientBalance)
            {
                recipientBalance = currentRecipientBalance;
                recipientBalanceLoaded = true;
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Could not reload recipient after unknown bank transfer outcome: {exception}");
        }

        if (senderBalanceLoaded)
        {
            try
            {
                var applied = _prefsManager.TryApplyPersistedBankBalance(
                    fromSession.UserId,
                    fromSlot,
                    expectedSenderProfileId,
                    senderBalance);
                if (applied &&
                    _playerManager.TryGetSessionByEntity(fromUid, out var currentSenderSession) &&
                    ReferenceEquals(currentSenderSession, fromSession) &&
                    TryComp<BankAccountComponent>(fromUid, out var senderBank))
                {
                    senderBank.Balance = senderBalance;
                    Dirty(fromUid, senderBank);
                    RaiseLocalEvent(new BalanceChangedEvent(fromSession, senderBalance));
                }
            }
            catch (Exception exception)
            {
                _log.Error($"Could not project sender balance after unknown transfer outcome: {exception}");
            }
        }

        if (recipientBalanceLoaded)
        {
            try
            {
                var applied = _prefsManager.TryApplyPersistedBankBalance(
                    recipientUserId,
                    recipientSlot,
                    expectedRecipientProfileId,
                    recipientBalance);
                if (applied &&
                    _playerManager.TryGetSessionById(recipientUserId, out var recipientSession) &&
                    recipientSession.AttachedEntity is { Valid: true } recipientEntity &&
                    _prefsManager.TryGetCachedPreferences(recipientUserId, out var recipientCachedPrefs) &&
                    recipientCachedPrefs.SelectedCharacterIndex == recipientSlot)
                {
                    if (TryComp<BankAccountComponent>(recipientEntity, out var recipientBank))
                    {
                        recipientBank.Balance = recipientBalance;
                        Dirty(recipientEntity, recipientBank);
                    }

                    RaiseLocalEvent(new BalanceChangedEvent(recipientSession, recipientBalance));
                }
            }
            catch (Exception exception)
            {
                _log.Error($"Could not project recipient balance after unknown transfer outcome: {exception}");
            }
        }

        return new CharacterBankTransferResult(
            CharacterBankTransferStatus.UnknownOutcome,
            senderBalance,
            recipientBalance);
    }

    /// <summary>
    /// Attempts to remove money from a character's bank account without a backing entity.
    /// This should only be used in cases where a character doesn't have a backing entity.
    /// </summary>
    /// <param name="session">The session of the player making the withdrawal.</param>
    /// <param name="prefs">The preferences storing the character whose bank will be changed.</param>
    /// <param name="profile">The profile of the character whose account is being withdrawn.</param>
    /// <param name="amount">The number of spesos to be withdrawn.</param>
    /// <param name="newBalance">The new value of the bank account.</param>
    /// <param name="spendLongTerm">Whether to also, and preferentially, spend long-term currency.</param>
    /// <returns>true if the transaction was successful, false if it was not.  When successful, newBalance contains the character's new balance.</returns>
    public bool TryBankWithdraw(ICommonSession session, PlayerPreferences prefs, HumanoidCharacterProfile profile, int amount, [NotNullWhen(true)] out int? newBalance, bool spendLongTerm = false)
    {
        newBalance = null;
        _log.Warning($"Rejected legacy synchronous bank withdrawal for {session.UserId}; use an awaited durable API.");
        return false;
    }

    /// <summary>
    /// Attempts to add money to a character's bank account.
    /// This should only be used in cases where a character doesn't have a backing entity.
    /// </summary>
    /// <param name="session">The session of the player making the deposit.</param>
    /// <param name="prefs">The preferences storing the character whose bank will be changed.</param>
    /// <param name="profile">The profile of the character whose account is being withdrawn.</param>
    /// <param name="amount">The number of spesos to be deposited.</param>
    /// <param name="newBalance">The new value of the bank account.</param>
    /// <returns>true if the transaction was successful, false if it was not.  When successful, newBalance contains the character's new balance.</returns>
    public bool TryBankDeposit(ICommonSession session, PlayerPreferences prefs, HumanoidCharacterProfile profile, int amount, [NotNullWhen(true)] out int? newBalance)
    {
        newBalance = null;
        _log.Warning($"Rejected legacy synchronous bank deposit for {session.UserId}; use an awaited durable API.");
        return false;
    }

    /// <summary>
    /// Attempts to remove money from an offline character's bank account.
    /// This method works with offline players by directly modifying their preferences and saving to the database.
    /// </summary>
    /// <param name="userId">The NetUserId of the offline player</param>
    /// <param name="prefs">The player's preferences</param>
    /// <param name="profile">The character profile to modify</param>
    /// <param name="amount">The amount to withdraw</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public async Task<bool> TryBankWithdrawOffline(
        NetUserId userId,
        PlayerPreferences prefs,
        HumanoidCharacterProfile profile,
        int expectedProfileId,
        int amount)
    {
        if (amount <= 0)
        {
            _log.Info($"TryBankWithdrawOffline: {amount} is invalid");
            return false;
        }

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"TryBankWithdrawOffline: {userId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }

        if (expectedProfileId <= 0 ||
            !TryAcquireBalanceMutation(userId, index, expectedProfileId, out var lease))
        {
            return false;
        }

        var originalBalance = profile.BankBalance;
        if (originalBalance < amount)
        {
            lease.Dispose();
            _log.Info($"TryBankWithdrawOffline: {userId} tried to withdraw {amount}, but has insufficient funds ({originalBalance})");
            return false;
        }

        var mutatedBalance = originalBalance - amount;
        try
        {
            await PersistBankBalanceAsync(
                lease,
                userId,
                index,
                originalBalance,
                mutatedBalance);

            _log.Info($"Offline player {userId} withdrew {amount}");
            return true;
        }
        catch (Exception exception)
        {
            var definiteRejection = HandleDefiniteInitialBalanceRejection(
                exception,
                lease,
                userId,
                index);
            if (definiteRejection)
            {
                _log.Warning($"Offline bank withdrawal was rejected for the exact stored profile " +
                             $"{userId}/{expectedProfileId} in slot {index}: {exception.Message}");
                return false;
            }

            var resolution = await ResolveOfflineProfileSaveFailureAsync(
                userId,
                lease,
                index,
                originalBalance,
                mutatedBalance,
                "offline withdrawal");
            if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedOriginal)
            {
                _log.Error($"Offline bank withdrawal did not commit for {userId}:{index}: {exception}");
                return false;
            }

            if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedMutation)
            {
                _log.Warning($"Offline bank withdrawal threw after a confirmed commit for {userId}:{index}: {exception}");
                TryProjectVerifiedOfflineBalance(lease, userId, index, mutatedBalance, "withdrawal");
                return true;
            }

            var ambiguousOutcome = resolution.VerificationFailure == null
                ? exception
                : new AggregateException(exception, resolution.VerificationFailure);
            BlockBalanceMutation(
                lease,
                userId,
                index,
                $"offline withdrawal outcome is unknown: {ambiguousOutcome}");
            throw new BankMutationRollbackException(
                $"Offline withdrawal outcome is unknown for {userId}:{index}.",
                ambiguousOutcome);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Attempts to add money to an offline character's bank account.
    /// This method works with offline players by directly modifying their preferences and saving to the database.
    /// </summary>
    /// <param name="userId">The NetUserId of the offline player</param>
    /// <param name="prefs">The player's preferences</param>
    /// <param name="profile">The character profile to modify</param>
    /// <param name="amount">The amount to deposit</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public async Task<bool> TryBankDepositOffline(
        NetUserId userId,
        PlayerPreferences prefs,
        HumanoidCharacterProfile profile,
        int expectedProfileId,
        int amount)
    {
        if (amount <= 0)
        {
            _log.Info($"TryBankDepositOffline: {amount} is invalid");
            return false;
        }

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"TryBankDepositOffline: {userId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }

        if (expectedProfileId <= 0 ||
            !TryAcquireBalanceMutation(userId, index, expectedProfileId, out var lease))
        {
            return false;
        }

        var originalBalance = profile.BankBalance;
        if (originalBalance > int.MaxValue - amount)
        {
            lease.Dispose();
            return false;
        }

        var mutatedBalance = originalBalance + amount;
        try
        {
            await PersistBankBalanceAsync(
                lease,
                userId,
                index,
                originalBalance,
                mutatedBalance);

            _log.Info($"Offline player {userId} deposited {amount}");
            return true;
        }
        catch (Exception exception)
        {
            var definiteRejection = HandleDefiniteInitialBalanceRejection(
                exception,
                lease,
                userId,
                index);
            if (definiteRejection)
            {
                _log.Warning($"Offline bank deposit was rejected for the exact stored profile " +
                             $"{userId}/{expectedProfileId} in slot {index}: {exception.Message}");
                return false;
            }

            var resolution = await ResolveOfflineProfileSaveFailureAsync(
                userId,
                lease,
                index,
                originalBalance,
                mutatedBalance,
                "offline deposit");
            if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedOriginal)
            {
                _log.Error($"Offline bank deposit did not commit for {userId}:{index}: {exception}");
                return false;
            }

            if (resolution.Outcome == ProfileSaveFailureOutcome.ConfirmedMutation)
            {
                _log.Warning($"Offline bank deposit threw after a confirmed commit for {userId}:{index}: {exception}");
                TryProjectVerifiedOfflineBalance(lease, userId, index, mutatedBalance, "deposit");
                return true;
            }

            var ambiguousOutcome = resolution.VerificationFailure == null
                ? exception
                : new AggregateException(exception, resolution.VerificationFailure);
            BlockBalanceMutation(
                lease,
                userId,
                index,
                $"offline deposit outcome is unknown: {ambiguousOutcome}");
            throw new BankMutationRollbackException(
                $"Offline deposit outcome is unknown for {userId}:{index}.",
                ambiguousOutcome);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Retrieves a character's balance via its in-game entity, if it has one.
    /// </summary>
    /// <param name="ent">The UID that the bank account is connected to, typically the player controlled mob</param>
    /// <param name="balance">When successful, contains the account balance in spesos. Otherwise, set to 0.</param>
    /// <returns>true if the account was successfully queried.</returns>
    public bool TryGetBalance(EntityUid ent, out int balance)
    {
        // Mono
        if (HasComp<IronmanComponent>(ent))
        {
            balance = 0;
            return true;
        }
        if (!_playerManager.TryGetSessionByEntity(ent, out var session) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs))
        {
            _log.Info($"{ent} has no cached prefs");
            balance = 0;
            return false;
        }

        if (prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            _log.Info($"{ent} has the wrong prefs type");
            balance = 0;
            return false;
        }

        balance = profile.BankBalance;
        return true;
    }

    /// <summary>
    /// Retrieves a character's balance via a player's session.
    /// </summary>
    /// <param name="session">The session of the player character to query.</param>
    /// <param name="balance">When successful, contains the account balance in spesos. Otherwise, set to 0.</param>
    /// <returns>true if the account was successfully queried.</returns>
    public bool TryGetBalance(ICommonSession session, out int balance)
    {
        // Mono
        if (session.AttachedEntity is { } attached && HasComp<IronmanComponent>(attached))
        {
            balance = 0;
            return true;
        }

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs))
        {
            _log.Info($"{session.UserId} has no cached prefs");
            balance = 0;
            return false;
        }

        if (prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            _log.Info($"{session.UserId} has the wrong prefs type");
            balance = 0;
            return false;
        }

        balance = profile.BankBalance;
        return true;
    }

    /// <summary>
    /// Update the bank balance to the character's current account balance.
    /// </summary>
    public void SyncBankBalance(EntityUid mobUid)
    {
        if (TryComp<BankAccountComponent>(mobUid, out var bank))
            UpdateBankBalance(mobUid, bank);
    }

    private void UpdateBankBalance(EntityUid mobUid, BankAccountComponent comp)
    {
        if (TryGetBalance(mobUid, out var balance))
            comp.Balance = balance;
        else
            comp.Balance = 0;

        Dirty(mobUid, comp);
    }

    /// <summary>
    /// Component initialized - if the player exists in the entity before the BankAccountComponent, update the player's account.
    /// </summary>
    public void OnInit(EntityUid mobUid, BankAccountComponent comp, ComponentInit _)
    {
        UpdateBankBalance(mobUid, comp);
    }

    /// <summary>
    /// Player's preferences loaded (mostly for hotjoin)
    /// </summary>
    public void OnPreferencesLoaded(EntityUid mobUid, BankAccountComponent comp, PreferencesLoadedEvent _)
    {
        UpdateBankBalance(mobUid, comp);
    }

    /// <summary>
    /// Player attached, make sure the bank account is up-to-date.
    /// </summary>
    public void OnPlayerAttached(EntityUid mobUid, BankAccountComponent comp, PlayerAttachedEvent args)
    {
        UpdateBankBalance(mobUid, comp);
        EnsurePayrollTimerStarted(args.Player);
    }

    /// <summary>
    /// Player detached, make sure the bank account is up-to-date.
    /// </summary>
    public void OnPlayerDetached(EntityUid mobUid, BankAccountComponent comp, PlayerDetachedEvent _)
    {
        UpdateBankBalance(mobUid, comp);
    }

    /// <summary>
    /// Ensures the bank account listed in the lobby is accurate by ensuring the preferences cache is up-to-date.
    /// </summary>
    private void OnPlayerLobbyJoin(PlayerJoinedLobbyEvent args)
    {
        var cts = new CancellationToken();
        _prefsManager.RefreshPreferencesAsync(args.PlayerSession, cts);
    }
}
