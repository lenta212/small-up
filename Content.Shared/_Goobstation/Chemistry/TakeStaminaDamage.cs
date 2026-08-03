// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Systems;
using Content.Shared.EntityEffects;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.Chemistry;

public sealed partial class TakeStaminaDamage : EntityEffect
{
    /// <summary>
    /// How much stamina damage to take.
    /// </summary>
    [DataField]
    public int Amount = 10;

    /// <summary>
    /// Whether stamina damage should be applied immediately
    /// </summary>
    [DataField]
    public bool Immediate;

    public override void Effect(EntityEffectBaseArgs args)
    {
        args.EntityManager.System<StaminaSystem>().TakeStaminaDamage(args.TargetEntity, Amount, visual: false, immediate: Immediate);
    }

    [UsedImplicitly]
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => Loc.GetString("reagent-effect-guidebook-deal-stamina-damage",
            ("immediate", Immediate),
            ("amount", MathF.Abs(Amount)),
            ("chance", Probability),
            ("deltasign", MathF.Sign(Amount)));
}
