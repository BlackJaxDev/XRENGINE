using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// Records the original transform and activation of the bound nodes before the cutscene writes them,
/// and puts them back when the cutscene ends. All storage is allocated when the snapshot is created.
/// </summary>
public sealed class CutsceneStateSnapshot
{
    private readonly CutsceneBindingTable _table;
    private readonly Vector3[] _translations;
    private readonly Quaternion[] _rotations;
    private readonly Vector3[] _scales;
    private readonly bool[] _hasTransform;
    private readonly bool[] _active;
    private readonly bool[] _hasActive;

    /// <summary>Creates a snapshot with storage for each slot of a binding table.</summary>
    /// <param name="table">The table that holds the bound nodes.</param>
    public CutsceneStateSnapshot(CutsceneBindingTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        int count = table.SlotCount;
        _translations = new Vector3[count];
        _rotations = new Quaternion[count];
        _scales = new Vector3[count];
        _hasTransform = new bool[count];
        _active = new bool[count];
        _hasActive = new bool[count];
    }

    /// <summary>
    /// Records the local transform of a slot. If the slot is already recorded, the first value stays.
    /// A slot without a live node, or with a node that has no standard transform, is not recorded.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public void CaptureTransform(int slot)
    {
        if ((uint)slot >= (uint)_hasTransform.Length || _hasTransform[slot])
            return;
        if (_table.GetNode(slot)?.Transform is not Transform transform)
            return;

        _translations[slot] = transform.Translation;
        _rotations[slot] = transform.Rotation;
        _scales[slot] = transform.Scale;
        _hasTransform[slot] = true;
    }

    /// <summary>
    /// Records the activation of a slot. If the slot is already recorded, the first value stays.
    /// A slot without a live node is not recorded.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public void CaptureActive(int slot)
    {
        if ((uint)slot >= (uint)_hasActive.Length || _hasActive[slot])
            return;
        if (_table.GetNode(slot) is not SceneNode node)
            return;

        _active[slot] = node.IsActiveSelf;
        _hasActive[slot] = true;
    }

    /// <summary>
    /// Writes the recorded transform of a slot to its node. The record stays, so a later call can write it again.
    /// The call does nothing if the slot has no record or no live node.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public void ApplyTransform(int slot)
    {
        if ((uint)slot >= (uint)_hasTransform.Length || !_hasTransform[slot])
            return;
        if (_table.GetNode(slot)?.Transform is not Transform transform)
            return;

        transform.Translation = _translations[slot];
        transform.Rotation = _rotations[slot];
        transform.Scale = _scales[slot];
    }

    /// <summary>
    /// Writes the recorded activation of a slot to its node. The record stays, so a later call can write it again.
    /// The call does nothing if the slot has no record or no live node.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public void ApplyActive(int slot)
    {
        if ((uint)slot >= (uint)_hasActive.Length || !_hasActive[slot])
            return;
        if (_table.GetNode(slot) is SceneNode node)
            node.IsActiveSelf = _active[slot];
    }

    /// <summary>
    /// Puts back the recorded values. A transform track restores the transform and an activation track restores the activation.
    /// A restored value is cleared, so the next capture records a new original value.
    /// </summary>
    /// <param name="forceAll">True to restore every track, whatever its finish behavior is.</param>
    /// <param name="perTrack">
    /// The finish behavior of each track, by track index. A track beyond the end of the span uses its own setting.
    /// </param>
    public void Restore(bool forceAll, ReadOnlySpan<ECutsceneFinishBehavior> perTrack)
    {
        for (int trackIndex = 0; trackIndex < _table.TrackCount; trackIndex++)
        {
            CutsceneTrack track = _table.GetTrack(trackIndex);
            ECutsceneFinishBehavior behavior = trackIndex < perTrack.Length ? perTrack[trackIndex] : track.WhenFinished;
            if (!forceAll && behavior != ECutsceneFinishBehavior.RestoreState)
                continue;

            int slot = track.BindingSlot;
            if ((uint)slot >= (uint)_hasTransform.Length)
                continue;

            if (track is TransformTrack)
                RestoreTransform(slot);
            else if (track is ActivationTrack)
                RestoreActive(slot);
        }
    }

    private void RestoreTransform(int slot)
    {
        if (!_hasTransform[slot])
            return;
        _hasTransform[slot] = false;

        if (_table.GetNode(slot)?.Transform is not Transform transform)
            return;

        transform.Translation = _translations[slot];
        transform.Rotation = _rotations[slot];
        transform.Scale = _scales[slot];
    }

    private void RestoreActive(int slot)
    {
        if (!_hasActive[slot])
            return;
        _hasActive[slot] = false;

        if (_table.GetNode(slot) is SceneNode node)
            node.IsActiveSelf = _active[slot];
    }
}
