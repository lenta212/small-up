#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.IntegrationTests.Pair;
using Content.Shared.GameTicking;
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
    public async Task UnknownConversationKeepsOrderedContextAcrossNaturalFollowUps()
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
            var handler = new SequenceGatewayHandler(
                GatewayReply("Первый ответ."),
                GatewayReply("Второй ответ."),
                GatewayReply("Третий ответ."));
            var fixture = await PrepareUnknownRadioFixture(
                pair,
                handler,
                "LuaMUnknownConversationContextTest");
            director = fixture.Director;

            var messages = new[]
            {
                "Неизвестный, привет",
                "понял, спасибо",
                "ты жив?",
            };
            var replies = new[]
            {
                "Первый ответ.",
                "Второй ответ.",
                "Третий ответ.",
            };

            for (var index = 0; index < messages.Length; index++)
            {
                var message = messages[index];
                await pair.Server.WaitPost(() => SendRadioMessage(fixture, message));
                var expectedReply = replies[index];
                await WaitForCondition(
                    pair,
                    () => handler.Calls >= index + 1 &&
                          GetPrivateDictionary<string, TimeSpan>(
                                  fixture.Director,
                                  "_recentAiRadioPayloads")
                              .Keys
                              .Any(key => key.Contains(expectedReply, StringComparison.Ordinal)),
                    $"Unknown reply {index + 1} was not emitted.");
            }

            Assert.That(handler.Calls, Is.EqualTo(3));
            var bodies = handler.Bodies;
            Assert.That(bodies, Has.Count.EqualTo(3));

            using var secondRequest = JsonDocument.Parse(bodies[1]);
            using var thirdRequest = JsonDocument.Parse(bodies[2]);
            var secondMessage = secondRequest.RootElement.GetProperty("message").GetString() ?? string.Empty;
            var thirdMessage = thirdRequest.RootElement.GetProperty("message").GetString() ?? string.Empty;

            Assert.Multiple(() =>
            {
                Assert.That(
                    secondMessage,
                    Does.Contain(
                        "recentDialogue=operator: привет | unknown: Первый ответ. | operator: понял, спасибо"));
                Assert.That(
                    thirdMessage,
                    Does.Contain(
                        "operator: понял, спасибо | unknown: Второй ответ. | operator: ты жив"));
                Assert.That(
                    thirdMessage,
                    Does.Not.Contain("operator: привет | operator: понял"),
                    "Completed turns must preserve the reply between their operator lines.");
            });
        }
        finally
        {
            director?.ResetGatewayHttpClientForTests();
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task UnknownGatewayRetriesTechnicalFallbackAndKeepsFinalFailureSilent()
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
            const string technicalFallback =
                "ИИ-провайдер временно не ответил. Команда не выполнена, но канал связи работает.";
            var recoveryHandler = new SequenceGatewayHandler(
                GatewayReply(technicalFallback),
                GatewayReply(technicalFallback),
                GatewayReply("Связь восстановилась."));
            var fixture = await PrepareUnknownRadioFixture(
                pair,
                recoveryHandler,
                "LuaMUnknownGatewayRetryTest");
            director = fixture.Director;

            await pair.Server.WaitPost(() =>
                SendRadioMessage(fixture, "Неизвестный, проверка повторов"));
            await WaitForCondition(
                pair,
                () => recoveryHandler.Calls >= 3 &&
                      GetPrivateDictionary<string, TimeSpan>(
                              fixture.Director,
                              "_recentAiRadioPayloads")
                          .Keys
                          .Any(key => key.Contains("Связь восстановилась.", StringComparison.Ordinal)),
                "Unknown did not emit the successful third gateway reply.",
                timeoutMilliseconds: 8_000);

            var firstPayloads = GetPrivateDictionary<string, TimeSpan>(
                fixture.Director,
                "_recentAiRadioPayloads");
            Assert.Multiple(() =>
            {
                Assert.That(recoveryHandler.Calls, Is.EqualTo(3));
                Assert.That(
                    firstPayloads.Keys.Any(key => key.Contains(technicalFallback, StringComparison.Ordinal)),
                    Is.False);
            });

            await pair.Server.WaitPost(() =>
            {
                InvokePrivateInstance(fixture.Director, "ClearUnknownRadioConversations");
                GetPrivateDictionary<string, TimeSpan>(
                    fixture.Director,
                    "_recentAiRadioPayloads").Clear();
                GetPrivateDictionary<string, TimeSpan>(
                    fixture.Director,
                    "_recentRadioAiRequests").Clear();
            });

            var silentHandler = new SequenceGatewayHandler(GatewayReply(technicalFallback));
            fixture.Director.SetGatewayHttpClientForTests(new HttpClient(silentHandler));

            await pair.Server.WaitPost(() =>
                SendRadioMessage(fixture, "Неизвестный, проверка полной тишины"));
            await WaitForCondition(
                pair,
                () => silentHandler.Calls >= 3 && !IsGatewayRequestActive(fixture.Director),
                "Unknown gateway did not finish all three rejected attempts.",
                timeoutMilliseconds: 8_000);

            Assert.Multiple(() =>
            {
                Assert.That(silentHandler.Calls, Is.EqualTo(3));
                Assert.That(
                    GetPrivateDictionary<string, TimeSpan>(
                        fixture.Director,
                        "_recentAiRadioPayloads"),
                    Is.Empty,
                    "No local or technical fallback may be sent after all provider attempts fail.");
            });
        }
        finally
        {
            director?.ResetGatewayHttpClientForTests();
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task UnknownGatewayReplyCannotCrossConversationReset()
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
            var handler = new BlockingGatewayHandler();
            var fixture = await PrepareUnknownRadioFixture(
                pair,
                handler,
                "LuaMUnknownGatewayResetTest");
            director = fixture.Director;

            await pair.Server.WaitPost(() =>
                SendRadioMessage(fixture, "Неизвестный, старый раунд"));
            await WaitForCondition(
                pair,
                () => handler.Calls >= 1,
                "The first Unknown gateway request did not start.");

            await pair.Server.WaitPost(() =>
            {
                InvokePrivateInstance(
                    fixture.Director,
                    "OnRoundRestartCleanup",
                    new RoundRestartCleanupEvent());
                SetPrivateField(
                    fixture.Director,
                    "_unknownOperator",
                    (EntityUid?) fixture.UnknownOperator);
                SendRadioMessage(fixture, "Неизвестный, новый разговор");
            });

            await WaitForCondition(
                pair,
                () => handler.CancellationObserved,
                "Round cleanup did not cancel the in-flight Unknown gateway request.");

            handler.Release(GatewayReply("Устаревший ответ."));
            await WaitForCondition(
                pair,
                () => !IsGatewayRequestActive(fixture.Director),
                "The obsolete Unknown gateway request did not release its gate.");

            var conversation = ReadSingleUnknownConversation(fixture.Director);
            Assert.Multiple(() =>
            {
                Assert.That(handler.Calls, Is.EqualTo(1));
                Assert.That(
                    GetPrivateDictionary<string, TimeSpan>(
                        fixture.Director,
                        "_recentAiRadioPayloads"),
                    Is.Empty,
                    "A reply from the cleared conversation must not reach radio.");
                Assert.That(conversation.Requests, Is.EqualTo(new[] { "новый разговор" }));
                Assert.That(conversation.Replies, Is.EqualTo(new[] { string.Empty }));
            });
        }
        finally
        {
            director?.ResetGatewayHttpClientForTests();
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

    private static async Task<UnknownRadioTestFixture> PrepareUnknownRadioFixture(
        TestPair pair,
        HttpMessageHandler handler,
        string mindName)
    {
        var server = pair.Server;
        server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, true);
        server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayUrl, "http://luam.invalid/propose_event");
        server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayBudgetWindowRequests, 100);
        server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayBudgetRoundRequests, 1000);

        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var playerMan = server.ResolveDependency<IPlayerManager>();
        var serverSession = playerMan.GetSessionById(clientSession!.UserId);
        var entMan = server.ResolveDependency<IEntityManager>();
        var mindSystem = entMan.System<MindSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var director = entMan.System<LuaMSectorAiDirectorSystem>();
        director.SetGatewayHttpClientForTests(new HttpClient(handler));

        var resetMethod = typeof(LuaMSectorAiDirectorSystem).GetMethod(
            "ResetRoundScopedState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var unknownOperatorField = typeof(LuaMSectorAiDirectorSystem).GetField(
            "_unknownOperator",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var receiveMethod = typeof(LuaMSectorAiDirectorSystem).GetMethod(
            "OnRadioReceive",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Multiple(() =>
        {
            Assert.That(resetMethod, Is.Not.Null);
            Assert.That(unknownOperatorField, Is.Not.Null);
            Assert.That(receiveMethod, Is.Not.Null);
        });

        var testMap = await pair.CreateTestMap();
        EntityUid speaker = default;
        EntityUid unknown = default;
        await server.WaitPost(() =>
        {
            resetMethod!.Invoke(director, new object[] { true });

            speaker = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
            var mind = mindSystem.CreateMind(serverSession.UserId, mindName);
            mindSystem.TransferTo(mind, speaker);
            playerMan.SetAttachedEntity(serverSession, speaker);

            unknown = entMan.SpawnEntity("MobHuman", testMap.GridCoords);
            unknownOperatorField!.SetValue(director, unknown);

            GetPrivateDictionary<string, TimeSpan>(director, "_recentRadioAiRequests").Clear();
            GetPrivateDictionary<string, TimeSpan>(director, "_recentAiRadioPayloads").Clear();
            GetPrivateField<Queue<TimeSpan>>(director, "_gatewayBudgetWindow").Clear();
            SetPrivateField(director, "_gatewayBudgetRoundUsed", 0);
            SetPrivateField(director, "_gatewayBudgetRoundActive", true);
        });

        await pair.RunTicksSync(5);
        return new UnknownRadioTestFixture(
            director,
            speaker,
            unknown,
            new ActiveRadioComponent(),
            proto.Index<RadioChannelPrototype>(SharedChatSystem.CommonChannel),
            SharedLanguageSystem.Universal,
            receiveMethod!);
    }

    private static void SendRadioMessage(UnknownRadioTestFixture fixture, string text)
    {
        var message = new ChatMessage(ChatChannel.Radio, text, text, NetEntity.Invalid, null);
        var eventArgs = new RadioReceiveEvent(
            fixture.Speaker,
            fixture.Channel,
            message,
            message,
            fixture.Language,
            fixture.Speaker,
            []);
        fixture.ReceiveMethod.Invoke(
            fixture.Director,
            new object[] { fixture.Speaker, fixture.Component, eventArgs });
    }

    private static async Task WaitForCondition(
        TestPair pair,
        Func<bool> condition,
        string failureMessage,
        int timeoutMilliseconds = 5_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMilliseconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            await pair.RunTicksSync(1);
        }

        Assert.That(condition(), Is.True, failureMessage);
    }

    private static void InvokePrivateInstance(
        object instance,
        string methodName,
        params object[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing private method {instance.GetType().Name}.{methodName}");
        method!.Invoke(instance, arguments);
    }

    private static bool IsGatewayRequestActive(object director)
    {
        var gate = GetPrivateField<object>(director, "_requestGate");
        var property = gate.GetType().GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(property, Is.Not.Null);
        return (bool) property!.GetValue(gate)!;
    }

    private static UnknownConversationSnapshot ReadSingleUnknownConversation(object director)
    {
        var conversations = GetPrivateField<IDictionary>(director, "_unknownRadioConversations");
        Assert.That(conversations, Has.Count.EqualTo(1));
        var state = conversations.Values.Cast<object>().Single();
        var historyProperty = state.GetType().GetProperty(
            "History",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.That(historyProperty, Is.Not.Null);

        var requests = new List<string>();
        var replies = new List<string>();
        foreach (var turn in ((IEnumerable) historyProperty!.GetValue(state)!).Cast<object>())
        {
            var requestProperty = turn.GetType().GetProperty(
                "Request",
                BindingFlags.Instance | BindingFlags.Public);
            var replyProperty = turn.GetType().GetProperty(
                "Reply",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.Multiple(() =>
            {
                Assert.That(requestProperty, Is.Not.Null);
                Assert.That(replyProperty, Is.Not.Null);
            });
            requests.Add((string) requestProperty!.GetValue(turn)!);
            replies.Add((string) replyProperty!.GetValue(turn)!);
        }

        return new UnknownConversationSnapshot(requests.ToArray(), replies.ToArray());
    }

    private static string GatewayReply(string reply)
    {
        return JsonSerializer.Serialize(new { reply, action = "none" });
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");
        return (T) field!.GetValue(instance)!;
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");
        field!.SetValue(instance, value);
    }

    private static Dictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(object instance, string fieldName)
        where TKey : notnull
    {
        return GetPrivateField<Dictionary<TKey, TValue>>(instance, fieldName);
    }

    private sealed record UnknownRadioTestFixture(
        LuaMSectorAiDirectorSystem Director,
        EntityUid Speaker,
        EntityUid UnknownOperator,
        ActiveRadioComponent Component,
        RadioChannelPrototype Channel,
        LanguagePrototype Language,
        MethodInfo ReceiveMethod);

    private sealed record UnknownConversationSnapshot(
        string[] Requests,
        string[] Replies);

    private sealed class SequenceGatewayHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly List<string> _bodies = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies)
                {
                    return _bodies.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.That(responseBodies, Is.Not.Empty);
            var call = Interlocked.Increment(ref _calls);
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ??
                       string.Empty;
            lock (_bodies)
            {
                _bodies.Add(body);
            }

            var responseBody = responseBodies[Math.Min(call - 1, responseBodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BlockingGatewayHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _cancellationObserved;

        public int Calls => Volatile.Read(ref _calls);
        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;

        public void Release(string responseBody)
        {
            _response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            cancellationToken.Register(() => Volatile.Write(ref _cancellationObserved, 1));
            return _response.Task;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.TrySetCanceled();
            base.Dispose(disposing);
        }
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
