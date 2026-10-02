using XREngine.Data;

/// <summary>Confines publisher platform folders to the caller-owned validation root.</summary>
internal sealed class LocalPlatformPaths(string root) : IRuntimePlatformPaths
{
    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        string path = Path.Combine(root, "platform-folders", folder.ToString());
        Directory.CreateDirectory(path);
        return path;
    }
}
