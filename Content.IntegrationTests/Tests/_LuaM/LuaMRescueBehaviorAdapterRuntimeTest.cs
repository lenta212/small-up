using System;
using System.Numerics;
using Content.Server._LuaM.AI;
using Content.Server._LuaM.Rescue;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared._LuaM.AI;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueBehaviorAdapterSystem))]
public sealed class LuaMRescueBehaviorAdapterRuntimeTest
{
    [Test]
    public async Task RescueMedicAndEscortApplySituationAwareSafetyDecisions()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var adapterSystem = entities.System<LuaMRescueBehaviorAdapterSystem>();
        var mobState = entities.System<MobStateSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var patient = entities.SpawnEntity(
                "MobHuman",
                map.GridCoords.Offset(new Vector2(0.4f, 0f)));
            mobState.ChangeMobState(patient, MobState.Critical);

            var medic = entities.SpawnEntity("LuaMRescueAgent", map.MapCoords);
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(medic);
            rescue.AutoAcquireTargets = false;
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            var medicBehavior = entities.GetComponent<LuaMBehaviorAgentComponent>(medic);
            medicBehavior.BuiltInPerception = false;
            var medicAdapter = entities.GetComponent<LuaMRescueBehaviorAdapterComponent>(medic);

            Assert.That(adapterSystem.RefreshNow(medic, rescue, medicAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(medic, out var treatment), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(medicBehavior.Profile.ToString(), Is.EqualTo("LuaMRescueMedicBehavior"));
                Assert.That(treatment.Intent, Is.EqualTo(LuaMBehaviorIntent.Treat));
                Assert.That(treatment.Target, Is.EqualTo(patient));
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Treating));
                Assert.That(rescue.ActivityContext.Target, Is.EqualTo(patient));
            });

            var medicThreat = entities.SpawnEntity(
                null,
                map.GridCoords.Offset(new Vector2(0.45f, 0.2f)));
            var medicTeam = entities.EnsureComponent<LuaMRescueTeamComponent>(medic);
            medicTeam.TeamId = 100;
            medicTeam.Leader = medic;
            medicTeam.Patient = patient;
            medicTeam.ThreatTarget = medicThreat;
            medicTeam.NearbyHostiles = 6;
            rescue.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Aibolit);

            Assert.That(adapterSystem.RefreshNow(medic, rescue, medicAdapter, force: true), Is.True);
            Assert.That(behavior.GetDecision(medic, out var evacuation), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(evacuation.Intent, Is.EqualTo(LuaMBehaviorIntent.Retreat));
                Assert.That(evacuation.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(rescue.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.PreparingEvacuation));
                Assert.That(
                    rescue.ActivityContext.Target,
                    Is.EqualTo(patient),
                    "The hostile observation must not replace the rescue-owned patient target.");
            });

            var escortLeader = entities.SpawnEntity("MobHuman", map.MapCoords);
            var shuttleAnchor = entities.SpawnEntity(
                null,
                map.GridCoords.Offset(new Vector2(-0.4f, 0f)));
            var escortThreat = entities.SpawnEntity(
                "MobHuman",
                map.GridCoords.Offset(new Vector2(0.45f, 0.3f)));
            entities.EnsureComponent<NpcFactionMemberComponent>(escortThreat);
            factions.AddFaction(escortThreat, "Syndicate");
            var escortTeam = entities.EnsureComponent<LuaMRescueTeamComponent>(escortLeader);
            escortTeam.TeamId = 101;
            escortTeam.Leader = escortLeader;
            escortTeam.Patient = patient;
            escortTeam.SceneAnchor = escortLeader;
            escortTeam.ShuttleAnchor = shuttleAnchor;
            escortTeam.ThreatTarget = escortThreat;
            escortTeam.NearbyHostiles = 1;
            escortTeam.SortiePlan = LuaMRescueSortiePlan.ThreatScreen;

            var escort = entities.SpawnEntity("LuaMRescueEscort", map.MapCoords);
            var escortComponent = entities.GetComponent<LuaMRescueEscortComponent>(escort);
            escortComponent.TeamId = 101;
            escortComponent.Role = LuaMRescueEscortRole.Zaslon;
            escortComponent.Leader = escortLeader;
            escortComponent.Patient = patient;
            escortComponent.SceneAnchor = escortLeader;
            escortComponent.ShuttleAnchor = shuttleAnchor;
            escortComponent.ThreatTarget = escortThreat;
            escortComponent.NearbyHostiles = 1;
            escortComponent.NextDutyActionAt = TimeSpan.MaxValue;
            escortComponent.NextSpeechTime = TimeSpan.MaxValue;
            var carrier = entities.EnsureComponent<LuaMRescueActivityCarrierComponent>(escort);
            carrier.ActivityRole = LuaMRescueRole.Zaslon;
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Zaslon);
            var escortBehavior = entities.GetComponent<LuaMBehaviorAgentComponent>(escort);
            escortBehavior.BuiltInPerception = false;
            var escortAdapter = entities.GetComponent<LuaMRescueBehaviorAdapterComponent>(escort);

            Assert.That(
                adapterSystem.RefreshNow(escort, escortComponent, escortAdapter, force: true),
                Is.True);
            Assert.That(behavior.GetDecision(escort, out var defense), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(escortBehavior.Profile.ToString(), Is.EqualTo("LuaMRescueCrewBehavior"));
                Assert.That(defense.Intent, Is.EqualTo(LuaMBehaviorIntent.DefendSelf));
                Assert.That(escortComponent.CurrentDuty, Is.EqualTo(LuaMRescueEscortDuty.ThreatScreen));
                Assert.That(escortComponent.CurrentFollowTarget, Is.EqualTo(escortThreat));
                Assert.That(carrier.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.ThreatScreen));
            });

            escortTeam.NearbyHostiles = 10;
            escortComponent.NearbyHostiles = 10;
            Assert.That(
                adapterSystem.RefreshNow(escort, escortComponent, escortAdapter, force: true),
                Is.True);
            Assert.That(behavior.GetDecision(escort, out var fallback), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(fallback.Intent, Is.EqualTo(LuaMBehaviorIntent.Retreat));
                Assert.That(fallback.Tier, Is.EqualTo(LuaMBehaviorTier.Safety));
                Assert.That(escortComponent.CurrentDuty, Is.EqualTo(LuaMRescueEscortDuty.ReturnToShuttle));
                Assert.That(escortComponent.CurrentFollowTarget, Is.EqualTo(shuttleAnchor));
                Assert.That(carrier.ActivityContext.Activity, Is.EqualTo(LuaMRescueActivity.Returning));
            });
        });

        await pair.CleanReturnAsync();
    }
}
