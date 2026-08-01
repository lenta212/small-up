using Content.IntegrationTests.Pair;
using Content.Server._LuaM.Rescue;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueAgentSystem))]
public sealed class LuaMRescueReliabilityRuntimeTest
{
    [Test]
    public async Task OperatorSnapshotCountsRetriesOpenCircuitsPendingWorkAndCustody()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = entities.SpawnEntity(null, map.MapCoords);
            var patient = entities.SpawnEntity(null, map.MapCoords);
            var strap = entities.SpawnEntity(null, map.MapCoords);
            var rescue = entities.AddComponent<LuaMRescueAgentComponent>(agent);

            rescue.ActivityContext.Generation = 7;
            rescue.ActivityContext.TerminalStatus = LuaMRescueTerminalStatus.Active;
            rescue.RouteFailureAttempts[patient] = 3;
            rescue.TreatmentAttempts[patient] = 2;
            rescue.PatientBuckleAttempts[(patient, strap)] = 1;
            rescue.SkippedTargets[patient] = TimeSpan.MaxValue;
            rescue.TerminalTreatmentFailures[patient] = "NoEffectiveMedicine";
            rescue.TerminalPatientBuckleFailures[(patient, strap)] = "AttemptLimitReached";
            rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Pickup;
            rescue.PendingPlayerActionTarget = strap;
            rescue.PendingMedicalDoAfterTarget = patient;
            rescue.PendingMedicalEffectVerification = true;
            rescue.DormantRouteProbeInFlight = true;
            rescue.RequiredOnboardHandoffPatients.Add(patient);

            Assert.That(rescueSystem.BuildRescueStatusLines(), Has.Some.Contains(
                "reliability=degraded,retry=3,circuits=3,pending=4,custody=1,generation=7,terminal=Active"));

            entities.DeleteEntity(agent);
            entities.DeleteEntity(patient);
            entities.DeleteEntity(strap);
        });

        await pair.CleanReturnAsync();
    }
}
