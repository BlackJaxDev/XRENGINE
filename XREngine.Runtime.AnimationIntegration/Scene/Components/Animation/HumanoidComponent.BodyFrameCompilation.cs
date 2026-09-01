using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    /// <summary>
    /// A single Hips correction can move only its descendants. Compile the
    /// concrete ancestry and fixed helper invariants once, never by name at runtime.
    /// Hips ancestors are handled separately as actual, movable parent frames.
    /// </summary>
    private bool TryCompileBodyHierarchyGuards(
        SceneNode?[] nodes,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaries,
        out CompiledHumanoidHierarchyGuard[] guards,
        out string diagnostic)
    {
        guards = [];
        if (nodes[(int)EHumanoidAvatarBoneRole.Hips] is not SceneNode hips)
        {
            diagnostic = "Body compensation requires a mapped Hips node.";
            return false;
        }
        var owned = new HashSet<SceneNode>();
        var ordered = new List<SceneNode>(nodes.Length + auxiliaries.Length);
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] is SceneNode node && owned.Add(node))
                ordered.Add(node);
        for (int i = 0; i < auxiliaries.Length; i++)
            if (owned.Add(auxiliaries[i].Node))
                ordered.Add(auxiliaries[i].Node);
        var seen = new HashSet<SceneNode>();
        var result = new List<CompiledHumanoidHierarchyGuard>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            SceneNode node = ordered[i];
            if (!ReferenceEquals(node, hips) && !IsConcreteAncestor(node, hips))
            {
                diagnostic = $"Body compensation cannot move humanoid bone '{node.Name}' outside the Hips subtree.";
                return false;
            }
            if (seen.Add(node))
                result.Add(new CompiledHumanoidHierarchyGuard(node.Transform, null));
            if (ReferenceEquals(node, hips))
                continue;
            for (SceneNode? parent = node.Parent; parent is not null && !owned.Contains(parent); parent = parent.Parent)
            {
                if (!seen.Add(parent))
                    continue;
                if (!_humanoidBindLocalPoses.TryGetValue(parent, out Matrix4x4 neutral))
                {
                    diagnostic = $"Body compensation helper '{parent.Name}' has no captured neutral local transform.";
                    return false;
                }
                result.Add(new CompiledHumanoidHierarchyGuard(parent.Transform, neutral));
            }
        }
        guards = result.ToArray();
        diagnostic = string.Empty;
        return true;
    }
}
