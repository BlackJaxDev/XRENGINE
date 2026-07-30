namespace XREngine.Rendering.Materials;

/// <summary>
/// Result of resolving one material texture through the active rendering backend.
/// </summary>
/// <param name="Status">Whether the reference is ready for the current draw generation.</param>
/// <param name="Reference">The backend reference. It is only draw-safe when <paramref name="Status"/> is ready.</param>
/// <param name="PublicationGeneration">The descriptor or residency generation that published the reference.</param>
/// <param name="Reason">A diagnostic reason for non-ready results.</param>
public readonly record struct MaterialTextureReferenceResolution(
    EMaterialTextureReferenceStatus Status,
    GPUMaterialTextureReference Reference,
    ulong PublicationGeneration,
    string Reason)
{
    public bool IsReady
        => Status == EMaterialTextureReferenceStatus.Ready &&
           !Reference.Equals(GPUMaterialTextureReference.None);

    public static MaterialTextureReferenceResolution Ready(
        GPUMaterialTextureReference reference,
        ulong publicationGeneration = 0ul)
        => new(
            EMaterialTextureReferenceStatus.Ready,
            reference,
            publicationGeneration,
            string.Empty);

    public static MaterialTextureReferenceResolution Pending(string reason)
        => new(
            EMaterialTextureReferenceStatus.Pending,
            GPUMaterialTextureReference.None,
            0ul,
            reason);

    public static MaterialTextureReferenceResolution Unsupported(string reason)
        => new(
            EMaterialTextureReferenceStatus.Unsupported,
            GPUMaterialTextureReference.None,
            0ul,
            reason);

    public static MaterialTextureReferenceResolution Failed(string reason)
        => new(
            EMaterialTextureReferenceStatus.Failed,
            GPUMaterialTextureReference.None,
            0ul,
            reason);
}
