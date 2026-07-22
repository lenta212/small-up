using Content.Shared.Corvax.TTS;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMRoboticTtsPrototypeTest
{
    [Test]
    public async Task PlayableRoboticSpeakersUseReservedTtsVoices()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(prototypes.Index<TTSVoicePrototype>("TrainingRobot").RoundStart, Is.False);
                Assert.That(prototypes.Index<TTSVoicePrototype>("Glados").RoundStart, Is.False);

                foreach (var prototypeId in new[]
                         {
                             "BorgChassisSelectable",
                             "PlayerBorgPDV",
                             "PlayerBorgTSF",
                             "MMI",
                             "PositronicBrain",
                             "PersonalAI",
                         })
                {
                    AssertVoice(prototypeId, "TrainingRobot", prototypes, components);
                }

                foreach (var prototypeId in new[]
                         {
                             "StationAiBrain",
                             "StationAiBrainVessel",
                             "StationAiBrainTSFMC",
                             "StationAiBrainPDV",
                             "StationAiBrainRedacted",
                         })
                {
                    AssertVoice(prototypeId, "Glados", prototypes, components);
                }

                var ipc = prototypes.Index<EntityPrototype>("MobIPC");
                Assert.That(ipc.TryGetComponent<TTSComponent>(out var ipcTts, components), Is.True);
                Assert.That(ipcTts.VoicePrototypeId, Is.Null,
                    "IPC voices must continue to come from the character profile.");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertVoice(
        string prototypeId,
        string expectedVoice,
        IPrototypeManager prototypes,
        IComponentFactory components)
    {
        var prototype = prototypes.Index<EntityPrototype>(prototypeId);
        Assert.That(
            prototype.TryGetComponent<TTSComponent>(out var tts, components),
            Is.True,
            $"{prototypeId} must have TTS enabled.");
        Assert.That(tts.VoicePrototypeId, Is.EqualTo(expectedVoice));
    }
}
