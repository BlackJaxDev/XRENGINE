namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Identifies a lighting contribution independently from the algorithm that produces it.
/// </summary>
[Flags]
public enum EGlobalIlluminationContribution
{
    None = 0,
    Diffuse = 1 << 0,
    Specular = 1 << 1,
    Debug = 1 << 2,
}
