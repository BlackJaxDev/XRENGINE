using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Holds the material inputs that the prepared unchanged-row walk compared
/// for one run of rows with the same material and material handle.
/// </summary>
/// <remarks>
/// Every value comes from the outputs of the row evaluation, and the prepare
/// never reads the material again. The consume reads the live material at
/// the swap and compares it with these values. Thus an accepted witness
/// proves that the swap-time gate would compare the same values.
/// </remarks>
internal struct AdvancedPreparedMaterialWitness
{
    /// <summary>The effective material of the rows, or null.</summary>
    public XRMaterial? Material;

    /// <summary>The registered material handle of the rows.</summary>
    public AdvancedGpuHandle Handle;

    /// <summary>The binding layout version that the rows compared.</summary>
    public ulong BindingLayoutVersion;

    /// <summary>The binding value version that the rows compared.</summary>
    public ulong BindingValueVersion;

    /// <summary>The binding resource version that the rows compared.</summary>
    public ulong BindingResourceVersion;

    /// <summary>The shader state revision that the rows compared.</summary>
    public long ShaderStateRevision;

    /// <summary>The uber state revision that the rows compared.</summary>
    public long UberStateRevision;

    /// <summary>The committed layout that the header check used.</summary>
    public MaterialBindingLayout? HeaderLayout;

    /// <summary>The committed coverage mode that the header check used.</summary>
    public EAdvancedMaterialCoverageMode HeaderCoverage;

    /// <summary>The committed render state class that the header check used.</summary>
    public EAdvancedMaterialRenderStateClass HeaderState;
}
