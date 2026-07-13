using Content.Server.Spawners.Components;
using Content.Shared.Nutrition.AnimalHusbandry;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Spawners.EntitySystems;

public sealed partial class SpawnerSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TimedSpawnerComponent, MapInitEvent>(OnMapInit);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var curTime = _timing.CurTime;
        var query = EntityQueryEnumerator<TimedSpawnerComponent>();
        while (query.MoveNext(out var uid, out var timedSpawner))
        {
            if (timedSpawner.NextFire > curTime)
                continue;

            OnTimerFired(uid, timedSpawner);

            timedSpawner.NextFire += timedSpawner.IntervalSeconds;
        }
    }

    private void OnMapInit(Entity<TimedSpawnerComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextFire = _timing.CurTime + ent.Comp.IntervalSeconds;
    }

    private void OnTimerFired(EntityUid uid, TimedSpawnerComponent component)
    {
        if (component.MaximumTotalSpawns is { } maximumTotal &&
            component.TotalSpawned >= maximumTotal)
        {
            return;
        }

        if (!_random.Prob(component.Chance))
            return;

        var transferOffspringReservation = HasComp<AnimalHusbandryOffspringComponent>(uid);
        var number = _random.Next(component.MinimumEntitiesSpawned, component.MaximumEntitiesSpawned);
        if (component.MaximumTotalSpawns is { } maximum)
            number = Math.Min(number, maximum - component.TotalSpawned);

        // A marked spawner represents exactly one reserved population slot.
        if (transferOffspringReservation)
            number = Math.Min(number, 1);

        var coordinates = Transform(uid).Coordinates;

        for (var i = 0; i < number; i++)
        {
            var entity = _random.Pick(component.Prototypes);
            if (transferOffspringReservation)
                RemComp<AnimalHusbandryOffspringComponent>(uid);

            EntityUid spawned;
            try
            {
                spawned = SpawnAtPosition(entity, coordinates);
            }
            catch
            {
                if (transferOffspringReservation && !TerminatingOrDeleted(uid))
                    EnsureComp<AnimalHusbandryOffspringComponent>(uid);

                throw;
            }

            if (transferOffspringReservation)
            {
                EnsureComp<AnimalHusbandryOffspringComponent>(spawned);
                component.MaximumTotalSpawns = component.TotalSpawned + 1;
                transferOffspringReservation = false;
            }

            component.TotalSpawned++;
        }
    }
}
