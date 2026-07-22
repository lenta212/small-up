using System.Collections.Generic;
using System.Linq;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Humanoid;
using Content.Shared.Speech.Components;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMVulpkaninVoicePrototypeTest
{
    private static readonly IReadOnlyDictionary<string, string> MaleVoice =
        new Dictionary<string, string>
        {
            ["Scream"] = "VulpkaninMaleScreams",
            ["Laugh"] = "VulpkaninLaugh",
            ["Sneeze"] = "VulpkaninSneezes",
            ["Crying"] = "VulpkaninCry",
            ["Growl"] = "VulpkaninMaleGrowl",
            ["Howl"] = "VulpkaninMaleHowl",
            ["Awoo"] = "VulpkaninMaleHowl",
        };

    private static readonly IReadOnlyDictionary<string, string> FemaleVoice =
        new Dictionary<string, string>
        {
            ["Scream"] = "VulpkaninFemaleScreams",
            ["Laugh"] = "VulpkaninLaugh",
            ["Sneeze"] = "VulpkaninSneezes",
            ["Crying"] = "VulpkaninCry",
            ["Growl"] = "VulpkaninFemaleGrowl",
            ["Howl"] = "VulpkaninFemaleHowl",
            ["Awoo"] = "VulpkaninFemaleHowl",
        };

    [Test]
    public async Task VulpkaninProfilesUseTheirOwnSpeciesAndSexSpecificSounds()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            // Abstract entity prototypes are not indexed at runtime, so inspect the
            // inherited component on the concrete playable species prototype.
            var vulp = prototypes.Index<EntityPrototype>("MobVulpkanin");
            Assert.That(vulp.TryGetComponent<VocalComponent>(out var vocal, components), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(vocal.Sounds, Is.Not.Null);
                Assert.That(vocal.Sounds, Has.Count.EqualTo(3));
                Assert.That(vocal.Sounds![Sex.Male], Is.EqualTo("MaleVulpkanin"));
                Assert.That(vocal.Sounds[Sex.Female], Is.EqualTo("FemaleVulpkanin"));
                Assert.That(vocal.Sounds[Sex.Unsexed], Is.EqualTo("MaleVulpkanin"));
            });

            AssertVoice(prototypes, "MaleVulpkanin", MaleVoice);
            AssertVoice(prototypes, "FemaleVulpkanin", FemaleVoice);

            AssertCollectionFiles(
                prototypes,
                "VulpkaninMaleScreams",
                "/Audio/_DeadSpace/Voice/Vulpkanin/male_fox_scream1.ogg",
                "/Audio/_DeadSpace/Voice/Vulpkanin/male_fox_scream2.ogg");
            AssertCollectionFiles(
                prototypes,
                "VulpkaninFemaleScreams",
                "/Audio/_DeadSpace/Voice/Vulpkanin/female_fox_scream1.ogg",
                "/Audio/_DeadSpace/Voice/Vulpkanin/female_fox_scream2.ogg");

            var maleFiles = GetCollectionFiles(prototypes, "VulpkaninMaleScreams");
            var femaleFiles = GetCollectionFiles(prototypes, "VulpkaninFemaleScreams");
            Assert.That(maleFiles.Intersect(femaleFiles), Is.Empty,
                "Masculine and feminine screams must never draw from the other profile.");
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertVoice(
        IPrototypeManager prototypes,
        string profileId,
        IReadOnlyDictionary<string, string> expected)
    {
        var profile = prototypes.Index<EmoteSoundsPrototype>(profileId);

        foreach (var (emote, collectionId) in expected)
        {
            Assert.That(profile.Sounds.TryGetValue(emote, out var specifier), Is.True,
                $"{profileId} is missing {emote}.");
            Assert.That(specifier, Is.TypeOf<SoundCollectionSpecifier>(),
                $"{profileId}/{emote} must resolve through one sound collection.");
            Assert.That(((SoundCollectionSpecifier) specifier!).Collection, Is.EqualTo(collectionId),
                $"{profileId}/{emote} selected another species or sex voice.");
        }
    }

    private static void AssertCollectionFiles(
        IPrototypeManager prototypes,
        string collectionId,
        params string[] expected)
    {
        Assert.That(GetCollectionFiles(prototypes, collectionId),
            Is.EquivalentTo(expected.Select(path => new ResPath(path))));
    }

    private static IReadOnlyCollection<ResPath> GetCollectionFiles(
        IPrototypeManager prototypes,
        string collectionId)
    {
        return prototypes.Index<SoundCollectionPrototype>(collectionId).PickFiles;
    }
}
