using Content.Server.NPC.HTN;

namespace Content.Server.NPC.HTN.Preconditions;

public sealed partial class HasActiveDoAfterPrecondition : HTNPrecondition
{
    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        return Invert;
    }
}
