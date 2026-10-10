using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private XRRenderProgram? _authoredOrderProgram;
    private WebGpuRenderProgram? _authoredOrderApiProgram;
    private EngineAuthoredOrderGateBinding _authoredOrderBinding;
    private WebGpuRenderProgram? _surfacePublicationProgram;

    /// <summary>Warms an exact cooked alternate while retaining the original material, callbacks, and ordinary program.</summary>
    internal bool TryPrepareAuthoredOrderingProgram(out WebGpuRenderProgram program)
    {
        Generate();
        if (Data.GetEffectiveTransparencyMode() is not (Models.Materials.ETransparencyMode.AlphaBlend or
            Models.Materials.ETransparencyMode.PremultipliedAlpha or Models.Materials.ETransparencyMode.Additive))
            throw new NotSupportedException("WebGPU.AuthoredOrdering.DirectTransparencyRequired: ordered direct replay requires an admitted sorted surface with an exact order-gate companion.");
        ShaderProgramArtifact source = _apiProgram!.Artifact;
        if (_authoredOrderApiProgram is null)
        {
            ShaderProgramArtifact? gate;
            EngineAuthoredOrderGateBinding binding;
            string reason;
            if (Data.EngineSemantic == EngineMaterialSemanticIdentity.UberBaseV1)
            {
                UberBaseMaterialProfile profile = Data.CookedUberBaseProfile
                    ?? throw new NotSupportedException("WebGPU.UberBase.OrderProfileMissing: the exact prepared source profile is required.");
                gate = null;
                foreach (UberBasePassArtifact pass in profile.PassArtifacts)
                    if (pass.Pass == EngineUberBaseShaderContract.OrderPass)
                    {
                        if (gate is not null || Renderer.ShaderArtifacts?.TryResolve(pass.ArtifactIdentity, ShaderCompileTarget.WebGPUWgsl, out gate) != true)
                            throw new NotSupportedException("WebGPU.UberBase.OrderArtifactMissing: recook the unique prepared final-position companion.");
                    }
                if (gate is null || !EngineAuthoredOrderGateContract.TryValidateUberBase(Data.ID, profile, source, gate, out binding, out reason))
                    throw new NotSupportedException("WebGPU.UberBase.OrderArtifactMismatch: the ordered program does not preserve the complete prepared source.");
            }
            else
            {
                if (!EngineAuthoredOrderGateContract.TryGetKey(source, out EngineMaterialVariantKey key, out reason))
                    throw new NotSupportedException($"WebGPU.AuthoredOrdering.DirectSourceUnsupported: '{Data.Name}': {reason}");
                if (Renderer.MaterialVariants?.TryResolve(key, out gate) != true || gate is null)
                    throw new NotSupportedException($"WebGPU.AuthoredOrdering.DirectVariantMissing: '{Data.Name}' requires '{key}'; recook the browser shader catalog.");
                if (!EngineAuthoredOrderGateContract.TryValidate(source, gate, out binding, out reason) || binding.SourceSemantic != Data.EngineSemantic)
                    throw new NotSupportedException($"WebGPU.AuthoredOrdering.DirectCompanionUnsupported: '{Data.Name}': {reason}");
            }
            using IDisposable publication = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            XRRenderProgram alternate = new(false, false, Data.Shaders)
            {
                Name = Data.Name + " authored ordering",
                CookedArtifact = gate,
            };
            SetField(ref _authoredOrderProgram, alternate);
            SetField(ref _authoredOrderBinding, binding);
            SetField(ref _authoredOrderApiProgram, (WebGpuRenderProgram)Renderer.GetOrCreateAPIRenderObject(alternate)!);
        }
        if (_authoredOrderBinding.SourceArtifactIdentity != source.Identity || _authoredOrderBinding.SourceSemantic != Data.EngineSemantic)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.DirectSourceChanged: replace the material and its ordered companion at a resource-generation boundary.");
        program = _authoredOrderApiProgram
            ?? throw new InvalidOperationException("WebGPU.AuthoredOrdering.DirectProgramPending: the alternate program was not retained.");
        return program.TryPrepareForRendering();
    }

    /// <summary>Publishes live surface state into the selected exact program without replacing the material's normal schema.</summary>
    internal void PublishSurface(WebGpuRenderProgram target)
    {
        if ((!ReferenceEquals(target, _apiProgram) && !ReferenceEquals(target, _authoredOrderApiProgram)) ||
            _surfacePublicationProgram is not null)
            throw new InvalidOperationException("WebGPU.Material.SurfaceProgramUnsupported: surface publication requires this material's prepared normal or ordered program and a non-nested scope.");
        SetField(ref _surfacePublicationProgram, target, publishNotifications: false);
        try { PublishSurface(); }
        finally { SetField(ref _surfacePublicationProgram, null, publishNotifications: false); }
    }

    private void DestroyAuthoredOrderingProgram()
    {
        _authoredOrderApiProgram?.Dispose();
        _authoredOrderProgram?.Destroy();
        SetField(ref _authoredOrderApiProgram, null);
        SetField(ref _authoredOrderProgram, null);
        SetField(ref _authoredOrderBinding, default);
        SetField(ref _surfacePublicationProgram, null, publishNotifications: false);
    }
}
