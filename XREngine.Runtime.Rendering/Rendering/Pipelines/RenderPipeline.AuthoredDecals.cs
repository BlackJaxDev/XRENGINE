using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public abstract partial class RenderPipeline
{
    private ulong _authoredDecalRequirementGeneration = ulong.MaxValue;
    private RenderPipelineRequirementsDeclaration? _authoredDecalDeclaration;
    private bool _requiresNativeAuthoredDecals;
    private RenderPipelineRequirements? _authoredDecalRequirements;

    /// <summary>World publication demand derived from the authored graph, independent of pipeline type.</summary>
    [Browsable(false), YamlIgnore]
    internal bool RequiresNativeAuthoredDecals
    {
        get
        {
            if (_authoredDecalRequirementGeneration != CommandGeneration ||
                !ReferenceEquals(_authoredDecalDeclaration, DeclaredRequirements) ||
                _authoredDecalRequirements is null || !_authoredDecalRequirements.AuthoredDecalDeclarationsUnchanged)
            {
                RenderPipelineRequirements requirements = CreateRequirements(RendererBackendId.WebGPU);
                if (requirements.HasConflictingAuthoredDecalConsumers)
                    throw new NotSupportedException("WebGPU.Pipeline.AuthoredDecalOwnershipConflict: the selected graph declares both native and raster DeferredDecals consumers.");
                SetField(ref _requiresNativeAuthoredDecals, requirements.Operations.Contains("native-authored-decals"), publishNotifications: false);
                SetField(ref _authoredDecalRequirementGeneration, CommandGeneration, publishNotifications: false);
                SetField(ref _authoredDecalDeclaration, DeclaredRequirements, publishNotifications: false);
                SetField(ref _authoredDecalRequirements, requirements, publishNotifications: false);
            }
            return _requiresNativeAuthoredDecals;
        }
    }
}
