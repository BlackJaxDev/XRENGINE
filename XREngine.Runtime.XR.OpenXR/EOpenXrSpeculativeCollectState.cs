namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>State of the stereo commands collected ahead of the next OpenXR frame.</summary>
internal enum EOpenXrSpeculativeCollectState
{
    /// <summary>No commands wait for a frame.</summary>
    None,

    /// <summary>The updating buffer has commands that wait for the next frame.</summary>
    Ready,
}
