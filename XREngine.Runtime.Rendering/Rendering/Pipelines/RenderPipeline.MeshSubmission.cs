using System.ComponentModel;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public abstract partial class RenderPipeline
{
    private ulong _meshSubmissionRequirementGeneration = ulong.MaxValue;
    private RenderPipelineRequirementsDeclaration? _meshSubmissionRequirementDeclaration;
    private bool _requiresGpuMeshSubmissionPublication;

    /// <summary>Requests the material-independent resident projection only for an authored meshlet consumer.</summary>
    [Browsable(false), YamlIgnore]
    internal bool RequiresGpuMeshSubmissionPublication
    {
        get
        {
            if (AppliedMeshSubmissionStrategy is EMeshSubmissionStrategy.GpuMeshletZeroReadback or EMeshSubmissionStrategy.GpuMeshletInstrumented)
                return true;
            if (_meshSubmissionRequirementGeneration != CommandGeneration ||
                !ReferenceEquals(_meshSubmissionRequirementDeclaration, DeclaredRequirements))
            {
                RenderPipelineRequirements requirements = CreateRequirements(RendererBackendId.WebGPU);
                SetField(ref _requiresGpuMeshSubmissionPublication, requirements.Operations.Contains("gpu-meshlet-meshes"), publishNotifications: false);
                SetField(ref _meshSubmissionRequirementGeneration, CommandGeneration, publishNotifications: false);
                SetField(ref _meshSubmissionRequirementDeclaration, DeclaredRequirements, publishNotifications: false);
            }
            return _requiresGpuMeshSubmissionPublication;
        }
    }
}
