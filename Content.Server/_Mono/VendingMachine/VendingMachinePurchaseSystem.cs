using Content.Server.Cargo.Systems;
using Content.Shared._Mono.VendingMachine;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;

namespace Content.Server._Mono.VendingMachine;

/// <summary>
/// System that handles vending machine purchase tracking and pricing modifications.
/// </summary>
public sealed partial class VendingMachinePurchaseSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        // Note: We don't subscribe to PriceCalculationEvent to avoid interfering with other pricing
        // Instead, we provide specific methods for cargo systems to call when needed
    }

    /// <summary>
    /// Adds a VendingMachinePurchaseComponent to an entity when it's purchased from a vending machine.
    /// </summary>
    /// <param name="purchasedEntity">The entity that was purchased</param>
    /// <param name="vendingMachine">The vending machine it was purchased from</param>
    /// <param name="purchasePrice">The price paid for the entity</param>
    public void MarkAsPurchased(EntityUid purchasedEntity, EntityUid vendingMachine, double purchasePrice)
    {
        // Add the component to track this purchase
        var purchaseComponent = EnsureComp<VendingMachinePurchaseComponent>(purchasedEntity);
        purchaseComponent.PurchaseGrid = Transform(vendingMachine).GridUid ?? EntityUid.Invalid;
        purchaseComponent.OriginalPurchasePrice = double.IsFinite(purchasePrice)
            ? Math.Max(0d, purchasePrice)
            : 0d;

        Dirty(purchasedEntity, purchaseComponent);
    }



    /// <summary>
    /// Gets the discounted price for a vending machine purchase if applicable.
    /// This method is called specifically by cargo systems to get the modified price.
    /// </summary>
    /// <param name="entity">The entity to check</param>
    /// <param name="currentGrid">The grid where the entity is being sold</param>
    /// <returns>The modified price if applicable, null otherwise</returns>
    public double? GetVendingMachineDiscountPrice(EntityUid entity, EntityUid currentGrid)
    {
        if (!TryComp<VendingMachinePurchaseComponent>(entity, out var component))
            return null;

        if (!TryComp<StaticPriceComponent>(entity, out var staticPrice))
            return null;

        _ = currentGrid;
        var normalPrice = double.IsFinite(staticPrice.Price)
            ? Math.Max(0d, staticPrice.Price)
            : 0d;
        return Math.Min(normalPrice, CalculateResaleCap(component.OriginalPurchasePrice));
    }

    /// <summary>
    /// Returns the maximum total cargo resale value for a vending purchase.
    /// Provenance follows the item across grids and caps every source of appraised
    /// value, not just its StaticPrice component.
    /// </summary>
    public double? GetVendingMachineResaleCap(EntityUid entity)
    {
        return TryComp<VendingMachinePurchaseComponent>(entity, out var component)
            ? CalculateResaleCap(component.OriginalPurchasePrice)
            : null;
    }

    internal static double CalculateResaleCap(double originalPurchasePrice)
    {
        if (!double.IsFinite(originalPurchasePrice) || originalPurchasePrice <= 0d)
            return 0d;

        return originalPurchasePrice * 0.5d;
    }

    /// <summary>
    /// Checks if an entity was purchased from a vending machine on the specified grid.
    /// </summary>
    /// <param name="entity">The entity to check</param>
    /// <param name="gridUid">The grid to check against</param>
    /// <returns>True if the entity was purchased from a vending machine on the specified grid</returns>
    public bool WasPurchasedOnGrid(EntityUid entity, EntityUid gridUid)
    {
        if (!TryComp<VendingMachinePurchaseComponent>(entity, out var component))
            return false;

        return component.PurchaseGrid == gridUid;
    }
}
