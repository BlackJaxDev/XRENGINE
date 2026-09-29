using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Publishing;

public sealed partial class BrowserWorldPublishExporter
{
    private static void CollectChannels(AnimationMember member, object target,
        Dictionary<TransformBase, int> indices, Transform[] detached,
        List<(BasePropAnim Curve, AnimationMember Setter)> channels, HashSet<AnimationMember> visited, string path, int depth = 0)
    {
        if (depth > 128 || !visited.Add(member) || visited.Count > 8192)
            throw Unsupported(path, "cyclic or oversized animation member tree");
        object next = target;
        if (member.Animation is BasePropAnim curve)
        {
            if (member.MemberType != EAnimationMemberType.Property || target is not TransformBase bone ||
                !indices.TryGetValue(bone, out int index) || member.Children.Count != 0)
                throw Unsupported(path, $"animation channel '{member.MemberName}' is not a leaf property of an exported skeleton transform");
            bool supported = member.MemberName switch
            {
                "Translation" or "Scale" => curve.GetType() == typeof(PropAnimVector3),
                "Rotation" => curve.GetType() == typeof(PropAnimQuaternion),
                "TranslationX" or "TranslationY" or "TranslationZ" or "ScaleX" or "ScaleY" or "ScaleZ" or
                    "QuaternionX" or "QuaternionY" or "QuaternionZ" or "QuaternionW" => curve.GetType() == typeof(PropAnimFloat),
                _ => false,
            };
            if (!supported)
                throw Unsupported(path, $"animation property '{member.MemberName}' or its native curve type requires an unsupported runtime adapter");
            AnimationMember setter = new(member.MemberName, EAnimationMemberType.Property, animation: null);
            setter.InitializeProperty(detached[index]);
            if (setter.MemberNotFound)
                throw Unsupported(path, $"native transform property '{member.MemberName}' cannot be bound");
            channels.Add((curve, setter));
            return;
        }
        if (member.MemberType != EAnimationMemberType.Group)
        {
            if (member.MemberType == EAnimationMemberType.Property && member.MemberName == "SceneNode" && target is AnimationClipComponent component)
                next = component.SceneNode;
            else if (member.MemberType == EAnimationMemberType.Property && member.MemberName == "Transform")
                next = target switch
                {
                    SceneNode node => node.Transform,
                    AnimationClipComponent clip => clip.Transform,
                    _ => throw Unsupported(path, "animation Transform path has an unsupported owner"),
                };
            else if (member.MemberType == EAnimationMemberType.Method && member.MemberName == "FindDescendantByName" &&
                target is SceneNode parent && member.AnimatedMethodArgumentIndex == -1 &&
                member.MethodArguments is { Length: 2 } args && args[0] is string name && args[1] is StringComparison comparison)
                next = FindUniqueDescendant(parent, name, comparison, path);
            else
                throw Unsupported(path, $"animation route '{member.MemberName}' is outside the generic SceneNode/Transform contract");
        }
        foreach (AnimationMember child in member.Children)
            CollectChannels(child, next, indices, detached, channels, visited, path, depth + 1);
    }

    private static SceneNode FindUniqueDescendant(SceneNode parent, string name, StringComparison comparison, string path)
    {
        if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCulture or StringComparison.InvariantCultureIgnoreCase))
            throw Unsupported(path, "culture-dependent animation bone lookup");
        SceneNode? match = null;
        Stack<TransformBase> pending = new();
        HashSet<TransformBase> visited = new(ReferenceEqualityComparer.Instance);
        // The native FindDescendantByName contract includes the starting node.
        pending.Push(parent.Transform);
        while (pending.TryPop(out TransformBase? transform))
        {
            if (!visited.Add(transform) || visited.Count > 8192)
                throw Unsupported(path, "cyclic or oversized animation lookup hierarchy");
            if (transform.SceneNode is SceneNode node && string.Equals(node.Name, name, comparison))
            {
                if (match is not null) throw Unsupported(path, $"ambiguous animation bone name '{name}'");
                match = node;
            }
            foreach (var child in transform.Children)
                if (child is not null) pending.Push(child);
        }
        return match ?? throw Unsupported(path, $"animation bone '{name}' was not found under '{parent.GetPath()}'");
    }
}
