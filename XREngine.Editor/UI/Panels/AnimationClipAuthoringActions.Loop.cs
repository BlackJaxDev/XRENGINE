using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.Editor;

internal static partial class AnimationClipAuthoringActions
{
    private static bool SetClipAndMemberLoops(AnimationClip clip, bool looped)
    {
        bool previous = clip.Looped;
        if (previous == looped || clip.IsDestroyed || clip.IsDestroyQueued)
            return false;
        AnimationMember? root = clip.RootMember;
        List<(AnimationMember Node, AnimationMember? Parent,
            object? Container, int Index, object Children,
            int ChildCount,
            BasePropAnim? Bound)> capturedNodes = [];
        List<(BaseAnimation Animation, XRAsset Document,
            Guid Identity, Guid DocumentIdentity, bool Previous)> targets = [];
        HashSet<AnimationMember> seenNodes =
            new(ReferenceEqualityComparer.Instance);
        HashSet<BaseAnimation> seenAnimations =
            new(ReferenceEqualityComparer.Instance);
        Stack<(AnimationMember Node, AnimationMember? Parent,
            object? Container, int Index)> pending = new();
        if (root is not null)
            pending.Push((root, null, null, -1));
        while (pending.Count > 0)
        {
            if (capturedNodes.Count >= 100_000)
                return false;
            var (node, parent, container, index) = pending.Pop();
            if (!seenNodes.Add(node))
                return false;
            BasePropAnim? bound = node.Animation;
            capturedNodes.Add((node, parent, container, index,
                node.Children,
                node.Children.Count, bound));
            if (bound is not null && seenAnimations.Add(bound))
            {
                XRAsset document = bound.SourceAsset;
                if (bound.IsDestroyed || bound.IsDestroyQueued ||
                    document.IsDestroyed || document.IsDestroyQueued)
                    return false;
                targets.Add((bound, document, bound.ID, document.ID,
                    bound.Looped));
            }
            for (int childIndex = node.Children.Count - 1;
                 childIndex >= 0; childIndex--)
                pending.Push((node.Children[childIndex], node,
                    node.Children, childIndex));
        }

        XRAsset clipDocument = clip.SourceAsset;
        Guid clipIdentity = clip.ID;
        Guid clipDocumentIdentity = clipDocument.ID;
        var nodes = capturedNodes.ToArray();
        var children = targets.ToArray();
        HashSet<XRAsset> seenDocuments =
            new(ReferenceEqualityComparer.Instance) { clipDocument };
        List<XRAsset> documents = [];
        foreach (var child in children)
            if (seenDocuments.Add(child.Document))
                documents.Add(child.Document);
        HashSet<object> ownerSet =
            new(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
            ownerSet.Add(node.Node);
        foreach (var child in children)
        {
            ownerSet.Add(child.Animation);
            ownerSet.Add(child.Document);
        }
        object[] owners = [.. ownerSet];

        bool OwnsWholeTree()
        {
            if (clip.IsDestroyed || clip.IsDestroyQueued ||
                clipDocument.IsDestroyed || clipDocument.IsDestroyQueued ||
                clip.ID != clipIdentity ||
                clipDocument.ID != clipDocumentIdentity ||
                !ReferenceEquals(clip.SourceAsset, clipDocument) ||
                !ReferenceEquals(clip.RootMember, root))
                return false;
            foreach (var node in nodes)
            {
                if (!ReferenceEquals(node.Node.Animation, node.Bound) ||
                    !ReferenceEquals(node.Node.Children,
                        node.Children) ||
                    node.Node.Children.Count != node.ChildCount)
                    return false;
                if (node.Parent is not null &&
                    (!ReferenceEquals(node.Parent.Children, node.Container) ||
                     (uint)node.Index >= (uint)node.Parent.Children.Count ||
                     !ReferenceEquals(node.Parent.Children[node.Index],
                         node.Node)))
                    return false;
            }
            foreach (var child in children)
                if (child.Animation.IsDestroyed ||
                    child.Animation.IsDestroyQueued ||
                    child.Document.IsDestroyed ||
                    child.Document.IsDestroyQueued ||
                    child.Animation.ID != child.Identity ||
                    child.Document.ID != child.DocumentIdentity ||
                    !ReferenceEquals(child.Animation.SourceAsset,
                        child.Document))
                    return false;
            return true;
        }

        bool Matches(bool applied)
        {
            if (!OwnsWholeTree() ||
                clip.Looped != (applied ? looped : previous))
                return false;
            foreach (var child in children)
                if (child.Animation.Looped !=
                    (applied ? looped : child.Previous))
                    return false;
            return true;
        }

        void MarkChildDocumentsDirty()
        {
            foreach (XRAsset document in documents)
                try { document.MarkDirty(); }
                catch (Exception exception)
                {
                    try { Debug.LogException(exception,
                        "Could not mark a child animation source dirty."); }
                    catch { }
                }
        }

        void Commit(bool forward)
        {
            bool from = forward ? previous : looped;
            bool to = forward ? looped : previous;
            bool expectedApplied = !forward;
            bool wrote = false;
            try
            {
                if (!Matches(expectedApplied) ||
                    !clip.PrepareLoopedChange(from, to) ||
                    !Matches(expectedApplied))
                    throw new InvalidOperationException(
                        "Animation loop owner or value changed before commit.");

                if (!clip.TrySetLoopedWithoutNotification(from, to))
                    throw new InvalidOperationException(
                        "Animation loop changed before commit.");
                wrote = true;
                foreach (var child in children)
                    if (!child.Animation.TrySetLoopedWithoutNotification(
                            forward ? child.Previous : looped,
                            forward ? looped : child.Previous))
                        throw new InvalidOperationException(
                            "Child animation loop changed before commit.");
                if (!Matches(forward))
                    throw new InvalidOperationException(
                        "Animation loop batch changed during commit.");

                clip.PublishLoopedChange(from, to);
                if (!Matches(forward))
                    throw new InvalidOperationException(
                        "Animation loop changed during notification.");
                foreach (var child in children)
                {
                    child.Animation.PublishLoopedChange();
                    if (!Matches(forward))
                        throw new InvalidOperationException(
                            "Child animation loop changed during notification.");
                }
            }
            finally
            {
                if (wrote)
                    MarkChildDocumentsDirty();
            }
        }

        return Apply(clip, "Set Animation Loop",
            () => Commit(forward: true),
            () => Commit(forward: false),
            () => Matches(applied: true),
            () => Matches(applied: false),
            OwnsWholeTree,
            [(clip, nameof(AnimationClip.Looped))],
            owners);
    }
}
