#nullable enable

using System.Reflection;
using Content.Server._LuaM.Sector;
using Content.Shared.CCVar;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMSyntheticControlTest
{
    [Test]
    public async Task ApplySyntheticControlEnablesDirectorAndReturnsSnapshot()
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
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() => director.ResetGatewayDiagnosticsForTests());

            var result = string.Empty;
            await server.WaitPost(() =>
            {
                result = director.ApplySyntheticControl("integration-test", announce: false);
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(result, Does.Contain("Synthetic control armed"));
                Assert.That(result, Does.Contain("condition="));

                var state = director.BuildAdminState(string.Empty, string.Empty);
                Assert.That(state.Enabled, Is.True);
                Assert.That(state.GatewaySharedContext, Has.Some.Contains("Counts only"));
                Assert.That(state.GatewaySharedContext, Has.Some.Contains("Selected target is anonymized"));
                Assert.That(state.GatewayWithheldContext, Has.Some.Contains("Real admin/player names"));
                Assert.That(state.GatewayWithheldContext, Has.Some.Contains("Exact player coordinates"));
                Assert.That(state.GatewayWithheldContext, Has.Some.Contains("Gateway tokens"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("External AI proposes intent"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Gateway requests are throttled"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Gateway audit counters"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Last gateway request shape preview"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("RAG/source preview"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Block reason preview"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Top-line AI outcome status"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Grouped AI outcome"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("Transport failure outcome"));
                Assert.That(state.GatewayPrivacyNotes, Has.Some.Contains("cannot run server actions"));
                Assert.That(state.AiOutcomeStatus, Does.Contain("gateway unavailable"));
                Assert.That(state.AiOutcomeStatus, Does.Contain("local safe commands"));
                Assert.That(state.AiOutcomeGroup, Is.EqualTo("gateway unavailable"));
                Assert.That(state.AiOutcomeSummary, Does.Contain("External model is disabled"));
                Assert.That(state.AiOutcomeSummary, Does.Contain("local safe commands"));
                Assert.That(state.GatewayLastRequestShape, Has.Some.Contains("No gateway request"));
                Assert.That(state.GatewayLastRequestShape, Has.Some.Contains("metadata only"));
                Assert.That(state.GatewayRagSourceShape, Has.Some.Contains("No gateway RAG/source retrieval"));
                Assert.That(state.GatewayRagSourceShape, Has.Some.Contains("source categories and counts only"));
                Assert.That(state.GatewayBlockReasonSummary, Has.Some.Contains("No gateway block reason"));
                Assert.That(state.GatewayBlockReasonSummary, Has.Some.Contains("unsafeInput=0"));
                Assert.That(state.GatewayBudgetWindowSeconds, Is.GreaterThan(0));
                Assert.That(state.GatewayBudgetWindowLimit, Is.GreaterThan(0));
                Assert.That(state.GatewayBudgetWindowRemaining, Is.EqualTo(state.GatewayBudgetWindowLimit));
                Assert.That(state.GatewayBudgetRoundLimit, Is.GreaterThan(0));
                Assert.That(state.GatewayAuditRedactions, Is.EqualTo(0));
                Assert.That(state.GatewayAuditBudgetBlocks, Is.EqualTo(0));
                Assert.That(state.GatewayAuditProviderOutputBlocks, Is.EqualTo(0));
                Assert.That(state.GatewayAuditTransportFailures, Is.EqualTo(0));
                Assert.That(state.GatewayBlockUnsafeInputs, Is.EqualTo(0));
                Assert.That(state.GatewayBlockBudgets, Is.EqualTo(0));
                Assert.That(state.GatewayBlockInvalidSchemas, Is.EqualTo(0));
                Assert.That(state.GatewayBlockForbiddenActions, Is.EqualTo(0));
                Assert.That(state.GatewayBlockLocalValidations, Is.EqualTo(0));
                Assert.That(state.GatewayRagAllowedSources, Is.EqualTo(0));
                Assert.That(state.GatewayRagDeniedSources, Is.EqualTo(0));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task GatewayBudgetBlocksSecondWindowRequestBeforeProviderUse()
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
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() =>
            {
                director.ResetGatewayDiagnosticsForTests();
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayBudgetWindow, 300);
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayBudgetWindowRequests, 1);
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGatewayBudgetRoundRequests, 60);
            });

            await server.WaitAssertion(() =>
            {
                var first = InvokeTryConsumeGatewayBudget(director, "integration budget test", out var firstReason);
                var second = InvokeTryConsumeGatewayBudget(director, "integration budget test", out var secondReason);
                var state = director.BuildAdminState(string.Empty, string.Empty);

                Assert.That(first, Is.True);
                Assert.That(firstReason, Is.EqualTo(string.Empty));
                Assert.That(second, Is.False);
                Assert.That(secondReason, Does.Contain("gateway budget exhausted"));
                Assert.That(state.GatewayBudgetWindowUsed, Is.EqualTo(1));
                Assert.That(state.GatewayBudgetWindowRemaining, Is.EqualTo(0));
                Assert.That(state.GatewayBudgetRetrySeconds, Is.GreaterThan(0));
                Assert.That(state.GatewayAuditBudgetBlocks, Is.EqualTo(1));
                Assert.That(state.GatewayBlockBudgets, Is.EqualTo(1));
                Assert.That(state.GatewayBlockReasonSummary, Has.Some.Contains("category=budget block"));
                Assert.That(state.AiOutcomeStatus, Does.Contain("last gateway block=budget block"));
                Assert.That(state.AiOutcomeStatus, Does.Contain("action did not run"));
                Assert.That(state.AiOutcomeStatus, Does.Contain("sensitive details withheld"));
                Assert.That(state.AiOutcomeGroup, Is.EqualTo("command blocked"));
                Assert.That(state.AiOutcomeSummary, Does.Contain("category=budget block"));
                Assert.That(state.AiOutcomeSummary, Does.Contain("action did not run"));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task GatewayAuditCountsRedactionsWithoutSensitiveValues()
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
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() => director.ResetGatewayDiagnosticsForTests());

            await server.WaitAssertion(() =>
            {
                var sanitized = InvokeSanitizeGatewayContextTextAudited(
                    director,
                    "GPS: 123, 456 token=secret-provider-token 11111111-2222-3333-4444-555555555555 sk_test_abcdefghijklmnopqrstuvwxyz",
                    500);
                _ = InvokeSanitizeGatewayContextTextAudited(
                    director,
                    new string('x', 64),
                    16);

                var state = director.BuildAdminState(string.Empty, string.Empty);

                Assert.That(sanitized, Does.Contain("GPS [withheld]"));
                Assert.That(sanitized, Does.Contain("token=[redacted]"));
                Assert.That(sanitized, Does.Contain("[redacted-id]"));
                Assert.That(sanitized, Does.Contain("[redacted-secret]"));
                Assert.That(sanitized, Does.Not.Contain("123, 456"));
                Assert.That(sanitized, Does.Not.Contain("secret-provider-token"));
                Assert.That(state.GatewayAuditRedactions, Is.GreaterThanOrEqualTo(4));
                Assert.That(state.GatewayAuditIdRedactions, Is.GreaterThanOrEqualTo(1));
                Assert.That(state.GatewayAuditSecretRedactions, Is.GreaterThanOrEqualTo(2));
                Assert.That(state.GatewayAuditLocationRedactions, Is.GreaterThanOrEqualTo(1));
                Assert.That(state.GatewayAuditTruncatedFields, Is.EqualTo(1));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task GatewayProviderBlockReasonsCategorizeOutputFailures()
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
            var entMan = server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() => director.ResetGatewayDiagnosticsForTests());

            await server.WaitAssertion(() =>
            {
                InvokeRecordGatewayProviderOutputBlock(
                    director,
                    "invalid schema",
                    "admin chat: provider returned invalid JSON token=secret-provider-token GPS: 123, 456");
                InvokeRecordGatewayProviderOutputBlock(
                    director,
                    "forbidden action",
                    "model action is not allowlisted: shutdown");
                InvokeRecordGatewayProviderOutputBlock(
                    director,
                    "local validation rejected",
                    "OpenAI-compatible API returned proposal, but local validation rejected it");

                var state = director.BuildAdminState(string.Empty, string.Empty);
                var blockReasons = string.Join("\n", state.GatewayBlockReasonSummary);

                Assert.That(state.GatewayAuditProviderOutputBlocks, Is.EqualTo(3));
                Assert.That(state.GatewayAuditTransportFailures, Is.EqualTo(0));
                Assert.That(state.GatewayBlockInvalidSchemas, Is.EqualTo(1));
                Assert.That(state.GatewayBlockForbiddenActions, Is.EqualTo(1));
                Assert.That(state.GatewayBlockLocalValidations, Is.EqualTo(1));
                Assert.That(state.AiOutcomeStatus, Does.Contain("last gateway block=local validation rejected"));
                Assert.That(state.AiOutcomeStatus, Does.Contain("action did not run"));
                Assert.That(state.AiOutcomeGroup, Is.EqualTo("provider rejected output"));
                Assert.That(state.AiOutcomeSummary, Does.Contain("category=local validation rejected"));
                Assert.That(state.AiOutcomeSummary, Does.Contain("action did not run"));
                Assert.That(state.AiOutcomeSummary, Does.Not.Contain("secret-provider-token"));
                Assert.That(state.AiOutcomeSummary, Does.Not.Contain("123, 456"));
                Assert.That(blockReasons, Does.Contain("invalidSchema=1"));
                Assert.That(blockReasons, Does.Contain("category=invalid schema"));
                Assert.That(blockReasons, Does.Contain("category=forbidden action"));
                Assert.That(blockReasons, Does.Contain("category=local validation rejected"));
                Assert.That(blockReasons, Does.Contain("token=[redacted]"));
                Assert.That(blockReasons, Does.Contain("GPS [withheld]"));
                Assert.That(blockReasons, Does.Not.Contain("secret-provider-token"));
                Assert.That(blockReasons, Does.Not.Contain("123, 456"));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static bool InvokeTryConsumeGatewayBudget(
        LuaMSectorAiDirectorSystem director,
        string purpose,
        out string reason)
    {
        var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
            "TryConsumeGatewayBudget",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null);

        var args = new object?[] { purpose, null };
        var result = (bool) method!.Invoke(director, args)!;
        reason = (string) args[1]!;
        return result;
    }

    private static string InvokeSanitizeGatewayContextTextAudited(
        LuaMSectorAiDirectorSystem director,
        string value,
        int limit)
    {
        var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
            "SanitizeGatewayContextTextAudited",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null);

        return (string) method!.Invoke(director, [value, limit])!;
    }

    private static void InvokeRecordGatewayProviderOutputBlock(
        LuaMSectorAiDirectorSystem director,
        string category,
        string reason)
    {
        var method = typeof(LuaMSectorAiDirectorSystem).GetMethod(
            "RecordGatewayProviderOutputBlock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null);

        method!.Invoke(director, [category, reason]);
    }
}
