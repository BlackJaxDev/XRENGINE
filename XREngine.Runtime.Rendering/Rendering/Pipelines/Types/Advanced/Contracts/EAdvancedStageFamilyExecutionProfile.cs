namespace XREngine.Rendering;

/// <summary>
/// Freezes the terminal environment for an Advanced command family before the
/// family builds its command and resource layout.
/// </summary>
internal enum EAdvancedStageFamilyExecutionProfile
{
    DesktopPresent,
    OpenXrTwoPassEye,
}
