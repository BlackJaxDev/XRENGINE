namespace XREngine.Rendering;

/// <summary>
/// Selects how engine-owned materials obtain their shader stages at construction time.
/// Cooked targets retain the material semantic and parameters without loading desktop source.
/// </summary>
public enum EngineMaterialConstructionTarget
{
    DesktopGlsl,
    WebGpuCooked,
}
