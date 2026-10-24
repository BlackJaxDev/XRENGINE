using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    internal static SceneNode[] CaptureNativeHierarchyDuplicateTargets(SceneNode clicked)
        => [.. FilterTopLevelHierarchyNodes(Selection.SceneNodes.Any(node => ReferenceEquals(node, clicked))
            ? Selection.SceneNodes : [clicked])];

    internal static string GetNativeHierarchyDuplicateName(SceneNode source, RuntimeWorld world)
    {
        string name = string.IsNullOrWhiteSpace(source.Name) ? SceneNode.DefaultName : source.Name!;
        int suffix = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffix >= 0 && name.EndsWith(')') && int.TryParse(name.AsSpan(suffix + 2, name.Length - suffix - 3), out _))
            name = name[..suffix];
        HashSet<string> names = new(StringComparer.Ordinal);
        if (source.Transform.OwnedRetirementParent is { } parent)
        {
            foreach (var child in parent.Children)
                if (child.OwnedRetirementNode is { } node)
                    names.Add(node.Name ?? SceneNode.DefaultName);
        }
        else
        {
            foreach (SceneNode root in world.RootNodes)
                names.Add(root.Name ?? SceneNode.DefaultName);
            foreach (XRScene scene in HierarchySceneOwnership.CaptureRootScenes(source, world))
                foreach (SceneNode root in scene.SceneRoots)
                    names.Add(root.Name ?? SceneNode.DefaultName);
        }
        for (int index = 1; ; index++)
        {
            string candidate = $"{name} ({index})";
            if (!names.Contains(candidate)) return candidate;
        }
    }
}
