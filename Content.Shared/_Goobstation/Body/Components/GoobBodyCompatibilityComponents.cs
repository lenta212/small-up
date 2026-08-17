namespace Content.Shared._Goobstation.Body.Components;

[RegisterComponent]
public sealed partial class WoundableComponent : Component
{
    [DataField]
    public string DamageContainer = string.Empty;

    [DataField]
    public float Integrity;

    [DataField]
    public float IntegrityCap;

    [DataField]
    public Dictionary<string, float> Thresholds = new();
}

[RegisterComponent]
public sealed partial class ConsciousnessRequiredComponent : Component
{
    [DataField]
    public string Identifier = string.Empty;

    [DataField]
    public bool CausesDeath;
}
