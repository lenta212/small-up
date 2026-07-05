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
            _payrollTimers[session.UserId] = elapsed - payoutCount * PayrollIntervalSeconds;
            var payout = (int) Math.Min((long) hourly * payoutCount, int.MaxValue);

            if (TryPayrollDeposit(uid, payout, out var newBalance))
            {
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

        int toSector = amount;
        int toLongTerm = 0;
        if (tax)
        {
            GetTaxedDepositAmount(amount, bank.Balance, out var afterTax, out var taxedAway);
            toSector = afterTax;
            toLongTerm = taxedAway;
            _ = _coins.AddMonoCoinsAsync(session.UserId, taxedAway);
        }

        if (TryBankDeposit(session, prefs, profile, toSector, out var newBalance))
        {
            bank.Balance = newBalance.Value;
            Dirty(mobUid, bank);
            _log.Info($"{mobUid} deposited {amount} (sector: {toSector}, savings: {toLongTerm})");
            return true;
        }

        return false;
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

        if (profile.BankBalance > int.MaxValue - amount)
            return false;

        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
            return false;

        newBalance = profile.BankBalance + amount;
        _prefsManager.SetProfile(session.UserId, index, profile.WithBankBalance(newBalance));
        bank.Balance = newBalance;
        Dirty(mobUid, bank);
        RaiseLocalEvent(new BalanceChangedEvent(session, newBalance));
        return true;
    }

    /// <summary>
    /// Transfers money between two online character bank accounts.
    /// This bypasses deposit tax and updates both saved profiles together.
    /// </summary>
    public bool TryBankTransfer(
        EntityUid fromUid,
        EntityUid toUid,
        int amount,
        out int fromBalance,
        out int toBalance,
        out string error)
    {
        fromBalance = 0;
        toBalance = 0;
        error = string.Empty;

        if (amount <= 0)
        {
            error = "amount-invalid";
            _log.Info($"TryBankTransfer: {amount} is invalid from Uid {fromUid} to Uid {toUid}");
            return false;
        }

        if (fromUid == toUid)
        {
            error = "same-account";
            return false;
        }

        if (!TryComp<BankAccountComponent>(fromUid, out var fromBank))
        {
            error = "sender-no-account";
            _log.Info($"TryBankTransfer: {fromUid} has no bank account");
            return false;
        }

        if (!TryComp<BankAccountComponent>(toUid, out var toBank))
        {
            error = "recipient-no-account";
            _log.Info($"TryBankTransfer: {toUid} has no bank account");
            return false;
        }

        if (HasComp<IronmanComponent>(fromUid) || HasComp<IronmanComponent>(toUid))
        {
            error = "ironman-blocked";
            _log.Info($"TryBankTransfer: transfer blocked by Ironman component ({fromUid} -> {toUid})");
            return false;
        }

        if (!_playerManager.TryGetSessionByEntity(fromUid, out var fromSession) ||
            !_playerManager.TryGetSessionByEntity(toUid, out var toSession))
        {
            error = "not-online";
            _log.Info($"TryBankTransfer: missing attached session ({fromUid} -> {toUid})");
            return false;
        }

        if (!_prefsManager.TryGetCachedPreferences(fromSession.UserId, out var fromPrefs) ||
            !_prefsManager.TryGetCachedPreferences(toSession.UserId, out var toPrefs))
        {
            error = "prefs-missing";
            _log.Info($"TryBankTransfer: missing cached prefs ({fromSession.UserId} -> {toSession.UserId})");
            return false;
        }

        if (fromPrefs.SelectedCharacter is not HumanoidCharacterProfile fromProfile ||
            toPrefs.SelectedCharacter is not HumanoidCharacterProfile toProfile)
        {
            error = "profile-invalid";
            _log.Info($"TryBankTransfer: invalid selected character profile ({fromSession.UserId} -> {toSession.UserId})");
            return false;
        }

        if (fromProfile.BankBalance < amount)
        {
            error = "insufficient-funds";
            fromBalance = fromProfile.BankBalance;
            return false;
        }

        if (toProfile.BankBalance > int.MaxValue - amount)
        {
            error = "recipient-overflow";
            return false;
        }

        var fromIndex = fromPrefs.IndexOfCharacter(fromProfile);
        var toIndex = toPrefs.IndexOfCharacter(toProfile);
        if (fromIndex == -1 || toIndex == -1)
        {
            error = "profile-index-missing";
            _log.Info($"TryBankTransfer: selected character index missing ({fromSession.UserId} -> {toSession.UserId})");
            return false;
        }

        fromBalance = fromProfile.BankBalance - amount;
        toBalance = toProfile.BankBalance + amount;

        _prefsManager.SetProfile(fromSession.UserId, fromIndex, fromProfile.WithBankBalance(fromBalance));
        _prefsManager.SetProfile(toSession.UserId, toIndex, toProfile.WithBankBalance(toBalance));

        fromBank.Balance = fromBalance;
        toBank.Balance = toBalance;
        Dirty(fromUid, fromBank);
        Dirty(toUid, toBank);

        RaiseLocalEvent(new BalanceChangedEvent(fromSession, fromBalance));
        RaiseLocalEvent(new BalanceChangedEvent(toSession, toBalance));
        _log.Info($"{fromUid} transferred {amount} to {toUid}");
        return true;
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

        int balance = profile.BankBalance;
        long totalBalance = balance;

        if (spendLongTerm)
        {
            var longTermBank = _coins.GetMonoCoinsBalance(session.UserId);
            totalBalance += longTermBank ?? 0l;
        }

        if (totalBalance < amount)
        {
            _log.Info($"TryBankWithdraw: {session.UserId} tried to withdraw {amount}, but has insufficient funds ({balance})");
            return false;
        }

        int leftoverAmount = amount;
        if (spendLongTerm)
        {
            var longTermBalance = totalBalance - balance;
            var toSpend = (int)Math.Min(longTermBalance, (long)leftoverAmount);
            leftoverAmount -= toSpend;
            _ = _coins.AddMonoCoinsAsync(session.UserId, -toSpend);
        }
        balance -= leftoverAmount;

        var newProfile = profile.WithBankBalance(balance);
        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"TryBankWithdraw: {session.UserId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }
        _prefsManager.SetProfile(session.UserId, index, newProfile);
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

        newBalance = profile.BankBalance + amount;

        var newProfile = profile.WithBankBalance(newBalance.Value);
        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"{session.UserId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }
        _prefsManager.SetProfile(session.UserId, index, newProfile);
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

        int balance = profile.BankBalance;

        if (balance < amount)
        {
            _log.Info($"TryBankWithdrawOffline: {userId} tried to withdraw {amount}, but has insufficient funds ({balance})");
            return false;
        }

        balance -= amount;

        var newProfile = profile.WithBankBalance(balance);
        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"TryBankWithdrawOffline: {userId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }

        // Update preferences in cache if the player data exists
        if (_prefsManager.TryGetCachedPreferences(userId, out var cachedPrefs))
        {
            _prefsManager.SetProfile(userId, index, newProfile);
        }
        else
        {
            // If not in cache, save directly to database
            await _db.SaveCharacterSlotAsync(userId, newProfile, index);
        }

        _log.Info($"Offline player {userId} withdrew {amount}");
        return true;
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

        int newBalance = profile.BankBalance + amount;

        var newProfile = profile.WithBankBalance(newBalance);
        var index = prefs.IndexOfCharacter(profile);
        if (index == -1)
        {
            _log.Info($"TryBankDepositOffline: {userId} tried to adjust the balance of {profile.Name}, but they were not in the user's character set.");
            return false;
        }

        // Update preferences in cache if the player data exists
        if (_prefsManager.TryGetCachedPreferences(userId, out var cachedPrefs))
        {
            _prefsManager.SetProfile(userId, index, newProfile);
        }
        else
        {
            // If not in cache, save directly to database
            await _db.SaveCharacterSlotAsync(userId, newProfile, index);
        }

        _log.Info($"Offline player {userId} deposited {amount}");
        return true;
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
