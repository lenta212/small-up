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
using Content.Shared._Mono.Traits.Physical;
using Content.Server._NF.Shipyard.Systems; // Frontier
using Content.Server._NF.SectorServices; // Frontier
using Content.Shared._Mono.Company;
using Robust.Shared.Prototypes;
using Content.Shared.DeviceNetwork.Components;
using Robust.Server.Player;

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

        private const int PdaBankIdLetterCount = 2;
        private const int PdaBankIdMinDigitCount = 4;
        private const int PdaBankIdDigitCount = 5;
        private static readonly ResPath PdaBankAccountRegistryPath = new("/luam/pda-bank-accounts.json");
        private static readonly JsonSerializerOptions PdaBankAccountRegistryJsonOptions = new()
        {
            WriteIndented = true,
        };

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
            SubscribeLocalEvent<PdaComponent, PdaDonationShopPurchaseMessage>(OnUiMessage);
            SubscribeLocalEvent<LuaMDonationShopAccountChangedEvent>(OnDonationShopAccountChanged);

            SubscribeLocalEvent<PdaComponent, CartridgeLoaderNotificationSentEvent>(OnNotification);

            SubscribeLocalEvent<StationRenamedEvent>(OnStationRenamed);
            SubscribeLocalEvent<EntityRenamedEvent>(OnEntityRenamed, after: new[] { typeof(IdCardSystem) });
            SubscribeLocalEvent<AlertLevelChangedEvent>(OnAlertLevelChanged);
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
            if (actor_uid != null && TryComp<BankAccountComponent>(actor_uid, out var account)) // frontier
            {
                balance = account.Balance; // frontier
                if (_playerManager.TryGetSessionByEntity(actor_uid.Value, out var actorSession))
                {
                    bankAccountId = RegisterPdaBankAccount(
                        GetPdaBankAccountId(actorSession, actor_uid.Value),
                        actorSession,
                        actor_uid.Value);
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
                donationShopState.Listings);

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

            UpdatePdaUi(uid, pda);
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
            _ = HandleBankTransferMessageAsync(uid, pda, msg);
        }

        private async Task HandleBankTransferMessageAsync(EntityUid uid, PdaComponent pda, PdaBankTransferMessage msg)
        {
            if (!PdaUiKey.Key.Equals(msg.UiKey))
                return;

            var actor = msg.Actor;
            var recipientId = ExtractPdaBankAccountId(msg.RecipientBankId);
            if (actor == EntityUid.Invalid || !Exists(actor))
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

            if (TryFindOnlineBankAccount(recipientId, out var recipient, out var recipientSession))
            {
                if (!_bank.TryBankTransfer(actor, recipient, msg.Amount, out var onlineSenderBalance, out _, out var onlineError))
                {
                    if (onlineError == "not-online")
                    {
                        var fallbackResult = await TryRegisteredBankTransfer(actor, recipientId, msg.Amount);
                        if (fallbackResult.Success)
                        {
                            var fallbackAmount = BankSystemExtensions.ToSpesoString(msg.Amount);
                            var fallbackBalance = BankSystemExtensions.ToSpesoString(fallbackResult.SenderBalance);
                            SetBankTransferStatus(
                                uid,
                                pda,
                                "comp-pda-ui-bank-transfer-success",
                                actor,
                                ("amount", fallbackAmount),
                                ("recipient", fallbackResult.RecipientName),
                                ("id", recipientId),
                                ("balance", fallbackBalance));

                            if (fallbackResult.RecipientSession != null)
                                NotifyBankTransferRecipient(fallbackResult.RecipientSession, fallbackAmount, actor);

                            if (fallbackResult.RecipientEntity is { Valid: true } fallbackRecipientEntity)
                                UpdatePdaUisForActor(fallbackRecipientEntity);

                            return;
                        }

                        var fallbackLocId = BankTransferErrorToLocale(fallbackResult.Error);
                        SetBankTransferStatus(uid, pda, fallbackLocId, actor, ("id", recipientId), ("reason", fallbackResult.Error));
                        return;
                    }

                    var locId = BankTransferErrorToLocale(onlineError);
                    SetBankTransferStatus(uid, pda, locId, actor, ("reason", onlineError));
                    return;
                }

                var onlineAmount = BankSystemExtensions.ToSpesoString(msg.Amount);
                var onlineBalance = BankSystemExtensions.ToSpesoString(onlineSenderBalance);
                SetBankTransferStatus(
                    uid,
                    pda,
                    "comp-pda-ui-bank-transfer-success",
                    actor,
                    ("amount", onlineAmount),
                    ("recipient", recipientSession.Name),
                    ("id", recipientId),
                    ("balance", onlineBalance));

                NotifyBankTransferRecipient(recipientSession, onlineAmount, actor);
                UpdatePdaUisForActor(recipient);
                return;
            }

            var result = await TryRegisteredBankTransfer(actor, recipientId, msg.Amount);
            if (!result.Success)
            {
                var locId = BankTransferErrorToLocale(result.Error);
                SetBankTransferStatus(uid, pda, locId, actor, ("id", recipientId), ("reason", result.Error));
                return;
            }

            var amount = BankSystemExtensions.ToSpesoString(msg.Amount);
            var balance = BankSystemExtensions.ToSpesoString(result.SenderBalance);
            SetBankTransferStatus(
                uid,
                pda,
                "comp-pda-ui-bank-transfer-success",
                actor,
                ("amount", amount),
                ("recipient", result.RecipientName),
                ("id", recipientId),
                ("balance", balance));

            if (result.RecipientSession != null)
                NotifyBankTransferRecipient(result.RecipientSession, amount, actor);

            if (result.RecipientEntity is { Valid: true } recipientEntity)
                UpdatePdaUisForActor(recipientEntity);
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
                "same-account" => "comp-pda-ui-bank-transfer-same-account",
                "insufficient-funds" => "comp-pda-ui-bank-transfer-insufficient-funds",
                "ironman-blocked" => "comp-pda-ui-bank-transfer-blocked",
                "recipient-not-found" => "comp-pda-ui-bank-transfer-recipient-not-found",
                _ => "comp-pda-ui-bank-transfer-failed",
            };
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

        private bool TryFindOnlineBankAccount(
            string bankAccountId,
            out EntityUid recipient,
            out ICommonSession session)
        {
            recipient = EntityUid.Invalid;
            session = default!;

            foreach (var candidate in _playerManager.Sessions)
            {
                if (candidate.AttachedEntity is not { Valid: true } attached ||
                    !HasComp<BankAccountComponent>(attached) ||
                    !ExtractPdaBankAccountId(GetPdaBankAccountId(candidate, attached)).Equals(bankAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                recipient = attached;
                session = candidate;
                return true;
            }

            return false;
        }

        private async Task<PdaBankTransferResult> TryRegisteredBankTransfer(EntityUid sender, string recipientId, int amount)
        {
            recipientId = ExtractPdaBankAccountId(recipientId);
            if (string.IsNullOrWhiteSpace(recipientId))
                return PdaBankTransferResult.Fail("recipient-not-found");

            if (amount <= 0)
                return PdaBankTransferResult.Fail("amount-invalid");

            if (HasComp<IronmanComponent>(sender))
                return PdaBankTransferResult.Fail("ironman-blocked");

            if (!_playerManager.TryGetSessionByEntity(sender, out var senderSession) ||
                !_preferences.TryGetCachedPreferences(senderSession.UserId, out var senderPrefs) ||
                senderPrefs.SelectedCharacter is not HumanoidCharacterProfile senderProfile ||
                !senderPrefs.TryIndexOfCharacter(senderProfile, out var senderSlot))
            {
                return PdaBankTransferResult.Fail("sender-no-account");
            }

            if (!HasComp<BankAccountComponent>(sender))
                return PdaBankTransferResult.Fail("sender-no-account");

            var registry = LoadPdaBankAccountRegistry();
            if (!registry.TryGetValue(recipientId, out var record) ||
                !Guid.TryParse(record.UserId, out var recipientGuid))
            {
                return PdaBankTransferResult.Fail("recipient-not-found");
            }

            var recipientUserId = new NetUserId(recipientGuid);
            if (recipientUserId == senderSession.UserId && record.Slot == senderSlot)
                return PdaBankTransferResult.Fail("same-account");

            if (senderProfile.BankBalance < amount)
                return PdaBankTransferResult.Fail("insufficient-funds", senderProfile.BankBalance);

            var recipientPrefs = _preferences.TryGetCachedPreferences(recipientUserId, out var cachedRecipientPrefs)
                ? cachedRecipientPrefs
                : await _db.GetPlayerPreferencesAsync(recipientUserId, default);
            if (recipientPrefs == null)
                return PdaBankTransferResult.Fail("recipient-not-found");

            if (!TryGetRegisteredRecipientProfile(recipientPrefs, recipientUserId, record, recipientId, out var recipientSlot, out var recipientProfile))
                return PdaBankTransferResult.Fail("recipient-not-found");

            if (recipientProfile.BankBalance > int.MaxValue - amount)
                return PdaBankTransferResult.Fail("recipient-overflow");

            var senderBalance = senderProfile.BankBalance - amount;
            var recipientBalance = recipientProfile.BankBalance + amount;

            if (!_bank.TryBankWithdraw(senderSession, senderPrefs, senderProfile, amount, out var withdrawnBalance))
                return PdaBankTransferResult.Fail("insufficient-funds", senderProfile.BankBalance);

            senderBalance = withdrawnBalance.Value;
            _bank.SyncBankBalance(sender);

            var recipientDeposited = await _bank.TryBankDepositOffline(
                recipientUserId,
                recipientPrefs,
                recipientProfile,
                amount);
            if (!recipientDeposited)
            {
                int? refundedBalance = null;
                var refunded = _preferences.TryGetCachedPreferences(senderSession.UserId, out var refundPrefs) &&
                               refundPrefs.Characters.TryGetValue(senderSlot, out var refundProfile) &&
                               refundProfile is HumanoidCharacterProfile refundHumanoid &&
                               _bank.TryBankDeposit(senderSession, refundPrefs, refundHumanoid, amount, out refundedBalance);

                _bank.SyncBankBalance(sender);
                return PdaBankTransferResult.Fail("failed", refunded ? refundedBalance ?? senderProfile.BankBalance : senderBalance);
            }

            ICommonSession? recipientSession = null;
            EntityUid? recipientEntity = null;
            if (_playerManager.TryGetSessionById(recipientUserId, out recipientSession) &&
                recipientSession.AttachedEntity is { Valid: true } attached &&
                recipientPrefs.SelectedCharacter is HumanoidCharacterProfile selectedRecipientProfile &&
                recipientPrefs.TryIndexOfCharacter(selectedRecipientProfile, out var selectedRecipientSlot) &&
                selectedRecipientSlot == recipientSlot)
            {
                recipientEntity = attached;
                _bank.SyncBankBalance(attached);
            }

            return PdaBankTransferResult.Ok(senderBalance, recipientBalance, recipientProfile.Name, recipientSession, recipientEntity);
        }

        private bool TryGetRegisteredRecipientProfile(
            PlayerPreferences prefs,
            NetUserId userId,
            PdaBankAccountRecord record,
            string recipientId,
            out int slot,
            out HumanoidCharacterProfile profile)
        {
            if (prefs.Characters.TryGetValue(record.Slot, out var exactProfile) &&
                exactProfile is HumanoidCharacterProfile exactHumanoid &&
                (BuildPdaBankAccountId(exactHumanoid.Name, record.LastUserName, userId.ToString())
                     .Equals(recipientId, StringComparison.OrdinalIgnoreCase) ||
                 BuildPdaBankAccountId(record.CharacterName, record.LastUserName, userId.ToString())
                     .Equals(recipientId, StringComparison.OrdinalIgnoreCase)))
            {
                slot = record.Slot;
                profile = exactHumanoid;
                return true;
            }

            foreach (var (candidateSlot, candidateProfile) in prefs.Characters)
            {
                if (candidateProfile is not HumanoidCharacterProfile humanoid)
                    continue;

                if (!BuildPdaBankAccountId(humanoid.Name, record.LastUserName, userId.ToString())
                        .Equals(recipientId, StringComparison.OrdinalIgnoreCase))
                    continue;

                slot = candidateSlot;
                profile = humanoid;
                return true;
            }

            slot = 0;
            profile = default!;
            return false;
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

        private string RegisterPdaBankAccount(string bankAccountId, ICommonSession session, EntityUid owner)
        {
            if (!_preferences.TryGetCachedPreferences(session.UserId, out var prefs) ||
                prefs.SelectedCharacter is not HumanoidCharacterProfile profile ||
                !prefs.TryIndexOfCharacter(profile, out var slot))
            {
                return bankAccountId;
            }

            var normalized = ExtractPdaBankAccountId(bankAccountId);
            if (string.IsNullOrWhiteSpace(normalized))
                return bankAccountId;

            var ownerName = owner is { Valid: true } && Exists(owner)
                ? MetaData(owner).EntityName
                : profile.Name;
            var record = new PdaBankAccountRecord(session.UserId.ToString(), slot, ownerName, session.Name);
            var registry = LoadPdaBankAccountRegistry();
            var changed = false;
            var registeredId = normalized;

            if (registry.TryGetValue(registeredId, out var existingRecord) &&
                !IsSamePdaBankAccount(existingRecord, record))
            {
                registeredId = PickAvailablePdaBankAccountId(record, registry);
            }

            if (!registry.TryGetValue(registeredId, out var current) || !current.Equals(record))
            {
                registry[registeredId] = record;
                changed = true;
            }

            if (changed)
                SavePdaBankAccountRegistry(registry);

            return registeredId;
        }

        private Dictionary<string, PdaBankAccountRecord> LoadPdaBankAccountRegistry()
        {
            if (!_resources.UserData.TryReadAllText(PdaBankAccountRegistryPath, out var json))
                return new Dictionary<string, PdaBankAccountRecord>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, PdaBankAccountRecord>>(json) ??
                       new Dictionary<string, PdaBankAccountRecord>(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new Dictionary<string, PdaBankAccountRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SavePdaBankAccountRegistry(Dictionary<string, PdaBankAccountRecord> registry)
        {
            _resources.UserData.CreateDir(PdaBankAccountRegistryPath.Directory);
            _resources.UserData.WriteAllText(
                PdaBankAccountRegistryPath,
                JsonSerializer.Serialize(registry, PdaBankAccountRegistryJsonOptions));
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

        private static string PickAvailablePdaBankAccountId(
            PdaBankAccountRecord record,
            IReadOnlyDictionary<string, PdaBankAccountRecord> registry)
        {
            for (var salt = 1; salt <= 100; salt++)
            {
                var candidate = GetPdaBankAccountPrefix(record.CharacterName, record.LastUserName, record.UserId) +
                                GetStablePdaBankAccountDigits($"{record.UserId}:{record.Slot}:{salt}");

                if (!registry.TryGetValue(candidate, out var existingRecord) ||
                    IsSamePdaBankAccount(existingRecord, record))
                {
                    return candidate;
                }
            }

            return GetPdaBankAccountPrefix(record.CharacterName, record.LastUserName, record.UserId) +
                   GetStablePdaBankAccountDigits($"{record.UserId}:{record.Slot}:fallback");
        }

        private static bool IsSamePdaBankAccount(PdaBankAccountRecord left, PdaBankAccountRecord right)
        {
            return left.Slot == right.Slot &&
                   left.UserId.Equals(right.UserId, StringComparison.OrdinalIgnoreCase);
        }

        private sealed record PdaBankAccountRecord(string UserId, int Slot, string CharacterName, string LastUserName);

        private sealed record PdaBankTransferResult(
            bool Success,
            string Error,
            int SenderBalance,
            int RecipientBalance,
            string RecipientName,
            ICommonSession? RecipientSession,
            EntityUid? RecipientEntity)
        {
            public static PdaBankTransferResult Ok(
                int senderBalance,
                int recipientBalance,
                string recipientName,
                ICommonSession? recipientSession,
                EntityUid? recipientEntity)
            {
                return new PdaBankTransferResult(
                    true,
                    string.Empty,
                    senderBalance,
                    recipientBalance,
                    recipientName,
                    recipientSession,
                    recipientEntity);
            }

            public static PdaBankTransferResult Fail(string error, int senderBalance = 0)
            {
                return new PdaBankTransferResult(false, error, senderBalance, 0, string.Empty, null, null);
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
