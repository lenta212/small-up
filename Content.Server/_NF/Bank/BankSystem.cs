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
    private readonly object _balanceMutationLock = new();
    private readonly HashSet<BankProfileKey> _activeBalanceMutations = new();
    private readonly HashSet<BankProfileKey> _blockedBalanceMutations = new();

    private readonly record struct BankProfileKey(NetUserId UserId, int Slot);

    internal enum ProfileSaveFailureOutcome
    {
        ConfirmedOriginal,
        ConfirmedMutation,
        Unknown,
    }

    private readonly record struct ProfileSaveFailureResolution(
        ProfileSaveFailureOutcome Outcome,
        Exception? VerificationFailure = null);

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
        private readonly BankProfileKey[] _keys;

        public BalanceMutationLease(BankSystem owner, BankProfileKey[] keys)
        {
            _owner = owner;
            _keys = keys;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseBalanceMutation(_keys);
        }
    }

    private bool TryAcquireBalanceMutation(
        NetUserId userId,
        int slot,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        return TryAcquireBalanceMutation(
            new BankProfileKey(userId, slot),
            null,
            out lease);
    }

    private bool TryAcquireBalanceMutation(
        NetUserId firstUserId,
        int firstSlot,
        NetUserId secondUserId,
        int secondSlot,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        return TryAcquireBalanceMutation(
            new BankProfileKey(firstUserId, firstSlot),
            new BankProfileKey(secondUserId, secondSlot),
            out lease);
    }

    private bool TryAcquireBalanceMutation(
        BankProfileKey first,
        BankProfileKey? second,
        [NotNullWhen(true)] out BalanceMutationLease? lease)
    {
        lock (_balanceMutationLock)
        {
            if (_activeBalanceMutations.Contains(first) ||
                _blockedBalanceMutations.Contains(first) ||
                second is { } secondKey &&
                secondKey != first &&
                (_activeBalanceMutations.Contains(secondKey) ||
                 _blockedBalanceMutations.Contains(secondKey)))
            {
                lease = null;
                return false;
            }

            _activeBalanceMutations.Add(first);
            if (second is { } distinctSecond && distinctSecond != first)
                _activeBalanceMutations.Add(distinctSecond);

            lease = new BalanceMutationLease(
                this,
                second is { } distinct && distinct != first
                    ? [first, distinct]
                    : [first]);
            return true;
        }
    }

    private void ReleaseBalanceMutation(IEnumerable<BankProfileKey> keys)
    {
        lock (_balanceMutationLock)
        {
            foreach (var key in keys)
                _activeBalanceMutations.Remove(key);
        }
    }

    private void BlockBalanceMutation(NetUserId userId, int slot, string operation, Exception exception)
    {
        BlockBalanceMutation(
            userId,
            slot,
            $"failed {operation} compensation: {exception}");
    }

    private void BlockBalanceMutation(NetUserId userId, int slot, string reason)
    {
        lock (_balanceMutationLock)
            _blockedBalanceMutations.Add(new BankProfileKey(userId, slot));

        _log.Error($"CRITICAL: bank profile {userId}:{slot} blocked: {reason}");
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

        var key = new BankProfileKey(session.UserId, prefs.SelectedCharacterIndex);
        lock (_balanceMutationLock)
        {
            return _activeBalanceMutations.Contains(key) || _blockedBalanceMutations.Contains(key);
        }
    }

    private void ObserveProfileSave(
        Task saveTask,
        BalanceMutationLease lease,
        string operation,
        ICommonSession firstSession,
        Func<Task>? afterSuccessfulSave = null)
    {
        _ = ObserveProfileSaveAsync(
            saveTask,
            lease,
            operation,
            firstSession,
            afterSuccessfulSave);
    }

    private async Task ObserveProfileSaveAsync(
        Task saveTask,
        BalanceMutationLease lease,
        string operation,
        ICommonSession firstSession,
        Func<Task>? afterSuccessfulSave)
    {
        try
        {
            try
            {
                await saveTask;
            }
            catch (Exception exception)
            {
                _log.Error($"{operation} profile save failed: {exception}");
                await ReconcileProfileAfterSaveFailureAsync(firstSession, operation);
                return;
            }

            if (afterSuccessfulSave == null)
                return;

            try
            {
                await afterSuccessfulSave();
            }
            catch (Exception exception)
            {
                _log.Error($"{operation} post-save balance update failed: {exception}");
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task ReconcileProfileAfterSaveFailureAsync(ICommonSession session, string operation)
    {
        try
        {
            await _prefsManager.RefreshPreferencesAsync(session, CancellationToken.None);
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
        int slot,
        int originalBalance,
        int mutatedBalance,
        string operation)
    {
        int? persistedBalance;
        try
        {
            var persistedPreferences = await _db.GetPlayerPreferencesAsync(
                session.UserId,
                CancellationToken.None);
            if (persistedPreferences == null ||
                !persistedPreferences.Characters.TryGetValue(slot, out var persistedCharacter) ||
                persistedCharacter is not HumanoidCharacterProfile persistedProfile)
            {
                var missingProfile = new InvalidOperationException(
                    $"Fresh profile {session.UserId}:{slot} was unavailable after {operation} save failure.");
                _log.Error(missingProfile.Message);
                return new ProfileSaveFailureResolution(
                    ProfileSaveFailureOutcome.Unknown,
                    missingProfile);
            }

            persistedBalance = persistedProfile.BankBalance;
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
                    persistedBalance.Value))
            {
                var cacheFailure = new InvalidOperationException(
                    $"Could not apply verified balance to cache for {session.UserId}:{slot} after {operation} save failure.");
                _log.Error(cacheFailure.Message);
                return new ProfileSaveFailureResolution(
                    ProfileSaveFailureOutcome.Unknown,
                    cacheFailure);
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
        _payrollTimers.Clear();
        _payrollMissingJobWarnings.Clear();
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

            if (TryPayrollDeposit(uid, payout, out var newBalance))
            {
                _payrollTimers[session.UserId] = elapsed - payoutCount * PayrollIntervalSeconds;
                NotifyPayrollReceived(session, payout, newBalance, hourly);
                _log.Info($"{uid} received payroll {payout}; new balance {newBalance}");
            }
        }

        foreach (var userId in _payrollTimers.Keys.Where(userId => !activePlayers.Contains(userId)).ToArray())
        {
            _payrollTimers.Remove(userId);
            _payrollMissingJobWarnings.Remove(userId);
        }
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
        var contentData = _playerManager.GetPlayerData(session.UserId).ContentData();
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
    /// has completed. The per-profile mutation lease remains held for that entire
    /// interval, allowing callers to safely defer irreversible world-side effects.
    /// </summary>
    public Task<bool> TryBankWithdrawAsync(EntityUid mobUid, int amount)
    {
        return TryBankWithdrawCoreAsync(mobUid, amount, null);
    }

    /// <summary>
    /// Durably debits a profile and invokes a synchronous world finalizer while
    /// retaining the same mutation lease. A failed finalizer restores the original
    /// balance durably before returning false.
    /// </summary>
    public Task<bool> TryBankWithdrawAsync(
        EntityUid mobUid,
        int amount,
        Func<bool> finalizeAfterCommit)
    {
        ArgumentNullException.ThrowIfNull(finalizeAfterCommit);
        return TryBankWithdrawCoreAsync(mobUid, amount, finalizeAfterCommit);
    }

    private async Task<bool> TryBankWithdrawCoreAsync(
        EntityUid mobUid,
        int amount,
        Func<bool>? finalizeAfterCommit)
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
                await _prefsManager.SetProfile(
                    session.UserId,
                    slot,
                    currentProfile.WithBankBalance(newBalance));
            }
            catch (Exception exception)
            {
                var resolution = await ResolveInitialProfileSaveFailureAsync(
                    session,
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
                try
                {
                    finalized = finalizeAfterCommit();
                }
                catch (Exception exception)
                {
                    _log.Error($"Committed withdrawal finalizer failed for {session.UserId}:{slot}: {exception}");
                }

                if (!finalized)
                {
                    try
                    {
                        await _prefsManager.SetProfile(session.UserId, slot, currentProfile);
                    }
                    catch (Exception exception)
                    {
                        _log.Error($"CRITICAL: withdrawal rollback failed for {session.UserId}:{slot}: {exception}");
                        BlockBalanceMutation(session.UserId, slot, "withdrawal rollback", exception);
                        await ReconcileProfileAfterSaveFailureAsync(session, "Durable withdrawal rollback");
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
    /// Durably adds money to the selected character. The returned task completes
    /// only after the authoritative profile save, while holding the same per-profile
    /// lease used by PDA transfers and all other bank mutations.
    /// </summary>
    public Task<bool> TryBankDepositAsync(EntityUid mobUid, int amount, bool tax = true)
    {
        return TryBankDepositCoreAsync(mobUid, amount, tax, null);
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
        return TryBankDepositCoreAsync(mobUid, amount, tax, finalizeAfterCommit);
    }

    private async Task<bool> TryBankDepositCoreAsync(
        EntityUid mobUid,
        int amount,
        bool tax,
        Func<bool>? finalizeAfterCommit)
    {
        if (!_cfg.GetCVar(MonoCVars.DepositEnabled))
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

            if (toSector <= 0 || currentProfile.BankBalance > int.MaxValue - toSector)
                return false;

            var newBalance = currentProfile.BankBalance + toSector;
            try
            {
                await _prefsManager.SetProfile(
                    session.UserId,
                    slot,
                    currentProfile.WithBankBalance(newBalance));
            }
            catch (Exception exception)
            {
                var resolution = await ResolveInitialProfileSaveFailureAsync(
                    session,
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
                        session.UserId,
                        slot,
                        $"initial durable deposit save outcome is unknown: {ambiguousOutcome}");
                    throw new BankMutationRollbackException(
                        $"Initial deposit outcome is unknown for {session.UserId}:{slot}.",
                        ambiguousOutcome);
                }

                _log.Warning($"Durable bank deposit save threw after a confirmed commit for {session.UserId}:{slot}; continuing finalization: {exception}");
            }

            async Task RollbackCommittedDepositAsync(bool savingsWereCredited)
            {
                Exception? rollbackFailure = null;
                try
                {
                    await _prefsManager.SetProfile(session.UserId, slot, currentProfile);
                }
                catch (Exception exception)
                {
                    rollbackFailure = exception;
                }

                if (savingsWereCredited && toLongTerm > 0)
                {
                    try
                    {
                        await _coins.AddMonoCoinsAsync(session.UserId, -toLongTerm);
                    }
                    catch (Exception exception)
                    {
                        rollbackFailure = rollbackFailure == null
                            ? exception
                            : new AggregateException(rollbackFailure, exception);
                    }
                }

                if (rollbackFailure != null)
                {
                    BlockBalanceMutation(session.UserId, slot, "deposit rollback", rollbackFailure);
                    await ReconcileProfileAfterSaveFailureAsync(session, "Durable deposit rollback");
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
                    await _coins.AddMonoCoinsAsync(session.UserId, toLongTerm);
                    savingsCredited = true;
                }
                catch (Exception exception)
                {
                    _log.Error($"Durable bank deposit savings update failed for {session.UserId}:{slot}: {exception}");

                    // MonoCoins updates are not transactional with the character
                    // profile save. An exception can arrive after their database
                    // commit, so the savings outcome is unknown and must never be
                    // retried automatically. Restore the sector profile best-effort,
                    // then keep this profile blocked even if that rollback succeeds.
                    Exception ambiguousOutcome = exception;
                    try
                    {
                        await _prefsManager.SetProfile(session.UserId, slot, currentProfile);
                    }
                    catch (Exception rollbackException)
                    {
                        ambiguousOutcome = new AggregateException(exception, rollbackException);
                    }

                    BlockBalanceMutation(
                        session.UserId,
                        slot,
                        "ambiguous savings credit",
                        ambiguousOutcome);
                    await ReconcileProfileAfterSaveFailureAsync(
                        session,
                        "Ambiguous durable deposit savings credit");
                    throw new BankMutationRollbackException(
                        $"Savings credit outcome is unknown for {session.UserId}:{slot}.",
                        ambiguousOutcome);
                }
            }

            if (finalizeAfterCommit != null)
            {
                var finalized = false;
                try
                {
                    finalized = finalizeAfterCommit();
                }
                catch (Exception exception)
                {
                    _log.Error($"Committed deposit finalizer failed for {session.UserId}:{slot}: {exception}");
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
        if (amount <= 0)
        {
            _log.Info($"TryBankWithdraw: {amount} is invalid from Uid {mobUid}");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
        {
            _log.Info($"TryBankWithdraw: {mobUid} has no bank account");
            return false;
        }

        // Mono
        if (HasComp<IronmanComponent>(mobUid))
        {
            _log.Info($"TryBankWithdraw: {mobUid} is blocked from withdrawals (Ironman)");
            return false;
        }

        if (!_playerManager.TryGetSessionByEntity(mobUid, out var session))
        {
            _log.Info($"TryBankWithdraw: {mobUid} has no attached session");
            return false;
        }

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs))
        {
            _log.Info($"TryBankWithdraw: {mobUid} has no cached prefs");
            return false;
        }

        if (prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            _log.Info($"TryBankWithdraw: {mobUid} has the wrong prefs type");
            return false;
        }

        if (TryBankWithdraw(session, prefs, profile, amount, out var newBalance))
        {
            bank.Balance = newBalance.Value;
            Dirty(mobUid, bank);
            _log.Info($"{mobUid} withdrew {amount}");
            return true;
        }
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
        // Mono start
        if (!_cfg.GetCVar(MonoCVars.DepositEnabled))
        {
            _log.Info($"TryBankDeposit: DepositEnabled cvar is disabled.");
            return false;
        }
        // Mono end
        if (amount <= 0)
        {
            _log.Info($"TryBankDeposit: {amount} is invalid from Uid {mobUid}");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
        {
            _log.Info($"TryBankDeposit: {mobUid} has no bank account");
            return false;
        }

        if (!_playerManager.TryGetSessionByEntity(mobUid, out var session))
        {
            _log.Info($"TryBankDeposit: {mobUid} has no attached session");
            return false;
        }

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs))
        {
            _log.Info($"TryBankDeposit: {mobUid} has no cached prefs");
            return false;
        }

        if (prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            _log.Info($"TryBankDeposit: {mobUid} has the wrong prefs type");
            return false;
        }

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1 || !TryAcquireBalanceMutation(session.UserId, index, out var lease))
            return false;

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
            !currentPrefs.Characters.TryGetValue(index, out var currentCharacter) ||
            currentCharacter is not HumanoidCharacterProfile currentProfile)
        {
            lease.Dispose();
            return false;
        }

        var toSector = amount;
        var toLongTerm = 0;
        if (tax)
        {
            GetTaxedDepositAmount(amount, currentProfile.BankBalance, out var afterTax, out var taxedAway);
            toSector = afterTax;
            toLongTerm = taxedAway;
        }

        if (toSector <= 0 || currentProfile.BankBalance > int.MaxValue - toSector)
        {
            lease.Dispose();
            return false;
        }

        var newBalance = currentProfile.BankBalance + toSector;
        var saveTask = _prefsManager.SetProfile(
            session.UserId,
            index,
            currentProfile.WithBankBalance(newBalance));
        Func<Task>? creditSavings = toLongTerm > 0
            ? () => _coins.AddMonoCoinsAsync(session.UserId, toLongTerm)
            : null;
        ObserveProfileSave(
            saveTask,
            lease,
            "Taxed bank deposit",
            session,
            afterSuccessfulSave: creditSavings);

        bank.Balance = newBalance;
        Dirty(mobUid, bank);
        RaiseLocalEvent(new BalanceChangedEvent(session, newBalance));
        _log.Info($"{mobUid} deposited {amount} (sector: {toSector}, savings: {toLongTerm})");
        return true;
    }

    private bool TryPayrollDeposit(EntityUid mobUid, int amount, out int newBalance)
    {
        newBalance = 0;
        if (amount <= 0)
            return false;

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
            return false;

        if (HasComp<IronmanComponent>(mobUid))
            return false;

        if (!_playerManager.TryGetSessionByEntity(mobUid, out var session) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs) ||
            prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
        {
            return false;
        }

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
            return false;

        if (!TryAcquireBalanceMutation(session.UserId, index, out var lease))
            return false;

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
            !currentPrefs.Characters.TryGetValue(index, out var currentCharacter) ||
            currentCharacter is not HumanoidCharacterProfile currentProfile ||
            currentProfile.BankBalance > int.MaxValue - amount)
        {
            lease.Dispose();
            return false;
        }

        newBalance = currentProfile.BankBalance + amount;
        var saveTask = _prefsManager.SetProfile(
            session.UserId,
            index,
            currentProfile.WithBankBalance(newBalance));
        ObserveProfileSave(saveTask, lease, "Payroll deposit", session);

        bank.Balance = newBalance;
        Dirty(mobUid, bank);
        RaiseLocalEvent(new BalanceChangedEvent(session, newBalance));
        return true;
    }

    /// <summary>
    /// Atomically transfers money from the selected character attached to
    /// <paramref name="fromUid"/> to a registered character account. The database
    /// commit is authoritative; cache, components, and events are updated only after
    /// both the debit and credit have committed together.
    /// </summary>
    public async Task<CharacterBankTransferResult> TryBankTransferPersistedAsync(
        EntityUid fromUid,
        NetUserId recipientUserId,
        int recipientSlot,
        string expectedRecipientName,
        int amount,
        CancellationToken cancel = default)
    {
        if (amount <= 0)
            return new CharacterBankTransferResult(CharacterBankTransferStatus.InvalidAmount);

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

        if (recipientUserId == fromSession.UserId && recipientSlot == fromSlot)
        {
            return new CharacterBankTransferResult(
                CharacterBankTransferStatus.SameAccount,
                fromProfile.BankBalance);
        }

        if (!TryAcquireBalanceMutation(
                fromSession.UserId,
                fromSlot,
                recipientUserId,
                recipientSlot,
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
            if (senderProfileId == null)
            {
                return new CharacterBankTransferResult(
                    CharacterBankTransferStatus.SenderNotFound,
                    currentFromProfile.BankBalance,
                    recipientProfile.BankBalance);
            }

            var recipientProfileId = await _db.GetCharacterIdAsync(recipientUserId, recipientSlot, cancel);
            if (recipientProfileId == null)
            {
                return new CharacterBankTransferResult(
                    CharacterBankTransferStatus.RecipientNotFound,
                    currentFromProfile.BankBalance,
                    recipientProfile.BankBalance);
            }

            var result = await _db.TransferCharacterBankBalanceAsync(
                fromSession.UserId,
                senderProfileId.Value,
                recipientUserId,
                recipientProfileId.Value,
                amount,
                cancel);
            if (!result.Success)
            {
                if (result.Status == CharacterBankTransferStatus.UnknownOutcome)
                {
                    // Neither profile may accept another absolute balance write
                    // while the transfer outcome is unknown. A later stale save
                    // could otherwise overwrite a COMMIT that actually succeeded.
                    BlockBalanceMutation(
                        fromSession.UserId,
                        fromSlot,
                        "atomic transfer COMMIT outcome could not be verified");
                    BlockBalanceMutation(
                        recipientUserId,
                        recipientSlot,
                        "atomic transfer COMMIT outcome could not be verified");

                    return await RefreshUnknownTransferOutcomeAsync(
                        fromUid,
                        fromSession,
                        fromSlot,
                        recipientUserId,
                        recipientSlot,
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
                        result.SenderBalance))
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
                    result.RecipientBalance);
                if (!recipientApplied &&
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
                fromBank.Balance = result.SenderBalance;
                Dirty(fromUid, fromBank);
                RaiseLocalEvent(new BalanceChangedEvent(fromSession, result.SenderBalance));
            }
            catch (Exception exception)
            {
                _log.Error($"Could not update sender runtime state after committed bank transfer: {exception}");
            }

            try
            {
                if (_playerManager.TryGetSessionById(recipientUserId, out var recipientSession) &&
                    recipientSession.AttachedEntity is { Valid: true } recipientEntity &&
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
                result.RecipientBalance);
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
        CharacterBankTransferResult originalResult)
    {
        var senderBalance = originalResult.SenderBalance;
        var recipientBalance = originalResult.RecipientBalance;
        var senderBalanceLoaded = false;
        var recipientBalanceLoaded = false;

        try
        {
            var senderPrefs = await _db.GetPlayerPreferencesAsync(fromSession.UserId, CancellationToken.None);
            if (senderPrefs?.Characters.TryGetValue(fromSlot, out var senderCharacter) == true &&
                senderCharacter is HumanoidCharacterProfile senderProfile)
            {
                senderBalance = senderProfile.BankBalance;
                senderBalanceLoaded = true;
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Could not reload sender after unknown bank transfer outcome: {exception}");
        }

        try
        {
            var recipientPrefs = await _db.GetPlayerPreferencesAsync(recipientUserId, CancellationToken.None);
            if (recipientPrefs?.Characters.TryGetValue(recipientSlot, out var recipientCharacter) == true &&
                recipientCharacter is HumanoidCharacterProfile recipientProfile)
            {
                recipientBalance = recipientProfile.BankBalance;
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
                _prefsManager.TryApplyPersistedBankBalance(fromSession.UserId, fromSlot, senderBalance);
                if (TryComp<BankAccountComponent>(fromUid, out var senderBank))
                {
                    senderBank.Balance = senderBalance;
                    Dirty(fromUid, senderBank);
                }

                RaiseLocalEvent(new BalanceChangedEvent(fromSession, senderBalance));
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
                _prefsManager.TryApplyPersistedBankBalance(recipientUserId, recipientSlot, recipientBalance);
                if (_playerManager.TryGetSessionById(recipientUserId, out var recipientSession) &&
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
        newBalance = null; // Default return
        if (amount <= 0)
        {
            _log.Info($"TryBankWithdraw: {amount} is invalid. Admin remove money variation.");
            return false;
        }

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"TryBankWithdraw: {session.UserId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }

        if (!TryAcquireBalanceMutation(session.UserId, index, out var lease))
            return false;

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
            !currentPrefs.Characters.TryGetValue(index, out var currentCharacter) ||
            currentCharacter is not HumanoidCharacterProfile currentProfile)
        {
            lease.Dispose();
            return false;
        }

        var balance = currentProfile.BankBalance;
        long totalBalance = balance;

        if (spendLongTerm)
        {
            var longTermBank = _coins.GetMonoCoinsBalance(session.UserId);
            totalBalance += longTermBank ?? 0l;
        }

        if (totalBalance < amount)
        {
            _log.Info($"TryBankWithdraw: {session.UserId} tried to withdraw {amount}, but has insufficient funds ({balance})");
            lease.Dispose();
            return false;
        }

        var leftoverAmount = amount;
        var longTermToSpend = 0;
        if (spendLongTerm)
        {
            var longTermBalance = totalBalance - balance;
            longTermToSpend = (int) Math.Min(longTermBalance, (long) leftoverAmount);
            leftoverAmount -= longTermToSpend;
        }
        balance -= leftoverAmount;

        var saveTask = _prefsManager.SetProfile(
            session.UserId,
            index,
            currentProfile.WithBankBalance(balance));
        Func<Task>? debitSavings = longTermToSpend > 0
            ? () => _coins.AddMonoCoinsAsync(session.UserId, -longTermToSpend)
            : null;
        ObserveProfileSave(
            saveTask,
            lease,
            "Bank withdrawal",
            session,
            afterSuccessfulSave: debitSavings);

        newBalance = balance;
        // Update any active admin UI with new balance
        RaiseLocalEvent(new BalanceChangedEvent(session, newBalance.Value));
        return true;
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
        newBalance = null; // Default return
        if (amount <= 0)
        {
            _log.Info($"TryBankDeposit: {amount} is invalid. Admin add money variation.");
            return false;
        }

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"{session.UserId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }

        if (!TryAcquireBalanceMutation(session.UserId, index, out var lease))
            return false;

        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out var currentPrefs) ||
            !currentPrefs.Characters.TryGetValue(index, out var currentCharacter) ||
            currentCharacter is not HumanoidCharacterProfile currentProfile ||
            currentProfile.BankBalance > int.MaxValue - amount)
        {
            lease.Dispose();
            return false;
        }

        newBalance = currentProfile.BankBalance + amount;
        var saveTask = _prefsManager.SetProfile(
            session.UserId,
            index,
            currentProfile.WithBankBalance(newBalance.Value));
        ObserveProfileSave(saveTask, lease, "Bank deposit", session);

        // Update any active admin UI with new balance
        RaiseLocalEvent(new BalanceChangedEvent(session, newBalance.Value));
        return true;
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
    public async Task<bool> TryBankWithdrawOffline(NetUserId userId, PlayerPreferences prefs, HumanoidCharacterProfile profile, int amount)
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

        if (!TryAcquireBalanceMutation(userId, index, out var lease))
            return false;

        try
        {
            var hasCachedPreferences = _prefsManager.TryGetCachedPreferences(userId, out var cachedPrefs);
            HumanoidCharacterProfile currentProfile;
            if (hasCachedPreferences)
            {
                if (!cachedPrefs!.Characters.TryGetValue(index, out var currentCharacter) ||
                    currentCharacter is not HumanoidCharacterProfile cachedProfile ||
                    !cachedProfile.Name.Equals(profile.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                currentProfile = cachedProfile;
            }
            else
            {
                var persistedPrefs = await _db.GetPlayerPreferencesAsync(userId, CancellationToken.None);
                if (persistedPrefs == null ||
                    !persistedPrefs.Characters.TryGetValue(index, out var persistedCharacter) ||
                    persistedCharacter is not HumanoidCharacterProfile persistedProfile ||
                    !persistedProfile.Name.Equals(profile.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                currentProfile = persistedProfile;
            }

            var balance = currentProfile.BankBalance;
            if (balance < amount)
            {
                _log.Info($"TryBankWithdrawOffline: {userId} tried to withdraw {amount}, but has insufficient funds ({balance})");
                return false;
            }

            balance -= amount;
            var newProfile = currentProfile.WithBankBalance(balance);

            if (hasCachedPreferences)
                await _prefsManager.SetProfile(userId, index, newProfile);
            else
                await _db.SaveCharacterSlotAsync(userId, newProfile, index);

            _log.Info($"Offline player {userId} withdrew {amount}");
            return true;
        }
        catch (Exception exception)
        {
            _log.Error($"Offline bank withdrawal failed for {userId}:{index}: {exception}");
            if (_playerManager.TryGetSessionById(userId, out var session))
                await ReconcileProfileAfterSaveFailureAsync(session, "Offline bank withdrawal");
            return false;
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
    public async Task<bool> TryBankDepositOffline(NetUserId userId, PlayerPreferences prefs, HumanoidCharacterProfile profile, int amount)
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

        if (!TryAcquireBalanceMutation(userId, index, out var lease))
            return false;

        try
        {
            var hasCachedPreferences = _prefsManager.TryGetCachedPreferences(userId, out var cachedPrefs);
            HumanoidCharacterProfile currentProfile;
            if (hasCachedPreferences)
            {
                if (!cachedPrefs!.Characters.TryGetValue(index, out var currentCharacter) ||
                    currentCharacter is not HumanoidCharacterProfile cachedProfile ||
                    !cachedProfile.Name.Equals(profile.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                currentProfile = cachedProfile;
            }
            else
            {
                var persistedPrefs = await _db.GetPlayerPreferencesAsync(userId, CancellationToken.None);
                if (persistedPrefs == null ||
                    !persistedPrefs.Characters.TryGetValue(index, out var persistedCharacter) ||
                    persistedCharacter is not HumanoidCharacterProfile persistedProfile ||
                    !persistedProfile.Name.Equals(profile.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                currentProfile = persistedProfile;
            }

            if (currentProfile.BankBalance > int.MaxValue - amount)
                return false;

            var newBalance = currentProfile.BankBalance + amount;
            var newProfile = currentProfile.WithBankBalance(newBalance);

            if (hasCachedPreferences)
                await _prefsManager.SetProfile(userId, index, newProfile);
            else
                await _db.SaveCharacterSlotAsync(userId, newProfile, index);

            _log.Info($"Offline player {userId} deposited {amount}");
            return true;
        }
        catch (Exception exception)
        {
            _log.Error($"Offline bank deposit failed for {userId}:{index}: {exception}");
            if (_playerManager.TryGetSessionById(userId, out var session))
                await ReconcileProfileAfterSaveFailureAsync(session, "Offline bank deposit");
            return false;
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
