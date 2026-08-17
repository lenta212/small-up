namespace Content.Shared._Goobstation.Compatibility;

[RegisterComponent]
public sealed partial class SlimeGrinderComponent : Component;

[RegisterComponent]
public sealed partial class EmpImmuneComponent : Component;

[RegisterComponent]
public sealed partial class SmartLinkArmComponent : Component;

[RegisterComponent]
public sealed partial class RecoilAbsorberArmComponent : Component;

[RegisterComponent]
public sealed partial class CrematoriumImmuneComponent : Component;

[RegisterComponent]
public sealed partial class VentCrawlerComponent : Component;

[RegisterComponent]
public sealed partial class SupermatterImmuneComponent : Component;

[RegisterComponent]
public sealed partial class PreventChasmFallingComponent : Component
{
    [DataField]
    public bool DeleteOnUse = true;
}

[RegisterComponent]
public sealed partial class ScannableForPointsComponent : Component
{
    [DataField]
    public int Points;
}

[RegisterComponent]
public sealed partial class RevivalContractComponent : Component
{
    [DataField]
    public EntityUid? Signer;

    [DataField]
    public EntityUid? ContractOwner;
}

[RegisterComponent]
public sealed partial class CheatDeathComponent : Component
{
    [DataField]
    public int ReviveAmount;

    [DataField]
    public bool InfiniteRevives;
}

[RegisterComponent]
public sealed partial class SpecialBreathingImmunityComponent : Component;

[RegisterComponent]
public sealed partial class SpecialPressureImmunityComponent : Component;

[RegisterComponent]
public sealed partial class SpecialLowTempImmunityComponent : Component;

[RegisterComponent]
public sealed partial class SpecialHighTempImmunityComponent : Component;

[RegisterComponent]
public sealed partial class SpeedModifierImmunityComponent : Component;

[RegisterComponent]
public sealed partial class ForcedStealthStatusEffectComponent : Component;

[RegisterComponent]
public sealed partial class ChangeFactionStatusEffectComponent : Component;

[RegisterComponent]
public sealed partial class GrantComponentsStatusEffectComponent : Component;

[RegisterComponent]
public sealed partial class SandevistanUserComponent : Component;

[RegisterComponent]
public sealed partial class SlasherAbsorbSoulsConditionComponent : Component;

[RegisterComponent]
public sealed partial class UnholyItemComponent : Component;

[RegisterComponent]
public sealed partial class DevilGripComponent : Component;

[RegisterComponent]
public sealed partial class VeryFlammableComponent : Component;

[RegisterComponent]
public sealed partial class PainNumbnessStatusEffectComponent : Component;

[RegisterComponent]
public sealed partial class TileMovementComponent : Component;
