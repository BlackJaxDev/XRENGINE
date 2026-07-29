using NUnit.Framework;

namespace XREngine.UnitTests.Scene;

internal sealed class UnityProjectTestSandbox : IDisposable
{
    public UnityProjectTestSandbox()
    {
        RootPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "UnityProjectImport",
            Guid.NewGuid().ToString("N"));
        AssetsPath = Path.Combine(RootPath, "Assets");
        CachePath = Path.Combine(RootPath, "Cache");
        PackagesPath = Path.Combine(RootPath, "Packages");
        ProjectSettingsPath = Path.Combine(RootPath, "ProjectSettings");
        Directory.CreateDirectory(AssetsPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(PackagesPath);
        Directory.CreateDirectory(ProjectSettingsPath);
        File.WriteAllText(
            Path.Combine(ProjectSettingsPath, "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.22f1\n");
    }

    public string RootPath { get; }
    public string AssetsPath { get; }
    public string CachePath { get; }
    public string PackagesPath { get; }
    public string ProjectSettingsPath { get; }

    public string WriteAsset(string portablePath, string contents = "")
    {
        string path = ResolvePortablePath(portablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public string WriteAssetWithMeta(
        string portablePath,
        string guid,
        string contents = "",
        string? importer = null)
    {
        string path = WriteAsset(portablePath, contents);
        WriteMeta(path, guid, importer);
        return path;
    }

    public static void WriteMeta(string assetPath, string guid, string? importer = null)
        => File.WriteAllText(
            $"{assetPath}.meta",
            $"fileFormatVersion: 2\nguid: {guid}\n{importer ?? string.Empty}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup after a test failure.
        }
    }

    private string ResolvePortablePath(string portablePath)
    {
        string normalized = portablePath.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.StartsWith($"Assets{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(RootPath, normalized);
        if (normalized.StartsWith($"Packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(RootPath, normalized);
        return Path.Combine(RootPath, normalized);
    }
}
