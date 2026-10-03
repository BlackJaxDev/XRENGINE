namespace XREngine.Rendering;

/// <summary>
/// Validates an engine-authored lit-color material and reads its numeric surface
/// without building a backend-specific material or allocating during steady-state reads.
/// A binding is owned by one render consumer; it is not a cross-thread snapshot.
/// </summary>
public sealed class StandardLitColorSurfaceBinding
{
    private StandardLitColorSurfaceReader _reader;

    private StandardLitColorSurfaceBinding(XRMaterial material, bool authoredCooked = false)
        => _reader = new(material, authoredCooked);

    /// <summary>
    /// Admits only an explicitly tagged material with one complete, unmixed factory
    /// parameter layout. A failed admission never silently chooses an alternate shader.
    /// </summary>
    public static bool TryCreate(XRMaterial material, out StandardLitColorSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        StandardLitColorSurfaceBinding candidate = new(material);
        if (!candidate.TryRead(out _, out reason))
        {
            binding = null;
            return false;
        }
        binding = candidate;
        return true;
    }

    /// <summary>Reads the same PBR inputs from an authored material with an exact cooked shader companion.</summary>
    public static bool TryCreateAuthoredCooked(XRMaterial material, out StandardLitColorSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        StandardLitColorSurfaceBinding candidate = new(material, authoredCooked: true);
        if (!candidate.TryRead(out _, out reason)) { binding = null; return false; }
        binding = candidate;
        return true;
    }

    /// <summary>Reads current values through the binding's cached schema.</summary>
    public bool TryRead(out StandardLitColorSurface surface, out string? reason)
        => _reader.TryRead(out surface, out reason);

    /// <summary>Validates and reads a live surface without allocating a binding.</summary>
    public static bool TryRead(XRMaterial material, out StandardLitColorSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        StandardLitColorSurfaceReader reader = new(material);
        return reader.TryRead(out surface, out reason);
    }

    /// <summary>Reads authored PBR factors without allocating when an exact cooked companion owns shader selection.</summary>
    public static bool TryReadAuthoredCooked(XRMaterial material, out StandardLitColorSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        StandardLitColorSurfaceReader reader = new(material, authoredCooked: true);
        return reader.TryRead(out surface, out reason);
    }
}
