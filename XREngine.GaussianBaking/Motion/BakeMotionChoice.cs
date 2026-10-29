using XREngine.Components.Animation;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>
/// Tells which clips a bake plays and how the bake samples them. <see cref="BakeMotionSelector"/> makes it, and
/// <see cref="BakeFrameSampler"/> plays it. Frame <c>i</c> is at <c>i × DurationSeconds / FrameCount</c> seconds.
/// </summary>
/// <param name="Players">The components that play the clips. It is empty for a still frame.</param>
/// <param name="Source">Why the bake plays these clips. The value is one of <see cref="BakeMotionSources"/>.</param>
/// <param name="ClipName">The name of the clip. It is null for a world and for a still frame.</param>
/// <param name="DurationSeconds">The length of the baked motion, in seconds. It is 0 for a still frame.</param>
/// <param name="FrameCount">The number of frames: a multiple of 8, or 1 for a still frame.</param>
/// <param name="FrameRate">The number of frames per second: <c>FrameCount / DurationSeconds</c>, or the budget rate for a still frame.</param>
public sealed record BakeMotionChoice(
    IReadOnlyList<AnimationClipComponent> Players,
    string Source,
    string? ClipName,
    float DurationSeconds,
    int FrameCount,
    float FrameRate)
{
    /// <summary>
    /// Gets the players that <see cref="BakeMotionSelector"/> made for this choice and that the content does not list,
    /// for example the player of a state machine clip. <see cref="BakeMotionSelector.ReleaseTemporaryPlayers"/>
    /// destroys them when the bake does not need them any more.
    /// </summary>
    public IReadOnlyList<AnimationClipComponent> TemporaryPlayers { get; init; } = [];
}
