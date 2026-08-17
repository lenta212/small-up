using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.EffectConditions;

/// <summary>
/// Compatibility alias used by imported Goobstation xenobiology reagents.
/// Equivalent to <see cref="ReagentThreshold"/> with Goob field names.
/// </summary>
public sealed partial class ReagentCondition : EntityEffectCondition
{
    [DataField]
    public string? Reagent;

    [DataField]
    public FixedPoint2 Min = FixedPoint2.Zero;

    [DataField]
    public FixedPoint2 Max = FixedPoint2.MaxValue;

    public override bool Condition(EntityEffectBaseArgs args)
    {
        if (args is not EntityEffectReagentArgs reagentArgs)
            return true;

        var reagent = Reagent ?? reagentArgs.Reagent?.ID;
        if (reagent == null)
            return true;

        var quantity = FixedPoint2.Zero;
        if (reagentArgs.Source != null)
            quantity = reagentArgs.Source.GetTotalPrototypeQuantity(reagent);

        return quantity >= Min && quantity <= Max;
    }

    public override string GuidebookExplanation(IPrototypeManager prototype)
    {
        ReagentPrototype? reagentProto = null;
        if (Reagent is not null)
            prototype.TryIndex(Reagent, out reagentProto);

        return Loc.GetString("reagent-effect-condition-guidebook-reagent-threshold",
            ("reagent", reagentProto?.LocalizedName ?? Loc.GetString("reagent-effect-condition-guidebook-this-reagent")),
            ("max", Max == FixedPoint2.MaxValue ? (float) int.MaxValue : Max.Float()),
            ("min", Min.Float()));
    }
}
