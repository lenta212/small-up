using System.Linq;
using System.Threading.Tasks;
using Content.Server._NF.Bank;
using System.Numerics;
using Content.Server.Advertise;
using Content.Server.Advertise.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Cargo.Components;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared._NF.Bank.Components; // Frontier
using Content.Shared.Cargo;
using Content.Shared.Damage;
using Content.Shared.Destructible;
using Content.Shared.DoAfter;
using Content.Shared.Emag.Systems;
using Content.Shared.Emp;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Throwing;
using Content.Shared.UserInterface;
using Content.Shared.VendingMachines;
using Content.Shared.Wall;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Audio.Systems;
using Content.Server.Administration.Logs; // Frontier
using Content.Shared.Database; // Frontier
using Content.Shared._NF.Bank.BUI; // Frontier
using Content.Server._NF.Contraband.Systems; // Frontier
using Content.Shared.Stacks; // Frontier
using Content.Server.Stack;
using Content.Server._Mono.VendingMachine;
using Content.Shared._Mono.Traits.Physical;
using Robust.Shared.Containers; // Frontier

namespace Content.Server.VendingMachines
{
    internal enum VendingCashRollbackOutcome
    {
        Restored,
        Spent,
        Unknown,
    }

    internal readonly record struct VendingCashObservation(
        bool EntityExists,
        bool SameComponent,
        int Count);

    internal readonly record struct VendingCashRollbackResolution(
        VendingCashRollbackOutcome Outcome,
        int? ObservedCount);

    public sealed partial class VendingMachineSystem : SharedVendingMachineSystem
    {
        [Dependency] private IRobustRandom _random = default!;
        [Dependency] private AccessReaderSystem _accessReader = default!;
        [Dependency] private AppearanceSystem _appearanceSystem = default!;
        [Dependency] private PricingSystem _pricing = default!;
        [Dependency] private ThrowingSystem _throwingSystem = default!;
        [Dependency] private IGameTiming _timing = default!;
        [Dependency] private SpeakOnUIClosedSystem _speakOnUIClosed = default!;
        [Dependency] private SharedPointLightSystem _light = default!;
        [Dependency] private EmagSystem _emag = default!;

        [Dependency] private IPrototypeManager _prototypeManager = default!; // Frontier
        [Dependency] private SharedAudioSystem _audioSystem = default!; // Frontier
        [Dependency] private BankSystem _bankSystem = default!; // Frontier
        [Dependency] private PopupSystem _popupSystem = default!; // Frontier
        [Dependency] private IAdminLogManager _adminLogger = default!; // Frontier
        [Dependency] private StackSystem _stack = default!; // Frontier
        [Dependency] private VendingMachinePurchaseSystem _vendingPurchase = default!; // Mono
        [Dependency] private UserInterfaceSystem _userInterface = default!;

        private const float WallVendEjectDistanceFromWall = 1f;
        private readonly HashSet<EntityUid> _bankVendsInFlight = new();

        internal static VendingCashRollbackResolution ResolveVendingCashRollback(
            bool cashMutationAttempted,
            int previousCount,
            int spentCount,
            Action restoreCash,
            Func<VendingCashObservation> observeCash,
            Action<Exception>? onFailure = null)
        {
            if (!cashMutationAttempted)
            {
                return new VendingCashRollbackResolution(
                    VendingCashRollbackOutcome.Restored,
                    previousCount);
            }

            try
            {
                restoreCash();
            }
            catch (Exception exception)
            {
                onFailure?.Invoke(exception);
            }

            VendingCashObservation observation;
            try
            {
                observation = observeCash();
            }
            catch (Exception exception)
            {
                onFailure?.Invoke(exception);
                return new VendingCashRollbackResolution(VendingCashRollbackOutcome.Unknown, null);
            }

            if (!observation.EntityExists || !observation.SameComponent)
                return new VendingCashRollbackResolution(VendingCashRollbackOutcome.Unknown, null);

            if (observation.Count == previousCount)
            {
                return new VendingCashRollbackResolution(
                    VendingCashRollbackOutcome.Restored,
                    observation.Count);
            }

            if (observation.Count == spentCount)
            {
                return new VendingCashRollbackResolution(
                    VendingCashRollbackOutcome.Spent,
                    observation.Count);
            }

            return new VendingCashRollbackResolution(
                VendingCashRollbackOutcome.Unknown,
                observation.Count);
        }

        internal static bool TryCalculateBatchTotal(
            int unitPrice,
            int quantity,
            out int totalPrice)
        {
            totalPrice = 0;
            if (unitPrice < 0 ||
                quantity is < 1 or > VendingMachineComponent.MaxPurchaseQuantity)
            {
                return false;
            }

            var total = (long) unitPrice * quantity;
            if (total > int.MaxValue)
                return false;

            totalPrice = (int) total;
            return true;
        }

        internal static bool HasSufficientStock(uint amount, int quantity)
        {
            if (quantity is < 1 or > VendingMachineComponent.MaxPurchaseQuantity)
                return false;

            return amount == uint.MaxValue || amount >= (uint) quantity;
        }

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<VendingMachineComponent, PowerChangedEvent>(OnPowerChanged);
            SubscribeLocalEvent<VendingMachineComponent, BreakageEventArgs>(OnBreak);
            SubscribeLocalEvent<VendingMachineComponent, DamageChangedEvent>(OnDamageChanged);
            SubscribeLocalEvent<VendingMachineComponent, PriceCalculationEvent>(OnVendingPrice);
            SubscribeLocalEvent<VendingMachineComponent, EntInsertedIntoContainerMessage>(OnEntityInserted); // Frontier
            SubscribeLocalEvent<VendingMachineComponent, EntRemovedFromContainerMessage>(OnEntityRemoved); // Frontier

            SubscribeLocalEvent<VendingMachineComponent, ActivatableUIOpenAttemptEvent>(OnActivatableUIOpenAttempt);

            Subs.BuiEvents<VendingMachineComponent>(VendingMachineUiKey.Key, subs =>
            {
                subs.Event<BoundUIOpenedEvent>(OnVendingUiOpened);
                subs.Event<VendingMachineEjectMessage>(OnInventoryEjectMessage);
            });

            SubscribeLocalEvent<VendingMachineComponent, VendingMachineSelfDispenseEvent>(OnSelfDispense);

            SubscribeLocalEvent<VendingMachineComponent, RestockDoAfterEvent>(OnDoAfter);

            SubscribeLocalEvent<VendingMachineRestockComponent, PriceCalculationEvent>(OnPriceCalculation);
        }

        private void OnVendingPrice(EntityUid uid, VendingMachineComponent component, ref PriceCalculationEvent args)
        {
            var price = 0.0;

            foreach (var entry in component.Inventory.Values)
            {
                if (!PrototypeManager.TryIndex<EntityPrototype>(entry.ID, out var proto))
                {
                    Log.Error($"Unable to find entity prototype {entry.ID} on {ToPrettyString(uid)} vending.");
                    continue;
                }

                price += entry.Amount; //* _pricing.GetEstimatedPrice(proto); Frontier - This is used to price the worth of a vending machine with the inventory it has.
            }

            //args.Price += price; Frontier - This is used to price the worth of a vending machine with the inventory it has.
        }

        protected override void OnMapInit(EntityUid uid, VendingMachineComponent component, MapInitEvent args)
        {
            base.OnMapInit(uid, component, args);

            if (HasComp<ApcPowerReceiverComponent>(uid))
            {
                TryUpdateVisualState(uid, component);
            }
        }

        private void OnActivatableUIOpenAttempt(EntityUid uid, VendingMachineComponent component, ActivatableUIOpenAttemptEvent args)
        {
            if (component.Broken)
                args.Cancel();
        }

        private void OnVendingUiOpened(EntityUid uid, VendingMachineComponent component, BoundUIOpenedEvent args)
        {
            UpdatePriceQuotes(uid, component);
        }

        internal bool TryGetUnitPrice(
            VendingMachineComponent component,
            EntityPrototype prototype,
            out int unitPrice)
        {
            unitPrice = 0;
            if (!component.RequiresCash)
                return true;

            var price = _pricing.GetEstimatedPrice(prototype);
            // Prototype pricing uses zero for otherwise-unpriced items, while vending
            // has historically charged a fallback amount for them.
            if (price == 0)
                price = 20;

            if (TryComp<MarketModifierComponent>(component.Owner, out var modifier))
                price *= modifier.Mod;

            // An explicit vending price overrides the general estimated price.
            var vendPrice = _pricing.GetEstimatedVendPrice(prototype);
            if (vendPrice > 0.0)
                price = vendPrice;

            if (!double.IsFinite(price) || price <= 0.0 || price > int.MaxValue)
                return false;

            unitPrice = checked((int) Math.Floor(price));
            return unitPrice > 0;
        }

        private void UpdatePriceQuotes(EntityUid uid, VendingMachineComponent component)
        {
            var prices = new Dictionary<string, int>();
            var entries = component.Inventory.Values
                .Concat(component.EmaggedInventory.Values)
                .Concat(component.ContrabandInventory.Values);

            foreach (var entry in entries)
            {
                if (!_prototypeManager.TryIndex<EntityPrototype>(entry.ID, out var prototype) ||
                    !TryGetUnitPrice(component, prototype, out var unitPrice))
                {
                    continue;
                }

                prices[entry.ID] = unitPrice;
            }

            _userInterface.SetUiState(
                uid,
                VendingMachineUiKey.Key,
                new VendingMachineBoundUserInterfaceState(prices, component.RequiresCash));
        }

        private void OnInventoryEjectMessage(EntityUid uid, VendingMachineComponent component, VendingMachineEjectMessage args)
        {
            if (!this.IsPowered(uid, EntityManager))
                return;

            if (args.Actor is not { Valid: true } entity || Deleted(entity))
                return;

            if (component.Ejecting)
                return;

            if (args.ExpectedTotalPrice < 0)
            {
                Deny(uid, component);
                UpdatePriceQuotes(uid, component);
                return;
            }

            _ = ObserveAuthorizedVendAsync(
                uid,
                entity,
                args.Type,
                args.ID,
                args.Quantity,
                args.ExpectedTotalPrice,
                component);
        }

        private async Task ObserveAuthorizedVendAsync(
            EntityUid uid,
            EntityUid sender,
            InventoryType type,
            string itemId,
            int quantity,
            int expectedTotalPrice,
            VendingMachineComponent component)
        {
            try
            {
                await AuthorizedVendAsync(
                    uid,
                    sender,
                    type,
                    itemId,
                    quantity,
                    expectedTotalPrice,
                    component,
                    allowBankAwait: true);
            }
            catch (Exception exception)
            {
                Log.Error($"Durable vending purchase failed for {ToPrettyString(sender)} at {ToPrettyString(uid)}: {exception}");
                if (!Deleted(uid) && TryComp<VendingMachineComponent>(uid, out var currentComponent))
                    Deny(uid, currentComponent);
            }
        }

        private void OnPowerChanged(EntityUid uid, VendingMachineComponent component, ref PowerChangedEvent args)
        {
            TryUpdateVisualState(uid, component);
        }

        private void OnBreak(EntityUid uid, VendingMachineComponent vendComponent, BreakageEventArgs eventArgs)
        {
            vendComponent.Broken = true;
            Dirty(uid, vendComponent);
            TryUpdateVisualState(uid, vendComponent);
        }

        private void OnDamageChanged(EntityUid uid, VendingMachineComponent component, DamageChangedEvent args)
        {
            if (!args.DamageIncreased && component.Broken)
            {
                component.Broken = false;
                Dirty(uid, component);
                TryUpdateVisualState(uid, component);
                return;
            }

            if (component.Broken || component.DispenseOnHitCoolingDown ||
                component.DispenseOnHitChance == null || args.DamageDelta == null)
                return;

            if (args.DamageIncreased && args.DamageDelta.GetTotal() >= component.DispenseOnHitThreshold &&
                _random.Prob(component.DispenseOnHitChance.Value))
            {
                if (component.DispenseOnHitCooldown > 0f)
                    component.DispenseOnHitCoolingDown = true;
                EjectRandom(uid, throwItem: true, forceEject: true, component);
            }
        }

        private void OnSelfDispense(EntityUid uid, VendingMachineComponent component, VendingMachineSelfDispenseEvent args)
        {
            if (args.Handled)
                return;

            args.Handled = true;
            EjectRandom(uid, throwItem: true, forceEject: false, component);
        }

        private void OnDoAfter(EntityUid uid, VendingMachineComponent component, DoAfterEvent args)
        {
            if (args.Handled || args.Cancelled || args.Args.Used == null)
                return;

            if (!TryComp<VendingMachineRestockComponent>(args.Args.Used, out var restockComponent))
            {
                Log.Error($"{ToPrettyString(args.Args.User)} tried to restock {ToPrettyString(uid)} with {ToPrettyString(args.Args.Used.Value)} which did not have a VendingMachineRestockComponent.");
                return;
            }

            TryRestockInventory(uid, component);

            Popup.PopupEntity(Loc.GetString("vending-machine-restock-done", ("this", args.Args.Used), ("user", args.Args.User), ("target", uid)), args.Args.User, PopupType.Medium);

            Audio.PlayPvs(restockComponent.SoundRestockDone, uid, AudioParams.Default.WithVolume(-2f).WithVariation(0.2f));

            Del(args.Args.Used.Value);

            args.Handled = true;
        }

        /// <summary>
        /// Sets the <see cref="VendingMachineComponent.CanShoot"/> property of the vending machine.
        /// </summary>
        public void SetShooting(EntityUid uid, bool canShoot, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return;

            component.CanShoot = canShoot;
        }

        /// <summary>
        /// Sets the <see cref="VendingMachineComponent.Contraband"/> property of the vending machine.
        /// </summary>
        public void SetContraband(EntityUid uid, bool contraband, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return;

            component.Contraband = contraband;
            Dirty(uid, component);
        }

        public void Deny(EntityUid uid, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            if (vendComponent.Denying)
                return;

            vendComponent.Denying = true;
            Audio.PlayPvs(vendComponent.SoundDeny, uid, AudioParams.Default.WithVolume(-2f));
            TryUpdateVisualState(uid, vendComponent);
        }

        /// <summary>
        /// Checks if the user is authorized to use this vending machine
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="sender">Entity trying to use the vending machine</param>
        /// <param name="vendComponent"></param>
        public bool IsAuthorized(EntityUid uid, EntityUid sender, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return false;

            if (!TryComp<AccessReaderComponent>(uid, out var accessReader))
                return true;

            if (_accessReader.IsAllowed(sender, uid, accessReader))
                return true;

            Popup.PopupEntity(Loc.GetString("vending-machine-component-try-eject-access-denied"), uid);
            Deny(uid, vendComponent);
            return false;
        }

        /// <summary>
        /// Tries to eject the provided item. Will do nothing if the vending machine is incapable of ejecting, already ejecting
        /// or the item doesn't exist in its inventory.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="type">The type of inventory the item is from</param>
        /// <param name="itemId">The prototype ID of the item</param>
        /// <param name="throwItem">Whether the item should be thrown in a random direction after ejection</param>
        /// <param name="vendComponent"></param>
        public bool TryEjectVendorItem(
            EntityUid uid,
            InventoryType type,
            string itemId,
            bool throwItem,
            VendingMachineComponent? vendComponent = null,
            int quantity = 1)
        {
            if (!Resolve(uid, ref vendComponent))
                return false;

            if (vendComponent.Ejecting || vendComponent.Broken || !this.IsPowered(uid, EntityManager))
            {
                return false;
            }

            var entry = GetEntry(uid, itemId, type, vendComponent);

            if (entry == null)
            {
                Popup.PopupEntity(Loc.GetString("vending-machine-component-try-eject-invalid-item"), uid);
                Deny(uid, vendComponent);
                return false;
            }

            if (!HasSufficientStock(entry.Amount, quantity))
            {
                Popup.PopupEntity(Loc.GetString("vending-machine-component-try-eject-out-of-stock"), uid);
                Deny(uid, vendComponent);
                return false;
            }

            if (string.IsNullOrEmpty(entry.ID))
                return false;

            if (!TryComp<TransformComponent>(vendComponent.Owner, out var transformComp))
                return false;

            // Start Ejecting, and prevent users from ordering while anim playing
            vendComponent.Ejecting = true;
            vendComponent.NextItemToEject = entry.ID;
            vendComponent.NextItemToEjectQuantity = quantity;
            vendComponent.ThrowNextItem = throwItem;

            if (TryComp(uid, out SpeakOnUIClosedComponent? speakComponent))
                _speakOnUIClosed.TrySetFlag((uid, speakComponent));

            // Frontier: unlimited vending
            // Infinite supplies must stay infinite.
            if (entry.Amount != uint.MaxValue)
                entry.Amount -= (uint) quantity;
            // End Frontier

            Dirty(uid, vendComponent);
            TryUpdateVisualState(uid, vendComponent);
            Audio.PlayPvs(vendComponent.SoundVend, uid);
            return true;
        }

        // Frontier: custom vending check
        /// <summary>
        /// Checks whether the user is authorized to use the vending machine, then ejects the provided item if true
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="sender">Entity that is trying to use the vending machine</param>
        /// <param name="type">The type of inventory the item is from</param>
        /// <param name="itemId">The prototype ID of the item</param>
        /// <param name="component"></param>
        public void AuthorizedVend(
            EntityUid uid,
            EntityUid sender,
            InventoryType type,
            string itemId,
            VendingMachineComponent component,
            int quantity = 1)
        {
            // Synchronous automation callers may still use free or cash-only
            // vending. A stored bank debit is never reported as complete before its
            // exact CAS, so those callers fail closed instead of spawning value.
            var task = AuthorizedVendAsync(
                uid,
                sender,
                type,
                itemId,
                quantity,
                null,
                component,
                allowBankAwait: false);
            if (!task.IsCompletedSuccessfully)
                _ = ObserveSynchronousAuthorizedVendAsync(task, uid, sender);
        }

        private async Task ObserveSynchronousAuthorizedVendAsync(Task<bool> task, EntityUid uid, EntityUid sender)
        {
            try
            {
                await task;
            }
            catch (Exception exception)
            {
                Log.Error($"Synchronous vending request failed for {ToPrettyString(sender)} at {ToPrettyString(uid)}: {exception}");
            }
        }

        private async Task<bool> AuthorizedVendAsync(
            EntityUid uid,
            EntityUid sender,
            InventoryType type,
            string itemId,
            int quantity,
            int? expectedTotalPrice,
            VendingMachineComponent component,
            bool allowBankAwait)
        {
            // Serialize the entire validation/finalization pipeline per machine.
            // Client-side pending buttons are not authoritative and a second BUI
            // request must not amplify quote work while a durable bank debit awaits.
            if (_bankVendsInFlight.Contains(uid))
                return false;

            if (quantity is < 1 or > VendingMachineComponent.MaxPurchaseQuantity)
            {
                Deny(uid, component);
                return false;
            }

            if (!_prototypeManager.TryIndex<EntityPrototype>(itemId, out var proto))
                return false;

            if (!TryGetUnitPrice(component, proto, out var unitPrice))
            {
                Log.Error($"Rejected invalid vending price for {proto.ID} at {ToPrettyString(uid)}.");
                Deny(uid, component);
                return false;
            }

            if (!TryCalculateBatchTotal(unitPrice, quantity, out var totalPrice))
            {
                Log.Error($"Rejected vending quantity {quantity} or total price for {proto.ID} at {ToPrettyString(uid)}.");
                Deny(uid, component);
                return false;
            }

            if (expectedTotalPrice is { } expected && expected != totalPrice)
            {
                Deny(uid, component);
                UpdatePriceQuotes(uid, component);
                return false;
            }

            if (!IsAuthorized(uid, sender, component))
                return false;

            var bankBalance = 0;
            if (!HasComp<IronmanComponent>(sender) && TryComp<BankAccountComponent>(sender, out var bank))
                bankBalance = bank.Balance;

            var cashSlotBalance = 0;
            Entity<StackComponent>? cashEntity = null;
            if (component.CashSlotName != null
                && component.CurrencyStackType != null
                && ItemSlots.TryGetSlot(uid, component.CashSlotName, out var cashSlot)
                && TryComp<StackComponent>(cashSlot?.ContainerSlot?.ContainedEntity, out var stackComp)
                && stackComp!.StackTypeId == component.CurrencyStackType)
            {
                cashSlotBalance = stackComp!.Count;
                cashEntity = (cashSlot!.ContainerSlot!.ContainedEntity.Value, stackComp!);
            }

            var availableFunds = (long) bankBalance + cashSlotBalance;
            if (totalPrice > availableFunds)
            {
                _popupSystem.PopupEntity(Loc.GetString("bank-insufficient-funds"), uid);
                Deny(uid, component);
                return false;
            }

            var cashToSpend = Math.Min(cashSlotBalance, totalPrice);
            var bankToSpend = totalPrice - cashToSpend;

            bool FinalizeVend()
            {
                if (Deleted(uid) ||
                    !TryComp<VendingMachineComponent>(uid, out var currentComponent) ||
                    !ReferenceEquals(currentComponent, component) ||
                    component.Ejecting ||
                    component.Broken ||
                    !this.IsPowered(uid, EntityManager))
                {
                    return false;
                }

                if (cashToSpend > 0 &&
                    (cashEntity == null ||
                     Deleted(cashEntity.Value.Owner) ||
                     !TryComp<StackComponent>(cashEntity.Value.Owner, out var currentCash) ||
                     currentCash.Count < cashToSpend ||
                     component.CashSlotName == null ||
                     !ItemSlots.TryGetSlot(uid, component.CashSlotName, out var currentCashSlot) ||
                     currentCashSlot?.ContainerSlot?.ContainedEntity != cashEntity.Value.Owner))
                {
                    return false;
                }

                var entry = GetEntry(uid, itemId, type, component);
                if (entry == null ||
                    !HasSufficientStock(entry.Amount, quantity) ||
                    string.IsNullOrEmpty(entry.ID))
                    return false;

                var reservedEntry = entry;
                var previousAmount = reservedEntry.Amount;
                var previousEjecting = component.Ejecting;
                var previousNextItem = component.NextItemToEject;
                var previousNextItemQuantity = component.NextItemToEjectQuantity;
                var previousThrowNextItem = component.ThrowNextItem;
                var previousPurchasePrice = component.LastPurchasePrice;
                var previousCashSlotBalance = component.CashSlotBalance;
                var previousCashCount = cashEntity?.Comp.Count;
                var previousCashLingering = cashEntity?.Comp.Lingering;
                var cashMutationAttempted = false;

                bool ResolveVendFailure(Exception failure)
                {
                    VendingCashRollbackResolution cashResolution;
                    if (cashEntity is { } cash && previousCashCount is { } cashCount)
                    {
                        cashResolution = ResolveVendingCashRollback(
                            cashMutationAttempted,
                            cashCount,
                            cashCount - cashToSpend,
                            restoreCash: () =>
                            {
                                if (Deleted(cash.Owner) ||
                                    !TryComp<StackComponent>(cash.Owner, out var currentCash) ||
                                    !ReferenceEquals(currentCash, cash.Comp))
                                {
                                    return;
                                }

                                currentCash.Lingering = true;
                                try
                                {
                                    _stack.SetCount(cash.Owner, cashCount, currentCash);
                                }
                                finally
                                {
                                    currentCash.Lingering = previousCashLingering ?? false;
                                }
                            },
                            observeCash: () =>
                            {
                                if (Deleted(cash.Owner))
                                    return new VendingCashObservation(false, false, 0);

                                if (!TryComp<StackComponent>(cash.Owner, out var currentCash))
                                    return new VendingCashObservation(false, false, 0);

                                return new VendingCashObservation(
                                    true,
                                    ReferenceEquals(currentCash, cash.Comp),
                                    currentCash.Count);
                            },
                            onFailure: rollbackException => Log.Error(
                                $"Could not restore or observe vending cash after {failure}: {rollbackException}"));
                    }
                    else
                    {
                        cashResolution = cashMutationAttempted
                            ? new VendingCashRollbackResolution(VendingCashRollbackOutcome.Unknown, null)
                            : new VendingCashRollbackResolution(VendingCashRollbackOutcome.Restored, null);
                    }

                    if (cashResolution.Outcome == VendingCashRollbackOutcome.Spent)
                    {
                        // The exact intended cash debit survived the failed
                        // restoration. Keep the already-reserved item and any bank
                        // debit together; compensating the bank here would create
                        // an underpaid or free vend.
                        component.CashSlotBalance = Math.Max(cashResolution.ObservedCount ?? 0, 0);
                        if (cashEntity is { } spentCash &&
                            !Deleted(spentCash.Owner) &&
                            spentCash.Comp.Count <= 0 &&
                            !spentCash.Comp.Lingering)
                        {
                            QueueDel(spentCash.Owner);
                        }

                        try
                        {
                            Dirty(uid, component);
                            TryUpdateVisualState(uid, component);
                        }
                        catch (Exception projectionException)
                        {
                            Log.Error($"Could not project retained paid vending reservation: {projectionException}");
                        }

                        Log.Error($"Vending cash remained spent after a failed vend finalizer; retaining the paid item reservation: {failure}");
                        return true;
                    }

                    // If cash was restored, the whole operation rolls back. If its
                    // state is missing/replaced/otherwise unknown, cancel the eject
                    // and compensate the exact bank debit rather than releasing an
                    // underpaid item. An unknown cash mutation can destroy value,
                    // but it cannot mint either an item or bank funds.
                    reservedEntry.Amount = previousAmount;
                    component.Ejecting = previousEjecting;
                    component.NextItemToEject = previousNextItem;
                    component.NextItemToEjectQuantity = previousNextItemQuantity;
                    component.ThrowNextItem = previousThrowNextItem;
                    component.LastPurchasePrice = previousPurchasePrice;
                    component.CashSlotBalance = cashResolution.Outcome == VendingCashRollbackOutcome.Restored
                        ? previousCashSlotBalance
                        : Math.Max(cashResolution.ObservedCount ?? 0, 0);

                    if (cashEntity is { } rolledBackCash &&
                        !Deleted(rolledBackCash.Owner) &&
                        rolledBackCash.Comp.Count <= 0 &&
                        !rolledBackCash.Comp.Lingering)
                    {
                        QueueDel(rolledBackCash.Owner);
                    }

                    if (cashResolution.Outcome == VendingCashRollbackOutcome.Unknown)
                        Log.Error($"Vending cash rollback outcome was unknown; the item was quarantined and the bank debit will be compensated: {failure}");

                    try
                    {
                        Dirty(uid, component);
                        TryUpdateVisualState(uid, component);
                    }
                    catch (Exception rollbackProjectionException)
                    {
                        // Authoritative reservation fields are restored; a stale
                        // appearance must not change the economic outcome.
                        Log.Error($"Could not project rolled-back vending state: {rollbackProjectionException}");
                    }

                    return false;
                }

                try
                {
                    component.LastPurchasePrice = unitPrice;
                    if (!TryEjectVendorItem(uid, type, itemId, component.CanShoot, component, quantity))
                    {
                        component.LastPurchasePrice = previousPurchasePrice;
                        return false;
                    }

                    if (cashToSpend > 0 && cashEntity != null)
                    {
                        var newCashSlotBalance = cashEntity.Value.Comp.Count - cashToSpend;
                        cashMutationAttempted = true;
                        cashEntity.Value.Comp.Lingering = true;
                        _stack.SetCount(cashEntity.Value.Owner, newCashSlotBalance, cashEntity.Value.Comp);
                        cashEntity.Value.Comp.Lingering = previousCashLingering ?? false;
                        component.CashSlotBalance = newCashSlotBalance;
                    }
                }
                catch (Exception exception)
                {
                    return ResolveVendFailure(exception);
                }

                foreach (var (account, taxCoeff) in component.TaxAccounts)
                {
                    if (!float.IsFinite(taxCoeff) || taxCoeff <= 0.0f)
                        continue;
                    var rawTax = (double) totalPrice * taxCoeff;
                    if (!double.IsFinite(rawTax) || rawTax <= 0.0 || rawTax > int.MaxValue)
                        continue;

                    try
                    {
                        var tax = checked((int) Math.Floor(rawTax));
                        if (tax > 0)
                            _bankSystem.TrySectorDeposit(account, tax, LedgerEntryType.VendorTax);
                    }
                    catch (Exception exception)
                    {
                        // Tax projection is ancillary after a successful vend.
                        Log.Error($"Could not record vending tax for {ToPrettyString(uid)}: {exception}");
                    }
                }

                try
                {
                    Dirty(uid, component);
                }
                catch (Exception exception)
                {
                    Log.Error($"Could not project committed vending state: {exception}");
                }

                try
                {
                    _adminLogger.Add(LogType.Action, LogImpact.Low,
                        $"{ToPrettyString(sender):user} bought from [vendingMachine:{ToPrettyString(uid!)}, product:{proto.Name}, quantity:{quantity}, unitCost:{unitPrice}, cost:{totalPrice}, with ${cashToSpend} cash and ${bankToSpend} bank.");
                }
                catch (Exception exception)
                {
                    Log.Error($"Could not write committed vending audit entry: {exception}");
                }

                if (cashMutationAttempted &&
                    cashEntity is { } consumedCash &&
                    consumedCash.Comp.Count <= 0 &&
                    !consumedCash.Comp.Lingering &&
                    !Deleted(consumedCash.Owner))
                {
                    QueueDel(consumedCash.Owner);
                }

                return true;
            }

            if (bankToSpend <= 0)
                return FinalizeVend();

            if (!allowBankAwait || !_bankVendsInFlight.Add(uid))
                return false;

            try
            {
                var committed = await _bankSystem.TryBankWithdrawAsync(
                    sender,
                    bankToSpend,
                    FinalizeVend);
                if (!committed)
                    Deny(uid, component);
                return committed;
            }
            finally
            {
                _bankVendsInFlight.Remove(uid);
            }
            // End Frontier
        }

        /// <summary>
        /// Tries to update the visuals of the component based on its current state.
        /// </summary>
        public void TryUpdateVisualState(EntityUid uid, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            var finalState = VendingMachineVisualState.Normal;
            if (vendComponent.Broken)
            {
                finalState = VendingMachineVisualState.Broken;
            }
            else if (vendComponent.Ejecting)
            {
                finalState = VendingMachineVisualState.Eject;
            }
            else if (vendComponent.Denying)
            {
                finalState = VendingMachineVisualState.Deny;
            }
            else if (!this.IsPowered(uid, EntityManager))
            {
                finalState = VendingMachineVisualState.Off;
            }

            if (_light.TryGetLight(uid, out var pointlight))
            {
                var lightState = finalState != VendingMachineVisualState.Broken && finalState != VendingMachineVisualState.Off;
                _light.SetEnabled(uid, lightState, pointlight);
            }

            _appearanceSystem.SetData(uid, VendingMachineVisuals.VisualState, finalState);
        }

        /// <summary>
        /// Ejects a random item from the available stock. Will do nothing if the vending machine is empty.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="throwItem">Whether to throw the item in a random direction after dispensing it.</param>
        /// <param name="forceEject">Whether to skip the regular ejection checks and immediately dispense the item without animation.</param>
        /// <param name="vendComponent"></param>
        public void EjectRandom(EntityUid uid, bool throwItem, bool forceEject = false, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            if (!this.IsPowered(uid, EntityManager))
                return;

            if (vendComponent.Ejecting)
                return;

            if (vendComponent.EjectRandomCounter < 1)
            {
                _audioSystem.PlayPvs(_audioSystem.GetSound(vendComponent.SoundDeny), uid);
                _popupSystem.PopupEntity(Loc.GetString("vending-machine-component-try-eject-access-abused"), uid, PopupType.MediumCaution);
                return;
            }

            var availableItems = GetAvailableInventory(uid, vendComponent);
            if (availableItems.Count <= 0)
                return;
            var item = _random.Pick(availableItems);

            if (forceEject)
            {
                vendComponent.NextItemToEject = item.ID;
                vendComponent.NextItemToEjectQuantity = 1;
                vendComponent.ThrowNextItem = throwItem;
                var entry = GetEntry(uid, item.ID, item.Type, vendComponent);
                if (entry != null && entry.Amount != uint.MaxValue)
                {
                    entry.Amount--;
                    Dirty(uid, vendComponent);
                }
                EjectItem(uid, vendComponent, forceEject);
            }
            else
                TryEjectVendorItem(uid, item.Type, item.ID, throwItem, vendComponent);

            //Mono: revise frontier vend protection, allow max vends below 2, add probability for extra freebies
            if (_random.Prob(vendComponent.EjectNoCountChance))
                return;

            if (vendComponent.EjectRandomCounter == vendComponent.EjectRandomMax)
                vendComponent.EjectNextChargeTime = _timing.CurTime + vendComponent.EjectRechargeDuration;
            //Mono end

            vendComponent.EjectRandomCounter -= 1;
        }

        public void AddCharges(EntityUid uid, int change, VendingMachineComponent? comp = null)
        {
            if (!Resolve(uid, ref comp, false))
                return;

            var old = comp.EjectRandomCounter;
            comp.EjectRandomCounter = Math.Clamp(comp.EjectRandomCounter + change, 0, comp.EjectRandomMax);
            if (comp.EjectRandomCounter != old)
                Dirty(uid, comp);
        }

        private void EjectItem(EntityUid uid, VendingMachineComponent? vendComponent = null, bool forceEject = false)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            // No need to update the visual state because we never changed it during a forced eject
            if (!forceEject)
                TryUpdateVisualState(uid, vendComponent);

            if (string.IsNullOrEmpty(vendComponent.NextItemToEject))
            {
                vendComponent.NextItemToEjectQuantity = 1;
                vendComponent.ThrowNextItem = false;
                return;
            }

            // Consume the reservation before materialization. Any exception after
            // Spawn must not leave a replayable NextItemToEject that can mint a
            // duplicate on a later animation update.
            var itemToEject = vendComponent.NextItemToEject!;
            var itemQuantity = vendComponent.NextItemToEjectQuantity;
            var throwNextItem = vendComponent.ThrowNextItem;
            var purchasePrice = !forceEject ? vendComponent.LastPurchasePrice : null;
            vendComponent.NextItemToEject = null;
            vendComponent.NextItemToEjectQuantity = 1;
            vendComponent.ThrowNextItem = false;
            if (!forceEject)
                vendComponent.LastPurchasePrice = null;

            if (itemQuantity is < 1 or > VendingMachineComponent.MaxPurchaseQuantity)
            {
                Log.Error($"Discarded invalid vending payout quantity {itemQuantity} for {itemToEject}.");
                return;
            }

            // Default spawn coordinates
            var spawnCoordinates = Transform(uid).Coordinates;

            //Make sure the wallvends spawn outside of the wall.

            if (TryComp<WallMountComponent>(uid, out var wallMountComponent))
            {

                var offset = wallMountComponent.Direction.ToWorldVec() * WallVendEjectDistanceFromWall;
                spawnCoordinates = spawnCoordinates.Offset(offset);
            }

            for (var i = 0; i < itemQuantity; i++)
            {
                EntityUid ent;
                try
                {
                    ent = Spawn(itemToEject, spawnCoordinates);
                }
                catch (Exception exception)
                {
                    Log.Error($"Could not materialize consumed vending payout {i + 1}/{itemQuantity} {itemToEject}: {exception}");
                    continue;
                }

                // Mono: Track completed purchases, including explicit zero-price vends,
                // but not random or forced ejects. Batch payouts use the unit price;
                // storing the total on every entity would multiply their resale cap.
                if (purchasePrice.HasValue)
                {
                    try
                    {
                        _vendingPurchase.MarkAsPurchased(ent, uid, purchasePrice.Value);
                    }
                    catch (Exception exception)
                    {
                        // A provenance-free vending payout could be resold above its
                        // purchase price. Delete it rather than leaking untracked value.
                        if (!Deleted(ent))
                            QueueDel(ent);
                        Log.Error($"Could not attach vending purchase provenance; discarded payout: {exception}");
                        continue;
                    }
                }

                if (!throwNextItem)
                    continue;

                try
                {
                    var range = vendComponent.NonLimitedEjectRange;
                    var direction = new Vector2(_random.NextFloat(-range, range), _random.NextFloat(-range, range));
                    _throwingSystem.TryThrow(ent, direction, vendComponent.NonLimitedEjectForce);
                }
                catch (Exception exception)
                {
                    // Throwing is a projection after the payout exists.
                    Log.Error($"Could not throw committed vending payout: {exception}");
                }
            }
        }

        private VendingMachineInventoryEntry? GetEntry(EntityUid uid, string entryId, InventoryType type, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return null;

            return type switch
            {
                InventoryType.Regular => component.Inventory.GetValueOrDefault(entryId),
                InventoryType.Emagged when _emag.CheckFlag(uid, EmagType.Interaction) =>
                    component.EmaggedInventory.GetValueOrDefault(entryId),
                InventoryType.Contraband when component.Contraband =>
                    component.ContrabandInventory.GetValueOrDefault(entryId),
                _ => null,
            };
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            var query = EntityQueryEnumerator<VendingMachineComponent>();
            while (query.MoveNext(out var uid, out var comp))
            {
                if (comp.Ejecting)
                {
                    comp.EjectAccumulator += frameTime;
                    if (comp.EjectAccumulator >= comp.EjectDelay)
                    {
                        comp.EjectAccumulator = 0f;
                        comp.Ejecting = false;

                        EjectItem(uid, comp);
                    }
                }

                if (comp.Denying)
                {
                    comp.DenyAccumulator += frameTime;
                    if (comp.DenyAccumulator >= comp.DenyDelay)
                    {
                        comp.DenyAccumulator = 0f;
                        comp.Denying = false;

                        TryUpdateVisualState(uid, comp);
                    }
                }

                if (comp.DispenseOnHitCoolingDown)
                {
                    comp.DispenseOnHitAccumulator += frameTime;
                    if (comp.DispenseOnHitAccumulator >= comp.DispenseOnHitCooldown)
                    {
                        comp.DispenseOnHitAccumulator = 0f;
                        comp.DispenseOnHitCoolingDown = false;
                    }
                }

                // Added block for charges
                if (comp.EjectRandomCounter == comp.EjectRandomMax || _timing.CurTime < comp.EjectNextChargeTime)
                    continue;

                AddCharges(uid, 1, comp);
                comp.EjectNextChargeTime = _timing.CurTime + comp.EjectRechargeDuration;
                // Added block for charges
            }
            var disabled = EntityQueryEnumerator<EmpDisabledComponent, VendingMachineComponent>();
            while (disabled.MoveNext(out var uid, out _, out var comp))
            {
                if (comp.NextEmpEject < _timing.CurTime)
                {
                    EjectRandom(uid, true, false, comp);
                    comp.NextEmpEject += TimeSpan.FromSeconds(5 * comp.EjectDelay);
                }
            }
        }

        public void TryRestockInventory(EntityUid uid, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            RestockInventoryFromPrototype(uid, vendComponent);

            Dirty(uid, vendComponent);
            TryUpdateVisualState(uid, vendComponent);
        }

        private void OnPriceCalculation(EntityUid uid, VendingMachineRestockComponent component, ref PriceCalculationEvent args)
        {
            args.Price = 0; // This area of the code make it so the cargoblacklist gets ignored, this change was to resolve it.
            return;

            List<double> priceSets = new();
            // Find the most expensive inventory and use that as the highest price.
            foreach (var vendingInventory in component.CanRestock)
            {
                double total = 0;
                if (PrototypeManager.TryIndex(vendingInventory, out VendingMachineInventoryPrototype? inventoryPrototype))
                {
                    foreach (var (item, amount) in inventoryPrototype.StartingInventory)
                    {
                        if (PrototypeManager.TryIndex(item, out EntityPrototype? entity))
                            total += _pricing.GetEstimatedPrice(entity) * amount;
                    }
                }
                priceSets.Add(total);
            }

            args.Price += priceSets.Max();
        }

        // Frontier: cash slot logic
        private void OnEntityInserted(Entity<VendingMachineComponent> ent, ref EntInsertedIntoContainerMessage args)
        {
            if (ent.Comp.CashSlotName != null
            && ent.Comp.CurrencyStackType != null
            && ItemSlots.TryGetSlot(ent, ent.Comp.CashSlotName, out var slot)
            && TryComp<StackComponent>(slot?.ContainerSlot?.ContainedEntity, out var stack)
            && stack.StackTypeId == ent.Comp.CurrencyStackType)
            {
                ent.Comp.CashSlotBalance = stack.Count;
            }
            else
            {
                ent.Comp.CashSlotBalance = 0;
            }
            Dirty(ent, ent.Comp);
        }

        private void OnEntityRemoved(Entity<VendingMachineComponent> ent, ref EntRemovedFromContainerMessage args)
        {
            ent.Comp.CashSlotBalance = 0;
            Dirty(ent, ent.Comp);
        }
        // End Frontier: cash slot logic
    }
}
