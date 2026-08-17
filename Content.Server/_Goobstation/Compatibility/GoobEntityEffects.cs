using Content.Shared.EntityEffects;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.Effects;

public sealed partial class ModifySlimeComponent : EntityEffect
{
    [DataField] public int ExtractBonus;
    [DataField] public float ChanceModifier;
    [DataField] public float PotencyModifier;
    [DataField] public float GrowthModifier;

    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class ChangeFactionEntityEffect : EntityEffect
{
    [DataField] public string? Faction;

    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class SexChange : EntityEffect
{
    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class ModifyStatusEffect : EntityEffect
{
    [DataField] public string? EffectProto;
    [DataField] public float Time;
    [DataField] public string Type = "Add";

    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class MakeUnreactiveEntityEffect : EntityEffect
{
    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class WashCreamPie : EntityEffect
{
    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class RandomSpeciesChange : EntityEffect
{
    [DataField]
    public List<string> Species = new();

    [DataField]
    public string? Prototype;

    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class PlaySoundEffect : EntityEffect
{
    [DataField]
    public SoundSpecifier? Sound;

    public override void Effect(EntityEffectBaseArgs args) { }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}


public sealed partial class SpawnEntity : EntityEffect
{
    [DataField] public string? Entity;
    [DataField] public int Number = 1;
    [DataField] public bool ShouldScale = true;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class CreateRQuantityEntityReactionEffect : EntityEffect
{
    [DataField] public string? Entity;
    [DataField] public int MaxEntities = 1;
    [DataField] public int MinEntities = 1;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class DoSmokeEntityEffect : EntityEffect
{
    [DataField] public float Duration;
    [DataField] public int SpreadAmount;
    [DataField] public string? SmokePrototype;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class ExtinguishNearby : EntityEffect
{
    [DataField] public float Range = 3f;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class Explosion : EntityEffect
{
    [DataField] public string? ExplosionType;
    [DataField] public float MaxIntensity;
    [DataField] public float IntensitySlope;
    [DataField] public float IntensityPerUnit;
    [DataField] public float MaxTotalIntensity;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class MutateNearbyPlantsEntityEffect : EntityEffect
{
    [DataField] public float Range = 3f;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class ForceStealthNearbyEffect : EntityEffect
{
    [DataField] public float Radius = 3f;
    [DataField] public float Time;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class IgniteNearbyEffect : EntityEffect
{
    [DataField] public float Radius = 3f;
    [DataField] public float FireStacks;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class Flash : EntityEffect
{
    [DataField] public float MaxRange;
    [DataField] public float RangePerUnit;
    [DataField] public SoundSpecifier? Sound;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}


public sealed partial class RandomTeleportNearby : EntityEffect
{
    [DataField] public float Radius = 3f;
    [DataField] public float Range = 3f;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class ScrambleNearbyEffect : EntityEffect
{
    [DataField] public float Radius = 3f;
    [DataField] public float Range = 3f;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class ChangeFactionNearbyEffect : EntityEffect
{
    [DataField] public float Radius = 3f;
    [DataField] public float Range = 3f;
    [DataField] public string? Faction;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}

public sealed partial class Emp : EntityEffect
{
    [DataField] public float Range = 3f;
    [DataField] public float EnergyConsumption;
    [DataField] public float DisableDuration;
    public override void Effect(EntityEffectBaseArgs args) { }
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}
