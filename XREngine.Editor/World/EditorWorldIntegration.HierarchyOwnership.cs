using XREngine.Scene;

namespace XREngine.Editor;

public sealed partial class EditorWorldIntegration
{
    internal XRScene? ExistingEditorScene => _editorScene;

    /// <summary>Copies routing links only when an authoring command captures its ownership.</summary>
    internal XRScene[] CaptureHierarchySourceRoutes(SceneNode root)
    {
        ThrowIfDisposed();
        List<XRScene> scenes = [];
        foreach (var pair in _editorOnlyNodesByScene)
            if (pair.Value.Contains(root))
                scenes.Add(pair.Key);
        return [.. scenes];
    }

    internal bool HasHierarchySourceRoutes(SceneNode root, IReadOnlyList<XRScene> scenes)
    {
        if (_disposed)
            return false;
        int count = 0;
        foreach (var pair in _editorOnlyNodesByScene)
            if (pair.Value.Contains(root))
            {
                if (!scenes.Contains(pair.Key))
                    return false;
                count++;
            }
        return count == scenes.Count;
    }

    /// <summary>Restores routing metadata after the command validates its captured world and scenes.</summary>
    internal void RestoreHierarchySourceRoutes(SceneNode root, IReadOnlyList<XRScene> scenes)
    {
        ThrowIfDisposed();
        RemoveFromSourceSceneMembership(root);
        foreach (XRScene scene in scenes)
            GetOrCreateSourceRoots(scene).Add(root);
    }
}
