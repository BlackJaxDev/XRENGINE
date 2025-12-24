using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private sealed class AssetExplorerPreviewCacheEntry(string path)
    {
        public string Path { get; private set; } = path;
        public XRTexture2D? Texture { get; set; }
        public bool RequestInFlight { get; set; }
        public uint RequestedSize { get; set; }

        public void UpdatePath(string path)
            => Path = path;
    }
}
