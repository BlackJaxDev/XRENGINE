using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// A fixed-size, immutable native mirror publication image. Every non-null source lifetime
/// retained by <see cref="AdvancedProjectiveMirrorMaterial.TryCaptureSnapshot"/> must be released
/// exactly once after the caller's whole publication transaction completes.
/// </summary>
internal readonly struct AdvancedProjectiveMirrorSnapshot(
    ulong revision,
    AdvancedProjectiveMirrorView desktop,
    AdvancedProjectiveMirrorView leftEye,
    AdvancedProjectiveMirrorView rightEye,
    AdvancedMutableTexturePublicationLifetime? desktopLifetime,
    AdvancedMutableTexturePublicationLifetime? leftEyeLifetime,
    AdvancedMutableTexturePublicationLifetime? rightEyeLifetime)
{
    internal ulong Revision { get; } = revision;
    internal AdvancedProjectiveMirrorView Desktop { get; } = desktop;
    internal AdvancedProjectiveMirrorView LeftEye { get; } = leftEye;
    internal AdvancedProjectiveMirrorView RightEye { get; } = rightEye;

    /// <summary>Gets a fixed view in desktop, left-eye, right-eye order.</summary>
    internal AdvancedProjectiveMirrorView this[int index]
        => index switch
        {
            0 => Desktop,
            1 => LeftEye,
            2 => RightEye,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A projective mirror snapshot has exactly three views."),
        };

    /// <summary>Releases the exact source generations retained during capture.</summary>
    internal void ReleaseRetainedSources()
    {
        desktopLifetime?.ReleasePublication();
        leftEyeLifetime?.ReleasePublication();
        rightEyeLifetime?.ReleasePublication();
    }
}
