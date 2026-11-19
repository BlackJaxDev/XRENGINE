namespace XREngine.Rendering.Models;

/// <summary>Selects how an FBX importer obtains mesh tangents.</summary>
public enum ModelTangentImportPolicy
{
    /// <summary>Keep the selected backend's existing import behavior.</summary>
    Default,
    /// <summary>Use authored FBX tangents and do not calculate missing tangents.</summary>
    Import,
    /// <summary>Calculate compatible tangents from the final positions, normals, and UV0.</summary>
    CalculateMikkCompatible,
}
