namespace XREngine.Rendering;

public interface IRuntimeAmbientSettings
{
    ColorF3 AmbientLightColor { get; set; }
    float AmbientLightIntensity { get; set; }
}
