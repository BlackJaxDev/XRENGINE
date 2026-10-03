using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IMeshDeformationBackendCapability
{
    private readonly Dictionary<(XRMeshRenderer Renderer, XRMesh Mesh), WebGpuMeshDeformation> _meshDeformations = [];
    private ShaderProgramArtifact? _meshDeformationArtifact;
    private XRRenderProgram? _meshDeformationProgram;
    private int _engineDeformationDispatchCount;
    private long _engineDeformationVertexCount;

    /// <summary>Canonical engine compute producers recorded in the latest attempted frame; excludes the reference browser scene runtime.</summary>
    public int LastEngineDeformationDispatchCount => _engineDeformationDispatchCount;
    /// <summary>Vertices processed by canonical engine deformation producers in the latest attempted frame.</summary>
    public long LastEngineDeformationVertexCount => _engineDeformationVertexCount;

    internal void CountEngineMeshDeformation(int vertices)
    {
        SetField(ref _engineDeformationDispatchCount, _engineDeformationDispatchCount + 1, publishNotifications: false);
        SetField(ref _engineDeformationVertexCount, checked(_engineDeformationVertexCount + vertices), publishNotifications: false);
    }

    /// <summary>Installs the exact content-catalog deformation kernel before any engine API objects exist.</summary>
    public void BindMeshDeformationArtifact(ShaderProgramArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || RenderObjectCache.Count != 0 || _meshDeformationProgram is not null)
            throw new InvalidOperationException("WebGPU.Deformation.AlreadyActive: bind the kernel before creating engine API objects.");
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != "skinning" ||
            artifact.ComputeEntryPoint != "skin" || artifact.VertexEntryPoint is not null ||
            artifact.FragmentEntryPoint is not null || artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.ComputeWorkgroupSize is not { X: 64, Y: 1, Z: 1 } || artifact.DescriptorBytes.IsDefaultOrEmpty)
            throw new NotSupportedException("WebGPU.Deformation.ArtifactInvalid: the catalog must supply the verified packed skinning compute contract.");
        SetField(ref _meshDeformationArtifact, artifact);
    }

    /// <inheritdoc />
    public bool TryPrepareMeshDeformation(XRMeshRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (renderer.Submeshes.Count == 0) return TryPrepareMeshDeformation(renderer, out _, record: false);
        bool ready = true;
        for (int index = 0; index < renderer.Submeshes.Count; index++)
            if (renderer.TryGetMesh(index, out XRMesh? mesh, out _) && mesh is not null)
                ready &= TryPrepareMeshDeformation(renderer, mesh, out _, record: false);
        return ready;
    }

    internal bool TryPrepareMeshDeformation(XRMeshRenderer renderer, out WebGpuMeshDeformation? deformation, bool record = true)
    {
        deformation = null;
        return renderer.Mesh is { } mesh && TryPrepareMeshDeformation(renderer, mesh, out deformation, record);
    }

    internal bool TryPrepareMeshDeformation(XRMeshRenderer renderer, XRMesh mesh,
        out WebGpuMeshDeformation? deformation, bool record = true)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(mesh);
        deformation = null;
        if (renderer.IsDestroyed || renderer.IsDestroyQueued || mesh.IsDestroyed || mesh.IsDestroyQueued)
            return false;
        var key = (renderer, mesh);
        bool skinning = mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning;
        bool blendshapes = mesh.HasBlendshapes && RuntimeEngine.Rendering.Settings.AllowBlendshapes;
        if (!skinning && !blendshapes)
        {
            if (_meshDeformations.Remove(key, out WebGpuMeshDeformation? obsolete)) obsolete.Dispose();
            return true;
        }
        if (!_engineRecording)
            return false;
        if (_meshDeformationArtifact is null)
            throw new NotSupportedException("WebGPU.Deformation.ArtifactMissing: the world package has no hash-bound packed skinning kernel.");

        renderer.EnterResourcePublicationLease();
        try
        {
            if (!renderer.TryPrepareDeformationInputs(mesh, skinning, blendshapes, record,
                out XRMeshDeformationInputSnapshot inputs, observePose: record))
                return DeformationPending();

            if (!_meshDeformations.TryGetValue(key, out deformation) ||
                !deformation.Matches(mesh, skinning, blendshapes, inputs.PaletteCount))
            {
                if (deformation is null && _meshDeformations.Count >= 1024)
                    throw new NotSupportedException("WebGPU.Deformation.OwnerCapacity: at most 1024 resident renderer/mesh deformation generations are admitted.");
                // Construct the complete replacement before retiring a prior generation.
                WebGpuMeshDeformation replacement = new(this, renderer, mesh, skinning, blendshapes, inputs.PaletteCount);
                if (deformation is not null) deformation.Dispose();
                _meshDeformations[key] = replacement;
                deformation = replacement;
            }
            XRRenderProgram? program = _meshDeformationProgram;
            if (program is null)
            {
                using IDisposable publication = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
                program = new XRRenderProgram(false, false, Array.Empty<XRShader>())
                {
                    Name = "Engine packed GPU deformation",
                    CookedArtifact = _meshDeformationArtifact,
                };
                SetField(ref _meshDeformationProgram, program);
            }
            if (!(record ? deformation.TryRecord(program, in inputs) : deformation.TryPrepare(program)))
                return DeformationPending();
            return true;
        }
        finally { renderer.ExitResourcePublicationLease(); }
    }

    private bool DeformationPending()
    {
        MarkEngineDrawPending();
        return false;
    }

    private void RetireDestroyedMeshDeformations()
    {
        SetField(ref _engineDeformationDispatchCount, 0, publishNotifications: false);
        SetField(ref _engineDeformationVertexCount, 0L, publishNotifications: false);
        // Dictionary removal is supported while enumerating on the render owner thread.
        // Destruction notifications only mark entries, so another thread never retires GPU handles.
        foreach (var (key, deformation) in _meshDeformations)
            if (key.Renderer.IsDestroyed || key.Mesh.IsDestroyed || deformation.OwnerDestroyed ||
                !key.Renderer.OwnsDeformationMesh(key.Mesh))
            {
                _meshDeformations.Remove(key);
                deformation.Dispose();
                key.Renderer.ReleaseUnusedDeformationInputs(key.Mesh);
            }
    }

    private void DestroyMeshDeformationResources()
    {
        foreach (WebGpuMeshDeformation deformation in _meshDeformations.Values) deformation.Dispose();
        _meshDeformations.Clear();
        _meshDeformationProgram?.Destroy(now: true);
        SetField(ref _meshDeformationProgram, null);
        SetField(ref _meshDeformationArtifact, null);
    }
}
