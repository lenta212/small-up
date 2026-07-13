using Content.Server._LuaM.Animals;
using Content.Server.StationEvents.Components;
using Content.Shared.GameTicking.Components;
using Content.Shared.Station.Components;
using Content.Shared.Storage;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.StationEvents.Events;

public sealed class VentCrittersRule : StationEventSystem<VentCrittersRuleComponent>
{
    [Dependency] private LuaMAnimalPopulationSystem _animalPopulation = default!;

    /*
     * DO NOT COPY PASTE THIS TO MAKE YOUR MOB EVENT.
     * USE THE PROTOTYPE.
     */

    protected override void Started(EntityUid uid, VentCrittersRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStations(gameRule.NumberOfGrids.Min, gameRule.NumberOfGrids.Max, out var stations)) // mono change
            return;

        var locations = EntityQueryEnumerator<VentCritterSpawnLocationComponent, TransformComponent>();
        var validLocations = new List<EntityCoordinates>();
        while (locations.MoveNext(out _, out _, out var transform))
        {
            var station = CompOrNull<StationMemberComponent>(transform.GridUid)?.Station;
            if (station.HasValue && stations.Contains(station.Value))
            {
                validLocations.Add(transform.Coordinates);
                foreach (var spawn in EntitySpawnCollection.GetSpawns(component.Entries, RobustRandom))
                {
                    TrySpawnPopulationControlled(spawn, transform.Coordinates, transform.MapID);
                }
            }
        }

        if (component.SpecialEntries.Count == 0 || validLocations.Count == 0)
        {
            return;
        }

        // guaranteed spawn
        var specialEntry = RobustRandom.Pick(component.SpecialEntries);
        var specialSpawn = RobustRandom.Pick(validLocations);
        if (specialEntry.PrototypeId is { } specialPrototype)
        {
            TrySpawnPopulationControlled(
                specialPrototype,
                specialSpawn,
                Transform(specialSpawn.EntityId).MapID);
        }

        foreach (var location in validLocations)
        {
            foreach (var spawn in EntitySpawnCollection.GetSpawns(component.SpecialEntries, RobustRandom))
            {
                TrySpawnPopulationControlled(spawn, location, Transform(location.EntityId).MapID);
            }
        }
    }

    private void TrySpawnPopulationControlled(EntProtoId prototype, EntityCoordinates coordinates, MapId mapId)
    {
        if (_animalPopulation.IsPopulationControlledPrototype(prototype) &&
            _animalPopulation.GetRemainingPopulationSlots(mapId) == 0)
        {
            return;
        }

        Spawn(prototype, coordinates);
    }
}
