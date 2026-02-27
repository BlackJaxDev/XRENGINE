using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/// <summary>
/// Managed wrapper around an <c>IPLScene</c>. Owns the Phonon scene lifecycle and provides
/// methods for adding/removing static and instanced meshes.
/// <para>
/// Implements <see cref="IAudioScene"/> so it can be passed to
/// <see cref="SteamAudioProcessor.SetScene(IAudioScene?)"/>.
/// </para>
/// <para>
/// Usage pattern:
/// <code>
///   var scene = new SteamAudioScene(context, sceneType);
///   uint meshId = scene.AddStaticMesh(vertices, indices, materials, materialIndices);
///   scene.Commit();
///   processor.SetScene(scene);
///   // ... later ...
///   scene.RemoveStaticMesh(meshId);
///   scene.Commit();
/// </code>
/// </para>
/// </summary>
public sealed class SteamAudioScene : IAudioScene, IDisposable
{
    private readonly IPLContext _context;
    private IPLScene _scene;
    private bool _disposed;
    private uint _nextMeshId = 1;

    // Tracking for cleanup
    private readonly Dictionary<uint, IPLStaticMesh> _staticMeshes = [];
    private readonly Dictionary<uint, InstancedMeshEntry> _instancedMeshes = [];

    /// <summary>The raw Phonon scene handle. Access only when the scene is alive.</summary>
    public IPLScene Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scene;
        }
    }

    /// <inheritdoc />
    public bool IsCommitted { get; private set; }

    /// <summary>Number of static meshes currently registered.</summary>
    public int StaticMeshCount => _staticMeshes.Count;

    /// <summary>Number of instanced meshes currently registered.</summary>
    public int InstancedMeshCount => _instancedMeshes.Count;

    /// <summary>
    /// Creates a new Steam Audio scene.
    /// </summary>
    /// <param name="context">The Phonon context (must outlive this scene).</param>
    /// <param name="sceneType">Ray-tracer backend (default: built-in).</param>
    public SteamAudioScene(IPLContext context, IPLSceneType sceneType = IPLSceneType.IPL_SCENETYPE_DEFAULT)
    {
        _context = context;

        var settings = new IPLSceneSettings
        {
            type = sceneType,
        };

        var error = Phonon.iplSceneCreate(context, ref settings, ref _scene);
        if (error != IPLerror.IPL_STATUS_SUCCESS)
            throw new InvalidOperationException($"iplSceneCreate failed: {error}");

        Debug.WriteLine("[SteamAudioScene] Created scene.");
    }

    // ------------------------------------------------------------------
    //  Static mesh management
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds a static (non-moving) triangle mesh to the scene.
    /// </summary>
    /// <param name="vertices">World-space vertex positions.</param>
    /// <param name="triangleIndices">Triangle index triplets (every 3 ints = 1 triangle).</param>
    /// <param name="materials">Acoustic materials for this mesh.</param>
    /// <param name="perTriangleMaterialIndices">
    /// Per-triangle index into <paramref name="materials"/>.
    /// Length must equal <c>triangleIndices.Length / 3</c>.
    /// If null, all triangles use material index 0.
    /// </param>
    /// <returns>An opaque mesh ID that can be used to remove the mesh later.</returns>
    public unsafe uint AddStaticMesh(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> triangleIndices,
        ReadOnlySpan<SteamAudioMaterial> materials,
        ReadOnlySpan<int> perTriangleMaterialIndices = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (vertices.IsEmpty)
            throw new ArgumentException("Vertices must not be empty.", nameof(vertices));
        if (triangleIndices.Length < 3 || triangleIndices.Length % 3 != 0)
            throw new ArgumentException("Triangle indices must be a multiple of 3.", nameof(triangleIndices));
        if (materials.IsEmpty)
            throw new ArgumentException("At least one material is required.", nameof(materials));

        int numTriangles = triangleIndices.Length / 3;

        // Default material indices: all zero
        bool ownedMaterialIndices = perTriangleMaterialIndices.IsEmpty;
        Span<int> matIndices = ownedMaterialIndices
            ? new int[numTriangles] // all default to 0
            : perTriangleMaterialIndices.ToArray();

        // Convert Vector3 → IPLVector3 (same layout, but explicit for safety)
        var iplVertices = new IPLVector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            iplVertices[i] = vertices[i];

        // Build flat triangle index array: Phonon expects contiguous int[3] per triangle.
        // IPLTriangle has MarshalAs(ByValArray) which makes it a managed type, so we pin
        // a flat int[] of 3 ints per triangle instead.
        var flatTriIndices = new int[numTriangles * 3];
        for (int i = 0; i < numTriangles; i++)
        {
            flatTriIndices[i * 3 + 0] = triangleIndices[i * 3];
            flatTriIndices[i * 3 + 1] = triangleIndices[i * 3 + 1];
            flatTriIndices[i * 3 + 2] = triangleIndices[i * 3 + 2];
        }

        // Build flat material array: Phonon expects contiguous IPLMaterial structs.
        // IPLMaterial has MarshalAs(ByValArray) fields, so we build a flat float[]
        // laid out as: [absorption0, absorption1, absorption2, scattering, transmission0, transmission1, transmission2] per material.
        // However, IPLStaticMeshSettings.materials expects IPLMaterial*, so we use GCHandle.
        var flatMaterials = new float[materials.Length * 7];
        for (int i = 0; i < materials.Length; i++)
        {
            var mat = materials[i].ToIPL();
            int off = i * 7;
            flatMaterials[off + 0] = mat.absorption[0];
            flatMaterials[off + 1] = mat.absorption[1];
            flatMaterials[off + 2] = mat.absorption[2];
            flatMaterials[off + 3] = mat.scattering;
            flatMaterials[off + 4] = mat.transmission[0];
            flatMaterials[off + 5] = mat.transmission[1];
            flatMaterials[off + 6] = mat.transmission[2];
        }

        // Pin arrays and create the static mesh.
        // IPLVector3 and int are unmanaged, so we can use fixed for those.
        // For materials (flat float[7] per item matching IPLMaterial layout) we use GCHandle.
        var matHandle = GCHandle.Alloc(flatMaterials, GCHandleType.Pinned);
        try
        {
            fixed (IPLVector3* vertexPtr = iplVertices)
            fixed (int* triPtr = flatTriIndices)
            fixed (int* matIdxPtr = matIndices)
            {
                var meshSettings = new IPLStaticMeshSettings
                {
                    numVertices = vertices.Length,
                    numTriangles = numTriangles,
                    numMaterials = materials.Length,
                    vertices = (IntPtr)vertexPtr,
                    triangles = (IntPtr)triPtr,
                    materialIndices = (IntPtr)matIdxPtr,
                    materials = matHandle.AddrOfPinnedObject(),
                };

                IPLStaticMesh staticMesh = default;
                var error = Phonon.iplStaticMeshCreate(_scene, ref meshSettings, ref staticMesh);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplStaticMeshCreate failed: {error}");

                Phonon.iplStaticMeshAdd(staticMesh, _scene);

                uint id = _nextMeshId++;
                _staticMeshes[id] = staticMesh;

                IsCommitted = false; // scene needs re-commit
                Debug.WriteLine($"[SteamAudioScene] Added static mesh #{id}: {vertices.Length} verts, {numTriangles} tris.");
                return id;
            }
        }
        finally
        {
            matHandle.Free();
        }
    }

    /// <summary>
    /// Removes a previously added static mesh.
    /// Call <see cref="Commit"/> afterwards to finalize the change.
    /// </summary>
    public void RemoveStaticMesh(uint meshId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_staticMeshes.Remove(meshId, out var staticMesh))
            return;

        Phonon.iplStaticMeshRemove(staticMesh, _scene);
        Phonon.iplStaticMeshRelease(ref staticMesh);
        IsCommitted = false;

        Debug.WriteLine($"[SteamAudioScene] Removed static mesh #{meshId}.");
    }

    // ------------------------------------------------------------------
    //  Instanced mesh management
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds an instanced (movable) mesh to the scene. The geometry is defined by a sub-scene
    /// that is placed in the world via the provided transform.
    /// </summary>
    /// <param name="subScene">A scene containing the mesh geometry to instance.</param>
    /// <param name="worldMatrix">Initial world transform (4×4 column-major).</param>
    /// <returns>An opaque mesh ID for later update/removal.</returns>
    public uint AddInstancedMesh(SteamAudioScene subScene, Matrix4x4 worldMatrix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(subScene);

        var iplTransform = ToIPLMatrix(worldMatrix);
        var settings = new IPLInstancedMeshSettings
        {
            subScene = subScene.Handle,
            transform = iplTransform,
        };

        IPLInstancedMesh instancedMesh = default;
        var error = Phonon.iplInstancedMeshCreate(_scene, ref settings, ref instancedMesh);
        if (error != IPLerror.IPL_STATUS_SUCCESS)
            throw new InvalidOperationException($"iplInstancedMeshCreate failed: {error}");

        Phonon.iplInstancedMeshAdd(instancedMesh, _scene);

        uint id = _nextMeshId++;
        _instancedMeshes[id] = new InstancedMeshEntry(instancedMesh, subScene);
        IsCommitted = false;

        Debug.WriteLine($"[SteamAudioScene] Added instanced mesh #{id}.");
        return id;
    }

    /// <summary>
    /// Updates the transform of an instanced mesh. Call <see cref="Commit"/> afterwards.
    /// </summary>
    public void UpdateInstancedMeshTransform(uint meshId, Matrix4x4 worldMatrix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_instancedMeshes.TryGetValue(meshId, out var entry))
            return;

        var iplTransform = ToIPLMatrix(worldMatrix);
        Phonon.iplInstancedMeshUpdateTransform(entry.Mesh, _scene, iplTransform);
        IsCommitted = false;
    }

    /// <summary>
    /// Removes a previously added instanced mesh.
    /// Call <see cref="Commit"/> afterwards.
    /// </summary>
    public void RemoveInstancedMesh(uint meshId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_instancedMeshes.Remove(meshId, out var entry))
            return;

        Phonon.iplInstancedMeshRemove(entry.Mesh, _scene);
        var mesh = entry.Mesh;
        Phonon.iplInstancedMeshRelease(ref mesh);
        IsCommitted = false;

        Debug.WriteLine($"[SteamAudioScene] Removed instanced mesh #{meshId}.");
    }

    // ------------------------------------------------------------------
    //  Commit
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Phonon.iplSceneCommit(_scene);
        IsCommitted = true;

        Debug.WriteLine("[SteamAudioScene] Scene committed.");
    }

    // ------------------------------------------------------------------
    //  Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Release all static meshes
        foreach (var kvp in _staticMeshes)
        {
            var mesh = kvp.Value;
            Phonon.iplStaticMeshRemove(mesh, _scene);
            Phonon.iplStaticMeshRelease(ref mesh);
        }
        _staticMeshes.Clear();

        // Release all instanced meshes
        foreach (var kvp in _instancedMeshes)
        {
            var mesh = kvp.Value.Mesh;
            Phonon.iplInstancedMeshRemove(mesh, _scene);
            Phonon.iplInstancedMeshRelease(ref mesh);
        }
        _instancedMeshes.Clear();

        // Release the scene itself
        if (_scene.Handle != IntPtr.Zero)
            Phonon.iplSceneRelease(ref _scene);

        Debug.WriteLine("[SteamAudioScene] Disposed.");
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static IPLMatrix4x4 ToIPLMatrix(Matrix4x4 m)
    {
        // IPLMatrix4x4 is row-major: elements[row, col]
        // System.Numerics.Matrix4x4 uses M<row><col> naming
        return new IPLMatrix4x4
        {
            elements = new float[,]
            {
                { m.M11, m.M12, m.M13, m.M14 },
                { m.M21, m.M22, m.M23, m.M24 },
                { m.M31, m.M32, m.M33, m.M34 },
                { m.M41, m.M42, m.M43, m.M44 },
            }
        };
    }

    private readonly record struct InstancedMeshEntry(IPLInstancedMesh Mesh, SteamAudioScene SubScene);
}
