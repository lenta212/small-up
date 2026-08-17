using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Compatibility shims for imported Goobstation slime HTN prototypes.
/// Missing behaviours fail planning cleanly instead of aborting prototype loading.
/// </summary>
public sealed partial class PickCorpseEaterTargetOperator : HTNOperator
{
    [DataField] public string TargetKey = string.Empty;
    [DataField] public string CorpseKey = string.Empty;
    [DataField] public string RangeKey = string.Empty;

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        return Task.FromResult<(bool, Dictionary<string, object>?)>((false, null));
    }
}

public sealed partial class PickSlimeLatchTargetOperator : HTNOperator
{
    [DataField] public string TargetKey = string.Empty;
    [DataField] public string LatchKey = string.Empty;
    [DataField] public string RangeKey = string.Empty;

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        return Task.FromResult<(bool, Dictionary<string, object>?)>((false, null));
    }
}

public sealed partial class SlimeLatchOperator : HTNOperator
{
    [DataField] public string LatchKey = string.Empty;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Failed;
    }
}

public sealed partial class EatCorpseOperator : HTNOperator
{
    [DataField] public string CorpseKey = string.Empty;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Failed;
    }
}
