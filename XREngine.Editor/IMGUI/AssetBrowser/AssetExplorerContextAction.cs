namespace XREngine.Editor;

public static partial class EditorImGuiUI
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
