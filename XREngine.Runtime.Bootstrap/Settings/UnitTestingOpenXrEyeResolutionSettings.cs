namespace XREngine.Runtime.Bootstrap;

public class UnitTestingOpenXrEyeResolutionSettings
{
    public EOpenXrEyeResolutionPreset Preset { get; set; } = EOpenXrEyeResolutionPreset.RuntimeRecommended;
    public float Scale { get; set; } = 1.0f;
    public uint CustomWidth { get; set; } = 0u;
    public uint CustomHeight { get; set; } = 0u;
}
