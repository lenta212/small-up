using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.NPC.HTN;
using Content.Server._LuaM.AI;
using Content.Shared.Damage;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared._LuaM.AI;
using Content.Shared._Shitmed.Body.Components;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMBehaviorSystem))]
public sealed class LuaMHostileMobRuntimeTest
{
    [Test]
    public async Task BloodCultistAcquiresAndDamagesPlayerInVacuum()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var melee = entities.System<SharedMeleeWeaponSystem>();
        var map = await pair.CreateTestMap();
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var session = players.GetSessionById(clientSession!.UserId);

        EntityUid cultist = default;
        EntityUid player = default;
        float startingDamage = 0f;

        await server.WaitAssertion(() =>
        {
            player = entities.SpawnEntity("MobHuman", Offset(map.MapCoords, 1.5f, 0f));
            Assert.That(players.SetAttachedEntity(session, player, true), Is.True);
            cultist = entities.SpawnEntity("MobBloodCultistAcolyte", map.MapCoords);

            var agent = entities.GetComponent<LuaMBehaviorAgentComponent>(cultist);
            var profile = prototypes.Index<LuaMBehaviorProfilePrototype>(agent.Profile);
            Assert.That(
                melee.TryGetWeapon(cultist, out var weaponUid, out var meleeWeapon, out var weaponUser),
                Is.True);
            var configuredMeleeDamage = melee.GetDamage(weaponUid, weaponUser, meleeWeapon).GetTotal().Float();

            Assert.Multiple(() =>
            {
                Assert.That(agent.Profile, Is.EqualTo("LuaMBloodCultistBehavior"));
                Assert.That(profile.PerceptionRange, Is.EqualTo(18f));
                Assert.That(profile.ThreatTolerance, Is.EqualTo(12f));
                Assert.That(profile.DecisionIntervalSeconds, Is.EqualTo(0.25f));
                Assert.That(entities.HasComponent<BreathingImmunityComponent>(cultist), Is.True);
                Assert.That(entities.HasComponent<PressureImmunityComponent>(cultist), Is.True);
                Assert.That(entities.HasComponent<BonusMeleeDamageComponent>(cultist), Is.True);
                Assert.That(configuredMeleeDamage, Is.GreaterThanOrEqualTo(44f));
                Assert.That(factions.GetNearbyHostiles(cultist, profile.PerceptionRange), Does.Contain(player));
            });

            Assert.That(behavior.EvaluateNow(cultist, out var decision), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(decision.Intent, Is.EqualTo(LuaMBehaviorIntent.DefendSelf));
                Assert.That(decision.Tier, Is.EqualTo(LuaMBehaviorTier.Combat));
                Assert.That(decision.Target, Is.EqualTo(player));
                Assert.That(
                    entities.GetComponent<HTNComponent>(cultist).RootTask.Task,
                    Is.Not.EqualTo("LuaMBehaviorRetreatCompound"));
            });

            var vulnerableWorker = entities.SpawnEntity("MobHuman", Offset(map.MapCoords, 30f, 2f));
            var vulnerableAgent = entities.EnsureComponent<LuaMBehaviorAgentComponent>(vulnerableWorker);
            vulnerableAgent.Profile = "LuaMHumanoidWorkerBehavior";
            Assert.That(behavior.EvaluateNow(vulnerableWorker, out var atmosphereDecision), Is.True);
            Assert.That(atmosphereDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.SeekSafeAtmosphere));
            entities.DeleteEntity(vulnerableWorker);

            var caster = entities.SpawnEntity("MobBloodCultistCaster", Offset(map.MapCoords, 30f, 0f));
            Assert.That(entities.GetComponent<GunComponent>(caster).DamageModifier, Is.EqualTo(1.25f));
            entities.DeleteEntity(caster);

            startingDamage = entities.GetComponent<DamageableComponent>(player).TotalDamage.Float();
        });

        await pair.RunTicksSync(180);

        await server.WaitAssertion(() =>
        {
            Assert.That(
                entities.GetComponent<DamageableComponent>(player).TotalDamage.Float(),
                Is.GreaterThan(startingDamage));
            Assert.That(players.SetAttachedEntity(session, null, true), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    private static MapCoordinates Offset(MapCoordinates origin, float x, float y)
    {
        return new MapCoordinates(origin.Position + new Vector2(x, y), origin.MapId);
    }
}
