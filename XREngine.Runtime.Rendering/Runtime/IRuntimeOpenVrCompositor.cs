using XREngine.Input;

namespace XREngine;

/// <summary>Submits a stereo texture pair to the active OpenVR compositor, and fades the headset image.</summary>
public interface IRuntimeOpenVrCompositor
{
    /// <summary>
    /// True while the OpenVR runtime and its compositor are live, so a fade can run. It is false before the start and after
    /// a shutdown of the runtime.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts a compositor fade of the headset image to a color, from the current fade state. The compositor animates the fade
    /// over the given time. The fade does not change the eye cameras, the head pose, or the submitted eye textures.
    /// A call from the update thread is safe: it is one client call to the compositor process, as the input and frame statistics
    /// reads on the update side are, and it does not touch the render thread submit state. The method does not throw.
    /// </summary>
    /// <param name="seconds">The fade time in seconds. Zero changes the image at once.</param>
    /// <param name="red">The red component of the fade color, from 0 to 1.</param>
    /// <param name="green">The green component of the fade color, from 0 to 1.</param>
    /// <param name="blue">The blue component of the fade color, from 0 to 1.</param>
    /// <param name="alpha">The opacity of the fade color at the end of the fade: 0 shows the scene, and 1 shows only the color.</param>
    /// <returns>False if the compositor is not available or the call failed.</returns>
    bool TryFadeToColor(float seconds, float red, float green, float blue, float alpha);

    RuntimeOpenVrSubmitResult SubmitEyes(
        nint leftEyeHandle,
        nint rightEyeHandle,
        RuntimeOpenVrTextureType textureType,
        RuntimeOpenVrColorSpace colorSpace,
        RuntimeOpenVrSubmitFlags flags);
}
