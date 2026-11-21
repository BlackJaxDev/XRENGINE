using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine.Cutscenes;

/// <summary>
/// Makes the clip copies that a cutscene samples. Slot assignment and member binding write to a clip,
/// so each binding needs its own copy, and the source asset stays unchanged.
/// </summary>
public static class CutsceneClipInstancer
{
    /// <summary>
    /// Makes one independent copy of a clip. This method allocates and reads the whole clip, so call it
    /// when a cutscene opens and never in the frame path.
    /// </summary>
    /// <param name="source">The clip to copy.</param>
    /// <returns>A copy that shares no member or animation with the source.</returns>
    /// <exception cref="InvalidOperationException">The serializer did not give a clip.</exception>
    public static AnimationClip CreateInstance(AnimationClip source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] payload = CookedBinarySerializer.Serialize(source);
        return CookedBinarySerializer.Deserialize(typeof(AnimationClip), payload) as AnimationClip
            ?? throw new InvalidOperationException($"The serializer did not make a copy of animation clip '{source.Name}'.");
    }
}
