using XREngine.Data.Rendering;
using XREngine.Data.Core;
using MemoryPack;
using YamlDotNet.Serialization;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    [RuntimeOnly, YamlIgnore, MemoryPackIgnore]
    private NativeVertexPassMaterial?[]? _nativeVertexPassMaterials;

    internal XRMaterial GetNativeVertexPassMaterial(EngineNativeVertexAuxiliaryPass pass)
    {
        int index = (int)pass;
        if (index is < 0 or >= 4)
            throw new ArgumentOutOfRangeException(nameof(pass));
        if (!AdvancedNativeVertexMaterialSource.TryResolveAuxiliary(this, RuntimeEngineMaterialArtifactServices.Resolver,
            pass, out var artifact, out string reason) || artifact is null)
            throw new NotSupportedException($"WebGPU.NativeVertex.AuxiliaryCompanionMissing: {pass}: {reason}");
        if (_nativeVertexPassMaterials is null)
            SetField(ref _nativeVertexPassMaterials, new NativeVertexPassMaterial?[4], publishNotifications: false);
        NativeVertexPassMaterial? existing = _nativeVertexPassMaterials![index];
        if (existing is not null)
        {
            if (existing.Artifact.Identity == artifact.Identity) return existing;
        }
        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        NativeVertexPassMaterial material = new(this, pass, artifact);
        RenderingParameters options = material.RenderOptions;
        options.CullMode = pass == EngineNativeVertexAuxiliaryPass.DepthNormal ? RenderOptions.CullMode : ECullMode.None;
        options.Winding = RenderOptions.Winding;
        options.AlphaToCoverage = ERenderParamUsage.Disabled;
        options.BlendModeAllDrawBuffers = BlendMode.Disabled();
        options.DepthTest.Enabled = ERenderParamUsage.Enabled;
        options.DepthTest.Function = RenderOptions.DepthTest.Function;
        options.DepthTest.UpdateDepth = true;
        options.RequiredEngineUniforms = EUniformRequirements.Camera;
        publication.Complete();
        _nativeVertexPassMaterials[index] = material;
        existing?.Destroy();
        return material;
    }

    private void DestroyNativeVertexPassMaterials(bool now)
    {
        if (_nativeVertexPassMaterials is null) return;
        for (int index = 0; index < _nativeVertexPassMaterials.Length; index++)
        {
            _nativeVertexPassMaterials[index]?.Destroy(now);
            _nativeVertexPassMaterials[index] = null;
        }
    }
}
