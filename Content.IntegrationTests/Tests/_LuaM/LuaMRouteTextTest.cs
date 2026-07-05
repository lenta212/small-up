#nullable enable

using System.Reflection;
using Content.Server._LuaM.Sector;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMRouteTextTest
{
    [Test]
    public void RouteMessageUsesContractCoordinatesWhenPresent()
    {
        var record = new LuaMSectorStoryRecord
        {
            Title = "Depot Run",
            ContractDescription = "GPS 12, 34 near depot A.",
            News = "Fallback GPS 90, 90."
        };

        var message = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "BuildEventRouteTargetMessage",
            record);

        Assert.That(message, Does.Contain("Depot Run"));
        Assert.That(message, Does.Contain("12, 34 near depot A"));
        Assert.That(message, Does.Not.Contain("90, 90"));
    }

    [Test]
    public void RouteMessageFallsBackToNewsCoordinates()
    {
        var record = new LuaMSectorStoryRecord
        {
            Title = "Recovery Run",
            ContractDescription = "No route here.",
            News = "Latest report GPS 44, 55 by the gate."
        };

        var message = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "BuildEventRouteTargetMessage",
            record);

        Assert.That(message, Does.Contain("Recovery Run"));
        Assert.That(message, Does.Contain("44, 55 by the gate"));
    }

    [Test]
    public void RouteResultMentionsTargetAndCoordinates()
    {
        var record = new LuaMSectorStoryRecord
        {
            Title = "Supply Chain",
            ContractDescription = "GPS 77, 88 at the relay.",
            News = string.Empty
        };

        var result = InvokePrivateStatic<string>(
            typeof(LuaMSectorAiDirectorSystem),
            "BuildEventRouteResult",
            record,
            "operator",
            "Test prefix");

        Assert.That(result, Does.Contain("Test prefix"));
        Assert.That(result, Does.Contain("\"Supply Chain\""));
        Assert.That(result, Does.Contain("operator"));
        Assert.That(result, Does.Contain("77, 88 at the relay"));
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, $"Missing private static method {type.Name}.{methodName}");

        return (T) method!.Invoke(null, args)!;
    }
}
