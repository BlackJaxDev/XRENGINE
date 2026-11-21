using System.Numerics;
using XREngine.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// Authoring operations on a <see cref="CutsceneAsset"/>. The methods do not record undo and do not notify a preview.
/// The caller does both.
/// </summary>
public static class CutsceneAssetEditing
{
    private static readonly CutsceneTime MinimumNewClipLength = CutsceneTime.FromSeconds(1.0);

    /// <summary>Finds the binding of a scene node, or adds one.</summary>
    /// <param name="asset">The asset that holds the bindings.</param>
    /// <param name="node">The scene node to bind.</param>
    /// <returns>The binding slot of the node.</returns>
    public static int GetOrAddBinding(CutsceneAsset asset, SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(node);

        for (int slot = 0; slot < asset.Bindings.Count; slot++)
        {
            CutsceneBindingReference? binding = asset.Bindings[slot];
            if (binding is { Kind: ECutsceneBindingKind.SceneObject } && binding.ObjectId == node.ID)
                return slot;
        }

        asset.Bindings.Add(new CutsceneBindingReference
        {
            Kind = ECutsceneBindingKind.SceneObject,
            ObjectId = node.ID,
            NodePath = BuildNodePath(node),
        });
        return asset.Bindings.Count - 1;
    }

    /// <summary>
    /// Adds a transform track for a node. The track has one clip that covers the whole cutscene,
    /// with one key at the current local transform, so the node does not move until an author adds keys.
    /// </summary>
    /// <param name="asset">The asset to change.</param>
    /// <param name="node">The node that the track moves.</param>
    /// <returns>The new track.</returns>
    public static TransformTrack AddTransformTrack(CutsceneAsset asset, SceneNode node)
    {
        int slot = GetOrAddBinding(asset, node);
        Transform? transform = node.Transform as Transform;
        Vector3 position = transform?.Translation ?? Vector3.Zero;
        Quaternion rotation = transform?.Rotation ?? Quaternion.Identity;
        Vector3 scale = transform?.Scale ?? Vector3.One;

        TransformTrackClip clip = new() { Start = CutsceneTime.Zero, Duration = EnsureDurationForNewClip(asset) };
        SetTransformKey(clip, CutsceneTime.Zero, position, rotation, scale);

        TransformTrack track = new() { Name = node.Name ?? "Transform", BindingSlot = slot };
        track.Clips.Add(clip);
        asset.Tracks.Add(track);
        return track;
    }

    /// <summary>Adds a camera cut track with one clip that shows the camera of a node for the whole cutscene.</summary>
    /// <param name="asset">The asset to change.</param>
    /// <param name="node">The node that holds the camera.</param>
    /// <returns>The new track.</returns>
    public static CameraCutTrack AddCameraCutTrack(CutsceneAsset asset, SceneNode node)
    {
        int slot = GetOrAddBinding(asset, node);
        CameraCutTrack track = new() { Name = node.Name ?? "Camera", BindingSlot = slot };
        track.Clips.Add(new CameraCutClip
        {
            Start = CutsceneTime.Zero,
            Duration = EnsureDurationForNewClip(asset),
            CameraBindingSlot = slot,
        });
        asset.Tracks.Add(track);
        return track;
    }

    /// <summary>Converts a cutscene time to the time of the curves of a clip.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="time">The cutscene time.</param>
    /// <returns>The clip time. This method does not limit it to the clip.</returns>
    public static CutsceneTime ToClipLocalTime(CutsceneClip clip, CutsceneTime time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return CutsceneTime.FromSeconds((time - clip.Start).ToSeconds() * clip.Speed) + clip.ClipOffset;
    }

    /// <summary>Adds a key to the three curves of a transform clip. A key at the same time is replaced.</summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="localTime">The clip time of the key.</param>
    /// <param name="position">The local position.</param>
    /// <param name="rotation">The local rotation.</param>
    /// <param name="scale">The local scale.</param>
    public static void SetTransformKey(TransformTrackClip clip, CutsceneTime localTime, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        ArgumentNullException.ThrowIfNull(clip);

        (CutsceneTime[] positionTimes, Vector3[] positionValues) = InsertKey(clip.Position.Times, clip.Position.Values, localTime, position);
        clip.Position.Times = positionTimes;
        clip.Position.Values = positionValues;

        (CutsceneTime[] rotationTimes, Quaternion[] rotationValues) = InsertKey(clip.Rotation.Times, clip.Rotation.Values, localTime, rotation);
        clip.Rotation.Times = rotationTimes;
        clip.Rotation.Values = rotationValues;

        (CutsceneTime[] scaleTimes, Vector3[] scaleValues) = InsertKey(clip.Scale.Times, clip.Scale.Values, localTime, scale);
        clip.Scale.Times = scaleTimes;
        clip.Scale.Values = scaleValues;
    }

    /// <summary>Removes the keys of a transform clip that are near a time.</summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="localTime">The clip time.</param>
    /// <param name="tolerance">The largest distance from the time at which a key is removed.</param>
    /// <returns>True if a key was removed.</returns>
    public static bool RemoveTransformKey(TransformTrackClip clip, CutsceneTime localTime, CutsceneTime tolerance)
    {
        ArgumentNullException.ThrowIfNull(clip);
        bool removed = false;

        if (TryRemoveKey(clip.Position.Times, clip.Position.Values, localTime, tolerance, out CutsceneTime[] positionTimes, out Vector3[] positionValues))
        {
            clip.Position.Times = positionTimes;
            clip.Position.Values = positionValues;
            removed = true;
        }

        if (TryRemoveKey(clip.Rotation.Times, clip.Rotation.Values, localTime, tolerance, out CutsceneTime[] rotationTimes, out Quaternion[] rotationValues))
        {
            clip.Rotation.Times = rotationTimes;
            clip.Rotation.Values = rotationValues;
            removed = true;
        }

        if (TryRemoveKey(clip.Scale.Times, clip.Scale.Values, localTime, tolerance, out CutsceneTime[] scaleTimes, out Vector3[] scaleValues))
        {
            clip.Scale.Times = scaleTimes;
            clip.Scale.Values = scaleValues;
            removed = true;
        }

        return removed;
    }

    /// <summary>Moves a clip on its track. A clip cannot start before zero.</summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="newStart">The new start time.</param>
    public static void MoveClip(CutsceneClip clip, CutsceneTime newStart)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ComputeMove(CutsceneClipSpan.Capture(clip), newStart).Apply(clip);
    }

    /// <summary>Moves the start of a clip and keeps its end and its content in place.</summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="newStart">The new start time.</param>
    /// <param name="minimumDuration">The shortest length that the clip can have.</param>
    public static void TrimClipStart(CutsceneClip clip, CutsceneTime newStart, CutsceneTime minimumDuration)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ComputeTrimStart(CutsceneClipSpan.Capture(clip), clip.Speed, newStart, minimumDuration).Apply(clip);
    }

    /// <summary>Moves the end of a clip. The start stays.</summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="newEnd">The new end time.</param>
    /// <param name="minimumDuration">The shortest length that the clip can have.</param>
    public static void TrimClipEnd(CutsceneClip clip, CutsceneTime newEnd, CutsceneTime minimumDuration)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ComputeTrimEnd(CutsceneClipSpan.Capture(clip), newEnd, minimumDuration).Apply(clip);
    }

    /// <summary>Calculates the span of a clip after a move.</summary>
    /// <param name="span">The current span.</param>
    /// <param name="newStart">The new start time. A clip cannot start before zero.</param>
    /// <returns>The new span.</returns>
    public static CutsceneClipSpan ComputeMove(CutsceneClipSpan span, CutsceneTime newStart)
        => span with { Start = newStart < CutsceneTime.Zero ? CutsceneTime.Zero : newStart };

    /// <summary>Calculates the span of a clip after a trim of its start. The end and the content stay in place.</summary>
    /// <param name="span">The current span.</param>
    /// <param name="speed">The play speed of the clip.</param>
    /// <param name="newStart">The new start time.</param>
    /// <param name="minimumDuration">The shortest length that the clip can have.</param>
    /// <returns>The new span.</returns>
    public static CutsceneClipSpan ComputeTrimStart(CutsceneClipSpan span, double speed, CutsceneTime newStart, CutsceneTime minimumDuration)
    {
        CutsceneTime end = span.Start + span.Duration;
        CutsceneTime limit = end - minimumDuration;
        CutsceneTime start = newStart.Clamp(CutsceneTime.Zero, limit < CutsceneTime.Zero ? CutsceneTime.Zero : limit);
        CutsceneTime contentShift = CutsceneTime.FromSeconds((start - span.Start).ToSeconds() * speed);
        return new CutsceneClipSpan(start, end - start, span.ClipOffset + contentShift);
    }

    /// <summary>Calculates the span of a clip after a trim of its end. The start stays.</summary>
    /// <param name="span">The current span.</param>
    /// <param name="newEnd">The new end time.</param>
    /// <param name="minimumDuration">The shortest length that the clip can have.</param>
    /// <returns>The new span.</returns>
    public static CutsceneClipSpan ComputeTrimEnd(CutsceneClipSpan span, CutsceneTime newEnd, CutsceneTime minimumDuration)
    {
        CutsceneTime duration = newEnd - span.Start;
        return span with { Duration = duration < minimumDuration ? minimumDuration : duration };
    }

    /// <summary>
    /// Adds a property track that writes one float member of a component on a node. The track has one clip that covers
    /// the whole cutscene, with one key at the current member value, so the value does not change until an author adds keys.
    /// </summary>
    /// <param name="asset">The asset to change.</param>
    /// <param name="node">The node that holds the component.</param>
    /// <param name="componentType">The short type name of the component, for example <c>UITextComponent</c>.</param>
    /// <param name="memberPath">The member path on the component, for example <c>Opacity</c>.</param>
    /// <param name="currentValue">The current value of the member. It becomes the first key.</param>
    /// <returns>The new track.</returns>
    public static PropertyTrack AddFloatPropertyTrack(CutsceneAsset asset, SceneNode node, string componentType, string memberPath, float currentValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);
        int slot = GetOrAddBinding(asset, node);
        PropertyTrackClip clip = new() { Start = CutsceneTime.Zero, Duration = EnsureDurationForNewClip(asset) };
        SetFloatKey(clip.FloatCurve, CutsceneTime.Zero, currentValue);

        PropertyTrack track = new() { Name = $"{node.Name} {memberPath}", BindingSlot = slot, ComponentType = componentType, MemberPath = memberPath };
        track.Clips.Add(clip);
        asset.Tracks.Add(track);
        return track;
    }

    /// <summary>
    /// Adds a camera property track for the camera of a node. The track has one clip that covers the whole cutscene, with
    /// one field of view key at the current value.
    /// </summary>
    /// <param name="asset">The asset to change.</param>
    /// <param name="node">The node that holds the camera.</param>
    /// <param name="fieldOfViewDegrees">The current vertical field of view in degrees. It becomes the first key.</param>
    /// <returns>The new track.</returns>
    public static CameraPropertyTrack AddFieldOfViewTrack(CutsceneAsset asset, SceneNode node, float fieldOfViewDegrees)
    {
        int slot = GetOrAddBinding(asset, node);
        CameraPropertyClip clip = new() { Start = CutsceneTime.Zero, Duration = EnsureDurationForNewClip(asset) };
        SetFloatKey(clip.FieldOfView, CutsceneTime.Zero, fieldOfViewDegrees);

        CameraPropertyTrack track = new() { Name = $"{node.Name} field of view", BindingSlot = slot };
        track.Clips.Add(clip);
        asset.Tracks.Add(track);
        return track;
    }

    /// <summary>Adds a key to a float curve, or replaces the key at the same time.</summary>
    /// <param name="curve">The curve to change.</param>
    /// <param name="localTime">The clip time of the key.</param>
    /// <param name="value">The key value.</param>
    public static void SetFloatKey(CutsceneFloatCurve curve, CutsceneTime localTime, float value)
    {
        ArgumentNullException.ThrowIfNull(curve);
        (CutsceneTime[] times, float[] values) = InsertKey(curve.Times, curve.Values, localTime, value);
        curve.Times = times;
        curve.Values = values;
    }

    /// <summary>Removes the key of a float curve that is nearest a time, within a tolerance.</summary>
    /// <param name="curve">The curve to change.</param>
    /// <param name="localTime">The clip time.</param>
    /// <param name="tolerance">The largest distance from the time at which a key is removed.</param>
    /// <returns>True if a key was removed.</returns>
    public static bool RemoveFloatKey(CutsceneFloatCurve curve, CutsceneTime localTime, CutsceneTime tolerance)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!TryRemoveKey(curve.Times, curve.Values, localTime, tolerance, out CutsceneTime[] times, out float[] values))
            return false;

        curve.Times = times;
        curve.Values = values;
        return true;
    }

    /// <summary>
    /// Returns the float curve that an author keys on a clip: the field of view of a camera property clip, or the float
    /// curve of a property clip. Other clips have none.
    /// </summary>
    public static CutsceneFloatCurve? GetKeyableFloatCurve(CutsceneClip clip)
        => clip switch
        {
            CameraPropertyClip camera => camera.FieldOfView,
            PropertyTrackClip property => property.FloatCurve,
            _ => null,
        };

    /// <summary>Returns the interpolation of the first curve of a clip, or null for a clip with no curve.</summary>
    public static ECutsceneCurveInterpolation? GetClipInterpolation(CutsceneClip clip)
        => clip switch
        {
            TransformTrackClip transform => transform.Position.Interpolation,
            CameraPropertyClip camera => camera.FieldOfView.Interpolation,
            PropertyTrackClip property => property.FloatCurve.Interpolation,
            _ => null,
        };

    /// <summary>Sets the interpolation of every curve of a clip. A clip with no curve does not change.</summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="interpolation">The new interpolation.</param>
    /// <returns>True if the clip has curves.</returns>
    public static bool SetClipInterpolation(CutsceneClip clip, ECutsceneCurveInterpolation interpolation)
    {
        switch (clip)
        {
            case TransformTrackClip transform:
                transform.Position.Interpolation = interpolation;
                transform.Rotation.Interpolation = interpolation;
                transform.Scale.Interpolation = interpolation;
                return true;
            case CameraPropertyClip camera:
                camera.FieldOfView.Interpolation = interpolation;
                return true;
            case PropertyTrackClip property:
                property.FloatCurve.Interpolation = interpolation;
                property.Vector3Curve.Interpolation = interpolation;
                property.QuaternionCurve.Interpolation = interpolation;
                return true;
            default:
                return false;
        }
    }

    private static CutsceneTime EnsureDurationForNewClip(CutsceneAsset asset)
    {
        if (asset.Duration < MinimumNewClipLength)
            asset.Duration = MinimumNewClipLength;
        return asset.Duration;
    }

    private static string BuildNodePath(SceneNode node)
    {
        string path = node.Name ?? string.Empty;
        for (TransformBase? parent = node.Transform.Parent; parent is not null; parent = parent.Parent)
            path = $"{parent.SceneNode?.Name}/{path}";
        return path;
    }

    private static (CutsceneTime[] Times, T[] Values) InsertKey<T>(CutsceneTime[] times, T[] values, CutsceneTime time, T value)
    {
        int count = Math.Min(times.Length, values.Length);
        int index = 0;
        while (index < count && times[index] < time)
            index++;

        bool replace = index < count && times[index] == time;
        int newCount = replace ? count : count + 1;
        CutsceneTime[] newTimes = new CutsceneTime[newCount];
        T[] newValues = new T[newCount];

        Array.Copy(times, newTimes, index);
        Array.Copy(values, newValues, index);
        newTimes[index] = time;
        newValues[index] = value;

        int sourceNext = replace ? index + 1 : index;
        Array.Copy(times, sourceNext, newTimes, index + 1, count - sourceNext);
        Array.Copy(values, sourceNext, newValues, index + 1, count - sourceNext);
        return (newTimes, newValues);
    }

    private static bool TryRemoveKey<T>(CutsceneTime[] times, T[] values, CutsceneTime time, CutsceneTime tolerance, out CutsceneTime[] newTimes, out T[] newValues)
    {
        newTimes = times;
        newValues = values;

        int count = Math.Min(times.Length, values.Length);
        int found = -1;
        for (int i = 0; i < count && found < 0; i++)
            if (Math.Abs(times[i].Flicks - time.Flicks) <= tolerance.Flicks)
                found = i;

        if (found < 0)
            return false;

        newTimes = new CutsceneTime[count - 1];
        newValues = new T[count - 1];
        Array.Copy(times, newTimes, found);
        Array.Copy(values, newValues, found);
        Array.Copy(times, found + 1, newTimes, found, count - found - 1);
        Array.Copy(values, found + 1, newValues, found, count - found - 1);
        return true;
    }
}
