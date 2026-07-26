namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents the dispatch parameters for the Vulkan upscale bridge.
/// </summary>
internal readonly struct VulkanUpscaleBridgeDispatchParameters
{
    /// <summary>
    /// The vendor of the Vulkan upscale bridge.
    /// </summary>
    public EVulkanUpscaleBridgeVendor Vendor { get; init; }
    /// <summary>
    /// The width of the input image.
    /// </summary>
    public uint InputWidth { get; init; }
    /// <summary>
    /// The height of the input image.
    /// </summary>
    public uint InputHeight { get; init; }
    /// <summary>
    /// The width of the output image.
    /// </summary>
    public uint OutputWidth { get; init; }
    /// <summary>
    /// The height of the output image.
    /// </summary>
    public uint OutputHeight { get; init; }
    /// <summary>
    /// The index of the current frame.
    /// </summary>
    public uint FrameIndex { get; init; }
    /// <summary>
    /// Indicates whether the history should be reset.
    /// </summary>
    public bool ResetHistory { get; init; }
    /// <summary>
    /// Indicates whether the output is in HDR.
    /// </summary>
    public bool OutputHdr { get; init; }
    /// <summary>
    /// Indicates whether the depth is reversed.
    /// </summary>
    public bool ReverseDepth { get; init; }
    /// <summary>
    /// Indicates whether the camera is orthographic.
    /// </summary>
    public bool IsOrthographic { get; init; }
    /// <summary>
    /// Indicates whether there is an exposure texture.
    /// </summary>
    public bool HasExposureTexture { get; init; }
    /// <summary>
    /// The DLSS quality mode.
    /// </summary>
    public EDlssQualityMode DlssQuality { get; init; }
    /// <summary>
    /// The XeSS quality mode.
    /// </summary>
    public EXessQualityMode XessQuality { get; init; }
    /// <summary>
    /// The DLSS sharpness value.
    /// </summary>
    public float DlssSharpness { get; init; }
    /// <summary>
    /// The XeSS sharpness value.
    /// </summary>
    public float XessSharpness { get; init; }
    /// <summary>
    /// The exposure scale factor.
    /// </summary>
    public float ExposureScale { get; init; }
    /// <summary>
    /// The scale factor for the motion vector in the X direction.
    /// </summary>
    public float MotionVectorScaleX { get; init; }
    /// <summary>
    /// The scale factor for the motion vector in the Y direction.
    /// </summary>
    public float MotionVectorScaleY { get; init; }
    /// <summary>
    /// The jitter offset in the X direction.
    /// </summary>
    public float JitterOffsetX { get; init; }
    /// <summary>
    /// The jitter offset in the Y direction.
    /// </summary>
    public float JitterOffsetY { get; init; }
    /// <summary>
    /// The camera view to clip matrix.
    /// </summary>
    public Matrix4x4 CameraViewToClip { get; init; }
    /// <summary>
    /// The clip to camera view matrix.
    /// </summary>
    public Matrix4x4 ClipToCameraView { get; init; }
    /// <summary>
    /// The clip to previous clip matrix.
    /// </summary>
    public Matrix4x4 ClipToPrevClip { get; init; }
    /// <summary>
    /// The previous clip to clip matrix.
    /// </summary>
    public Matrix4x4 PrevClipToClip { get; init; }
    /// <summary>
    /// The camera position in world space.
    /// </summary>
    public Vector3 CameraPosition { get; init; }
    /// <summary>
    /// The camera up vector in world space.
    /// </summary>
    public Vector3 CameraUp { get; init; }
    /// <summary>
    /// The camera right vector in world space.
    /// </summary>
    public Vector3 CameraRight { get; init; }
    /// <summary>
    /// The camera forward vector in world space.
    /// </summary>
    public Vector3 CameraForward { get; init; }
    /// <summary>
    /// The camera near plane distance.
    /// </summary>
    public float CameraNear { get; init; }
    /// <summary>
    /// The camera far plane distance.
    /// </summary>
    public float CameraFar { get; init; }
    /// <summary>
    /// The camera field of view in radians.
    /// </summary>
    public float CameraFovRadians { get; init; }
    /// <summary>
    /// The camera aspect ratio.
    /// </summary>
    public float CameraAspectRatio { get; init; }
}
