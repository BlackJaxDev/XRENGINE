using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;

namespace XREngine.UnitTests.Scene;

internal sealed class UnityAvatarImportTestShaderServices : IRuntimeShaderServices
{
    public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
        => new T();

    public T LoadEngineAsset<T>(
        JobPriority priority,
        bool bypassJobThread,
        string assetRoot,
        string relativePath)
        where T : XRAsset, new()
        => CreateEngineAsset<T>(relativePath);

    public Task<T> LoadEngineAssetAsync<T>(
        JobPriority priority,
        bool bypassJobThread,
        string assetRoot,
        string relativePath)
        where T : XRAsset, new()
        => Task.FromResult(CreateEngineAsset<T>(relativePath));

    public void LogWarning(string message)
    {
    }

    private static T CreateEngineAsset<T>(string relativePath) where T : XRAsset, new()
    {
        if (typeof(T) != typeof(XRShader))
            return new T();

        string fullPath = ResolveWorkspacePath(
            Path.Combine("Build", "CommonAssets", "Shaders", relativePath));
        TextFile source = new(fullPath)
        {
            Text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "void main() {}\n",
        };
        XRShader shader = new(XRShader.ResolveType(Path.GetExtension(relativePath)), source)
        {
            FilePath = fullPath,
            Name = Path.GetFileName(relativePath),
        };
        return (T)(XRAsset)shader;
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return relativePath;
    }
}

