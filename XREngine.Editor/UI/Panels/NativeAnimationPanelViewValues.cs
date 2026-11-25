using XREngine.Animation;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Stores an Animation panel's local editing view for one transfer.</summary>
internal readonly record struct NativeAnimationPanelViewValues(
    AnimationClip? Clip,
    ObjectConstructionIdentity? ClipIdentity,
    float ClipLength,
    int SampleRate,
    AnimationMember? RootMember,
    AnimationMember? Member,
    BasePropAnim? Animation,
    ObjectConstructionIdentity? AnimationIdentity,
    Keyframe? Key,
    BaseKeyframeTrack? Track,
    int MemberPage,
    int KeyPage,
    bool GraphView,
    float PlotZoom,
    float PlotWindowStart,
    bool StretchLengthEdits,
    int ValueChannel,
    NativeAnimationCandidateView Candidates,
    NativeAnimationSearchViewValues? Search = null)
{
    internal bool HasValidScalars =>
        (Clip is null ? ClipIdentity is null && RootMember is null && Member is null &&
            Animation is null && AnimationIdentity is null && Key is null &&
            Track is null && ClipLength == 0 && SampleRate == 0 :
            ClipIdentity?.IsCurrent == true &&
            ReferenceEquals(ClipIdentity.Value.Target, Clip) &&
            !Clip.IsDestroyed && !Clip.IsDestroyQueued &&
            ReferenceEquals(Clip.RootMember, RootMember) &&
            Clip.LengthInSeconds == ClipLength && Clip.SampleRate == SampleRate) &&
        Candidates.IsCurrent && (Search is null || Search.Value.IsValid) &&
        float.IsFinite(ClipLength) && ClipLength >= 0 &&
        MemberPage >= 0 && KeyPage >= 0 &&
        float.IsFinite(PlotZoom) && PlotZoom is >= 1 and <= 32 &&
        float.IsFinite(PlotWindowStart) && PlotWindowStart >= 0 &&
        ValueChannel is >= -1 and <= 3 &&
        (Member is not null || Animation is null && Key is null && Track is null) &&
        (Member is null || ReferenceEquals(Member.Animation, Animation)) &&
        (Animation is null ? AnimationIdentity is null :
            AnimationIdentity?.IsCurrent == true &&
            !Animation.IsDestroyed && !Animation.IsDestroyQueued &&
            ReferenceEquals(AnimationIdentity.Value.Target, Animation)) &&
        (Animation is not null || Track is null) &&
        (Key is null || Track is not null &&
            ReferenceEquals(Key.OwningTrack, Track));
}
