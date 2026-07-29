namespace XREngine.Rendering;

/// <summary>
/// Lowers stable logical references into the selected backend encoding while
/// preserving one shader-facing uvec4 shape.
/// </summary>
public static class AdvancedResourceReferenceEncoder
{
    public const uint FallbackSlot = 0u;

    public static AdvancedEncodedTextureReference EncodeTexture(
        EAdvancedTextureIndirectionMode mode,
        in AdvancedTextureReference reference,
        in AdvancedBackendTexturePayload payload,
        AdvancedResourceResidencyDiagnostics? diagnostics = null)
    {
        bool stale = reference.Handle.IsValid &&
            payload.LogicalGeneration != reference.Handle.Generation;
        bool resident = reference.Handle.IsValid &&
            !stale &&
            (payload.Flags & EAdvancedResourceReferenceFlags.Resident) != 0;
        if (!resident)
        {
            diagnostics?.RecordTextureFallback(stale);
            EAdvancedResourceReferenceFlags flags =
                EAdvancedResourceReferenceFlags.Fallback |
                (stale ? EAdvancedResourceReferenceFlags.StaleGeneration : 0);
            return new(FallbackSlot, FallbackSlot, (uint)reference.Fallback, flags);
        }

        return mode switch
        {
            EAdvancedTextureIndirectionMode.TextureArray => new(
                payload.TextureArrayIndex,
                payload.TextureArrayLayer,
                payload.SamplerIndex,
                EAdvancedResourceReferenceFlags.Resident),
            EAdvancedTextureIndirectionMode.OpenGlBindlessHandles => new(
                unchecked((uint)payload.OpenGlBindlessHandle),
                unchecked((uint)(payload.OpenGlBindlessHandle >> 32)),
                payload.SamplerIndex,
                EAdvancedResourceReferenceFlags.Resident),
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing => new(
                payload.VulkanDescriptorIndex,
                payload.SamplerIndex,
                0u,
                EAdvancedResourceReferenceFlags.Resident),
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap => new(
                payload.VulkanHeapResourceIndex,
                payload.SamplerIndex,
                0u,
                EAdvancedResourceReferenceFlags.Resident),
            _ => EncodeTextureFallback(reference, diagnostics),
        };
    }

    public static AdvancedEncodedSamplerReference EncodeSampler(
        EAdvancedTextureIndirectionMode mode,
        in AdvancedSamplerReference reference,
        in AdvancedBackendSamplerPayload payload,
        AdvancedResourceResidencyDiagnostics? diagnostics = null)
    {
        bool stale = reference.Handle.IsValid &&
            payload.LogicalGeneration != reference.Handle.Generation;
        bool resident = reference.Handle.IsValid &&
            !stale &&
            (payload.Flags & EAdvancedResourceReferenceFlags.Resident) != 0;
        if (!resident)
        {
            diagnostics?.RecordSamplerFallback(stale);
            EAdvancedResourceReferenceFlags flags =
                EAdvancedResourceReferenceFlags.Fallback |
                (stale ? EAdvancedResourceReferenceFlags.StaleGeneration : 0);
            return new(FallbackSlot, 0u, 0u, flags);
        }

        return mode switch
        {
            EAdvancedTextureIndirectionMode.TextureArray or
            EAdvancedTextureIndirectionMode.OpenGlBindlessHandles => new(
                payload.OpenGlSamplerIndex,
                0u,
                0u,
                EAdvancedResourceReferenceFlags.Resident),
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing => new(
                payload.VulkanDescriptorIndex,
                0u,
                0u,
                EAdvancedResourceReferenceFlags.Resident),
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap => new(
                payload.VulkanHeapSamplerIndex,
                0u,
                0u,
                EAdvancedResourceReferenceFlags.Resident),
            _ => EncodeSamplerFallback(diagnostics),
        };
    }

    private static AdvancedEncodedTextureReference EncodeTextureFallback(
        in AdvancedTextureReference reference,
        AdvancedResourceResidencyDiagnostics? diagnostics)
    {
        diagnostics?.RecordTextureFallback(staleGeneration: false);
        return new(
            FallbackSlot,
            FallbackSlot,
            (uint)reference.Fallback,
            EAdvancedResourceReferenceFlags.Fallback);
    }

    private static AdvancedEncodedSamplerReference EncodeSamplerFallback(
        AdvancedResourceResidencyDiagnostics? diagnostics)
    {
        diagnostics?.RecordSamplerFallback(staleGeneration: false);
        return new(
            FallbackSlot,
            0u,
            0u,
            EAdvancedResourceReferenceFlags.Fallback);
    }
}
