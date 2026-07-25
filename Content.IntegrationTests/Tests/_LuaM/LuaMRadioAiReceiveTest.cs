#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using Content.Server._NF.Radio;
using Content.Server._LuaM.Sector;
using Content.Server.Chat.V2;
using Content.Server.Mind;
using Content.Server.Radio;
using Content.Server.Radio.Components;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared._EinsteinEngines.Language.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Chat.V2.Repository;
using Content.Shared.Radio;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
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
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, true);
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
            var messageText = "\u043d\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043d\u044b\u0439, \u0441\u0442\u0430\u0442\u0443\u0441";
            var message = new ChatMessage(ChatChannel.Radio, messageText, messageText, NetEntity.Invalid, null);
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker, []);

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

                Assert.That(tokens, Is.Empty);
                Assert.That(actors, Is.Empty);
                Assert.That(payloads.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(payloads.Keys.Any(key => key.Contains(radioChannel.ID, StringComparison.OrdinalIgnoreCase)), Is.True);
            });

            var transformMethod = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnRadioTransformMessage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(transformMethod, Is.Not.Null, "Missing private OnRadioTransformMessage method");

            await server.WaitPost(() =>
            {
                const string knownToken = "known-radio-transform-token";
                const string replyText = "\u041d\u0435 \u043f\u043e\u043d\u044f\u043b, \u043a\u0430\u043a \u044d\u0442\u043e \u0441\u0434\u0435\u043b\u0430\u0442\u044c.";
                const string replyActor = "\u041d\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043d\u044b\u0439";
                var tokens = GetPrivateDictionary<string, TimeSpan>(director, "_pendingAiRadioReplyTokens");
                var actors = GetPrivateDictionary<string, string>(director, "_pendingAiRadioReplyActors");
                tokens[knownToken] = TimeSpan.MaxValue;
                actors[knownToken] = replyActor;

                var transform = new RadioTransformMessageEvent(
                    radioChannel,
                    speaker,
                    "ignored",
                    $"__LUAM_AI_RADIO__{knownToken}|{replyText}",
                    speaker);
                object[] invokeArgs =
                [
                    speaker,
                    entMan.GetComponent<MetaDataComponent>(speaker),
                    transform,
                ];

                transformMethod!.Invoke(director, invokeArgs);
                var result = (RadioTransformMessageEvent) invokeArgs[2];

                Assert.That(result.Message, Is.EqualTo(replyText));
                Assert.That(result.Message, Does.Not.Contain("__LUAM_AI_RADIO__"));
                Assert.That(result.Name, Is.EqualTo(replyActor));
                Assert.That(tokens, Is.Empty);
                Assert.That(actors, Is.Empty);
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
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, true);
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
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker, []);

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

                Assert.That(tokens, Is.Empty);
                Assert.That(actors, Is.Empty);
                Assert.That(payloads.Keys.Any(key => key.Contains("\u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u043d\u0430 \u0441\u0432\u044f\u0437\u0438.", StringComparison.Ordinal)), Is.True);
                Assert.That(payloads.Keys.Any(key => key.Contains("Медканал чистый", StringComparison.Ordinal)), Is.True);
                Assert.That(payloads.Keys.Any(key => key.Contains("Секторная память LuaM", StringComparison.Ordinal)), Is.True);
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
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, true);
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
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker, []);

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
                Assert.That(handler.LastBody, Does.Contain("phraseBundles"));
                Assert.That(handler.LastBody, Does.Contain("radio-style"));
                Assert.That(handler.LastBody, Does.Contain("current-status"));
                Assert.That(handler.LastBody, Does.Contain("local-fallback-style"));
                Assert.That(handler.LastBody, Does.Contain("\"allowedActions\":[\"none\"]"));
                Assert.That(tokens, Is.Empty);
                Assert.That(actors, Is.Empty);
                Assert.That(payloads.Keys.Any(key => key.Contains("Еду по текущему медсигналу", StringComparison.Ordinal)), Is.True);
            });
        }
        finally
        {
            director?.ResetGatewayHttpClientForTests();
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task WorldActionCooldownIsSharedBetweenLocalChatAndRadio()
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
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, true);
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayUrl, string.Empty);
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var proto = server.ResolveDependency<IPrototypeManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();
            var unknownOperatorField = typeof(LuaMSectorAiDirectorSystem).GetField(
                "_unknownOperator",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(unknownOperatorField, Is.Not.Null, "Missing private _unknownOperator field");
            var testMap = await pair.CreateTestMap();
            EntityUid speaker = default;

            await server.WaitPost(() =>
            {
                speaker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMSharedPlayerAiCooldownTest");
                mindSystem.TransferTo(mind, speaker);
                playerMan.SetAttachedEntity(serverSession, speaker);
                unknownOperatorField!.SetValue(director, speaker);

                GetPrivateDictionary<NetUserId, TimeSpan>(director, "_nextPlayerWorldActionByUser").Clear();
                GetPrivateDictionary<string, TimeSpan>(director, "_recentRadioAiRequests").Clear();
                GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads").Clear();
            });

            await pair.RunTicksSync(5);

            var localMethod = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnChatMessageCreated",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var radioMethod = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnRadioReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(localMethod, Is.Not.Null, "Missing private OnChatMessageCreated method");
            Assert.That(radioMethod, Is.Not.Null, "Missing private OnRadioReceive method");

            var cooldowns = GetPrivateDictionary<NetUserId, TimeSpan>(director, "_nextPlayerWorldActionByUser");
            var firstNextAllowed = TimeSpan.Zero;
            var statusReply = string.Empty;
            await server.WaitPost(() =>
            {
                localMethod!.Invoke(
                    director,
                    new object[] { new MessageCreatedEvent(new LocalChatCreatedEvent(speaker, "/luam mission", 10f)) });

                Assert.That(cooldowns.TryGetValue(serverSession.UserId, out firstNextAllowed), Is.True);
                statusReply = director.HandlePlayerAiRequest(serverSession, "status", "integration cooldown read-only");
            });

            Assert.That(statusReply, Does.Contain("Статус сектора"));
            Assert.That(cooldowns[serverSession.UserId], Is.EqualTo(firstNextAllowed));

            var radioChannel = proto.Index<RadioChannelPrototype>(SharedChatSystem.CommonChannel);
            var language = SharedLanguageSystem.Universal;
            var component = new ActiveRadioComponent();
            var message = new ChatMessage(ChatChannel.Radio, "/luam danger", "/luam danger", NetEntity.Invalid, null);
            var eventArgs = new RadioReceiveEvent(speaker, radioChannel, message, message, language, speaker, []);

            await server.WaitPost(() =>
            {
                radioMethod!.Invoke(director, new object[] { speaker, component, eventArgs });
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                var payloads = GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads");
                Assert.That(cooldowns, Has.Count.EqualTo(1));
                Assert.That(cooldowns[serverSession.UserId], Is.EqualTo(firstNextAllowed));
                Assert.That(
                    payloads.Keys.Any(key => key.Contains("охлаждается", StringComparison.OrdinalIgnoreCase)),
                    Is.True);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task DisabledDirectorBlocksDirectorAndAibolitRadioWithoutEnablingOrCallingGateway()
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
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, false);
            server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayUrl, "http://luam.invalid/propose_event");
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(clientSession!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var mindSystem = entMan.System<MindSystem>();
            var proto = server.ResolveDependency<IPrototypeManager>();
            director = entMan.System<LuaMSectorAiDirectorSystem>();
            var handler = new StaticGatewayHandler("{\"reply\":\"unexpected\",\"action\":\"none\"}");
            director.SetGatewayHttpClientForTests(new HttpClient(handler));
            var testMap = await pair.CreateTestMap();
            EntityUid speaker = default;

            await server.WaitPost(() =>
            {
                speaker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
                var mind = mindSystem.CreateMind(serverSession.UserId, "LuaMDisabledRadioAiReceiveTest");
                mindSystem.TransferTo(mind, speaker);
                playerMan.SetAttachedEntity(serverSession, speaker);

                GetPrivateDictionary<NetUserId, TimeSpan>(director, "_nextPlayerWorldActionByUser").Clear();
                GetPrivateDictionary<string, TimeSpan>(director, "_recentRadioAiRequests").Clear();
                GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads").Clear();
            });

            await pair.RunTicksSync(5);

            var radioChannel = proto.Index<RadioChannelPrototype>(SharedChatSystem.CommonChannel);
            var language = SharedLanguageSystem.Universal;
            var component = new ActiveRadioComponent();
            var directorMessage = new ChatMessage(ChatChannel.Radio, "AI, mission", "AI, mission", NetEntity.Invalid, null);
            var directorEvent = new RadioReceiveEvent(
                speaker,
                radioChannel,
                directorMessage,
                directorMessage,
                language,
                speaker,
                []);
            var aibolitText = "\u0410\u0439\u0431\u043e\u043b\u0438\u0442, \u0441\u0442\u0430\u0442\u0443\u0441";
            var aibolitMessage = new ChatMessage(
                ChatChannel.Radio,
                aibolitText,
                aibolitText,
                NetEntity.Invalid,
                null);
            var aibolitEvent = new RadioReceiveEvent(
                speaker,
                radioChannel,
                aibolitMessage,
                aibolitMessage,
                language,
                speaker,
                []);
            var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "OnRadioReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing private OnRadioReceive method");

            await server.WaitPost(() =>
            {
                method!.Invoke(director, new object[] { speaker, component, directorEvent });
                method.Invoke(director, new object[] { speaker, component, aibolitEvent });
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var cooldowns = GetPrivateDictionary<NetUserId, TimeSpan>(director, "_nextPlayerWorldActionByUser");
                var payloads = GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads");

                Assert.That(handler.Calls, Is.Zero);
                Assert.That(server.CfgMan.GetCVar(CCVars.LuaMAiDirectorEnabled), Is.False);
                Assert.That(cooldowns, Is.Empty);
                Assert.That(
                    payloads.Keys.Any(key => key.Contains("отключен администратором", StringComparison.OrdinalIgnoreCase)),
                    Is.True);
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
