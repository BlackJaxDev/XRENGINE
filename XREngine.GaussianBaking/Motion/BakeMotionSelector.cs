using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>
/// Chooses the clips that a bake plays, and the duration and frame count of the baked motion.
/// </summary>
public static class BakeMotionSelector
{
    /// <summary>The warning when no clip has the name that the bake options ask for. The clip name follows it.</summary>
    public const string ClipNotFoundWarning = "clip-not-found:";

    /// <summary>The warning when a clip cannot play and the selector skips it. The clip name follows it.</summary>
    public const string ClipUnplayableWarning = "clip-unplayable:";

    /// <summary>The warning when the clip is longer than the maximum duration of the budget.</summary>
    public const string ClipTruncatedWarning = "clip-truncated";

    private const string IdleClipName = "idle";

    /// <summary>
    /// Chooses the clip that an avatar or item bake plays.
    /// </summary>
    /// <remarks>
    /// <para>The candidates, in depth-first order, are the clips of the <see cref="AnimationClipComponent"/>s, and then
    /// the <see cref="AnimationClip"/> motions of the <see cref="AnimStateMachineComponent"/> states. A component that is
    /// not active, or that is on a node that is not active (up to <paramref name="root"/>), is not a candidate, because
    /// the engine does not play it. A state machine clip plays through a temporary <see cref="AnimationClipComponent"/>
    /// that refers to the state machine node. The node does not list that component, so it does not tick and later
    /// searches do not find it. The choice gives that component in <see cref="BakeMotionChoice.TemporaryPlayers"/>; call
    /// <see cref="ReleaseTemporaryPlayers"/> when the bake is complete.</para>
    /// <para>A clip is playable when its length is more than 0 and
    /// <see cref="AnimationClipComponent.TryValidatePlaybackCapabilities(out string)"/> passes, for example when a
    /// humanoid clip has a sibling <see cref="HumanoidComponent"/>. A checked clip that is not playable adds the warning
    /// <see cref="ClipUnplayableWarning"/> and the selector goes to the next candidate.</para>
    /// <para>The order of preference is: the first playable clip with the name <paramref name="requestedClip"/>
    /// (any letter case), then the first playable clip with the name <c>idle</c> (any letter case), then the first
    /// playable clip. When no clip has the requested name, the warning is <see cref="ClipNotFoundWarning"/>. When no
    /// clip is playable, the choice is one still frame.</para>
    /// <para>The duration is the clip length, but not more than <see cref="GaussianBakeBudget.MaxDurationSeconds"/>
    /// (with the warning <see cref="ClipTruncatedWarning"/>). The frame count is the duration × the budget frame rate,
    /// rounded and then made a multiple of 8, with a minimum of 8.</para>
    /// </remarks>
    /// <param name="root">The root node of the prefab instance.</param>
    /// <param name="requestedClip">The clip name from the bake options, or null.</param>
    /// <param name="budget">The budget of the bake kind.</param>
    /// <param name="warnings">The collection that receives the warnings.</param>
    public static BakeMotionChoice ChooseForPrefab(SceneNode root, string? requestedClip, GaussianBakeBudget budget, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(warnings);

        List<BakeClipCandidate> candidates = CollectCandidates(root);
        BakeClipCandidate? chosen = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(requestedClip))
            {
                chosen = FindPlayable(candidates, requestedClip, warnings, out bool nameFound);
                if (!nameFound)
                    warnings.Add(ClipNotFoundWarning + requestedClip);
                if (chosen is not null)
                    return CreateClipChoice(chosen, BakeMotionSources.Option, budget, warnings);
            }

            chosen = FindPlayable(candidates, IdleClipName, warnings, out _);
            if (chosen is not null)
                return CreateClipChoice(chosen, BakeMotionSources.Idle, budget, warnings);

            chosen = FindPlayable(candidates, name: null, warnings, out _);
            if (chosen is not null)
                return CreateClipChoice(chosen, BakeMotionSources.First, budget, warnings);

            return CreateStill(budget);
        }
        finally
        {
            ReleaseUnusedPlayers(candidates, chosen);
        }
    }

    /// <summary>
    /// Chooses the clips that a world bake plays. Every playable <see cref="AnimationClipComponent"/> of every scene
    /// plays at the same time, for <see cref="GaussianBakeBudget.MaxDurationSeconds"/>. Each one plays at the frame
    /// time modulo its own length. A component that is not active, or that is on a node that is not active, does not
    /// play. With no playable clip, the choice is one still frame.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="budget">The budget of the world kind.</param>
    public static BakeMotionChoice ChooseForWorld(XRWorld world, GaussianBakeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(budget);

        List<AnimationClipComponent> players = [];
        foreach (XRScene? scene in world.Scenes)
        {
            if (scene is null)
                continue;

            foreach (SceneNode? sceneRoot in scene.RootNodes)
            {
                if (sceneRoot is not SceneNode root)
                    continue;

                root.IterateComponents<AnimationClipComponent>(component =>
                {
                    if (component.Animation is { } clip
                        && IsEnabled(component, root)
                        && HasPlayableLength(clip)
                        && component.TryValidatePlaybackCapabilities(out _))
                    {
                        players.Add(component);
                    }
                }, true);
            }
        }

        if (players.Count == 0)
            return CreateStill(budget);

        float duration = budget.MaxDurationSeconds;
        int frameCount = FrameCountFor(duration, budget.FrameRate);
        return new BakeMotionChoice(players, BakeMotionSources.World, null, duration, frameCount, frameCount / duration);
    }

    /// <summary>
    /// Destroys the temporary players of a choice (<see cref="BakeMotionChoice.TemporaryPlayers"/>), as the selector
    /// destroys the temporary players that it does not choose: the engine destroys a queued object at its next
    /// destruction pass. The components that the content lists stay. A second call does nothing.
    /// </summary>
    /// <param name="choice">The choice of <see cref="ChooseForPrefab"/> or <see cref="ChooseForWorld"/>.</param>
    public static void ReleaseTemporaryPlayers(BakeMotionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        foreach (AnimationClipComponent player in choice.TemporaryPlayers)
        {
            if (!player.IsDestroyed && !player.IsDestroyQueued)
                player.Destroy();
        }
    }

    /// <summary>
    /// Lists the clips of the clip components, and then the clips of the state machine states, in depth-first order.
    /// A state machine clip that a component on the same node already plays is not listed again. Components that the
    /// engine does not play (see <see cref="IsEnabled"/>) are left out.
    /// </summary>
    private static List<BakeClipCandidate> CollectCandidates(SceneNode root)
    {
        List<BakeClipCandidate> candidates = [];
        root.IterateComponents<AnimationClipComponent>(component =>
        {
            if (component.Animation is { } clip && IsEnabled(component, root))
                candidates.Add(new BakeClipCandidate(clip, component, component.SceneNode));
        }, true);

        root.IterateComponents<AnimStateMachineComponent>(machine =>
        {
            if (machine.StateMachine is not { } stateMachine || !IsEnabled(machine, root))
                return;

            foreach (AnimLayer? layer in stateMachine.Layers)
            {
                if (layer is null)
                    continue;

                foreach (AnimState? state in layer.States)
                {
                    if (state?.Motion is AnimationClip clip && !ContainsClip(candidates, clip, machine.SceneNode))
                        candidates.Add(new BakeClipCandidate(clip, null, machine.SceneNode));
                }
            }
        }, true);

        return candidates;
    }

    /// <summary>
    /// Returns true when the component and every node from its node up to <paramref name="root"/> are active, as for a
    /// component that the engine plays. The content does not have to be in a world.
    /// </summary>
    internal static bool IsEnabled(XRComponent component, SceneNode root)
    {
        if (!component.IsActive)
            return false;

        for (SceneNode? node = component.SceneNode; node is not null; node = node.Parent)
        {
            if (!node.IsActiveSelf)
                return false;
            if (ReferenceEquals(node, root))
                break;
        }

        return true;
    }

    private static bool ContainsClip(List<BakeClipCandidate> candidates, AnimationClip clip, SceneNode node)
    {
        foreach (BakeClipCandidate candidate in candidates)
        {
            if (ReferenceEquals(candidate.Clip, clip) && ReferenceEquals(candidate.Node, node))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the first playable candidate with the name (any letter case), or the first playable candidate when
    /// <paramref name="name"/> is null.
    /// </summary>
    /// <param name="nameFound">True when a candidate has the name, playable or not.</param>
    private static BakeClipCandidate? FindPlayable(List<BakeClipCandidate> candidates, string? name, ICollection<string> warnings, out bool nameFound)
    {
        nameFound = false;
        foreach (BakeClipCandidate candidate in candidates)
        {
            if (name is not null && !string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            nameFound = true;
            if (CheckPlayable(candidate, warnings))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Checks a candidate once. A candidate that is not playable adds <see cref="ClipUnplayableWarning"/> and loses
    /// its temporary player.
    /// </summary>
    private static bool CheckPlayable(BakeClipCandidate candidate, ICollection<string> warnings)
    {
        if (candidate.Playable is bool known)
            return known;

        bool playable = HasPlayableLength(candidate.Clip)
            && TryCreatePlayer(candidate)
            && candidate.Player!.TryValidatePlaybackCapabilities(out _);
        candidate.Playable = playable;
        if (!playable)
        {
            warnings.Add(ClipUnplayableWarning + candidate.Name);
            ReleaseTemporaryPlayer(candidate);
        }

        return playable;
    }

    private static bool HasPlayableLength(AnimationClip clip)
        => float.IsFinite(clip.LengthInSeconds) && clip.LengthInSeconds > 0.0f;

    /// <summary>
    /// Sets the player of the candidate. A state machine clip gets a new animation clip component that refers to the
    /// state machine node but that the node does not list.
    /// </summary>
    private static bool TryCreatePlayer(BakeClipCandidate candidate)
    {
        if (candidate.Component is not null)
        {
            candidate.Player = candidate.Component;
            return true;
        }

        AnimationClipComponent? player = candidate.Node.AddComponent<AnimationClipComponent>();
        if (player is null)
            return false;

        candidate.Node.DetachComponent(player);
        player.Animation = candidate.Clip;
        candidate.Player = player;
        return true;
    }

    private static void ReleaseUnusedPlayers(List<BakeClipCandidate> candidates, BakeClipCandidate? chosen)
    {
        foreach (BakeClipCandidate candidate in candidates)
        {
            if (!ReferenceEquals(candidate, chosen))
                ReleaseTemporaryPlayer(candidate);
        }
    }

    /// <summary>Destroys the temporary player of a state machine clip. The component of a clip component candidate stays.</summary>
    private static void ReleaseTemporaryPlayer(BakeClipCandidate candidate)
    {
        if (candidate.Component is null && candidate.Player is { } temporary)
            temporary.Destroy();
        candidate.Player = null;
    }

    private static BakeMotionChoice CreateClipChoice(BakeClipCandidate candidate, string source, GaussianBakeBudget budget, ICollection<string> warnings)
    {
        float duration = candidate.Clip.LengthInSeconds;
        if (duration > budget.MaxDurationSeconds)
        {
            duration = budget.MaxDurationSeconds;
            warnings.Add(ClipTruncatedWarning);
        }

        int frameCount = FrameCountFor(duration, budget.FrameRate);
        AnimationClipComponent player = candidate.Player!;
        return new BakeMotionChoice([player], source, candidate.Clip.Name, duration, frameCount, frameCount / duration)
        {
            TemporaryPlayers = candidate.Component is null ? [player] : [],
        };
    }

    private static BakeMotionChoice CreateStill(GaussianBakeBudget budget)
        => new([], BakeMotionSources.None, null, 0.0f, 1, budget.FrameRate);

    /// <summary>Returns max(8, the rounded frame count made up to a multiple of 8).</summary>
    private static int FrameCountFor(float durationSeconds, float frameRate)
    {
        long frames = (long)Math.Round((double)durationSeconds * frameRate, MidpointRounding.AwayFromZero);
        long multipleOf8 = (frames + 7L) / 8L * 8L;
        return (int)Math.Max(8L, multipleOf8);
    }
}
