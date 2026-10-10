namespace XREngine.Components.Capture.Lights;

/// <summary>Identifies whether a generation is produced by a GPU batch or retained cooked image data.</summary>
public enum ELightProbeIblProvenance
{
    GpuCapture,
    RetainedCookedData,
}
