using XREngine.Input;

namespace XREngine;

/// <summary>Submits a stereo texture pair to the active OpenVR compositor, and fades the headset image.</summary>
public interface IRuntimeOpenVrCompositor
{
    /// <summary>
    /// Starts a compositor fade of the headset image to a color, from the current fade state. The compositor animates the fade
    /// over the given time. The fade does not change the eye cameras, the head pose, or the submitted eye textures.
    /// </summary>
    /// <param name="seconds">The fade time in seconds. Zero changes the image at once.</param>
    /// <param name="red">The red component of the fade color, from 0 to 1.</param>
    /// <param name="green">The green component of the fade color, from 0 to 1.</param>
    /// <param name="blue">The blue component of the fade color, from 0 to 1.</param>
    /// <param name="alpha">The opacity of the fade color at the end of the fade: 0 shows the scene, and 1 shows only the color.</param>
    void FadeToColor(float seconds, float red, float green, float blue, float alpha);

    RuntimeOpenVrSubmitResult SubmitEyes(
        nint leftEyeHandle,
        nint rightEyeHandle,
        RuntimeOpenVrTextureType textureType,
        RuntimeOpenVrColorSpace colorSpace,
        RuntimeOpenVrSubmitFlags flags);
}
