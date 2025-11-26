namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private readonly record struct AssetExplorerEntry(string Name, string Path, bool IsDirectory, long Size, DateTime ModifiedUtc);
    }
}
