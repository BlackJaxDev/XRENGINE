using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    internal static SceneNode[] CaptureNativeHierarchyDuplicateTargets(SceneNode clicked)
        => [.. FilterTopLevelHierarchyNodes(Selection.SceneNodes.Any(node => ReferenceEquals(node, clicked))
            ? Selection.SceneNodes : [clicked])];

    internal static string GetNativeHierarchyDuplicateName(SceneNode source, RuntimeWorld world)
        => GetNativeHierarchyDuplicateNames([source], world)[0];

    internal static string[] GetNativeHierarchyDuplicateNames(IReadOnlyList<SceneNode> sources, RuntimeWorld world)
    {
        string[] names = new string[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            string stem = Stem(sources[i]);
            HashSet<string> occupied = CollectOccupiedNames(sources[i], world);
            for (int previous = 0; previous < i; previous++)
                if (DestinationsOverlap(sources[i], sources[previous], world))
                    occupied.Add(names[previous]);
            for (int ordinal = 1; ; ordinal++)
            {
                string candidate = $"{stem} ({ordinal})";
                if (occupied.Contains(candidate)) continue;
                names[i] = candidate;
                break;
            }
        }
        return names;
    }

    internal static bool IsNativeHierarchyDuplicateNameAvailable(SceneNode source, RuntimeWorld world,
        string name, IReadOnlyList<SceneNode?>? ownClones = null)
    {
        if (source.Transform.OwnedRetirementParent is { } parent)
        {
            foreach (var child in parent.Children)
                if (child.OwnedRetirementNode is { } node && !Owns(node, ownClones) &&
                    (node.Name ?? SceneNode.DefaultName) == name)
                    return false;
            return true;
        }
        foreach (SceneNode root in world.RootNodes)
            if (!Owns(root, ownClones) && (root.Name ?? SceneNode.DefaultName) == name)
                return false;
        foreach (XRScene scene in HierarchySceneOwnership.CaptureRootScenes(source, world))
            foreach (SceneNode root in scene.SceneRoots)
                if (!Owns(root, ownClones) && (root.Name ?? SceneNode.DefaultName) == name)
                    return false;
        return true;
    }

    private static bool DestinationsOverlap(SceneNode left, SceneNode right, RuntimeWorld world)
    {
        var leftParent = left.Transform.OwnedRetirementParent;
        var rightParent = right.Transform.OwnedRetirementParent;
        if (leftParent is not null || rightParent is not null)
            return leftParent is not null && ReferenceEquals(leftParent, rightParent);
        if (world.RootNodes.Contains(left) && world.RootNodes.Contains(right)) return true;
        XRScene[] leftScenes = HierarchySceneOwnership.CaptureRootScenes(left, world);
        XRScene[] rightScenes = HierarchySceneOwnership.CaptureRootScenes(right, world);
        foreach (XRScene scene in leftScenes)
            foreach (XRScene peer in rightScenes)
                if (ReferenceEquals(scene, peer)) return true;
        return false;
    }

    private static HashSet<string> CollectOccupiedNames(SceneNode source, RuntimeWorld world)
    {
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
        return names;
    }

    private static string Stem(SceneNode source)
    {
        string name = string.IsNullOrWhiteSpace(source.Name) ? SceneNode.DefaultName : source.Name!;
        int suffix = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffix >= 0 && name.EndsWith(')') &&
            int.TryParse(name.AsSpan(suffix + 2, name.Length - suffix - 3), out _))
            name = name[..suffix];
        return name;
    }

    private static bool Owns(SceneNode node, IReadOnlyList<SceneNode?>? ownClones)
    {
        if (ownClones is null) return false;
        foreach (SceneNode? clone in ownClones)
            if (ReferenceEquals(node, clone)) return true;
        return false;
    }
}
