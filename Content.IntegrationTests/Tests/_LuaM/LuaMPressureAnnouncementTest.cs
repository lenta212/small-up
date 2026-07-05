#nullable enable

using System;
using System.Reflection;
using Content.Server._LuaM.Sector;
using Content.Shared._LuaM.Sector;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMPressureAnnouncementTest
{
    [Test]
    public async System.Threading.Tasks.Task PressureAnnouncementLocIdCyclesWithPhaseAndSeverity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var entMan = pair.Server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorDynamicEventSystem>();

            var condition = new LuaMSectorConditionStatus(
                "pressure",
                "Sector pressure",
                3,
                "Pressure is rising.",
                "integration-test",
                true);

            SetPrivateField(director, "_pressureAnnouncementPhase", 0);
            var phase0 = InvokePrivate<string>(director, "GetPressureAnnouncementLocId", condition.Severity);

            SetPrivateField(director, "_pressureAnnouncementPhase", 1);
            var phase1 = InvokePrivate<string>(director, "GetPressureAnnouncementLocId", condition.Severity);

            SetPrivateField(director, "_pressureAnnouncementPhase", 6);
            var phase6 = InvokePrivate<string>(director, "GetPressureAnnouncementLocId", condition.Severity);

            var severity1 = InvokePrivate<string>(director, "GetPressureAnnouncementLocId", 1);
            var severity5 = InvokePrivate<string>(director, "GetPressureAnnouncementLocId", 5);

            Assert.That(phase0, Is.Not.Empty);
            Assert.That(phase1, Is.Not.Empty);
            Assert.That(phase6, Is.Not.Empty);
            Assert.That(phase0, Is.Not.EqualTo(phase1));
            Assert.That(phase1, Is.Not.EqualTo(phase6));
            Assert.That(severity1, Is.Not.Empty);
            Assert.That(severity5, Is.Not.Empty);
            Assert.That(severity1, Is.Not.EqualTo(severity5));
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async System.Threading.Tasks.Task PressureAnnouncementIntervalStaysWithinConfiguredBounds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false
        });

        try
        {
            var entMan = pair.Server.ResolveDependency<IEntityManager>();
            var director = entMan.System<LuaMSectorDynamicEventSystem>();

            var min = LuaMSectorDynamicEventSystem.PressureAnnouncementMinIntervalSeconds;
            var max = LuaMSectorDynamicEventSystem.PressureAnnouncementMaxIntervalSeconds;

            for (var severity = 1; severity <= 5; severity++)
            {
                var interval = InvokePrivate<TimeSpan>(director, "GetPressureAnnouncementInterval", severity);
                Assert.That(interval.TotalSeconds, Is.InRange(min, max));
            }
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static TResult InvokePrivate<TResult>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"Missing private method {instance.GetType().Name}.{methodName}");
        return (TResult) method!.Invoke(instance, args)!;
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Missing private field {instance.GetType().Name}.{fieldName}");
        field!.SetValue(instance, value);
    }
}
