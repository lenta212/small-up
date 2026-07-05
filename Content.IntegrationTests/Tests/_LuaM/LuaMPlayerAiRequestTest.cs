#nullable enable

using System.Collections.Generic;
using Content.Server._LuaM.Sector;
using Content.Server._NF.SectorServices;
using System.Reflection;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Server.Player;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMPlayerAiRequestTest
{
    [Test]
    public async Task HelpRequestReturnsPlayerFacingInstructions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var help = director.HandlePlayerAiRequest(serverSession, "помогите", "integration");
            var empty = director.HandlePlayerAiRequest(serverSession, "   ", "integration");

            Assert.That(help, Does.Contain("Канал ИИ активен"));
            Assert.That(help, Does.Contain("дайджест"));
            Assert.That(help, Does.Contain("брифинг"));
            Assert.That(help, Does.Contain("совет"));
            Assert.That(help, Does.Contain("маршрут"));
            Assert.That(help, Does.Contain("не создаёт задание"));
            Assert.That(empty, Is.EqualTo("Канал ИИ не получил текста. Запросите дайджест, брифинг, совет, статус, маршрут или задание."));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task PlainTextFallsBackToPlayerFacingTaskHint()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var reply = director.HandlePlayerAiRequest(serverSession, "обычный текст без триггера", "integration");

            Assert.That(reply, Does.Contain("Сообщение принято, но задание не создано"));
            Assert.That(reply, Does.Contain("ИИ, дайджест"));
            Assert.That(reply, Does.Contain("ИИ, брифинг"));
            Assert.That(reply, Does.Contain("ИИ, совет"));
            Assert.That(reply, Does.Contain("ИИ, статус"));
            Assert.That(reply, Does.Contain("ИИ, маршрут"));
            Assert.That(reply, Does.Contain("ИИ, задание"));
            Assert.That(reply, Does.Contain("/luam"));
            Assert.That(reply, Does.Contain("КПК показывает сводку"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task DigestRequestReturnsReadOnlyRecap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var reply = director.HandlePlayerAiRequest(serverSession, "ИИ, дайджест", "integration");

            Assert.That(reply, Does.Contain("Дайджест LuaM"));
            Assert.That(reply, Does.Contain("Сводка дня"));
            Assert.That(reply, Does.Not.Contain("Приказ принят"));
            Assert.That(reply, Does.Not.Contain("ИИ создал условия"));
            Assert.That(reply, Does.Not.Contain("Сообщение принято, но задание не создано"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task BriefingRequestReturnsReadOnlyStarterPlan()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var reply = director.HandlePlayerAiRequest(serverSession, "ИИ, брифинг", "integration");

            Assert.That(reply, Does.Contain("Стартовый брифинг LuaM"));
            Assert.That(reply, Does.Contain("Первые шаги"));
            Assert.That(reply, Does.Not.Contain("Приказ принят"));
            Assert.That(reply, Does.Not.Contain("ИИ создал условия"));
            Assert.That(reply, Does.Not.Contain("Сообщение принято, но задание не создано"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task AdviceRequestReturnsReadOnlyNextStep()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var reply = director.HandlePlayerAiRequest(serverSession, "что делать", "integration");
            var naturalPlan = director.HandlePlayerAiRequest(serverSession, "ИИ, что дальше", "integration");

            Assert.That(reply, Does.Contain("Совет ИИ"));
            Assert.That(naturalPlan, Does.Contain("Совет ИИ"));
            Assert.That(reply, Does.Not.Contain("Приказ принят"));
            Assert.That(naturalPlan, Does.Not.Contain("Приказ принят"));
            Assert.That(reply, Does.Not.Contain("ИИ создал условия"));
            Assert.That(naturalPlan, Does.Not.Contain("ИИ создал условия"));
            Assert.That(reply, Does.Not.Contain("Сообщение принято, но задание не создано"));
            Assert.That(naturalPlan, Does.Not.Contain("Сообщение принято, но задание не создано"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task SpeechCommandRoutesIntoPublicChatReply()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var reply = director.HandlePlayerAiRequest(serverSession, "ИИ, передай в общий чат тревогу по сектору", "integration");

            Assert.That(reply, Does.Contain("Выполнено: ИИ написал сообщение в общий чат"));
            Assert.That(reply, Does.Not.Contain("Сообщение принято, но задание не создано"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task RouteRequestReturnsCoordinatesForOpenLead()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var sectorService = entMan.System<SectorServiceSystem>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() =>
            {
                var host = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                entMan.AddComponent<StationSectorServiceHostComponent>(host);
            });

            await pair.RunTicksSync(2);

            await server.WaitPost(() =>
            {
                var service = sectorService.GetServiceEntity();
                Assert.That(service.IsValid(), Is.True);

                var memory = entMan.EnsureComponent<LuaMSectorMemoryComponent>(service);
                var records = GetPrivateField<List<LuaMSectorStoryRecord>>(memory, "Records");
                records.Clear();
                records.Add(new LuaMSectorStoryRecord
                {
                    Story = "LuaMSectorRuntimeDistress901",
                    Title = "Field Repairs",
                    ContractDescription = "GPS 42, 84 at the relay station.",
                    Hazard = "Relay overheating",
                    Resolved = false,
                });
            });

            await pair.RunTicksSync(2);

            var route = director.HandlePlayerAiRequest(serverSession, "маршрут", "integration");

            Assert.That(route, Does.Contain("Маршрут открыт"));
            Assert.That(route, Does.Contain("Field Repairs"));
            Assert.That(route, Does.Contain("42, 84 at the relay station"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task StatusRequestReturnsSectorSummary()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            var reply = director.HandlePlayerAiRequest(serverSession, "статус", "integration");
            var addressedReply = director.HandlePlayerAiRequest(serverSession, "ИИ, статус", "integration chat");

            Assert.That(reply, Does.Contain("Статус сектора"));
            Assert.That(reply, Does.Contain("активных операторов"));
            Assert.That(reply, Does.Contain("Следующие действия"));
            Assert.That(addressedReply, Does.Contain("Статус сектора"));
            Assert.That(addressedReply, Does.Contain("Следующие действия"));
            Assert.That(addressedReply, Does.Not.Contain("Выполнено: ИИ написал сообщение в общий чат"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task MissionRequestCreatesPersonalPressureReply()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            string reply = string.Empty;
            await server.WaitPost(() =>
            {
                reply = director.HandlePlayerAiRequest(serverSession, "mission", "integration");
            });

            Assert.That(reply, Does.Contain("Персональное давление").Or.Contain("Personal pressure conditions applied"));
            Assert.That(reply, Does.Not.Contain("Сообщение принято, но задание не создано"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task StargateRequestOpensSubspaceAndReturnsAPlayerFacingReply()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            string reply = string.Empty;
            await server.WaitPost(() =>
            {
                reply = director.HandlePlayerAiRequest(serverSession, "врата", "integration");
            });

            Assert.That(reply, Does.Contain("Подпространственный приказ принят"));
            Assert.That(reply, Does.Contain("Пинпоинтер врат"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task DangerRequestRaisesThreatAndReturnsAPlayerFacingReply()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var server = pair.Server;
            var session = pair.Client.Session;
            Assert.That(session, Is.Not.Null);

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var serverSession = playerMan.GetSessionById(session!.UserId);
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            string reply = string.Empty;
            await server.WaitPost(() =>
            {
                reply = director.HandlePlayerAiRequest(serverSession, "опасность", "integration");
            });

            Assert.That(reply, Does.Contain("Опасность повышена"));
            Assert.That(reply, Does.Contain("conditions"));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {instance.GetType().Name}.{fieldName}");
        return (T) field!.GetValue(instance)!;
    }
}
