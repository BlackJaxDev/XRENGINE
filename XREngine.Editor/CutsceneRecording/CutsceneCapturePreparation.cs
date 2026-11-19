using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Residency;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Makes the materials and textures of a recorded world ready before the first captured frame, and keeps the textures at
/// full resolution for the whole recording. Texture streaming must not change a texture during the capture, because two
/// frames at the same time would then look different.
/// </summary>
/// <remarks>
/// The recorder calls <see cref="PrepareWorldMaterials"/> on a worker thread before the world begins play, because the
/// Uber material setup reads shader files, and the update thread does not allow blocking file reads.
/// <see cref="RequestWorldTextures"/> runs again on the update thread after the begin play, so it also finds the materials
/// that components create at activation, for example the text meshes of UI components. Dispose the object after the
/// recording to release the texture requests.
/// </remarks>
internal sealed class CutsceneCapturePreparation : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<XRTexture2D> _requestedTextures = new(ReferenceEqualityComparer.Instance);
    private readonly List<TextureResidencyHandle> _handles = [];
    private IDisposable? _budgetLease = TextureResidencyRequests.Shared.AcquireBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);

    /// <summary>The number of texture residency requests that the preparation holds.</summary>
    public int RequestedTextureCount
    {
        get
        {
            lock (_gate)
                return _handles.Count;
        }
    }

    /// <summary>
    /// Initializes the Uber state of each material under the root nodes and requests its textures at full resolution.
    /// Call it on a worker thread.
    /// </summary>
    /// <returns>The number of distinct materials.</returns>
    public int PrepareWorldMaterials(IReadOnlyList<SceneNode> rootNodes)
        => VisitMaterials(rootNodes, initializeUberState: true);

    /// <summary>Requests the textures of each material under the root nodes at full resolution. Any thread can call it.</summary>
    public void RequestWorldTextures(IReadOnlyList<SceneNode> rootNodes)
        => VisitMaterials(rootNodes, initializeUberState: false);

    /// <summary>
    /// True when no texture upload or import runs, each requested texture is resident, and the renderer has no startup
    /// program work left.
    /// </summary>
    public bool AreTexturesAndProgramsReady(XRWindow window)
    {
        if (XRTexture2D.ActiveProgressiveUploadCount > 0 ||
            XRTexture2D.QueuedProgressiveUploadCount > 0 ||
            TextureResidencyRequests.Shared.AreStreamedTexturesImporting)
        {
            return false;
        }

        lock (_gate)
        {
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i].State == ETextureResidencyRequestState.Pending)
                    return false;
            }
        }

        return !HasPendingProgramWork(window, out _, out _);
    }

    /// <summary>A text for a timeout message with the upload, texture request, and program counts.</summary>
    public string DescribeTexturesAndPrograms(XRWindow window)
    {
        int pending = 0;
        int total;
        lock (_gate)
        {
            total = _handles.Count;
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i].State == ETextureResidencyRequestState.Pending)
                    pending++;
            }
        }

        HasPendingProgramWork(window, out int pendingStartupWork, out bool pendingAsyncPrograms);
        return $"activeUploads={XRTexture2D.ActiveProgressiveUploadCount}, " +
               $"queuedUploads={XRTexture2D.QueuedProgressiveUploadCount}, " +
               $"streamImporting={TextureResidencyRequests.Shared.AreStreamedTexturesImporting}, " +
               $"pendingTextures={pending}/{total}, " +
               $"pendingStartupWork={pendingStartupWork}, " +
               $"pendingAsyncPrograms={pendingAsyncPrograms}";
    }

    /// <summary>Releases the texture requests and the cutscene streaming budget.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            for (int i = 0; i < _handles.Count; i++)
                _handles[i].Dispose();
            _handles.Clear();
            _requestedTextures.Clear();
        }

        _budgetLease?.Dispose();
        _budgetLease = null;
    }

    private static bool HasPendingProgramWork(XRWindow window, out int pendingStartupWork, out bool pendingAsyncPrograms)
    {
        pendingStartupWork = 0;
        pendingAsyncPrograms = false;
        IRuntimeRendererHost? host = (window.Renderer as IRuntimeRendererHost) ?? (AbstractRenderer.Current as IRuntimeRendererHost);
        if (host is null ||
            !host.TryGetBackendCapability<IRendererStartupWarmupBackendCapability>(out var warmup) ||
            warmup is null)
        {
            return false;
        }

        pendingStartupWork = warmup.PendingStartupWorkCount;
        pendingAsyncPrograms = warmup.HasPendingAsyncPrograms;
        return pendingStartupWork > 0 || pendingAsyncPrograms;
    }

    private int VisitMaterials(IReadOnlyList<SceneNode> rootNodes, bool initializeUberState)
    {
        HashSet<XRMaterial> visited = new(ReferenceEqualityComparer.Instance);

        void Visit(XRMaterial? material)
        {
            if (material is null || !visited.Add(material))
                return;

            if (initializeUberState)
                material.EnsureUberStateInitialized();
            RequestTextures(material);
        }

        for (int r = 0; r < rootNodes.Count; r++)
        {
            rootNodes[r].IterateComponents(component =>
            {
                if (component is RenderableComponent renderable)
                {
                    foreach (RenderableMesh renderableMesh in renderable.Meshes)
                    {
                        Visit(renderableMesh.MaterialOverride);
                        foreach (var lod in renderableMesh.LODs)
                            Visit(lod?.Renderer?.Material);
                    }
                }

                if (component is ModelComponent { Model: { } model })
                {
                    for (int i = 0; i < model.Meshes.Count; i++)
                    {
                        if (model.Meshes[i] is not { } subMesh)
                            continue;
                        foreach (var lod in subMesh.LODs)
                            Visit(lod?.Material);
                    }
                }

                if (component is UITextComponent { Mesh.Material: { } textMaterial })
                    Visit(textMaterial);
            }, iterateChildHierarchy: true);
        }

        return visited.Count;
    }

    private void RequestTextures(XRMaterial material)
    {
        foreach (XRTexture? texture in material.Textures)
        {
            if (texture is not XRTexture2D texture2D)
                continue;

            texture2D.PreferSynchronousGpuUpload = true;
            bool hasHydratedCpuMips = texture2D.Mipmaps is { Length: > 0 } mips && mips[0]?.Data is { Length: > 4 };
            if (hasHydratedCpuMips ||
                (string.IsNullOrWhiteSpace(texture2D.OriginalPath) && string.IsNullOrWhiteSpace(texture2D.FilePath)))
            {
                continue;
            }

            lock (_gate)
            {
                if (!_requestedTextures.Add(texture2D))
                    continue;
            }

            TextureResidencyHandle? handle = TextureResidencyRequests.Shared.RequestResidency(
                texture2D,
                targetMip: 0,
                deadlineFrame: 0L,
                priority: JobPriority.Highest,
                generation: 1);
            if (handle is null)
                continue;

            lock (_gate)
                _handles.Add(handle);
        }
    }
}
