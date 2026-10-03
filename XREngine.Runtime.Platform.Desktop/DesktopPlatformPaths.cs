using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Resolves the operating-system folders used by desktop assets and authoring tools.</summary>
internal sealed class DesktopPlatformPaths : IRuntimePlatformPaths
{
    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
}
