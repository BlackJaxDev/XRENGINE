using System.ComponentModel;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public abstract partial class RenderPipeline
{
    private ulong _meshSubmissionRequirementGeneration = ulong.MaxValue;
    private RenderPipelineRequirementsDeclaration? _meshSubmissionRequirementDeclaration;
    private bool _requiresGpuMeshSubmissionPublication;
    private bool _meshSubmissionRequirementIsCooked;
    private RenderPipelineRequirements? _meshSubmissionRequirements;

    /// <summary>Requests resident source ownership for meshlets and browser authored indexed-indirect consumers.</summary>
    [Browsable(false), YamlIgnore]
    internal bool RequiresGpuMeshSubmissionPublication
    {
        get
        {
            if (AppliedMeshSubmissionStrategy is EMeshSubmissionStrategy.GpuMeshletZeroReadback or EMeshSubmissionStrategy.GpuMeshletInstrumented)
                return true;
            bool authoredIndexed = RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked;
            if (authoredIndexed && AppliedMeshSubmissionStrategy is EMeshSubmissionStrategy.GpuIndirectZeroReadback or EMeshSubmissionStrategy.GpuIndirectInstrumented)
                return true;
            if (_meshSubmissionRequirementGeneration != CommandGeneration ||
                _meshSubmissionRequirementIsCooked != authoredIndexed ||
                !ReferenceEquals(_meshSubmissionRequirementDeclaration, DeclaredRequirements) ||
                _meshSubmissionRequirements is null || !_meshSubmissionRequirements.CommandTopologyUnchanged)
            {
                RenderPipelineRequirements requirements = CreateRequirements(RendererBackendId.WebGPU);
                SetField(ref _requiresGpuMeshSubmissionPublication, requirements.Operations.Contains("gpu-meshlet-meshes") ||
                    authoredIndexed && requirements.Programs.ContainsKey("indirect::cull-primitive"), publishNotifications: false);
                SetField(ref _meshSubmissionRequirementGeneration, CommandGeneration, publishNotifications: false);
                SetField(ref _meshSubmissionRequirementIsCooked, authoredIndexed, publishNotifications: false);
                SetField(ref _meshSubmissionRequirementDeclaration, DeclaredRequirements, publishNotifications: false);
                SetField(ref _meshSubmissionRequirements, requirements, publishNotifications: false);
            }
            return _requiresGpuMeshSubmissionPublication;
        }
    }
}
