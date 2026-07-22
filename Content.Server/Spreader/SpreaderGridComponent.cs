// Mono - file changed
using Content.Shared.Spreader;
using Robust.Shared.Prototypes;

namespace Content.Server.Spreader;

[RegisterComponent]
public sealed partial class SpreaderGridComponent : Component
{
    [DataField]
    public float UpdateAccumulator = 0f;

    [DataField]
    public float UpdateSpacing = 1f;

    // Existing maps contain this field. Runtime queue entries are derived from
    // ActiveEdgeSpreader components and must not become snapshot references.
    [DataField("spreadQueues")]
    private Dictionary<ProtoId<EdgeSpreaderPrototype>, Queue<EntityUid>> _serializedSpreadQueues = new();

    public readonly Dictionary<ProtoId<EdgeSpreaderPrototype>, Queue<Entity<EdgeSpreaderComponent>>> SpreadQueues = new();
}
