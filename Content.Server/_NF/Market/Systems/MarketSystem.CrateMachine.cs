using Content.Server._NF.CrateMachine;
using Content.Server._NF.Market.Components;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NF.Market.Extensions;
using Content.Shared._NF.Market;
using Content.Shared._NF.Market.Components;
using Content.Shared._NF.Market.Events;
using Content.Shared._NF.Bank.Components;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Content.Shared._NF.CrateMachine.Components;

namespace Content.Server._NF.Market.Systems;

public sealed partial class MarketSystem
{
    [Dependency] private CrateMachineSystem _crateMachine = default!;
    private readonly HashSet<EntityUid> _cratePurchasesInFlight = new();
    private readonly HashSet<EntityUid> _marketConsolePurchasesInFlight = new();

    private void InitializeCrateMachine()
    {
        SubscribeLocalEvent<MarketConsoleComponent, MarketPurchaseMessage>(OnMarketConsolePurchaseCrateMessage);
        SubscribeLocalEvent<CrateMachineComponent, CrateMachineOpenedEvent>(OnCrateMachineOpened);
    }

    private void OnMarketConsolePurchaseCrateMessage(EntityUid consoleUid,
        MarketConsoleComponent component,
        ref MarketPurchaseMessage args)
    {
        _ = ObserveMarketConsolePurchaseCrateAsync(consoleUid, component, args);
    }

    private async Task ObserveMarketConsolePurchaseCrateAsync(
        EntityUid consoleUid,
        MarketConsoleComponent component,
        MarketPurchaseMessage args)
    {
        try
        {
            await OnMarketConsolePurchaseCrateAsync(consoleUid, component, args);
        }
        catch (Exception exception)
        {
            Log.Error($"Durable market crate purchase failed: {exception}");
        }
    }

    private async Task OnMarketConsolePurchaseCrateAsync(
        EntityUid consoleUid,
        MarketConsoleComponent component,
        MarketPurchaseMessage args)
    {
        var marketMod = 1f;
        if (TryComp<MarketModifierComponent>(consoleUid, out var marketModComponent))
        {
            marketMod = marketModComponent.Mod;
        }

        if (!_crateMachine.FindNearestUnoccupied(consoleUid, component.MaxCrateMachineDistance, out var machineUid) || !_entityManager.TryGetComponent<CrateMachineComponent> (machineUid, out var comp))
        {
            _popup.PopupEntity(Loc.GetString("market-no-crate-machine-available"), consoleUid, Filter.PvsExcept(consoleUid), true);
            _audio.PlayPredicted(component.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));

            return;
        }
        await OnPurchaseCrateMessageAsync(machineUid.Value, consoleUid, comp, component, marketMod, args);
    }

    private async Task OnPurchaseCrateMessageAsync(EntityUid crateMachineUid,
        EntityUid consoleUid,
        CrateMachineComponent component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        MarketPurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (!TryComp<BankAccountComponent>(player, out var bankAccount))
            return;

        await TrySpawnCrateAsync(crateMachineUid, player, consoleUid, component, consoleComponent, marketMod, bankAccount);
    }

    private async Task TrySpawnCrateAsync(EntityUid crateMachineUid,
        EntityUid player,
        EntityUid consoleUid,
        CrateMachineComponent component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        BankAccountComponent playerBank)
    {
        if (!TryComp<MarketItemSpawnerComponent>(crateMachineUid, out _))
            return;

        if (!_marketConsolePurchasesInFlight.Add(consoleUid))
            return;

        if (!_cratePurchasesInFlight.Add(crateMachineUid))
        {
            _marketConsolePurchasesInFlight.Remove(consoleUid);
            return;
        }

        try
        {
            var liveCart = consoleComponent.CartDataList;
            var purchasedItems = CloneMarketDataList(liveCart);
            var transactionCost = consoleComponent.TransactionCost;
            var purchasedEntityCount = CalculateEntityAmount(purchasedItems);
            if (purchasedEntityCount is <= 0 or > 30 ||
                !TryCalculateMarketCost(purchasedItems, marketMod, transactionCost, out var spawnCost))
            {
                _popup.PopupEntity(Loc.GetString("market-insufficient-funds"), consoleUid, player);
                _audio.PlayPredicted(consoleComponent.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));
                return;
            }

            if (playerBank.Balance < spawnCost)
                return;

            var committed = await _bankSystem.TryBankWithdrawAsync(
                player,
                spawnCost,
                finalizeAfterCommit: () =>
                {
                    if (Deleted(crateMachineUid) ||
                        Deleted(consoleUid) ||
                        !TryComp<CrateMachineComponent>(crateMachineUid, out var currentCrateMachine) ||
                        !TryComp<MarketItemSpawnerComponent>(crateMachineUid, out var currentSpawner) ||
                        !TryComp<MarketConsoleComponent>(consoleUid, out var currentConsole) ||
                        !ReferenceEquals(currentConsole, consoleComponent) ||
                        !ReferenceEquals(currentConsole.CartDataList, liveCart) ||
                        currentConsole.TransactionCost != transactionCost ||
                        GetCurrentMarketModifier(consoleUid) != marketMod ||
                        !MarketDataListsEqual(currentConsole.CartDataList, purchasedItems) ||
                        CalculateEntityAmount(currentConsole.CartDataList) != purchasedEntityCount ||
                        currentSpawner.ItemsToSpawn.Count != 0 ||
                        _crateMachine.IsOccupied(crateMachineUid, currentCrateMachine))
                    {
                        return false;
                    }

                    var previousPayload = currentSpawner.ItemsToSpawn;
                    var previousCart = currentConsole.CartDataList;
                    var previousOpeningTime = currentCrateMachine.OpeningTimeRemaining;
                    try
                    {
                        currentSpawner.ItemsToSpawn = purchasedItems;
                        currentConsole.CartDataList = [];
                        _crateMachine.OpenFor(crateMachineUid, currentCrateMachine);
                        return true;
                    }
                    catch (Exception exception)
                    {
                        // The bank compensates only after the entire world
                        // reservation has been restored. OpenFor mutates its timer
                        // before touching appearance, so all three fields are part
                        // of the transaction snapshot.
                        currentSpawner.ItemsToSpawn = previousPayload;
                        currentConsole.CartDataList = previousCart;
                        try
                        {
                            _crateMachine.RestoreOpeningTime(
                                crateMachineUid,
                                currentCrateMachine,
                                previousOpeningTime);
                        }
                        catch (Exception rollbackException)
                        {
                            Log.Error($"Could not refresh rolled-back crate machine visuals: {rollbackException}");
                        }

                        Log.Error($"Market crate reservation was rolled back before bank compensation: {exception}");
                        return false;
                    }
                });
            if (!committed)
            {
                _popup.PopupEntity(Loc.GetString("market-insufficient-funds"), consoleUid, player);
                _audio.PlayPredicted(consoleComponent.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));
                return;
            }

            try
            {
                _audio.PlayPredicted(
                    consoleComponent.SuccessSound,
                    consoleUid,
                    null,
                    AudioParams.Default.WithMaxDistance(5f));
            }
            catch (Exception exception)
            {
                // The purchase and its reservation are already committed. Audio
                // delivery is only a projection and must never make the caller
                // report a durable purchase as failed.
                Log.Error($"Could not play committed market purchase sound: {exception}");
            }
        }
        finally
        {
            _marketConsolePurchasesInFlight.Remove(consoleUid);
            _cratePurchasesInFlight.Remove(crateMachineUid);
        }
    }

    private float GetCurrentMarketModifier(EntityUid consoleUid)
    {
        return TryComp<MarketModifierComponent>(consoleUid, out var modifier)
            ? modifier.Mod
            : 1f;
    }

    internal static bool TryCalculateMarketCost(
        IReadOnlyCollection<MarketData> items,
        float marketModifier,
        int transactionCost,
        out int cost)
    {
        cost = 0;
        if (items.Count == 0 ||
            !float.IsFinite(marketModifier) ||
            marketModifier <= 0f ||
            transactionCost < 0)
        {
            return false;
        }

        long total = transactionCost;
        foreach (var item in items)
        {
            if (item.Quantity <= 0 || !double.IsFinite(item.Price) || item.Price <= 0d)
                return false;

            var lineValue = Math.Round(item.Price * item.Quantity * marketModifier);
            if (!double.IsFinite(lineValue) || lineValue <= 0d || lineValue > int.MaxValue)
                return false;

            total += (long) lineValue;
            if (total > int.MaxValue)
                return false;
        }

        if (total <= 0)
            return false;

        cost = (int) total;
        return true;
    }

    private static List<MarketData> CloneMarketDataList(IEnumerable<MarketData> source)
    {
        return source
            .Select(data => new MarketData(
                data.Prototype,
                data.StackPrototype,
                data.Quantity,
                data.Price))
            .ToList();
    }

    private static bool MarketDataListsEqual(
        IReadOnlyList<MarketData> current,
        IReadOnlyList<MarketData> expected)
    {
        if (current.Count != expected.Count)
            return false;

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = expected[index];
            if (left.Prototype != right.Prototype ||
                left.StackPrototype != right.StackPrototype ||
                left.Quantity != right.Quantity ||
                !left.Price.Equals(right.Price))
            {
                return false;
            }
        }

        return true;
    }

    private void SpawnCrateItems(
        List<MarketData> spawnList,
        EntityUid targetCrate,
        ICollection<EntityUid> spawnedEntities)
    {
        var coordinates = Transform(targetCrate).Coordinates;
        foreach (var data in spawnList)
        {
            if (data.StackPrototype != null && _prototypeManager.TryIndex(data.StackPrototype, out var stackPrototype))
            {
                var entityList = _stackSystem.SpawnMultiple(stackPrototype.Spawn, data.Quantity, coordinates);
                foreach (var entity in entityList)
                {
                    spawnedEntities.Add(entity);
                    _crateMachine.InsertIntoCrate(entity, targetCrate);
                }
            }
            else
            {
                // Spawn the requested quantity of non-stackable items
                for (int i = 0; i < data.Quantity; i++)
                {
                    var spawn = Spawn(data.Prototype, coordinates);
                    spawnedEntities.Add(spawn);
                    _crateMachine.InsertIntoCrate(spawn, targetCrate);
                }
            }
        }
    }

    private void OnCrateMachineOpened(EntityUid uid, CrateMachineComponent component, CrateMachineOpenedEvent args)
    {
        if (!TryComp<MarketItemSpawnerComponent>(uid, out var itemSpawner))
            return;

        // Consume first. If any spawn fails, the payload cannot be replayed by a
        // later animation event to duplicate the purchase.
        var spawnList = itemSpawner.ItemsToSpawn;
        itemSpawner.ItemsToSpawn = [];
        if (spawnList.Count == 0)
            return;

        EntityUid? targetCrate = null;
        var spawnedEntities = new List<EntityUid>();
        try
        {
            targetCrate = _crateMachine.SpawnCrate(uid, component);
            SpawnCrateItems(spawnList, targetCrate.Value, spawnedEntities);
        }
        catch (Exception exception)
        {
            foreach (var spawned in spawnedEntities)
            {
                if (!Deleted(spawned))
                    QueueDel(spawned);
            }

            if (targetCrate is { } crate && !Deleted(crate))
                QueueDel(crate);

            Log.Error($"Market crate materialization failed after consuming its payload: {exception}");
        }
    }
}
