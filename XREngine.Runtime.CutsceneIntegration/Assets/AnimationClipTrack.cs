using System.Numerics;

namespace XREngine.Cutscenes;

/// <summary>A track that plays animation clips on its target. Its clips are AnimationClipTrackClip instances.</summary>
public class AnimationClipTrack : CutsceneTrack
{
    private Vector3 _rootOffsetPosition;
    private Quaternion _rootOffsetRotation = Quaternion.Identity;

    /// <summary>
    /// The position of the humanoid root placement, relative to the transform of the target before the
    /// first clip. A target without a humanoid has no root placement.
    /// </summary>
    public Vector3 RootOffsetPosition
    {
        get => _rootOffsetPosition;
        set => SetField(ref _rootOffsetPosition, value);
    }

    /// <summary>The rotation of the humanoid root placement, relative to the transform of the target before the first clip.</summary>
    public Quaternion RootOffsetRotation
    {
        get => _rootOffsetRotation;
        set => SetField(ref _rootOffsetRotation, value);
    }
}
