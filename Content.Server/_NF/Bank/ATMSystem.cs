/*
 * New Frontiers - This file is licensed under AGPLv3
 * Copyright (c) 2024 New Frontiers Contributors
 * See AGPLv3.txt for details.
 */
using Content.Server._Mono.MonoCoins;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Bank.Events;
using Content.Shared.Coordinates;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared.Containers.ItemSlots;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Server.Administration.Logs;
using Content.Shared._Mono.CCVar; // Mono
using Content.Shared.Database;
using System.Threading.Tasks;
using Robust.Shared.Audio.Systems;
using Content.Shared._NF.Bank.BUI;
using Robust.Shared.Configuration; // Mono

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem
{
    [Dependency] private IConfigurationManager _cfg = default!; // Mono
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private StackSystem _stackSystem = default!;
    [Dependency] private UserInterfaceSystem _uiSystem = default!;
    [Dependency] private SharedContainerSystem _containerSystem = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;

    private readonly object _atmDepositLock = new();
    private readonly HashSet<EntityUid> _atmDepositsInFlight = new();

    private void InitializeATM()
    {
        SubscribeLocalEvent<BankATMComponent, BankWithdrawMessage>(OnWithdraw);
        SubscribeLocalEvent<BankATMComponent, BankDepositMessage>(OnDeposit);
        SubscribeLocalEvent<BankATMComponent, BoundUIOpenedEvent>(OnATMUIOpen);
        SubscribeLocalEvent<BankATMComponent, EntInsertedIntoContainerMessage>(OnCashSlotChanged);
        SubscribeLocalEvent<BankATMComponent, EntRemovedFromContainerMessage>(OnCashSlotChanged);
    }

    private void OnWithdraw(EntityUid uid, BankATMComponent component, BankWithdrawMessage args)
    {
        _ = ObserveWithdrawAsync(uid, component, args);
    }

    private async Task ObserveWithdrawAsync(EntityUid uid, BankATMComponent component, BankWithdrawMessage args)
    {
        try
        {
            await OnWithdrawAsync(uid, component, args);
        }
        catch (Exception exception)
        {
            _log.Error($"Unhandled durable ATM withdrawal failure: {exception}");
            try
            {
                if (args.Actor is { Valid: true } actor &&
                    Exists(actor) &&
                    TryComp<BankATMComponent>(uid, out var currentComponent))
                {
                    ConsolePopup(actor, Loc.GetString("bank-atm-menu-transaction-denied"));
                    PlayDenySound(uid, currentComponent);
                }
            }
            catch (Exception reportException)
            {
                _log.Error($"Could not report durable ATM withdrawal failure: {reportException}");
            }
        }
    }

    private async Task OnWithdrawAsync(EntityUid uid, BankATMComponent component, BankWithdrawMessage args)
    {
        if (args.Actor is not { Valid : true } player)
            return;

        // to keep the window stateful
        GetInsertedCashAmount(component, out var deposit);

        var state = new BankATMMenuInterfaceState(0, 0, false, deposit);

        // check for a bank account
        if (!TryComp<BankAccountComponent>(player, out var bank))
        {
            _log.Info($"{player} has no bank account");
            ConsolePopup(player, Loc.GetString("bank-atm-menu-no-bank"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        state.Balance = bank.Balance;
        state.Savings = GetEntSavings(player);

        // check for sufficient funds
        if (bank.Balance < args.Amount)
        {
            ConsolePopup(args.Actor, Loc.GetString("bank-insufficient-funds"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        state.Enabled = true;

        // Resolve everything needed to deliver the cash before committing the
        // debit. After the await, no configuration lookup may turn a committed
        // withdrawal into a failed payout.
        var stackPrototype = _prototypeManager.Index<StackPrototype>(component.CashType);
        var payoutCoordinates = uid.ToCoordinates();

        // Validation and persistence happen in the banking system. A successful
        // result means the debit is durable and its profile lease has been released.
        if (!await TryBankWithdrawAsync(
                player,
                args.Amount,
                finalizeAfterCommit: () =>
                {
                    _stackSystem.Spawn(args.Amount, stackPrototype, payoutCoordinates);
                    return true;
                }))
        {
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-transaction-denied"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        state.Balance = bank.Balance;

        ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-withdraw-successful"));
        PlayConfirmSound(uid, component);
        _adminLogger.Add(LogType.ATMUsage, LogImpact.Low, $"{ToPrettyString(player):actor} withdrew {args.Amount} from {ToPrettyString(component.Owner)}");

        _uiSystem.SetUiState(uid, args.UiKey, state);
    }

    private void OnDeposit(EntityUid uid, BankATMComponent component, BankDepositMessage args)
    {
        _ = ObserveDepositAsync(uid, component, args);
    }

    private async Task ObserveDepositAsync(EntityUid uid, BankATMComponent component, BankDepositMessage args)
    {
        try
        {
            await OnDepositAsync(uid, component, args);
        }
        catch (Exception exception)
        {
            _log.Error($"Unhandled durable ATM deposit failure: {exception}");
            try
            {
                if (args.Actor is { Valid: true } actor &&
                    Exists(actor) &&
                    TryComp<BankATMComponent>(uid, out var currentComponent))
                {
                    ConsolePopup(actor, Loc.GetString("bank-atm-menu-transaction-denied"));
                    PlayDenySound(uid, currentComponent);
                }
            }
            catch (Exception reportException)
            {
                _log.Error($"Could not report durable ATM deposit failure: {reportException}");
            }
        }
    }

    private async Task OnDepositAsync(EntityUid uid, BankATMComponent component, BankDepositMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        // gets the money inside a cashslot of an ATM.
        // Dynamically knows what kind of cash to look for according to BankATMComponent
        GetInsertedCashAmount(component, out var deposit);

        var state = new BankATMMenuInterfaceState(0, 0, false, deposit);

        // make sure the user actually has a bank
        if (!TryComp<BankAccountComponent>(player, out var bank))
        {
            _log.Info($"{player} has no bank account");
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-no-bank"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        state.Balance = bank.Balance;
        if (_playerManager.TryGetSessionByEntity(player, out var session))
            state.Savings = _coins.GetMonoCoinsBalance(session.UserId) ?? 0;

        // validating the cash slot was setup correctly in the yaml
        if (component.CashSlot.ContainerSlot is not BaseContainer cashSlot)
        {
            _log.Info($"ATM has no cash slot");
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-no-bank"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        // validate stack prototypes
        if (!TryComp<StackComponent>(component.CashSlot.ContainerSlot.ContainedEntity, out var stackComponent) ||
            stackComponent.StackTypeId == null)
        {
            _log.Info($"ATM cash slot contains bad stack prototype");
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-wrong-cash"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        // and then check them against the ATM's CashType
        if (_prototypeManager.Index<StackPrototype>(component.CashType) != _prototypeManager.Index<StackPrototype>(stackComponent.StackTypeId))
        {
            _log.Info($"{stackComponent.StackTypeId} is not {component.CashType}");
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-wrong-cash"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        if (component.CashSlot.ContainerSlot?.ContainedEntity is not { Valid: true } cashEntity)
        {
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-transaction-denied"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        var originalDeposit = deposit;
        var taxes = new List<(SectorBankAccount Account, int Amount)>();
        long netDeposit = originalDeposit;
        foreach (var (account, taxCoeff) in component.TaxAccounts)
        {
            if (!float.IsFinite(taxCoeff) || taxCoeff <= 0.0f)
                continue;

            var tax = (int)Math.Floor(originalDeposit * taxCoeff);
            if (tax <= 0)
                continue;

            taxes.Add((account, tax));
            netDeposit -= tax;
        }

        if (netDeposit <= 0 || netDeposit > int.MaxValue)
        {
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-transaction-denied"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        bool ownsDeposit;
        lock (_atmDepositLock)
            ownsDeposit = _atmDepositsInFlight.Add(uid);

        if (!ownsDeposit)
        {
            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-transaction-denied"));
            PlayDenySound(uid, component);
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        var slotWasLocked = component.CashSlot.Locked;
        var releaseAtmReservation = true;
        try
        {
            _itemSlots.SetLock(uid, component.CashSlot, true);

            // Re-read after owning the ATM reservation. Two different players may
            // have submitted the same UI message before either one reached an await.
            GetInsertedCashAmount(component, out var reservedDeposit);
            if (!ReferenceEquals(component.CashSlot.ContainerSlot, cashSlot) ||
                component.CashSlot.ContainerSlot?.ContainedEntity != cashEntity ||
                reservedDeposit != originalDeposit ||
                !TryComp<StackComponent>(cashEntity, out var reservedStack) ||
                reservedStack.Count != originalDeposit ||
                reservedStack.StackTypeId != component.CashType)
            {
                ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-transaction-denied"));
                PlayDenySound(uid, component);
                _uiSystem.SetUiState(uid, args.UiKey, state);
                return;
            }

            state.Enabled = true;
            var committed = await TryBankDepositAsync(
                player,
                (int) netDeposit,
                tax: true,
                finalizeAfterCommit: () =>
                {
                    // The cash slot is locked, but verify the exact reserved stack
                    // again after the database await before consuming anything.
                    if (!ReferenceEquals(component.CashSlot.ContainerSlot, cashSlot) ||
                        component.CashSlot.ContainerSlot?.ContainedEntity != cashEntity ||
                        !TryComp<StackComponent>(cashEntity, out var committedStack) ||
                        committedStack.Count != originalDeposit ||
                        committedStack.StackTypeId != component.CashType)
                    {
                        return false;
                    }

                    // Cash consumption and tax credits happen only after the bank
                    // commit. Once cash consumption starts, later accounting errors
                    // are logged but cannot turn the committed exchange into a retry.
                    try
                    {
                        _containerSystem.CleanContainer(cashSlot);
                    }
                    catch (Exception exception)
                    {
                        _log.Error($"ATM could not clean committed cash stack {cashEntity}: {exception}");
                        try
                        {
                            if (Exists(cashEntity))
                                QueueDel(cashEntity);
                        }
                        catch (Exception fallbackException)
                        {
                            _log.Error($"CRITICAL: ATM could not consume committed cash stack {cashEntity}: {fallbackException}");
                            return false;
                        }
                    }

                    foreach (var (account, tax) in taxes)
                    {
                        try
                        {
                            if (!TrySectorDeposit(account, tax, LedgerEntryType.AtmTax))
                                _log.Error($"ATM tax deposit failed for {account}: {tax}");
                        }
                        catch (Exception exception)
                        {
                            _log.Error($"ATM tax deposit threw for {account}: {exception}");
                        }
                    }

                    return true;
                });

            if (!committed)
            {
                ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-transaction-denied"));
                PlayDenySound(uid, component);
                _uiSystem.SetUiState(uid, args.UiKey, state);
                return;
            }

            ConsolePopup(args.Actor, Loc.GetString("bank-atm-menu-deposit-successful"));
            PlayConfirmSound(uid, component);
            _adminLogger.Add(LogType.ATMUsage, LogImpact.Low, $"{ToPrettyString(player):actor} deposited {netDeposit} into {ToPrettyString(component.Owner)}");

            state.Deposit = 0;
            state.Balance = bank.Balance;
            if (session != null)
                state.Savings = _coins.GetMonoCoinsBalance(session.UserId) ?? 0;

            _uiSystem.SetUiState(uid, args.UiKey, state);
        }
        catch (BankMutationRollbackException)
        {
            // The money outcome cannot safely be retried. Keep both the ATM guard
            // and its physical cash slot locked until operator recovery/restart.
            releaseAtmReservation = false;
            throw;
        }
        finally
        {
            if (releaseAtmReservation)
            {
                try
                {
                    if (Exists(uid))
                        _itemSlots.SetLock(uid, component.CashSlot, slotWasLocked);
                }
                catch (Exception exception)
                {
                    _log.Error($"Could not restore ATM cash-slot lock for {uid}: {exception}");
                }

                lock (_atmDepositLock)
                    _atmDepositsInFlight.Remove(uid);
            }
        }
    }

    private void OnCashSlotChanged(EntityUid uid, BankATMComponent component, ContainerModifiedMessage args)
    {
        if (!TryComp<ActivatableUIComponent>(uid, out var uiComp) || uiComp.Key is null)
            return;

        var uiUsers = _uiSystem.GetActors(uid, uiComp.Key);
        GetInsertedCashAmount(component, out var deposit);

        foreach (var user in uiUsers)
        {
            if (user is not { Valid: true } player)
                continue;

            if (!TryComp<BankAccountComponent>(player, out var bank))
                continue;

            var state = new BankATMMenuInterfaceState(bank.Balance, 0, true, deposit);
            state.Savings = GetEntSavings(player);

            if (component.CashSlot.ContainerSlot?.ContainedEntity is not { Valid : true } cash)
                state.Deposit = 0;
            else
                state.Deposit = deposit;

            _uiSystem.SetUiState(uid, uiComp.Key, state);
        }
    }

    private void OnATMUIOpen(EntityUid uid, BankATMComponent component, BoundUIOpenedEvent args)
    {
        var player = args.Actor;

        if (player == null)
            return;

        GetInsertedCashAmount(component, out var deposit);

        var state = new BankATMMenuInterfaceState(0, 0, false, deposit);

        if (!TryComp<BankAccountComponent>(player, out var bank))
        {
            _log.Info($"{player} has no bank account");
            _uiSystem.SetUiState(uid, args.UiKey, state);
            return;
        }

        state.Balance = bank.Balance;
        state.Savings = GetEntSavings(player);

        state.Enabled = true;
        _uiSystem.SetUiState(uid, args.UiKey, state);
    }

    private void GetInsertedCashAmount(BankATMComponent component, out int amount)
    {
        amount = 0;
        var cashEntity = component.CashSlot.ContainerSlot?.ContainedEntity;
        // Nothing inserted: amount should be 0.
        if (cashEntity is null)
            return;

        // Invalid item inserted (doubloons, FMC, telecrystals...): amount should be negative (to denote an error)
        if (!TryComp<StackComponent>(cashEntity, out var cashStack) ||
            cashStack.StackTypeId != component.CashType)
        {
            amount = -1;
            return;
        }

        // Valid amount: output the stack's value.
        amount = cashStack.Count;
        return;
    }

    private void PlayDenySound(EntityUid uid, BankATMComponent component)
    {
        _audio.PlayPvs(_audio.GetSound(component.ErrorSound), uid);
    }

    private void PlayConfirmSound(EntityUid uid, BankATMComponent component)
    {
        _audio.PlayPvs(_audio.GetSound(component.ConfirmSound), uid);
    }

    private void ConsolePopup(EntityUid actor, string text)
    {
        if (actor is { Valid: true } player)
            _popup.PopupEntity(text, player);
    }

    private long GetEntSavings(EntityUid uid)
    {
        if (_playerManager.TryGetSessionByEntity(uid, out var session))
            return _coins.GetMonoCoinsBalance(session.UserId) ?? 0;
        return 0;
    }
}
