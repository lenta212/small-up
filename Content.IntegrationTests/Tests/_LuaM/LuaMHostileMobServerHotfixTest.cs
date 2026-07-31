using System.Linq;
using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Server.Weapons.Ranged.Systems;
using Content.Server._LuaM.AI;
using Content.Shared.Damage;
using Content.Shared.NPC.Systems;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared._LuaM.AI;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMBehaviorSystem))]
public sealed class LuaMHostileMobServerHotfixTest
{
    [Test]
    public async Task BloodCultistEngagesPlayerInVacuumAtExtendedRange()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var behavior = entities.System<LuaMBehaviorSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var melee = entities.System<SharedMeleeWeaponSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var session = players.GetSessionById(clientSession!.UserId);

        EntityUid cultist = default;
        EntityUid player = default;
        float startingDamage = 0f;

        await server.WaitAssertion(() =>
        {
            player = entities.SpawnEntity("MobHuman", Offset(map.MapCoords, 15f, 0f));
            Assert.That(players.SetAttachedEntity(session, player, true), Is.True);
            cultist = entities.SpawnEntity("MobBloodCultistAcolyte", map.MapCoords);

            var agent = entities.GetComponent<LuaMBehaviorAgentComponent>(cultist);
            var profile = prototypes.Index<LuaMBehaviorProfilePrototype>(agent.Profile);
            Assert.That(profile.PerceptionRange, Is.LessThan(15f),
                "the test must exercise the server-only cultist range override");
            Assert.That(factions.GetNearbyHostiles(cultist, profile.PerceptionRange), Does.Not.Contain(player));
            Assert.That(factions.GetNearbyHostiles(cultist, 18f), Does.Contain(player));

            Assert.That(behavior.EvaluateNow(cultist, out var decision), Is.True);
            agent = entities.GetComponent<LuaMBehaviorAgentComponent>(cultist);
            Assert.Multiple(() =>
            {
                Assert.That(decision.Intent, Is.EqualTo(LuaMBehaviorIntent.DefendSelf));
                Assert.That(decision.Tier, Is.EqualTo(LuaMBehaviorTier.Combat));
                Assert.That(decision.Target, Is.EqualTo(player));
                Assert.That(agent.NextEvaluation - timing.CurTime,
                    Is.EqualTo(TimeSpan.FromSeconds(0.25)));
                Assert.That(agent.Observations.Any(observation =>
                        observation.Stimulus is LuaMBehaviorStimulus.UnsafeAtmosphere or
                            LuaMBehaviorStimulus.LowPressure or
                            LuaMBehaviorStimulus.HighPressure or
                            LuaMBehaviorStimulus.ExtremeTemperature),
                    Is.False,
                    "vacuum-immune hostile mobs must not retreat from a harmless atmosphere condition");
                Assert.That(
                    entities.GetComponent<HTNComponent>(cultist).RootTask.Task,
                    Is.Not.EqualTo("LuaMBehaviorRetreatCompound"));
            });

            Assert.That(
                melee.TryGetWeapon(cultist, out var weaponUid, out var meleeWeapon, out var weaponUser),
                Is.True);
            Assert.That(
                melee.GetDamage(weaponUid, weaponUser, meleeWeapon).GetTotal().Float(),
                Is.EqualTo(44f).Within(0.01f),
                "the cultist's 38-damage blade should receive the server-side +6 slash bonus");

            var vulnerableWorker = entities.SpawnEntity("MobHuman", Offset(map.MapCoords, 30f, 2f));
            var vulnerableAgent = entities.EnsureComponent<LuaMBehaviorAgentComponent>(vulnerableWorker);
            vulnerableAgent.Profile = "LuaMHumanoidWorkerBehavior";
            Assert.That(behavior.EvaluateNow(vulnerableWorker, out var atmosphereDecision), Is.True);
            Assert.That(atmosphereDecision.Intent, Is.EqualTo(LuaMBehaviorIntent.SeekSafeAtmosphere),
                "ordinary vulnerable humanoids must retain atmosphere self-preservation");
            entities.DeleteEntity(vulnerableWorker);

            transform.SetMapCoordinates(player, Offset(map.MapCoords, 1.5f, 0f));
            Assert.That(behavior.EvaluateNow(cultist, out decision), Is.True);
            Assert.That(decision.Target, Is.EqualTo(player));
            startingDamage = entities.GetComponent<DamageableComponent>(player).TotalDamage.Float();
        });

        await pair.RunTicksSync(240);

        await server.WaitAssertion(() =>
        {
            Assert.That(
                entities.GetComponent<DamageableComponent>(player).TotalDamage.Float(),
                Is.GreaterThan(startingDamage),
                "the cultist selected a combat intent but never executed an attack");
            Assert.That(players.SetAttachedEntity(session, null, true), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BloodCultistRangedShotsReceiveServerSideDamageBoost()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var gunSystem = entities.System<GunSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var caster = entities.SpawnEntity("MobBloodCultistCaster", map.MapCoords);
            var gun = entities.GetComponent<GunComponent>(caster);
            gunSystem.AttemptShoot(
                caster,
                caster,
                gun,
                new EntityCoordinates(caster, new Vector2(20f, 0f)));

            ProjectileComponent? cultProjectile = null;
            var projectileQuery = entities.EntityQueryEnumerator<ProjectileComponent>();
            while (projectileQuery.MoveNext(out _, out var projectile))
            {
                if (projectile.Shooter == caster && projectile.Weapon == caster)
                {
                    cultProjectile = projectile;
                    break;
                }
            }

            Assert.That(cultProjectile, Is.Not.Null, "the cult caster did not create a projectile");
            Assert.That(
                cultProjectile!.Damage.GetTotal().Float(),
                Is.EqualTo(8.75f).Within(0.01f),
                "the 7-damage dark bolt should receive the server-side 1.25x multiplier");

            var priest = entities.SpawnEntity("MobBloodCultistPriest", Offset(map.MapCoords, 30f, 0f));
            var cultHitscan = entities.SpawnEntity("BloodCultLaser", Offset(map.MapCoords, 30f, 1f));
            entities.EnsureComponent<LuaMBloodCultShotComponent>(cultHitscan);
            var cultHitscanDamage = entities.GetComponent<HitscanBasicDamageComponent>(cultHitscan);
            var cultRay = new HitscanRaycastFiredEvent
            {
                FromCoordinates = entities.GetComponent<TransformComponent>(cultHitscan).Coordinates,
                ShotDirection = Vector2.UnitX,
                Gun = priest,
                Shooter = priest,
                DistanceTried = 1f,
            };
            entities.EventBus.RaiseLocalEvent(cultHitscan, ref cultRay);
            Assert.That(
                cultHitscanDamage.Damage.GetTotal().Float(),
                Is.EqualTo(12.5f).Within(0.01f),
                "the priest's 10-damage hitscan should receive the server-side 1.25x multiplier");

            entities.EventBus.RaiseLocalEvent(cultHitscan, ref cultRay);
            Assert.That(
                cultHitscanDamage.Damage.GetTotal().Float(),
                Is.EqualTo(12.5f).Within(0.01f),
                "reflected or repeated hitscan events must not compound the multiplier");

            var ordinaryShooter = entities.SpawnEntity("MobHuman", Offset(map.MapCoords, 40f, 0f));
            var ordinaryHitscan = entities.SpawnEntity("BloodCultLaser", Offset(map.MapCoords, 40f, 1f));
            var ordinaryHitscanDamage = entities.GetComponent<HitscanBasicDamageComponent>(ordinaryHitscan);
            var ordinaryRay = new HitscanRaycastFiredEvent
            {
                FromCoordinates = entities.GetComponent<TransformComponent>(ordinaryHitscan).Coordinates,
                ShotDirection = Vector2.UnitX,
                Gun = ordinaryShooter,
                Shooter = ordinaryShooter,
                DistanceTried = 1f,
            };
            entities.EventBus.RaiseLocalEvent(ordinaryHitscan, ref ordinaryRay);
            Assert.That(
                ordinaryHitscanDamage.Damage.GetTotal().Float(),
                Is.EqualTo(10f).Within(0.01f),
                "non-cult shooters must not receive the cult damage bonus");
        });

        await pair.CleanReturnAsync();
    }

    private static MapCoordinates Offset(MapCoordinates origin, float x, float y)
    {
        return new MapCoordinates(origin.Position + new Vector2(x, y), origin.MapId);
    }
}
