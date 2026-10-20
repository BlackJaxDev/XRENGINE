using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Holds the raster inputs that the prepared unchanged-row walk compared for
/// one run of rows with the same raster options object.
/// </summary>
/// <remarks>
/// Both values come from the render state that the row evaluation created and
/// compared with its plan. The prepare never reads the options again. The
/// consume reads the live options at the swap and compares them with these values.
/// </remarks>
internal struct AdvancedPreparedOptionsWitness
{
    /// <summary>The effective raster options of the rows.</summary>
    public RenderingParameters? Options;

    /// <summary>The cull mode of the compared render state.</summary>
    public uint CullMode;

    /// <summary>The native raster-state flags of the compared render state.</summary>
    public uint RasterStateFlags;
}
