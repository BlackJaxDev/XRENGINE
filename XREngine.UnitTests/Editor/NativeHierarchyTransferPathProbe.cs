using XREngine.Data;

namespace XREngine.UnitTests.Editor;

/// <summary>Runs a deterministic callback at the preference save boundary.</summary>
internal sealed class NativeHierarchyTransferPathProbe : IRuntimePlatformPaths
{
    internal Action? OnLookup { get; set; }

    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        OnLookup?.Invoke();
        return string.Empty;
    }
}
