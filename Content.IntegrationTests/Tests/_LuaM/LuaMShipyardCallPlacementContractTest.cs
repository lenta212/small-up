using System.IO;
using Content.Server._NF.Shipyard.Systems;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[TestOf(typeof(ShipyardSystem))]
public sealed class LuaMShipyardCallPlacementContractTest
{
    [Test]
    public void ProximityFallbackReportsNearbyPlacementWithoutClaimingSelectedGate()
    {
        var source = ReadSource("Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs");
        var call = Slice(
            source,
            "private async Task HandleCallShipMessageAsync(",
            "private bool IsPersistentShipCallTargetCurrent(");
        var proximitySuccess = Slice(
            call,
            "if (_shuttle.TryFTLProximity(restored, stationGrid))",
            "_sawmill.Error(");
        var successPopup = Slice(
            call,
            "var successLocId = GetPersistentShipCallSuccessLocId(usedProximityFallback);",
            "PlayConfirmSound(player, uid, component);");

        Assert.Multiple(() =>
        {
            Assert.That(proximitySuccess, Does.Contain("usedProximityFallback = true;"));
            Assert.That(proximitySuccess, Does.Contain("return true;"));
            Assert.That(successPopup, Does.Contain("if (usedProximityFallback)"));
            Assert.That(successPopup, Does.Contain("ConsolePopup(player, Loc.GetString(successLocId));"),
                "The proximity-success popup must not claim or interpolate the selected gate.");
            Assert.That(
                successPopup,
                Does.Contain("ConsolePopup(player, Loc.GetString(successLocId, (\"gate\", gateName)));"),
                "Only the actual docking branch may interpolate the selected gate.");
        });
    }

    [TestCase(false, "shipyard-console-call-success")]
    [TestCase(true, "shipyard-console-call-success-nearby")]
    public void SuccessLocalizationMatchesActualPlacement(
        bool usedProximityFallback,
        string expectedLocId)
    {
        Assert.That(
            ShipyardSystem.GetPersistentShipCallSuccessLocId(usedProximityFallback),
            Is.EqualTo(expectedLocId));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing start marker: {startMarker}");
        Assert.That(end, Is.GreaterThan(start), $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate repository root from test output directory.");
        return string.Empty;
    }
}
