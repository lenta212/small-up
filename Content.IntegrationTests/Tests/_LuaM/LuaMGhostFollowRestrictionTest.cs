#nullable enable
using System.Linq;
using Content.IntegrationTests.Pair;
using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Server.Ghost.Roles;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Follower.Components;
using Content.Shared.Ghost;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMGhostFollowRestrictionTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: LuaMGhostFollowPlainTarget
  components:
  - type: Transform

- type: entity
  id: LuaMGhostFollowRestrictedMob
  components:
  - type: MindContainer
  - type: MobState

- type: entity
  id: LuaMGhostRolePlainTarget
  components:
  - type: GhostRole

- type: entity
  id: LuaMGhostRoleRestrictedMob
  components:
  - type: MindContainer
  - type: MobState
  - type: GhostRole
";

    [Test]
    public async Task RegularGhostFollowVerbCannotTargetMobs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var verbSystem = entMan.System<SharedVerbSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitPost(() =>
        {
            var ghost = entMan.SpawnEntity(GameTicker.ObserverPrototypeName, map.GridCoords);
            var restrictedMob = entMan.SpawnEntity("LuaMGhostFollowRestrictedMob", map.GridCoords);
            var plainTarget = entMan.SpawnEntity("LuaMGhostFollowPlainTarget", map.GridCoords);

            Assert.That(entMan.HasComponent<GhostComponent>(ghost), Is.True);

            var restrictedVerbs = verbSystem.GetLocalVerbs(restrictedMob, ghost, typeof(AlternativeVerb), force: true);
            Assert.That(restrictedVerbs.Any(IsFollowVerb), Is.False,
                "Regular ghosts must not receive the generic Follow verb on mobs/sleepers/players.");

            var plainVerbs = verbSystem.GetLocalVerbs(plainTarget, ghost, typeof(AlternativeVerb), force: true);
            var followVerb = plainVerbs.SingleOrDefault(IsFollowVerb);
            Assert.That(followVerb, Is.Not.Null,
                "The restriction should not remove ordinary ghost following for harmless non-mob targets.");

            followVerb!.Act?.Invoke();
            Assert.That(entMan.TryGetComponent<FollowerComponent>(ghost, out var follower), Is.True);
            Assert.That(follower!.Following, Is.EqualTo(plainTarget));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RegularGhostRoleFollowButtonCannotLocateMobs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            Connected = true,
        });

        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<Robust.Server.Player.IPlayerManager>();
        var adminManager = server.ResolveDependency<IAdminManager>();
        var map = await pair.CreateTestMap();
        var session = playerMan.Sessions.Single();

        await server.WaitPost(() =>
        {
            var adminData = adminManager.GetAdminData(session, includeDeAdmin: true);
            var wasActive = adminData?.Active ?? false;
            if (adminData != null)
                adminData.Active = false;

            var ghost = entMan.SpawnEntity(GameTicker.ObserverPrototypeName, map.GridCoords);
            server.PlayerMan.SetAttachedEntity(session, ghost);

            var restrictedRole = entMan.SpawnEntity("LuaMGhostRoleRestrictedMob", map.GridCoords);
            var plainRole = entMan.SpawnEntity("LuaMGhostRolePlainTarget", map.GridCoords);
            var ghostRoles = entMan.System<GhostRoleSystem>();

            var restrictedId = entMan.GetComponent<GhostRoleComponent>(restrictedRole).Identifier;
            ghostRoles.Follow(session, restrictedId);
            Assert.That(entMan.HasComponent<FollowerComponent>(ghost), Is.False,
                "The global ghost-role Follow button must not be a remote locator for mob/sleeper roles.");

            var plainId = entMan.GetComponent<GhostRoleComponent>(plainRole).Identifier;
            ghostRoles.Follow(session, plainId);
            Assert.That(entMan.TryGetComponent<FollowerComponent>(ghost, out var follower), Is.True);
            Assert.That(follower!.Following, Is.EqualTo(plainRole));

            if (adminData != null)
                adminData.Active = wasActive;
        });

        await pair.CleanReturnAsync();
    }

    private static bool IsFollowVerb(Verb verb)
    {
        return verb is AlternativeVerb && verb.Text == Loc.GetString("verb-follow-text");
    }
}
