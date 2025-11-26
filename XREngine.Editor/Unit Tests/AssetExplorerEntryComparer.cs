namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private sealed class AssetExplorerEntryComparer : IComparer<AssetExplorerEntry>
        {
            public static readonly AssetExplorerEntryComparer Instance = new();

            public int Compare(AssetExplorerEntry x, AssetExplorerEntry y)
            {
                if (x.IsDirectory != y.IsDirectory)
                    return x.IsDirectory ? -1 : 1;

                return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
