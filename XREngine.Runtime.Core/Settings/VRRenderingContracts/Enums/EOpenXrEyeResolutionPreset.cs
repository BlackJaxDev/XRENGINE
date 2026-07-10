namespace XREngine;

public enum EOpenXrEyeResolutionPreset
{
    /// <summary>
    /// Use the active OpenXR runtime's recommended image rect size.
    /// </summary>
    RuntimeRecommended,

    /// <summary>
    /// Valve Index native panel resolution, 1440 x 1600 per eye.
    /// </summary>
    ValveIndex,

    /// <summary>
    /// Meta Quest Pro native panel resolution, 1800 x 1920 per eye.
    /// </summary>
    QuestPro,

    /// <summary>
    /// Bigscreen Beyond 2 native panel resolution, 2560 x 2560 per eye.
    /// </summary>
    BigscreenBeyond2,

    /// <summary>
    /// Use the configured custom width and height.
    /// </summary>
    Custom,
}
