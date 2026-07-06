#nullable enable

using System.Reflection;
using Content.Server._LuaM.Sector;
using Content.Shared._LuaM.Sector;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMAiDirectorParsingTest
{
    [TestCase("ИИ, статус сектора", "статус сектора")]
    [TestCase("иишка, передай всем тревогу", "передай всем тревогу")]
    [TestCase("Иишка, передай всем тревогу", "передай всем тревогу")]
    [TestCase("секторный отчёт, ИИ", "секторный отчёт")]
    [TestCase("ИИ", "статус")]
    [TestCase("LuaM, route", "route")]
    [TestCase("AI status", "status")]
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
    public void LocalChatAiRequestRequiresExplicitAiAddress(string message)
    {
        var (matched, request) = InvokePrivateStatic<bool, string>(
            typeof(LuaMSectorAiDirectorSystem),
            "TryExtractLocalChatAiRequest",
            message);

        Assert.That(matched, Is.False);
        Assert.That(request, Is.EqualTo(string.Empty));
    }

    [TestCase("ИИ, напиши в чат: тревога на секторе", "напиши в чат: тревога на секторе")]
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
        Assert.That(help, Does.Contain("В обычном чате"));
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
}
