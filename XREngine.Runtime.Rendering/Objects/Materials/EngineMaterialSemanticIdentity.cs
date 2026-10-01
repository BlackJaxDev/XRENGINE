namespace XREngine.Rendering;

/// <summary>
/// Versioned identity of engine-authored material behavior. A material without this
/// identity must use its authored shaders, regardless of parameter or shader names.
/// </summary>
public readonly record struct EngineMaterialSemanticIdentity(EngineMaterialSemantic Semantic, int Version)
{
    public static EngineMaterialSemanticIdentity None => default;

    public static EngineMaterialSemanticIdentity StandardLitColorV1 => new(EngineMaterialSemantic.StandardLitColor, 1);

    /// <summary>Rejects unknown semantics and revisions before they can select cooked code.</summary>
    public void Validate()
    {
        if (Semantic == EngineMaterialSemantic.None && Version == 0)
            return;
        if (Semantic == EngineMaterialSemantic.StandardLitColor && Version == 1)
            return;
        throw new ArgumentException($"Unsupported engine material semantic '{Semantic}' version {Version}.");
    }
}
