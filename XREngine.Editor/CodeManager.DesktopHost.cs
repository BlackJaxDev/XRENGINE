using XREngine;

internal partial class CodeManager
{
    /// <summary>Resolves the optional desktop-only composition project without adding it to gameplay.</summary>
    private static string? ResolveDesktopHostProject()
    {
        string? path = Engine.CurrentProject?.DesktopHostProjectPath;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Path.IsPathRooted(path) || Uri.TryCreate(path, UriKind.Absolute, out _))
            throw new InvalidOperationException("DesktopHostProjectPath must be relative to the authored project.");
        string root = Engine.CurrentProject?.ProjectDirectory
            ?? throw new InvalidOperationException("A desktop host project requires a saved XRProject.");
        string resolved = Path.GetFullPath(Path.Combine(root, path));
        if (!string.Equals(Path.GetExtension(resolved), ".csproj", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(resolved))
            throw new FileNotFoundException("The configured desktop host project does not exist.", resolved);
        return resolved;
    }
}
