using XREngine.Components.Animation;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>
/// Puts content in the pose of one motion time and keeps it there until <see cref="Dispose"/>, for example while the
/// color capture renders the content. <see cref="Dispose"/> puts back the earlier pose.
/// </summary>
/// <remarks>
/// <para>Unlike <see cref="BakeFrameSampler"/>, the hold does not use a diagnostic evaluation scope: the world and the
/// render matrices receive the pose, so that a render shows it. The other animation clip components and state machine
/// components in the hierarchies are disabled, and each player that plays is paused, so that no tick changes the
/// pose. <see cref="Dispose"/> resumes the paused players, enables the components again, and puts back the blendshape
/// weights and the transforms.</para>
/// <para>Begin and dispose the hold on the thread that updates the content (see
/// <c>IBakeRenderServices.RunOnContentThreadAsync</c>). Other code that moves transforms, such as
/// an inverse kinematics solver, is not stopped.</para>
/// </remarks>
public sealed class BakePoseHold : IDisposable
{
    private readonly BakePoseSnapshot _snapshot;
    private readonly List<AnimationClipComponent> _pausedPlayers;
    private bool _disposed;

    private BakePoseHold(BakePoseSnapshot snapshot, List<AnimationClipComponent> pausedPlayers)
    {
        _snapshot = snapshot;
        _pausedPlayers = pausedPlayers;
    }

    /// <summary>Poses the content at <paramref name="timeSeconds"/> and keeps the pose.</summary>
    /// <param name="roots">The root nodes of the content. A node must not be under another node of the list.</param>
    /// <param name="choice">The motion to play.</param>
    /// <param name="timeSeconds">The time, in seconds. It must be finite and not negative.</param>
    /// <returns>The hold. Dispose it to put back the earlier pose.</returns>
    public static BakePoseHold Begin(IReadOnlyList<SceneNode> roots, BakeMotionChoice choice, float timeSeconds)
    {
        SceneNode[] contentRoots = BakeFrameSampler.CheckRoots(roots);
        ArgumentNullException.ThrowIfNull(choice);
        BakeFrameSampler.CheckTime(timeSeconds);

        BakePoseSnapshot snapshot = BakePoseSnapshot.Capture(contentRoots, choice.Players);
        List<AnimationClipComponent> paused = [];
        BakePoseHold hold = new(snapshot, paused);
        try
        {
            snapshot.DisableOtherAnimationComponents();
            foreach (AnimationClipComponent player in choice.Players)
            {
                if (player.IsPlaying && !player.IsPaused)
                {
                    player.Pause();
                    paused.Add(player);
                }
            }

            BakeFrameSampler.Pose(snapshot.Roots, choice, timeSeconds, inclusiveEnd: false);
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    /// <summary>Puts back the pose that the content had before <see cref="Begin"/>. A second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _snapshot.Restore();
        }
        finally
        {
            foreach (AnimationClipComponent player in _pausedPlayers)
                player.Resume();
        }
    }
}
