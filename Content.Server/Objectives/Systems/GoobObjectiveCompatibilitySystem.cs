using Content.Server.Objectives.Components;
using Content.Shared.Objectives.Components;

namespace Content.Server.Objectives.Systems;

public sealed partial class GoobObjectiveCompatibilitySystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BlobCaptureConditionComponent, ObjectiveGetProgressEvent>(OnBlobProgress);
        SubscribeLocalEvent<SignContractConditionComponent, ObjectiveGetProgressEvent>(OnDevilContractProgress);
        SubscribeLocalEvent<MeetContractWeightConditionComponent, ObjectiveGetProgressEvent>(OnDevilWeightProgress);
        SubscribeLocalEvent<DetonateNukeConditionComponent, ObjectiveGetProgressEvent>(OnNukeProgress);
    }

    private void OnBlobProgress(Entity<BlobCaptureConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        args.Progress = 0f;
    }

    private void OnDevilContractProgress(Entity<SignContractConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        args.Progress = 0f;
    }

    private void OnDevilWeightProgress(Entity<MeetContractWeightConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        args.Progress = 0f;
    }

    private void OnNukeProgress(Entity<DetonateNukeConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        args.Progress = 0f;
    }
}
