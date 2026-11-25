using XREngine.Animation;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>
/// Remembers the views of the clips that an Animation panel showed most recently. When the panel
/// shows one of these clips again, it gets that view back. This keeps the view across a play-mode
/// transition, which clears the selection on enter and restores it on exit, and across a normal
/// change of selection. A clip matches only while it is the same live object with the same
/// construction identity. The memory holds at most <see cref="Capacity"/> clips.
/// </summary>
internal sealed class NativeAnimationClipViewMemory
{
    /// <summary>The maximum number of remembered clips. The oldest entry goes first.</summary>
    public const int Capacity = 8;

    private readonly (ObjectConstructionIdentity Clip, NativeAnimationClipView View)[] _entries =
        new (ObjectConstructionIdentity, NativeAnimationClipView)[Capacity];
    private int _count;

    /// <summary>The number of remembered clips.</summary>
    public int Count => _count;

    /// <summary>Stores the view of a clip as the newest entry. It replaces an older entry for the same clip.</summary>
    public void Remember(AnimationClip clip, NativeAnimationClipView view)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Remove(clip);
        if (clip.IsDestroyed || clip.IsDestroyQueued)
            return;
        if (_count == Capacity)
            _count--;
        Array.Copy(_entries, 0, _entries, 1, _count);
        _entries[0] = (ObjectConstructionIdentity.Capture(clip), view);
        _count++;
    }

    /// <summary>Removes and returns the view of a clip. Returns false when no current entry exists.</summary>
    public bool TryTake(AnimationClip clip, out NativeAnimationClipView view)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = IndexOf(clip);
        view = default;
        if (index < 0)
            return false;
        (ObjectConstructionIdentity identity, view) = _entries[index];
        RemoveAt(index);
        return identity.IsCurrent && !clip.IsDestroyed && !clip.IsDestroyQueued;
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
    }

    private void Remove(AnimationClip clip)
    {
        int index = IndexOf(clip);
        if (index >= 0)
            RemoveAt(index);
    }

    private int IndexOf(AnimationClip clip)
    {
        for (int index = 0; index < _count; index++)
            if (ReferenceEquals(_entries[index].Clip.Target, clip))
                return index;
        return -1;
    }

    private void RemoveAt(int index)
    {
        _count--;
        Array.Copy(_entries, index + 1, _entries, index, _count - index);
        _entries[_count] = default;
    }
}
