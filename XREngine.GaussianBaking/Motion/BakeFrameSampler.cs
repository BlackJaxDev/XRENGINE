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
/// <c>EvaluateAtTime(time, dispatchEvents: false)</c>. Then each root hierarchy calculates its matrices again with
/// <c>RecalculateMatrixHierarchyImmediate(true, true)</c>. When a player is not under a root, the hierarchy of
/// that player also calculates its matrices again, but the surface covers only the roots.</para>
/// <para>Content with more than one root node, such as the scenes of a world, uses the overloads that take a list of
/// roots. The surface then has the meshes of each root in turn.</para>
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
        ForEachFrame([root], choice, visit);
    }

    /// <summary>
    /// Calculates the surface of several content roots at each frame of <paramref name="choice"/> and gives it to
    /// <paramref name="visit"/>, with the rules of <see cref="ForEachFrame(SceneNode, BakeMotionChoice, Action{int, DeformedSurface})"/>.
    /// </summary>
    /// <param name="roots">The root nodes of the content. A node must not be under another node of the list.</param>
    /// <param name="choice">The motion to play.</param>
    /// <param name="visit">Receives the frame index and the surface at that frame.</param>
    public static void ForEachFrame(IReadOnlyList<SceneNode> roots, BakeMotionChoice choice, Action<int, DeformedSurface> visit)
    {
        SceneNode[] contentRoots = CheckRoots(roots);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(visit);

        using TransformDiagnosticEvaluationScope scope = TransformBase.BeginDiagnosticEvaluation();
        BakePoseSnapshot snapshot = BakePoseSnapshot.Capture(contentRoots, choice.Players);
        try
        {
            snapshot.DisableOtherAnimationComponents();
            int frameCount = Math.Max(1, choice.FrameCount);
            DeformedSurface? surface = null;
            for (int frame = 0; frame < frameCount; frame++)
            {
                Pose(snapshot.Roots, choice, FrameTime(choice, frame), inclusiveEnd: false);
                surface = DeformedSurfaceEvaluator.EvaluateRoots(contentRoots, surface);
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
        return EvaluateAt([root], choice, timeSeconds);
    }

    /// <summary>
    /// Calculates the surface of several content roots at one time. The content gets its earlier pose back before the
    /// method returns.
    /// </summary>
    /// <param name="roots">The root nodes of the content. A node must not be under another node of the list.</param>
    /// <param name="choice">The motion to play.</param>
    /// <param name="timeSeconds">The time, in seconds. It must be finite and not negative.</param>
    /// <returns>A new surface.</returns>
    public static DeformedSurface EvaluateAt(IReadOnlyList<SceneNode> roots, BakeMotionChoice choice, float timeSeconds)
    {
        SceneNode[] contentRoots = CheckRoots(roots);
        ArgumentNullException.ThrowIfNull(choice);
        CheckTime(timeSeconds);
        return EvaluateOnce(contentRoots, choice, timeSeconds, inclusiveEnd: false);
    }

    /// <summary>
    /// Calculates the surface at the end of the baked motion: at <see cref="BakeMotionChoice.DurationSeconds"/>, where
    /// the loop starts again. Each player evaluates at that time modulo its clip length, but a time at the end of a
    /// clip gives the last pose of the clip, not the first. Compare the result with the surface at time 0 to find out
    /// if the motion loops with no jump. The content gets its earlier pose back before the method returns.
    /// </summary>
    /// <param name="roots">The root nodes of the content. A node must not be under another node of the list.</param>
    /// <param name="choice">The motion to play.</param>
    /// <returns>A new surface. For a still choice, the surface at time 0.</returns>
    public static DeformedSurface EvaluateLoopEnd(IReadOnlyList<SceneNode> roots, BakeMotionChoice choice)
    {
        SceneNode[] contentRoots = CheckRoots(roots);
        ArgumentNullException.ThrowIfNull(choice);
        float end = choice.FrameCount <= 1 ? 0.0f : choice.DurationSeconds;
        CheckTime(end);
        return EvaluateOnce(contentRoots, choice, end, inclusiveEnd: true);
    }

    /// <summary>
    /// Evaluates each player at the time (see <see cref="WrapTime"/>), and then updates each pose hierarchy, so that the
    /// world matrices and the render matrices get the pose.
    /// </summary>
    /// <param name="poseRoots">The hierarchies to update.</param>
    /// <param name="choice">The motion to play.</param>
    /// <param name="timeSeconds">The time, in seconds.</param>
    /// <param name="inclusiveEnd">True to give the last pose of a clip at the end of the clip, false to give the first pose.</param>
    internal static void Pose(IReadOnlyList<SceneNode> poseRoots, BakeMotionChoice choice, float timeSeconds, bool inclusiveEnd)
    {
        IReadOnlyList<AnimationClipComponent> players = choice.Players;
        for (int i = 0; i < players.Count; i++)
        {
            AnimationClipComponent player = players[i];
            float length = player.Animation?.LengthInSeconds ?? 0.0f;
            player.EvaluateAtTime(WrapTime(timeSeconds, length, inclusiveEnd), dispatchEvents: false);
        }

        for (int i = 0; i < poseRoots.Count; i++)
            poseRoots[i].Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
    }

    /// <summary>Returns a copy of the roots, and checks that there is at least one and that none is null.</summary>
    internal static SceneNode[] CheckRoots(IReadOnlyList<SceneNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        SceneNode[] contentRoots = [.. roots];
        if (contentRoots.Length == 0)
            throw new ArgumentException("There must be at least one root node.", nameof(roots));
        foreach (SceneNode root in contentRoots)
            ArgumentNullException.ThrowIfNull(root, nameof(roots));
        return contentRoots;
    }

    /// <summary>Checks that a time is finite and not negative.</summary>
    internal static void CheckTime(float timeSeconds)
    {
        if (!float.IsFinite(timeSeconds) || timeSeconds < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(timeSeconds), timeSeconds, "The time must be finite and not negative.");
    }

    private static DeformedSurface EvaluateOnce(SceneNode[] contentRoots, BakeMotionChoice choice, float timeSeconds, bool inclusiveEnd)
    {
        using TransformDiagnosticEvaluationScope scope = TransformBase.BeginDiagnosticEvaluation();
        BakePoseSnapshot snapshot = BakePoseSnapshot.Capture(contentRoots, choice.Players);
        try
        {
            snapshot.DisableOtherAnimationComponents();
            Pose(snapshot.Roots, choice, timeSeconds, inclusiveEnd);
            return DeformedSurfaceEvaluator.EvaluateRoots(contentRoots, reuse: null);
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

    /// <summary>
    /// Returns <paramref name="timeSeconds"/> modulo <paramref name="lengthSeconds"/>. When
    /// <paramref name="inclusiveEnd"/> is false, the result is in [0, length). When it is true, a time that is more
    /// than 0 gives a result in (0, length], so that the end of a clip gives the last pose of the clip.
    /// </summary>
    private static float WrapTime(float timeSeconds, float lengthSeconds, bool inclusiveEnd)
    {
        if (!(lengthSeconds > 0.0f))
            return timeSeconds;
        if (inclusiveEnd ? timeSeconds <= lengthSeconds : timeSeconds < lengthSeconds)
            return timeSeconds;

        double wrapped = timeSeconds - Math.Floor((double)timeSeconds / lengthSeconds) * lengthSeconds;
        if (inclusiveEnd)
            return wrapped <= 0.0 ? lengthSeconds : (float)wrapped;
        return wrapped >= lengthSeconds ? 0.0f : (float)wrapped;
    }
}
