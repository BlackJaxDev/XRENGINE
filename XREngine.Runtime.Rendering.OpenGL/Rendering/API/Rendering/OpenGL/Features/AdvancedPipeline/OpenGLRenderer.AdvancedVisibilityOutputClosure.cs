using System.Diagnostics.CodeAnalysis;
using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    internal bool TryCaptureAdvancedVisibilityOutputClosure(in AdvancedVisibilityStageBackendRequest request,
        out OpenGLAdvancedVisibilityOutputClosure closure, out string reason)
    {
        closure = default;
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        if (pipeline is null || _advancedOutputRegistry is null ||
            !_advancedOutputRegistry.TryGetRetainedSlot(in reservation, out OpenGLAdvancedVisibilitySlot? slot) || slot is null)
        {
            reason = "The OpenGL Advanced stage has no active frozen output slot.";
            return false;
        }
        long generation = pipeline.ResourceGeneration;
        if (slot.MonoTextureArrayAliases is not { } aliases || !aliases.MatchesFamily(in reservation, generation))
        {
            reason = "OpenGL Advanced output generation changed after fenced slot acquisition; its current views remain retained.";
            return false;
        }
        if (!TryGetLayeredTexture(pipeline, request.IdentityTargetName, ESizedInternalFormat.Rg32ui, generation, aliases, out XRTexture? identity, out uint identityId, out uint layers, out reason) ||
            !TryGetLayeredTexture(pipeline, request.MetadataTargetName, ESizedInternalFormat.R32ui, generation, aliases, out XRTexture? metadata, out uint metadataId, out uint metadataLayers, out reason) ||
            !TryGetLayeredTexture(pipeline, request.SelectionTargetName, ESizedInternalFormat.R32ui, generation, aliases, out XRTexture? selection, out uint selectionId, out uint selectionLayers, out reason) ||
            !TryGetLayeredTexture(pipeline, request.DepthTargetName, null, generation, aliases, out XRTexture? depth, out uint depthId, out uint depthLayers, out reason) ||
            !TryGetLayeredTexture(pipeline, request.CurrentDepthPyramidTargetName, ESizedInternalFormat.R32f, generation, aliases, out XRTexture? depthPyramid, out uint depthPyramidId, out uint pyramidLayers, out reason))
            return false;

        if (!ValidateCoreOutputs(identity, metadata, selection, depth, depthPyramid, layers, metadataLayers, selectionLayers, depthLayers, pyramidLayers, request.NativeViewIndex, out reason))
            return false;

        bool requireNative = request.Stage is EAdvancedRenderStage.WorkClassification or EAdvancedRenderStage.AmbientOcclusion or EAdvancedRenderStage.NativeOpaqueShading;
        XRTexture? ambientOcclusion = null, hdr = null, velocity = null, reactive = null, diagnostics = null;
        uint ambientOcclusionId = 0u, hdrId = 0u, velocityId = 0u, reactiveId = 0u, diagnosticsId = 0u;
        if (requireNative &&
            (!TryGetLayeredTexture(pipeline, request.AmbientOcclusionTargetName, ESizedInternalFormat.R8, generation, aliases, out ambientOcclusion, out ambientOcclusionId, out uint aoLayers, out reason) ||
             !TryGetLayeredTexture(pipeline, AdvancedRenderPipeline.HDRSceneTextureName, ESizedInternalFormat.Rgba16f, generation, aliases, out hdr, out hdrId, out uint hdrLayers, out reason) ||
             !TryGetLayeredTexture(pipeline, AdvancedRenderPipeline.VelocityTextureName, ESizedInternalFormat.Rg16f, generation, aliases, out velocity, out velocityId, out uint velocityLayers, out reason) ||
             !TryGetLayeredTexture(pipeline, AdvancedTemporalHistoryContract.ReactiveMaskResourceName, ESizedInternalFormat.R8, generation, aliases, out reactive, out reactiveId, out uint reactiveLayers, out reason) ||
             !TryGetLayeredTexture(pipeline, AdvancedShadingResourceNames.ShadingDiagnostics, ESizedInternalFormat.R32ui, generation, aliases, out diagnostics, out diagnosticsId, out uint diagnosticsLayers, out reason) ||
             !ValidateNativeOutputExtents(identity!, layers, ambientOcclusion!, aoLayers, hdr!, hdrLayers, velocity!, velocityLayers, reactive!, reactiveLayers, diagnostics!, diagnosticsLayers, out reason)))
            return false;

        closure = new(identity!, identityId, metadata!, metadataId, selection!, selectionId, depth!, depthId, depthPyramid!, depthPyramidId,
            ambientOcclusion, ambientOcclusionId, hdr, hdrId, velocity, velocityId, reactive, reactiveId, diagnostics, diagnosticsId,
            GetWidth(identity!), GetHeight(identity!), layers, request.NativeViewIndex);
        if (!closure.IsValid || (requireNative && !closure.HasNativeComputeOutputs))
        {
            closure = default;
            reason = "The OpenGL Advanced closure contains an invalid native texture name.";
            return false;
        }
        reason = "Ready";
        return true;
    }

    private bool TryGetLayeredTexture(XRRenderPipelineInstance pipeline, string name, ESizedInternalFormat? expectedFormat,
        long generation, OpenGLAdvancedMonoTextureArrayAliasOwner aliases, [NotNullWhen(true)] out XRTexture? texture,
        out uint bindingId, out uint layers, out string reason)
    {
        texture = null; bindingId = 0u; layers = 0u;
        if (!pipeline.Resources.TryGetTexture(name, out texture))
        { reason = $"The frozen OpenGL pipeline generation has no Advanced texture '{name}'."; return false; }
        switch (texture)
        {
            case XRTexture2DArray array when GetOrCreateAPIRenderObject(array, generateNow: true) is GLTexture2DArray glArray:
                // Generating a GL name alone does not realize texture storage.
                // Bind runs the wrapper's invalidation/upload path before DSA
                // queries or texture views can consume the output image.
                glArray.Bind();
                if (!glArray.TryGetBindingId(out bindingId) || !RawGL.IsTexture(bindingId))
                { reason = $"OpenGL Advanced array texture '{name}' has no realized native storage."; return false; }
                layers = array.Depth;
                if (!HasExpectedFormat(array.SizedInternalFormat, expectedFormat))
                { reason = $"OpenGL Advanced texture '{name}' has format {array.SizedInternalFormat}, expected {expectedFormat}."; return false; }
                if (!HasExpectedNativeFormat(bindingId, expectedFormat))
                { reason = $"OpenGL Advanced texture '{name}' has a GL storage format that does not match its frozen contract."; return false; }
                reason = "Ready";
                return true;
            case XRTexture2D mono when GetOrCreateAPIRenderObject(mono, generateNow: true) is GLTexture2D glMono:
                glMono.Bind();
                if (!glMono.TryGetBindingId(out uint sourceId) || !RawGL.IsTexture(sourceId))
                { reason = $"OpenGL Advanced mono texture '{name}' has no realized native storage."; return false; }
                if (!HasExpectedFormat(mono.SizedInternalFormat, expectedFormat))
                { reason = $"OpenGL Advanced texture '{name}' has format {mono.SizedInternalFormat}, expected {expectedFormat}."; return false; }
                if (!HasExpectedNativeFormat(sourceId, expectedFormat))
                { reason = $"OpenGL Advanced texture '{name}' has a GL storage format that does not match its frozen contract."; return false; }
                if (!aliases.TryGetOrCreate(name, mono, sourceId, generation, out bindingId, out reason)) return false;
                layers = 1u;
                return true;
            default:
                texture = null;
                reason = $"OpenGL Advanced texture '{name}' must be an immutable XRTexture2DArray or immutable mono XRTexture2D.";
                return false;
        }
    }

    private static bool ValidateCoreOutputs(XRTexture identity, XRTexture metadata, XRTexture selection, XRTexture depth, XRTexture pyramid,
        uint layers, uint metadataLayers, uint selectionLayers, uint depthLayers, uint pyramidLayers, uint view, out string reason)
    {
        if (layers == 0u || view >= layers || metadataLayers != layers || selectionLayers != layers || depthLayers != layers || pyramidLayers != layers ||
            GetWidth(identity) == 0u || GetHeight(identity) == 0u || GetWidth(metadata) != GetWidth(identity) || GetHeight(metadata) != GetHeight(identity) ||
            GetWidth(selection) != GetWidth(identity) || GetHeight(selection) != GetHeight(identity) || GetWidth(depth) != GetWidth(identity) || GetHeight(depth) != GetHeight(identity) ||
            GetWidth(pyramid) != DivideAdvancedExtentRoundUp(GetWidth(identity), 64u) || GetHeight(pyramid) != DivideAdvancedExtentRoundUp(GetHeight(identity), 64u))
        { reason = "The frozen OpenGL Advanced outputs do not have the required full-resolution/layered extent contract."; return false; }
        if (!IsDepthFormat(depth))
        { reason = "The frozen OpenGL Advanced depth texture does not use a depth-compatible format."; return false; }
        reason = "Ready";
        return true;
    }

    private static bool ValidateNativeOutputExtents(XRTexture identity, uint layers, XRTexture ao, uint aoLayers, XRTexture hdr, uint hdrLayers,
        XRTexture velocity, uint velocityLayers, XRTexture reactive, uint reactiveLayers, XRTexture diagnostics, uint diagnosticsLayers, out string reason)
    {
        if (aoLayers != layers || hdrLayers != layers || velocityLayers != layers || reactiveLayers != layers || diagnosticsLayers != layers ||
            GetWidth(ao) != GetWidth(identity) || GetHeight(ao) != GetHeight(identity) || GetWidth(hdr) != GetWidth(identity) || GetHeight(hdr) != GetHeight(identity) ||
            GetWidth(velocity) != GetWidth(identity) || GetHeight(velocity) != GetHeight(identity) || GetWidth(reactive) != GetWidth(identity) || GetHeight(reactive) != GetHeight(identity) ||
            GetWidth(diagnostics) != GetWidth(identity) || GetHeight(diagnostics) != GetHeight(identity))
        { reason = "The frozen OpenGL native Advanced outputs do not match the visibility extent and layer count."; return false; }
        reason = "Ready";
        return true;
    }

    private static bool HasExpectedFormat(ESizedInternalFormat actual, ESizedInternalFormat? expected) => expected is null || actual == expected.Value;
    private bool HasExpectedNativeFormat(uint texture, ESizedInternalFormat? expected)
    {
        if (expected is null) return true;
        RawGL.GetTextureLevelParameter(texture, 0, GLEnum.TextureInternalFormat, out int actual);
        GLEnum required = expected.Value switch
        {
            ESizedInternalFormat.Rg32ui => GLEnum.RG32ui,
            ESizedInternalFormat.R32ui => GLEnum.R32ui,
            ESizedInternalFormat.R32f => GLEnum.R32f,
            ESizedInternalFormat.R8 => GLEnum.R8,
            ESizedInternalFormat.Rgba16f => GLEnum.Rgba16f,
            ESizedInternalFormat.Rg16f => GLEnum.RG16f,
            _ => GLEnum.None,
        };
        return required != GLEnum.None && actual == (int)required;
    }
    private static bool IsDepthFormat(XRTexture texture) => texture switch
    {
        XRTexture2D mono => mono.SizedInternalFormat is ESizedInternalFormat.DepthComponent16 or ESizedInternalFormat.DepthComponent24 or ESizedInternalFormat.DepthComponent32f or ESizedInternalFormat.Depth24Stencil8 or ESizedInternalFormat.Depth32fStencil8,
        XRTexture2DArray array => array.SizedInternalFormat is ESizedInternalFormat.DepthComponent16 or ESizedInternalFormat.DepthComponent24 or ESizedInternalFormat.DepthComponent32f or ESizedInternalFormat.Depth24Stencil8 or ESizedInternalFormat.Depth32fStencil8,
        _ => false,
    };
    private static uint GetWidth(XRTexture texture) => texture switch { XRTexture2D mono => mono.Width, XRTexture2DArray array => array.Width, _ => 0u };
    private static uint GetHeight(XRTexture texture) => texture switch { XRTexture2D mono => mono.Height, XRTexture2DArray array => array.Height, _ => 0u };
    private static uint DivideAdvancedExtentRoundUp(uint value, uint divisor) => Math.Max(1u, checked((value + divisor - 1u) / divisor));
}
