namespace XREngine.Animation;

public abstract partial class MotionBase
{
    /// <summary>
    /// Evaluates one graph occurrence from its state-owned unbounded clock. Blend-tree
    /// children are sampled immediately at their occurrence-local speed so a shared
    /// motion asset cannot make two leaves share mutable playback time.
    /// </summary>
    internal void EvaluateAnimationValuesAtNormalizedStateTime(
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        bool additive = false)
    {
        switch (this)
        {
            case AnimationClip clip:
                clip.EvaluateClipAnimationValuesAtNormalizedStateTime(
                    variables,
                    normalizedTime,
                    additive);
                break;
            case BlendTree1D tree1D:
                tree1D.EvaluateAnimationValuesAtNormalizedStateTimeCore(
                    variables,
                    normalizedTime,
                    additive);
                break;
            case BlendTree2D tree2D:
                tree2D.EvaluateAnimationValuesAtNormalizedStateTimeCore(
                    variables,
                    normalizedTime,
                    additive);
                break;
            case BlendTreeDirect directTree:
                directTree.EvaluateAnimationValuesAtNormalizedStateTimeCore(
                    variables,
                    normalizedTime,
                    additive);
                break;
        }
    }

    /// <summary>
    /// Resolves the current state duration. Blend trees return the weighted child
    /// duration at the current parameter values, including occurrence-local speed.
    /// </summary>
    internal double GetEffectiveDurationSeconds(IDictionary<string, AnimVar> variables)
        => this switch
        {
            AnimationClip clip when float.IsFinite(clip.LengthInSeconds) && clip.LengthInSeconds > 0.0f
                => clip.LengthInSeconds,
            BlendTree1D tree1D => tree1D.GetEffectiveDurationSecondsCore(variables),
            BlendTree2D tree2D => tree2D.GetEffectiveDurationSecondsCore(variables),
            BlendTreeDirect directTree => directTree.GetEffectiveDurationSecondsCore(variables),
            _ => 0.0,
        };
}
