using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Access.Systems;
using Content.Server.AlertLevel;
using Content.Server.CartridgeLoader;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Instruments;
using Content.Server.PDA.Ringer;
using Content.Server.Preferences.Managers;
using Content.Server.Station.Systems;
using Content.Server.Store.Systems;
using Content.Server.Traitor.Uplink;
using Content.Server._NF.Bank;
using Content.Server._LuaM.Donation;
using Content.Shared.Access.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.Chat;
using Content.Shared._NF.Bank;
using Content.Shared.Light;
using Content.Shared.Light.EntitySystems;
using Content.Shared.PDA;
using Content.Shared.Preferences;
using Robust.Shared.ContentPack;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Content.Shared._NF.Bank.Components; // Frontier
using Content.Shared._NF.Shipyard.Components; // Frontier
using Content.Server._NF.Shipyard.Systems; // Frontier
using Content.Server._NF.SectorServices; // Frontier
using Content.Shared._Mono.Company;
using Robust.Shared.Prototypes;
using Content.Shared.DeviceNetwork.Components;
using Robust.Server.Player;
using Robust.Shared.Timing;

namespace Content.Server.PDA
{
    public sealed partial class PdaSystem : SharedPdaSystem
    {
        [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
        [Dependency] private InstrumentSystem _instrument = default!;
        [Dependency] private RingerSystem _ringer = default!;
        [Dependency] private StationSystem _station = default!;
        [Dependency] private StoreSystem _store = default!;
        [Dependency] private IChatManager _chatManager = default!;
        [Dependency] private IPlayerManager _playerManager = default!;
        [Dependency] private UserInterfaceSystem _ui = default!;
        [Dependency] private UnpoweredFlashlightSystem _unpoweredFlashlight = default!;
        [Dependency] private ContainerSystem _containerSystem = default!;
        [Dependency] private IdCardSystem _idCard = default!;
        [Dependency] private SectorServiceSystem _sectorService = default!;
        [Dependency] private IPrototypeManager _prototypeManager = default!;
        [Dependency] private BankSystem _bank = default!;
        [Dependency] private LuaMDonationShopSystem _donationShop = default!;
        [Dependency] private IServerPreferencesManager _preferences = default!;
        [Dependency] private IServerDbManager _db = default!;
        [Dependency] private IResourceManager _resources = default!;
        [Dependency] private IGameTiming _timing = default!;

        private const int PdaBankIdLetterCount = 2;
        private const int PdaBankIdMinDigitCount = 4;
        private const int PdaBankIdDigitCount = 5;
        private static readonly ResPath PdaBankAccountRegistryPath = new("/luam/pda-bank-accounts.json");
        private readonly Dictionary<BankSlotKey, BankProfileIdentity> _activeBankProfiles = new();
        private readonly Dictionary<BankProfileIdentity, PdaBankAccountRecord> _registeredBankAccountIds = new();
        private readonly Dictionary<BankSlotKey, BankLoadAttempt> _bankStateLoadsInFlight = new();
        private readonly Dictionary<BankSlotKey, BankLoadRetry> _bankStateRetryAfter = new();
        private readonly Dictionary<BankProfileIdentity, PdaBankTransferRecovery> _bankTransferRecoveries = new();
        private readonly Dictionary<EntityUid, PendingPdaBankTransfer> _pendingBankTransferConfirmations = new();
        private readonly HashSet<EntityUid> _bankTransferPreviewsInFlight = new();
        private readonly object _legacyBankRegistryLock = new();
        private Task? _legacyBankRegistryImportTask;
        private readonly HashSet<EntityUid> _bankTransferMessagesInFlight = new();
        private readonly HashSet<EntityUid> _registeredBankTransfersInFlight = new();
        private readonly HashSet<BankStableProfileIdentity> _bankTransferRetryBlockedProfiles = new();
        private long _nextBankLoadAttemptId;

        private readonly record struct BankSlotKey(NetUserId UserId, int Slot);

        private readonly record struct BankProfileIdentity(
            NetUserId UserId,
            int Slot,
            int ProfileId,
            long Generation);

        private readonly record struct BankStableProfileIdentity(NetUserId UserId, int ProfileId);

        private readonly record struct BankLoadAttempt(long Id, long Generation, string CharacterName);

        private readonly record struct BankLoadRetry(long Generation, TimeSpan RetryAfter);

        private sealed record PendingPdaBankTransfer(
            Guid OperationId,
            BankProfileIdentity Sender,
            PdaBankAccountRecord Recipient,
            int Amount,
            TimeSpan ExpiresAt);

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<PdaComponent, LightToggleEvent>(OnLightToggle);

            // UI Events:
            SubscribeLocalEvent<PdaComponent, BoundUIOpenedEvent>(OnPdaOpen);
            SubscribeLocalEvent<PdaComponent, PdaRequestUpdateInterfaceMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaToggleFlashlightMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaShowRingtoneMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaShowMusicMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaShowUplinkMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaLockUplinkMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaBankTransferMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaBankTransferPreviewMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaBankTransferCancelMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, PdaBankTransferAcknowledgeMessage>(OnUiMessage);
            SubscribeLocalEvent<PdaComponent, ComponentShutdown>(OnPdaShutdown);
            SubscribeLocalEvent<PdaComponent, PdaDonationShopPurchaseMessage>(OnUiMessage);
            SubscribeLocalEvent<LuaMDonationShopAccountChangedEvent>(OnDonationShopAccountChanged);

            SubscribeLocalEvent<PdaComponent, CartridgeLoaderNotificationSentEvent>(OnNotification);

            SubscribeLocalEvent<StationRenamedEvent>(OnStationRenamed);
            SubscribeLocalEvent<EntityRenamedEvent>(OnEntityRenamed, after: new[] { typeof(IdCardSystem) });
            SubscribeLocalEvent<AlertLevelChangedEvent>(OnAlertLevelChanged);
            _preferences.CharacterSlotIdentityInvalidated += OnCharacterSlotIdentityInvalidated;
        }

        public override void Shutdown()
        {
            _preferences.CharacterSlotIdentityInvalidated -= OnCharacterSlotIdentityInvalidated;
            base.Shutdown();
        }

        private void OnCharacterSlotIdentityInvalidated(CharacterSlotIdentityInvalidated ev)
        {
            var slotKey = new BankSlotKey(ev.UserId, ev.Slot);
            var invalidatedIdentities = new HashSet<BankProfileIdentity>();
            lock (_registeredBankAccountIds)
            {
                if (_activeBankProfiles.TryGetValue(slotKey, out var identity) &&
                    identity.Generation < ev.Generation)
                {
                    _activeBankProfiles.Remove(slotKey);
                    invalidatedIdentities.Add(identity);
                }

                invalidatedIdentities.UnionWith(_registeredBankAccountIds.Keys
                    .Where(key => key.UserId == ev.UserId &&
                                  key.Slot == ev.Slot &&
                                  key.Generation < ev.Generation));
                invalidatedIdentities.UnionWith(_bankTransferRecoveries.Keys
                    .Where(key => key.UserId == ev.UserId &&
                                  key.Slot == ev.Slot &&
                                  key.Generation < ev.Generation));

                foreach (var invalidatedIdentity in invalidatedIdentities)
                {
                    _registeredBankAccountIds.Remove(invalidatedIdentity);
                    _bankTransferRecoveries.Remove(invalidatedIdentity);
                }

                if (_bankStateLoadsInFlight.TryGetValue(slotKey, out var loadAttempt) &&
                    loadAttempt.Generation < ev.Generation)
                {
                    _bankStateLoadsInFlight.Remove(slotKey);
                }

                if (_bankStateRetryAfter.TryGetValue(slotKey, out var retry) &&
                    retry.Generation < ev.Generation)
                {
                    _bankStateRetryAfter.Remove(slotKey);
                }
            }

            lock (_pendingBankTransferConfirmations)
            {
                foreach (var (pdaUid, pending) in _pendingBankTransferConfirmations.ToArray())
                {
                    if (pending.Sender.UserId == ev.UserId &&
                        pending.Sender.Slot == ev.Slot &&
                        pending.Sender.Generation < ev.Generation)
                    {
                        _pendingBankTransferConfirmations.Remove(pdaUid);
                    }
                }
            }
        }

        private void RemovePendingBankTransfer(EntityUid pdaUid, Guid operationId)
        {
            lock (_pendingBankTransferConfirmations)
            {
                if (_pendingBankTransferConfirmations.TryGetValue(pdaUid, out var pending) &&
                    pending.OperationId == operationId)
                {
                    _pendingBankTransferConfirmations.Remove(pdaUid);
                }
            }
        }

        private void OnEntityRenamed(ref EntityRenamedEvent ev)
        {
            if (HasComp<IdCardComponent>(ev.Uid))
                return;

            if (_idCard.TryFindIdCard(ev.Uid, out var idCard))
            {
                var query = EntityQueryEnumerator<PdaComponent>();

                while (query.MoveNext(out var uid, out var comp))
                {
                    if (comp.ContainedId == idCard)
                    {
                        SetOwner(uid, comp, ev.Uid, ev.NewName);
                    }
                }
            }
        }

        protected override void OnComponentInit(EntityUid uid, PdaComponent pda, ComponentInit args)
        {
            base.OnComponentInit(uid, pda, args);

            if (!HasComp<UserInterfaceComponent>(uid))
                return;

            UpdateAlertLevel(uid, pda);
            UpdateStationName(uid, pda);
        }

        protected override void OnItemInserted(EntityUid uid, PdaComponent pda, EntInsertedIntoContainerMessage args)
        {
            base.OnItemInserted(uid, pda, args);
            var id = CompOrNull<IdCardComponent>(pda.ContainedId);
            if (id != null)
                pda.OwnerName = id.FullName;
            UpdatePdaUi(uid, pda);
        }

        protected override void OnItemRemoved(EntityUid uid, PdaComponent pda, EntRemovedFromContainerMessage args)
        {
            if (args.Container.ID != pda.IdSlot.ID && args.Container.ID != pda.PenSlot.ID && args.Container.ID != pda.PaiSlot.ID)
                return;

            // TODO: This is super cursed just use compstates please.
            if (MetaData(uid).EntityLifeStage >= EntityLifeStage.Terminating)
                return;

            base.OnItemRemoved(uid, pda, args);
            UpdatePdaUi(uid, pda);
        }

        private void OnLightToggle(EntityUid uid, PdaComponent pda, LightToggleEvent args)
        {
            pda.FlashlightOn = args.IsOn;
            UpdatePdaUi(uid, pda);
        }

        public void SetOwner(EntityUid uid, PdaComponent pda, EntityUid owner, string ownerName)
        {
            pda.OwnerName = ownerName;
            pda.PdaOwner = owner;
            UpdatePdaUi(uid, pda);
        }

        private void OnStationRenamed(StationRenamedEvent ev)
        {
            UpdateAllPdaUisOnStation();
        }

        private void OnAlertLevelChanged(AlertLevelChangedEvent args)
        {
            UpdateAllPdaUisOnStation();
        }

        private void UpdateAllPdaUisOnStation()
        {
            var query = AllEntityQuery<PdaComponent>();
            while (query.MoveNext(out var ent, out var comp))
            {
                UpdatePdaUi(ent, comp);
            }
        }

        private void OnNotification(Entity<PdaComponent> ent, ref CartridgeLoaderNotificationSentEvent args)
        {
            _ringer.RingerPlayRingtone(ent.Owner);

            if (!_containerSystem.TryGetContainingContainer((ent, null, null), out var container)
                || !TryComp<ActorComponent>(container.Owner, out var actor))
                return;

            var message = FormattedMessage.EscapeText(args.Message);
            var wrappedMessage = Loc.GetString("pda-notification-message",
                ("header", args.Header),
                ("message", message));

            _chatManager.ChatMessageToOne(
                ChatChannel.Notifications,
                message,
                wrappedMessage,
                EntityUid.Invalid,
                false,
                actor.PlayerSession.Channel);
        }

        /// <summary>
        /// Send new UI state to clients, call if you modify something like uplink.
        /// </summary>
        public void UpdatePdaUi(EntityUid uid, PdaComponent? pda = null, EntityUid? actor_uid = null) // Frontier
        {
            if (!Resolve(uid, ref pda, false))
                return;

            if (!_ui.HasUi(uid, PdaUiKey.Key))
                return;

            actor_uid = ResolvePdaUiActor(uid, pda, actor_uid);

            var address = GetDeviceNetAddress(uid);
            var hasInstrument = HasComp<InstrumentComponent>(uid);
            var showUplink = HasComp<UplinkComponent>(uid) && IsUnlocked(uid);

            UpdateStationName(uid, pda);
            UpdateAlertLevel(uid, pda);
            // TODO: Update the level and name of the station with each call to UpdatePdaUi is only needed for latejoin players.
            // TODO: If someone can implement changing the level and name of the station when changing the PDA grid, this can be removed.

            // TODO don't make this depend on cartridge loader!?!?
            if (!TryComp(uid, out CartridgeLoaderComponent? loader))
                return;

            var programs = _cartridgeLoader.GetAvailablePrograms(uid, loader);
            var id = CompOrNull<IdCardComponent>(pda.ContainedId);
            var balance = 0; // frontier
            string? bankAccountId = null; // frontier
            var payrollHourly = 0; // LuaM
            var payrollNextSeconds = 0; // LuaM
            var bankTransferRetryBlocked = false; // LuaM
            var bankTransferReady = false; // LuaM
            PdaBankTransferConfirmation? bankTransferConfirmation = null; // LuaM
            PdaBankTransferRecovery? bankTransferRecovery = null; // LuaM
            if (actor_uid != null && TryComp<BankAccountComponent>(actor_uid, out var account)) // frontier
            {
                balance = account.Balance; // frontier
                if (_playerManager.TryGetSessionByEntity(actor_uid.Value, out var actorSession) &&
                    TryGetBankSelection(actorSession, out var bankSlotKey, out var bankProfile))
                {
                    var currentGeneration = _preferences.GetCharacterSlotGeneration(
                        bankSlotKey.UserId,
                        bankSlotKey.Slot);
                    BankProfileIdentity? bankIdentity = null;
                    BankLoadAttempt? loadAttempt = null;
                    lock (_registeredBankAccountIds)
                    {
                        if (_activeBankProfiles.TryGetValue(bankSlotKey, out var activeIdentity) &&
                            activeIdentity.Generation == _preferences.GetCharacterSlotGeneration(
                                activeIdentity.UserId,
                                activeIdentity.Slot) &&
                            _registeredBankAccountIds.TryGetValue(activeIdentity, out var registeredAccount) &&
                            registeredAccount.ProfileId == activeIdentity.ProfileId)
                        {
                            bankIdentity = activeIdentity;
                            bankTransferReady = true;
                            bankAccountId = registeredAccount.BankId;
                            _bankTransferRecoveries.TryGetValue(activeIdentity, out bankTransferRecovery);
                        }

                        var retryAllowed = !_bankStateRetryAfter.TryGetValue(bankSlotKey, out var retry) ||
                                           retry.Generation != currentGeneration ||
                                           retry.RetryAfter <= _timing.CurTime;
                        if (!bankTransferReady &&
                            retryAllowed &&
                            !_bankStateLoadsInFlight.ContainsKey(bankSlotKey))
                        {
                            loadAttempt = new BankLoadAttempt(
                                checked(++_nextBankLoadAttemptId),
                                currentGeneration,
                                bankProfile.Name);
                            _bankStateLoadsInFlight[bankSlotKey] = loadAttempt.Value;
                        }
                    }

                    if (bankIdentity is { } currentIdentity)
                    {
                        bankTransferRetryBlocked = IsBankTransferRetryBlocked(currentIdentity);
                    }

                    if (loadAttempt is { } attempt)
                    {
                        _ = ObserveLoadPdaBankStateAsync(
                            uid,
                            actor_uid.Value,
                            actorSession,
                            bankSlotKey,
                            attempt);
                    }

                    lock (_pendingBankTransferConfirmations)
                    {
                        if (_pendingBankTransferConfirmations.TryGetValue(uid, out var pending) &&
                            bankIdentity is { } identity &&
                            pending.Sender == identity &&
                            pending.ExpiresAt > _timing.CurTime)
                        {
                            bankTransferConfirmation = new PdaBankTransferConfirmation(
                                pending.OperationId,
                                pending.Recipient.CharacterName,
                                pending.Recipient.BankId,
                                pending.Amount);
                        }
                    }

                    if (_bank.TryGetPayrollStatus(actor_uid.Value, actorSession, out var hourly, out var nextSeconds))
                    {
                        payrollHourly = hourly;
                        payrollNextSeconds = nextSeconds;
                    }
                }
            }
            var ownedShipName = ""; // Frontier
            if (TryComp<ShuttleDeedComponent>(pda.ContainedId, out var shuttleDeedComp)) // Frontier
                ownedShipName = ShipyardSystem.GetFullName(shuttleDeedComp); // Frontier
            var donationShopState = actor_uid != null && _playerManager.TryGetSessionByEntity(actor_uid.Value, out var donationSession)
                ? _donationShop.GetPdaState(donationSession)
                : _donationShop.GetPdaState(null);

            // Get company information from ID card
            string? companyName = null;
            Color companyColor = Color.White;
            if (id?.CompanyName != null && !string.IsNullOrWhiteSpace(id.CompanyName) && id.CompanyName != "None")
            {
                if (_prototypeManager.TryIndex<CompanyPrototype>(id.CompanyName, out var companyProto))
                {
                    companyName = companyProto.Name; // Use the display name, not the ID
                    companyColor = companyProto.Color;
                }
                else
                {
                    // Fallback to ID if prototype not found
                    companyName = id.CompanyName;
                }
            }

            var state = new PdaUpdateState(
                programs,
                GetNetEntity(loader.ActiveProgram),
                pda.FlashlightOn,
                pda.PenSlot.HasItem,
                pda.PaiSlot.HasItem,
                new PdaIdInfoText
                {
                    ActualOwnerName = pda.OwnerName,
                    IdOwner = id?.FullName,
                    JobTitle = id?.LocalizedJobTitle,
                    CompanyName = companyName,
                    CompanyColor = companyColor,
                    StationAlertLevel = pda.StationAlertLevel,
                    StationAlertColor = pda.StationAlertColor
                },
                balance, // Frontier
                bankAccountId, // Frontier
                pda.LastBankTransferStatus, // Frontier
                bankTransferRetryBlocked, // LuaM
                bankTransferReady, // LuaM
                bankTransferConfirmation, // LuaM
                bankTransferRecovery, // LuaM
                payrollHourly, // LuaM
                payrollNextSeconds, // LuaM
                ownedShipName, // Frontier
                pda.StationName,
                showUplink,
                hasInstrument,
                address,
                donationShopState.Balance,
                donationShopState.Access,
                donationShopState.AccessUntil,
                pda.LastDonationShopStatus,
                donationShopState.Listings,
                donationShopState.HasGoldPdaFrame);

            _ui.SetUiState(uid, PdaUiKey.Key, state);
        }

        private void OnPdaOpen(Entity<PdaComponent> ent, ref BoundUIOpenedEvent args)
        {
            if (!PdaUiKey.Key.Equals(args.UiKey))
                return;

            UpdatePdaUi(ent.Owner, ent.Comp, args.Actor); // Frontier
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaRequestUpdateInterfaceMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            UpdatePdaUi(uid, pda, msg.Actor);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaToggleFlashlightMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            // TODO PREDICTION
            // When moving this to shared, fill in the user field
            _unpoweredFlashlight.TryToggleLight(uid, user: null);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaShowRingtoneMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            if (HasComp<RingerComponent>(uid))
                _ringer.ToggleRingerUI(uid, msg.Actor);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaShowMusicMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            if (TryComp<InstrumentComponent>(uid, out var instrument))
                _instrument.ToggleInstrumentUi(uid, msg.Actor, instrument);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaShowUplinkMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            // check if its locked again to prevent malicious clients opening locked uplinks
            if (HasComp<UplinkComponent>(uid) && IsUnlocked(uid))
                _store.ToggleUi(msg.Actor, uid);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaLockUplinkMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            if (TryComp<RingerUplinkComponent>(uid, out var uplink))
            {
                _ringer.LockUplink(uid, uplink);
                UpdatePdaUi(uid, pda);
            }
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaBankTransferMessage msg)
        {
            _ = ObserveBankTransferMessageAsync(uid, pda, msg);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaBankTransferPreviewMessage msg)
        {
            _ = ObserveBankTransferPreviewAsync(uid, pda, msg);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaBankTransferCancelMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey) ||
                msg.Actor == EntityUid.Invalid ||
                !_playerManager.TryGetSessionByEntity(msg.Actor, out var session) ||
                !TryGetCurrentBankIdentity(msg.Actor, session, out var senderIdentity, out _))
                return;

            var cancelled = false;
            lock (_pendingBankTransferConfirmations)
            {
                if (_pendingBankTransferConfirmations.TryGetValue(uid, out var pending) &&
                    pending.OperationId == msg.OperationId &&
                    pending.Sender == senderIdentity)
                {
                    _pendingBankTransferConfirmations.Remove(uid);
                    cancelled = true;
                }
            }

            if (cancelled)
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-cancelled", msg.Actor);
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaBankTransferAcknowledgeMessage msg)
        {
            _ = ObserveBankTransferAcknowledgementAsync(uid, pda, msg);
        }

        private void OnPdaShutdown(EntityUid uid, PdaComponent component, ComponentShutdown args)
        {
            lock (_pendingBankTransferConfirmations)
                _pendingBankTransferConfirmations.Remove(uid);
        }

        private async Task ObserveBankTransferPreviewAsync(
            EntityUid uid,
            PdaComponent pda,
            PdaBankTransferPreviewMessage msg)
        {
            ICommonSession? requestSession = null;
            BankProfileIdentity? requestIdentity = null;
            if (msg.Actor != EntityUid.Invalid &&
                _playerManager.TryGetSessionByEntity(msg.Actor, out var currentSession) &&
                TryGetCurrentBankIdentity(msg.Actor, currentSession, out var identity, out _))
            {
                requestSession = currentSession;
                requestIdentity = identity;
            }

            try
            {
                await HandleBankTransferPreviewAsync(uid, pda, msg);
            }
            catch (Exception exception)
            {
                Log.Error($"Unhandled PDA bank transfer preview failure: {exception}");
                if (requestSession != null &&
                    requestIdentity is { } expectedIdentity &&
                    IsCurrentBankContext(msg.Actor, requestSession, expectedIdentity) &&
                    TryComp<PdaComponent>(uid, out var currentPda))
                {
                    SetBankTransferStatus(uid, currentPda, "comp-pda-ui-bank-transfer-internal-error", msg.Actor);
                }
            }
        }

        private async Task HandleBankTransferPreviewAsync(
            EntityUid uid,
            PdaComponent pda,
            PdaBankTransferPreviewMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            var actor = msg.Actor;
            var recipientId = ExtractPdaBankAccountId(msg.RecipientBankId);
            if (actor == EntityUid.Invalid ||
                !Exists(actor) ||
                !_playerManager.TryGetSessionByEntity(actor, out var session) ||
                !TryGetCurrentBankIdentity(actor, session, out var senderIdentity, out var senderAccount))
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-no-sender", actor);
                return;
            }

            if (string.IsNullOrWhiteSpace(recipientId))
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-missing-recipient", actor);
                return;
            }

            if (msg.Amount <= 0)
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-invalid-amount", actor);
                return;
            }

            lock (_registeredBankAccountIds)
            {
                if (_bankTransferRecoveries.ContainsKey(senderIdentity))
                {
                    SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-reconcile-required", actor);
                    return;
                }
            }

            bool previewStarted;
            lock (_bankTransferPreviewsInFlight)
                previewStarted = _bankTransferPreviewsInFlight.Add(actor);
            if (!previewStarted)
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-pending", actor);
                return;
            }

            try
            {
                var recipient = await _db.GetPdaBankAccountAsync(recipientId);
                if (!IsCurrentBankContext(actor, session, senderIdentity))
                    return;

                if (recipient == null)
                {
                    SetBankTransferStatus(
                        uid,
                        pda,
                        "comp-pda-ui-bank-transfer-recipient-not-found",
                        actor,
                        ("id", recipientId));
                    return;
                }

                if (recipient.Value.ProfileId == senderAccount.ProfileId)
                {
                    SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-same-account", actor);
                    return;
                }

                var pending = new PendingPdaBankTransfer(
                    Guid.NewGuid(),
                    senderIdentity,
                    recipient.Value,
                    msg.Amount,
                    _timing.CurTime + TimeSpan.FromMinutes(2));
                lock (_registeredBankAccountIds)
                {
                    // Publish only while the same durable profile identity and slot
                    // generation are still active. Lifecycle invalidation takes this
                    // lock before clearing pending confirmations, so a stale preview
                    // cannot survive a delete/recreate or preference refresh race.
                    if (!IsCurrentBankContext(actor, session, senderIdentity))
                        return;

                    lock (_pendingBankTransferConfirmations)
                        _pendingBankTransferConfirmations[uid] = pending;
                }

                SetBankTransferStatus(
                    uid,
                    pda,
                    "comp-pda-ui-bank-transfer-confirmation-ready",
                    actor,
                    ("recipient", recipient.Value.CharacterName),
                    ("id", recipient.Value.BankId));
            }
            finally
            {
                lock (_bankTransferPreviewsInFlight)
                    _bankTransferPreviewsInFlight.Remove(actor);
            }
        }

        private async Task ObserveBankTransferMessageAsync(EntityUid uid, PdaComponent pda, PdaBankTransferMessage msg)
        {
            ICommonSession? requestSession = null;
            BankProfileIdentity? requestIdentity = null;
            if (msg.Actor != EntityUid.Invalid &&
                _playerManager.TryGetSessionByEntity(msg.Actor, out var currentSession) &&
                TryGetCurrentBankIdentity(msg.Actor, currentSession, out var identity, out _))
            {
                requestSession = currentSession;
                requestIdentity = identity;
            }

            try
            {
                await HandleBankTransferMessageAsync(uid, pda, msg);
            }
            catch (Exception exception)
            {
                Log.Error($"Unhandled PDA bank transfer failure: {exception}");

                // This observer must never fault: the UI event is necessarily fire-and-forget.
                try
                {
                    if (requestSession != null &&
                        requestIdentity is { } expectedIdentity &&
                        IsCurrentBankContext(msg.Actor, requestSession, expectedIdentity) &&
                        TryComp<PdaComponent>(uid, out var currentPda))
                    {
                        SetBankTransferStatus(
                            uid,
                            currentPda,
                            "comp-pda-ui-bank-transfer-internal-error",
                            msg.Actor);
                    }
                }
                catch (Exception statusException)
                {
                    Log.Error($"Could not report PDA bank transfer failure: {statusException}");
                }
            }
        }

        private async Task HandleBankTransferMessageAsync(EntityUid uid, PdaComponent pda, PdaBankTransferMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            var actor = msg.Actor;
            if (actor == EntityUid.Invalid ||
                !Exists(actor) ||
                !_playerManager.TryGetSessionByEntity(actor, out var session) ||
                !TryGetCurrentBankIdentity(actor, session, out var senderIdentity, out _))
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-no-sender", actor);
                return;
            }

            PendingPdaBankTransfer? pending = null;
            var confirmationInvalid = false;
            lock (_pendingBankTransferConfirmations)
            {
                if (!_pendingBankTransferConfirmations.TryGetValue(uid, out pending) ||
                    pending == null ||
                    pending.OperationId != msg.OperationId ||
                    pending.Sender != senderIdentity ||
                    pending.ExpiresAt <= _timing.CurTime)
                {
                    _pendingBankTransferConfirmations.Remove(uid);
                    confirmationInvalid = true;
                }
            }

            if (confirmationInvalid || pending == null)
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-confirmation-expired", actor);
                return;
            }

            if (IsBankTransferRetryBlocked(senderIdentity))
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-outcome-unknown", actor);
                return;
            }

            bool hasRecovery;
            lock (_registeredBankAccountIds)
                hasRecovery = _bankTransferRecoveries.ContainsKey(senderIdentity);
            if (hasRecovery)
            {
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-reconcile-required", actor);
                return;
            }

            bool transferStarted;
            lock (_bankTransferMessagesInFlight)
                transferStarted = _bankTransferMessagesInFlight.Add(actor);

            if (!transferStarted)
            {
                SetBankTransferStatus(
                    uid,
                    pda,
                    "comp-pda-ui-bank-transfer-pending",
                    actor);
                return;
            }

            try
            {
                await ProcessBankTransferMessageAsync(uid, pda, actor, pending);
            }
            finally
            {
                lock (_bankTransferMessagesInFlight)
                {
                    _bankTransferMessagesInFlight.Remove(actor);
                }
            }
        }

        private async Task ProcessBankTransferMessageAsync(
            EntityUid uid,
            PdaComponent pda,
            EntityUid actor,
            PendingPdaBankTransfer pending)
        {
            var result = await TryRegisteredBankTransferForIdentity(
                actor,
                pending.Sender,
                pending.Recipient,
                pending.Amount,
                pending.OperationId);
            var senderContextCurrent = TryGetCurrentBankIdentity(actor, out var currentSenderIdentity, out _) &&
                                       currentSenderIdentity == pending.Sender;
            if (!result.Success)
            {
                if (result.Error != "outcome-unknown")
                    RemovePendingBankTransfer(uid, pending.OperationId);

                if (senderContextCurrent)
                {
                    SetBankTransferStatus(
                        uid,
                        pda,
                        BankTransferErrorToLocale(result.Error),
                        actor,
                        ("id", pending.Recipient.BankId),
                        ("reason", result.Error));
                }
                return;
            }

            RemovePendingBankTransfer(uid, pending.OperationId);
            if (senderContextCurrent)
            {
                lock (_registeredBankAccountIds)
                {
                    _bankTransferRecoveries[pending.Sender] = new PdaBankTransferRecovery(
                        pending.OperationId,
                        pending.Recipient.CharacterName,
                        pending.Recipient.BankId,
                        pending.Amount,
                        result.SenderBalance);
                }
            }

            var amount = BankSystemExtensions.ToSpesoString(pending.Amount);
            var balance = BankSystemExtensions.ToSpesoString(result.SenderBalance);
            if (senderContextCurrent)
            {
                try
                {
                    SetBankTransferStatus(
                        uid,
                        pda,
                        "comp-pda-ui-bank-transfer-success",
                        actor,
                        ("amount", amount),
                        ("recipient", result.RecipientName),
                        ("id", pending.Recipient.BankId),
                        ("balance", balance),
                        ("operation", pending.OperationId.ToString("N")));
                }
                catch (Exception exception)
                {
                    Log.Error($"Committed PDA bank transfer, but could not update sender UI: {exception}");
                }
            }

            if (!result.AlreadyProcessed && result.RecipientSession != null)
            {
                try
                {
                    NotifyBankTransferRecipient(result.RecipientSession, amount, actor);
                }
                catch (Exception exception)
                {
                    Log.Error($"Committed PDA bank transfer, but could not notify recipient: {exception}");
                }
            }

            if (result.RecipientEntity is { Valid: true } recipientEntity)
            {
                try
                {
                    UpdatePdaUisForActor(recipientEntity);
                }
                catch (Exception exception)
                {
                    Log.Error($"Committed PDA bank transfer, but could not refresh recipient UI: {exception}");
                }
            }
        }

        private void OnUiMessage(EntityUid uid, PdaComponent pda, PdaDonationShopPurchaseMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            var actor = msg.Actor;
            if (actor == EntityUid.Invalid || !Exists(actor))
            {
                pda.LastDonationShopStatus = Loc.GetString("comp-pda-ui-donation-shop-status-no-user");
                UpdatePdaUi(uid, pda);
                return;
            }

            _donationShop.TryPurchase(actor, msg.ListingId, out var status);
            pda.LastDonationShopStatus = status;
            UpdatePdaUi(uid, pda, actor);
        }

        private void OnDonationShopAccountChanged(LuaMDonationShopAccountChangedEvent ev)
        {
            if (_playerManager.TryGetSessionById(ev.UserId, out var session) &&
                session.AttachedEntity is { Valid: true } attached)
            {
                UpdatePdaUisForActor(attached);
            }
        }

        private static string BankTransferErrorToLocale(string error)
        {
            return error switch
            {
                "amount-invalid" => "comp-pda-ui-bank-transfer-invalid-amount",
                "sender-no-account" => "comp-pda-ui-bank-transfer-no-sender",
                "same-account" => "comp-pda-ui-bank-transfer-same-account",
                "insufficient-funds" => "comp-pda-ui-bank-transfer-insufficient-funds",
                "ironman-blocked" => "comp-pda-ui-bank-transfer-blocked",
                "recipient-not-found" => "comp-pda-ui-bank-transfer-recipient-not-found",
                "recipient-overflow" => "comp-pda-ui-bank-transfer-recipient-overflow",
                "operation-conflict" => "comp-pda-ui-bank-transfer-conflict",
                "transfer-pending" => "comp-pda-ui-bank-transfer-pending",
                "conflict" => "comp-pda-ui-bank-transfer-conflict",
                "outcome-unknown" => "comp-pda-ui-bank-transfer-outcome-unknown",
                _ => "comp-pda-ui-bank-transfer-failed",
            };
        }

        private bool IsBankTransferRetryBlocked(BankProfileIdentity identity)
        {
            lock (_bankTransferRetryBlockedProfiles)
                return _bankTransferRetryBlockedProfiles.Contains(ToStableIdentity(identity));
        }

        private static BankStableProfileIdentity ToStableIdentity(BankProfileIdentity identity)
        {
            return new BankStableProfileIdentity(identity.UserId, identity.ProfileId);
        }

        private void SetBankTransferStatus(
            EntityUid uid,
            PdaComponent pda,
            string locId,
            EntityUid actor,
            params (string, object)[] args)
        {
            pda.LastBankTransferStatus = Loc.GetString(locId, args);
            UpdatePdaUi(uid, pda, actor);
        }

        private void NotifyBankTransferRecipient(ICommonSession recipient, string amount, EntityUid sender)
        {
            var senderName = MetaData(sender).EntityName;
            var message = Loc.GetString(
                "comp-pda-ui-bank-transfer-received",
                ("amount", amount),
                ("sender", senderName));

            _chatManager.ChatMessageToOne(
                ChatChannel.Notifications,
                message,
                message,
                EntityUid.Invalid,
                false,
                recipient.Channel);
        }

        private void UpdatePdaUisForActor(EntityUid actor)
        {
            var query = EntityQueryEnumerator<PdaComponent>();
            while (query.MoveNext(out var uid, out var pda))
            {
                if (pda.PdaOwner == actor)
                {
                    UpdatePdaUi(uid, pda, actor);
                    continue;
                }

                if (_containerSystem.TryGetContainingContainer((uid, null, null), out var container) &&
                    container.Owner == actor)
                {
                    UpdatePdaUi(uid, pda, actor);
                }
            }
        }

        private Task<PdaBankTransferResult> TryRegisteredBankTransfer(
            EntityUid sender,
            PdaBankAccountRecord recipient,
            int amount,
            Guid operationId)
        {
            if (!TryGetCurrentBankIdentity(sender, out var senderIdentity, out _))
                return Task.FromResult(PdaBankTransferResult.Fail("sender-no-account"));

            return TryRegisteredBankTransferForIdentity(sender, senderIdentity, recipient, amount, operationId);
        }

        private async Task<PdaBankTransferResult> TryRegisteredBankTransferForIdentity(
            EntityUid sender,
            BankProfileIdentity senderIdentity,
            PdaBankAccountRecord recipient,
            int amount,
            Guid operationId)
        {
            lock (_registeredBankTransfersInFlight)
            {
                if (!_registeredBankTransfersInFlight.Add(sender))
                    return PdaBankTransferResult.Fail("transfer-pending");
            }

            try
            {
                return await TryRegisteredBankTransferCore(sender, senderIdentity, recipient, amount, operationId);
            }
            finally
            {
                lock (_registeredBankTransfersInFlight)
                {
                    _registeredBankTransfersInFlight.Remove(sender);
                }
            }
        }

        private async Task ObserveBankTransferAcknowledgementAsync(
            EntityUid uid,
            PdaComponent pda,
            PdaBankTransferAcknowledgeMessage msg)
        {
            ICommonSession? requestSession = null;
            BankProfileIdentity? requestIdentity = null;
            try
            {
                if (!PdaUiKey.Key.Equals(msg.UiKey) ||
                    msg.Actor == EntityUid.Invalid ||
                    !_playerManager.TryGetSessionByEntity(msg.Actor, out var session) ||
                    !TryGetCurrentBankIdentity(msg.Actor, session, out var senderIdentity, out _))
                {
                    return;
                }
                requestSession = session;
                requestIdentity = senderIdentity;

                PdaBankTransferRecovery? recovery;
                lock (_registeredBankAccountIds)
                    _bankTransferRecoveries.TryGetValue(senderIdentity, out recovery);
                if (recovery == null || recovery.OperationId != msg.OperationId)
                    return;

                var acknowledged = await _db.AcknowledgeCharacterBankTransferAsync(
                        session.UserId,
                        senderIdentity.ProfileId,
                        msg.OperationId);
                if (!IsCurrentBankContext(msg.Actor, session, senderIdentity))
                    return;

                if (!acknowledged)
                {
                    SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-reconcile-required", msg.Actor);
                    return;
                }

                lock (_registeredBankAccountIds)
                    _bankTransferRecoveries.Remove(senderIdentity);
                SetBankTransferStatus(uid, pda, "comp-pda-ui-bank-transfer-acknowledged", msg.Actor);
            }
            catch (Exception exception)
            {
                Log.Error($"Could not acknowledge PDA bank transfer result: {exception}");
                if (requestSession != null &&
                    requestIdentity is { } expectedIdentity &&
                    IsCurrentBankContext(msg.Actor, requestSession, expectedIdentity) &&
                    TryComp<PdaComponent>(uid, out var currentPda))
                {
                    SetBankTransferStatus(uid, currentPda, "comp-pda-ui-bank-transfer-reconcile-required", msg.Actor);
                }
            }
        }

        private EntityUid? ResolvePdaUiActor(EntityUid uid, PdaComponent pda, EntityUid? requestedActor)
        {
            if (requestedActor.HasValue &&
                requestedActor.Value.Valid &&
                Exists(requestedActor.Value) &&
                HasComp<BankAccountComponent>(requestedActor.Value))
            {
                return requestedActor;
            }

            if (_containerSystem.TryGetContainingContainer((uid, null, null), out var container) &&
                container.Owner.Valid &&
                Exists(container.Owner) &&
                HasComp<BankAccountComponent>(container.Owner))
            {
                return container.Owner;
            }

            if (pda.PdaOwner.HasValue &&
                pda.PdaOwner.Value.Valid &&
                Exists(pda.PdaOwner.Value) &&
                HasComp<BankAccountComponent>(pda.PdaOwner.Value))
            {
                return pda.PdaOwner;
            }

            return requestedActor;
        }

        private async Task<PdaBankTransferResult> TryRegisteredBankTransferCore(
            EntityUid sender,
            BankProfileIdentity senderIdentity,
            PdaBankAccountRecord recipient,
            int amount,
            Guid operationId)
        {
            if (string.IsNullOrWhiteSpace(recipient.BankId))
                return PdaBankTransferResult.Fail("recipient-not-found");

            if (amount <= 0)
                return PdaBankTransferResult.Fail("amount-invalid");

            if (!HasComp<BankAccountComponent>(sender))
                return PdaBankTransferResult.Fail("sender-no-account");

            if (!TryGetCurrentBankIdentity(sender, out var currentIdentity, out _) ||
                currentIdentity != senderIdentity)
            {
                return PdaBankTransferResult.Fail("sender-no-account");
            }

            // Capture the initiating account before yielding. The attached entity can
            // disappear while the database transaction is committing, but an unknown
            // outcome must still disable retries after the player reconnects.
            if (!_playerManager.TryGetSessionByEntity(sender, out var senderSessionBeforeTransfer))
                return PdaBankTransferResult.Fail("sender-no-account");

            var persistedResult = await _bank.TryBankTransferPersistedAsync(
                sender,
                senderIdentity.ProfileId,
                recipient.UserId,
                recipient.ProfileId,
                recipient.Slot,
                recipient.CharacterName,
                amount,
                operationId);
            if (!persistedResult.Success)
            {
                if (persistedResult.Status == CharacterBankTransferStatus.UnknownOutcome)
                {
                    lock (_bankTransferRetryBlockedProfiles)
                        _bankTransferRetryBlockedProfiles.Add(ToStableIdentity(senderIdentity));
                }

                return PdaBankTransferResult.Fail(persistedResult.Error, persistedResult.SenderBalance);
            }

            ICommonSession? recipientSession = null;
            EntityUid? recipientEntity = null;
            if (_playerManager.TryGetSessionById(recipient.UserId, out recipientSession) &&
                recipientSession.AttachedEntity is { Valid: true } attached &&
                _preferences.TryGetCachedPreferences(recipient.UserId, out var updatedRecipientPrefs) &&
                updatedRecipientPrefs.SelectedCharacterIndex == recipient.Slot)
            {
                recipientEntity = attached;
            }

            return PdaBankTransferResult.Ok(
                persistedResult.SenderBalance,
                persistedResult.RecipientBalance,
                recipient.CharacterName,
                recipientSession,
                recipientEntity,
                persistedResult.AlreadyProcessed);
        }

        private string GetPdaBankAccountId(ICommonSession session, EntityUid? owner = null)
        {
            var ownerName = owner is { Valid: true } ownerUid && Exists(ownerUid)
                ? MetaData(ownerUid).EntityName
                : null;

            return BuildPdaBankAccountId(ownerName, session.Name, session.UserId.ToString());
        }

        private static string BuildPdaBankAccountId(string? ownerName, string? userName, string userId)
        {
            return GetPdaBankAccountPrefix(ownerName, userName, userId) +
                   GetStablePdaBankAccountDigits(userId);
        }

        private bool TryGetBankSelection(
            ICommonSession session,
            out BankSlotKey key,
            out HumanoidCharacterProfile profile)
        {
            if (_preferences.TryGetCachedPreferences(session.UserId, out var prefs) &&
                prefs.SelectedCharacter is HumanoidCharacterProfile selected &&
                prefs.TryIndexOfCharacter(selected, out var slot))
            {
                key = new BankSlotKey(session.UserId, slot);
                profile = selected;
                return true;
            }

            key = default;
            profile = default!;
            return false;
        }

        private bool TryGetCurrentBankIdentity(
            EntityUid actor,
            out BankProfileIdentity identity,
            out PdaBankAccountRecord account)
        {
            if (_playerManager.TryGetSessionByEntity(actor, out var session))
                return TryGetCurrentBankIdentity(actor, session, out identity, out account);

            identity = default;
            account = default;
            return false;
        }

        private bool TryGetCurrentBankIdentity(
            EntityUid actor,
            ICommonSession session,
            out BankProfileIdentity identity,
            out PdaBankAccountRecord account)
        {
            identity = default;
            account = default;
            if (session.AttachedEntity != actor ||
                !_playerManager.TryGetSessionById(session.UserId, out var currentSession) ||
                !ReferenceEquals(currentSession, session) ||
                !TryGetBankSelection(session, out var slotKey, out _))
            {
                return false;
            }

            lock (_registeredBankAccountIds)
            {
                return _activeBankProfiles.TryGetValue(slotKey, out identity) &&
                       identity.Generation == _preferences.GetCharacterSlotGeneration(identity.UserId, identity.Slot) &&
                       _registeredBankAccountIds.TryGetValue(identity, out account) &&
                       account.ProfileId == identity.ProfileId;
            }
        }

        private bool IsCurrentBankContext(
            EntityUid actor,
            ICommonSession session,
            BankProfileIdentity expectedIdentity)
        {
            return TryGetCurrentBankIdentity(actor, session, out var identity, out _) &&
                   identity == expectedIdentity;
        }

        private bool IsBankLoadContextCurrent(
            EntityUid actor,
            ICommonSession session,
            BankSlotKey key,
            BankLoadAttempt attempt,
            bool requireAttempt)
        {
            if (session.AttachedEntity != actor ||
                !_playerManager.TryGetSessionById(session.UserId, out var currentSession) ||
                !ReferenceEquals(currentSession, session) ||
                !_playerManager.TryGetSessionByEntity(actor, out var actorSession) ||
                !ReferenceEquals(actorSession, session) ||
                !TryGetBankSelection(session, out var currentKey, out var currentProfile) ||
                currentKey != key ||
                !currentProfile.Name.Equals(attempt.CharacterName, StringComparison.Ordinal) ||
                _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot) != attempt.Generation)
            {
                return false;
            }

            if (!requireAttempt)
                return true;

            lock (_registeredBankAccountIds)
            {
                return _bankStateLoadsInFlight.TryGetValue(key, out var currentAttempt) &&
                       currentAttempt == attempt;
            }
        }

        private void SetBankLoadRetryIfCurrent(BankSlotKey key, BankLoadAttempt attempt)
        {
            lock (_registeredBankAccountIds)
            {
                if (_bankStateLoadsInFlight.TryGetValue(key, out var currentAttempt) &&
                    currentAttempt == attempt)
                {
                    _bankStateRetryAfter[key] = new BankLoadRetry(
                        attempt.Generation,
                        _timing.CurTime + TimeSpan.FromSeconds(10));
                }
            }
        }

        private async Task ObserveLoadPdaBankStateAsync(
            EntityUid pdaUid,
            EntityUid actor,
            ICommonSession session,
            BankSlotKey key,
            BankLoadAttempt attempt)
        {
            try
            {
                await EnsureLegacyPdaBankRegistryImportedAsync();
                if (!IsBankLoadContextCurrent(actor, session, key, attempt, requireAttempt: true))
                    return;

                var preferredId = GetPdaBankAccountId(session, actor);
                var candidates = BuildPdaBankAccountCandidates(
                    preferredId,
                    attempt.CharacterName,
                    session.Name,
                    session.UserId.ToString(),
                    key.Slot);
                var registration = await _db.RegisterPdaBankAccountAsync(
                    session.UserId,
                    key.Slot,
                    attempt.CharacterName,
                    session.Name,
                    candidates);
                if (!IsBankLoadContextCurrent(actor, session, key, attempt, requireAttempt: true))
                    return;

                if (!registration.Success || registration.Account is not { } account)
                {
                    SetBankLoadRetryIfCurrent(key, attempt);
                    if (registration.Status == PdaBankAccountRegistrationStatus.ProfileMissing)
                        return;

                    Log.Error($"Could not register durable PDA bank state for {session.UserId}:{key.Slot}: " +
                              registration.Status);
                    if (TryComp<PdaComponent>(pdaUid, out var currentPda))
                        currentPda.LastBankTransferStatus = Loc.GetString("comp-pda-ui-bank-transfer-reconcile-required");
                    return;
                }

                var identity = new BankProfileIdentity(
                    account.UserId,
                    account.Slot,
                    account.ProfileId,
                    attempt.Generation);
                if (identity.UserId != key.UserId || identity.Slot != key.Slot)
                    throw new InvalidOperationException("PDA bank registration returned a mismatched profile identity.");

                PdaBankTransferRecovery? recovery = null;
                var journal = await _db.GetUnacknowledgedCharacterBankTransferAsync(
                    session.UserId,
                    identity.ProfileId);
                if (journal != null)
                {
                    var recipient = await _db.GetPdaBankAccountByProfileIdAsync(journal.Value.RecipientProfileId);
                    recovery = new PdaBankTransferRecovery(
                        journal.Value.OperationId,
                        recipient?.CharacterName ?? Loc.GetString("comp-pda-ui-unknown"),
                        recipient?.BankId ?? Loc.GetString("comp-pda-ui-unknown"),
                        journal.Value.Amount,
                        journal.Value.SenderBalanceAfter);
                }

                var activeProfileId = await _db.GetCharacterIdAsync(session.UserId, key.Slot);
                if (activeProfileId != identity.ProfileId ||
                    !IsBankLoadContextCurrent(actor, session, key, attempt, requireAttempt: true))
                {
                    return;
                }

                lock (_registeredBankAccountIds)
                {
                    if (!_bankStateLoadsInFlight.TryGetValue(key, out var currentAttempt) ||
                        currentAttempt != attempt ||
                        _preferences.GetCharacterSlotGeneration(key.UserId, key.Slot) != attempt.Generation)
                    {
                        return;
                    }

                    _activeBankProfiles[key] = identity;
                    _registeredBankAccountIds[identity] = account;
                    _bankStateRetryAfter.Remove(key);
                    if (recovery != null)
                        _bankTransferRecoveries[identity] = recovery;
                    else
                        _bankTransferRecoveries.Remove(identity);
                }
            }
            catch (Exception exception)
            {
                if (IsBankLoadContextCurrent(actor, session, key, attempt, requireAttempt: true))
                {
                    Log.Error($"Could not initialize durable PDA bank state for {session.UserId}:{key.Slot}: {exception}");
                    SetBankLoadRetryIfCurrent(key, attempt);
                    if (TryComp<PdaComponent>(pdaUid, out var currentPda))
                        currentPda.LastBankTransferStatus = Loc.GetString("comp-pda-ui-bank-transfer-reconcile-required");
                }
            }
            finally
            {
                lock (_registeredBankAccountIds)
                {
                    if (_bankStateLoadsInFlight.TryGetValue(key, out var currentAttempt) &&
                        currentAttempt == attempt)
                    {
                        _bankStateLoadsInFlight.Remove(key);
                    }
                }

                if (IsBankLoadContextCurrent(actor, session, key, attempt, requireAttempt: false) &&
                    TryComp<PdaComponent>(pdaUid, out var currentPda))
                    UpdatePdaUi(pdaUid, currentPda, actor);
            }
        }

        private Task EnsureLegacyPdaBankRegistryImportedAsync()
        {
            lock (_legacyBankRegistryLock)
                return _legacyBankRegistryImportTask ??= ImportLegacyPdaBankRegistryAsync();
        }

        private async Task ImportLegacyPdaBankRegistryAsync()
        {
            var registry = LoadLegacyPdaBankAccountRegistry();
            foreach (var (legacyId, record) in registry)
            {
                if (!Guid.TryParse(record.UserId, out var userGuid))
                    continue;

                try
                {
                    var normalizedLegacyId = ExtractPdaBankAccountId(legacyId);
                    var preferred = BuildPdaBankAccountId(record.CharacterName, record.LastUserName, record.UserId);
                    var candidates = BuildPdaBankAccountCandidates(
                        normalizedLegacyId,
                        record.CharacterName,
                        record.LastUserName,
                        record.UserId,
                        record.Slot,
                        preferred);
                    var registration = await _db.RegisterPdaBankAccountAsync(
                        new NetUserId(userGuid),
                        record.Slot,
                        record.CharacterName,
                        record.LastUserName,
                        candidates);
                    if (!registration.Success &&
                        registration.Status != PdaBankAccountRegistrationStatus.ProfileMissing)
                    {
                        Log.Error($"Could not import legacy PDA bank account {legacyId}: " +
                                  registration.Status);
                    }
                }
                catch (Exception exception)
                {
                    // A bad legacy entry must not prevent new canonical accounts
                    // from registering. It remains in the source JSON for repair.
                    Log.Error($"Could not import legacy PDA bank account {legacyId}: {exception}");
                }
            }
        }

        private Dictionary<string, LegacyPdaBankAccountRecord> LoadLegacyPdaBankAccountRegistry()
        {
            _resources.UserData.CreateDir(PdaBankAccountRegistryPath.Directory);

            if (!_resources.UserData.TryReadAllText(PdaBankAccountRegistryPath, out var json))
                return new Dictionary<string, LegacyPdaBankAccountRecord>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, LegacyPdaBankAccountRecord>>(json);
                return deserialized == null
                    ? new Dictionary<string, LegacyPdaBankAccountRecord>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, LegacyPdaBankAccountRecord>(deserialized, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException exception)
            {
                Log.Error($"Legacy PDA bank registry is malformed and was not imported; source file was left untouched: {exception}");
                return new Dictionary<string, LegacyPdaBankAccountRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string ExtractPdaBankAccountId(string value)
        {
            var chars = value
                .Trim()
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray();

            for (var i = 0; i <= chars.Length - (PdaBankIdLetterCount + PdaBankIdMinDigitCount); i++)
            {
                if (!char.IsLetter(chars[i]) || !char.IsLetter(chars[i + 1]))
                    continue;

                var digitCount = 0;
                for (var j = i + PdaBankIdLetterCount;
                     j < chars.Length && digitCount < PdaBankIdDigitCount && char.IsDigit(chars[j]);
                     j++)
                {
                    digitCount++;
                }

                if (digitCount < PdaBankIdMinDigitCount)
                    continue;

                return new string(chars.Skip(i).Take(PdaBankIdLetterCount + digitCount).ToArray());
            }

            return string.Empty;
        }

        private static string GetPdaBankAccountPrefix(params string?[] sources)
        {
            var letters = new char[PdaBankIdLetterCount];
            var count = 0;

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                foreach (var ch in source)
                {
                    if (!char.IsLetter(ch))
                        continue;

                    letters[count++] = char.ToUpperInvariant(ch);
                    if (count == PdaBankIdLetterCount)
                        return new string(letters);
                }
            }

            while (count < PdaBankIdLetterCount)
            {
                letters[count] = (char) ('X' + count);
                count++;
            }

            return new string(letters);
        }

        private static string GetStablePdaBankAccountDigits(string seed)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;

            foreach (var ch in seed)
            {
                hash ^= char.ToUpperInvariant(ch);
                hash *= prime;
            }

            var minimum = (uint) Math.Pow(10, PdaBankIdDigitCount - 1);
            var range = minimum * 9;
            var suffix = hash % range + minimum;
            return suffix.ToString($"D{PdaBankIdDigitCount}", CultureInfo.InvariantCulture);
        }

        private static IReadOnlyList<string> BuildPdaBankAccountCandidates(
            string preferredId,
            string characterName,
            string userName,
            string userId,
            int slot,
            string? secondaryPreferredId = null)
        {
            const int collisionCandidateCount = 128;
            var candidates = new List<string>(collisionCandidateCount + 2);

            void AddCandidate(string? candidate)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(candidate);
            }

            AddCandidate(ExtractPdaBankAccountId(preferredId));
            AddCandidate(ExtractPdaBankAccountId(secondaryPreferredId ?? string.Empty));
            for (var salt = 1; salt <= collisionCandidateCount; salt++)
            {
                AddCandidate(BuildPdaBankAccountCollisionId(
                    characterName,
                    userName,
                    userId,
                    slot,
                    salt));
            }

            return candidates;
        }

        private static string BuildPdaBankAccountCollisionId(
            string? characterName,
            string? userName,
            string userId,
            int slot,
            int salt)
        {
            return GetPdaBankAccountPrefix(characterName, userName, userId) +
                   GetStablePdaBankAccountDigits($"{userId}:{slot}:{salt}");
        }

        private sealed record LegacyPdaBankAccountRecord(
            string UserId,
            int Slot,
            string CharacterName,
            string LastUserName);

        private sealed record PdaBankTransferResult(
            bool Success,
            string Error,
            int SenderBalance,
            int RecipientBalance,
            string RecipientName,
            ICommonSession? RecipientSession,
            EntityUid? RecipientEntity,
            bool AlreadyProcessed)
        {
            public static PdaBankTransferResult Ok(
                int senderBalance,
                int recipientBalance,
                string recipientName,
                ICommonSession? recipientSession,
                EntityUid? recipientEntity,
                bool alreadyProcessed)
            {
                return new PdaBankTransferResult(
                    true,
                    string.Empty,
                    senderBalance,
                    recipientBalance,
                    recipientName,
                    recipientSession,
                    recipientEntity,
                    alreadyProcessed);
            }

            public static PdaBankTransferResult Fail(string error, int senderBalance = 0)
            {
                return new PdaBankTransferResult(false, error, senderBalance, 0, string.Empty, null, null, false);
            }
        }

        private bool IsUnlocked(EntityUid uid)
        {
            return !TryComp<RingerUplinkComponent>(uid, out var uplink) || uplink.Unlocked;
        }

        private void UpdateStationName(EntityUid uid, PdaComponent pda)
        {
            var station = _station.GetOwningStation(uid);
            pda.StationName = station is null ? null : Name(station.Value);
        }

        private void UpdateAlertLevel(EntityUid uid, PdaComponent pda)
        {
            //var station = _station.GetOwningStation(uid); // Frontier
            var station = _sectorService.GetServiceEntity(); // Frontier
            if (!TryComp(station, out AlertLevelComponent? alertComp) ||
                alertComp.AlertLevels == null)
                return;
            pda.StationAlertLevel = alertComp.CurrentLevel;
            if (alertComp.AlertLevels.Levels.TryGetValue(alertComp.CurrentLevel, out var details))
                pda.StationAlertColor = details.Color;
        }

        private string? GetDeviceNetAddress(EntityUid uid)
        {
            string? address = null;

            if (TryComp(uid, out DeviceNetworkComponent? deviceNetworkComponent))
            {
                address = deviceNetworkComponent?.Address;
            }

            return address;
        }
    }
}
