#nullable enable

using System.Linq;
using System.Reflection;
using Content.Server._LuaM.Administration;
using Content.Shared._LuaM.Administration;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMGatewayShipPresetTest
{
    [Test]
    public async Task GatewayShipPresetListComesFromSpawnableVesselPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false,
        });

        try
        {
            var server = pair.Server;
            LuaMAiDirectorGatewayShipEntry[] presets = [];

            await server.WaitPost(() =>
            {
                var eui = new LuaMAiDirectorEui();
                var method = typeof(LuaMAiDirectorEui).GetMethod(
                    "BuildGatewayShipPresets",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.That(method, Is.Not.Null);
                presets = (LuaMAiDirectorGatewayShipEntry[]) method!.Invoke(eui, [])!;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(presets.Length, Is.GreaterThan(50));

                var ids = presets.Select(preset => preset.GameMapId).ToHashSet();
                Assert.That(ids, Does.Contain("Baeg"));
                Assert.That(ids, Does.Contain("Twilight"));
                Assert.That(ids, Does.Contain("Triage"));
                Assert.That(ids, Does.Contain("Hammerhead"));
                Assert.That(ids, Does.Contain("Tzipora"));
                Assert.That(ids, Does.Contain("Tokarev"));
                Assert.That(ids, Does.Contain("QJ490"));
                Assert.That(ids, Does.Contain("Kopye"));
                Assert.That(ids, Does.Contain("Kupol"));
                Assert.That(ids, Does.Contain("Molotok"));

                Assert.That(presets, Has.All.Matches<LuaMAiDirectorGatewayShipEntry>(preset =>
                    !string.IsNullOrWhiteSpace(preset.GameMapId) &&
                    !string.IsNullOrWhiteSpace(preset.Name) &&
                    preset.GameMapId.All(c =>
                        c is >= 'a' and <= 'z' ||
                        c is >= 'A' and <= 'Z' ||
                        c is >= '0' and <= '9' ||
                        c is '-' or '_' or '.')));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task GatewayShipPresetValidationAcceptsSafeSpawnableVessels()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false,
        });

        try
        {
            var server = pair.Server;
            var validTwilight = false;
            var validTwilightError = string.Empty;
            string? validTwilightGameMap = null;
            string? validTwilightDisplayName = null;
            var validBaeg = false;
            var validBaegError = string.Empty;
            string? validBaegGameMap = null;
            string? validBaegDisplayName = null;
            var unsafeCommand = true;
            var unsafeCommandError = string.Empty;
            var unsafePath = true;
            var unsafePathError = string.Empty;
            var unknownShip = true;
            var unknownShipError = string.Empty;
            var stationMap = true;
            var stationMapError = string.Empty;
            var customAttachment = false;
            var customAttachmentError = "not called";
            string? customAttachmentGameMap = null;

            await server.WaitPost(() =>
            {
                var eui = new LuaMAiDirectorEui();
                (validTwilight, validTwilightError, var twilightPreset) = ResolveGatewayShipPreset(eui, "Twilight", string.Empty);
                validTwilightGameMap = GetPresetProperty(twilightPreset, "GameMap");
                validTwilightDisplayName = GetPresetProperty(twilightPreset, "DisplayName");

                (validBaeg, validBaegError, var baegPreset) = ResolveGatewayShipPreset(eui, "Baeg", string.Empty);
                validBaegGameMap = GetPresetProperty(baegPreset, "GameMap");
                validBaegDisplayName = GetPresetProperty(baegPreset, "DisplayName");

                (unsafeCommand, unsafeCommandError, _) = ResolveGatewayShipPreset(eui, "Twilight;shutdown", string.Empty);
                (unsafePath, unsafePathError, _) = ResolveGatewayShipPreset(eui, "../Twilight", string.Empty);
                (unknownShip, unknownShipError, _) = ResolveGatewayShipPreset(eui, "DefinitelyMissingShip", string.Empty);
                (stationMap, stationMapError, _) = ResolveGatewayShipPreset(eui, "Saltern", string.Empty);
                (customAttachment, customAttachmentError, var customAttachmentPreset) = ResolveGatewayShipPreset(eui, "Kopye", string.Empty);
                customAttachmentGameMap = GetPresetProperty(customAttachmentPreset, "GameMap");
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(validTwilight, Is.True);
                Assert.That(validTwilightError, Is.EqualTo(string.Empty));
                Assert.That(validTwilightGameMap, Is.EqualTo("Twilight"));
                Assert.That(validTwilightDisplayName, Does.Contain("Twilight"));

                Assert.That(validBaeg, Is.True);
                Assert.That(validBaegError, Is.EqualTo(string.Empty));
                Assert.That(validBaegGameMap, Is.EqualTo("Baeg"));
                Assert.That(validBaegDisplayName, Does.Contain("Baeg"));

                Assert.That(unsafeCommand, Is.False);
                Assert.That(unsafeCommandError, Does.Contain("invalid gameMap id"));
                Assert.That(unsafeCommandError, Does.Contain("Twilight;shutdown"));

                Assert.That(unsafePath, Is.False);
                Assert.That(unsafePathError, Does.Contain("invalid gameMap id"));

                Assert.That(unknownShip, Is.False);
                Assert.That(unknownShipError, Does.Contain("unknown vessel"));

                Assert.That(stationMap, Is.False);
                Assert.That(stationMapError, Does.Contain("unknown vessel"));

                Assert.That(customAttachment, Is.True);
                Assert.That(customAttachmentError, Is.EqualTo(string.Empty));
                Assert.That(customAttachmentGameMap, Is.EqualTo("Kopye"));
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static (bool Success, string Error, object? Preset) ResolveGatewayShipPreset(
        LuaMAiDirectorEui eui,
        string gameMapId,
        string displayName)
    {
        var method = typeof(LuaMAiDirectorEui).GetMethod(
            "TryResolveGatewayShipPreset",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.That(method, Is.Not.Null);

        var args = new object?[] { gameMapId, displayName, null, null };
        var result = (bool) method!.Invoke(eui, args)!;
        return (result, (string) args[3]!, args[2]);
    }

    private static string? GetPresetProperty(object? preset, string propertyName)
    {
        Assert.That(preset, Is.Not.Null);
        return (string?) preset!.GetType().GetProperty(propertyName)!.GetValue(preset);
    }
}
