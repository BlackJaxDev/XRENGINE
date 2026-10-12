using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;
using XREngine.Core.Files;

namespace XREngine.Rendering;

/// <summary>Runtime-only auxiliary wrapper which reads the source material's exact live local-vertex inputs.</summary>
internal sealed class NativeVertexPassMaterial : XRMaterial
{
    private readonly XRShader _ownedShader;
    private readonly TextFile _ownedShaderSource;
    internal XRMaterial Source { get; }
    internal EngineNativeVertexAuxiliaryPass NativePass { get; }
    internal ShaderProgramArtifact Artifact { get; }

    internal NativeVertexPassMaterial(XRMaterial source, EngineNativeVertexAuxiliaryPass pass, ShaderProgramArtifact artifact)
    {
        Source = source;
        NativePass = pass;
        Artifact = artifact;
        Name = source.Name;
        _ownedShader = new XRShader(artifact.FragmentEntryPoint is null ? EShaderType.Vertex : EShaderType.Fragment)
            { CookedArtifactIdentity = artifact.Identity };
        _ownedShaderSource = _ownedShader.Source;
        Shaders.Add(_ownedShader);
        RenderPass = source.RenderPass;
    }

    public override void OnSettingUniforms(XRRenderProgram program)
    {
        base.OnSettingUniforms(program);
        if (!AdvancedNativeVertexMaterialSource.TryResolveAuxiliary(Source, RuntimeEngineMaterialArtifactServices.Resolver,
            NativePass, out ShaderProgramArtifact? artifact, out string reason) || artifact?.Identity != Artifact.Identity ||
            !AdvancedNativeVertexMaterialSource.TryCapture(Source, out AdvancedNativeVertexMaterial vertex, out reason))
            throw new NotSupportedException($"WebGPU.NativeVertex.AuxiliarySourceChanged: {reason} Recreate the exact source-owned auxiliary material.");
        program.Uniform(AdvancedNativeVertexMaterialSource.Input0, vertex.Inputs.Input0);
        program.Uniform(AdvancedNativeVertexMaterialSource.Input1, vertex.Inputs.Input1);
        program.Uniform(AdvancedNativeVertexMaterialSource.Input2, vertex.Inputs.Input2);
        program.Uniform(AdvancedNativeVertexMaterialSource.Input3, vertex.Inputs.Input3);
    }

    public override void Generate()
    {
        if (_ownedShaderSource is { IsDestroyed: true }) _ownedShaderSource.Generate();
        if (_ownedShader is { IsDestroyed: true }) _ownedShader.Generate();
        base.Generate();
    }

    protected override void OnDestroying()
    {
        try { base.OnDestroying(); }
        finally
        {
            try
            {
                _ownedShader.Destroy(now: true);
                if (!_ownedShader.IsDestroyed) throw new InvalidOperationException("Native vertex auxiliary shader destruction was vetoed.");
            }
            finally
            {
                _ownedShaderSource.Destroy(now: true);
                if (!_ownedShaderSource.IsDestroyed) throw new InvalidOperationException("Native vertex auxiliary source destruction was vetoed.");
            }
        }
    }
}
