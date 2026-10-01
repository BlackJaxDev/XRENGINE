namespace XREngine.Runtime.Bootstrap;

public class UnitTestingVrFoveationSettings
{
    public EVrFoveationMode Mode { get; set; } = EVrFoveationMode.Off;
    public EVrFoveationQualityPreset QualityPreset { get; set; } = EVrFoveationQualityPreset.Balanced;
    public bool RequireRequested { get; set; } = false;
}
