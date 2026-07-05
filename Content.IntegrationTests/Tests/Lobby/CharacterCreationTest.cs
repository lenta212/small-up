using System.Linq;
using Content.Client.Lobby;
using Content.Server.Preferences.Managers;
using Content.Shared.Preferences;
using Robust.Client.State;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests.Lobby
{
    [TestFixture]
    [TestOf(typeof(ClientPreferencesManager))]
    [TestOf(typeof(ServerPreferencesManager))]
    public sealed class CharacterCreationTest
    {
        [Test]
        public async Task CreateDeleteCreateTest()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
            var server = pair.Server;
            var client = pair.Client;

            var clientNetManager = client.ResolveDependency<IClientNetManager>();
            var clientStateManager = client.ResolveDependency<IStateManager>();
            var clientPrefManager = client.ResolveDependency<IClientPreferencesManager>();

            var serverPrefManager = server.ResolveDependency<IServerPreferencesManager>();


            // Need to run them in sync to receive the messages.
            await pair.RunTicksSync(1);

            await PoolManager.WaitUntil(client, () => clientStateManager.CurrentState is LobbyState, 600);

            Assert.That(clientNetManager.ServerChannel, Is.Not.Null);

            var clientNetId = clientNetManager.ServerChannel.UserId;
            HumanoidCharacterProfile profile = null;

            clientPrefManager.SelectCharacter(0);

            await client.WaitAssertion(() =>
            {
                var clientCharacters = clientPrefManager.Preferences?.Characters;
                Assert.That(clientCharacters, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(clientCharacters, Has.Count.EqualTo(1));

                    Assert.That(clientStateManager.CurrentState, Is.TypeOf<LobbyState>());
                });
            });

            await client.WaitPost(() =>
            {
                profile = HumanoidCharacterProfile.Random();
                clientPrefManager.CreateCharacter(profile);
            });
            await pair.RunTicksSync(5);

            await client.WaitAssertion(() =>
            {
                var clientCharacters = clientPrefManager.Preferences?.Characters;

                Assert.That(clientCharacters, Is.Not.Null);
                Assert.That(clientCharacters, Has.Count.EqualTo(2));
                Assert.That(clientCharacters[1], Is.TypeOf<HumanoidCharacterProfile>());
                Assert.That(((HumanoidCharacterProfile)clientCharacters[1]).BankBalance, Is.EqualTo(HumanoidCharacterProfile.DefaultBalance));
                Assert.That(clientCharacters[1].MemberwiseEquals(profile));
            });

            await PoolManager.WaitUntil(server, () =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters;
                return serverCharacters.Count == 2 &&
                       serverCharacters.TryGetValue(1, out var serverProfile) &&
                       serverProfile.MemberwiseEquals(profile);
            }, maxTicks: 600);

            await server.WaitAssertion(() =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters;

                Assert.That(serverCharacters, Has.Count.EqualTo(2));
                Assert.That(serverCharacters[1], Is.TypeOf<HumanoidCharacterProfile>());
                Assert.That(((HumanoidCharacterProfile)serverCharacters[1]).BankBalance, Is.EqualTo(HumanoidCharacterProfile.DefaultBalance));
                Assert.That(serverCharacters[1].MemberwiseEquals(profile), ProfileDiff(serverCharacters[1], profile));
            });

            await client.WaitPost(() => clientPrefManager.DeleteCharacter(1));
            await pair.RunTicksSync(5);

            await client.WaitAssertion(() =>
            {
                var clientCharacters = clientPrefManager.Preferences?.Characters.Count;
                Assert.That(clientCharacters, Is.EqualTo(1));
            });

            await PoolManager.WaitUntil(server, () => serverPrefManager.GetPreferences(clientNetId).Characters.Count == 1, maxTicks: 60);

            await server.WaitAssertion(() =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters.Count;
                Assert.That(serverCharacters, Is.EqualTo(1));
            });

            await client.WaitIdleAsync();

            await client.WaitPost(() =>
            {
                profile = HumanoidCharacterProfile.Random();
                clientPrefManager.CreateCharacter(profile);
            });
            await pair.RunTicksSync(5);

            await client.WaitAssertion(() =>
            {
                var clientCharacters = clientPrefManager.Preferences?.Characters;

                Assert.That(clientCharacters, Is.Not.Null);
                Assert.That(clientCharacters, Has.Count.EqualTo(2));
                Assert.That(clientCharacters[1], Is.TypeOf<HumanoidCharacterProfile>());
                Assert.That(((HumanoidCharacterProfile)clientCharacters[1]).BankBalance, Is.EqualTo(HumanoidCharacterProfile.DefaultBalance));
                Assert.That(clientCharacters[1].MemberwiseEquals(profile));
            });

            await PoolManager.WaitUntil(server, () =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters;
                return serverCharacters.Count == 2 &&
                       serverCharacters.TryGetValue(1, out var serverProfile) &&
                       serverProfile.MemberwiseEquals(profile);
            }, maxTicks: 600); //60->600 - Mono: recycled pairs can take longer to deliver preference updates.

            await server.WaitAssertion(() =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters;

                Assert.That(serverCharacters, Has.Count.EqualTo(2));
                Assert.That(serverCharacters[1], Is.TypeOf<HumanoidCharacterProfile>());
                Assert.That(((HumanoidCharacterProfile)serverCharacters[1]).BankBalance, Is.EqualTo(HumanoidCharacterProfile.DefaultBalance));
                Assert.That(serverCharacters[1].MemberwiseEquals(profile), ProfileDiff(serverCharacters[1], profile));
            });
            await pair.CleanReturnAsync();
        }

        private static string ProfileDiff(ICharacterProfile actual, ICharacterProfile expected)
        {
            if (actual is not HumanoidCharacterProfile actualHumanoid ||
                expected is not HumanoidCharacterProfile expectedHumanoid)
            {
                return $"actual={actual.GetType().Name}, expected={expected.GetType().Name}";
            }

            return string.Join("; ", new[]
            {
                $"Name: '{actualHumanoid.Name}' vs '{expectedHumanoid.Name}'",
                $"Age: {actualHumanoid.Age} vs {expectedHumanoid.Age}",
                $"Sex: {actualHumanoid.Sex} vs {expectedHumanoid.Sex}",
                $"Gender: {actualHumanoid.Gender} vs {expectedHumanoid.Gender}",
                $"BankBalance: {actualHumanoid.BankBalance} vs {expectedHumanoid.BankBalance}",
                $"PreferenceUnavailable: {actualHumanoid.PreferenceUnavailable} vs {expectedHumanoid.PreferenceUnavailable}",
                $"SpawnPriority: {actualHumanoid.SpawnPriority} vs {expectedHumanoid.SpawnPriority}",
                $"Species: {actualHumanoid.Species} vs {expectedHumanoid.Species}",
                $"Company: {actualHumanoid.Company} vs {expectedHumanoid.Company}",
                $"FlavorText: '{actualHumanoid.FlavorText}' vs '{expectedHumanoid.FlavorText}'",
                $"AppearanceEqual: {actualHumanoid.Appearance.MemberwiseEquals(expectedHumanoid.Appearance)}",
                $"JobsEqual: {actualHumanoid.JobPriorities.SequenceEqual(expectedHumanoid.JobPriorities)}",
                $"AntagsEqual: {actualHumanoid.AntagPreferences.SequenceEqual(expectedHumanoid.AntagPreferences)}",
                $"TraitsEqual: {actualHumanoid.TraitPreferences.SequenceEqual(expectedHumanoid.TraitPreferences)}",
                $"LoadoutsEqual: {actualHumanoid.Loadouts.Count == expectedHumanoid.Loadouts.Count}",
            });
        }
    }
}
