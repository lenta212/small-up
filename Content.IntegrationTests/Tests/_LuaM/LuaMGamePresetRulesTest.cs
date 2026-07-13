using System.Collections.Generic;
using Content.Server.GameTicking.Presets;
using Content.Server.RoundEnd;
using Content.Shared.CCVar;
using Robust.Shared.GameObjects;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMGamePresetRulesTest
{
    [Test]
    public async Task ProductionPresetRulesResolveWithoutRoundLengthOverride()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = true
        });

        try
        {
            await pair.Server.WaitAssertion(() =>
            {
                var prototypes = pair.Server.ProtoMan;
                var componentFactory = pair.Server.EntMan.ComponentFactory;

                Assert.Multiple(() =>
                {
                    foreach (var presetId in new[] { "MonoAllAtOnce", "MonoMixed", "LuaMDeadSpaceLowPop" })
                    {
                        var preset = prototypes.Index<GamePresetPrototype>(presetId);
                        foreach (var rule in preset.Rules)
                        {
                            Assert.That(
                                prototypes.TryIndex<EntityPrototype>(rule, out _),
                                Is.True,
                                $"{presetId} references missing entity prototype '{rule}'");
                        }
                    }

                    var apocalypse = prototypes.Index<GamePresetPrototype>("MonoAllAtOnce");
                    foreach (var rule in apocalypse.Rules)
                    {
                        var rulePrototype = prototypes.Index<EntityPrototype>(rule);
                        Assert.That(
                            rulePrototype.TryGetComponent<RoundEndTimeRuleComponent>(out _, componentFactory),
                            Is.False,
                            $"MonoAllAtOnce must use shuttle.auto_call_time instead of '{rule}' as a round-length override");
                    }
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task RoleTimerOverrideIsAuthoritativeAtRuntime()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = true
        });

        try
        {
            pair.Server.CfgMan.SetCVar(CCVars.GameRoleTimers, true);
            pair.Server.CfgMan.SetCVar(CCVars.GameRoleTimerOverride, "LuaMRoleLadder");
            await pair.RunTicksSync(1);

            await pair.Server.WaitAssertion(() =>
            {
                var prototypes = pair.Server.ProtoMan;
                var entities = pair.Server.EntMan;

                bool RequirementsMet(JobPrototype job, IReadOnlyDictionary<string, TimeSpan> playTimes)
                {
                    return JobRequirements.TryRequirementsMet(
                        job,
                        playTimes,
                        out _,
                        entities,
                        prototypes,
                        null);
                }

                var contractor = prototypes.Index<JobPrototype>("Contractor");
                var pilot = prototypes.Index<JobPrototype>("Pilot");
                var brigmedic = prototypes.Index<JobPrototype>("Brigmedic");
                var director = prototypes.Index<JobPrototype>("DirectorOfCare");

                var noPlaytime = new Dictionary<string, TimeSpan>();
                Assert.That(RequirementsMet(contractor, noPlaytime), Is.True, "starter role must remain open");
                Assert.That(RequirementsMet(pilot, noPlaytime), Is.False, "pilot must be locked below one hour");

                var pilotUnlocked = new Dictionary<string, TimeSpan>
                {
                    [PlayTimeTrackingShared.TrackerOverall] = TimeSpan.FromHours(1)
                };
                Assert.That(RequirementsMet(pilot, pilotUnlocked), Is.True, "pilot must unlock at one hour");

                var brigmedicAlternateBypass = new Dictionary<string, TimeSpan>
                {
                    [PlayTimeTrackingShared.TrackerOverall] = TimeSpan.FromHours(20)
                };
                Assert.That(
                    RequirementsMet(brigmedic, brigmedicAlternateBypass),
                    Is.False,
                    "the original 20-hour alternate set must not bypass the LuaM security-role requirement");
                brigmedicAlternateBypass["JobSecurityOfficer"] = TimeSpan.FromHours(2);
                Assert.That(RequirementsMet(brigmedic, brigmedicAlternateBypass), Is.True);

                var directorAlternateBypass = new Dictionary<string, TimeSpan>
                {
                    [PlayTimeTrackingShared.TrackerOverall] = TimeSpan.FromHours(50)
                };
                Assert.That(
                    RequirementsMet(director, directorAlternateBypass),
                    Is.False,
                    "the original 50-hour alternate set must not bypass the LuaM medic-role requirement");
                directorAlternateBypass["MdMedic"] = TimeSpan.FromHours(6);
                Assert.That(RequirementsMet(director, directorAlternateBypass), Is.True);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
