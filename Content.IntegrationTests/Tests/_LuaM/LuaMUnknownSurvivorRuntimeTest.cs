using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Station.Systems;
using Content.Server._LuaM.Sector;
using Content.Shared.Mobs.Systems;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMSectorAiDirectorSystem))]
public sealed class LuaMUnknownSurvivorRuntimeTest
{
    private static readonly string[] SurvivalStateFields =
    [
        "_unknownOperator",
        "_unknownSurvivorClaimed",
        "_unknownSurvivalStage",
        "_unknownSurvivalMistakes",
        "_unknownSurvivalAdviceCount",
    ];

    [Test]
    public async Task RadioAdviceCannotMutateLivingPlayerUnknownSurvivor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var director = entities.System<LuaMSectorAiDirectorSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        var savedState = new Dictionary<string, object?>();
        ICommonSession? session = null;
        EntityUid? previousAttached = null;
        var player = EntityUid.Invalid;

        try
        {
            await server.WaitAssertion(() =>
            {
                CaptureFields(director, SurvivalStateFields, savedState);
                session = server.PlayerMan.Sessions.Single();
                previousAttached = session.AttachedEntity;
                player = entities.SpawnEntity("MobHuman", map.MapCoords);
                Assert.That(server.PlayerMan.SetAttachedEntity(session, player, true), Is.True);
                Assert.That(entities.HasComponent<ActorComponent>(player), Is.True);
                Assert.That(mobState.IsAlive(player), Is.True);

                SetField(director, "_unknownOperator", player);
                // Exercise the Actor guard independently of the spawn-claim flag.
                SetField(director, "_unknownSurvivorClaimed", false);
                SetField(director, "_unknownSurvivalStage",
                    Enum.ToObject(GetField("_unknownSurvivalStage").FieldType, 0));
                SetField(director, "_unknownSurvivalMistakes", 0);
                SetField(director, "_unknownSurvivalAdviceCount", 0);

                var handleAdvice = typeof(LuaMSectorAiDirectorSystem).GetMethod(
                    "HandleUnknownSurvivalAdvice",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(handleAdvice, Is.Not.Null);

                const string dangerousAdvice =
                    "\u043e\u0442\u043a\u0440\u043e\u0439 \u0448\u043b\u044e\u0437";
                for (var i = 0; i < 4; i++)
                {
                    var reply = handleAdvice!.Invoke(director, [dangerousAdvice]);
                    Assert.That(reply, Is.EqualTo(string.Empty));
                }

                Assert.Multiple(() =>
                {
                    Assert.That(mobState.IsAlive(player), Is.True,
                        "Radio advice must never change the MobState of a player-controlled survivor.");
                    Assert.That(GetFieldValue(director, "_unknownSurvivalMistakes"), Is.EqualTo(0));
                    Assert.That(GetFieldValue(director, "_unknownSurvivalAdviceCount"), Is.EqualTo(0));
                    Assert.That(
                        Convert.ToInt32(GetFieldValue(director, "_unknownSurvivalStage")),
                        Is.EqualTo(0));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                RestoreFields(director, savedState);

                if (session != null && session.AttachedEntity != previousAttached)
                    server.PlayerMan.SetAttachedEntity(session, null, true);

                if (session != null &&
                    previousAttached is { } previous &&
                    entities.EntityExists(previous))
                {
                    server.PlayerMan.SetAttachedEntity(session, previous, true);
                }

                if (entities.EntityExists(player))
                    entities.DeleteEntity(player);
            });

            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task CryosleepPreferenceStillSpawnsUnknownSurvivorOnWreck()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var director = entities.System<LuaMSectorAiDirectorSystem>();
        var stationSpawning = entities.System<StationSpawningSystem>();
        var map = await pair.CreateTestMap();

        var stateFields = SurvivalStateFields.Append("_unknownShuttle").ToArray();
        var savedState = new Dictionary<string, object?>();
        var crewServer = EntityUid.Invalid;
        var cryosleepSpawner = EntityUid.Invalid;
        EntityUid? spawned = null;
        EntityUid? wreck = null;

        try
        {
            await server.WaitAssertion(() =>
            {
                CaptureFields(director, stateFields, savedState);
                SetField(director, "_unknownOperator", null);
                SetField(director, "_unknownSurvivorClaimed", false);
                SetField(director, "_unknownShuttle", null);

                crewServer = entities.SpawnEntity("CrewMonitoringServer", map.MapCoords);
                cryosleepSpawner = entities.SpawnEntity("CryogenicSleepUnitSpawner", map.MapCoords);
                var profile = new HumanoidCharacterProfile()
                    .WithSpawnPriorityPreference(SpawnPriorityPreference.Cryosleep);

                spawned = stationSpawning.SpawnPlayerCharacterOnStation(
                    station: null,
                    job: new ProtoId<JobPrototype>("LuaMUnknownSurvivor"),
                    profile: profile);

                Assert.That(spawned, Is.Not.Null);
                wreck = GetFieldValue(director, "_unknownShuttle") is EntityUid wreckUid
                    ? wreckUid
                    : null;
                Assert.That(wreck, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(profile.SpawnPriority, Is.EqualTo(SpawnPriorityPreference.Cryosleep));
                    Assert.That(GetFieldValue(director, "_unknownSurvivorClaimed"), Is.EqualTo(true));
                    Assert.That(
                        entities.GetComponent<TransformComponent>(spawned!.Value).ParentUid,
                        Is.EqualTo(wreck),
                        "Unknown Survivor must claim the spawn before the cryosleep container handler.");
                    Assert.That(
                        entities.GetComponent<TransformComponent>(spawned.Value).ParentUid,
                        Is.Not.EqualTo(cryosleepSpawner));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                RestoreFields(director, savedState);
                DeleteIfExists(entities, spawned);
                DeleteIfExists(entities, wreck);
                DeleteIfExists(entities, cryosleepSpawner);
                DeleteIfExists(entities, crewServer);
            });

            await pair.CleanReturnAsync();
        }
    }

    private static void CaptureFields(
        LuaMSectorAiDirectorSystem director,
        IEnumerable<string> names,
        IDictionary<string, object?> destination)
    {
        foreach (var name in names)
            destination[name] = GetFieldValue(director, name);
    }

    private static void RestoreFields(
        LuaMSectorAiDirectorSystem director,
        IReadOnlyDictionary<string, object?> state)
    {
        foreach (var (name, value) in state)
            SetField(director, name, value);
    }

    private static object? GetFieldValue(LuaMSectorAiDirectorSystem director, string name)
    {
        return GetField(name).GetValue(director);
    }

    private static void SetField(LuaMSectorAiDirectorSystem director, string name, object? value)
    {
        GetField(name).SetValue(director, value);
    }

    private static FieldInfo GetField(string name)
    {
        var field = typeof(LuaMSectorAiDirectorSystem).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}.");
        return field!;
    }

    private static void DeleteIfExists(IEntityManager entities, EntityUid? uid)
    {
        if (uid is { } entity && entities.EntityExists(entity))
            entities.DeleteEntity(entity);
    }
}
