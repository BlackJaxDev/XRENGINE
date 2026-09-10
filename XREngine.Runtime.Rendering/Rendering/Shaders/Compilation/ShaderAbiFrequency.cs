namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Update cadence for an engine-owned uniform resource.
/// </summary>
public enum ShaderAbiFrequency
{
    Unknown,
    Frame,
    View,
    Pass,
    Material,
    Object,
    Instance,
    RuntimeCallback,
}
