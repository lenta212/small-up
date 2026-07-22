using Content.Client.UserInterface.Controls;
using Content.Client.VendingMachines.UI;
using Content.Shared.VendingMachines;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using System.Linq;
using Robust.Client.GameObjects;
using Content.Shared._NF.Bank.Components; // Frontier

namespace Content.Client.VendingMachines
{
    public sealed class VendingMachineBoundUserInterface : BoundUserInterface
    {
        [ViewVariables]
        private VendingMachineMenu? _menu;

        [ViewVariables]
        private List<VendingMachineInventoryEntry> _cachedInventory = new();

        // Frontier: balance
        private UserInterfaceSystem _uiSystem = default!;

        [ViewVariables]
        private int _balance = 0;
        [ViewVariables]
        private int _cashSlotBalance = 0;
        // End Frontier
        [ViewVariables]
        private bool _requiresCash; // mono
        private IReadOnlyDictionary<string, int> _unitPrices = new Dictionary<string, int>();
        private bool _hasPriceState;

        public VendingMachineBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
        {
        }

        protected override void Open()
        {
            base.Open();

            // Frontier: state and balance status
            _uiSystem = EntMan.System<UserInterfaceSystem>();
            // End Frontier

            _menu = this.CreateWindowCenteredLeft<VendingMachineMenu>();
            // Frontier: no exceptions
            if (EntMan.TryGetComponent(Owner, out MetaDataComponent? meta))
                _menu.Title = meta.EntityName;
            else
                _menu.Title = Loc.GetString("vending-machine-nf-fallback-title");
            // End Frontier: no exceptions
            _menu.OnItemSelected += OnItemSelected;
            Refresh();
        }

        public void Refresh()
        {
            var system = EntMan.System<VendingMachineSystem>();
            _cachedInventory = system.GetAllInventory(Owner);

            // Frontier: state and balance status
            var uiUsers = _uiSystem.GetActors(Owner, UiKey);
            foreach (var uiUser in uiUsers)
            {
                if (EntMan.TryGetComponent<BankAccountComponent>(uiUser, out var bank))
                    _balance = bank.Balance;
            }
            int? cashSlotValue = null;
            if (EntMan.TryGetComponent<VendingMachineComponent>(Owner, out var vendingMachine))
            {
                _cashSlotBalance = vendingMachine.CashSlotBalance;
                if (!_hasPriceState)
                    _requiresCash = vendingMachine.RequiresCash; // mono
                if (vendingMachine.CashSlotName != null)
                    cashSlotValue = _cashSlotBalance;
            }
            else
            {
                _cashSlotBalance = 0;
            }
            // End Frontier

            _menu?.Populate(_cachedInventory, _unitPrices, _balance, cashSlotValue, _requiresCash); // Frontier: add _balance, mono: add _requiresCash
        }

        protected override void UpdateState(BoundUserInterfaceState state)
        {
            base.UpdateState(state);

            if (state is not VendingMachineBoundUserInterfaceState vendingState)
                return;

            _unitPrices = new Dictionary<string, int>(vendingState.UnitPrices);
            _requiresCash = vendingState.RequiresCash;
            _hasPriceState = true;
            if (_menu != null)
                Refresh();
        }

        private void OnItemSelected(GUIBoundKeyEventArgs args, ListData data)
        {
            if (args.Function != EngineKeyFunctions.UIClick)
                return;

            if (data is not VendorItemsListData
                {
                    ItemIndex: var itemIndex,
                    PurchaseQuantity: var purchaseQuantity,
                    TotalPrice: var totalPrice,
                    CanPurchase: true,
                })
                return;

            if (_cachedInventory.Count == 0)
                return;

            var selectedItem = _cachedInventory.ElementAtOrDefault(itemIndex);

            if (selectedItem == null)
                return;

            SendMessage(new VendingMachineEjectMessage(
                selectedItem.Type,
                selectedItem.ID,
                purchaseQuantity,
                totalPrice));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing)
                return;

            if (_menu == null)
                return;

            _menu.OnItemSelected -= OnItemSelected;
            _menu.OnClose -= Close;
            _menu.Dispose();
        }
    }
}
