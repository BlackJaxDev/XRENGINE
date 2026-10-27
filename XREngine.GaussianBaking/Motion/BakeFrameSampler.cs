using XREngine.Components.Animation;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>
/// Plays the clips of a <see cref="BakeMotionChoice"/> on content and calculates the deformed surface at each frame.
/// </summary>
/// <remarks>
/// <para>At time <c>t</c>, each player evaluates at <c>t</c> modulo its clip length, with
/// <c>EvaluateAtTime(time, dispatchEvents: false)</c>. Then the root hierarchy calculates its matrices again with
/// <c>RecalculateMatrixHierarchyImmediate(true, true)</c>. When a player is not under the root, the hierarchy of
/// that player also calculates its matrices again, but the surface covers only the root.</para>
/// <para>During sampling, the other animation clip components and state machine components in these hierarchies
/// are disabled, and a transform diagnostic evaluation scope is active, so the world does not receive the sampled
/// poses. When sampling ends, also after an exception, the sampler puts back the enabled state of those components,
/// the blendshape weights, and the transforms. Do not start a sampler call from inside a visitor, because a
/// diagnostic evaluation scope cannot be nested.</para>
/// </remarks>
public static class BakeFrameSampler
{
    /// <summary>
    /// Calculates the surface at each frame of <paramref name="choice"/> and gives it to <paramref name="visit"/>.
    /// Frame <c>i</c> is at <c>i × DurationSeconds / FrameCount</c> seconds. All frames use the same
    /// <see cref="DeformedSurface"/> instance, so copy the data that must stay after the visit.
    /// </summary>
    /// <param name="root">The root node of the content.</param>
    /// <param name="choice">The motion to play.</param>
    /// <param name="visit">Receives the frame index and the surface at that frame.</param>
    public static void ForEachFrame(SceneNode root, BakeMotionChoice choice, Action<int, DeformedSurface> visit)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(visit);

        using TransformDiagnosticEvaluationScope scope = TransformBase.BeginDiagnosticEvaluation();
        BakePoseSnapshot snapshot = BakePoseSnapshot.Capture(root, choice.Players);
        try
        {
            snapshot.DisableOtherAnimationComponents();
            int frameCount = Math.Max(1, choice.FrameCount);
            DeformedSurface? surface = null;
            for (int frame = 0; frame < frameCount; frame++)
            {
                Pose(snapshot.Roots, choice, FrameTime(choice, frame));
                surface = DeformedSurfaceEvaluator.Evaluate(root, surface);
                visit(frame, surface);
            }
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// Calculates the surface at one time. The content gets its earlier pose back before the method returns.
    /// </summary>
    /// <param name="root">The root node of the content.</param>
    /// <param name="choice">The motion to play.</param>
    /// <param name="timeSeconds">The time, in seconds. It must be finite and not negative.</param>
    /// <returns>A new surface.</returns>
    public static DeformedSurface EvaluateAt(SceneNode root, BakeMotionChoice choice, float timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(choice);
        if (!float.IsFinite(timeSeconds) || timeSeconds < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(timeSeconds), timeSeconds, "The time must be finite and not negative.");

        using TransformDiagnosticEvaluationScope scope = TransformBase.BeginDiagnosticEvaluation();
        BakePoseSnapshot snapshot = BakePoseSnapshot.Capture(root, choice.Players);
        try
        {
            snapshot.DisableOtherAnimationComponents();
            Pose(snapshot.Roots, choice, timeSeconds);
            return DeformedSurfaceEvaluator.Evaluate(root);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    private static float FrameTime(BakeMotionChoice choice, int frame)
        => choice.FrameCount <= 1
            ? 0.0f
            : (float)((double)frame * choice.DurationSeconds / choice.FrameCount);

    /// <summary>Evaluates each player at the time modulo its clip length, and then updates each pose hierarchy.</summary>
    private static void Pose(IReadOnlyList<SceneNode> roots, BakeMotionChoice choice, float timeSeconds)
    {
        IReadOnlyList<AnimationClipComponent> players = choice.Players;
        for (int i = 0; i < players.Count; i++)
        {
            AnimationClipComponent player = players[i];
            float length = player.Animation?.LengthInSeconds ?? 0.0f;
            player.EvaluateAtTime(WrapTime(timeSeconds, length), dispatchEvents: false);
        }

        for (int i = 0; i < roots.Count; i++)
            roots[i].Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
    }

    /// <summary>Returns <paramref name="timeSeconds"/> modulo <paramref name="lengthSeconds"/>, in [0, length).</summary>
    private static float WrapTime(float timeSeconds, float lengthSeconds)
    {
        if (!(lengthSeconds > 0.0f) || timeSeconds < lengthSeconds)
            return timeSeconds;

        double wrapped = timeSeconds - Math.Floor((double)timeSeconds / lengthSeconds) * lengthSeconds;
        return wrapped >= lengthSeconds ? 0.0f : (float)wrapped;
    }
}
