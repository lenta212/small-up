using Robust.Shared.Prototypes;

namespace Content.Server._Mono.ShadowCatPortal;

[RegisterComponent, Access(typeof(ShadowCatPortalSystem))]
public sealed partial class ShadowCatPortalComponent : Component
{
    [DataField]
    public EntProtoId CatPrototype = "MobCatShadow";

    [DataField]
    public TimeSpan SpawnDelay = TimeSpan.FromSeconds(2);
}
