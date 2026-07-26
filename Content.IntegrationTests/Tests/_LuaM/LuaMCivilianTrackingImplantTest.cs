using System.Linq;
using Content.Server.Explosion.Components;
using Content.Server.Jobs;
using Content.Shared.Implants.Components;
using Content.Shared.Mobs;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Store;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMCivilianTrackingImplantTest
{
    private static readonly string[] BiologicalCivilianJobs =
    {
        "Contractor",
        "Pilot",
        "Mercenary",
    };

    private static readonly string[] PirateJobs =
    {
        "Pirate",
        "PirateFirstMate",
        "PirateCaptain",
    };

    private static readonly string[] VanguardJobs =
    {
        "PDVInfiltrator",
        "PDVDenasvar",
    };

    [Test]
    public async Task CivilianTrackerIsAutomaticAndVanguardImplantersRemainAvailable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var civilian = prototypes.Index<DepartmentPrototype>("Civilian");
            var biologicalJobs = civilian.Roles
                .Select(prototypes.Index)
                .Where(job => job.JobEntity == null)
                .Select(job => job.ID)
                .ToArray();

            Assert.That(biologicalJobs, Is.EquivalentTo(BiologicalCivilianJobs));

            foreach (var jobId in BiologicalCivilianJobs)
            {
                var job = prototypes.Index<JobPrototype>(jobId);
                var trackerSpecials = job.Special
                    .OfType<AddImplantSpecial>()
                    .Where(special => special.Implants.Contains("MedicalTrackingImplant"))
                    .ToArray();

                Assert.Multiple(() =>
                {
                    Assert.That(trackerSpecials, Has.Length.EqualTo(1),
                        $"{jobId} must receive exactly one automatic medical tracker.");
                    Assert.That(trackerSpecials.Single().ApplyOnClone, Is.True,
                        $"{jobId} clones must keep the civilian tracking invariant.");
                });

                var loadout = prototypes.Index<RoleLoadoutPrototype>($"Job{jobId}");
                var groups = loadout.Groups.Select(group => group.Id).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(groups, Does.Contain("CivilianImplanter"));
                    Assert.That(groups, Does.Not.Contain("ContractorImplanter"),
                        $"{jobId} must not receive the redundant medical implanter fallback.");
                });
            }

            var borg = prototypes.Index<JobPrototype>("Borg");
            Assert.Multiple(() =>
            {
                Assert.That(borg.JobEntity, Is.EqualTo("PlayerBorgBattery"));
                Assert.That(GetImplants(borg), Does.Not.Contain("MedicalTrackingImplant"));
            });

            var civilianImplanters = prototypes.Index<LoadoutGroupPrototype>("CivilianImplanter");
            Assert.Multiple(() =>
            {
                Assert.That(civilianImplanters.MinLimit, Is.Zero);
                Assert.That(civilianImplanters.Fallbacks, Is.Empty);
                Assert.That(civilianImplanters.Loadouts.Select(loadout => loadout.Id), Is.EquivalentTo(new[]
                {
                    "ContractorLightImplanter",
                    "ContractorBikeHornImplanter",
                    "ContractorSadTromboneImplanter",
                    "ContractorMimePowersImplanter",
                }));
                Assert.That(civilianImplanters.Loadouts.Select(loadout => loadout.Id),
                    Does.Not.Contain("ContractorMedicalTrackingImplanter"));
            });

            var tracker = prototypes.Index<EntityPrototype>("MedicalTrackingImplant");
            Assert.That(tracker.TryGetComponent<SubdermalImplantComponent>(out _, components), Is.True);
            Assert.That(tracker.TryGetComponent<RattleComponent>(out var rattle, components), Is.True);
            Assert.That(tracker.TryGetComponent<TriggerOnMobstateChangeComponent>(out var trigger, components), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(rattle.RadioChannel.Id, Is.EqualTo("Medical"));
                Assert.That(trigger.MobState, Does.Contain(MobState.Critical));
                Assert.That(trigger.MobState, Does.Contain(MobState.Dead));
            });

            foreach (var jobId in PirateJobs)
            {
                var job = prototypes.Index<JobPrototype>(jobId);
                Assert.That(GetImplants(job).Count(id => id == "FreelanceTrackingImplant"), Is.EqualTo(1),
                    $"{jobId} must keep exactly one Freelance tracker.");
            }

            foreach (var jobId in VanguardJobs)
            {
                var job = prototypes.Index<JobPrototype>(jobId);
                Assert.That(GetImplants(job).Count(id => id == "ResistanceTrackingImplant"), Is.EqualTo(1),
                    $"{jobId} must receive exactly one Resistance tracker.");
                Assert.That(GetImplants(job), Does.Not.Contain("FreelanceTrackingImplant"));
            }

            var listing = prototypes.Index<ListingPrototype>("UplinkPirateImplanterFreelance");
            Assert.That(listing.ProductEntity?.Id, Is.EqualTo("RadioImplanterFreelance"));

            var vanguardImplanter = prototypes.Index<EntityPrototype>("RadioImplanterFreelance");
            Assert.That(vanguardImplanter.TryGetComponent<ImplanterComponent>(out var implanter, components), Is.True);
            Assert.That(implanter.Implant?.Id, Is.EqualTo("RadioImplantFreelance"));

        });

        await pair.CleanReturnAsync();
    }

    private static string[] GetImplants(JobPrototype job)
    {
        return job.Special
            .OfType<AddImplantSpecial>()
            .SelectMany(special => special.Implants)
            .ToArray();
    }
}
