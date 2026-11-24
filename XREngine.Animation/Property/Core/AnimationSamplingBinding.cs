namespace XREngine.Animation
{
    /// <summary>
    /// One clip in a sampling layout that several clips share.
    /// </summary>
    /// <param name="Clip">The clip to sample. Use a separate instance for each binding.</param>
    /// <param name="Scope">
    /// The target identity. In one scope, clips on the same logical path share a slot, so they can
    /// blend. In different scopes, the same logical path has different slots, because the targets differ.
    /// </param>
    public readonly record struct AnimationSamplingBinding(AnimationClip Clip, string Scope = "");
}
