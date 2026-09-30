namespace XREngine.Data;

/// <summary>Resolves platform folders without operating-system queries in portable libraries.</summary>
public interface IRuntimePlatformPaths
{
    string GetFolderPath(Environment.SpecialFolder folder);
}
