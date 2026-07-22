#nullable enable

using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server._LuaM.Administration;
using Content.Server._LuaM.Sector;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared._LuaM.Administration;
using Robust.Server.Player;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMAiDirectorAuthorizationTest
{
    [Test]
    public async Task GameMasterModeBypassesConfirmationButNeverGrantsServerFlag()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        LuaMAiDirectorEui? eui = null;
        IAdminManager? adminManager = null;
        AdminData? adminData = null;
        EuiManager? euiManager = null;
        LuaMSectorAiDirectorSystem? director = null;
        AdminFlags originalFlags = default;
        var originalActive = false;
        var hadOriginalAdminData = false;

        try
        {
            var server = pair.Server;
            var clientSession = pair.Client.Session;
            Assert.That(clientSession, Is.Not.Null);

            var playerManager = server.ResolveDependency<IPlayerManager>();
            var session = playerManager.GetSessionById(clientSession!.UserId);
            adminManager = server.ResolveDependency<IAdminManager>();
            euiManager = server.ResolveDependency<EuiManager>();
            var entMan = server.ResolveDependency<IEntityManager>();
            director = entMan.System<LuaMSectorAiDirectorSystem>();

            await server.WaitPost(() =>
            {
                adminData = adminManager.GetAdminData(session, includeDeAdmin: true);
                if (adminData == null)
                {
                    adminManager.PromoteHost(session);
                    return;
                }

                hadOriginalAdminData = true;
                originalFlags = adminData.Flags;
                originalActive = adminData.Active;
            });

            if (adminData == null)
            {
                await pair.RunTicksSync(5);
                await server.WaitAssertion(() =>
                {
                    adminData = adminManager.GetAdminData(session, includeDeAdmin: true);
                    Assert.That(adminData, Is.Not.Null, "Test session could not be promoted to an administrator.");
                });
            }

            await server.WaitAssertion(() =>
            {
                Assert.That(adminData, Is.Not.Null);
                adminData!.Active = true;
                adminData.Flags = AdminFlags.Admin;
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorEnabled, false);
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGameMasterMode, true);

                eui = new LuaMAiDirectorEui();
                euiManager.OpenEui(eui, session);

                eui.HandleMessage(new LuaMAiDirectorEuiMsg.SetEnabled { Enabled = true });
                var denied = (LuaMAiDirectorEuiState) eui.GetNewState();

                Assert.That(denied.GameMasterModeEnabled, Is.True);
                Assert.That(denied.CanRunServerActions, Is.False);
                Assert.That(denied.Enabled, Is.False);
                Assert.That(denied.HasPendingConfirmation, Is.False);
                Assert.That(denied.LastResult, Does.Contain("requires the Server admin flag"));

                adminData.Flags = AdminFlags.Admin | AdminFlags.Server;
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGameMasterMode, false);
                eui.HandleMessage(new LuaMAiDirectorEuiMsg.SetEnabled { Enabled = true });
                var pending = (LuaMAiDirectorEuiState) eui.GetNewState();

                Assert.That(pending.Enabled, Is.False);
                Assert.That(pending.CanRunServerActions, Is.True);
                Assert.That(pending.HasPendingConfirmation, Is.True);
                Assert.That(pending.PendingConfirmationId, Is.Not.Empty);

                adminData.Flags = AdminFlags.Admin;
                eui.HandleMessage(new LuaMAiDirectorEuiMsg.ConfirmPendingAction
                {
                    ConfirmationId = pending.PendingConfirmationId,
                });
                var revoked = (LuaMAiDirectorEuiState) eui.GetNewState();

                Assert.That(revoked.Enabled, Is.False);
                Assert.That(revoked.CanRunServerActions, Is.False);
                Assert.That(revoked.HasPendingConfirmation, Is.False);
                Assert.That(revoked.LastResult, Does.Contain("requires the Server admin flag"));

                adminData.Flags = AdminFlags.Admin | AdminFlags.Server;
                server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGameMasterMode, true);
                eui.HandleMessage(new LuaMAiDirectorEuiMsg.SetEnabled { Enabled = true });
                var gameMaster = (LuaMAiDirectorEuiState) eui.GetNewState();

                Assert.That(gameMaster.Enabled, Is.True);
                Assert.That(gameMaster.CanRunServerActions, Is.True);
                Assert.That(gameMaster.HasPendingConfirmation, Is.False);
                Assert.That(gameMaster.LastResult, Does.Contain("enabled"));

                eui.HandleMessage(new LuaMAiDirectorEuiMsg.QuickAction
                {
                    Action = LuaMAiDirectorEuiMsg.QuickEvent,
                    TargetUserId = string.Empty,
                    TemplateId = LuaMAiDirectorEuiMsg.AutoTemplateId,
                });
                var missingQuickTarget = (LuaMAiDirectorEuiState) eui.GetNewState();
                Assert.That(missingQuickTarget.HasPendingConfirmation, Is.False);
                Assert.That(missingQuickTarget.LastResult, Does.Contain("select an active player target"));

                eui.HandleMessage(new LuaMAiDirectorEuiMsg.Generate
                {
                    TargetUserId = string.Empty,
                    TemplateId = LuaMAiDirectorEuiMsg.AutoTemplateId,
                    Instruction = "create a local process",
                    UseGateway = false,
                    IgnoreOpenLead = false,
                });
                var missingGenerateTarget = (LuaMAiDirectorEuiState) eui.GetNewState();
                Assert.That(missingGenerateTarget.HasPendingConfirmation, Is.False);
                Assert.That(missingGenerateTarget.LastResult, Does.Contain("select an active player target"));
            });

            await server.WaitPost(() =>
            {
                eui!.HandleMessage(new LuaMAiDirectorEuiMsg.Chat
                {
                    Message = "subspace_rift near selected player",
                    TargetUserId = session.UserId.ToString(),
                    TemplateId = LuaMAiDirectorEuiMsg.AutoTemplateId,
                });
            });
            await pair.RunTicksSync(10);
            await server.WaitAssertion(() =>
            {
                var advisoryChat = (LuaMAiDirectorEuiState) eui!.GetNewState();
                Assert.That(advisoryChat.HasPendingConfirmation, Is.False);
                Assert.That(advisoryChat.ChatTranscript, Does.Contain("Action not executed"));
                Assert.That(advisoryChat.ChatTranscript, Does.Contain("Server-flag confirmed action"));
            });
        }
        finally
        {
            if (pair.Server.IsAlive)
            {
                await pair.Server.WaitPost(() =>
                {
                    if (eui != null && !eui.IsShutDown)
                        euiManager?.CloseEui(eui);

                    director?.AdminSetEnabled(false);
                    pair.Server.CfgMan.SetCVar(CCVars.LuaMAiDirectorGameMasterMode, false);

                    if (hadOriginalAdminData && adminData != null)
                    {
                        adminData.Flags = originalFlags;
                        adminData.Active = originalActive;
                    }
                });
            }

            await pair.CleanReturnAsync();
        }
    }
}
