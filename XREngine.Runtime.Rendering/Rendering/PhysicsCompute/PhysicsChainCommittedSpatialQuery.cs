using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Holds the last resolved committed spatial query of one renderer, with the witnesses that
/// keep it valid. A query reuses the result while the dispatcher spatial epoch, the published
/// page usability, the renderer coverage and bone generation, the mesh envelope, the scene
/// material publication, and the drawn material contracts are unchanged. A reuse takes no
/// dispatcher, scene, or material lock.
/// </summary>
/// <remarks>
/// Callers hold the instance lock while they read or write the fields. The lock belongs to one
/// renderer, so concurrent queries of different renderers do not contend.
/// </remarks>
internal sealed class PhysicsChainCommittedSpatialQuery
{
    /// <summary>The dispatcher that filled the result.</summary>
    public GPUPhysicsChainDispatcher? Dispatcher;

    /// <summary>The dispatcher spatial epoch read before the result was resolved.</summary>
    public long DispatcherEpoch;

    /// <summary>True when the fields hold a resolved result.</summary>
    public bool Filled;

    /// <summary>True when a published output page existed and was usable at resolution.</summary>
    public bool PublishedPageUsable;

    /// <summary>The renderer bone coverage at resolution.</summary>
    public bool CoverageComplete;

    /// <summary>The renderer bone buffer generation at resolution.</summary>
    public long BoneBufferGeneration;

    /// <summary>The renderer mesh at resolution.</summary>
    public XRMesh? Mesh;

    /// <summary>True when <see cref="Envelope"/> was part of the resolution.</summary>
    public bool HasEnvelope;

    /// <summary>The mesh envelope of the committed renderer state.</summary>
    public PhysicsChainMeshEnvelopeStamp Envelope;

    /// <summary>The scene that published the drawn materials, or null for direct renderer materials.</summary>
    public GPUScene? Scene;

    /// <summary>The renderer-command material publication of <see cref="Scene"/> at resolution.</summary>
    public long SceneGeneration;

    /// <summary>The drawn material snapshots that the result used.</summary>
    public readonly List<PhysicsChainDrawMaterialSnapshot> Materials = [];

    /// <summary>The combined drawn material contract that the result used.</summary>
    public PhysicsChainMaterialBoundsContract DrawnMaterial;

    /// <summary>The resolved status.</summary>
    public EPhysicsChainSpatialBoundsStatus Status;

    /// <summary>The committed bound when <see cref="Status"/> is ready.</summary>
    public AABB Bounds;

    /// <summary>The committed bound version when <see cref="Status"/> is ready.</summary>
    public ulong Version;

    /// <summary>True when the dispatcher had a committed bounds source for the renderer.</summary>
    public bool HasBoundsSource;

    /// <summary>The committed bounds source when <see cref="HasBoundsSource"/> is true.</summary>
    public PhysicsChainGpuBoundsSource BoundsSource;

    /// <summary>Releases references and marks the result as unresolved.</summary>
    public void Clear()
    {
        Filled = false;
        Dispatcher = null;
        Mesh = null;
        HasEnvelope = false;
        Envelope = default;
        Scene = null;
        Materials.Clear();
        DrawnMaterial = default;
        HasBoundsSource = false;
        BoundsSource = default;
        Bounds = default;
        Version = 0UL;
    }
}
