namespace XREngine.Rendering.Commands;

/// <summary>
/// How a retained texture source changed after its canonical publication, for the backend
/// source check (<see cref="AdvancedTextureResidencyDrift"/>).
/// </summary>
internal enum EAdvancedTextureDriftKind : byte
{
    /// <summary>Unchanged, or changed in a way that needs the exact comparison.</summary>
    Exact,
    /// <summary>Every change was a dense streaming resident commit.</summary>
    Residency,
    /// <summary>The published record described the loading placeholder, which never drew.</summary>
    Placeholder,
}
