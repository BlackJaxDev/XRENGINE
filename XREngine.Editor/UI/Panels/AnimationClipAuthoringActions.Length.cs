using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.Editor;

internal static partial class AnimationClipAuthoringActions
{
    private static bool SetClipAndMemberLengths(AnimationClip clip, float seconds, bool stretch)
    {
        if (!float.IsFinite(seconds) || seconds < 0 || seconds > 3600 ||
            !float.IsFinite(clip.LengthInSeconds) || clip.LengthInSeconds < 0 ||
            clip.IsDestroyed || clip.IsDestroyQueued)
            return false;
        float previous = clip.LengthInSeconds;
        AnimationMember? root = clip.RootMember;
        List<(AnimationMember Node, AnimationMember? Parent, object? Container,
            int Index, object Children, int Count, BasePropAnim? Animation)> captured = [];
        List<AnimationLengthEdit> edits = [];
        HashSet<AnimationMember> seenNodes = new(ReferenceEqualityComparer.Instance);
        HashSet<BasePropAnim> seenAnimations = new(ReferenceEqualityComparer.Instance);
        HashSet<BaseKeyframeTrack> seenTracks = new(ReferenceEqualityComparer.Instance);
        Stack<(AnimationMember Node, AnimationMember? Parent, object? Container, int Index)> pending = new();
        if (root is not null)
            pending.Push((root, null, null, -1));
        while (pending.Count != 0)
        {
            if (captured.Count >= 100_000)
                return RejectLength("The animation member tree exceeds the supported edit size.");
            var (node, parent, container, index) = pending.Pop();
            if (!seenNodes.Add(node))
                return RejectLength("The animation member tree has repeated or cyclic nodes.");
            BasePropAnim? animation = node.Animation;
            captured.Add((node, parent, container, index, node.Children, node.Children.Count, animation));
            if (animation is not null && seenAnimations.Add(animation))
            {
                BaseKeyframeTrack? track = TrackFor(animation);
                if (track is null || !seenTracks.Add(track) || CaptureOrder(track) is not { } order)
                    return RejectLength("An animation track is unsupported, shared, or has invalid links.");
                Func<bool>[] payloads = new Func<bool>[order.Length];
                for (int keyIndex = 0; keyIndex < order.Length; keyIndex++)
                {
                    Func<bool>? payload = CaptureKeyPayload(order[keyIndex]);
                    if (payload is null)
                        return RejectLength("A key payload does not support exact length edits.");
                    payloads[keyIndex] = payload;
                }
                if (!AnimationLengthEdit.TryCapture(animation, track, order, payloads,
                        seconds, stretch, out AnimationLengthEdit? edit, out string reason))
                    return RejectLength(reason);
                edits.Add(edit!);
            }
            for (int child = node.Children.Count - 1; child >= 0; child--)
                pending.Push((node.Children[child], node, node.Children, child));
        }
        if (previous == seconds && !edits.Any(edit => edit.HasChange))
            return false;
        var nodes = captured.ToArray();
        AnimationLengthEdit[] children = edits.ToArray();
        Func<bool> clipLive = CaptureClipLifetime(clip);
        HashSet<object> owners = new(ReferenceEqualityComparer.Instance);
        HashSet<XRAsset> documents = new(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
            owners.Add(node.Node);
        foreach (AnimationLengthEdit edit in children)
        {
            owners.Add(edit.Animation);
            owners.Add(edit.Track);
            owners.Add(edit.Document);
            documents.Add(edit.Document);
            foreach (Keyframe key in edit.Keys)
                owners.Add(key);
        }
        documents.Remove(clip.SourceAsset);
        XRAsset[] childDocuments = [.. documents];

        bool OwnsTree()
        {
            if (!clipLive() || !ReferenceEquals(clip.RootMember, root))
                return false;
            foreach (var node in nodes)
            {
                if (!ReferenceEquals(node.Node.Animation, node.Animation) ||
                    !ReferenceEquals(node.Node.Children, node.Children) ||
                    node.Node.Children.Count != node.Count)
                    return false;
                if (node.Parent is not null &&
                    (!ReferenceEquals(node.Parent.Children, node.Container) ||
                     (uint)node.Index >= (uint)node.Parent.Children.Count ||
                     !ReferenceEquals(node.Parent.Children[node.Index], node.Node)))
                    return false;
            }
            return true;
        }
        bool Matches(bool after)
        {
            if (!OwnsTree() || clip.LengthInSeconds != (after ? seconds : previous))
                return false;
            foreach (AnimationLengthEdit edit in children)
                if (!edit.Matches(after))
                    return false;
            return true;
        }
        bool CanCommit(bool forward)
        {
            if (!Matches(after: !forward))
                return false;
            foreach (AnimationLengthEdit edit in children)
                if (!edit.CanCommit(forward))
                    return false;
            return true;
        }
        void Commit(bool forward)
        {
            float from = forward ? previous : seconds;
            float to = forward ? seconds : previous;
            bool wrote = false;
            try
            {
                if (!CanCommit(forward) || !clip.PrepareLengthChange(from, to) || !CanCommit(forward))
                    throw new InvalidOperationException("Animation length ownership changed before commit.");
                if (!clip.TrySetLengthWithoutNotification(from, to))
                    throw new InvalidOperationException("Animation length changed before commit.");
                wrote = true;
                foreach (AnimationLengthEdit edit in children)
                    edit.Commit(forward);
                void Validate()
                {
                    if (!Matches(after: forward))
                        throw new InvalidOperationException("Animation timing changed during notification.");
                    foreach (AnimationLengthEdit edit in children)
                        if (!edit.MatchesCurrentCommit(after: forward))
                            throw new InvalidOperationException("Animation track was edited during notification.");
                }
                Validate();
                clip.PublishLengthChange(from, to);
                Validate();
                foreach (AnimationLengthEdit edit in children)
                    edit.Publish(forward, Validate);
            }
            finally
            {
                if (wrote)
                    foreach (XRAsset document in childDocuments)
                        try { document.MarkDirty(); }
                        catch (Exception exception)
                        {
                            try { Debug.LogException(exception, "Could not mark an animation source dirty."); }
                            catch { }
                        }
            }
        }
        return Apply(clip, stretch ? "Stretch Animation Length" : "Set Animation Length",
            () => Commit(true), () => Commit(false), () => Matches(true), () => Matches(false),
            OwnsTree, [(clip, nameof(AnimationClip.LengthInSeconds))], [.. owners]);
    }

    private static bool RejectLength(string reason)
    {
        Debug.UI($"[NativeAnimation] Length edit unavailable: {reason}");
        return false;
    }
}
