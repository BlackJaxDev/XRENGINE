using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Pipeline-owned, GPU-resampled material images supported by both graphics backends.
/// Sampling uses one array, avoiding bindless handles and divergent descriptor indexing.
/// </summary>
internal sealed class DDGIMaterialTextureCache : IDisposable
{
    public const uint Resolution = 256;
    public const int MaximumLayers = 512;
    private readonly Dictionary<XRTexture2D, DDGIMaterialTextureEntry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly List<XRTexture2D> _removed = [];
    private readonly Stack<int> _freeLayers = new();
    private XRRenderProgram? _copyProgram;
    private XRGpuFence? _copySubmission;
    private int _nextLayer = 1;
    private ulong _frame;

    public XRTexture2DArray? Atlas { get; private set; }

    public void BeginFrame(ulong frame) => _frame = frame;

    public int GetLayer(XRTexture texture)
    {
        if (texture is not XRTexture2D source || source.MultiSample)
            throw new InvalidOperationException("DDGI material maps must be single-sample 2D textures.");
        if (!_entries.TryGetValue(source, out DDGIMaterialTextureEntry? entry))
        {
            int layer = _freeLayers.Count > 0 ? _freeLayers.Pop() : _nextLayer;
            if (layer >= MaximumLayers)
                throw new InvalidOperationException("DDGI material texture capacity exceeded (511 distinct maps per pipeline instance).");
            if (layer == _nextLayer)
                _nextLayer++;
            entry = new DDGIMaterialTextureEntry(source, layer);
            _entries.Add(source, entry);
        }
        entry.LastUsedFrame = _frame;
        return entry.Layer;
    }

    public bool Prepare(out string? diagnostic)
    {
        diagnostic = null;
        if (!ResolveCopySubmission())
        {
            diagnostic = "DDGI material image copies are awaiting backend submission.";
            return false;
        }
        PruneUnused();
        int requiredLayers = 1;
        foreach (DDGIMaterialTextureEntry entry in _entries.Values)
            requiredLayers = Math.Max(requiredLayers, entry.Layer + 1);
        EnsureAtlas(requiredLayers);
        if (_entries.Count == 0)
            return true;
        _copyProgram ??= new XRRenderProgram(true, false,
            ShaderHelper.LoadEngineShader("Compute/DDGI/ddgi_material_copy.comp", EShaderType.Compute)) { Name = "DDGI.MaterialCopy" };
        if (!_copyProgram.IsLinked)
            _copyProgram.Link();
        if (!_copyProgram.IsLinked)
        {
            diagnostic = "DDGI material texture resampling program is still linking.";
            return false;
        }
        // Validate all sources before queuing a partial copy batch.
        foreach (DDGIMaterialTextureEntry entry in _entries.Values)
            if (entry.Source.Width == 0 || entry.Source.Height == 0)
            {
                diagnostic = "A DDGI material texture has no resident image dimensions yet.";
                return false;
            }

        bool copied = false;
        foreach (DDGIMaterialTextureEntry entry in _entries.Values)
        {
            entry.RefreshMipmapSubscriptions();
            long revision = entry.Revision;
            bool streaming = entry.IsStreaming;
            if (entry.CopiedRevision == revision && !entry.IsGpuAuthored && !streaming && !entry.CopiedWhileStreaming)
                continue;
            _copyProgram.Sampler("uSource", entry.Source, 0);
            _copyProgram.BindImageTexture(0u, Atlas!, 0, true, 0,
                XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);
            _copyProgram.Uniform("uLayer", entry.Layer);
            _copyProgram.DispatchCompute((Resolution + 7u) / 8u, (Resolution + 7u) / 8u, 1u,
                EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            entry.PendingRevision = revision;
            entry.PendingWhileStreaming = streaming;
            copied = true;
        }
        if (!copied)
            return true;

        // Queued Vulkan work can still be rejected before submission. Keep the
        // authored revisions dirty until the backend accepts this copy batch.
        _copySubmission = AbstractRenderer.Current?.InsertGpuFence();
        if (_copySubmission is null)
        {
            CompleteCopies(accepted: false);
            diagnostic = "DDGI material image copies require a GPU submission receipt.";
            return false;
        }
        return true;
    }

    private bool ResolveCopySubmission()
    {
        if (_copySubmission is null)
            return true;
        if (_copySubmission.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return false;

        CompleteCopies(_copySubmission.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted &&
            _copySubmission.Poll() != EGpuFenceStatus.Failed);
        _copySubmission.Dispose();
        _copySubmission = null;
        return true;
    }

    private void CompleteCopies(bool accepted)
    {
        foreach (DDGIMaterialTextureEntry entry in _entries.Values)
        {
            if (accepted && entry.PendingRevision != 0)
            {
                entry.CopiedRevision = entry.PendingRevision;
                entry.CopiedWhileStreaming = entry.PendingWhileStreaming;
            }
            entry.PendingRevision = 0;
        }
    }

    public void PruneUnused()
    {
        _removed.Clear();
        foreach (var pair in _entries)
        {
            if (pair.Value.LastUsedFrame != _frame)
                _removed.Add(pair.Key);
        }
        for (int i = 0; i < _removed.Count; i++)
        {
            DDGIMaterialTextureEntry entry = _entries[_removed[i]];
            _freeLayers.Push(entry.Layer);
            entry.Dispose();
            _entries.Remove(_removed[i]);
        }
    }

    private void EnsureAtlas(int layers)
    {
        if (Atlas is not null && Atlas.Depth >= layers)
            return;
        uint capacity = 1;
        while (capacity < layers)
            capacity *= 2;
        Atlas?.Destroy();
        Atlas = new XRTexture2DArray(capacity, Resolution, Resolution,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
        {
            Name = DDGIResourceImports.MaterialTextures,
            SizedInternalFormat = ESizedInternalFormat.Rgba16f,
            RequiresStorageUsage = true,
            Resizable = false,
            AutoGenerateMipmaps = false,
            SmallestAllowedMipmapLevel = 0,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };
        for (int i = 0; i < Atlas.Textures.Length; i++)
            Atlas.Textures[i].SmallestAllowedMipmapLevel = 0;
        Atlas.PushData();
        foreach (DDGIMaterialTextureEntry entry in _entries.Values)
            entry.CopiedRevision = 0;
    }

    public void Dispose()
    {
        _copySubmission?.Dispose();
        _copySubmission = null;
        foreach (DDGIMaterialTextureEntry entry in _entries.Values)
            entry.Dispose();
        _entries.Clear();
        Atlas?.Destroy();
        Atlas = null;
        _copyProgram?.Destroy();
    }
}
