namespace XREngine.Rendering;

/// <summary>Immutable uniformly timed local-TRS frames; translation/scale interpolate linearly and rotations use slerp.</summary>
public sealed class BrowserAnimationClip
{
    public const int MaximumFrames = 600;
    private readonly BrowserBoneTransform[] _frames;

    public BrowserAnimationClip(int boneCount, float durationSeconds, ReadOnlySpan<BrowserBoneTransform> frames)
    {
        if (boneCount is < 1 or > BrowserSkeleton.MaximumBones || !float.IsFinite(durationSeconds) ||
            durationSeconds is < 0.01f or > 600 || frames.Length % boneCount != 0 ||
            frames.Length / boneCount is < 2 or > MaximumFrames)
            throw new ArgumentException("A browser clip requires two to 600 full bone frames over 0.01 to 600 seconds.");
        for (int i = 0; i < frames.Length; i++)
            frames[i].Validate();
        BoneCount = boneCount;
        DurationSeconds = durationSeconds;
        FrameCount = frames.Length / boneCount;
        _frames = frames.ToArray();
    }

    public int BoneCount { get; }
    public int FrameCount { get; }
    public float DurationSeconds { get; }

    /// <summary>Samples a repeating clip. Authors include a matching final frame for a continuous loop seam.</summary>
    internal void Sample(float time, Span<BrowserBoneTransform> destination)
    {
        float frame = time / DurationSeconds * (FrameCount - 1);
        int first = Math.Min((int)frame, FrameCount - 2);
        float fraction = frame - first;
        int firstOffset = first * BoneCount;
        int nextOffset = firstOffset + BoneCount;
        for (int bone = 0; bone < BoneCount; bone++)
            destination[bone] = BrowserBoneTransform.Interpolate(_frames[firstOffset + bone], _frames[nextOffset + bone], fraction);
    }
}
