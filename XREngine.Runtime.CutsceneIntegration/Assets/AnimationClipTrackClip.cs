using XREngine.Animation;

namespace XREngine.Cutscenes;

/// <summary>A span of time that plays one animation clip. The cutscene samples its own copy of the clip.</summary>
public class AnimationClipTrackClip : CutsceneClip
{
    private AnimationClip? _clip;

    /// <summary>The animation clip to play.</summary>
    public AnimationClip? Clip
    {
        get => _clip;
        set => SetField(ref _clip, value);
    }
}
