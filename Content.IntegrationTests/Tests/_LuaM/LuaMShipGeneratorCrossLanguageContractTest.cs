using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Content.Server._LuaM.ShipGen;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipGeneratorCrossLanguageContractTest
{
    private const string FixturePath = "Tools/fixtures/luam_ship_generator_contract_v1.json";

    [Test]
    public void DeterministicPythonBlueprintMatrixIsAcceptedByServerValidator()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FullPath(FixturePath)));
        var root = fixture.RootElement;
        Assert.That(root.GetProperty("fixtureVersion").GetInt32(), Is.EqualTo(1));
        var seed = root.GetProperty("seed").GetString();
        Assert.That(seed, Is.Not.Null.And.Not.Empty);

        var generatorType = typeof(LuaMShipGeneratorSystem);
        var optionsField = generatorType.GetField(
            "JsonOptions",
            BindingFlags.Static | BindingFlags.NonPublic);
        var validate = generatorType.GetMethod(
            "TryValidateBlueprint",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.Multiple(() =>
        {
            Assert.That(optionsField, Is.Not.Null, "Production JSON options must remain discoverable by the contract test.");
            Assert.That(validate, Is.Not.Null, "Server blueprint validator was not found.");
        });
        var jsonOptions = (JsonSerializerOptions) optionsField!.GetValue(null)!;

        var observedCases = new List<string>();
        var failures = new List<string>();
        foreach (var contractCase in root.GetProperty("cases").EnumerateArray())
        {
            var preset = contractCase.GetProperty("preset").GetString()!;
            var size = contractCase.GetProperty("size").GetString()!;
            var caseName = $"{preset}/{size}";
            observedCases.Add(caseName);

            LuaMShipBlueprintResponse response;
            try
            {
                response = contractCase.GetProperty("blueprint")
                    .Deserialize<LuaMShipBlueprintResponse>(jsonOptions);
            }
            catch (JsonException exception)
            {
                failures.Add($"{caseName}: production JSON contract rejected fixture: {exception.Message}");
                continue;
            }

            if (response == null)
            {
                failures.Add($"{caseName}: deserialized to null");
                continue;
            }

            var request = new LuaMShipGenerationRequest(preset, size, seed!, string.Empty);
            object[] arguments = [request, response, null, null];
            var accepted = (bool) validate!.Invoke(null, arguments)!;
            if (!accepted)
                failures.Add($"{caseName}: {arguments[3]}");
        }

        var expectedCases = new[]
        {
            "expedition/small",
            "expedition/medium",
            "expedition/large",
            "fighter/small",
            "fighter/medium",
            "fighter/large",
            "salvage/small",
            "salvage/medium",
            "salvage/large",
        };
        Assert.Multiple(() =>
        {
            Assert.That(observedCases, Is.EqualTo(expectedCases), "Fixture must cover the complete 3 x 3 matrix once.");
            Assert.That(failures, Is.Empty, string.Join(System.Environment.NewLine, failures));
        });
    }

    private static string FullPath(string relativePath)
    {
        var root = Path.GetFullPath(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", ".."));
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
