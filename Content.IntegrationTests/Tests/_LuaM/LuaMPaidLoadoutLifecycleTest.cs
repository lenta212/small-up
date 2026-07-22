#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Mono.MonoCoins;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Stacks;
using NUnit.Framework;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMPaidLoadoutLifecycleTest
{
    private const string PaidLoadout = "LuaMPaidLoadoutPositive";

    [TestPrototypes]
    private const string Prototypes = @"
- type: playTimeTracker
  id: PlayTimeLuaMPaidLoadoutSafety

- type: playTimeTracker
  id: PlayTimeLuaMPaidLoadoutProduction

- type: loadout
  id: LuaMPaidLoadoutPositive
  price: 100
  equipment:
    gloves: ClothingHandsGlovesColorBlack

- type: loadout
  id: LuaMPaidLoadoutFree
  price: 0
  equipment:
    jumpsuit: ClothingUniformJumpsuitColorGrey

- type: loadout
  id: LuaMPaidLoadoutNegative
  price: -50
  equipment:
    shoes: ClothingShoesColorBlack

- type: loadout
  id: LuaMPaidLoadoutMandatoryFallback
  price: 0
  equipment:
    shoes: ClothingShoesColorWhite

- type: loadout
  id: LuaMPaidLoadoutStorage
  price: 100
  storage:
    back:
    - LuaMPaidLoadoutStorageValue

- type: entity
  id: LuaMPaidLoadoutStorageValue
  name: paid loadout storage value

- type: loadoutGroup
  id: LuaMPaidLoadoutPositiveGroup
  name: generic-unknown
  minLimit: 1
  maxLimit: 1
  loadouts:
  - LuaMPaidLoadoutPositive
  fallbacks:
  - LuaMPaidLoadoutPositive

- type: loadoutGroup
  id: LuaMPaidLoadoutFreeGroup
  name: generic-unknown
  minLimit: 0
  maxLimit: 1
  loadouts:
  - LuaMPaidLoadoutFree

- type: loadoutGroup
  id: LuaMPaidLoadoutNegativeGroup
  name: generic-unknown
  minLimit: 1
  maxLimit: 1
  loadouts:
  - LuaMPaidLoadoutNegative
  fallbacks:
  - LuaMPaidLoadoutNegative

- type: loadoutGroup
  id: LuaMPaidLoadoutProductionGroup
  name: generic-unknown
  minLimit: 1
  maxLimit: 1
  loadouts:
  - LuaMPaidLoadoutPositive
  fallbacks:
  - LuaMPaidLoadoutMandatoryFallback

- type: roleLoadout
  id: JobLuaMPaidLoadoutSafety
  groups:
  - LuaMPaidLoadoutPositiveGroup
  - LuaMPaidLoadoutFreeGroup
  - LuaMPaidLoadoutNegativeGroup

- type: roleLoadout
  id: JobLuaMPaidLoadoutProduction
  groups:
  - LuaMPaidLoadoutProductionGroup

- type: job
  id: LuaMPaidLoadoutSafety
  playTimeTracker: PlayTimeLuaMPaidLoadoutSafety

- type: job
  id: LuaMPaidLoadoutProduction
  playTimeTracker: PlayTimeLuaMPaidLoadoutProduction

- type: stack
  id: LuaMStackGuardStack
  spawn: LuaMStackGuardUnit
  maxCount: 10

- type: entity
  id: LuaMStackGuardUnit
  components:
  - type: Stack
    stackType: LuaMStackGuardStack
    count: 1

- type: stack
  id: LuaMStackInvalidMaxStack
  spawn: LuaMStackInvalidMaxUnit
  maxCount: 0

- type: entity
  id: LuaMStackInvalidMaxUnit
  components:
  - type: Stack
    stackType: LuaMStackInvalidMaxStack
    count: 1
";

    [Test]
    public async Task FreeLoadoutEquipsButNegativePriceFailsClosed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        try
        {
            var server = pair.Server;
            var entMan = server.ResolveDependency<IEntityManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var map = await pair.CreateTestMap();

            await server.WaitAssertion(() =>
            {
                var profile = new HumanoidCharacterProfile();
                var loadout = new RoleLoadout("JobLuaMPaidLoadoutSafety");
                loadout.SelectedLoadouts["LuaMPaidLoadoutPositiveGroup"] = new List<Loadout>();
                loadout.SelectedLoadouts["LuaMPaidLoadoutFreeGroup"] = new List<Loadout>
                {
                    new() { Prototype = "LuaMPaidLoadoutFree" },
                };
                loadout.SelectedLoadouts["LuaMPaidLoadoutNegativeGroup"] = new List<Loadout>
                {
                    new() { Prototype = "LuaMPaidLoadoutNegative" },
                };
                profile.SetLoadout(loadout);

                var actor = spawning.SpawnPlayerMob(
                    map.GridCoords,
                    "LuaMPaidLoadoutSafety",
                    profile,
                    station: null);

                Assert.Multiple(() =>
                {
                    Assert.That(inventory.TryGetSlotEntity(actor, "jumpsuit", out var jumpsuit), Is.True);
                    Assert.That(PrototypeId(entMan, jumpsuit), Is.EqualTo("ClothingUniformJumpsuitColorGrey"));
                    Assert.That(inventory.TryGetSlotEntity(actor, "shoes", out _), Is.False,
                        "A negative price must never be interpreted as free value.");
                    Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out _), Is.False,
                        "A positive-price fallback must never materialize without a durable debit.");
                });

                entMan.DeleteEntity(actor);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task DebitDoesNotStartBeforeExactAttachment()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            var entMan = server.ResolveDependency<IEntityManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var map = await pair.CreateTestMap();
            EntityUid actor = default;
            PendingPaidLoadoutComponent? pending = null;
            var debitCalls = 0;
            Task<bool>? result = null;

            await server.WaitPost(() =>
            {
                if (session.AttachedEntity != null)
                    playerMan.SetAttachedEntity(session, null, true);

                Assert.That(session.AttachedEntity, Is.Null);
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(spawning, pending, session, slot: 0, profileId: 1);
                result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, finalize =>
                {
                    debitCalls++;
                    return Task.FromResult(finalize());
                });
            });

            var processed = await result!;
            Assert.Multiple(() =>
            {
                Assert.That(processed, Is.False);
                Assert.That(debitCalls, Is.Zero);
                Assert.That(pending!.Started, Is.False,
                    "A pre-attachment probe must leave the pending operation available for the real attach event.");
                Assert.That(entMan.HasComponent<PendingPaidLoadoutComponent>(actor), Is.True);
            });

            await server.WaitPost(() => entMan.DeleteEntity(actor));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SyncAndDelayedDebitEquipOnceAfterCommit(bool delayed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var probe = entMan.System<LuaMPaidLoadoutProbeSystem>();
            var map = await pair.CreateTestMap();
            var (slot, profileId) = await GetSelectedProfileIdentity(server, preferences, session);

            EntityUid actor = default;
            Func<bool>? finalizer = null;
            Task<bool>? result = null;
            TaskCompletionSource<bool>? debitCompletion = null;
            var duplicateFinalizerResult = true;

            await server.WaitPost(() =>
            {
                probe.Reset();
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                playerMan.SetAttachedEntity(session, actor);
                var pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(spawning, pending, session, slot, profileId);

                if (delayed)
                {
                    debitCompletion = new TaskCompletionSource<bool>();
                    result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, finalize =>
                    {
                        finalizer = finalize;
                        return debitCompletion.Task;
                    });
                }
                else
                {
                    result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, finalize =>
                    {
                        var finalized = finalize();
                        duplicateFinalizerResult = finalize();
                        return Task.FromResult(finalized);
                    });
                }
            });

            if (delayed)
            {
                await server.WaitAssertion(() =>
                {
                    Assert.That(finalizer, Is.Not.Null);
                    Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out _), Is.False,
                        "Paid value must remain unmaterialized while the durable debit is pending.");
                    Assert.That(probe.Count(actor), Is.Zero);
                });

                await server.WaitPost(() =>
                {
                    var finalized = finalizer!();
                    duplicateFinalizerResult = finalizer();
                    debitCompletion!.SetResult(finalized);
                });
            }

            Assert.That(await result!, Is.True);
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out var gloves), Is.True);
                    Assert.That(PrototypeId(entMan, gloves), Is.EqualTo("ClothingHandsGlovesColorBlack"));
                    Assert.That(probe.Count(actor), Is.EqualTo(1),
                        "Post-paid completion must be published exactly once.");
                    Assert.That(duplicateFinalizerResult, Is.False,
                        "A duplicate bank callback must not equip or publish again.");
                    Assert.That(entMan.HasComponent<PendingPaidLoadoutComponent>(actor), Is.False);
                });
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StaleOrDeletedIdentityCompensatesWithoutGear(bool deleteActor)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var probe = entMan.System<LuaMPaidLoadoutProbeSystem>();
            var map = await pair.CreateTestMap();
            var (slot, profileId) = await GetSelectedProfileIdentity(server, preferences, session);

            EntityUid actor = default;
            Func<bool>? finalizer = null;
            Task<bool>? result = null;
            TaskCompletionSource<bool>? debitCompletion = null;
            var compensated = false;
            const int initialBalance = 1_000;
            var authoritativeBalance = initialBalance;

            await server.WaitPost(() =>
            {
                probe.Reset();
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                playerMan.SetAttachedEntity(session, actor);
                var pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(spawning, pending, session, slot, profileId);
                debitCompletion = new TaskCompletionSource<bool>();
                result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, finalize =>
                {
                    authoritativeBalance -= pending.Cost;
                    finalizer = () =>
                    {
                        var finalized = finalize();
                        if (!finalized)
                        {
                            authoritativeBalance += pending.Cost;
                            compensated = true;
                        }

                        return finalized;
                    };
                    return debitCompletion.Task;
                });
            });

            await server.WaitPost(() =>
            {
                Assert.That(finalizer, Is.Not.Null);
                if (deleteActor)
                    entMan.DeleteEntity(actor);
                else
                    playerMan.SetAttachedEntity(session, null, true);

                var finalized = finalizer!();
                debitCompletion!.SetResult(finalized);
            });

            Assert.That(await result!, Is.False);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(compensated, Is.True,
                        "The debit owner must compensate when its post-commit finalizer rejects stale identity.");
                    Assert.That(authoritativeBalance, Is.EqualTo(initialBalance),
                        "A rejected finalizer must restore the authoritative debit exactly once.");
                    Assert.That(probe.Count(actor), Is.Zero);
                    if (!deleteActor)
                        Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out _), Is.False);
                });
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task ReplacedProfileFailsBeforeDebit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var probe = entMan.System<LuaMPaidLoadoutProbeSystem>();
            var map = await pair.CreateTestMap();
            var (slot, profileId) = await GetSelectedProfileIdentity(server, preferences, session);
            EntityUid actor = default;
            var debitCalls = 0;
            Task<bool>? result = null;

            await server.WaitPost(() =>
            {
                probe.Reset();
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                playerMan.SetAttachedEntity(session, actor);
                var pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(spawning, pending, session, slot, checked(profileId + 1));
                result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, finalize =>
                {
                    debitCalls++;
                    return Task.FromResult(finalize());
                });
            });

            Assert.That(await result!, Is.False);
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(debitCalls, Is.Zero);
                    Assert.That(probe.Count(actor), Is.Zero);
                    Assert.That(entMan.HasComponent<PendingPaidLoadoutComponent>(actor), Is.False);
                });
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task SamePriceMaterializationMutationFailsBeforeDebit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var map = await pair.CreateTestMap();
            var (slot, profileId) = await GetSelectedProfileIdentity(server, preferences, session);
            EntityUid actor = default;
            Task<bool>? result = null;
            var debitCalls = 0;

            await server.WaitPost(() =>
            {
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                playerMan.SetAttachedEntity(session, actor);
                var pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(spawning, pending, session, slot, profileId);

                var loadout = prototypes.Index<LoadoutPrototype>(PaidLoadout);
                var original = loadout.Equipment["gloves"];
                loadout.Equipment["gloves"] = "ClothingHandsGlovesColorWhite";
                try
                {
                    result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, finalize =>
                    {
                        debitCalls++;
                        return Task.FromResult(finalize());
                    });
                }
                finally
                {
                    loadout.Equipment["gloves"] = original;
                }
            });

            Assert.That(await result!, Is.False);
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(debitCalls, Is.Zero,
                        "A same-price value mutation must invalidate the quote before debit.");
                    Assert.That(entMan.HasComponent<PendingPaidLoadoutComponent>(actor), Is.False);
                });
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task MissingStorageTargetDeliversPaidValueAtPlayerLocation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var map = await pair.CreateTestMap();
            var (slot, profileId) = await GetSelectedProfileIdentity(server, preferences, session);
            EntityUid actor = default;
            Task<bool>? result = null;

            await server.WaitPost(() =>
            {
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                playerMan.SetAttachedEntity(session, actor);
                Assert.That(inventory.TryGetSlotEntity(actor, "back", out _), Is.False);
                var pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(
                    spawning,
                    pending,
                    session,
                    slot,
                    profileId,
                    paidLoadout: "LuaMPaidLoadoutStorage");
                result = spawning.ProcessPendingPaidLoadoutAsync(
                    actor,
                    pending,
                    finalize => Task.FromResult(finalize()));
            });

            Assert.That(await result!, Is.True);
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.That(EntitiesWithPrototype(entMan, "LuaMPaidLoadoutStorageValue"), Has.Count.EqualTo(1),
                    "Missing storage must fall back to a real world delivery rather than silently charging.");
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task DefiniteDebitFailureDeliversMandatoryFreeFallback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var map = await pair.CreateTestMap();
            var (slot, profileId) = await GetSelectedProfileIdentity(server, preferences, session);
            EntityUid actor = default;
            Task<bool>? result = null;
            var debitCalls = 0;

            await server.WaitPost(() =>
            {
                actor = entMan.SpawnEntity("MobHuman", map.GridCoords);
                playerMan.SetAttachedEntity(session, actor);
                var pending = entMan.AddComponent<PendingPaidLoadoutComponent>(actor);
                ConfigurePending(
                    spawning,
                    pending,
                    session,
                    slot,
                    profileId,
                    fallbackLoadout: "LuaMPaidLoadoutMandatoryFallback");
                result = spawning.ProcessPendingPaidLoadoutAsync(actor, pending, _ =>
                {
                    debitCalls++;
                    return Task.FromResult(false);
                });
            });

            Assert.That(await result!, Is.False);
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(debitCalls, Is.EqualTo(1));
                    Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out _), Is.False);
                    Assert.That(inventory.TryGetSlotEntity(actor, "shoes", out var shoes), Is.True);
                    Assert.That(PrototypeId(entMan, shoes), Is.EqualTo("ClothingShoesColorWhite"));
                });
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task ProductionSpawnCapturesMandatoryFallbackForDefiniteDebitFailure()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var map = await pair.CreateTestMap();

            var slot = -1;
            HumanoidCharacterProfile? current = null;
            await server.WaitAssertion(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                slot = cached.SelectedCharacterIndex;
                current = (HumanoidCharacterProfile) cached.Characters[slot];
            });

            var roleLoadout = new RoleLoadout("JobLuaMPaidLoadoutProduction");
            roleLoadout.SelectedLoadouts["LuaMPaidLoadoutProductionGroup"] = new List<Loadout>
            {
                new() { Prototype = PaidLoadout },
            };
            var paidProfile = current!
                .WithBankBalance(1_000)
                .WithLoadout(roleLoadout);
            Task? saveProfile = null;
            await server.WaitPost(() =>
                saveProfile = preferences.SetProfile(session.UserId, slot, paidProfile));
            await saveProfile!;

            HumanoidCharacterProfile? cachedPaidProfile = null;
            await server.WaitAssertion(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                cachedPaidProfile = (HumanoidCharacterProfile) cached.Characters[slot];
            });

            EntityUid actor = default;
            Task<bool>? result = null;
            await server.WaitPost(() =>
            {
                if (session.AttachedEntity != null)
                    playerMan.SetAttachedEntity(session, null, true);

                actor = spawning.SpawnPlayerMob(
                    map.GridCoords,
                    "LuaMPaidLoadoutProduction",
                    cachedPaidProfile,
                    station: null,
                    session: session);
                var pending = entMan.GetComponent<PendingPaidLoadoutComponent>(actor);
                Assert.That(pending.FallbackGear, Has.Count.EqualTo(1),
                    "The production selection path must preserve the mandatory free fallback while paid value is staged.");

                // Suppress the automatic real debit for this definite-failure
                // branch, then exercise the exact component captured by spawn.
                pending.Started = true;
                playerMan.SetAttachedEntity(session, actor);
                pending.Started = false;
                result = spawning.ProcessPendingPaidLoadoutAsync(
                    actor,
                    pending,
                    _ => Task.FromResult(false));
            });

            Assert.That(await result!, Is.False);
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out _), Is.False);
                    Assert.That(inventory.TryGetSlotEntity(actor, "shoes", out var shoes), Is.True);
                    Assert.That(PrototypeId(entMan, shoes), Is.EqualTo("ClothingShoesColorWhite"));
                });
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task SpawnAttachProductionPathDurablyDebitsAndDelivers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
            Fresh = true,
        });

        ICommonSession? attachedSession = null;
        try
        {
            var server = pair.Server;
            var session = GetServerSession(pair);
            attachedSession = session;
            var entMan = server.ResolveDependency<IEntityManager>();
            var preferences = server.ResolveDependency<IServerPreferencesManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var coins = server.ResolveDependency<MonoCoinsManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var spawning = entMan.System<StationSpawningSystem>();
            var inventory = entMan.System<InventorySystem>();
            var map = await pair.CreateTestMap();

            var slot = -1;
            HumanoidCharacterProfile? current = null;
            await server.WaitAssertion(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                slot = cached.SelectedCharacterIndex;
                current = (HumanoidCharacterProfile) cached.Characters[slot];
            });

            var roleLoadout = new RoleLoadout("JobLuaMPaidLoadoutProduction");
            roleLoadout.SelectedLoadouts["LuaMPaidLoadoutProductionGroup"] = new List<Loadout>
            {
                new() { Prototype = PaidLoadout },
            };
            var paidProfile = current!
                .WithBankBalance(1_000)
                .WithLoadout(roleLoadout);
            Task? saveProfile = null;
            await server.WaitPost(() =>
                saveProfile = preferences.SetProfile(session.UserId, slot, paidProfile));
            await saveProfile!;

            var profileId = 0;
            HumanoidCharacterProfile? cachedPaidProfile = null;
            await server.WaitAssertion(() =>
            {
                var cached = preferences.GetPreferences(session.UserId);
                cachedPaidProfile = (HumanoidCharacterProfile) cached.Characters[slot];
                Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out profileId), Is.True);
            });

            var bankBefore = await db.GetCharacterBankBalanceAsync(session.UserId, profileId, slot);
            var coinsBefore = await coins.GetMonoCoinsBalanceAsync(session.UserId);
            Assert.That(bankBefore, Is.Not.Null);

            EntityUid actor = default;
            await server.WaitPost(() =>
            {
                if (session.AttachedEntity != null)
                    playerMan.SetAttachedEntity(session, null, true);

                actor = spawning.SpawnPlayerMob(
                    map.GridCoords,
                    "LuaMPaidLoadoutProduction",
                    cachedPaidProfile,
                    station: null,
                    session: session);
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.HasComponent<PendingPaidLoadoutComponent>(actor), Is.True);
                    Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out _), Is.False);
                });
                playerMan.SetAttachedEntity(session, actor);
            });

            await PoolManager.WaitUntil(
                server,
                () => !entMan.HasComponent<PendingPaidLoadoutComponent>(actor) &&
                      inventory.TryGetSlotEntity(actor, "gloves", out _),
                maxTicks: 600);

            var bankAfter = await db.GetCharacterBankBalanceAsync(session.UserId, profileId, slot);
            var coinsAfter = await coins.GetMonoCoinsBalanceAsync(session.UserId);
            Assert.That(bankAfter, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That((bankBefore!.Value - bankAfter!.Value) + (coinsBefore - coinsAfter), Is.EqualTo(100));
                Assert.That(inventory.TryGetSlotEntity(actor, "gloves", out var gloves), Is.True);
                Assert.That(PrototypeId(entMan, gloves), Is.EqualTo("ClothingHandsGlovesColorBlack"));
            });
        }
        finally
        {
            await Detach(pair, attachedSession);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task StackBulkSpawnRejectsBeforeOversizedAllocation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        try
        {
            var server = pair.Server;
            var entMan = server.ResolveDependency<IEntityManager>();
            var stackSystem = entMan.System<StackSystem>();
            var map = await pair.CreateTestMap();

            await server.WaitAssertion(() =>
            {
                var oversizedAmount = checked(StackSystem.MaxSpawnedEntitiesPerCall * 10 + 1);
                Assert.Multiple(() =>
                {
                    Assert.Throws<InvalidOperationException>(() => stackSystem.SpawnMultiple(
                        "LuaMStackGuardUnit",
                        oversizedAmount,
                        map.GridCoords));
                    Assert.That(EntitiesWithPrototype(entMan, "LuaMStackGuardUnit"), Is.Empty,
                        "The cap must be checked before creating any payout entity.");
                    Assert.Throws<InvalidOperationException>(() => stackSystem.SpawnMultiple(
                        "LuaMStackInvalidMaxUnit",
                        1,
                        map.GridCoords));
                    Assert.That(EntitiesWithPrototype(entMan, "LuaMStackInvalidMaxUnit"), Is.Empty,
                        "An invalid maximum stack size must fail before spawning.");
                });

                var valid = stackSystem.SpawnMultiple("LuaMStackGuardUnit", 21, map.GridCoords);
                Assert.Multiple(() =>
                {
                    Assert.That(valid, Has.Count.EqualTo(3));
                    Assert.That(valid.Sum(uid => entMan.GetComponent<StackComponent>(uid).Count), Is.EqualTo(21));
                });

                foreach (var uid in valid)
                    entMan.DeleteEntity(uid);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static void ConfigurePending(
        StationSpawningSystem spawning,
        PendingPaidLoadoutComponent component,
        ICommonSession session,
        int slot,
        int profileId,
        string paidLoadout = PaidLoadout,
        string? fallbackLoadout = null)
    {
        component.ExpectedSession = session;
        component.ExpectedSlot = slot;
        component.ExpectedProfileId = profileId;
        component.PrototypeRevision = spawning.PaidLoadoutPrototypeRevision;
        var paidEntry = spawning.CapturePendingPaidLoadoutEntry(paidLoadout);
        component.Cost = paidEntry.Price;
        component.Gear.Add(paidEntry);
        if (fallbackLoadout != null)
            component.FallbackGear.Add(spawning.CapturePendingPaidLoadoutEntry(fallbackLoadout));
    }

    private static ICommonSession GetServerSession(Content.IntegrationTests.Pair.TestPair pair)
    {
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var playerMan = pair.Server.ResolveDependency<IPlayerManager>();
        return playerMan.GetSessionById(clientSession!.UserId);
    }

    private static async Task<(int Slot, int ProfileId)> GetSelectedProfileIdentity(
        RobustIntegrationTest.ServerIntegrationInstance server,
        IServerPreferencesManager preferences,
        ICommonSession session)
    {
        var slot = -1;
        var profileId = 0;
        await server.WaitAssertion(() =>
        {
            var cached = preferences.GetPreferences(session.UserId);
            slot = cached.SelectedCharacterIndex;
            Assert.That(preferences.TryGetCharacterProfileId(session.UserId, slot, out profileId), Is.True);
        });

        return (slot, profileId);
    }

    private static string? PrototypeId(IEntityManager entMan, EntityUid? uid)
    {
        return uid is { } entity
            ? entMan.GetComponent<MetaDataComponent>(entity).EntityPrototype?.ID
            : null;
    }

    private static List<EntityUid> EntitiesWithPrototype(IEntityManager entMan, string prototype)
    {
        return entMan.AllComponents<MetaDataComponent>()
            .Where(meta => meta.Component.EntityPrototype?.ID == prototype)
            .Select(meta => meta.Uid)
            .ToList();
    }

    private static async Task Detach(Content.IntegrationTests.Pair.TestPair pair, ICommonSession? session)
    {
        if (session == null)
            return;

        await pair.Server.WaitPost(() =>
        {
            if (session.AttachedEntity != null)
                pair.Server.PlayerMan.SetAttachedEntity(session, null, true);
        });
    }
}

public sealed class LuaMPaidLoadoutProbeSystem : EntitySystem
{
    private readonly Dictionary<EntityUid, int> _counts = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MetaDataComponent, PaidLoadoutEquippedEvent>(OnPaidLoadoutEquipped);
    }

    public void Reset()
    {
        _counts.Clear();
    }

    public int Count(EntityUid entity)
    {
        return _counts.GetValueOrDefault(entity);
    }

    private void OnPaidLoadoutEquipped(
        EntityUid uid,
        MetaDataComponent component,
        ref PaidLoadoutEquippedEvent args)
    {
        _counts[uid] = Count(uid) + 1;
    }
}
