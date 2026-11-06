using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// Records the original transform, activation, and field of view of the bound nodes before the cutscene writes them,
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
    private readonly float[] _fieldOfView;
    private readonly bool[] _hasFieldOfView;

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
        _fieldOfView = new float[count];
        _hasFieldOfView = new bool[count];
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
    /// Records the vertical field of view of the perspective camera of a slot. If the slot is already recorded, the first value stays.
    /// A slot without a live node, or with a node that has no perspective camera, is not recorded.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public void CaptureFieldOfView(int slot)
    {
        if ((uint)slot >= (uint)_hasFieldOfView.Length || _hasFieldOfView[slot])
            return;
        if (CutsceneCameraLookup.FindPerspective(_table.GetNode(slot)) is not { } parameters)
            return;

        _fieldOfView[slot] = parameters.VerticalFieldOfView;
        _hasFieldOfView[slot] = true;
    }

    /// <summary>Reads the recorded field of view of a slot.</summary>
    /// <param name="slot">The binding slot.</param>
    /// <param name="fieldOfView">The recorded vertical field of view in degrees.</param>
    /// <returns>True if the slot has a record.</returns>
    public bool TryGetFieldOfView(int slot, out float fieldOfView)
    {
        if ((uint)slot >= (uint)_hasFieldOfView.Length || !_hasFieldOfView[slot])
        {
            fieldOfView = 0f;
            return false;
        }

        fieldOfView = _fieldOfView[slot];
        return true;
    }

    /// <summary>
    /// Writes the recorded field of view of a slot to its camera. The record stays, so a later call can write it again.
    /// The call does nothing if the slot has no record or no perspective camera.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public void ApplyFieldOfView(int slot)
    {
        if ((uint)slot >= (uint)_hasFieldOfView.Length || !_hasFieldOfView[slot])
            return;
        if (CutsceneCameraLookup.FindPerspective(_table.GetNode(slot)) is { } parameters)
            parameters.VerticalFieldOfView = _fieldOfView[slot];
    }

    /// <summary>Reads the recorded transform of a slot.</summary>
    /// <param name="slot">The binding slot.</param>
    /// <param name="translation">The recorded translation.</param>
    /// <param name="rotation">The recorded rotation.</param>
    /// <param name="scale">The recorded scale.</param>
    /// <returns>True if the slot has a record.</returns>
    public bool TryGetTransform(int slot, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        if ((uint)slot >= (uint)_hasTransform.Length || !_hasTransform[slot])
        {
            translation = default;
            rotation = Quaternion.Identity;
            scale = Vector3.One;
            return false;
        }

        translation = _translations[slot];
        rotation = _rotations[slot];
        scale = _scales[slot];
        return true;
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
        // First restore each record that a track with the restore setting uses. A record is shared by the
        // tracks of one slot, so a kept track must not clear it before a later track restores it.
        for (int trackIndex = 0; trackIndex < _table.TrackCount; trackIndex++)
        {
            CutsceneTrack track = _table.GetTrack(trackIndex);
            int slot = track.BindingSlot;
            if ((uint)slot >= (uint)_hasTransform.Length || !ShouldRestore(track, trackIndex, forceAll, perTrack))
                continue;

            if (track is TransformTrack or AnimationClipTrack)
                RestoreTransform(slot);
            else if (track is ActivationTrack)
                RestoreActive(slot);
            else if (track is CameraPropertyTrack)
                RestoreFieldOfView(slot);
        }

        // A record that no track restored keeps its live value, which is the original value of the next run.
        for (int trackIndex = 0; trackIndex < _table.TrackCount; trackIndex++)
        {
            CutsceneTrack track = _table.GetTrack(trackIndex);
            int slot = track.BindingSlot;
            if ((uint)slot < (uint)_hasTransform.Length && !ShouldRestore(track, trackIndex, forceAll, perTrack))
                Forget(track, slot);
        }
    }

    private static bool ShouldRestore(CutsceneTrack track, int trackIndex, bool forceAll, ReadOnlySpan<ECutsceneFinishBehavior> perTrack)
    {
        ECutsceneFinishBehavior behavior = trackIndex < perTrack.Length ? perTrack[trackIndex] : track.WhenFinished;
        return forceAll || behavior == ECutsceneFinishBehavior.RestoreState;
    }

    /// <summary>
    /// Puts back every recorded value of every slot. This does not read the tracks of the asset,
    /// so it also restores a target whose track was removed after the value was recorded.
    /// </summary>
    public void RestoreAll()
    {
        for (int slot = 0; slot < _hasTransform.Length; slot++)
        {
            RestoreTransform(slot);
            RestoreActive(slot);
            RestoreFieldOfView(slot);
        }
    }

    private void Forget(CutsceneTrack track, int slot)
    {
        if (track is TransformTrack or AnimationClipTrack)
            _hasTransform[slot] = false;
        else if (track is ActivationTrack)
            _hasActive[slot] = false;
        else if (track is CameraPropertyTrack)
            _hasFieldOfView[slot] = false;
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

    private void RestoreFieldOfView(int slot)
    {
        if (!_hasFieldOfView[slot])
            return;
        _hasFieldOfView[slot] = false;

        if (CutsceneCameraLookup.FindPerspective(_table.GetNode(slot)) is { } parameters)
            parameters.VerticalFieldOfView = _fieldOfView[slot];
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
