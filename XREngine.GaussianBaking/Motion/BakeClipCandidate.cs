using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>
/// One clip that <see cref="BakeMotionSelector"/> can choose: the clip of an animation clip component, or a clip
/// motion of a state machine state.
/// </summary>
internal sealed class BakeClipCandidate(AnimationClip clip, AnimationClipComponent? component, SceneNode node)
{
    /// <summary>Gets the clip.</summary>
    public AnimationClip Clip { get; } = clip;

    /// <summary>Gets the animation clip component that holds the clip. It is null for a state machine clip.</summary>
    public AnimationClipComponent? Component { get; } = component;

    /// <summary>Gets the node of the component that plays the clip.</summary>
    public SceneNode Node { get; } = node;

    /// <summary>Gets the clip name, or an empty string when the clip has no name.</summary>
    public string Name => Clip.Name ?? string.Empty;

    /// <summary>
    /// Gets or sets the component that plays the clip: <see cref="Component"/>, or a temporary component for a
    /// state machine clip. It is null until the selector checks the clip.
    /// </summary>
    public AnimationClipComponent? Player { get; set; }

    /// <summary>Gets or sets the result of the playback check. It is null until the selector checks the clip.</summary>
    public bool? Playable { get; set; }
}
