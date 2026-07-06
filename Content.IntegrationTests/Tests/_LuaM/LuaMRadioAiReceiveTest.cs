#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using Content.Server._LuaM.Sector;
using Content.Server.Mind;
using Content.Server.Radio;
using Content.Server.Radio.Components;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared._EinsteinEngines.Language.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Radio;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMRadioAiReceiveTest
{
    [Test]
    public async Task RadioAiMarkerTriggersReplyToken()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayUrl, string.Empty);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var proto = server.ResolveDependency<IPrototypeManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();
            var testMap = await pair.CreateTestMap();
            EntityUid speaker = default;

            await server.WaitPost(() =>
            {
                speaker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMRadioAiReceiveTest");
                mindSystem.TransferTo(mind, speaker);
                playerMan.SetAttachedEntity(serverSession, speaker);
            });

            await pair.RunTicksSync(5);

            var radioChannel = proto.Index<RadioChannelPrototype>(SharedChatSystem.CommonChannel);
            var language = SharedLanguageSystem.Universal;
            var component = new ActiveRadioComponent();
            var message = new ChatMessage(ChatChannel.Radio, "ИИ, статус", "ИИ, статус", NetEntity.Invalid, null);
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker);

            var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnRadioReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing private OnRadioReceive method");

            await server.WaitPost(() =>
            {
                method!.Invoke(director, new object[] { speaker, component, eventArgs });
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                var tokens = GetPrivateDictionary<string, TimeSpan>(director, "_pendingAiRadioReplyTokens");
                var payloads = GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads");

                Assert.That(tokens.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(payloads.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(payloads.Keys.Any(key => key.Contains(radioChannel.ID, StringComparison.OrdinalIgnoreCase)), Is.True);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task AibolitRadioMarkerTriggersRescueReplyActor()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayUrl, string.Empty);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var proto = server.ResolveDependency<IPrototypeManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();
            var testMap = await pair.CreateTestMap();
            EntityUid speaker = default;

            await server.WaitPost(() =>
            {
                speaker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMAibolitRadioAiReceiveTest");
                mindSystem.TransferTo(mind, speaker);
                playerMan.SetAttachedEntity(serverSession, speaker);
            });

            await pair.RunTicksSync(5);

            var radioChannel = proto.Index<RadioChannelPrototype>(SharedChatSystem.CommonChannel);
            var language = SharedLanguageSystem.Universal;
            var component = new ActiveRadioComponent();
            var messageText = "\u0410\u0439\u0431\u043e\u043b\u0438\u0442, \u0441\u0442\u0430\u0442\u0443\u0441";
            var message = new ChatMessage(ChatChannel.Radio, messageText, messageText, NetEntity.Invalid, null);
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker);

            var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnRadioReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing private OnRadioReceive method");

            await server.WaitPost(() =>
            {
                method!.Invoke(director, new object[] { speaker, component, eventArgs });
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                var tokens = GetPrivateDictionary<string, TimeSpan>(director, "_pendingAiRadioReplyTokens");
                var actors = GetPrivateDictionary<string, string>(director, "_pendingAiRadioReplyActors");
                var payloads = GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads");

                Assert.That(tokens.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(actors.Values, Does.Contain("\u0410\u0439\u0431\u043e\u043b\u0438\u0442"));
                Assert.That(payloads.Keys.Any(key => key.Contains("\u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u043d\u0430 \u0441\u0432\u044f\u0437\u0438.", StringComparison.Ordinal)), Is.True);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task AibolitRadioMarkerUsesConfiguredGatewayReply()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        LuaMSectorAiDirectorSystem? director = null;

        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayUrl, "http://luam.invalid/propose_event");

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var proto = server.ResolveDependency<IPrototypeManager>();
            director = entMan.System<LuaMSectorAiDirectorSystem>();
            var handler = new StaticGatewayHandler(
                "{\"reply\":\"Еду по текущему медсигналу. Держите коридор чистым и не трогайте пациента.\",\"action\":\"none\"}");
            director.SetGatewayHttpClientForTests(new HttpClient(handler));
            var testMap = await pair.CreateTestMap();
            EntityUid speaker = default;

            await server.WaitPost(() =>
            {
                speaker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMAibolitRadioGatewayTest");
                mindSystem.TransferTo(mind, speaker);
                playerMan.SetAttachedEntity(serverSession, speaker);
            });

            await pair.RunTicksSync(5);

            var radioChannel = proto.Index<RadioChannelPrototype>(SharedChatSystem.CommonChannel);
            var language = SharedLanguageSystem.Universal;
            var component = new ActiveRadioComponent();
            var messageText = "\u0410\u0439\u0431\u043e\u043b\u0438\u0442, \u043d\u0443\u0436\u043d\u0430 \u043f\u043e\u043c\u043e\u0449\u044c \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443";
            var message = new ChatMessage(ChatChannel.Radio, messageText, messageText, NetEntity.Invalid, null);
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker);

            var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnRadioReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing private OnRadioReceive method");

            await server.WaitPost(() =>
            {
                method!.Invoke(director, new object[] { speaker, component, eventArgs });
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                var tokens = GetPrivateDictionary<string, TimeSpan>(director, "_pendingAiRadioReplyTokens");
                var actors = GetPrivateDictionary<string, string>(director, "_pendingAiRadioReplyActors");
                var payloads = GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads");

                Assert.That(handler.Calls, Is.EqualTo(1));
                Assert.That(handler.LastRequest?.RequestUri?.AbsolutePath, Is.EqualTo("/chat"));
                Assert.That(handler.LastBody, Does.Contain("aibolit-radio"));
                Assert.That(handler.LastBody, Does.Contain("\"allowedActions\":[\"none\"]"));
                Assert.That(tokens.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(actors.Values, Does.Contain("\u0410\u0439\u0431\u043e\u043b\u0438\u0442"));
                Assert.That(payloads.Keys.Any(key => key.Contains("Еду по текущему медсигналу", StringComparison.Ordinal)), Is.True);
            });
        }
        finally
        {
            director?.ResetGatewayHttpClientForTests();
            await pair.CleanReturnAsync();
        }
    }

    private static Dictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(object instance, string fieldName)
        where TKey : notnull
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");

        return (Dictionary<TKey, TValue>) field!.GetValue(instance)!;
    }

    private sealed class StaticGatewayHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
