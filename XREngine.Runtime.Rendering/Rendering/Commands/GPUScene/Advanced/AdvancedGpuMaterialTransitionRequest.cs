using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// One unique desired material variant in a whole-scene ownership transition.
/// The publisher resolves <see cref="MaterialHandle"/> during preflight.
/// </summary>
internal struct AdvancedGpuMaterialTransitionRequest(
    XRMaterial? material,
    MaterialBindingLayout layout,
    EAdvancedMaterialCoverageMode coverage,
    EAdvancedMaterialRenderStateClass state,
    uint constantWordCount,
    uint textureBindingCount,
    uint acquireCount,
    bool requiresPayloadUpdate)
{
    public XRMaterial? Material = material;
    public MaterialBindingLayout Layout = layout;
    public EAdvancedMaterialCoverageMode Coverage = coverage;
    public EAdvancedMaterialRenderStateClass State = state;
    public uint ConstantWordCount = constantWordCount;
    public uint TextureBindingCount = textureBindingCount;
    public uint AcquireCount = acquireCount;
    public bool RequiresPayloadUpdate = requiresPayloadUpdate;
    public AdvancedEngineSurfaceRecord EngineSurface;
    public AdvancedUberBaseSurfaceRecord UberBaseSurface;
    public AdvancedNativeVertexMaterial NativeVertex;
    public AdvancedGpuHandle MaterialHandle = AdvancedGpuHandle.Invalid;
    internal AdvancedUberValidationStamp StrictUberValidationStamp;

    private XRMaterial? _plannedMaterial = material;
    private ulong _plannedLayoutVersion = material?.BindingLayoutVersion ?? 0ul;
    private ulong _plannedValueVersion = material?.BindingValueVersion ?? 0ul;
    private ulong _plannedResourceVersion = material?.BindingResourceVersion ?? 0ul;
    private long _plannedShaderRevision = material?.ShaderStateRevision ?? 0L;
    private long _plannedUberRevision = material?.UberStateRevision ?? 0L;

    private XRMaterial? _validatedMaterial;
    private AdvancedGpuHandle _validatedHandle;
    private ulong _validatedLayoutVersion;
    private ulong _validatedValueVersion;
    private ulong _validatedResourceVersion;
    private long _validatedShaderRevision;
    private long _validatedUberRevision;
    private bool _hasRetainedValidation;

    /// <summary>Records the source state checked by committed material reuse.</summary>
    internal void SetRetainedValidation(
        AdvancedGpuHandle handle,
        ulong layoutVersion,
        ulong valueVersion,
        ulong resourceVersion,
        long shaderRevision,
        long uberRevision)
    {
        _validatedMaterial = Material;
        _validatedHandle = handle;
        _validatedLayoutVersion = layoutVersion;
        _validatedValueVersion = valueVersion;
        _validatedResourceVersion = resourceVersion;
        _validatedShaderRevision = shaderRevision;
        _validatedUberRevision = uberRevision;
        _hasRetainedValidation = true;
    }

    /// <summary>Checks the material state captured when this request was built.</summary>
    internal readonly bool HasCurrentPlannedIdentity()
        => ReferenceEquals(_plannedMaterial, Material) &&
           _plannedLayoutVersion == (Material?.BindingLayoutVersion ?? 0ul) &&
           _plannedValueVersion == (Material?.BindingValueVersion ?? 0ul) &&
           _plannedResourceVersion == (Material?.BindingResourceVersion ?? 0ul) &&
           _plannedShaderRevision == (Material?.ShaderStateRevision ?? 0L) &&
           _plannedUberRevision == (Material?.UberStateRevision ?? 0L);

    /// <summary>Checks that a retained source and its row are still current.</summary>
    internal readonly bool HasCurrentRetainedValidation(AdvancedGpuHandle handle)
        => _hasRetainedValidation && handle.IsValid && _validatedHandle == handle &&
           ReferenceEquals(_validatedMaterial, Material) &&
           _validatedLayoutVersion == (Material?.BindingLayoutVersion ?? 0ul) &&
           _validatedValueVersion == (Material?.BindingValueVersion ?? 0ul) &&
           _validatedResourceVersion == (Material?.BindingResourceVersion ?? 0ul) &&
           _validatedShaderRevision == (Material?.ShaderStateRevision ?? 0L) &&
           _validatedUberRevision == (Material?.UberStateRevision ?? 0L);
}
