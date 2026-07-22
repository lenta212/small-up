using System.Numerics;
using Content.Server.Mind;
using Content.Server._LuaM.Rescue;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

/// <summary>
/// Shared server-side entity setup for rescue activity runtime tests.
/// This deliberately uses the real production prototypes rather than source-text contracts.
/// </summary>
internal sealed class LuaMRescueActivityRuntimeFixture
{
    public const string AgentPrototype = "LuaMRescueAgent";
    public const string EscortPrototype = "LuaMRescueEscort";
    public const string PatientPrototype = "MobHuman";
    public const string HostilePatientPrototype = "MobHumanSyndicateAgentBase";
    public const string BorerPrototype = "MobCorticalBorer";

    private readonly IEntityManager _entities;
    private readonly MobStateSystem _mobState;
    private readonly DamageableSystem _damageable;
    private readonly SharedContainerSystem _containers;
    private readonly MindSystem _minds;
    private readonly MapId _mapId;

    public LuaMRescueActivityRuntimeFixture(IEntityManager entities, MapId mapId)
    {
        _entities = entities;
        _mapId = mapId;
        _mobState = entities.System<MobStateSystem>();
        _damageable = entities.System<DamageableSystem>();
        _containers = entities.System<SharedContainerSystem>();
        _minds = entities.System<MindSystem>();
    }

    public EntityUid SpawnAgent(Vector2 position)
    {
        var agent = Spawn(AgentPrototype, position);
        _entities.GetComponent<LuaMRescueAgentComponent>(agent).AutoAcquireTargets = false;
        return agent;
    }

    public EntityUid SpawnPatient(Vector2 position, MobState state = MobState.Alive)
    {
        var patient = Spawn(PatientPrototype, position);
        SetMobState(patient, state);
        return patient;
    }

    public EntityUid SpawnHostilePatient(Vector2 position, MobState state = MobState.Alive)
    {
        var patient = Spawn(HostilePatientPrototype, position);
        SetMobState(patient, state);
        return patient;
    }

    public EntityUid SpawnEscort(Vector2 position)
    {
        return Spawn(EscortPrototype, position);
    }

    public EntityUid SpawnBorer(Vector2 position, MobState state = MobState.Alive)
    {
        var borer = Spawn(BorerPrototype, position);
        SetMobState(borer, state);
        return borer;
    }

    public void SetMobState(EntityUid entity, MobState state)
    {
        _mobState.ChangeMobState(entity, state);
    }

    public void ApplyDamage(EntityUid entity, string damageType, int amount)
    {
        var damage = new DamageSpecifier();
        damage.DamageDict.Add(damageType, amount);
        Assert.That(
            _damageable.TryChangeDamage(entity, damage, ignoreResistances: true),
            Is.Not.Null);
    }

    public void GiveMind(EntityUid entity)
    {
        var mind = _minds.CreateMind(null, "LuaM rescue runtime patient");
        _minds.TransferTo(mind, entity, createGhost: false, mind: mind.Comp);
    }

    public EntityUid PutInContainer(EntityUid entity, Vector2 position)
    {
        var owner = _entities.SpawnEntity(null, Coordinates(position));
        var container = _containers.EnsureContainer<Container>(owner, "LuaMRescueActivityRuntimeContainer");
        Assert.That(_containers.Insert(entity, container), Is.True);
        return owner;
    }

    private EntityUid Spawn(string prototype, Vector2 position)
    {
        return _entities.SpawnEntity(prototype, Coordinates(position));
    }

    private MapCoordinates Coordinates(Vector2 position)
    {
        return new MapCoordinates(position, _mapId);
    }
}
