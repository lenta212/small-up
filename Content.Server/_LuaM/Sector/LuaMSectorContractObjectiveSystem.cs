using System.Numerics;
using Content.Server._NF.BountyContracts;
using Content.Shared._LuaM.Sector;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

/// <summary>
/// Materializes the physical objective for a static LuaM sector contract and
/// binds that exact instance to the accepting character and route pinpointer.
/// </summary>
public sealed class LuaMSectorContractObjectiveSystem : EntitySystem
{
    private const float ObjectiveDistance = 48f;

    [Dependency] private readonly LuaMSectorStorySystem _stories = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BountyContractAcceptanceChangedEvent>(OnAcceptanceChanged);
        SubscribeLocalEvent<LuaMSectorStoryResolvedEvent>(OnStoryResolved);
        SubscribeLocalEvent<LuaMSectorMemoryResetEvent>(OnMemoryReset);
    }

    private void OnAcceptanceChanged(BountyContractAcceptanceChangedEvent ev)
    {
        if (!_stories.TryGetStoryByActiveContractId(ev.ContractId, out var story) ||
            story.ContractObjectivePrototype == null)
        {
            return;
        }

        ev.Relevant = true;

        if (!ev.Accepted)
        {
            DeleteObjectives(ev.ContractId);
            ev.Prepared = true;
            return;
        }

        var query = EntityQueryEnumerator<LuaMSectorContractObjectiveComponent>();
        while (query.MoveNext(out var existing, out var objective))
        {
            if (TerminatingOrDeleted(existing) || objective.ContractId != ev.ContractId)
                continue;

            objective.AuthorizedActor = ev.Actor;
            if (TryComp<LuaMSectorEvidenceComponent>(existing, out var existingEvidence))
                BindEvidence(existingEvidence, story, ev.ContractId, ev.Actor);
            _stories.TryBindContractRouteTarget(story.Story, existing);
            ev.Prepared = true;
            return;
        }

        if (!TryGetObjectiveCoordinates(ev.Actor, ev.ContractId, out var coordinates))
            return;

        var uid = Spawn(story.ContractObjectivePrototype.Value, coordinates);
        var evidence = EnsureComp<LuaMSectorEvidenceComponent>(uid);
        BindEvidence(evidence, story, ev.ContractId, ev.Actor);

        var objectiveComponent = EnsureComp<LuaMSectorContractObjectiveComponent>(uid);
        objectiveComponent.Story = story.Story;
        objectiveComponent.ContractId = ev.ContractId;
        objectiveComponent.AuthorizedActor = ev.Actor;

        _metaData.SetEntityName(uid, $"контрактный предмет: {story.Title}");
        _metaData.SetEntityDescription(
            uid,
            $"Физическое доказательство для контракта «{story.ContractName}». Доставьте предмет к секторному терминалу LuaM и подайте его лично.");

        if (!_stories.TryBindContractRouteTarget(story.Story, uid))
        {
            QueueDel(uid);
            return;
        }

        ev.Prepared = true;
    }

    private bool TryGetObjectiveCoordinates(EntityUid actor, uint contractId, out MapCoordinates coordinates)
    {
        coordinates = MapCoordinates.Nullspace;
        if (!TryComp<TransformComponent>(actor, out var actorTransform))
            return false;

        var actorCoordinates = _transform.ToMapCoordinates(actorTransform.Coordinates, logError: false);
        if (actorCoordinates == MapCoordinates.Nullspace)
            return false;

        // Stable angle prevents repeated accept/release cycles from moving a contract
        // unpredictably while spreading simultaneous objectives around the sector.
        var angle = Angle.FromDegrees(contractId * 137.508f);
        coordinates = new MapCoordinates(
            actorCoordinates.Position + angle.ToVec() * ObjectiveDistance,
            actorCoordinates.MapId);
        return true;
    }

    private static void BindEvidence(
        LuaMSectorEvidenceComponent evidence,
        LuaMSectorStoryRecord story,
        uint contractId,
        EntityUid actor)
    {
        evidence.Story = story.Story;
        evidence.ContractId = contractId;
        evidence.AuthorizedActor = actor;
        evidence.AcknowledgeHazard = true;
        evidence.ResolveStory = true;
        evidence.RequireSectorTerminal = true;
        evidence.Note = $"contract objective delivered: {story.ContractName}";
    }

    private void OnStoryResolved(LuaMSectorStoryResolvedEvent ev)
    {
        DeleteObjectives(ev.Story);
    }

    private void OnMemoryReset(LuaMSectorMemoryResetEvent ev)
    {
        var query = EntityQueryEnumerator<LuaMSectorContractObjectiveComponent>();
        while (query.MoveNext(out var uid, out _))
            QueueDel(uid);
    }

    private void DeleteObjectives(uint contractId)
    {
        var query = EntityQueryEnumerator<LuaMSectorContractObjectiveComponent>();
        while (query.MoveNext(out var uid, out var objective))
        {
            if (objective.ContractId == contractId)
                QueueDel(uid);
        }
    }

    private void DeleteObjectives(ProtoId<LuaMSectorStoryPrototype> story)
    {
        var query = EntityQueryEnumerator<LuaMSectorContractObjectiveComponent>();
        while (query.MoveNext(out var uid, out var objective))
        {
            if (objective.Story == story)
                QueueDel(uid);
        }
    }
}

[RegisterComponent]
[Access(typeof(LuaMSectorContractObjectiveSystem))]
public sealed partial class LuaMSectorContractObjectiveComponent : Component
{
    [DataField(required: true)]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField(required: true)]
    public uint ContractId;

    [DataField]
    public EntityUid AuthorizedActor = EntityUid.Invalid;
}
