using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>
/// Stores the per-clip part of an Animation panel view: the selected member and key, the member and
/// key pages, and the plot window. The panel checks the member and key against the clip before it
/// uses them again.
/// </summary>
internal readonly record struct NativeAnimationClipView(
    AnimationMember? Member,
    Keyframe? Key,
    int MemberPage,
    int KeyPage,
    float PlotZoom,
    float PlotWindowStart);
