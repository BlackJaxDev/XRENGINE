namespace XREngine.Animation;

public partial class AnimStateMachine
{
    /// <summary>
    /// Validates every unique clip reachable through state and blend-tree
    /// motions before the state machine begins evaluation.
    /// </summary>
    public bool TryValidateUnityImportCapabilities(
        out string diagnostic,
        out bool requiresHumanoidAvatar)
    {
        var visited = new HashSet<MotionBase>(ReferenceEqualityComparer.Instance);
        requiresHumanoidAvatar = false;
        for (int layerIndex = 0; layerIndex < Layers.Count; layerIndex++)
        {
            AnimLayer? layer = Layers[layerIndex];
            if (layer is null)
                continue;

            for (int stateIndex = 0; stateIndex < layer.States.Count; stateIndex++)
            {
                AnimState? state = layer.States[stateIndex];
                if (state?.Motion is null)
                    continue;

                if (!TryValidateMotion(
                    state.Motion,
                    visited,
                    ref requiresHumanoidAvatar,
                    out diagnostic))
                {
                    diagnostic =
                        $"Layer {layerIndex}, state '{state.Name}' contains a non-executable Unity motion: {diagnostic}";
                    return false;
                }
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryValidateMotion(
        MotionBase motion,
        HashSet<MotionBase> visited,
        ref bool requiresHumanoidAvatar,
        out string diagnostic)
    {
        if (!visited.Add(motion))
        {
            diagnostic = string.Empty;
            return true;
        }

        switch (motion)
        {
            case AnimationClip clip:
                requiresHumanoidAvatar |= clip.UnityImportManifest?.RequiresHumanoidAvatar == true
                    || clip.HasMuscleChannels
                    || clip.HasRootMotion
                    || clip.HasIKGoals;
                if (!clip.TryValidateUnityPlaybackCapabilities(out diagnostic))
                {
                    diagnostic = $"Clip '{clip.Name}': {diagnostic}";
                    return false;
                }
                break;

            case BlendTree1D tree1D:
                for (int i = 0; i < tree1D.Children.Count; i++)
                {
                    MotionBase? child = tree1D.Children[i].Motion;
                    if (child is not null
                        && !TryValidateMotion(child, visited, ref requiresHumanoidAvatar, out diagnostic))
                        return false;
                }
                break;

            case BlendTree2D tree2D:
                for (int i = 0; i < tree2D.Children.Count; i++)
                {
                    MotionBase? child = tree2D.Children[i].Motion;
                    if (child is not null
                        && !TryValidateMotion(child, visited, ref requiresHumanoidAvatar, out diagnostic))
                        return false;
                }
                break;

            case BlendTreeDirect directTree:
                for (int i = 0; i < directTree.Children.Count; i++)
                {
                    MotionBase? child = directTree.Children[i].Motion;
                    if (child is not null
                        && !TryValidateMotion(child, visited, ref requiresHumanoidAvatar, out diagnostic))
                        return false;
                }
                break;
        }

        diagnostic = string.Empty;
        return true;
    }
}
