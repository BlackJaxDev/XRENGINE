using XREngine.Rendering.Compute;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Holds one unchanged-row verdict that the collect thread prepared for the
/// next Advanced resident-scene publication, with every witness sample.
/// </summary>
/// <remarks>
/// <para>
/// The prepare samples each epoch witness before it reads the first row. Each
/// writer of a witnessed input advances its epoch after its write. Thus an
/// epoch that is equal at the consume proves that no witnessed write completed
/// after the sample. The mesh, material, and options witnesses hold the values
/// that the rows compared, and the consume compares them with the live values.
/// </para>
/// <para>
/// A verdict is single use. The next publication takes it before any check,
/// so no later publication can see it. The per-row values and the mesh,
/// material, and options witnesses stay in publisher storage. The counts in
/// this value give their used length.
/// </para>
/// </remarks>
internal readonly struct AdvancedPreparedUnchangedRowGate
{
    /// <summary>Gets whether the prepare stored this verdict. The default value is not valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Gets the managed thread of the prepare. Only this thread can consume the verdict.</summary>
    public int OwnerThreadId { get; init; }

    /// <summary>Gets the completed publication count at the prepare.</summary>
    public long PublisherEpoch { get; init; }

    /// <summary>Gets the scene row-input version at the prepare.</summary>
    public long RowInputVersion { get; init; }

    /// <summary>Gets the number of command rows that the prepare walked.</summary>
    public int RowCount { get; init; }

    /// <summary>Gets the only publication frame that can consume the verdict.</summary>
    public ulong ExpectedFrameId { get; init; }

    /// <summary>Gets the late render-thread world write count at the prepare.</summary>
    public long LateRenderWorldWriteEpoch { get; init; }

    /// <summary>Gets the bone-set generation and coverage change count at the prepare.</summary>
    public long GpuDrivenBoneStateEpoch { get; init; }

    /// <summary>Gets the skinning setting at the prepare.</summary>
    public bool AllowSkinning { get; init; }

    /// <summary>Gets the compute skinning setting at the prepare.</summary>
    public bool CalculateSkinningInComputeShader { get; init; }

    /// <summary>Gets the number of mesh witnesses.</summary>
    public int MeshWitnessCount { get; init; }

    /// <summary>Gets the number of material witnesses.</summary>
    public int MaterialWitnessCount { get; init; }

    /// <summary>Gets the number of raster options witnesses.</summary>
    public int OptionsWitnessCount { get; init; }

    /// <summary>Gets the dispatcher of the covered rows, or null when no row is covered.</summary>
    public GPUPhysicsChainDispatcher? BoundsDispatcher { get; init; }
}
