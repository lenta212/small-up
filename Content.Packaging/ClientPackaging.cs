using System.Diagnostics;
using System.IO.Compression;
using Robust.Packaging;
using Robust.Packaging.AssetProcessing;
using Robust.Packaging.AssetProcessing.Passes;
using Robust.Packaging.Utility;
using Robust.Shared.Timing;

namespace Content.Packaging;

public static class ClientPackaging
{
    private static readonly IReadOnlySet<string> ClientOnlyIgnoredResources = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        // Authoritative progression, expedition generation, examinations, and
        // other server-private data must never be shipped in SS14.Client.zip.
        "ServerOnly",
    };

    /// <summary>
    /// Be advised this can be called from server packaging during a HybridACZ build.
    /// </summary>
    public static async Task PackageClient(bool skipBuild, string configuration, IPackageLogger logger)
    {
        logger.Info("Building client...");

        if (!skipBuild)
        {
            await ProcessHelpers.RunCheck(new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "build",
                    Path.Combine("Content.Client", "Content.Client.csproj"),
                    "-c", configuration,
                    "--nologo",
                    "/v:m",
                    "/t:Rebuild",
                    "/p:FullRelease=true",
                    "/m"
                }
            });
        }

        logger.Info("Packaging client...");

        var sw = RStopwatch.StartNew();
        {
            await using var zipFile =
                File.Open(Path.Combine("release", "SS14.Client.zip"), FileMode.Create, FileAccess.ReadWrite);
            using var zip = new ZipArchive(zipFile, ZipArchiveMode.Update);
            var writer = new AssetPassZipWriter(zip);

            await WriteResources("", writer, logger, default);
            await writer.FinishedTask;
        }

        logger.Info($"Finished packaging client in {sw.Elapsed}");
    }

    public static async Task WriteResources(
        string contentDir,
        AssetPass pass,
        IPackageLogger logger,
        CancellationToken cancel)
    {
        ValidateServerOnlyDirectoryCasing(contentDir);

        var graph = new RobustClientAssetGraph();
        pass.Dependencies.Add(new AssetPassDependency(graph.Output.Name));

        var dropSvgPass = new AssetPassFilterDrop(f => f.Path.EndsWith(".svg"))
        {
            Name = "DropSvgPass",
        };
        dropSvgPass.AddDependency(graph.Input).AddBefore(graph.PresetPasses);

        var dropPrivateBuildArtifactsPass = new AssetPassFilterDrop(ReleaseSurfacePolicy.IsPrivateBuildArtifact)
        {
            Name = "DropPrivateBuildArtifactsPass",
        };
        dropPrivateBuildArtifactsPass.AddDependency(graph.Input).AddBefore(graph.PresetPasses);

        AssetGraph.CalculateGraph([pass, dropSvgPass, dropPrivateBuildArtifactsPass, ..graph.AllPasses], logger);

        var inputPass = graph.Input;

        await RobustSharedPackaging.WriteContentAssemblies(
            inputPass,
            contentDir,
            "Content.Client",
            new[] { "Content.Client", "Content.Shared", "Content.Shared.Database" },
            cancel: cancel);

        await RobustClientPackaging.WriteClientResources(
            contentDir,
            inputPass,
            ClientOnlyIgnoredResources,
            cancel);

        inputPass.InjectFinished();
    }

    private static void ValidateServerOnlyDirectoryCasing(string contentDir)
    {
        var resources = Path.Combine(contentDir, "Resources");
        var matches = Directory
            .EnumerateFileSystemEntries(resources)
            .Where(path => Path.GetFileName(path).Equals("ServerOnly", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();

        if (matches.Length != 1 || !string.Equals(matches[0], "ServerOnly", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resources must contain exactly one canonically-cased ServerOnly directory before client packaging.");
        }
    }
}
