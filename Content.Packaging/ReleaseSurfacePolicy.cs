using Robust.Packaging.AssetProcessing;

namespace Content.Packaging;

internal static class ReleaseSurfacePolicy
{
    private static readonly string[] PrivateBuildArtifactExtensions =
    {
        ".pdb",
        ".mdb",
        ".cs",
        ".fs",
        ".vb",
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".sln",
        ".suo",
        ".user",
        ".log",
        ".tmp",
        ".cache",
        ".bak",
    };

    private static readonly string[] PrivateBuildArtifactNames =
    {
        ".env",
        ".env.local",
        ".env.production",
        "appsettings.development.json",
        "appsettings.local.json",
        "secrets.json",
        "nuget.config",
    };

    private static readonly string[] PrivateBuildArtifactFragments =
    {
        "/.git/",
        "/.github/",
        "/.vs/",
        "/obj/",
        "/testresults/",
    };

    public static bool IsPrivateBuildArtifact(AssetFile file)
    {
        return IsPrivateBuildArtifactPath(file.Path);
    }

    public static bool IsPrivateBuildArtifactPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lower = $"/{normalized}".ToLowerInvariant();
        var fileName = Path.GetFileName(normalized).ToLowerInvariant();
        var extension = Path.GetExtension(fileName);

        if (PrivateBuildArtifactExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return true;

        if (PrivateBuildArtifactNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            return true;

        return PrivateBuildArtifactFragments.Any(lower.Contains);
    }
}
