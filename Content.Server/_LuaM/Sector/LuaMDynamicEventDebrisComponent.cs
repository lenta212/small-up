using System.Collections.Generic;
using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorDynamicEventSystem))]
public sealed partial class LuaMDynamicEventDebrisComponent : Component
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string TemplateId = string.Empty;

    [DataField]
    public string CreatedBy = string.Empty;

    [DataField]
    public string MarkerLocation = string.Empty;

    [DataField]
    public int DebrisSerial;

    [DataField]
    public bool HostileContact;

    [DataField]
    public int HostileCount;

    public List<EntityUid> HostileUids = new();
}
