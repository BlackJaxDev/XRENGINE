namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private sealed class AssetExplorerContextAction(string label, Action<string> handler, Func<string, bool>? predicate)
        {
            public string Label { get; } = label;
            public Action<string> Handler { get; } = handler;
            public Func<string, bool>? Predicate { get; } = predicate;

            public bool ShouldDisplay(string path)
                => Predicate?.Invoke(path) ?? true;
        }
    }
}
