using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.EffectConditions;

/// <summary>
/// Compatibility condition for imported Goobstation xenobiology reagents.
/// Local metabolizer typing differs in this branch; keep prototype loading quiet.
/// </summary>
public sealed partial class MetabolizerTypeCondition : EntityEffectCondition
{
    [DataField]
    public string? MetabolizerType;

    [DataField]
    public bool ShouldHave = true;

    [DataField]
    public List<string> MetabolizerTypes = new();

    public override bool Condition(EntityEffectBaseArgs args) => true;

    public override string GuidebookExplanation(IPrototypeManager prototype) => string.Empty;
}
