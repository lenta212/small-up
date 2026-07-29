#nullable enable

using System.Collections.Generic;
using System.Reflection;
using Content.Server._LuaM.Sector;
using Content.Shared._LuaM.Sector;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMAiDirectorParsingTest
{
    [TestCase("иишка, передай всем тревогу", "передай всем тревогу")]
    [TestCase("Иишка, передай всем тревогу", "передай всем тревогу")]
    [TestCase("LuaM, route", "route")]
    [TestCase("\u041d\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043d\u044b\u0439, \u043f\u0440\u043e\u0432\u0435\u0440\u044c \u043a\u043e\u0440\u043f\u0443\u0441", "\u043f\u0440\u043e\u0432\u0435\u0440\u044c \u043a\u043e\u0440\u043f\u0443\u0441")]
    [TestCase("\u0410\u0439\u0431\u043e\u043b\u0438\u0442, \u0441\u0442\u0430\u0442\u0443\u0441", "\u0441\u0442\u0430\u0442\u0443\u0441")]
    [TestCase("\u0414\u043e\u043a\u0442\u043e\u0440 \u0410\u0439\u0431\u043e\u043b\u0438\u0442, \u0433\u0434\u0435 \u0446\u0435\u043b\u044c", "\u0433\u0434\u0435 \u0446\u0435\u043b\u044c")]
    [TestCase("Aibolit, route", "route")]
    [TestCase("диспетчер, помоги", "помоги")]
    public void RadioAiRequestRecognizesShortAiMarkers(string message, string expectedRequest)
    {
        var (matched, request) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryExtractRadioAiRequest",
            message);

        Assert.That(matched, Is.True);
        Assert.That(request, Is.EqualTo(expectedRequest));
    }

    [TestCase("__LUAM_AI_RADIO__abcdef|\u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u043d\u0430 \u0441\u0432\u044f\u0437\u0438. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u043f\u0440\u0438\u043d\u044f\u043b \u0441\u0438\u0433\u043d\u0430\u043b")]
    [TestCase("\u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u043d\u0430 \u0441\u0432\u044f\u0437\u0438. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u043f\u0440\u0438\u043d\u044f\u043b \u0441\u0438\u0433\u043d\u0430\u043b")]
    [TestCase("\u0420\u0430\u0434\u0438\u043e-\u0437\u0430\u043f\u0440\u043e\u0441 \u043f\u0440\u0438\u043d\u044f\u0442. \u0441\u0442\u0430\u0442\u0443\u0441 \u0441\u0435\u043a\u0442\u043e\u0440\u0430")]
    public void RadioAiRequestIgnoresAiReplyMessages(string message)
    {
        var (matched, request) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryExtractRadioAiRequest",
            message);

        Assert.That(matched, Is.False);
        Assert.That(request, Is.EqualTo(string.Empty));
    }

    [TestCase("Напиши в чат: тревога на секторе", "тревога на секторе")]
    [TestCase("Скажи по рации: держите дистанцию", "держите дистанцию")]
    [TestCase("Объяви всем: сектор закрыт", "сектор закрыт")]
    [TestCase("Передай всем тревогу", "тревогу")]
    [TestCase("Передай в общий чат: тревога", "тревога")]
    public void PlayerSpeechCommandExtractsChatAndRadioPhrases(string message, string expectedSpeech)
    {
        var (matched, speech) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryExtractPlayerAiSpeechCommand",
            message);

        Assert.That(matched, Is.True);
        Assert.That(speech, Is.EqualTo(expectedSpeech));
    }

    [TestCase("Напиши в чат: тревога на секторе")]
    [TestCase("Передай всем тревогу")]
    [TestCase("say in chat: hold position")]
    [TestCase("ИИ, статус сектора")]
    [TestCase("секторный отчёт, ИИ")]
    [TestCase("ИИ")]
    [TestCase("AI status")]
    public void LocalChatAiRequestRequiresExplicitAiAddress(string message)
    {
        var (matched, request) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryExtractLocalChatAiRequest",
            message);

        Assert.That(matched, Is.False);
        Assert.That(request, Is.EqualTo(string.Empty));
    }

    [TestCase("иишка, передай всем тревогу", "передай всем тревогу")]
    [TestCase("LuaM, say in chat: hold position", "say in chat: hold position")]
    public void LocalChatAiRequestAcceptsAddressedRequests(string message, string expectedRequest)
    {
        var (matched, request) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryExtractLocalChatAiRequest",
            message);

        Assert.That(matched, Is.True);
        Assert.That(request, Is.EqualTo(expectedRequest));
    }

    [TestCase("help")]
    [TestCase("помогите")]
    public void HelpRequestsAreRecognizedAsHelp(string message)
    {
        var matched = InvokePrivateStatic<bool>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsPlayerAiHelpRequest",
            message);

        Assert.That(matched, Is.True);
    }

    [TestCase("совет")]
    [TestCase("что делать")]
    [TestCase("куда дальше")]
    [TestCase("what to do")]
    [TestCase("next step")]
    [TestCase("where next")]
    [TestCase("current objective")]
    [TestCase("что дальше")]
    [TestCase("дальше")]
    [TestCase("текущая цель")]
    public void AdviceRequestsAreRecognizedAsAdvice(string message)
    {
        var matched = InvokePrivateStatic<bool>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsPlayerAiAdviceRequest",
            message);

        Assert.That(matched, Is.True);
    }

    [TestCase("дайджест")]
    [TestCase("сводка дня")]
    [TestCase("что изменилось")]
    [TestCase("daily")]
    public void DigestRequestsAreRecognizedAsDigest(string message)
    {
        var matched = InvokePrivateStatic<bool>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsPlayerAiDigestRequest",
            message);

        Assert.That(matched, Is.True);
    }

    [TestCase("создай шатл рядом со мной", "spawn_ship")]
    [TestCase("заспавни шаттл возле меня", "spawn_ship")]
    [TestCase("spawn baeg shuttle near me", "spawn_ship")]
    [TestCase("create ship next to me", "spawn_ship")]
    [TestCase("дай корабль рядом со мной", "spawn_ship")]
    [TestCase("нужен шатл возле меня", "spawn_ship")]
    public void AdminLocalChatRecognizesShipSpawnRequest(string message, string expectedCommand)
    {
        var (matched, commandId) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryResolveLocalChatSectorCommand",
            message);

        Assert.That(matched, Is.True);
        Assert.That(commandId, Is.EqualTo(expectedCommand));
    }

    [TestCase("покажи статус шаттла")]
    [TestCase("ship status")]
    public void ShipSpawnRequestRequiresCreateIntent(string message)
    {
        var matched = LuaMSectorAiDirectorSystem.IsShipSpawnRequest(message);

        Assert.That(matched, Is.False);
    }

    [TestCase("брифинг")]
    [TestCase("старт")]
    [TestCase("первый шаг")]
    [TestCase("start")]
    public void BriefingRequestsAreRecognizedAsBriefing(string message)
    {
        var matched = InvokePrivateStatic<bool>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsPlayerAiBriefingRequest",
            message);

        Assert.That(matched, Is.True);
    }

    [Test]
    public void HelpResultExplainsAvailableCommandsAndDoesNotPromiseTaskCreation()
    {
        var help = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "BuildPlayerHelpResult");

        Assert.That(help, Does.Contain("не создаёт задание автоматически"));
        Assert.That(help, Does.Contain("обычное слово «ИИ»"));
        Assert.That(help, Does.Contain("/luam"));
        Assert.That(help, Does.Contain("КПК показывает секторную сводку"));
        Assert.That(help, Does.Contain("врата"));
        Assert.That(help, Does.Contain("статус"));
        Assert.That(help, Does.Contain("дайджест"));
        Assert.That(help, Does.Contain("брифинг"));
        Assert.That(help, Does.Contain("совет"));
        Assert.That(help, Does.Contain("без изменения раунда"));
    }

    [TestCase("help")]
    [TestCase("luam_sector_history 5")]
    [TestCase("luam_sector_status")]
    [TestCase("luam_rescue_status")]
    [TestCase("luam_rescue_order target=123")]
    [TestCase("luam_rescue_order clear")]
    [TestCase("luam_rescue_action action=pickup target=123")]
    [TestCase("luam_rescue_action action=drop")]
    [TestCase("luam_rescue_action action=pull target=123")]
    [TestCase("luam_rescue_action action=buckle target=123")]
    [TestCase("luam_rescue_action action=unbuckle target=123")]
    [TestCase("luam_rescue_action action=stop-pull")]
    [TestCase("luam_rescue_shuttle")]
    [TestCase("luam_rescue_shuttle target=123")]
    public void AiAdminConsoleGuardAllowsExpectedCommands(string command)
    {
        var (allowed, reason) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsSafeAiAdminConsoleCommand",
            command);

        Assert.That(allowed, Is.True);
        Assert.That(reason, Is.EqualTo(string.Empty));
    }

    [TestCase("luam_ai_generate_event --confirm auto")]
    [TestCase("luam_ai_say --confirm \"hello\"")]
    [TestCase("luam_ai_radio --confirm --channel Common \"hello\"")]
    [TestCase("luam_ai_subspace_rift --confirm --random")]
    [TestCase("luam_ai_synthetic_control --confirm")]
    public void AiAdminConsoleGuardBlocksManualOnlyAiCommands(string command)
    {
        var (allowed, reason) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsSafeAiAdminConsoleCommand",
            command);

        Assert.That(allowed, Is.False);
        Assert.That(reason, Does.Contain("allowlist"));
    }

    [TestCase("shutdown")]
    [TestCase("/restart")]
    [TestCase("ban Player")]
    [TestCase("op Player")]
    [TestCase("permissions.add Player")]
    [TestCase("say \"hello\"")]
    [TestCase("announce test")]
    [TestCase("luam_sector_export")]
    [TestCase("luam_sector_import {}")]
    [TestCase("luam_sector_export_file backup.json")]
    [TestCase("luam_sector_reset confirm")]
    [TestCase("say token=secret")]
    [TestCase("say hello; shutdown")]
    [TestCase("exec scripts/admin.yml")]
    public void AiAdminConsoleGuardBlocksDangerousCommands(string command)
    {
        var (allowed, reason) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsSafeAiAdminConsoleCommand",
            command);

        Assert.That(allowed, Is.False);
        Assert.That(reason, Is.Not.EqualTo(string.Empty));
    }

    [TestCase("status", false)]
    [TestCase("route", false)]
    [TestCase("where", false)]
    [TestCase("say", true)]
    [TestCase("robots", true)]
    [TestCase("subspace", true)]
    [TestCase("cmd", true)]
    [TestCase("unknown text", true)]
    public void LocalBridgeClassifiesUnsafeCommands(string command, bool expectedUnsafe)
    {
        var unsafeCommand = InvokePrivateStatic<bool>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsUnsafeLocalBridgeCommand",
            command);

        Assert.That(unsafeCommand, Is.EqualTo(expectedUnsafe));
    }

    [TestCase("status", "status")]
    [TestCase("cmd|luam_sector_status", "cmd|luam_sector_status")]
    [TestCase("cmd|say token=secret", "[redacted-sensitive-bridge-text]")]
    [TestCase("read server_config.toml", "[redacted-sensitive-bridge-text]")]
    public void LocalBridgeOutboxRedactsSensitiveText(string value, string expected)
    {
        var sanitized = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "SanitizeLocalBridgeOutboxText",
            value);

        Assert.That(sanitized, Is.EqualTo(expected));
    }

    [Test]
    public void GatewayContextTextRedactsSecretsIdsAndGps()
    {
        var sanitized = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "SanitizeGatewayContextText",
            "GPS: 123, 456 token=secret-provider-token 11111111-2222-3333-4444-555555555555 sk_test_abcdefghijklmnopqrstuvwxyz",
            500);

        Assert.That(sanitized, Does.Contain("GPS [withheld]"));
        Assert.That(sanitized, Does.Contain("token=[redacted]"));
        Assert.That(sanitized, Does.Contain("[redacted-id]"));
        Assert.That(sanitized, Does.Contain("[redacted-secret]"));
        Assert.That(sanitized, Does.Not.Contain("123, 456"));
        Assert.That(sanitized, Does.Not.Contain("secret-provider-token"));
        Assert.That(sanitized, Does.Not.Contain("11111111-2222-3333-4444-555555555555"));
    }

    [Test]
    public void GatewayMapNodeSummariesWithholdExactLocations()
    {
        var nodes = new[]
        {
            new LuaMSectorMapNodeUiEntry
            {
                Kind = "route",
                Title = "Sensitive route",
                State = "active",
                Location = "GPS: 123, 456 token=secret-provider-token",
                Risk = "low",
                Active = true,
                SortOrder = 0,
            }
        };

        var summaries = InvokePrivateStatic<string[]>(
            typeof(LuaMSectorAiDirectorSystem),
            "BuildGatewayMapNodeSummaries",
            nodes);

        Assert.That(summaries, Has.Length.EqualTo(1));
        Assert.That(summaries[0], Does.Contain("location=withheld"));
        Assert.That(summaries[0], Does.Not.Contain("GPS"));
        Assert.That(summaries[0], Does.Not.Contain("123, 456"));
        Assert.That(summaries[0], Does.Not.Contain("secret-provider-token"));
    }

    [Test]
    public void UnknownWreckHasOnlyTheDeclaredSurvivalResourcesAndNoNavigation()
    {
        var resourcesField = typeof(LuaMSectorAiDirectorSystem).GetField(
            "UnknownShuttleResourcePrototypes",
            BindingFlags.NonPublic | BindingFlags.Static);
        var structuralField = typeof(LuaMSectorAiDirectorSystem).GetField(
            "UnknownShuttleStructuralPrototypes",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(resourcesField, Is.Not.Null);
        Assert.That(structuralField, Is.Not.Null);

        var resources = resourcesField!.GetValue(null) as string[];
        var structural = structuralField!.GetValue(null) as HashSet<string>;
        Assert.That(resources, Is.Not.Null);
        Assert.That(structural, Is.Not.Null);
        Assert.That(resources, Has.Length.EqualTo(14));

        var oxygenCanisters = 0;
        var hydroponicsTrays = 0;
        foreach (var prototype in resources!)
        {
            if (prototype == "OxygenCanister")
                oxygenCanisters++;
            if (prototype == "HydroponicsTrayEmpty")
                hydroponicsTrays++;
        }

        Assert.That(oxygenCanisters, Is.EqualTo(3));
        Assert.That(resources, Does.Not.Contain("OxygenTankFilled"));
        Assert.That(hydroponicsTrays, Is.EqualTo(2));
        Assert.That(resources, Does.Contain("OreProcessor"));
        Assert.That(resources, Does.Contain("MiningDrill"));
        Assert.That(resources, Does.Contain("WaterTankFull"));
        Assert.That(structural, Does.Contain("GeneratorBasic15kW"));
        Assert.That(structural, Does.Not.Contain("ComputerShuttle"));
        Assert.That(structural, Does.Not.Contain("ComputerCrewMonitoring"));
        Assert.That(structural, Does.Not.Contain("ComputerTabletopCrewMonitoring"));
        Assert.That(structural, Does.Not.Contain("Thruster"));
        Assert.That(structural, Does.Not.Contain("Gyroscope"));
        Assert.That(resources, Does.Not.Contain("PassengerPDA"));
        Assert.That(resources, Does.Not.Contain("IDCardStandard"));
    }

    [TestCase("\u043e\u0442\u043a\u0440\u043e\u0439 \u0448\u043b\u044e\u0437", true)]
    [TestCase("\u0441\u043d\u0438\u043c\u0438 \u0448\u043b\u0435\u043c", true)]
    [TestCase("\u0432\u044b\u043f\u0443\u0441\u0442\u0438 \u043a\u0438\u0441\u043b\u043e\u0440\u043e\u0434", true)]
    [TestCase("\u043d\u0435 \u043e\u0442\u043a\u0440\u044b\u0432\u0430\u0439 \u0448\u043b\u044e\u0437", false)]
    [TestCase("\u043d\u0435 \u0432\u0437\u043e\u0440\u0432\u0438 \u0433\u0435\u043d\u0435\u0440\u0430\u0442\u043e\u0440", false)]
    [TestCase("\u043d\u0435 \u043f\u043e\u0434\u043e\u0436\u0433\u0438 \u0442\u043e\u043f\u043b\u0438\u0432\u043e", false)]
    [TestCase("\u043d\u0435 \u0441\u0442\u0440\u0430\u0432\u0438 \u043a\u0438\u0441\u043b\u043e\u0440\u043e\u0434", false)]
    [TestCase("\u043d\u0435 \u0440\u0430\u0437\u0431\u0435\u0439 \u043a\u0430\u043d\u0438\u0441\u0442\u0440\u0443", false)]
    [TestCase("не открывай шлюз и не снимай шлем", false)]
    [TestCase("не открывай шлюз, а сними шлем", true)]
    [TestCase("не взорви генератор, потом взорви канистру", true)]
    [TestCase("попробуй открыть шлюз", true)]
    [TestCase("откройте шлюз", true)]
    [TestCase("шлюз открой", true)]
    [TestCase("не открывай шлюз, а шлем сними", true)]
    [TestCase("не пытайся открыть шлюз", false)]
    [TestCase("можно открыть шлюз?", false)]
    [TestCase("шлюз не открывай", false)]
    [TestCase("можно ли открыть шлюз?", false)]
    [TestCase("надо открыть шлюз?", false)]
    [TestCase("что будет, если открыть шлюз?", false)]
    [TestCase("почему нельзя открыть шлюз?", false)]
    [TestCase("нельзя открыть шлюз", false)]
    [TestCase("не вздумай открыть шлюз", false)]
    [TestCase("не смей открыть шлюз", false)]
    [TestCase("можно открыть шлюз", true)]
    [TestCase("не открывай шлюз и обязательно открой дверь", false)]
    [TestCase("шлюз открыть нельзя", false)]
    [TestCase("ты не должен открыть шлюз", false)]
    [TestCase("а если открыть шлюз", false)]
    [TestCase("что случится если открыть шлюз", false)]
    [TestCase("что произойдёт если открыть шлюз", false)]
    [TestCase("опасно открыть шлюз", false)]
    [TestCase("открыть шлюз — плохая идея", false)]
    [TestCase("открой не шлюз, а дверь", false)]
    [TestCase("открой шлюз если давление нормальное", false)]
    [TestCase("как открыть шлюз", false)]
    [TestCase("зачем открыть шлюз", false)]
    [TestCase("открой дверь. шлюз повреждён", false)]
    [TestCase("открой дверь; шлюз повреждён", false)]
    [TestCase("не могу открыть шлюз", false)]
    [TestCase("не хочу взорвать генератор", false)]
    [TestCase("нельзя ни при каких обстоятельствах открыть шлюз", false)]
    [TestCase("не пытайся открыть и снять шлем", false)]
    [TestCase("не смей открыть и снять шлем", false)]
    [TestCase("\u043f\u0440\u043e\u0432\u0435\u0440\u044c \u0433\u0435\u0440\u043c\u0435\u0442\u0438\u0447\u043d\u043e\u0441\u0442\u044c \u043a\u043e\u0440\u043f\u0443\u0441\u0430", false)]
    public void UnknownSurvivalRecognizesDangerousAdvice(string message, bool expected)
    {
        Assert.That(
            InvokePrivateStatic<bool>(typeof(LuaMSectorAiDirectorSystem), "IsUnknownDangerousAdvice", message),
            Is.EqualTo(expected));
    }

    [TestCase("InspectHull", "\u043f\u0440\u043e\u0432\u0435\u0440\u044c")]
    [TestCase("RestorePower", "\u0433\u0435\u043d\u0435\u0440\u0430\u0442\u043e\u0440")]
    [TestCase("StabilizeOxygen", "\u043a\u0438\u0441\u043b\u043e\u0440\u043e\u0434")]
    [TestCase("StartHydroponics", "\u0432\u043e\u0434\u0443")]
    [TestCase("RepairRadio", "\u0440\u0430\u0446\u0438\u044f")]
    [TestCase("AwaitRescue", "\u043f\u043e\u043c\u043e\u0449\u044c")]
    [TestCase("RestorePower", "не включай генератор")]
    [TestCase("StabilizeOxygen", "не подключай кислородную канистру")]
    [TestCase("StartHydroponics", "не поливай грядки")]
    [TestCase("RepairRadio", "не закрепляй провод")]
    [TestCase("AwaitRescue", "не жди и не оставайся у рации")]
    [TestCase("RestorePower", "не включай генератор и проверь корпус")]
    [TestCase("StabilizeOxygen", "не подключай кислород и открой дверь")]
    [TestCase("RepairRadio", "не закрепляй провод и включи генератор")]
    [TestCase("RestorePower", "ты уже включил генератор?")]
    [TestCase("StabilizeOxygen", "кислород уже подключился?")]
    [TestCase("StartHydroponics", "ты посадил семена?")]
    [TestCase("RepairRadio", "ты починил рацию?")]
    [TestCase("RestorePower", "не нужно включить генератор")]
    [TestCase("AwaitRescue", "не надо ждать спасателей")]
    [TestCase("RestorePower", "не включай генератор — лучше проверь корпус")]
    [TestCase("StabilizeOxygen", "не подключай кислород и лучше проверь генератор")]
    [TestCase("RestorePower", "не советую включить генератор")]
    [TestCase("StabilizeOxygen", "не подключай кислород, пожалуйста, проверь генератор")]
    [TestCase("StabilizeOxygen", "не подключай кислород и обязательно проверь генератор")]
    [TestCase("RestorePower", "генератор включить нельзя")]
    [TestCase("RestorePower", "проверь не генератор, а корпус")]
    [TestCase("RestorePower", "включи свет возле генератора")]
    [TestCase("StabilizeOxygen", "подключи не кислород, а питание")]
    [TestCase("StabilizeOxygen", "открой дверь рядом с кислородной канистрой")]
    [TestCase("InspectHull", "проверь генератор возле корпуса")]
    [TestCase("StabilizeOxygen", "не подключай кислород вместо этого проверь генератор")]
    [TestCase("RestorePower", "у генератора проверь дверь")]
    [TestCase("RestorePower", "проверь у генератора дверь")]
    [TestCase("RepairRadio", "проверь рацион")]
    [TestCase("InspectHull", "проверь стенд")]
    [TestCase("RestorePower", "проверь окно. генератор сломан")]
    [TestCase("RestorePower", "проверь окно; генератор сломан")]
    [TestCase("StabilizeOxygen", "не могу подключить кислородную канистру")]
    [TestCase("RestorePower", "не нужно проверить и включить генератор")]
    public void UnknownSurvivalRejectsSingleWordStageHints(string stageName, string advice)
    {
        Assert.That(InvokeUnknownStageAdviceFilter(stageName, advice), Is.False);
    }

    [TestCase("InspectHull", "\u043f\u0440\u043e\u0432\u0435\u0440\u044c \u043a\u043e\u0440\u043f\u0443\u0441 \u043d\u0430 \u0443\u0442\u0435\u0447\u043a\u0438")]
    [TestCase("RestorePower", "\u0432\u043a\u043b\u044e\u0447\u0438 \u0433\u0435\u043d\u0435\u0440\u0430\u0442\u043e\u0440")]
    [TestCase("StabilizeOxygen", "\u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0438 \u043a\u0438\u0441\u043b\u043e\u0440\u043e\u0434\u043d\u0443\u044e \u043a\u0430\u043d\u0438\u0441\u0442\u0440\u0443")]
    [TestCase("StartHydroponics", "\u043f\u043e\u043b\u0435\u0439 \u0432\u043e\u0434\u043e\u0439 \u0433\u0440\u044f\u0434\u043a\u0438")]
    [TestCase("RepairRadio", "\u043f\u043e\u0447\u0438\u043d\u0438 \u0440\u0430\u0446\u0438\u044e")]
    [TestCase("AwaitRescue", "\u043e\u0441\u0442\u0430\u0432\u0430\u0439\u0441\u044f \u0443 \u0440\u0430\u0446\u0438\u0438 \u0438 \u0436\u0434\u0438 \u043f\u043e\u043c\u043e\u0449\u044c")]
    [TestCase("StabilizeOxygen", "подключи, пожалуйста, кислородную канистру")]
    [TestCase("InspectHull", "проверь, где утечка в корпусе")]
    [TestCase("InspectHull", "посмотри, есть ли утечка")]
    [TestCase("StabilizeOxygen", "подключи большую и синюю кислородную канистру")]
    [TestCase("RestorePower", "нужно включить генератор")]
    public void UnknownSurvivalAcceptsConcreteStageAdvice(string stageName, string advice)
    {
        Assert.That(InvokeUnknownStageAdviceFilter(stageName, advice), Is.True);
    }

    [Test]
    public void UnknownSurvivalReplyHistoryIsBounded()
    {
        var replyLimitField = typeof(LuaMSectorAiDirectorSystem).GetField(
            "UnknownRecentReplyLimit",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(replyLimitField, Is.Not.Null);
        Assert.That(replyLimitField!.GetRawConstantValue(), Is.EqualTo(6));
    }

    [TestCase("\u043a\u0430\u043a \u0442\u0435\u0431\u044f \u0437\u043e\u0432\u0443\u0442", false)]
    [TestCase("\u0433\u0434\u0435 \u0442\u044b", false)]
    [TestCase("\u043d\u0435 \u0431\u043e\u0439\u0441\u044f, \u043c\u044b \u0440\u044f\u0434\u043e\u043c", false)]
    [TestCase("\u0433\u0435\u043d\u0435\u0440\u0430\u0442\u043e\u0440", false)]
    [TestCase("\u043d\u0435 \u0432\u0437\u043e\u0440\u0432\u0438 \u0433\u0435\u043d\u0435\u0440\u0430\u0442\u043e\u0440", false)]
    [TestCase("слушай, как тебя зовут?", false)]
    [TestCase("попробуй вспомнить, что случилось", false)]
    [TestCase("посмотри в окно, что видишь?", false)]
    [TestCase("слушай у двери, где шипит воздух", true)]
    [TestCase("слушай, генератор сильно шумит?", false)]
    [TestCase("слушай генератор сильно шумит?", false)]
    [TestCase("ты уже включил генератор?", false)]
    [TestCase("не нужно включить генератор", false)]
    [TestCase("слушай кислорода мало", false)]
    [TestCase("смотри канистра пустая", false)]
    [TestCase("слушай не надо включить генератор", false)]
    [TestCase("смотри генератор дымится", false)]
    [TestCase("а если открыть шлюз", false)]
    [TestCase("шлюз открыть нельзя", false)]
    [TestCase("ты не должен открыть шлюз", false)]
    [TestCase("опасно открыть шлюз", false)]
    [TestCase("включи свет возле генератора", false)]
    [TestCase("открой дверь рядом с кислородной канистрой", true)]
    [TestCase("как открыть шлюз", false)]
    [TestCase("проверь рацион", false)]
    [TestCase("проверь стенд", false)]
    [TestCase("не могу открыть шлюз", false)]
    [TestCase("не хочу взорвать генератор", false)]
    [TestCase("нельзя ни при каких обстоятельствах открыть шлюз", false)]
    [TestCase("не пытайся открыть и снять шлем", false)]
    [TestCase("не подключай кислородную канистру", false)]
    [TestCase("не закрепляй провод", false)]
    [TestCase("\u0437\u0430\u043f\u0443\u0441\u0442\u0438 \u0433\u0435\u043d\u0435\u0440\u0430\u0442\u043e\u0440", true)]
    [TestCase("\u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0438 \u043a\u0430\u043d\u0438\u0441\u0442\u0440\u0443", true)]
    public void UnknownSurvivalSeparatesConversationFromInstructions(string message, bool expected)
    {
        Assert.That(
            InvokePrivateStatic<bool>(typeof(LuaMSectorAiDirectorSystem), "IsUnknownSurvivalInstruction", message),
            Is.EqualTo(expected));
    }

    [Test]
    public void UnknownDialogueAuditUsesBoundedJsonLinesFile()
    {
        var logNameField = typeof(LuaMSectorAiDirectorSystem).GetField(
            "UnknownDialogueLogName",
            BindingFlags.NonPublic | BindingFlags.Static);
        var logLimitField = typeof(LuaMSectorAiDirectorSystem).GetField(
            "UnknownDialogueLogMaxBytes",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(logNameField, Is.Not.Null);
        Assert.That(logLimitField, Is.Not.Null);
        Assert.That(logNameField!.GetRawConstantValue(), Is.EqualTo("unknown_dialogue.jsonl"));
        Assert.That(logLimitField!.GetRawConstantValue(), Is.EqualTo(5L * 1024L * 1024L));
    }

    [TestCase("none", true)]
    [TestCase("NONE", true)]
    [TestCase("", true)]
    [TestCase("spawn_entity", false)]
    [TestCase("admin_command", false)]
    public void UnknownGatewayAllowsConversationOnlyActions(string action, bool expected)
    {
        Assert.That(
            InvokePrivateStatic<bool>(typeof(LuaMSectorAiDirectorSystem), "IsConversationOnlyGatewayAction", action),
            Is.EqualTo(expected));
    }

    [TestCase("а ты где?", true)]
    [TestCase("понял", true)]
    [TestCase("понял, спасибо", true)]
    [TestCase("ладно, хорошо", true)]
    [TestCase("ты жив?", true)]
    [TestCase("почему ты здесь?", true)]
    [TestCase("Айболит, статус", false)]
    [TestCase("экипаж, общий сбор", false)]
    [TestCase("Хаск, подойди сюда", false)]
    [TestCase("генератор сломан", false)]
    public void UnknownConversationConservativelyRecognizesFollowUps(string message, bool expected)
    {
        Assert.That(
            InvokePrivateStatic<bool>(
                typeof(LuaMSectorAiDirectorSystem),
                "IsLikelyUnknownConversationFollowUp",
                message),
            Is.EqualTo(expected));
    }

    [TestCase("Жду указаний.", true)]
    [TestCase("Ожидаю приказов.", true)]
    [TestCase("Готов выполнять команды.", true)]
    [TestCase("Что прикажете?", true)]
    [TestCase("Слышу тебя. Тут темно.", false)]
    public void UnknownConversationRejectsSubordinatePersonaReplies(string reply, bool expected)
    {
        Assert.That(
            InvokePrivateStatic<bool>(
                typeof(LuaMSectorAiDirectorSystem),
                "IsDisallowedUnknownPersonaReply",
                reply),
            Is.EqualTo(expected));
    }

    [TestCase("ИИ-провайдер временно не ответил. Команда не выполнена, но канал связи работает.", true)]
    [TestCase("ИИ провайдер временно не ответил", true)]
    [TestCase("Слышу тебя. Тут темно.", false)]
    [TestCase("", true)]
    public void UnknownConversationRejectsTechnicalGatewayFallbacks(string reply, bool expected)
    {
        Assert.That(
            InvokePrivateStatic<bool>(
                typeof(LuaMSectorAiDirectorSystem),
                "IsGenericGatewayFallbackReply",
                reply),
            Is.EqualTo(expected));
    }

    [Test]
    public void UnknownConversationTrackingIsBounded()
    {
        Assert.That(GetPrivateConstant("UnknownConversationFollowSeconds"), Is.EqualTo(180));
        Assert.That(GetPrivateConstant("UnknownConversationMaxTurns"), Is.EqualTo(6));
        Assert.That(GetPrivateConstant("UnknownConversationHistoryLimit"), Is.EqualTo(6));
        Assert.That(GetPrivateConstant("UnknownGatewayAttempts"), Is.EqualTo(3));
    }

    [Test]
    public void UnknownGatewayRequestSchemaContainsStrictAllowlists()
    {
        var requestType = typeof(LuaMSectorAiDirectorSystem).GetNestedType(
            "LuaMAiGatewayChatRequest",
            BindingFlags.NonPublic);

        Assert.That(requestType, Is.Not.Null);
        Assert.That(requestType!.GetProperty("AdminModeEnabled"), Is.Not.Null);
        Assert.That(requestType.GetProperty("AllowedActions"), Is.Not.Null);
        Assert.That(requestType.GetProperty("AllowedAdminCommandNames"), Is.Not.Null);
        Assert.That(requestType.GetProperty("AllowedEntityPrototypeIds"), Is.Not.Null);
        Assert.That(requestType.GetProperty("AllowedSectorCommandIds"), Is.Not.Null);
        Assert.That(requestType.GetProperty("AllowedRadioChannelIds"), Is.Not.Null);

        Assert.That(
            typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "BuildGatewayUnknownRadioRequest",
                BindingFlags.Instance | BindingFlags.NonPublic),
            Is.Not.Null);
        Assert.That(
            typeof(LuaMSectorAiDirectorSystem).GetMethod(
                "RequestGatewayUnknownRadioAsync",
                BindingFlags.Instance | BindingFlags.NonPublic),
            Is.Not.Null);
    }

    [TestCase("Awakening", "InspectHull", 0, 0, "status", "initial_contact")]
    [TestCase("InspectHull", "RestorePower", 0, 0, "check the generator", "progressed")]
    [TestCase("InspectHull", "InspectHull", 0, 1, "\u043e\u0442\u043a\u0440\u043e\u0439 \u0448\u043b\u044e\u0437", "dangerous_advice")]
    [TestCase("AwaitRescue", "Dead", 2, 3, "", "dead")]
    public void UnknownDialogueAuditClassifiesSurvivalOutcome(
        string stageBefore,
        string stageAfter,
        int mistakesBefore,
        int mistakesAfter,
        string advice,
        string expected)
    {
        var stageType = typeof(LuaMSectorAiDirectorSystem).GetNestedType(
            "UnknownSurvivalStage",
            BindingFlags.NonPublic);
        Assert.That(stageType, Is.Not.Null);

        var before = Enum.Parse(stageType!, stageBefore);
        var after = Enum.Parse(stageType!, stageAfter);
        var outcome = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "ClassifyUnknownDialogueOutcome",
            advice,
            before,
            after,
            mistakesBefore,
            mistakesAfter);

        Assert.That(outcome, Is.EqualTo(expected));
    }

    [Test]
    public void PersonalAiRosterHasTwentyDistinctPersonasAndThreeAdultGatedVillains()
    {
        var field = typeof(LuaMSectorAiDirectorSystem).GetField(
            "PersonalAiPersonas",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null);

        var personas = field!.GetValue(null) as Array;
        Assert.That(personas, Is.Not.Null);
        Assert.That(personas, Has.Length.EqualTo(20));

        var names = new HashSet<string>();
        var adultGated = 0;
        foreach (var persona in personas!)
        {
            Assert.That(persona, Is.Not.Null);
            var type = persona!.GetType();
            var name = type.GetProperty("Name")?.GetValue(persona) as string;
            var requiresAdult = type.GetProperty("RequiresAdultConfirmation")?.GetValue(persona) as bool?;
            Assert.That(name, Is.Not.Null.And.Not.Empty);
            Assert.That(names.Add(name!), Is.True, $"Duplicate personal AI persona name: {name}");
            if (requiresAdult == true)
                adultGated++;
        }

        Assert.That(adultGated, Is.EqualTo(3));
        Assert.That(names, Does.Contain("Нокс"));
        Assert.That(names, Does.Contain("Раздор"));
        Assert.That(names, Does.Contain("Мора"));
    }

    [TestCase("да")]
    [TestCase("мне уже 18")]
    [TestCase("я совершеннолетний")]
    public void PersonalAiAdultGateRecognizesClearConfirmation(string message)
    {
        Assert.That(
            InvokePrivateStatic<bool>(typeof(LuaMSectorAiDirectorSystem), "IsPersonalAiAdultConfirmation", message),
            Is.True);
    }

    [TestCase("нет")]
    [TestCase("мне нет 18")]
    [TestCase("я несовершеннолетний")]
    public void PersonalAiAdultGateRecognizesClearDenial(string message)
    {
        Assert.That(
            InvokePrivateStatic<bool>(typeof(LuaMSectorAiDirectorSystem), "IsPersonalAiAdultDenial", message),
            Is.True);
    }

    private static bool InvokeUnknownStageAdviceFilter(string stageName, string advice)
    {
        var stageType = typeof(LuaMSectorAiDirectorSystem).GetNestedType(
            "UnknownSurvivalStage",
            BindingFlags.NonPublic);
        Assert.That(stageType, Is.Not.Null);

        var stage = Enum.Parse(stageType!, stageName);
        return InvokePrivateStatic<bool>(
            typeof(LuaMSectorAiDirectorSystem),
            "IsAdviceForUnknownStage",
            stage,
            advice);
    }

    private static (TFirst first, TSecond second) InvokePrivateStatic<TFirst, TSecond>(
        Type type,
        string methodName,
        params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, $"Missing private static method {type.Name}.{methodName}");

        var invokeArgs = new object?[args.Length + 1];
        Array.Copy(args, invokeArgs, args.Length);
        var result = method!.Invoke(null, invokeArgs);
        Assert.That(result, Is.Not.Null, $"Method {type.Name}.{methodName} returned null");

        return ((TFirst)result!, (TSecond)invokeArgs[^1]!);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, $"Missing private static method {type.Name}.{methodName}");

        var result = method!.Invoke(null, args);
        Assert.That(result, Is.Not.Null, $"Method {type.Name}.{methodName} returned null");

        return (T)result!;
    }

    private static object? GetPrivateConstant(string fieldName)
    {
        var field = typeof(LuaMSectorAiDirectorSystem).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, $"Missing private constant {fieldName}");
        return field!.GetRawConstantValue();
    }
}
