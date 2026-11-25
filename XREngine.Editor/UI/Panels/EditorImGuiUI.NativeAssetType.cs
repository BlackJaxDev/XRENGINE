namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>Uses the editor's registered asset type resolver without opening ImGui.</summary>
    internal static Type? ResolveNativeAssetType(string path)
        => ResolveAssetTypeForPath(path)?.Type;
}
