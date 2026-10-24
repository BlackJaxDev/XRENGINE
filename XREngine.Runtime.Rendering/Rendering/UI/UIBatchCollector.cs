using System;
using XREngine.Rendering.UI.State;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>
/// Collects ordered UI runs and draws each compatible run with isolated GPU storage.
/// <para>
/// Material quads from <see cref="UIMaterialComponent"/> and text quads from
/// <see cref="UITextComponent"/> retain painter order across run boundaries.
/// </para>
/// <para>
/// Each run can combine several primitives into one instanced draw.
/// </para>
/// </summary>
public sealed partial class UIBatchCollector : IDisposable
{
    private static readonly object CollectorRegistryGate = new();
    private static readonly List<WeakReference<UIBatchCollector>> CollectorRegistry = [];
    private static readonly List<(AbstractRenderer Renderer, long Generation,
        MaterialGPUResources? Material, TextGPUResources? Text)> DeferredCohortRetirement = [];
    private static readonly List<ReceiptLease> UnclaimedReceiptLeases = [];
    private const int MaxUnclaimedReceiptLeases = 512;
    private static int s_pendingReceiptReservations;

    public UIBatchCollector()
    {
        lock (CollectorRegistryGate)
            CollectorRegistry.Add(new WeakReference<UIBatchCollector>(this));
    }

    /// <summary>
    /// Releases cohorts for an exact renderer generation after its owner has
    /// completed GPU drain and backend teardown preparation.
    /// </summary>
    public static void NotifyRendererKnownIdle(AbstractRenderer renderer, long backendGeneration)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (CollectorRegistryGate)
        {
            for (int index = UnclaimedReceiptLeases.Count - 1; index >= 0; index--)
            {
                ReceiptLease lease = UnclaimedReceiptLeases[index];
                if (!ReferenceEquals(lease.Renderer, renderer) || lease.BackendGeneration != backendGeneration) continue;
                lease.Fence?.Dispose();
                lease.Fence = null;
                UnclaimedReceiptLeases.RemoveAt(index);
            }
            for (int index = DeferredCohortRetirement.Count - 1; index >= 0; index--)
            {
                var deferred = DeferredCohortRetirement[index];
                if (!ReferenceEquals(deferred.Renderer, renderer) || deferred.Generation != backendGeneration)
                    continue;
                ForceReleaseReceipt(deferred.Material?.Receipt);
                ForceReleaseReceipt(deferred.Text?.Receipt);
                if (deferred.Material is not null) DestroyMaterialGPU(deferred.Material);
                deferred.Text?.Dispose();
                DeferredCohortRetirement.RemoveAt(index);
            }
            for (int index = CollectorRegistry.Count - 1; index >= 0; index--)
            {
                if (!CollectorRegistry[index].TryGetTarget(out UIBatchCollector? collector) || collector._disposed)
                {
                    CollectorRegistry.RemoveAt(index);
                    continue;
                }
                collector.RetireKnownIdleRenderer(renderer, backendGeneration);
                collector.RetireBackdropKnownIdle(renderer, backendGeneration);
            }
            RetireQuarantinedBackdropKnownIdle(renderer, backendGeneration);
        }
    }

    private void RetireKnownIdleRenderer(AbstractRenderer renderer, long backendGeneration)
    {
        _staleMaterialRunKeys.Clear();
        foreach (var pair in _materialRunGPU)
            if (ReferenceEquals(pair.Key.Renderer, renderer) && pair.Key.BackendGeneration == backendGeneration)
                _staleMaterialRunKeys.Add(pair.Key);
        foreach (var key in _staleMaterialRunKeys)
        {
            List<MaterialGPUResources> cohorts = _materialRunGPU[key];
            while (cohorts.Count != 0)
            {
                MaterialGPUResources gpu = cohorts[^1];
                ForceReleaseReceipt(gpu.Receipt);
                gpu.Receipt = null;
                DestroyMaterialGPU(gpu);
                cohorts.RemoveAt(cohorts.Count - 1);
                _gpuCohortCount--;
            }
            _materialRunGPU.Remove(key);
        }
        _staleTextRunKeys.Clear();
        foreach (var pair in _textGPU)
            if (ReferenceEquals(pair.Key.Renderer, renderer) && pair.Key.BackendGeneration == backendGeneration)
                _staleTextRunKeys.Add(pair.Key);
        foreach (var key in _staleTextRunKeys)
        {
            List<TextGPUResources> cohorts = _textGPU[key];
            while (cohorts.Count != 0)
            {
                TextGPUResources gpu = cohorts[^1];
                ForceReleaseReceipt(gpu.Receipt);
                gpu.Receipt = null;
                gpu.Dispose();
                cohorts.RemoveAt(cohorts.Count - 1);
                _gpuCohortCount--;
            }
            _textGPU.Remove(key);
        }
    }
    private const string TextAtlasTypeUniformName = "TextAtlasType";
    private const string MsdfDistanceRangeUniformName = "MsdfDistanceRange";
    private const string MsdfDistanceRangeMiddleUniformName = "MsdfDistanceRangeMiddle";
    private const string MsdfFillBiasUniformName = "MsdfFillBias";
    private const string TextDebugModeUniformName = "TextDebugMode";
    private const string TextRenderLayerUniformName = "TextRenderLayer";
    private const string TextRenderLayerVertexUniformName = "TextRenderLayer_VTX";
    private const int TextAtlasBitmap = 0;
    private const int TextRenderLayerCombined = 0;
    private const int TextRenderLayerOutline = 1;
    private const int TextRenderLayerFill = 2;
    private const uint TextInstanceVec4Count = 8;
    private static readonly bool BatchDiagnosticsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("XRE_UI_BATCH_DIAGNOSTICS"), "1", StringComparison.Ordinal);
    private static int s_textAddDiagCount;
    private static int s_textMarkerDiagCount;
    private static int s_textRenderDiagCount;
    private static int s_textUploadDiagCount;
    private static int s_textPrepareDiagCount;
    private static int s_materialRenderDiagCount;
    private static long s_textPrepareTotalCalls;
    private static long s_textPrepareFailureCount;
    private static long s_textPrepareSummaryLastTicks;
    private static readonly Dictionary<string, int> s_textPrepareReasonCounts = new();
    private static readonly object s_textPrepareSummaryLock = new();

    #region Batch Entry Types

    private enum EBatchMarkerKind
    {
        Material,
        Text,
    }

    /// <summary>
    /// Per-instance data for a batched material quad.
    /// </summary>
    public struct MaterialQuadEntry
    {
        public Matrix4x4 WorldMatrix;
        public Vector4 Color;
        public Vector4 UIXYWH;
        public Vector4 UV;
        public Vector4 BorderColor;
        public float BorderWidth;
        public ulong PrimitiveHandle;
    }

    /// <summary>
    /// Per-instance data for a batched text component.
    /// Glyphs is a snapshot of the glyph layout data.
    /// </summary>
    public struct TextEntry
    {
        public Matrix4x4 WorldMatrix;
        public Vector4 TextColor;
        public Vector4 OutlineColor;
        public float OutlineThickness;
        public Vector4 UIXYWH;
        public UITextLayoutResult Layout;
        public int GlyphCount;
        public ulong PrimitiveHandle;
    }

    /// <summary>
    /// Aggregated text batch data for a single render pass and font atlas.
    /// </summary>
    private sealed class TextBatchData
    {
        public ulong RunId;
        public UICompositionOwner CompositionOwner;
        public ulong CompositionSegmentIdentity;
        public ulong CompositionSegmentRevision;
        public BoundingRectangle? CropRegion;
        public XRTexture2D? Atlas;
        public int AtlasType;
        public float DistanceRange;
        public float DistanceRangeMiddle;
        public float MsdfFillBias;
        public int DebugMode;
        public bool HasOutline;
        public readonly List<TextEntry> Entries = [];
        public int TotalGlyphs;

        public void Clear()
        {
            CompositionOwner = default;
            CompositionSegmentIdentity = 0;
            CompositionSegmentRevision = 0;
            CropRegion = null;
            Atlas = null;
            AtlasType = TextAtlasBitmap;
            DistanceRange = 0.0f;
            DistanceRangeMiddle = 0.5f;
            MsdfFillBias = 0.5f;
            DebugMode = 0;
            HasOutline = false;
            Entries.Clear();
            TotalGlyphs = 0;
        }
    }

    /// <summary>
    /// Aggregated material quad batch for a single render pass.
    /// </summary>
    private sealed class MaterialQuadBatchData
    {
        public ulong RunId;
        public UICompositionOwner CompositionOwner;
        public ulong CompositionSegmentIdentity;
        public ulong CompositionSegmentRevision;
        public BoundingRectangle? CropRegion;
        public XRTexture2D? Texture;
        public bool ImageSrgb;
        public bool Border;
        public UIBackdropRequest Backdrop;
        public readonly List<MaterialQuadEntry> Entries = [];

        public void Clear()
        {
            CompositionOwner = default;
            CompositionSegmentIdentity = 0;
            CompositionSegmentRevision = 0;
            CropRegion = null;
            Texture = null;
            ImageSrgb = false;
            Border = false;
            Backdrop = default;
            Entries.Clear();
        }
    }

    private sealed class BatchPool<T> where T : class, new()
    {
        public readonly List<T> Items = [];
        public int Count;

        public T Acquire(out int index)
        {
            index = Count;
            if (index == Items.Count)
                Items.Add(new T());

            Count++;
            return Items[index];
        }

        public T this[int index] => Items[index];

        public bool TryGet(int index, out T item)
        {
            if ((uint)index < (uint)Count)
            {
                item = Items[index];
                return true;
            }

            item = null!;
            return false;
        }

        public void Reset(Action<T> clear)
        {
            for (int i = 0; i < Count; i++)
                clear(Items[i]);

            Count = 0;
        }
    }

    private sealed class CollectPassState
    {
        public EBatchMarkerKind? ActiveKind;
        public UICompositionOwner ActiveCompositionOwner;
        public ulong ActiveCompositionSegmentIdentity;
        public int ActiveZIndex;
        public BoundingRectangle? ActiveCropRegion;
        public XRTexture2D? ActiveMaterialTexture;
        public bool ActiveMaterialImageSrgb;
        public bool ActiveMaterialBorder;
        public XRTexture2D? ActiveTextAtlas;
        public int ActiveTextAtlasType;
        public float ActiveTextDistanceRange;
        public float ActiveTextDistanceRangeMiddle;
        public float ActiveTextMsdfFillBias;
        public int ActiveTextDebugMode;
        public int ActiveGroupIndex = -1;

        public void Reset()
        {
            ActiveKind = null;
            ActiveCompositionOwner = default;
            ActiveCompositionSegmentIdentity = 0;
            ActiveZIndex = 0;
            ActiveCropRegion = null;
            ActiveMaterialTexture = null;
            ActiveMaterialImageSrgb = false;
            ActiveMaterialBorder = false;
            ActiveTextAtlas = null;
            ActiveTextAtlasType = TextAtlasBitmap;
            ActiveTextDistanceRange = 0.0f;
            ActiveTextDistanceRangeMiddle = 0.5f;
            ActiveTextMsdfFillBias = 0.5f;
            ActiveTextDebugMode = 0;
            ActiveGroupIndex = -1;
        }
    }

    private sealed class MarkerPool
    {
        public readonly List<BatchMarkerCommand> Commands = [];
        public int UsedCount;

        public BatchMarkerCommand Acquire(UIBatchCollector owner, int renderPass)
        {
            int index = UsedCount++;
            if (index == Commands.Count)
                Commands.Add(new BatchMarkerCommand(owner, renderPass));

            return Commands[index];
        }

        public void ResetUsage() => UsedCount = 0;
    }

    private sealed class BatchMarkerCommand : RenderCommand2D
    {
        private readonly UIBatchCollector _owner;
        private readonly int _renderPass;
        private EBatchMarkerKind _kind;
        private int _groupIndex;
        private EBatchMarkerKind _renderKind;
        private int _renderGroupIndex;

        public BatchMarkerCommand(UIBatchCollector owner, int renderPass)
            : base(renderPass)
        {
            _owner = owner;
            _renderPass = renderPass;
        }

        public void Configure(EBatchMarkerKind kind, int groupIndex, int zIndex)
        {
            bool markerChanged = _kind != kind || _groupIndex != groupIndex;
            _kind = kind;
            _groupIndex = groupIndex;
            if (markerChanged)
                MarkDirty();
            ZIndex = zIndex;
            Enabled = true;
        }

        public override void SwapBuffers()
        {
            long capturedVersion = BeginSwapBuffers();
            _renderKind = _kind;
            _renderGroupIndex = _groupIndex;
            CompleteSwapBuffers(capturedVersion);
        }

        public override void Render()
        {
            OnPreRender();
            if (BatchDiagnosticsEnabled && _kind == EBatchMarkerKind.Text && s_textMarkerDiagCount++ < 40)
            {
                Debug.Log(
                    ELogCategory.UI,
                    "[FpsTextDiag] UIBatchMarker.Render #{0}: pass={1} kind={2} group={3} renderKind={4} renderGroup={5} zIndex={6}",
                    s_textMarkerDiagCount,
                    _renderPass,
                    _kind,
                    _groupIndex,
                    _renderKind,
                    _renderGroupIndex,
                    ZIndex);
            }

            _owner.RenderBatchMarker(_renderPass, _renderKind, _renderGroupIndex);
            OnPostRender();
        }
    }

    #endregion

    #region Double-buffered Batch Lists

    // Collecting side (written on collect-visible thread)
    private Dictionary<int, BatchPool<MaterialQuadBatchData>> _collectMaterialGroups = [];
    private Dictionary<int, BatchPool<TextBatchData>> _collectTextGroups = [];
    private readonly Dictionary<int, CollectPassState> _collectPassStates = [];

    // Rendering side (read on render thread)
    private Dictionary<int, BatchPool<MaterialQuadBatchData>> _renderMaterialGroups = [];
    private Dictionary<int, BatchPool<TextBatchData>> _renderTextGroups = [];
    private ulong _nextRunId;
    private readonly HashSet<ulong> _claimedPreviousRunIds = [];
    private readonly Dictionary<ulong, (int Pass, MaterialQuadBatchData Run)> _oldMaterialRunByHandle = [];
    private readonly Dictionary<ulong, (int Pass, TextBatchData Run)> _oldTextRunByHandle = [];
    internal int LastRunReconcileVisitCount { get; private set; }

    private ulong AllocateRunId()
    {
        ulong id = ++_nextRunId;
        return id == 0 ? ++_nextRunId : id;
    }

    // Markers retain their ordinal position in the painter stream. GPU cohorts use
    // this separate identity so an inserted earlier run cannot rebase later storage.
    private void ReconcileCollectedRunIds()
    {
        _claimedPreviousRunIds.Clear();
        _oldMaterialRunByHandle.Clear();
        _oldTextRunByHandle.Clear();
        LastRunReconcileVisitCount = 0;
        foreach (var pass in _renderMaterialGroups)
            for (int group = 0; group < pass.Value.Count; group++)
            {
                MaterialQuadBatchData old = pass.Value[group];
                foreach (MaterialQuadEntry entry in old.Entries)
                {
                    LastRunReconcileVisitCount++;
                    if (entry.PrimitiveHandle != 0)
                        _oldMaterialRunByHandle.TryAdd(entry.PrimitiveHandle, (pass.Key, old));
                }
            }
        foreach (var pass in _renderTextGroups)
            for (int group = 0; group < pass.Value.Count; group++)
            {
                TextBatchData old = pass.Value[group];
                foreach (TextEntry entry in old.Entries)
                {
                    LastRunReconcileVisitCount++;
                    if (entry.PrimitiveHandle != 0)
                        _oldTextRunByHandle.TryAdd(entry.PrimitiveHandle, (pass.Key, old));
                }
            }
        foreach (var pass in _collectMaterialGroups)
        {
            for (int index = 0; index < pass.Value.Count; index++)
            {
                MaterialQuadBatchData current = pass.Value[index];
                foreach (MaterialQuadEntry entry in current.Entries)
                {
                    LastRunReconcileVisitCount++;
                    if (entry.PrimitiveHandle == 0 ||
                        !_oldMaterialRunByHandle.TryGetValue(entry.PrimitiveHandle, out var match) ||
                        match.Pass != pass.Key) continue;
                    MaterialQuadBatchData old = match.Run;
                    if (_claimedPreviousRunIds.Contains(old.RunId) || current.CropRegion != old.CropRegion ||
                        current.CompositionOwner != old.CompositionOwner ||
                        current.CompositionSegmentIdentity != old.CompositionSegmentIdentity ||
                        !ReferenceEquals(current.Texture, old.Texture) ||
                        current.ImageSrgb != old.ImageSrgb || current.Border != old.Border) continue;
                    current.RunId = old.RunId;
                    _claimedPreviousRunIds.Add(old.RunId);
                    break;
                }
            }
        }
        foreach (var pass in _collectTextGroups)
        {
            for (int index = 0; index < pass.Value.Count; index++)
            {
                TextBatchData current = pass.Value[index];
                foreach (TextEntry entry in current.Entries)
                {
                    LastRunReconcileVisitCount++;
                    if (entry.PrimitiveHandle == 0 ||
                        !_oldTextRunByHandle.TryGetValue(entry.PrimitiveHandle, out var match) ||
                        match.Pass != pass.Key) continue;
                    TextBatchData old = match.Run;
                    if (_claimedPreviousRunIds.Contains(old.RunId) || current.CropRegion != old.CropRegion ||
                        current.CompositionOwner != old.CompositionOwner ||
                        current.CompositionSegmentIdentity != old.CompositionSegmentIdentity ||
                        !ReferenceEquals(current.Atlas, old.Atlas) || current.AtlasType != old.AtlasType ||
                        current.DistanceRange != old.DistanceRange ||
                        current.DistanceRangeMiddle != old.DistanceRangeMiddle ||
                        current.MsdfFillBias != old.MsdfFillBias || current.DebugMode != old.DebugMode) continue;
                    current.RunId = old.RunId;
                    _claimedPreviousRunIds.Add(old.RunId);
                    break;
                }
            }
        }
        _oldMaterialRunByHandle.Clear();
        _oldTextRunByHandle.Clear();
        _claimedPreviousRunIds.Clear();
    }

    private readonly Dictionary<int, MarkerPool> _markerPools = [];
    private readonly record struct PublishedMarker(int RecordIndex, int Pass, int ZIndex,
        EBatchMarkerKind Kind, int GroupIndex);
    private List<PublishedMarker> _collectPublishedMarkers = [];
    private List<PublishedMarker> _renderPublishedMarkers = [];
    private UIRenderRecord[]? _collectPublishedStorage;
    private UIRenderRecord[]? _renderPublishedStorage;
    private UICompositionSegmentSet? _collectCompositionStorage;
    private UICompositionSegmentSet? _renderCompositionStorage;
    private int _collectPublishedRecordIndex = -1;
    private bool _reusePublishedRuns;
    /// <summary>Draws authored by the currently published frozen UI packet.</summary>
    public int ExpectedPublishedDrawIntentCount
    {
        get
        {
            int count = _renderPublishedMarkers.Count;
            if (_renderPublishedStorage is { } records)
                foreach (UIRenderRecord record in records)
                    if (record.Visible && !record.SupportsBatch && record.FrozenCommand is not null)
                        count++;
            return count;
        }
    }

    public bool HasPendingPublishedTextLayout
    {
        get
        {
            if (_renderPublishedStorage is { } records)
                foreach (UIRenderRecord record in records)
                    if (record.Visible && record.SupportsBatch && record.IsText && record.TextLayout is null)
                        return true;
            return false;
        }
    }
    internal ulong RetainedRunReuseCount { get; private set; }
    private sealed class RetainedPrimitiveSlot
    {
        public long MembershipId;
        public UIElementIdentity Identity;
        public uint Generation;
        public ulong SeenEpoch;
        public bool Occupied;
        public bool Registered;
    }
    private readonly object _primitiveSlotGate = new();
    private readonly List<RetainedPrimitiveSlot> _primitiveSlots = [];
    private readonly Stack<int> _freePrimitiveSlots = [];
    private readonly Dictionary<(long MembershipId, UIElementIdentity Identity), int> _slotByIdentity = [];
    private readonly Dictionary<long, int> _registeredSlotByMembership = [];
    private ulong _membershipEpoch;
    private uint _nextPrimitiveGeneration;

    internal ulong GetRetainedPrimitiveHandle(long membershipId)
    {
        lock (_primitiveSlotGate)
        {
            if (_registeredSlotByMembership.TryGetValue(membershipId, out int registered))
                return PrimitiveHandle(registered);
            foreach (var entry in _slotByIdentity)
                if (entry.Key.MembershipId == membershipId)
                    return PrimitiveHandle(entry.Value);
            return 0;
        }
    }

    internal ulong GetRetainedPrimitiveHandle(long membershipId, UIElementIdentity identity)
    {
        lock (_primitiveSlotGate)
            return _slotByIdentity.TryGetValue((membershipId, identity), out int index)
                ? PrimitiveHandle(index) : 0;
    }

    private ulong PrimitiveHandle(int index)
        => ((ulong)_primitiveSlots[index].Generation << 32) | (uint)index;

    /// <summary>Reserves one stable primitive slot for an active UI membership.</summary>
    internal ulong RegisterPrimitiveMembership(long membershipId, UIElementIdentity identity)
    {
        if (membershipId <= 0 || !identity.IsValid)
            throw new ArgumentException("A batch primitive requires a valid membership and element identity.");
        lock (_primitiveSlotGate)
        {
            ulong handle = RetainPrimitiveLocked(membershipId, identity);
            int index = (int)(uint)handle;
            if (_registeredSlotByMembership.TryGetValue(membershipId, out int previous) && previous != index)
                _primitiveSlots[previous].Registered = false;
            _registeredSlotByMembership[membershipId] = index;
            _primitiveSlots[index].Registered = true;
            return handle;
        }
    }

    /// <summary>Ends owner registration without invalidating an older published GPU cohort.</summary>
    internal void UnregisterPrimitiveMembership(long membershipId, ulong handle)
    {
        lock (_primitiveSlotGate)
        {
            if (!_registeredSlotByMembership.TryGetValue(membershipId, out int index) ||
                PrimitiveHandle(index) != handle)
                return;
            ReleasePrimitiveSlotLocked(index);
        }
    }

    private ulong RetainPrimitive(in UIRenderRecord record)
    {
        lock (_primitiveSlotGate)
            return RetainPrimitiveLocked(record.MembershipId, record.Identity);
    }

    private ulong RetainPrimitiveLocked(long membershipId, UIElementIdentity identity)
    {
        if (!_slotByIdentity.TryGetValue((membershipId, identity), out int index))
        {
            index = _freePrimitiveSlots.Count != 0 ? _freePrimitiveSlots.Pop() : _primitiveSlots.Count;
            if (index == _primitiveSlots.Count)
                _primitiveSlots.Add(new RetainedPrimitiveSlot());
            _slotByIdentity.Add((membershipId, identity), index);
        }
        RetainedPrimitiveSlot slot = _primitiveSlots[index];
        if (!slot.Occupied)
        {
            uint next = ++_nextPrimitiveGeneration;
            slot.Generation = next == 0 ? ++_nextPrimitiveGeneration : next;
        }
        slot.MembershipId = membershipId;
        slot.Identity = identity;
        slot.Occupied = true;
        slot.SeenEpoch = Math.Max(slot.SeenEpoch, _membershipEpoch);
        return PrimitiveHandle(index);
    }

    private void RetireMissingPrimitives()
    {
        lock (_primitiveSlotGate)
        {
            for (int index = 0; index < _primitiveSlots.Count; index++)
            {
                RetainedPrimitiveSlot slot = _primitiveSlots[index];
                if (!slot.Occupied || slot.Registered || slot.SeenEpoch == _membershipEpoch)
                    continue;
                ReleasePrimitiveSlotLocked(index);
            }
        }
    }

    private void ReleasePrimitiveSlotLocked(int index)
    {
        RetainedPrimitiveSlot slot = _primitiveSlots[index];
        _slotByIdentity.Remove((slot.MembershipId, slot.Identity));
        if (slot.Registered && _registeredSlotByMembership.TryGetValue(slot.MembershipId, out int registered) && registered == index)
            _registeredSlotByMembership.Remove(slot.MembershipId);
        slot.Occupied = false;
        slot.Registered = false;
        slot.MembershipId = 0;
        slot.Identity = default;
        _freePrimitiveSlots.Push(index);
    }

    #endregion

    #region GPU Resources — Material Quads

    private XRMeshRenderer? _matQuadMesh;
    private XRDataBuffer<Vector4>? _matQuadTransformBuf;  // binding 0: 4 × vec4 per instance (mat4 rows)
    private XRDataBuffer<Vector4>? _matQuadColorBuf;      // binding 1: vec4 per instance
    private XRDataBuffer<Vector4>? _matQuadBoundsBuf;     // binding 2: vec4 per instance
    private XRDataBuffer<Vector4>? _matQuadUvBuf;         // textured binding 3: UV rectangle per instance
    private XRDataBuffer<Vector4>? _matQuadBorderWidthBuf; // border binding 4: width per instance
    private XRDataBuffer<uint>? _matQuadDrawIndexBuf;      // desktop binding 5: draw order → stable slot
    private XRMeshRenderer? _matQuadBorderMesh;
    private XRMeshRenderer? _matQuadBackdropMesh;
    private XRTexture2D? _matQuadBackdropTexture;
    private Dictionary<(XRTexture2D Texture, bool Srgb), XRMeshRenderer> _materialTextureMeshes = [];
    private List<(XRTexture2D Texture, bool Srgb)> _staleMaterialTextures = [];
    private const int MaxMaterialTextureMeshes = 64;
    private const int MaxRunCohorts = 4;
    private const int MaxCanvasGpuCohorts = 256;
    private const uint MaxRetainedQuadSlotsPerRun = 1u << 20;
    private const uint MaxRetainedGlyphSlotsPerRun = 1u << 20;
    private uint _matQuadCapacity;
    private bool _matQuadNeedsPush;
    private bool _legacyMaterialGpuCreated;
    private readonly Dictionary<(AbstractRenderer Renderer, long BackendGeneration, int Pass, ulong RunId), List<MaterialGPUResources>> _materialRunGPU = [];
    private readonly List<(AbstractRenderer Renderer, long BackendGeneration, int Pass, ulong RunId)> _staleMaterialRunKeys = [];
    private readonly HashSet<(int Pass, ulong RunId)> _activeMaterialRunKeys = [];
    private int _gpuCohortCount;

    private sealed class MaterialGPUResources
    {
        public XRMeshRenderer? Mesh;
        public XRDataBuffer<Vector4>? Transform;
        public XRDataBuffer<Vector4>? Color;
        public XRDataBuffer<Vector4>? Bounds;
        public XRDataBuffer<Vector4>? Uv;
        public XRDataBuffer<Vector4>? BorderWidth;
        public XRDataBuffer<uint>? DrawIndex;
        public XRMeshRenderer? BorderMesh;
        public XRMeshRenderer? BackdropMesh;
        public XRTexture2D? BackdropTexture;
        public Dictionary<(XRTexture2D Texture, bool Srgb), XRMeshRenderer> TextureMeshes = [];
        public List<(XRTexture2D Texture, bool Srgb)> StaleTextures = [];
        public uint Capacity;
        public bool NeedsPush;
        public readonly List<MaterialQuadEntry> UploadedEntries = [];
        public readonly RetainedQuadSlots Slots = new();
        public readonly List<MaterialQuadEntry> UploadedSlots = [];
        public readonly List<bool> InitializedSlots = [];
        public readonly List<uint> UploadedDrawIndices = [];
        public readonly List<uint> CurrentDrawIndices = [];
        public readonly List<(uint Slot, MaterialQuadEntry Entry)> ScratchSlots = [];
        public bool HasUploadedEntries;
        public bool HasUploadedBorder;
        public bool HasUploadedImage;
        public int UploadedVariant = -1;
        public bool UsedUv;
        public bool UsedBorderWidth;
        public XRGpuFence? Fence;
        public bool Submitted;
        public ReceiptLease? Receipt;
    }

    #endregion

    #region GPU Resources — Text Quads (per font atlas)

    private sealed class TextGPUResources : IDisposable
    {
        public XRMeshRenderer? Mesh;
        public XRDataBuffer<Vector4>? GlyphTransformsBuf;  // binding 0: vec4 per glyph
        public XRDataBuffer<Vector4>? GlyphTexCoordsBuf;   // binding 1: vec4 per glyph
        public XRDataBuffer<Vector4>? TextInstanceBuf;      // binding 2: TextInstanceVec4Count × vec4 per text
        public XRDataBuffer<uint>? GlyphTextIndexBuf;       // binding 3: uint per glyph → text index
        public XRDataBuffer<uint>? GlyphDrawIndexBuf;       // desktop binding 4: painter order → stable glyph slot
        public uint GlyphCapacity;
        public uint DrawCapacity;
        public uint TextCapacity;
        public readonly RetainedGlyphRanges GlyphRanges = new();
        public readonly RetainedQuadSlots TextSlots = new();
        public readonly List<RetainedGlyphRanges.Allocation?> ActiveGlyphRanges = [];
        public readonly List<uint> ActiveTextSlots = [];
        public readonly List<(uint Slot, TextEntry Entry)> SortedTextSlots = [];
        public readonly List<(uint Offset, uint Count, int EntryIndex)> DirtyGlyphRanges = [];
        public readonly List<TextEntry> UploadedTextSlots = [];
        public readonly List<bool> InitializedTextSlots = [];
        public readonly List<uint> UploadedDrawIndices = [];
        public readonly List<uint> CurrentDrawIndices = [];
        public bool NeedsPush;
        public readonly List<TextEntry> UploadedEntries = [];
        public bool HasUploadedData;
        public XRGpuFence? Fence;
        public bool Submitted;
        public ReceiptLease? Receipt;

        public void Dispose()
        {
            XRMeshRenderer? meshRenderer = Mesh;
            XRMesh? mesh = meshRenderer?.Mesh;
            XRMaterial? material = meshRenderer?.Material;
            GlyphTransformsBuf?.Destroy();
            GlyphTexCoordsBuf?.Destroy();
            TextInstanceBuf?.Destroy();
            GlyphTextIndexBuf?.Destroy();
            GlyphDrawIndexBuf?.Destroy();
            meshRenderer?.Destroy();
            mesh?.Destroy();
            material?.Destroy();
            Fence?.Dispose();
            Fence = null;
            GlyphTransformsBuf = null;
            GlyphTexCoordsBuf = null;
            TextInstanceBuf = null;
            GlyphTextIndexBuf = null;
            GlyphDrawIndexBuf = null;
            Mesh = null;
        }
    }

    private readonly Dictionary<(AbstractRenderer Renderer, long BackendGeneration, XRTexture2D Atlas, int Pass, ulong RunId), List<TextGPUResources>> _textGPU = [];
    private readonly List<(AbstractRenderer Renderer, long BackendGeneration, XRTexture2D Atlas, int Pass, ulong RunId)> _staleTextRunKeys = [];
    private readonly HashSet<(XRTexture2D Atlas, int Pass, ulong RunId)> _activeTextRunKeys = [];
    internal int LastActiveRunBuildVisitCount { get; private set; }
    private bool _disposed;

    private sealed class ReceiptLease(AbstractRenderer renderer, ulong id)
    {
        public readonly AbstractRenderer Renderer = renderer;
        public readonly long BackendGeneration = renderer.BackendGeneration;
        public readonly ulong Id = id;
        public XRGpuFence? Fence;
        public bool Sealed;
        public bool Authored;
        public int Claims;
        public readonly UIReceiptCompletionProbe Completion = new(renderer, id);

        // The fallback renderer consumes its retirement entry on the first
        // successful query. All cohorts sharing this receipt must observe that
        // single proof, including cohorts inspected on later frames.
        public bool IsRetired
        {
            get
            {
                return Sealed && Completion.IsRetired;
            }
        }

        public void Release()
        {
            if (--Claims == 0)
            {
                Fence?.Dispose();
                Fence = null;
            }
        }
    }

    private ReceiptLease? _activeReceipt;

    internal UIReceiptCompletionProbe BeginPublicationReceipt(ulong receiptId)
    {
        if (receiptId == 0 || _activeReceipt is not null)
            throw new InvalidOperationException("A UI publication receipt is already active or has no identity.");
        AbstractRenderer renderer = AbstractRenderer.Current ??
            throw new InvalidOperationException("A UI publication receipt requires an active renderer.");
        SweepDeferredCohorts(renderer);
        SweepBackdropSurfacesForRenderer(renderer);
        lock (CollectorRegistryGate)
        {
            if (UnclaimedReceiptLeases.Count + s_pendingReceiptReservations >= MaxUnclaimedReceiptLeases)
                throw new InvalidOperationException("The UI receipt retirement budget is full.");
            s_pendingReceiptReservations++;
        }
        _activeReceipt = new ReceiptLease(renderer, receiptId);
        BeginBackdropAuthoring();
        return _activeReceipt.Completion;
    }

    public void FailCurrentUiPublicationAuthoring(string reason)
        => _activeReceipt?.Renderer.FailCurrentUiPublicationAuthoring(reason);

    public void SealPublicationReceipt(ulong receiptId, XRGpuFence? completionFence, bool authored)
    {
        ReceiptLease lease = _activeReceipt ??
            throw new InvalidOperationException("No UI publication receipt is active.");
        if (lease.Id != receiptId)
            throw new InvalidOperationException("UI publication receipt identity mismatch.");
        _activeReceipt = null;
        lease.Fence = completionFence;
        lease.Sealed = true;
        lease.Authored = authored;
        lease.Completion.Seal(authored, completionFence);
        SealBackdropSurface(lease.Completion);
        lock (CollectorRegistryGate)
        {
            s_pendingReceiptReservations--;
            if (lease.Claims == 0)
            {
                if (lease.IsRetired)
                    lease.Fence?.Dispose();
                else
                    UnclaimedReceiptLeases.Add(lease);
            }
        }
    }

    private void Claim(MaterialGPUResources gpu)
    {
        if (_activeReceipt is not { } lease)
            return;
        if (gpu.Receipt is not null)
            throw new InvalidOperationException("A UI GPU cohort still belongs to a publication receipt.");
        gpu.Receipt = lease;
        lease.Claims++;
    }

    private void Claim(TextGPUResources gpu)
    {
        if (_activeReceipt is not { } lease)
            return;
        if (gpu.Receipt is not null)
            throw new InvalidOperationException("A UI GPU cohort still belongs to a publication receipt.");
        gpu.Receipt = lease;
        lease.Claims++;
    }

    private static bool ReleaseRetiredReceipt(ref ReceiptLease? lease)
    {
        if (lease is null)
            return true;
        if (!lease.IsRetired)
            return false;
        lease.Release();
        lease = null;
        return true;
    }

    private static void ForceReleaseReceipt(ReceiptLease? lease)
    {
        if (lease is not null)
            lease.Release();
    }

    #endregion

    #region Public State

    private bool _enabled = true;
    private UIPublication? _renderingPublication;
    private BoundingRectangle _rasterRegion;
    private bool _hasRasterRegion;

    /// <summary>Sets the exact owner-frozen packet being rendered by this canvas.</summary>
    public void BindRenderingPublication(UIPublication? publication)
    {
        if (ReferenceEquals(_renderingPublication, publication))
            return;
        publication?.Retain();
        UIPublication? previous = _renderingPublication;
        _renderingPublication = publication;
        previous?.Release();
    }

    /// <summary>Reads a frozen source record from the currently rendering packet.</summary>
    public bool TryGetRenderingRecord(long membershipId, out UIRenderRecord record)
    {
        if (_renderingPublication is { } publication)
            return publication.TryGetRenderRecord(membershipId, out record);
        record = default;
        return false;
    }

    /// <summary>Supplies the active target region after viewport/FBO selection.</summary>
    public void BeginRasterMapping(BoundingRectangle renderRegion)
    {
        _rasterRegion = renderRegion;
        _hasRasterRegion = true;
    }

    public void EndRasterMapping() => _hasRasterRegion = false;
    /// <summary>The owner publication selected when this collection started.</summary>
    public ulong CollectedPublicationGeneration { get; private set; }

    /// <summary>Tags the next collect pass with its immutable canvas publication.</summary>
    public void BeginPublicationCollection(ulong generation)
        => CollectedPublicationGeneration = generation;
    /// <summary>
    /// When false, batch collection is disabled and components fall back to individual rendering.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// True if any material quad or text data was collected this frame.
    /// </summary>
    public bool HasBatchData
    {
        get
        {
            foreach (var groups in _renderMaterialGroups.Values)
                if (groups.Count > 0) return true;
            foreach (var groups in _renderTextGroups.Values)
                if (groups.Count > 0) return true;
            return false;
        }
    }

    /// <summary>
    /// Checks whether the collected runs can use isolated GPU storage on the current render owner.
    /// The caller must keep collection and rendering serialized until it swaps the candidate.
    /// </summary>
    public bool CanCommitCollectedBatches()
    {
        bool hasRuns = false;
        foreach (var groups in _collectMaterialGroups.Values)
            hasRuns |= groups.Count != 0;
        foreach (var groups in _collectTextGroups.Values)
            hasRuns |= groups.Count != 0;
        if (!hasRuns)
            return true;

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return false;
        RendererBackendId backend = renderer.BackendId;
        if (backend != RendererBackendId.OpenGL && backend != RendererBackendId.Vulkan)
            return true;
        if (backend == RendererBackendId.Vulkan &&
            (renderer is not IRuntimeRendererHost host ||
             !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var reuse) || reuse is null))
            return false;

        foreach (var passGroups in _collectMaterialGroups)
        {
            for (int group = 0; group < passGroups.Value.Count; group++)
            {
                var key = (renderer, renderer.BackendGeneration, passGroups.Key, passGroups.Value[group].RunId);
                if (!_materialRunGPU.TryGetValue(key, out var cohorts) || cohorts.Count < MaxRunCohorts)
                    continue;
                bool ready = false;
                for (int i = 0; i < cohorts.Count; i++)
                    if (CanReuseMaterialGPU(cohorts[i]))
                    {
                        ready = true;
                        break;
                    }
                if (!ready)
                    return false;
            }
        }

        foreach (var passGroups in _collectTextGroups)
        {
            for (int group = 0; group < passGroups.Value.Count; group++)
            {
                TextBatchData batch = passGroups.Value[group];
                if (batch.Atlas is null || batch.TotalGlyphs == 0)
                    continue;
                var key = (renderer, renderer.BackendGeneration, batch.Atlas, passGroups.Key, batch.RunId);
                if (!_textGPU.TryGetValue(key, out var cohorts) || cohorts.Count < MaxRunCohorts)
                    continue;
                bool ready = false;
                for (int i = 0; i < cohorts.Count; i++)
                    if (CanReuseTextGPU(cohorts[i]))
                    {
                        ready = true;
                        break;
                    }
                if (!ready)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Preflights every published run before the first marker can issue a draw.</summary>
    public bool CanRenderPublishedBatches()
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return !HasBatchData;
        SweepDeferredCohorts(renderer);
        lock (CollectorRegistryGate)
            if (UnclaimedReceiptLeases.Count + s_pendingReceiptReservations >= MaxUnclaimedReceiptLeases)
                return false;
        if (renderer.BackendId != RendererBackendId.OpenGL &&
            renderer.BackendId != RendererBackendId.Vulkan)
            return true;
        RetireInactiveGpuCohorts(renderer);
        int newCohorts = 0;
        foreach (var passGroups in _renderMaterialGroups)
        {
            for (int group = 0; group < passGroups.Value.Count; group++)
            {
                if (passGroups.Value[group].Entries.Count == 0)
                    continue;
                var key = (renderer, renderer.BackendGeneration, passGroups.Key, passGroups.Value[group].RunId);
                if (_materialRunGPU.TryGetValue(key, out var cohorts))
                {
                    bool ready = false;
                    foreach (MaterialGPUResources gpu in cohorts)
                        if (CanReuseMaterialGPU(gpu)) { ready = true; break; }
                    if (ready) continue;
                    if (cohorts.Count >= MaxRunCohorts) return false;
                }
                newCohorts++;
            }
        }
        foreach (var passGroups in _renderTextGroups)
        {
            for (int group = 0; group < passGroups.Value.Count; group++)
            {
                TextBatchData batch = passGroups.Value[group];
                if (batch.Atlas is null || batch.TotalGlyphs == 0)
                    continue;
                var key = (renderer, renderer.BackendGeneration, batch.Atlas, passGroups.Key, batch.RunId);
                if (_textGPU.TryGetValue(key, out var cohorts))
                {
                    bool ready = false;
                    foreach (TextGPUResources gpu in cohorts)
                        if (CanReuseTextGPU(gpu)) { ready = true; break; }
                    if (ready) continue;
                    if (cohorts.Count >= MaxRunCohorts) return false;
                }
                newCohorts++;
            }
        }
        return CountGpuCohorts() + newCohorts <= MaxCanvasGpuCohorts;
    }

    #endregion

    #region Collect-Visible Thread API

    /// <summary>
    /// Collects one ordered, owner-frozen render packet. The caller supplies normal
    /// render commands for non-batch primitives at their exact stream position.
    /// </summary>
    public void CollectPublishedRuns(UIPublication publication, RenderCommandCollection passes,
        Action<UIRenderRecord> collectUnbatched)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(collectUnbatched);
        BeginPublicationCollection(publication.Generation);
        ReadOnlySpan<UIRenderRecord> records = publication.Renders;
        _reusePublishedRuns = ReferenceEquals(publication.RenderStorage, _renderPublishedStorage) &&
            ReferenceEquals(publication.CompositionStorage, _renderCompositionStorage);
        if (_reusePublishedRuns)
        {
            RetainedRunReuseCount++;
            int markerIndex = 0;
            for (int index = 0; index < records.Length; index++)
            {
                UIRenderRecord record = records[index];
                if (!record.Visible) continue;
                while (markerIndex < _renderPublishedMarkers.Count &&
                    _renderPublishedMarkers[markerIndex].RecordIndex == index)
                {
                    PublishedMarker marker = _renderPublishedMarkers[markerIndex++];
                    passes.AddCPU(AcquireMarker(marker.Pass, marker.ZIndex, marker.Kind, marker.GroupIndex));
                }
                if (!record.SupportsBatch)
                    collectUnbatched(record);
            }
            return;
        }

        _collectPublishedStorage = publication.RenderStorage;
        _collectCompositionStorage = publication.CompositionStorage;
        _collectPublishedMarkers.Clear();
        lock (_primitiveSlotGate)
            _membershipEpoch++;
        for (int index = 0; index < records.Length; index++)
        {
            _collectPublishedRecordIndex = index;
            UIRenderRecord record = records[index];
            ulong primitiveHandle = RetainPrimitive(in record);
            if (!record.Visible)
                continue;
            if (!record.SupportsBatch)
            {
                BreakBatchRun(record.RenderPass);
                collectUnbatched(record);
                continue;
            }
            publication.TryGetCompositionSegment(record.MembershipId, out UICompositionSegment segment);
            ulong segmentIdentity = segment?.Identity ?? 0;
            ulong segmentRevision = segment?.Revision ?? 0;
            if (record.IsText)
            {
                if (record.TextLayout is not { } layout || record.Atlas is null || layout.Glyphs.Length == 0)
                    continue;
                Matrix4x4 world = record.WorldMatrix;
                Vector4 color = record.TextColor;
                Vector4 outline = record.OutlineColor;
                Vector4 bounds = record.Bounds;
                AddTextQuad(record.RenderPass, record.ZIndex, passes, record.Atlas,
                    in world, in color, in outline, record.OutlineThickness, in bounds,
                    record.AtlasType, record.DistanceRange, record.DistanceRangeMiddle,
                    record.MsdfFillBias, record.DebugMode, layout, record.Crop, primitiveHandle, record.CompositionOwner, segmentIdentity, segmentRevision);
            }
            else
            {
                Matrix4x4 world = record.WorldMatrix;
                Vector4 color = record.QuadColor;
                Vector4 bounds = record.Bounds;
                AddMaterialQuad(record.RenderPass, record.ZIndex, passes,
                    in world, in color, in bounds, record.Crop, record.Texture,
                    record.UV, record.ImageSrgb, record.IsBorder,
                    record.BorderColor, record.BorderWidth, primitiveHandle, record.CompositionOwner, segmentIdentity, segmentRevision,
                    record.Backdrop);
            }
        }
        _collectPublishedRecordIndex = -1;
        ReconcileCollectedRunIds();
        RetireMissingPrimitives();
    }

    /// <summary>
    /// Register a material quad for batched rendering.
    /// Called from the collect-visible thread and inserts an inline dispatch marker into the pass stream.
    /// </summary>
    public void AddMaterialQuad(
        int renderPass,
        int zIndex,
        RenderCommandCollection passes,
        in Matrix4x4 worldMatrix,
        in Vector4 color,
        in Vector4 uixywh,
        BoundingRectangle? cropRegion = null,
        XRTexture2D? texture = null,
        Vector4 uv = default,
        bool imageSrgb = false,
        bool border = false,
        Vector4 borderColor = default,
        float borderWidth = 0.0f,
        ulong primitiveHandle = 0,
        UICompositionOwner compositionOwner = default,
        ulong compositionSegmentIdentity = 0,
        ulong compositionSegmentRevision = 0,
        UIBackdropRequest backdrop = default)
    {
        UIRuntimeCounters.Add(UIWorkCounter.BatchEntriesCollected);
        var state = GetOrCreateCollectPassState(renderPass);
        var groups = GetOrCreatePool(_collectMaterialGroups, renderPass);

        MaterialQuadBatchData batch;
        if (state.ActiveCompositionOwner == compositionOwner &&
            state.ActiveCompositionSegmentIdentity == compositionSegmentIdentity &&
            state.ActiveKind == EBatchMarkerKind.Material && state.ActiveZIndex == zIndex &&
            state.ActiveCropRegion == cropRegion &&
            ReferenceEquals(state.ActiveMaterialTexture, texture) &&
            state.ActiveMaterialImageSrgb == imageSrgb &&
            state.ActiveMaterialBorder == border &&
            groups.TryGet(state.ActiveGroupIndex, out var existingBatch) &&
            existingBatch.Backdrop == backdrop)
        {
            batch = existingBatch;
        }
        else
        {
            batch = groups.Acquire(out int groupIndex);
            batch.Clear();
            batch.RunId = AllocateRunId();
            batch.CompositionOwner = compositionOwner;
            batch.CompositionSegmentIdentity = compositionSegmentIdentity;
            batch.CompositionSegmentRevision = compositionSegmentRevision;
            state.ActiveCompositionOwner = compositionOwner;
            state.ActiveCompositionSegmentIdentity = compositionSegmentIdentity;
            batch.CropRegion = cropRegion;
            batch.Texture = texture;
            batch.ImageSrgb = imageSrgb;
            batch.Border = border;
            batch.Backdrop = backdrop;
            state.ActiveKind = EBatchMarkerKind.Material;
            state.ActiveZIndex = zIndex;
            state.ActiveCropRegion = cropRegion;
            state.ActiveMaterialTexture = texture;
            state.ActiveMaterialImageSrgb = imageSrgb;
            state.ActiveMaterialBorder = border;
            state.ActiveTextAtlas = null;
            state.ActiveGroupIndex = groupIndex;
            passes.AddCPU(AcquireMarker(renderPass, zIndex, EBatchMarkerKind.Material, groupIndex));
            if (_collectPublishedRecordIndex >= 0)
                _collectPublishedMarkers.Add(new(_collectPublishedRecordIndex, renderPass, zIndex,
                    EBatchMarkerKind.Material, groupIndex));
        }

        batch.Entries.Add(new MaterialQuadEntry
        {
            WorldMatrix = worldMatrix,
            Color = color,
            UIXYWH = uixywh,
            UV = uv,
            BorderColor = borderColor,
            BorderWidth = borderWidth,
            PrimitiveHandle = primitiveHandle
        });
    }

    /// <summary>
    /// Register a text component's glyph data for batched rendering.
    /// Called from the collect-visible thread.
    /// </summary>
    /// <param name="renderPass">The render pass index.</param>
    /// <param name="zIndex">The current 2D sort key for the source component.</param>
    /// <param name="passes">The pass collection used to inject ordered dispatch markers.</param>
    /// <param name="fontAtlas">The font atlas texture used by this text.</param>
    /// <param name="worldMatrix">The text component's world matrix.</param>
    /// <param name="textColor">The text color.</param>
    /// <param name="uixywh">The UI bounds (x, y, w, h).</param>
    /// <param name="glyphs">Snapshot of glyph layout data.</param>
    public void AddTextQuad(
        int renderPass,
        int zIndex,
        RenderCommandCollection passes,
        XRTexture2D fontAtlas,
        in Matrix4x4 worldMatrix,
        in Vector4 textColor,
        in Vector4 outlineColor,
        float outlineThickness,
        in Vector4 uixywh,
        int atlasType,
        float distanceRange,
        float distanceRangeMiddle,
        float msdfFillBias,
        int debugMode,
        UITextLayoutResult layout,
        BoundingRectangle? cropRegion = null,
        ulong primitiveHandle = 0,
        UICompositionOwner compositionOwner = default,
        ulong compositionSegmentIdentity = 0,
        ulong compositionSegmentRevision = 0)
    {
        UIRuntimeCounters.Add(UIWorkCounter.BatchEntriesCollected);
        var state = GetOrCreateCollectPassState(renderPass);
        var groups = GetOrCreatePool(_collectTextGroups, renderPass);

        TextBatchData batch;
        if (state.ActiveCompositionOwner == compositionOwner &&
            state.ActiveCompositionSegmentIdentity == compositionSegmentIdentity && TextBatchMatches(state, zIndex, fontAtlas, atlasType, distanceRange, distanceRangeMiddle, msdfFillBias, debugMode, cropRegion) &&
            groups.TryGet(state.ActiveGroupIndex, out var existingBatch))
        {
            batch = existingBatch;
        }
        else
        {
            batch = groups.Acquire(out int groupIndex);
            batch.Clear();
            batch.RunId = AllocateRunId();
            batch.CompositionOwner = compositionOwner;
            batch.CompositionSegmentIdentity = compositionSegmentIdentity;
            batch.CompositionSegmentRevision = compositionSegmentRevision;
            state.ActiveCompositionOwner = compositionOwner;
            state.ActiveCompositionSegmentIdentity = compositionSegmentIdentity;
            batch.CropRegion = cropRegion;
            batch.Atlas = fontAtlas;
            batch.AtlasType = atlasType;
            batch.DistanceRange = distanceRange;
            batch.DistanceRangeMiddle = distanceRangeMiddle;
            batch.MsdfFillBias = msdfFillBias;
            batch.DebugMode = debugMode;
            state.ActiveKind = EBatchMarkerKind.Text;
            state.ActiveZIndex = zIndex;
            state.ActiveCropRegion = cropRegion;
            state.ActiveTextAtlas = fontAtlas;
            state.ActiveTextAtlasType = atlasType;
            state.ActiveTextDistanceRange = distanceRange;
            state.ActiveTextDistanceRangeMiddle = distanceRangeMiddle;
            state.ActiveTextMsdfFillBias = msdfFillBias;
            state.ActiveTextDebugMode = debugMode;
            state.ActiveGroupIndex = groupIndex;
            passes.AddCPU(AcquireMarker(renderPass, zIndex, EBatchMarkerKind.Text, groupIndex));
            if (_collectPublishedRecordIndex >= 0)
                _collectPublishedMarkers.Add(new(_collectPublishedRecordIndex, renderPass, zIndex,
                    EBatchMarkerKind.Text, groupIndex));
        }

        int glyphCount = layout.Glyphs.Length;

        batch.Entries.Add(new TextEntry
        {
            WorldMatrix = worldMatrix,
            TextColor = textColor,
            OutlineColor = outlineColor,
            OutlineThickness = outlineThickness,
            UIXYWH = uixywh,
            Layout = layout,
            GlyphCount = glyphCount,
            PrimitiveHandle = primitiveHandle,
        });
        batch.HasOutline |= outlineThickness > 0.0f && outlineColor.W > 0.0f;
        batch.TotalGlyphs += glyphCount;

        if (BatchDiagnosticsEnabled && s_textAddDiagCount++ < 40)
        {
            UITextLayoutGlyph firstGlyph = glyphCount > 0 ? layout.Glyphs.Span[0] : default;
            Debug.Log(
                ELogCategory.UI,
                "[FpsTextDiag] UIBatchCollector.AddTextQuad #{0}: pass={1} z={2} group={3} entries={4} totalGlyphs={5} glyphs={6} atlasType={7} range={8:F2} middle={9:F2} bias={10:F2} debugMode={11} color=({12:F2},{13:F2},{14:F2},{15:F2}) worldT=({16:F1},{17:F1},{18:F1}) bounds=({19:F1},{20:F1},{21:F1},{22:F1}) firstTfm=({23:F2},{24:F2},{25:F2},{26:F2}) firstUv=({27:F5},{28:F5},{29:F5},{30:F5}) atlas=({31}x{32})",
                s_textAddDiagCount,
                renderPass,
                zIndex,
                state.ActiveGroupIndex,
                batch.Entries.Count,
                batch.TotalGlyphs,
                glyphCount,
                atlasType,
                distanceRange,
                distanceRangeMiddle,
                msdfFillBias,
                debugMode,
                textColor.X,
                textColor.Y,
                textColor.Z,
                textColor.W,
                worldMatrix.M41,
                worldMatrix.M42,
                worldMatrix.M43,
                uixywh.X,
                uixywh.Y,
                uixywh.Z,
                uixywh.W,
                firstGlyph.Transform.X,
                firstGlyph.Transform.Y,
                firstGlyph.Transform.Z,
                firstGlyph.Transform.W,
                firstGlyph.Uvs.X,
                firstGlyph.Uvs.Y,
                firstGlyph.Uvs.Z,
                firstGlyph.Uvs.W,
                fontAtlas.Width,
                fontAtlas.Height);
        }
    }

    public void BreakBatchRun(int renderPass)
    {
        if (_collectPassStates.TryGetValue(renderPass, out var state))
            state.Reset();
    }

    #endregion

    #region Swap Buffers

    /// <summary>
    /// Discards only the uncommitted collect side. The caller must first discard
    /// the paired RenderCommandCollection updating commands so no marker still
    /// refers to a reused slot.
    /// </summary>
    public void DiscardCollectedCandidate()
    {
        CollectedPublicationGeneration = 0;
        _reusePublishedRuns = false;
        _collectPublishedRecordIndex = -1;
        _collectPublishedStorage = null;
        _collectCompositionStorage = null;
        _collectPublishedMarkers.Clear();
        foreach (var groups in _collectMaterialGroups.Values)
            groups.Reset(static batch => batch.Clear());
        foreach (var groups in _collectTextGroups.Values)
            groups.Reset(static batch => batch.Clear());
        foreach (var state in _collectPassStates.Values)
            state.Reset();
        foreach (var markerPool in _markerPools.Values)
            markerPool.ResetUsage();
    }

    /// <summary>
    /// Swaps collecting and rendering batch data.
    /// Call between collect-visible and render, alongside <see cref="Rendering.Commands.RenderCommandCollection.SwapBuffers"/>.
    /// </summary>
    public void SwapBuffers()
    {
        UIRuntimeCounters.Add(UIWorkCounter.BatchCollectionResets);
        if (_reusePublishedRuns)
        {
            _reusePublishedRuns = false;
            _collectPublishedRecordIndex = -1;
            foreach (var state in _collectPassStates.Values)
                state.Reset();
            foreach (var markerPool in _markerPools.Values)
                markerPool.ResetUsage();
            return;
        }
        (_collectMaterialGroups, _renderMaterialGroups) = (_renderMaterialGroups, _collectMaterialGroups);
        (_collectTextGroups, _renderTextGroups) = (_renderTextGroups, _collectTextGroups);
        (_collectPublishedMarkers, _renderPublishedMarkers) = (_renderPublishedMarkers, _collectPublishedMarkers);
        _renderPublishedStorage = _collectPublishedStorage;
        _renderCompositionStorage = _collectCompositionStorage;
        _collectPublishedStorage = null;
        _collectCompositionStorage = null;
        _collectPublishedMarkers.Clear();

        foreach (var groups in _collectMaterialGroups.Values)
            groups.Reset(static batch => batch.Clear());
        foreach (var groups in _collectTextGroups.Values)
            groups.Reset(static batch => batch.Clear());
        foreach (var state in _collectPassStates.Values)
            state.Reset();
        foreach (var markerPool in _markerPools.Values)
            markerPool.ResetUsage();
    }

    /// <summary>
    /// Clears both collecting and rendering-side batch lists immediately.
    /// Useful when toggling between batched and strict per-item rendering modes.
    /// </summary>
    public void Clear()
    {
        CollectedPublicationGeneration = 0;
        _reusePublishedRuns = false;
        _collectPublishedRecordIndex = -1;
        _collectPublishedStorage = null;
        _collectCompositionStorage = null;
        _renderPublishedStorage = null;
        _renderCompositionStorage = null;
        _collectPublishedMarkers.Clear();
        _renderPublishedMarkers.Clear();
        lock (_primitiveSlotGate)
        {
            _slotByIdentity.Clear();
            _registeredSlotByMembership.Clear();
            _primitiveSlots.Clear();
            _freePrimitiveSlots.Clear();
        }
        foreach (var groups in _collectMaterialGroups.Values)
            groups.Reset(static batch => batch.Clear());
        foreach (var groups in _renderMaterialGroups.Values)
            groups.Reset(static batch => batch.Clear());

        foreach (var groups in _collectTextGroups.Values)
            groups.Reset(static batch => batch.Clear());
        foreach (var groups in _renderTextGroups.Values)
            groups.Reset(static batch => batch.Clear());

        foreach (var state in _collectPassStates.Values)
            state.Reset();
        foreach (var markerPool in _markerPools.Values)
            markerPool.ResetUsage();
    }

    #endregion

    #region Render Thread API

    public void RenderMaterialQuadBatch(int renderPass)
    {
        if (!Enabled || !_renderMaterialGroups.TryGetValue(renderPass, out var groups) || groups.Count == 0)
            return;

        for (int i = 0; i < groups.Count; i++)
            RenderMaterialGroup(renderPass, i);
    }

    /// <summary>
    /// Renders all batched text quads for the given render pass.
    /// Issues one instanced draw per font atlas.
    /// Call from the render thread.
    /// </summary>
    public void RenderTextBatch(int renderPass)
    {
        if (!Enabled || !_renderTextGroups.TryGetValue(renderPass, out var groups) || groups.Count == 0)
            return;

        for (int i = 0; i < groups.Count; i++)
            RenderTextGroup(renderPass, i);
    }

    #endregion

    #region GPU Resource Management — Material Quads

    private bool CanReuseMaterialGPU(MaterialGPUResources gpu)
    {
        if (gpu.Receipt is not null)
        {
            if (!ReleaseRetiredReceipt(ref gpu.Receipt))
                return false;
            gpu.Submitted = false;
        }
        if (!gpu.Submitted)
            return true;
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan)
            return HasCompletedOrAbandonedVulkanFence(gpu.Fence) &&
                CanReuseVulkanBuffer(gpu.Transform) && CanReuseVulkanBuffer(gpu.Color) &&
                CanReuseVulkanBuffer(gpu.Bounds) &&
                CanReuseVulkanBuffer(gpu.DrawIndex) &&
                (!gpu.UsedUv || CanReuseVulkanBuffer(gpu.Uv)) &&
                (!gpu.UsedBorderWidth || CanReuseVulkanBuffer(gpu.BorderWidth));
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.OpenGL)
            return IsFenceSignaled(gpu.Fence);
        return AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
    }

    private bool CanReuseTextGPU(TextGPUResources gpu)
    {
        if (gpu.Receipt is not null)
        {
            if (!ReleaseRetiredReceipt(ref gpu.Receipt))
                return false;
            gpu.Submitted = false;
        }
        if (!gpu.Submitted)
            return true;
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan)
            return HasCompletedOrAbandonedVulkanFence(gpu.Fence) &&
                CanReuseVulkanBuffer(gpu.GlyphTransformsBuf) &&
                CanReuseVulkanBuffer(gpu.GlyphTexCoordsBuf) &&
                CanReuseVulkanBuffer(gpu.TextInstanceBuf) &&
                CanReuseVulkanBuffer(gpu.GlyphTextIndexBuf) &&
                CanReuseVulkanBuffer(gpu.GlyphDrawIndexBuf);
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.OpenGL)
            return IsFenceSignaled(gpu.Fence);
        return AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
    }

    private static bool CanReuseVulkanBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return true;
        return AbstractRenderer.Current is IRuntimeRendererHost host &&
            host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var reuse) &&
            reuse?.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;
    }

    private static bool IsFenceSignaled(XRGpuFence? fence)
        => fence is not null && fence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted &&
           fence.Poll() == EGpuFenceStatus.Signaled;

    private static bool HasCompletedOrAbandonedVulkanFence(XRGpuFence? fence)
    {
        if (fence is null)
            return false;
        return IsCompletedOrAbandonedVulkanSubmission(
            fence.SubmissionStatus, fence.FirstFailureDiagnostic?.NativeSubmission,
            fence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted && fence.Poll() == EGpuFenceStatus.Signaled);
    }

    /// <summary>Only a completed submission or a proven never-submitted rollback can release a cohort.</summary>
    internal static bool IsCompletedOrAbandonedVulkanSubmission(
        EGpuFenceSubmissionStatus status, EGpuFenceNativeSubmission? nativeSubmission, bool signaled)
        => status == EGpuFenceSubmissionStatus.Submitted && signaled ||
           status == EGpuFenceSubmissionStatus.Failed &&
           nativeSubmission == EGpuFenceNativeSubmission.NotCalled;

    private void RetireInactiveGpuCohorts(AbstractRenderer renderer)
    {
        SweepDeferredCohorts(renderer);
        BuildActiveGpuRunKeys();
        _staleMaterialRunKeys.Clear();
        foreach (var pair in _materialRunGPU)
        {
            var key = pair.Key;
            if (!ReferenceEquals(key.Renderer, renderer) || key.BackendGeneration != renderer.BackendGeneration ||
                _activeMaterialRunKeys.Contains((key.Pass, key.RunId)))
                continue;
            bool ready = true;
            foreach (MaterialGPUResources gpu in pair.Value)
                if (!CanReuseMaterialGPU(gpu))
                {
                    ready = false;
                    break;
                }
            if (ready)
                _staleMaterialRunKeys.Add(key);
        }
        foreach (var key in _staleMaterialRunKeys)
        {
            List<MaterialGPUResources> cohorts = _materialRunGPU[key];
            while (cohorts.Count != 0)
            {
                MaterialGPUResources gpu = cohorts[^1];
                DestroyMaterialGPU(gpu);
                cohorts.RemoveAt(cohorts.Count - 1);
                _gpuCohortCount--;
            }
            _materialRunGPU.Remove(key);
        }

        _staleTextRunKeys.Clear();
        foreach (var pair in _textGPU)
        {
            var key = pair.Key;
            bool active = _activeTextRunKeys.Contains((key.Atlas, key.Pass, key.RunId));
            if (!ReferenceEquals(key.Renderer, renderer) || key.BackendGeneration != renderer.BackendGeneration || active)
                continue;
            bool ready = true;
            foreach (TextGPUResources gpu in pair.Value)
                if (!CanReuseTextGPU(gpu))
                {
                    ready = false;
                    break;
                }
            if (ready)
                _staleTextRunKeys.Add(key);
        }
        foreach (var key in _staleTextRunKeys)
        {
            List<TextGPUResources> cohorts = _textGPU[key];
            while (cohorts.Count != 0)
            {
                TextGPUResources gpu = cohorts[^1];
                gpu.Dispose();
                cohorts.RemoveAt(cohorts.Count - 1);
                _gpuCohortCount--;
            }
            _textGPU.Remove(key);
        }
    }

    private static void SweepDeferredCohorts(AbstractRenderer renderer)
    {
        lock (CollectorRegistryGate)
        {
            for (int index = UnclaimedReceiptLeases.Count - 1; index >= 0; index--)
            {
                ReceiptLease lease = UnclaimedReceiptLeases[index];
                if (!ReferenceEquals(lease.Renderer, renderer) || !lease.IsRetired) continue;
                lease.Fence?.Dispose();
                lease.Fence = null;
                UnclaimedReceiptLeases.RemoveAt(index);
            }
            for (int index = DeferredCohortRetirement.Count - 1; index >= 0; index--)
            {
                var deferred = DeferredCohortRetirement[index];
                if (!ReferenceEquals(deferred.Renderer, renderer) ||
                    deferred.Material?.Receipt is { } materialLease && !materialLease.IsRetired ||
                    deferred.Text?.Receipt is { } textLease && !textLease.IsRetired)
                    continue;
                ForceReleaseReceipt(deferred.Material?.Receipt);
                ForceReleaseReceipt(deferred.Text?.Receipt);
                if (deferred.Material is not null) DestroyMaterialGPU(deferred.Material);
                deferred.Text?.Dispose();
                DeferredCohortRetirement.RemoveAt(index);
            }
        }
    }

    private int CountGpuCohorts() => _gpuCohortCount;

    private void BuildActiveGpuRunKeys()
    {
        _activeMaterialRunKeys.Clear();
        _activeTextRunKeys.Clear();
        LastActiveRunBuildVisitCount = 0;
        foreach (var pass in _renderMaterialGroups)
            for (int index = 0; index < pass.Value.Count; index++)
            {
                LastActiveRunBuildVisitCount++;
                _activeMaterialRunKeys.Add((pass.Key, pass.Value[index].RunId));
            }
        foreach (var pass in _renderTextGroups)
            for (int index = 0; index < pass.Value.Count; index++)
            {
                LastActiveRunBuildVisitCount++;
                TextBatchData batch = pass.Value[index];
                if (batch.Atlas is not null)
                    _activeTextRunKeys.Add((batch.Atlas, pass.Key, batch.RunId));
            }
    }

    private MaterialGPUResources AcquireMaterialGPU(AbstractRenderer renderer, int renderPass, ulong runId)
    {
        var key = (renderer, renderer.BackendGeneration, renderPass, runId);
        if (!_materialRunGPU.TryGetValue(key, out var cohorts))
            cohorts = [];
        for (int i = 0; i < cohorts.Count; i++)
            if (CanReuseMaterialGPU(cohorts[i]))
                return cohorts[i];
        if (cohorts.Count >= MaxRunCohorts)
            throw new InvalidOperationException($"UI material run ({renderPass}, {runId}) has no retired GPU cohort.");
        if (CountGpuCohorts() >= MaxCanvasGpuCohorts)
            RetireInactiveGpuCohorts(renderer);
        if (CountGpuCohorts() >= MaxCanvasGpuCohorts)
            throw new InvalidOperationException("UI GPU cohort budget is full while earlier draws remain in use.");
        MaterialGPUResources gpu = new();
        if (!_materialRunGPU.ContainsKey(key))
            _materialRunGPU.Add(key, cohorts);
        cohorts.Add(gpu);
        _gpuCohortCount++;
        return gpu;
    }

    private void LoadMaterialGPU(MaterialGPUResources gpu)
    {
        _matQuadMesh = gpu.Mesh;
        _matQuadTransformBuf = gpu.Transform;
        _matQuadColorBuf = gpu.Color;
        _matQuadBoundsBuf = gpu.Bounds;
        _matQuadUvBuf = gpu.Uv;
        _matQuadBorderWidthBuf = gpu.BorderWidth;
        _matQuadDrawIndexBuf = gpu.DrawIndex;
        _matQuadBorderMesh = gpu.BorderMesh;
        _matQuadBackdropMesh = gpu.BackdropMesh;
        _matQuadBackdropTexture = gpu.BackdropTexture;
        _materialTextureMeshes = gpu.TextureMeshes;
        _staleMaterialTextures = gpu.StaleTextures;
        _matQuadCapacity = gpu.Capacity;
        _matQuadNeedsPush = gpu.NeedsPush;
    }

    private void StoreMaterialGPU(MaterialGPUResources gpu)
    {
        gpu.Mesh = _matQuadMesh;
        gpu.Transform = _matQuadTransformBuf;
        gpu.Color = _matQuadColorBuf;
        gpu.Bounds = _matQuadBoundsBuf;
        gpu.Uv = _matQuadUvBuf;
        gpu.BorderWidth = _matQuadBorderWidthBuf;
        gpu.DrawIndex = _matQuadDrawIndexBuf;
        gpu.BorderMesh = _matQuadBorderMesh;
        gpu.BackdropMesh = _matQuadBackdropMesh;
        gpu.BackdropTexture = _matQuadBackdropTexture;
        gpu.TextureMeshes = _materialTextureMeshes;
        gpu.StaleTextures = _staleMaterialTextures;
        gpu.Capacity = _matQuadCapacity;
        gpu.NeedsPush = _matQuadNeedsPush;
    }

    private static bool MaterialEntriesMatch(MaterialGPUResources gpu, List<MaterialQuadEntry> entries)
    {
        if (!gpu.HasUploadedEntries || gpu.UploadedEntries.Count != entries.Count)
            return false;
        for (int index = 0; index < entries.Count; index++)
        {
            MaterialQuadEntry old = gpu.UploadedEntries[index];
            MaterialQuadEntry current = entries[index];
            if (old.WorldMatrix != current.WorldMatrix || old.Color != current.Color ||
                old.UIXYWH != current.UIXYWH || old.UV != current.UV ||
                old.BorderColor != current.BorderColor || old.BorderWidth != current.BorderWidth ||
                old.PrimitiveHandle != current.PrimitiveHandle)
                return false;
        }
        return true;
    }

    private static void RememberMaterialEntries(MaterialGPUResources gpu, List<MaterialQuadEntry> entries)
    {
        gpu.UploadedEntries.Clear();
        for (int index = 0; index < entries.Count; index++)
            gpu.UploadedEntries.Add(entries[index]);
        gpu.HasUploadedEntries = true;
    }

    private void EnsureMaterialQuadMesh()
    {
        if (_matQuadMesh is not null)
            return;

        // Create the batched material
        bool webGpu = AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
        XRShader[] shaders = webGpu ? [] : UIBatchMaterialPrototype.Solid.CreateDesktopShaders();

        var material = new XRMaterial(Array.Empty<ShaderVar>(), shaders)
        {
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = UIBatchMaterialPrototype.Solid.CreateRenderOptions(webGpu)
        };
        if (webGpu)
            material.EngineSemantic = EngineMaterialSemanticIdentity.UIQuadBatchedV2;

        _matQuadMesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material)
        {
            Name = "UIBatchMaterialQuadRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        if (_matQuadMesh.Mesh is not null)
            _matQuadMesh.Mesh.Name = "UIBatchMaterialQuadMesh";

        DisableShaderPipelines(_matQuadMesh);
        _matQuadMesh.EnsureRenderPipelineVersionsCreated();

        // Create initial SSBOs with a reasonable starting capacity
        _matQuadCapacity = 64;
        CreateMaterialQuadBuffers(_matQuadMesh);
        _matQuadNeedsPush = true; // Ensure first upload allocates GPU buffers via PushData
    }

    private void CreateMaterialQuadBuffers(XRMeshRenderer mesh)
    {
        _matQuadTransformBuf?.Destroy();
        _matQuadColorBuf?.Destroy();
        _matQuadBoundsBuf?.Destroy();
        _matQuadUvBuf?.Destroy();
        _matQuadBorderWidthBuf?.Destroy();
        _matQuadDrawIndexBuf?.Destroy();

        // Transform buffer: 4 vec4 rows per matrix, so elementCount = capacity * 4
        _matQuadTransformBuf = new XRDataBuffer<Vector4>(
            "QuadTransformBuffer", EBufferTarget.ShaderStorageBuffer,
            _matQuadCapacity * 4)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 0,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadTransformBuffer"] = _matQuadTransformBuf;

        // Color buffer: 1 vec4 per instance
        _matQuadColorBuf = new XRDataBuffer<Vector4>(
            "QuadColorBuffer", EBufferTarget.ShaderStorageBuffer,
            _matQuadCapacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 1,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadColorBuffer"] = _matQuadColorBuf;

        // Bounds buffer: 1 vec4 per instance
        _matQuadBoundsBuf = new XRDataBuffer<Vector4>(
            "QuadBoundsBuffer", EBufferTarget.ShaderStorageBuffer,
            _matQuadCapacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 2,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadBoundsBuffer"] = _matQuadBoundsBuf;

        _matQuadUvBuf = new XRDataBuffer<Vector4>(
            "QuadUvBuffer", EBufferTarget.ShaderStorageBuffer, _matQuadCapacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 3,
            DisposeOnPush = false
        };
        if (AbstractRenderer.Current?.BackendId != RendererBackendId.WebGPU)
        {
            _matQuadBorderWidthBuf = new XRDataBuffer<Vector4>(
                "QuadBorderWidthBuffer", EBufferTarget.ShaderStorageBuffer, _matQuadCapacity)
            {
                Usage = EBufferUsage.StreamDraw,
                BindingIndexOverride = 4,
                DisposeOnPush = false
            };
            _matQuadDrawIndexBuf = new XRDataBuffer<uint>(
                "QuadDrawIndexBuffer", EBufferTarget.ShaderStorageBuffer, _matQuadCapacity)
            {
                Usage = EBufferUsage.StreamDraw,
                BindingIndexOverride = 5,
                DisposeOnPush = false
            };
            mesh.Buffers["QuadDrawIndexBuffer"] = _matQuadDrawIndexBuf;
        }
    }

    private XRMeshRenderer EnsureMaterialBorderMesh()
    {
        if (_matQuadBorderMesh is not null)
            return _matQuadBorderMesh;
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU)
            throw new NotSupportedException("A cooked WebGPU border primitive is not available.");
        EnsureMaterialQuadMesh();
        XRMaterial material = new(Array.Empty<ShaderVar>(),
            UIBatchMaterialPrototype.Border.CreateDesktopShaders())
        {
            Name = "UIBatchBorder",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = UIBatchMaterialPrototype.Border.CreateRenderOptions(false)
        };
        XRMeshRenderer mesh = new(_matQuadMesh!.Mesh!, material)
        {
            Name = "UIBatchBorderRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        mesh.Buffers["QuadTransformBuffer"] = _matQuadTransformBuf!;
        mesh.Buffers["QuadColorBuffer"] = _matQuadColorBuf!;
        mesh.Buffers["QuadBoundsBuffer"] = _matQuadBoundsBuf!;
        mesh.Buffers["QuadUvBuffer"] = _matQuadUvBuf!;
        mesh.Buffers["QuadBorderWidthBuffer"] = _matQuadBorderWidthBuf!;
        mesh.Buffers["QuadDrawIndexBuffer"] = _matQuadDrawIndexBuf!;
        DisableShaderPipelines(mesh);
        mesh.EnsureRenderPipelineVersionsCreated();
        _matQuadBorderMesh = mesh;
        return mesh;
    }

    private XRMeshRenderer EnsureMaterialTextureMesh(XRTexture2D texture, bool srgb)
    {
        var key = (texture, srgb);
        if (_materialTextureMeshes.TryGetValue(key, out XRMeshRenderer? existing))
            return existing;

        // Old image materials are retired on the render thread. They borrow the
        // image resource, so replacement must not destroy the authored texture.
        _staleMaterialTextures.Clear();
        foreach (var cached in _materialTextureMeshes.Keys)
            if (!IsCurrentMaterialTexture(cached))
                _staleMaterialTextures.Add(cached);
        foreach (var stale in _staleMaterialTextures)
        {
            XRMeshRenderer retired = _materialTextureMeshes[stale];
            XRMaterial? retiredMaterial = retired.Material;
            retired.Destroy();
            retiredMaterial?.Destroy();
            _materialTextureMeshes.Remove(stale);
        }
        if (_materialTextureMeshes.Count >= MaxMaterialTextureMeshes)
            throw new NotSupportedException("UI.ImageTextureBudgetExceeded: at most 64 distinct image and color-space bindings may be visible in one frame.");

        EnsureMaterialQuadMesh();
        bool webGpu = AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
        XRShader[] shaders = webGpu ? [] : UIBatchMaterialPrototype.Image.CreateDesktopShaders();
        XRMaterial material = new(Array.Empty<ShaderVar>(), [texture], shaders)
        {
            Name = "UIBatchMaterialTexture",
            SurfaceTextureBindings = [new(EMaterialTextureSemantic.BaseColor, texture, IsSrgb: srgb)],
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = UIBatchMaterialPrototype.Image.CreateRenderOptions(webGpu)
        };
        if (webGpu)
            material.EngineSemantic = EngineMaterialSemanticIdentity.UIQuadBatchedTextureV2;
        XRMeshRenderer mesh = new(_matQuadMesh!.Mesh!, material)
        {
            Name = "UIBatchMaterialTextureRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        mesh.Buffers["QuadTransformBuffer"] = _matQuadTransformBuf!;
        mesh.Buffers["QuadColorBuffer"] = _matQuadColorBuf!;
        mesh.Buffers["QuadBoundsBuffer"] = _matQuadBoundsBuf!;
        mesh.Buffers["QuadUvBuffer"] = _matQuadUvBuf!;
        if (!webGpu)
            mesh.Buffers["QuadDrawIndexBuffer"] = _matQuadDrawIndexBuf!;
        mesh.BindingPublishers.Add(new UIRequiredTextureBindings(texture));
        DisableShaderPipelines(mesh);
        mesh.EnsureRenderPipelineVersionsCreated();
        _materialTextureMeshes.Add(key, mesh);
        return mesh;
    }

    private bool IsCurrentMaterialTexture((XRTexture2D Texture, bool Srgb) key)
    {
        foreach (BatchPool<MaterialQuadBatchData> groups in _renderMaterialGroups.Values)
            for (int index = 0; index < groups.Count; index++)
                if (ReferenceEquals(groups[index].Texture, key.Texture) && groups[index].ImageSrgb == key.Srgb)
                    return true;
        return false;
    }

    private void UploadMaterialQuadData(List<MaterialQuadEntry> entries, bool textured, bool border, MaterialGPUResources? cohort)
    {
        uint count = (uint)entries.Count;
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU && count > 65536)
            throw new NotSupportedException("WebGPU.UI.QuadCapacityExceeded: one screen UI quad group admits at most 65536 instances.");
        if (cohort is not null)
        {
            UploadRetainedMaterialData(entries, textured, border, cohort);
            return;
        }

        // Resize if needed
        if (count > _matQuadCapacity)
        {
            _matQuadCapacity = NextPowerOf2(count);
            _matQuadTransformBuf!.Resize(_matQuadCapacity * 4);
            _matQuadColorBuf!.Resize(_matQuadCapacity);
            _matQuadBoundsBuf!.Resize(_matQuadCapacity);
            _matQuadUvBuf?.Resize(_matQuadCapacity);
            _matQuadBorderWidthBuf?.Resize(_matQuadCapacity);
            _matQuadNeedsPush = true;
            if (cohort is not null)
            {
                cohort.HasUploadedBorder = false;
                cohort.HasUploadedImage = false;
            }
        }

        // A cohort is writable only after its prior draw has completed. Compare
        // against that cohort's own uploaded snapshot, not another frame's run.
        bool fullUpload = _matQuadNeedsPush || cohort is null || !cohort.HasUploadedEntries ||
            border && !cohort.HasUploadedBorder || textured && !cohort.HasUploadedImage ||
            cohort.UploadedEntries.Count < entries.Count;
        if (!fullUpload)
        {
            UploadChangedMaterialRanges(entries, cohort!.UploadedEntries, textured, border);
            return;
        }

        // Write transform data (4 × vec4 per instance = 16 floats per matrix)
        uint transformElementCount = fullUpload ? _matQuadCapacity * 4u : count * 4u;
        uint instanceElementCount = fullUpload ? _matQuadCapacity : count;
        XRBufferWriter<Vector4> transformWriter = _matQuadTransformBuf!.Alloc<Vector4>(transformElementCount);
        XRBufferWriter<Vector4> colorWriter = _matQuadColorBuf!.Alloc<Vector4>(instanceElementCount);
        XRBufferWriter<Vector4> boundsWriter = _matQuadBoundsBuf!.Alloc<Vector4>(instanceElementCount);
        XRBufferWriter<Vector4> uvWriter = textured || border
            ? _matQuadUvBuf!.Alloc<Vector4>(instanceElementCount) : default;
        XRBufferWriter<Vector4> borderWidthWriter = border
            ? _matQuadBorderWidthBuf!.Alloc<Vector4>(instanceElementCount) : default;
        Span<Vector4> transforms = transformWriter.Span;
        Span<Vector4> colors = colorWriter.Span;
        Span<Vector4> bounds = boundsWriter.Span;
        Span<Vector4> uvs = textured || border ? uvWriter.Span : default;
        Span<Vector4> borderWidths = border ? borderWidthWriter.Span : default;
        int transformIndex = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            ref readonly var e = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries)[i];

            // Write matrix rows (row-major, shader transposes)
            WriteMatrix4x4(transforms, ref transformIndex, in e.WorldMatrix);
            colors[i] = e.Color;
            bounds[i] = e.UIXYWH;
            if (textured)
                uvs[i] = e.UV;
            else if (border)
            {
                uvs[i] = e.BorderColor;
                borderWidths[i] = new Vector4(e.BorderWidth, 0, 0, 0);
            }
        }

        transformWriter.Commit();
        colorWriter.Commit();
        boundsWriter.Commit();
        if (textured || border)
            uvWriter.Commit();
        if (border)
            borderWidthWriter.Commit();
        UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted,
            ((long)transformElementCount + instanceElementCount * (border ? 4L : textured ? 3L : 2L)) * 16L);
        UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges, border ? 5 : textured ? 4 : 3);
        _matQuadNeedsPush = false;
    }

    private void UploadRetainedMaterialData(List<MaterialQuadEntry> entries, bool textured, bool border,
        MaterialGPUResources gpu)
    {
        List<uint> draw = gpu.CurrentDrawIndices;
        List<(uint Slot, MaterialQuadEntry Entry)> slots = gpu.ScratchSlots;
        draw.Clear();
        slots.Clear();
        gpu.Slots.BeginFrame();
        for (int index = 0; index < entries.Count; index++)
        {
            MaterialQuadEntry entry = entries[index];
            uint slot = gpu.Slots.Reserve(entry.PrimitiveHandle, index).Index;
            draw.Add(slot);
            slots.Add((slot, entry));
        }
        gpu.Slots.EndFrame();
        uint storageCount = gpu.Slots.HighWater;
        if (storageCount > MaxRetainedQuadSlotsPerRun)
            throw new InvalidOperationException("A UI quad run exceeds its retained GPU slot budget.");
        slots.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));

        bool full = _matQuadNeedsPush || !gpu.HasUploadedEntries ||
            gpu.UploadedVariant != (border ? 2 : textured ? 1 : 0);
        if (storageCount > _matQuadCapacity)
        {
            _matQuadCapacity = NextPowerOf2(storageCount);
            _matQuadTransformBuf!.Resize(_matQuadCapacity * 4);
            _matQuadColorBuf!.Resize(_matQuadCapacity);
            _matQuadBoundsBuf!.Resize(_matQuadCapacity);
            _matQuadUvBuf!.Resize(_matQuadCapacity);
            _matQuadBorderWidthBuf!.Resize(_matQuadCapacity);
            _matQuadDrawIndexBuf!.Resize(_matQuadCapacity);
            full = true;
        }
        while (gpu.UploadedSlots.Count < storageCount)
        {
            gpu.UploadedSlots.Add(default);
            gpu.InitializedSlots.Add(false);
        }

        if (full)
        {
            // The full write clears holes in the CPU/GPU buffers. Their old snapshots
            // are no longer resident, even if a later member has identical values.
            for (int index = 0; index < gpu.InitializedSlots.Count; index++)
                gpu.InitializedSlots[index] = false;
            XRBufferWriter<Vector4> transforms = _matQuadTransformBuf!.Alloc<Vector4>(storageCount * 4);
            XRBufferWriter<Vector4> colors = _matQuadColorBuf!.Alloc<Vector4>(storageCount);
            XRBufferWriter<Vector4> bounds = _matQuadBoundsBuf!.Alloc<Vector4>(storageCount);
            XRBufferWriter<Vector4> uvs = textured || border
                ? _matQuadUvBuf!.Alloc<Vector4>(storageCount) : default;
            XRBufferWriter<Vector4> widths = border
                ? _matQuadBorderWidthBuf!.Alloc<Vector4>(storageCount) : default;
            XRBufferWriter<uint> indices = _matQuadDrawIndexBuf!.Alloc<uint>((uint)draw.Count);
            transforms.Span.Clear();
            colors.Span.Clear();
            bounds.Span.Clear();
            if (textured || border) uvs.Span.Clear();
            if (border) widths.Span.Clear();
            indices.Span.Clear();
            foreach ((uint slot, MaterialQuadEntry entry) in slots)
            {
                int matrix = checked((int)slot * 4);
                WriteMatrix4x4(transforms.Span, ref matrix, in entry.WorldMatrix);
                colors.Span[(int)slot] = entry.Color;
                bounds.Span[(int)slot] = entry.UIXYWH;
                if (textured || border)
                    uvs.Span[(int)slot] = border ? entry.BorderColor : entry.UV;
                if (border)
                    widths.Span[(int)slot] = new Vector4(entry.BorderWidth, 0, 0, 0);
            }
            for (int index = 0; index < draw.Count; index++)
                indices.Span[index] = draw[index];
            transforms.Commit();
            colors.Commit();
            bounds.Commit();
            if (textured || border) uvs.Commit();
            if (border) widths.Commit();
            indices.Commit();
            UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted,
                (long)storageCount * (64L + 16L + 16L +
                    (textured || border ? 16L : 0L) + (border ? 16L : 0L)) + draw.Count * 4L);
            UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges, border ? 6 : textured ? 5 : 4);
        }
        else
        {
            for (int field = 0; field < (border ? 5 : textured ? 4 : 3); field++)
            {
                int first = 0;
                while (first < slots.Count)
                {
                    while (first < slots.Count && !MaterialSlotFieldChanged(slots[first], gpu, field, border))
                        first++;
                    if (first == slots.Count) break;
                    int end = first + 1;
                    while (end < slots.Count && slots[end].Slot == slots[end - 1].Slot + 1 &&
                        MaterialSlotFieldChanged(slots[end], gpu, field, border))
                        end++;
                    uint offset = slots[first].Slot;
                    uint count = (uint)(end - first);
                    XRBufferWriter<Vector4> writer = field switch
                    {
                        0 => _matQuadTransformBuf!.AllocAt<Vector4>(offset * 4, count * 4),
                        1 => _matQuadColorBuf!.AllocAt<Vector4>(offset, count),
                        2 => _matQuadBoundsBuf!.AllocAt<Vector4>(offset, count),
                        3 => _matQuadUvBuf!.AllocAt<Vector4>(offset, count),
                        _ => _matQuadBorderWidthBuf!.AllocAt<Vector4>(offset, count),
                    };
                    for (int index = first; index < end; index++)
                    {
                        MaterialQuadEntry entry = slots[index].Entry;
                        int local = index - first;
                        switch (field)
                        {
                            case 0:
                                int matrix = local * 4;
                                WriteMatrix4x4(writer.Span, ref matrix, in entry.WorldMatrix);
                                break;
                            case 1: writer.Span[local] = entry.Color; break;
                            case 2: writer.Span[local] = entry.UIXYWH; break;
                            case 3: writer.Span[local] = border ? entry.BorderColor : entry.UV; break;
                            default: writer.Span[local] = new Vector4(entry.BorderWidth, 0, 0, 0); break;
                        }
                    }
                    writer.Commit();
                    UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, count * (field == 0 ? 64L : 16L));
                    UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
                    first = end;
                }
            }
            List<uint> previous = gpu.UploadedDrawIndices;
            int firstDraw = 0;
            while (firstDraw < draw.Count)
            {
                while (firstDraw < draw.Count && firstDraw < previous.Count && draw[firstDraw] == previous[firstDraw])
                    firstDraw++;
                if (firstDraw == draw.Count) break;
                int end = firstDraw + 1;
                while (end < draw.Count && (end >= previous.Count || draw[end] != previous[end])) end++;
                XRBufferWriter<uint> writer = _matQuadDrawIndexBuf!.AllocAt<uint>((uint)firstDraw, (uint)(end - firstDraw));
                for (int index = firstDraw; index < end; index++)
                    writer.Span[index - firstDraw] = draw[index];
                writer.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (end - firstDraw) * 4L);
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
                firstDraw = end;
            }
        }

        foreach ((uint slot, MaterialQuadEntry entry) in slots)
        {
            gpu.UploadedSlots[(int)slot] = entry;
            gpu.InitializedSlots[(int)slot] = true;
        }
        gpu.UploadedDrawIndices.Clear();
        gpu.UploadedDrawIndices.AddRange(draw);
        _matQuadNeedsPush = false;
    }

    private static bool MaterialSlotFieldChanged((uint Slot, MaterialQuadEntry Entry) value,
        MaterialGPUResources gpu, int field, bool border)
        => !gpu.InitializedSlots[(int)value.Slot] ||
           MaterialFieldChanged(value.Entry, gpu.UploadedSlots[(int)value.Slot], field, border);

    private void UploadChangedMaterialRanges(List<MaterialQuadEntry> entries,
        List<MaterialQuadEntry> previous, bool textured, bool border)
    {
        for (int field = 0; field < (border ? 5 : textured ? 4 : 3); field++)
        {
            int first = 0;
            while (first < entries.Count)
            {
                while (first < entries.Count && !MaterialFieldChanged(entries[first], previous[first], field, border))
                    first++;
                if (first == entries.Count)
                    break;
                int end = first + 1;
                while (end < entries.Count && MaterialFieldChanged(entries[end], previous[end], field, border))
                    end++;

                uint offset = (uint)first;
                uint count = (uint)(end - first);
                XRBufferWriter<Vector4> writer = field switch
                {
                    0 => _matQuadTransformBuf!.AllocAt<Vector4>(offset * 4, count * 4),
                    1 => _matQuadColorBuf!.AllocAt<Vector4>(offset, count),
                    2 => _matQuadBoundsBuf!.AllocAt<Vector4>(offset, count),
                    3 => _matQuadUvBuf!.AllocAt<Vector4>(offset, count),
                    _ => _matQuadBorderWidthBuf!.AllocAt<Vector4>(offset, count),
                };
                Span<Vector4> data = writer.Span;
                for (int index = first; index < end; index++)
                {
                    MaterialQuadEntry entry = entries[index];
                    int local = index - first;
                    switch (field)
                    {
                        case 0:
                            int matrixOffset = local * 4;
                            WriteMatrix4x4(data, ref matrixOffset, in entry.WorldMatrix);
                            break;
                        case 1: data[local] = entry.Color; break;
                        case 2: data[local] = entry.UIXYWH; break;
                        case 3: data[local] = border ? entry.BorderColor : entry.UV; break;
                        default: data[local] = new Vector4(entry.BorderWidth, 0, 0, 0); break;
                    }
                }
                writer.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, count * (field == 0 ? 64L : 16L));
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
                first = end;
            }
        }
    }

    private static bool MaterialFieldChanged(in MaterialQuadEntry current, in MaterialQuadEntry previous, int field, bool border)
        => field switch
        {
            0 => current.WorldMatrix != previous.WorldMatrix,
            1 => current.Color != previous.Color,
            2 => current.UIXYWH != previous.UIXYWH,
            3 => border ? current.BorderColor != previous.BorderColor : current.UV != previous.UV,
            _ => current.BorderWidth != previous.BorderWidth,
        };

    #endregion

    #region GPU Resource Management — Text Quads

    private TextGPUResources EnsureTextGPUResources(AbstractRenderer renderer, XRTexture2D fontAtlas, int renderPass, ulong runId)
    {
        var key = (renderer, renderer.BackendGeneration, fontAtlas, renderPass, runId);
        if (!_textGPU.TryGetValue(key, out var cohorts))
            cohorts = [];
        for (int i = 0; i < cohorts.Count; i++)
            if (CanReuseTextGPU(cohorts[i]))
                return cohorts[i];
        if (cohorts.Count >= MaxRunCohorts)
            throw new InvalidOperationException($"UI text run ({renderPass}, {runId}) has no retired GPU cohort.");
        if (CountGpuCohorts() >= MaxCanvasGpuCohorts)
            RetireInactiveGpuCohorts(renderer);
        if (CountGpuCohorts() >= MaxCanvasGpuCohorts)
            throw new InvalidOperationException("UI GPU cohort budget is full while earlier draws remain in use.");

        TextGPUResources gpu = new();

        bool webGpu = AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
        XRShader[] shaders = webGpu ? [] : UIBatchMaterialPrototype.Text.CreateDesktopShaders();

        ShaderVar[] parameters =
        [
            new ShaderInt(0, TextAtlasTypeUniformName),
            new ShaderFloat(0.0f, MsdfDistanceRangeUniformName),
            new ShaderFloat(0.5f, MsdfDistanceRangeMiddleUniformName),
            new ShaderFloat(0.5f, MsdfFillBiasUniformName),
            new ShaderInt(0, TextDebugModeUniformName),
            new ShaderInt(TextRenderLayerCombined, TextRenderLayerUniformName),
            new ShaderInt(TextRenderLayerCombined, TextRenderLayerVertexUniformName),
        ];

        var material = new XRMaterial(parameters, [fontAtlas], shaders)
        {
            Name = "UIBatchTextMaterial",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = UIBatchMaterialPrototype.Text.CreateRenderOptions(webGpu)
        };
        if (webGpu)
            material.EngineSemantic = EngineMaterialSemanticIdentity.UITextBatchedBitmapV2;

        gpu.Mesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material);
        gpu.Mesh.Name = "UIBatchTextRenderer";
        if (gpu.Mesh.Mesh is not null)
            gpu.Mesh.Mesh.Name = "UIBatchTextQuadMesh";
        gpu.Mesh.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
        gpu.Mesh.CaptureUniformsOnRender = true;
        gpu.Mesh.BindingPublishers.Add(new UIRequiredTextureBindings(fontAtlas));
        DisableShaderPipelines(gpu.Mesh);
        gpu.Mesh.EnsureRenderPipelineVersionsCreated();

        gpu.GlyphCapacity = 256;
        gpu.DrawCapacity = 256;
        gpu.TextCapacity = 32;
        CreateTextBuffers(gpu);
        gpu.NeedsPush = true; // Ensure first upload allocates GPU buffers via PushData

        if (!_textGPU.ContainsKey(key))
            _textGPU.Add(key, cohorts);
        cohorts.Add(gpu);
        _gpuCohortCount++;
        return gpu;
    }

    private static bool TextDataMatches(TextGPUResources gpu, TextBatchData batch)
    {
        if (!gpu.HasUploadedData || gpu.UploadedEntries.Count != batch.Entries.Count)
            return false;
        for (int index = 0; index < batch.Entries.Count; index++)
        {
            TextEntry old = gpu.UploadedEntries[index];
            TextEntry current = batch.Entries[index];
            if (old.WorldMatrix != current.WorldMatrix || old.TextColor != current.TextColor ||
                old.OutlineColor != current.OutlineColor || old.OutlineThickness != current.OutlineThickness ||
                old.UIXYWH != current.UIXYWH || !ReferenceEquals(old.Layout, current.Layout) ||
                old.GlyphCount != current.GlyphCount || old.PrimitiveHandle != current.PrimitiveHandle)
                return false;
        }
        return true;
    }

    private static void RememberTextData(TextGPUResources gpu, TextBatchData batch)
    {
        gpu.UploadedEntries.Clear();
        for (int index = 0; index < batch.Entries.Count; index++)
            gpu.UploadedEntries.Add(batch.Entries[index]);
        gpu.HasUploadedData = true;
    }

    private static XRMeshRenderer.BaseVersion GetImmediateRenderVersion(XRMeshRenderer mesh)
    {
        if (RuntimeEngine.Rendering.State.IsStereoPass)
        {
            if (!RuntimeEngine.Rendering.State.IsVulkan
                && RuntimeEngine.Rendering.Settings.PreferNVStereo
                && RuntimeEngine.Rendering.State.IsNVIDIA)
                return mesh.GetNVStereoVersion();

            if (RuntimeEngine.Rendering.State.HasAnyMultiViewExtension)
                return mesh.GetOVRMultiViewVersion();
        }

        return mesh.GetDefaultVersion();
    }

    private static void DisableShaderPipelines(XRMeshRenderer mesh)
        => mesh.SetShaderPipelinesAllowedForAllVersions(false);

    private static void CreateTextBuffers(TextGPUResources gpu)
    {
        var mesh = gpu.Mesh!;

        gpu.GlyphTransformsBuf?.Destroy();
        gpu.GlyphTexCoordsBuf?.Destroy();
        gpu.TextInstanceBuf?.Destroy();
        gpu.GlyphTextIndexBuf?.Destroy();
        gpu.GlyphDrawIndexBuf?.Destroy();

        // Glyph transforms: vec4 per glyph
        gpu.GlyphTransformsBuf = new XRDataBuffer<Vector4>(
            "GlyphTransformsBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.GlyphCapacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 0,
            DisposeOnPush = false
        };
        mesh.Buffers["GlyphTransformsBuffer"] = gpu.GlyphTransformsBuf;

        // Glyph tex coords: vec4 per glyph
        gpu.GlyphTexCoordsBuf = new XRDataBuffer<Vector4>(
            "GlyphTexCoordsBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.GlyphCapacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 1,
            DisposeOnPush = false
        };
        mesh.Buffers["GlyphTexCoordsBuffer"] = gpu.GlyphTexCoordsBuf;

        // Text instance data: matrix rows + fill color + bounds + outline color + outline params.
        gpu.TextInstanceBuf = new XRDataBuffer<Vector4>(
            "TextInstanceBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.TextCapacity * TextInstanceVec4Count)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 2,
            DisposeOnPush = false
        };
        mesh.Buffers["TextInstanceBuffer"] = gpu.TextInstanceBuf;

        // Glyph-to-text index: uint per glyph
        gpu.GlyphTextIndexBuf = new XRDataBuffer<uint>(
            "GlyphTextIndexBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.GlyphCapacity, integral: true)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 3,
            DisposeOnPush = false
        };
        mesh.Buffers["GlyphTextIndexBuffer"] = gpu.GlyphTextIndexBuf;
        if (AbstractRenderer.Current?.BackendId != RendererBackendId.WebGPU)
        {
            gpu.GlyphDrawIndexBuf = new XRDataBuffer<uint>(
                "GlyphDrawIndexBuffer", EBufferTarget.ShaderStorageBuffer,
                gpu.DrawCapacity, integral: true)
            {
                Usage = EBufferUsage.StreamDraw,
                BindingIndexOverride = 4,
                DisposeOnPush = false
            };
            mesh.Buffers["GlyphDrawIndexBuffer"] = gpu.GlyphDrawIndexBuf;
        }
    }

    private static BatchPool<T> GetOrCreatePool<T>(Dictionary<int, BatchPool<T>> pools, int renderPass) where T : class, new()
    {
        if (!pools.TryGetValue(renderPass, out var pool))
        {
            pool = new BatchPool<T>();
            pools[renderPass] = pool;
        }

        return pool;
    }

    private CollectPassState GetOrCreateCollectPassState(int renderPass)
    {
        if (!_collectPassStates.TryGetValue(renderPass, out var state))
        {
            state = new CollectPassState();
            _collectPassStates[renderPass] = state;
        }

        return state;
    }

    private BatchMarkerCommand AcquireMarker(int renderPass, int zIndex, EBatchMarkerKind kind, int groupIndex)
    {
        if (!_markerPools.TryGetValue(renderPass, out var markerPool))
        {
            markerPool = new MarkerPool();
            _markerPools[renderPass] = markerPool;
        }

        BatchMarkerCommand marker = markerPool.Acquire(this, renderPass);
        marker.Configure(kind, groupIndex, zIndex);
        return marker;
    }

    private void RenderBatchMarker(int renderPass, EBatchMarkerKind kind, int groupIndex)
    {
        try
        {
            if (kind == EBatchMarkerKind.Material)
                RenderMaterialGroup(renderPass, groupIndex);
            else
                RenderTextGroup(renderPass, groupIndex);
        }
        catch (Exception error)
        {
            FailCurrentUiPublicationAuthoring(error.Message);
            throw;
        }
    }

    private void RenderMaterialGroup(int renderPass, int groupIndex)
    {
        if (!_renderMaterialGroups.TryGetValue(renderPass, out var groups) ||
            !groups.TryGet(groupIndex, out var batch) ||
            batch.Entries.Count == 0)
        {
            return;
        }

        XRTexture2D? backdropTexture = null;
        BoundingRectangle backdropRegion = default;
        if (batch.Backdrop.IsDefined && !TryGetBackdropCapture(out backdropTexture, out backdropRegion))
        {
            FailCurrentUiPublicationAuthoring("UI.BackdropBlur.CaptureMissing: no checked capture is ready in this output attempt.");
            return;
        }

        AbstractRenderer renderer = AbstractRenderer.Current ??
            throw new InvalidOperationException("UI batching requires an active renderer.");
        RendererBackendId backendId = renderer.BackendId;
        bool isolatedCohort = backendId == RendererBackendId.Vulkan || backendId == RendererBackendId.OpenGL;
        MaterialGPUResources? cohort = isolatedCohort ? AcquireMaterialGPU(renderer, renderPass, batch.RunId) : null;
        if (cohort is not null)
        {
            Claim(cohort);
            LoadMaterialGPU(cohort);
        }
        else
            _legacyMaterialGpuCreated = true;
        bool drawSubmitted = false;
        try
        {
        using var sample = RuntimeEngine.Profiler.Start();
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        bool profileGpu = profiler.ShouldInstrumentCommandScopes;
        int variant = batch.Backdrop.IsDefined ? 3 : batch.Border ? 2 : batch.Texture is not null ? 1 : 0;
        bool uploadRequired = cohort is null || cohort.UploadedVariant != variant ||
            batch.Border && !cohort.HasUploadedBorder ||
            (batch.Texture is not null || batch.Backdrop.IsDefined) && !cohort.HasUploadedImage ||
            !MaterialEntriesMatch(cohort, batch.Entries);

        try
        {
            if (uploadRequired && profileGpu)
            {
                using (profiler.StartScope($"UIBatchMaterialUpload[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batch.Entries.Count}]"))
                {
                    EnsureMaterialQuadMesh();
                    UploadMaterialQuadData(batch.Entries, batch.Texture is not null || batch.Backdrop.IsDefined, batch.Border, cohort);
                }
            }
            else if (uploadRequired)
            {
                EnsureMaterialQuadMesh();
                UploadMaterialQuadData(batch.Entries, batch.Texture is not null || batch.Backdrop.IsDefined, batch.Border, cohort);
            }
        }
        catch
        {
            if (cohort is not null)
            {
                cohort.HasUploadedEntries = false;
                cohort.NeedsPush = true;
            }
            throw;
        }
        if (cohort is not null && uploadRequired)
        {
            RememberMaterialEntries(cohort, batch.Entries);
            cohort.HasUploadedBorder |= batch.Border;
            cohort.HasUploadedImage |= batch.Texture is not null || batch.Backdrop.IsDefined;
            cohort.UploadedVariant = variant;
        }

        XRMeshRenderer materialQuadMesh = batch.Backdrop.IsDefined
            ? EnsureBackdropMesh(backdropTexture!) : batch.Border ? EnsureMaterialBorderMesh() :
            batch.Texture is { } texture
                ? EnsureMaterialTextureMesh(texture, batch.ImageSrgb) : _matQuadMesh!;

        var version = GetImmediateRenderVersion(materialQuadMesh);
        bool prepared;
        if (profileGpu)
        {
            using (profiler.StartScope($"UIBatchMaterialPrepare[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batch.Entries.Count}]"))
            {
                version.Generate();
                prepared = materialQuadMesh.TryPrepareForRendering();
            }
        }
        else
        {
            version.Generate();
            prepared = materialQuadMesh.TryPrepareForRendering();
        }

        if (!prepared)
        {
            FailCurrentUiPublicationAuthoring("The published UI material draw is not prepared for rendering.");
            return;
        }

        if (BatchDiagnosticsEnabled)
            Debug.UIEvery(
                "UIBatchCollector.RenderMaterialQuadBatch",
                TimeSpan.FromSeconds(5),
                "[UIBatch] RenderMaterialQuadBatch: pass={0}, entries={1}, capacity={2}",
                renderPass, batch.Entries.Count, _matQuadCapacity);

        if (BatchDiagnosticsEnabled && s_materialRenderDiagCount++ < 40)
        {
            MaterialQuadEntry firstEntry = batch.Entries[0];
            Vector4 unitQuad = new(0.0f, 0.0f, 1.0f, 1.0f);
            string projectedQuad = GetProjectedGlyphSummary(in firstEntry.WorldMatrix, in unitQuad);
            Debug.Log(
                ELogCategory.UI,
                "[UIBatch] RenderMaterialQuadGroup #{0}: pass={1} group={2} entries={3} color=({4:F2},{5:F2},{6:F2},{7:F2}) worldT=({8:F1},{9:F1},{10:F1}) bounds=({11:F1},{12:F1},{13:F1},{14:F1}) projected={15}",
                s_materialRenderDiagCount,
                renderPass,
                groupIndex,
                batch.Entries.Count,
                firstEntry.Color.X,
                firstEntry.Color.Y,
                firstEntry.Color.Z,
                firstEntry.Color.W,
                firstEntry.WorldMatrix.M41,
                firstEntry.WorldMatrix.M42,
                firstEntry.WorldMatrix.M43,
                firstEntry.UIXYWH.X,
                firstEntry.UIXYWH.Y,
                firstEntry.UIXYWH.Z,
                firstEntry.UIXYWH.W,
                projectedQuad);
        }

        if (materialQuadMesh.Material is { } material && material.RenderPass != renderPass)
            material.RenderPass = renderPass;

        if (batch.Backdrop.IsDefined && materialQuadMesh.Material is { } backdropMaterial)
        {
            backdropMaterial.SetVector2("CaptureOrigin", new Vector2(backdropRegion.X, backdropRegion.Y));
            backdropMaterial.SetVector2("CaptureSize", new Vector2(backdropRegion.Width, backdropRegion.Height));
            backdropMaterial.SetFloat("BlurStrength", batch.Backdrop.BlurStrength);
        }

        UIStaticDrawCandidate cacheCandidate = CaptureStaticDrawCandidate(renderer, materialQuadMesh, batch);
        using UIStaticDrawScope cacheScope = new(in cacheCandidate);
        ulong backdropDrawScope = 0UL;
        ulong backdropOutputScope = 0UL;
        if (batch.Backdrop.IsDefined)
        {
            UIIndexedDrawIntent expected = UIIndexedDrawIntent.Capture(materialQuadMesh,
                EPrimitiveType.Triangles, checked((uint)batch.Entries.Count));
            if (!renderer.TryBeginUiIndexedDrawScope([expected], out backdropDrawScope))
            {
                FailCurrentUiPublicationAuthoring("UI.BackdropBlur.DrawScopeRejected: the panel has no exact native draw scope.");
                return;
            }
        }
        bool backdropManagedDrawSucceeded = false;
        try
        {
            if (batch.Backdrop.IsDefined &&
                !renderer.TryBeginUiBackdropConsumerDrawScope(_activeBackdropWindow!,
                    _activeBackdropRegion, _activeBackdropDispatch, out backdropOutputScope))
                FailCurrentUiPublicationAuthoring(
                    "UI.BackdropBlur.OutputScopeRejected: the panel has no exact native output scope.");
            if (batch.Backdrop.IsDefined && backdropOutputScope == 0UL)
                return;
            ApplyBatchCropping(batch.CropRegion);
            if (profileGpu)
            {
                using (profiler.StartScope($"UIBatchMaterialDraw[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batch.Entries.Count}]"))
                {
                    drawSubmitted = true;
                    materialQuadMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batch.Entries.Count);
                }
            }
            else
            {
                drawSubmitted = true;
                materialQuadMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batch.Entries.Count);
            }
            backdropManagedDrawSucceeded = true;
        }
        finally
        {
            try
            {
                if (backdropDrawScope != 0UL)
                {
                    bool indexedComplete = renderer.CompleteUiIndexedDrawScope(backdropDrawScope);
                    bool outputComplete = backdropOutputScope != 0UL &&
                        renderer.CompleteUiBackdropConsumerDrawScope(backdropOutputScope);
                    if (backdropManagedDrawSucceeded && indexedComplete && outputComplete)
                        AcknowledgeBackdropConsumer(renderPass, batch.RunId);
                    else
                        FailCurrentUiPublicationAuthoring(
                            "UI.BackdropBlur.ConsumerIncomplete: the required native panel draw or output was not acknowledged.");
                }
            }
            finally
            {
                if (backdropDrawScope != 0UL)
                    renderer.AbortUiBackdropConsumerDrawScope();
                DisableBatchCropping();
            }
        }
        }
        finally
        {
            if (cohort is not null)
            {
                StoreMaterialGPU(cohort);
                if (drawSubmitted && isolatedCohort)
                {
                    // Optional buffers are created for later variants but only
                    // acquire native contents when a draw actually binds them.
                    // Remember every variant ever submitted through this cohort.
                    cohort.UsedUv |= batch.Texture is not null || batch.Border;
                    cohort.UsedBorderWidth |= batch.Border;
                    cohort.Submitted = true;
                    XRGpuFence? previous = cohort.Fence;
                    cohort.Fence = null;
                    previous?.Dispose();
                    if (_activeReceipt is null)
                        cohort.Fence = renderer.InsertGpuFence() ??
                            throw new InvalidOperationException("UI batching requires a GPU completion fence.");
                }
            }
        }
    }

    private UIStaticDrawCandidate CaptureStaticDrawCandidate(AbstractRenderer renderer,
        XRMeshRenderer mesh, MaterialQuadBatchData batch)
    {
        if (renderer.BackendId != RendererBackendId.Vulkan || batch.Texture is not null || batch.Border ||
            batch.Backdrop.IsDefined ||
            !batch.CompositionOwner.IsExplicit || batch.CompositionOwner.Kind != EUICompositionLayerKind.StaticChrome ||
            batch.CompositionSegmentIdentity == 0 || batch.CompositionSegmentRevision == 0 ||
            _activeReceipt is not { } receipt || !ReferenceEquals(receipt.Renderer, renderer) ||
            _renderingPublication is not { AuthoringDrawSpace: ECanvasDrawSpace.Screen,
                DrawSpace: ECanvasDrawSpace.Screen, AuthoringOffscreen: false } publication ||
            !ReferenceEquals(publication.RenderStorage, _renderPublishedStorage) ||
            !ReferenceEquals(publication.CompositionStorage, _renderCompositionStorage))
            return default;

        UIIndexedDrawIntent intent = UIIndexedDrawIntent.Capture(mesh,
            EPrimitiveType.Triangles, checked((uint)batch.Entries.Count));
        UIStaticDrawContract contract = new(publication.CanvasLifetime, batch.CompositionOwner,
            batch.CompositionSegmentIdentity, batch.CompositionSegmentRevision, batch.RunId,
            renderer.BackendGeneration, intent) { CapturedMesh = mesh.Mesh };
        return contract.IsDefined ? new(contract, renderer, receipt.Id) : default;
    }

    private void RenderTextGroup(int renderPass, int groupIndex)
    {
        if (!_renderTextGroups.TryGetValue(renderPass, out var groups) ||
            !groups.TryGet(groupIndex, out var batchData) ||
            batchData.Atlas is null ||
            batchData.Entries.Count == 0 ||
            batchData.TotalGlyphs == 0)
        {
            return;
        }

        AbstractRenderer renderer = AbstractRenderer.Current ??
            throw new InvalidOperationException("UI text batching requires an active renderer.");
        if (renderer.BackendId == RendererBackendId.WebGPU &&
            (batchData.AtlasType != TextAtlasBitmap || batchData.DebugMode != 0))
            throw new NotSupportedException("WebGPU.UI.TextProfileUnsupported: screen text requires a bitmap atlas and normal shading.");

        using var sample = RuntimeEngine.Profiler.Start();

        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        bool profileGpu = profiler.ShouldInstrumentCommandScopes;
        TextGPUResources gpu;
        if (profileGpu)
        {
            using (profiler.StartScope($"UIBatchTextEnsureResources[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
                gpu = EnsureTextGPUResources(renderer, batchData.Atlas, renderPass, batchData.RunId);
        }
        else
        {
            gpu = EnsureTextGPUResources(renderer, batchData.Atlas, renderPass, batchData.RunId);
        }
        Claim(gpu);

        XRMeshRenderer textMesh = gpu.Mesh!;
        var material = textMesh.Material;
        if (material is not null && material.RenderPass != renderPass)
            material.RenderPass = renderPass;

        material?.SetInt(TextAtlasTypeUniformName, batchData.AtlasType);
        material?.SetFloat(MsdfDistanceRangeUniformName, batchData.DistanceRange);
        material?.SetFloat(MsdfDistanceRangeMiddleUniformName, batchData.DistanceRangeMiddle);
        material?.SetFloat(MsdfFillBiasUniformName, batchData.MsdfFillBias);
        material?.SetInt(TextDebugModeUniformName, batchData.DebugMode);
        material?.SetInt(TextRenderLayerUniformName, TextRenderLayerCombined);
        material?.SetInt(TextRenderLayerVertexUniformName, TextRenderLayerCombined);

        if (BatchDiagnosticsEnabled && s_textRenderDiagCount++ < 40)
        {
            var firstEntry = batchData.Entries[0];
            UITextLayoutGlyph firstGlyph = firstEntry.GlyphCount > 0
                ? firstEntry.Layout.Glyphs.Span[0]
                : default;
            Vector4 firstTransform = firstGlyph.Transform;
            string projectedGlyph = GetProjectedGlyphSummary(in firstEntry.WorldMatrix, in firstTransform);
            Debug.Log(
                ELogCategory.UI,
                "[FpsTextDiag] UIBatchCollector.RenderTextGroup #{0}: pass={1} group={2} entries={3} totalGlyphs={4} atlasType={5} range={6:F2} middle={7:F2} bias={8:F2} debugMode={9} glyphCap={10} textCap={11} material={12} mesh={13} color=({14:F2},{15:F2},{16:F2},{17:F2}) worldT=({18:F1},{19:F1},{20:F1}) bounds=({21:F1},{22:F1},{23:F1},{24:F1}) firstTfm=({25:F2},{26:F2},{27:F2},{28:F2}) firstUv=({29:F5},{30:F5},{31:F5},{32:F5}) projected={33}",
                s_textRenderDiagCount,
                renderPass,
                groupIndex,
                batchData.Entries.Count,
                batchData.TotalGlyphs,
                batchData.AtlasType,
                batchData.DistanceRange,
                batchData.DistanceRangeMiddle,
                batchData.MsdfFillBias,
                batchData.DebugMode,
                gpu.GlyphCapacity,
                gpu.TextCapacity,
                material is not null,
                gpu.Mesh is not null,
                firstEntry.TextColor.X,
                firstEntry.TextColor.Y,
                firstEntry.TextColor.Z,
                firstEntry.TextColor.W,
                firstEntry.WorldMatrix.M41,
                firstEntry.WorldMatrix.M42,
                firstEntry.WorldMatrix.M43,
                firstEntry.UIXYWH.X,
                firstEntry.UIXYWH.Y,
                firstEntry.UIXYWH.Z,
                firstEntry.UIXYWH.W,
                firstGlyph.Transform.X,
                firstGlyph.Transform.Y,
                firstGlyph.Transform.Z,
                firstGlyph.Transform.W,
                firstGlyph.Uvs.X,
                firstGlyph.Uvs.Y,
                firstGlyph.Uvs.Z,
                firstGlyph.Uvs.W,
                projectedGlyph);
        }

        bool textUploadRequired = !TextDataMatches(gpu, batchData);
        try
        {
            if (textUploadRequired && profileGpu)
            {
                using (profiler.StartScope($"UIBatchTextUpload[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
                    FinalizeAndUploadTextData(batchData, gpu, batchData.Atlas);
            }
            else if (textUploadRequired)
            {
                FinalizeAndUploadTextData(batchData, gpu, batchData.Atlas);
            }
        }
        catch
        {
            gpu.HasUploadedData = false;
            gpu.NeedsPush = true;
            throw;
        }
        if (textUploadRequired)
            RememberTextData(gpu, batchData);

        var version = GetImmediateRenderVersion(gpu.Mesh!);
        if (BatchDiagnosticsEnabled)
            s_textPrepareTotalCalls++;
        bool prepared;
        string prepareReason;
        if (profileGpu)
        {
            using (profiler.StartScope($"UIBatchTextPrepare[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
            {
                version.Generate();
                prepared = textMesh.TryPrepareForRendering(out prepareReason);
            }
        }
        else
        {
            version.Generate();
            prepared = textMesh.TryPrepareForRendering(out prepareReason);
        }

        if (!prepared)
        {
            FailCurrentUiPublicationAuthoring("The published UI text draw is not prepared for rendering: " + prepareReason);
            if (BatchDiagnosticsEnabled)
            {
                s_textPrepareFailureCount++;
                if (!s_textPrepareReasonCounts.TryGetValue(prepareReason, out int reasonCount))
                    reasonCount = 0;
                s_textPrepareReasonCounts[prepareReason] = reasonCount + 1;
            }

            if (BatchDiagnosticsEnabled && s_textPrepareDiagCount++ < 40)
            {
                string prepareDetail = textMesh.GetLastPrepareDetail();
                Debug.Log(
                    ELogCategory.UI,
                    "[FpsTextDiag] UIBatchCollector.RenderTextGroup prepare pending #{0}: pass={1} group={2} totalGlyphs={3} debugMode={4} reason={5} ids[gpu={6:X8} mesh={7:X8} ver={8:X8}] detail={9}",
                    s_textPrepareDiagCount,
                    renderPass,
                    groupIndex,
                    batchData.TotalGlyphs,
                    batchData.DebugMode,
                    prepareReason,
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(gpu),
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(textMesh),
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(version),
                    string.IsNullOrEmpty(prepareDetail) ? "<none>" : prepareDetail);
            }

            if (BatchDiagnosticsEnabled)
                MaybeLogTextPrepareSummary();
            return;
        }

        if (BatchDiagnosticsEnabled)
            MaybeLogTextPrepareSummary();

        bool textDrawSubmitted = false;
        ApplyBatchCropping(batchData.CropRegion);
        try
        {
            if (profileGpu)
            {
                using (profiler.StartScope($"UIBatchTextDraw[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
                {
                    textDrawSubmitted = true;
                    textMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batchData.TotalGlyphs);
                }
            }
            else
            {
                textDrawSubmitted = true;
                textMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batchData.TotalGlyphs);
            }
        }
        finally
        {
            material?.SetInt(TextRenderLayerUniformName, TextRenderLayerCombined);
            material?.SetInt(TextRenderLayerVertexUniformName, TextRenderLayerCombined);
            DisableBatchCropping();
            if (textDrawSubmitted && (renderer.BackendId == RendererBackendId.OpenGL ||
                renderer.BackendId == RendererBackendId.Vulkan))
            {
                gpu.Submitted = true;
                XRGpuFence? previous = gpu.Fence;
                gpu.Fence = null;
                previous?.Dispose();
                if (_activeReceipt is null)
                    gpu.Fence = renderer.InsertGpuFence() ??
                        throw new InvalidOperationException("UI text batching requires a GPU completion fence.");
            }
            else if (textDrawSubmitted)
                gpu.Submitted = true;
        }
    }

    private static void MaybeLogTextPrepareSummary()
    {
        // Emit a periodic summary so failure-vs-success ratios and reason breakdown are
        // observable beyond the 40-line per-process cap on individual prepare-pending logs.
        const long SummaryIntervalSeconds = 5;

        long nowTicks = Stopwatch.GetTimestamp();
        long ticksPerSecond = Stopwatch.Frequency;
        long lastTicks = Interlocked.Read(ref s_textPrepareSummaryLastTicks);
        if (lastTicks != 0 && (nowTicks - lastTicks) < SummaryIntervalSeconds * ticksPerSecond)
            return;

        lock (s_textPrepareSummaryLock)
        {
            // Re-check inside the lock.
            lastTicks = s_textPrepareSummaryLastTicks;
            if (lastTicks != 0 && (nowTicks - lastTicks) < SummaryIntervalSeconds * ticksPerSecond)
                return;
            s_textPrepareSummaryLastTicks = nowTicks;

            long total = Interlocked.Read(ref s_textPrepareTotalCalls);
            long fail = Interlocked.Read(ref s_textPrepareFailureCount);
            long ok = total - fail;

            string reasonsSummary;
            if (s_textPrepareReasonCounts.Count == 0)
            {
                reasonsSummary = "<none>";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (var kvp in s_textPrepareReasonCounts)
                {
                    if (!first)
                        sb.Append(", ");
                    sb.Append(kvp.Key).Append('=').Append(kvp.Value);
                    first = false;
                }
                reasonsSummary = sb.ToString();
            }

            Debug.Log(
                ELogCategory.UI,
                "[FpsTextDiag] UIBatchCollector text prepare summary: total={0} ok={1} fail={2} reasons=[{3}]",
                total,
                ok,
                fail,
                reasonsSummary);
        }
    }

    private void FinalizeAndUploadTextData(TextBatchData batchData, TextGPUResources gpu, XRTexture2D atlas)
    {
        if (AbstractRenderer.Current?.BackendId != RendererBackendId.WebGPU)
        {
            UploadRetainedTextData(batchData, gpu);
            return;
        }
        uint totalGlyphs = (uint)batchData.TotalGlyphs;
        uint textCount = (uint)batchData.Entries.Count;
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU &&
            (totalGlyphs > 65536 || textCount > 65536))
            throw new NotSupportedException("WebGPU.UI.TextCapacityExceeded: one bitmap text group admits at most 65536 glyphs and text entries.");

        // Resize if needed
        bool resized = false;
        if (totalGlyphs > gpu.GlyphCapacity)
        {
            gpu.GlyphCapacity = NextPowerOf2(totalGlyphs);
            gpu.GlyphTransformsBuf!.Resize(gpu.GlyphCapacity);
            gpu.GlyphTexCoordsBuf!.Resize(gpu.GlyphCapacity);
            gpu.GlyphTextIndexBuf!.Resize(gpu.GlyphCapacity);
            resized = true;
        }
        if (textCount > gpu.TextCapacity)
        {
            gpu.TextCapacity = NextPowerOf2(textCount);
            gpu.TextInstanceBuf!.Resize(gpu.TextCapacity * TextInstanceVec4Count);
            resized = true;
        }
        if (resized)
            gpu.NeedsPush = true;

        if (!gpu.NeedsPush && gpu.HasUploadedData &&
            gpu.UploadedEntries.Count == batchData.Entries.Count &&
            HaveSameGlyphCounts(gpu.UploadedEntries, batchData.Entries))
        {
            UploadChangedTextRanges(batchData.Entries, gpu);
            return;
        }

        bool fullUpload = gpu.NeedsPush;
        uint glyphElementCount = fullUpload ? gpu.GlyphCapacity : totalGlyphs;
        uint textElementCount = fullUpload
            ? gpu.TextCapacity * TextInstanceVec4Count
            : textCount * TextInstanceVec4Count;
        XRBufferWriter<Vector4> glyphTransformWriter = gpu.GlyphTransformsBuf!.Alloc<Vector4>(glyphElementCount);
        XRBufferWriter<Vector4> glyphUvWriter = gpu.GlyphTexCoordsBuf!.Alloc<Vector4>(glyphElementCount);
        XRBufferWriter<Vector4> textInstanceWriter = gpu.TextInstanceBuf!.Alloc<Vector4>(textElementCount);
        XRBufferWriter<uint> glyphIndexWriter = gpu.GlyphTextIndexBuf!.Alloc<uint>(glyphElementCount);
        Span<Vector4> glyphTransforms = glyphTransformWriter.Span;
        Span<Vector4> glyphUvs = glyphUvWriter.Span;
        Span<Vector4> textInstances = textInstanceWriter.Span;
        Span<uint> glyphIndices = glyphIndexWriter.Span;
        int glyphIndex = 0;
        int textInstanceIndex = 0;

        for (int t = 0; t < batchData.Entries.Count; t++)
        {
            ref readonly var entry = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batchData.Entries)[t];

            // Write text instance data: matrix rows + color/bounds/outline metadata.
            WriteMatrix4x4(textInstances, ref textInstanceIndex, in entry.WorldMatrix);
            textInstances[textInstanceIndex++] = entry.TextColor;
            textInstances[textInstanceIndex++] = entry.UIXYWH;
            textInstances[textInstanceIndex++] = entry.OutlineColor;
            textInstances[textInstanceIndex++] = new Vector4(entry.OutlineThickness, 0.0f, 0.0f, 0.0f);

            // Write glyph data
            for (int g = 0; g < entry.GlyphCount; g++)
            {
                UITextLayoutGlyph glyph = entry.Layout.Glyphs.Span[g];
                glyphTransforms[glyphIndex] = glyph.Transform;
                glyphUvs[glyphIndex] = glyph.Uvs;
                glyphIndices[glyphIndex] = (uint)t;
                glyphIndex++;
            }
        }

        if (BatchDiagnosticsEnabled && s_textUploadDiagCount++ < 40)
        {
            Debug.Log(
                ELogCategory.UI,
                "[FpsTextDiag] UIBatchCollector.FinalizeAndUploadTextData #{0}: entries={1} totalGlyphs={2} resized={3} needsPush={4} glyphCap={5} textCap={6} atlas=({7}x{8})",
                s_textUploadDiagCount,
                batchData.Entries.Count,
                batchData.TotalGlyphs,
                resized,
                gpu.NeedsPush,
                gpu.GlyphCapacity,
                gpu.TextCapacity,
                atlas.Width,
                atlas.Height);
        }

        glyphTransformWriter.Commit();
        glyphUvWriter.Commit();
        textInstanceWriter.Commit();
        glyphIndexWriter.Commit();
        UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted,
            (long)glyphElementCount * 36L + (long)textElementCount * 16L);
        UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges, 4);
        gpu.NeedsPush = false;
    }

    private static void UploadRetainedTextData(TextBatchData batch, TextGPUResources gpu)
    {
        List<RetainedGlyphRanges.Allocation?> allocations = gpu.ActiveGlyphRanges;
        List<uint> textSlots = gpu.ActiveTextSlots;
        List<(uint Slot, TextEntry Entry)> sortedTextSlots = gpu.SortedTextSlots;
        List<uint> drawIndices = gpu.CurrentDrawIndices;
        allocations.Clear();
        textSlots.Clear();
        sortedTextSlots.Clear();
        drawIndices.Clear();
        gpu.GlyphRanges.BeginFrame();
        gpu.TextSlots.BeginFrame();
        for (int textIndex = 0; textIndex < batch.Entries.Count; textIndex++)
        {
            TextEntry entry = batch.Entries[textIndex];
            uint textSlot = gpu.TextSlots.Reserve(entry.PrimitiveHandle, textIndex).Index;
            textSlots.Add(textSlot);
            sortedTextSlots.Add((textSlot, entry));
            RetainedGlyphRanges.Allocation? allocation = entry.GlyphCount == 0 ? null :
                gpu.GlyphRanges.Reserve(entry.PrimitiveHandle, textIndex, entry.GlyphCount);
            allocations.Add(allocation);
            if (allocation is null)
                continue;
            for (int glyph = 0; glyph < entry.GlyphCount; glyph++)
                drawIndices.Add(checked(allocation.Offset + (uint)glyph));
        }
        gpu.GlyphRanges.EndFrame();
        gpu.TextSlots.EndFrame();
        sortedTextSlots.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));

        uint storageCount = gpu.GlyphRanges.HighWater;
        uint drawCount = (uint)drawIndices.Count;
        uint textCount = gpu.TextSlots.HighWater;
        if (storageCount > MaxRetainedGlyphSlotsPerRun || drawCount > MaxRetainedGlyphSlotsPerRun)
            throw new InvalidOperationException("A UI text run exceeds its retained GPU glyph budget.");
        if (textCount > MaxRetainedQuadSlotsPerRun)
            throw new InvalidOperationException("A UI text run exceeds its retained GPU instance budget.");
        bool full = gpu.NeedsPush;
        if (storageCount > gpu.GlyphCapacity)
        {
            gpu.GlyphCapacity = NextPowerOf2(storageCount);
            gpu.GlyphTransformsBuf!.Resize(gpu.GlyphCapacity);
            gpu.GlyphTexCoordsBuf!.Resize(gpu.GlyphCapacity);
            gpu.GlyphTextIndexBuf!.Resize(gpu.GlyphCapacity);
            full = true;
        }
        if (drawCount > gpu.DrawCapacity)
        {
            gpu.DrawCapacity = NextPowerOf2(drawCount);
            gpu.GlyphDrawIndexBuf!.Resize(gpu.DrawCapacity);
            full = true;
        }
        if (textCount > gpu.TextCapacity)
        {
            gpu.TextCapacity = NextPowerOf2(textCount);
            gpu.TextInstanceBuf!.Resize(gpu.TextCapacity * TextInstanceVec4Count);
            full = true;
        }
        while (gpu.UploadedTextSlots.Count < textCount)
        {
            gpu.UploadedTextSlots.Add(default);
            gpu.InitializedTextSlots.Add(false);
        }

        if (full)
        {
            for (int index = 0; index < gpu.InitializedTextSlots.Count; index++)
                gpu.InitializedTextSlots[index] = false;
            XRBufferWriter<Vector4> transforms = gpu.GlyphTransformsBuf!.Alloc<Vector4>(storageCount);
            XRBufferWriter<Vector4> uvs = gpu.GlyphTexCoordsBuf!.Alloc<Vector4>(storageCount);
            XRBufferWriter<uint> textIndices = gpu.GlyphTextIndexBuf!.Alloc<uint>(storageCount);
            XRBufferWriter<uint> draw = gpu.GlyphDrawIndexBuf!.Alloc<uint>(drawCount);
            XRBufferWriter<Vector4> instances = gpu.TextInstanceBuf!.Alloc<Vector4>(textCount * TextInstanceVec4Count);
            transforms.Span.Clear();
            uvs.Span.Clear();
            textIndices.Span.Clear();
            draw.Span.Clear();
            instances.Span.Clear();
            for (int textIndex = 0; textIndex < batch.Entries.Count; textIndex++)
            {
                TextEntry entry = batch.Entries[textIndex];
                RetainedGlyphRanges.Allocation? allocation = allocations[textIndex];
                if (allocation is null) continue;
                for (int glyph = 0; glyph < entry.GlyphCount; glyph++)
                {
                    uint slot = allocation.Offset + (uint)glyph;
                    UITextLayoutGlyph value = entry.Layout.Glyphs.Span[glyph];
                    transforms.Span[(int)slot] = value.Transform;
                    uvs.Span[(int)slot] = value.Uvs;
                    textIndices.Span[(int)slot] = textSlots[textIndex];
                }
            }
            for (int index = 0; index < drawIndices.Count; index++)
                draw.Span[index] = drawIndices[index];
            foreach ((uint slot, TextEntry entry) in sortedTextSlots)
                WriteRetainedTextInstance(entry,
                    instances.Span.Slice(checked((int)slot * (int)TextInstanceVec4Count), (int)TextInstanceVec4Count));
            transforms.Commit();
            uvs.Commit();
            textIndices.Commit();
            draw.Commit();
            instances.Commit();
            UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted,
                (long)storageCount * 36L + (long)drawCount * 4L +
                (long)textCount * TextInstanceVec4Count * 16L);
            UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges, 5);
        }
        else
        {
            UploadChangedGlyphRanges(batch, gpu, geometry: true);
            UploadChangedGlyphRanges(batch, gpu, geometry: false);
            int firstText = 0;
            while (firstText < sortedTextSlots.Count)
            {
                while (firstText < sortedTextSlots.Count &&
                    gpu.InitializedTextSlots[(int)sortedTextSlots[firstText].Slot] &&
                    !TextInstanceChanged(sortedTextSlots[firstText].Entry,
                        gpu.UploadedTextSlots[(int)sortedTextSlots[firstText].Slot])) firstText++;
                if (firstText == sortedTextSlots.Count) break;
                int endText = firstText + 1;
                while (endText < sortedTextSlots.Count &&
                    sortedTextSlots[endText].Slot == sortedTextSlots[endText - 1].Slot + 1 &&
                    (!gpu.InitializedTextSlots[(int)sortedTextSlots[endText].Slot] ||
                     TextInstanceChanged(sortedTextSlots[endText].Entry,
                         gpu.UploadedTextSlots[(int)sortedTextSlots[endText].Slot]))) endText++;
                uint offset = sortedTextSlots[firstText].Slot * TextInstanceVec4Count;
                uint count = (uint)(endText - firstText) * TextInstanceVec4Count;
                XRBufferWriter<Vector4> instances = gpu.TextInstanceBuf!.AllocAt<Vector4>(offset, count);
                for (int index = firstText; index < endText; index++)
                    WriteRetainedTextInstance(sortedTextSlots[index].Entry,
                        instances.Span.Slice((index - firstText) * (int)TextInstanceVec4Count, (int)TextInstanceVec4Count));
                instances.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (long)count * 16L);
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
                firstText = endText;
            }

            List<uint> previous = gpu.UploadedDrawIndices;
            int first = 0;
            while (first < drawIndices.Count)
            {
                while (first < drawIndices.Count && first < previous.Count &&
                    drawIndices[first] == previous[first]) first++;
                if (first == drawIndices.Count) break;
                int end = first + 1;
                while (end < drawIndices.Count &&
                    (end >= previous.Count || drawIndices[end] != previous[end])) end++;
                XRBufferWriter<uint> draw = gpu.GlyphDrawIndexBuf!.AllocAt<uint>((uint)first, (uint)(end - first));
                for (int index = first; index < end; index++)
                    draw.Span[index - first] = drawIndices[index];
                draw.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (end - first) * 4L);
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
                first = end;
            }
        }
        for (int textIndex = 0; textIndex < batch.Entries.Count; textIndex++)
        {
            RetainedGlyphRanges.Allocation? allocation = allocations[textIndex];
            if (allocation is null) continue;
            allocation.Layout = batch.Entries[textIndex].Layout;
            allocation.TextIndex = checked((int)textSlots[textIndex]);
        }
        foreach ((uint slot, TextEntry entry) in sortedTextSlots)
        {
            gpu.UploadedTextSlots[(int)slot] = entry;
            gpu.InitializedTextSlots[(int)slot] = true;
        }
        gpu.UploadedDrawIndices.Clear();
        gpu.UploadedDrawIndices.AddRange(drawIndices);
        gpu.NeedsPush = false;
    }

    private static void UploadChangedGlyphRanges(TextBatchData batch, TextGPUResources gpu, bool geometry)
    {
        List<(uint Offset, uint Count, int EntryIndex)> dirty = gpu.DirtyGlyphRanges;
        dirty.Clear();
        for (int index = 0; index < batch.Entries.Count; index++)
        {
            RetainedGlyphRanges.Allocation? allocation = gpu.ActiveGlyphRanges[index];
            if (allocation is null) continue;
            bool changed = geometry
                ? !ReferenceEquals(allocation.Layout, batch.Entries[index].Layout)
                : allocation.TextIndex != checked((int)gpu.ActiveTextSlots[index]);
            if (changed)
                dirty.Add((allocation.Offset, (uint)batch.Entries[index].GlyphCount, index));
        }
        dirty.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        for (int first = 0; first < dirty.Count;)
        {
            int end = first + 1;
            uint last = checked(dirty[first].Offset + dirty[first].Count);
            while (end < dirty.Count && dirty[end].Offset == last)
                last = checked(last + dirty[end++].Count);
            uint count = last - dirty[first].Offset;
            if (geometry)
            {
                XRBufferWriter<Vector4> transforms = gpu.GlyphTransformsBuf!.AllocAt<Vector4>(dirty[first].Offset, count);
                XRBufferWriter<Vector4> uvs = gpu.GlyphTexCoordsBuf!.AllocAt<Vector4>(dirty[first].Offset, count);
                for (int index = first; index < end; index++)
                {
                    UITextLayoutResult layout = batch.Entries[dirty[index].EntryIndex].Layout;
                    int baseIndex = checked((int)(dirty[index].Offset - dirty[first].Offset));
                    for (int glyph = 0; glyph < dirty[index].Count; glyph++)
                    {
                        UITextLayoutGlyph value = layout.Glyphs.Span[glyph];
                        transforms.Span[baseIndex + glyph] = value.Transform;
                        uvs.Span[baseIndex + glyph] = value.Uvs;
                    }
                }
                transforms.Commit();
                uvs.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (long)count * 32L);
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges, 2);
            }
            else
            {
                XRBufferWriter<uint> indices = gpu.GlyphTextIndexBuf!.AllocAt<uint>(dirty[first].Offset, count);
                for (int index = first; index < end; index++)
                {
                    int baseIndex = checked((int)(dirty[index].Offset - dirty[first].Offset));
                    indices.Span.Slice(baseIndex, checked((int)dirty[index].Count))
                        .Fill(gpu.ActiveTextSlots[dirty[index].EntryIndex]);
                }
                indices.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (long)count * 4L);
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
            }
            first = end;
        }
    }

    private static void WriteRetainedTextInstances(List<TextEntry> entries, int first, int end,
        Span<Vector4> destination)
    {
        int index = 0;
        for (int textIndex = first; textIndex < end; textIndex++)
        {
            TextEntry entry = entries[textIndex];
            WriteMatrix4x4(destination, ref index, in entry.WorldMatrix);
            destination[index++] = entry.TextColor;
            destination[index++] = entry.UIXYWH;
            destination[index++] = entry.OutlineColor;
            destination[index++] = new Vector4(entry.OutlineThickness, 0, 0, 0);
        }
    }

    private static void WriteRetainedTextInstance(TextEntry entry, Span<Vector4> destination)
    {
        int index = 0;
        WriteMatrix4x4(destination, ref index, in entry.WorldMatrix);
        destination[index++] = entry.TextColor;
        destination[index++] = entry.UIXYWH;
        destination[index++] = entry.OutlineColor;
        destination[index] = new Vector4(entry.OutlineThickness, 0, 0, 0);
    }

    private static bool HaveSameGlyphCounts(List<TextEntry> previous, List<TextEntry> current)
    {
        for (int index = 0; index < current.Count; index++)
            if (previous[index].GlyphCount != current[index].GlyphCount)
                return false;
        return true;
    }

    private static bool TextInstanceChanged(in TextEntry current, in TextEntry previous)
        => current.WorldMatrix != previous.WorldMatrix || current.TextColor != previous.TextColor ||
           current.UIXYWH != previous.UIXYWH || current.OutlineColor != previous.OutlineColor ||
           current.OutlineThickness != previous.OutlineThickness;

    private static void UploadChangedTextRanges(List<TextEntry> entries, TextGPUResources gpu)
    {
        List<TextEntry> previous = gpu.UploadedEntries;
        int first = 0;
        while (first < entries.Count)
        {
            while (first < entries.Count && !TextInstanceChanged(entries[first], previous[first]))
                first++;
            if (first == entries.Count) break;
            int end = first + 1;
            while (end < entries.Count && TextInstanceChanged(entries[end], previous[end])) end++;
            uint elementOffset = (uint)first * TextInstanceVec4Count;
            uint elementCount = (uint)(end - first) * TextInstanceVec4Count;
            XRBufferWriter<Vector4> writer = gpu.TextInstanceBuf!.AllocAt<Vector4>(elementOffset, elementCount);
            Span<Vector4> destination = writer.Span;
            int writeIndex = 0;
            for (int index = first; index < end; index++)
            {
                TextEntry entry = entries[index];
                WriteMatrix4x4(destination, ref writeIndex, in entry.WorldMatrix);
                destination[writeIndex++] = entry.TextColor;
                destination[writeIndex++] = entry.UIXYWH;
                destination[writeIndex++] = entry.OutlineColor;
                destination[writeIndex++] = new Vector4(entry.OutlineThickness, 0, 0, 0);
            }
            writer.Commit();
            UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (long)elementCount * 16);
            UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges);
            first = end;
        }

        int glyphOffset = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            TextEntry entry = entries[index];
            if (!ReferenceEquals(entry.Layout, previous[index].Layout) && entry.GlyphCount != 0)
            {
                uint offset = (uint)glyphOffset;
                uint count = (uint)entry.GlyphCount;
                XRBufferWriter<Vector4> transforms = gpu.GlyphTransformsBuf!.AllocAt<Vector4>(offset, count);
                XRBufferWriter<Vector4> uvs = gpu.GlyphTexCoordsBuf!.AllocAt<Vector4>(offset, count);
                Span<Vector4> transformSpan = transforms.Span;
                Span<Vector4> uvSpan = uvs.Span;
                for (int glyph = 0; glyph < entry.GlyphCount; glyph++)
                {
                    UITextLayoutGlyph value = entry.Layout.Glyphs.Span[glyph];
                    transformSpan[glyph] = value.Transform;
                    uvSpan[glyph] = value.Uvs;
                }
                transforms.Commit();
                uvs.Commit();
                UIRuntimeCounters.Add(UIWorkCounter.BufferBytesCommitted, (long)count * 32);
                UIRuntimeCounters.Add(UIWorkCounter.BufferCommitRanges, 2);
            }
            glyphOffset += entry.GlyphCount;
        }
    }

    #endregion

    #region Helpers

    private static void DisableBatchCropping()
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null) return;
        BoundingRectangle prior = RuntimeEngine.Rendering.State.RenderingPipelineState?.CurrentCropRegion ?? BoundingRectangle.Empty;
        if (prior.Width > 0 && prior.Height > 0)
        {
            renderer.SetCroppingEnabled(true);
            renderer.CropRenderArea(prior);
        }
        else
            renderer.SetCroppingEnabled(false);
    }

    private void ApplyBatchCropping(BoundingRectangle? cropRegion)
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null) return;
        BoundingRectangle prior = RuntimeEngine.Rendering.State.RenderingPipelineState?.CurrentCropRegion ?? BoundingRectangle.Empty;
        if (cropRegion is not { } region)
        {
            renderer.SetCroppingEnabled(prior.Width > 0 && prior.Height > 0);
            if (prior.Width > 0 && prior.Height > 0) renderer.CropRenderArea(prior);
            return;
        }
        if (_hasRasterRegion && _renderingPublication is { } publication)
            region = MapCanvasCropToRaster(region, publication.CanvasBounds, _rasterRegion);
        if (prior.Width > 0 && prior.Height > 0)
        {
            int left = Math.Max(region.X, prior.X);
            int bottom = Math.Max(region.Y, prior.Y);
            int right = Math.Min(region.X + region.Width, prior.X + prior.Width);
            int top = Math.Min(region.Y + region.Height, prior.Y + prior.Height);
            region = new BoundingRectangle(left, bottom, Math.Max(0, right - left), Math.Max(0, top - bottom));
        }
        renderer.SetCroppingEnabled(true);
        renderer.CropRenderArea(region);
    }

    internal static BoundingRectangle MapCanvasCropToRaster(BoundingRectangle crop,
        BoundingRectangleF canvas, BoundingRectangle raster)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || raster.Width <= 0 || raster.Height <= 0)
            return BoundingRectangle.Empty;
        float scaleX = (float)raster.Width / canvas.Width;
        float scaleY = (float)raster.Height / canvas.Height;
        int left = Math.Clamp((int)MathF.Floor(raster.X + (crop.X - canvas.X) * scaleX),
            raster.X, raster.X + raster.Width);
        int bottom = Math.Clamp((int)MathF.Floor(raster.Y + (crop.Y - canvas.Y) * scaleY),
            raster.Y, raster.Y + raster.Height);
        int right = Math.Clamp((int)MathF.Ceiling(raster.X + (crop.X + crop.Width - canvas.X) * scaleX),
            raster.X, raster.X + raster.Width);
        int top = Math.Clamp((int)MathF.Ceiling(raster.Y + (crop.Y + crop.Height - canvas.Y) * scaleY),
            raster.Y, raster.Y + raster.Height);
        return new BoundingRectangle(left, bottom, Math.Max(0, right - left), Math.Max(0, top - bottom));
    }

    /// <summary>The shared screen batch has one authored straight-alpha raster profile.</summary>
    internal static bool HasWebGpuRasterProfile(RenderingParameters parameters)
    {
        BlendMode? blend = parameters.BlendModeAllDrawBuffers;
        return parameters.CullMode == ECullMode.None &&
            parameters.DepthTest.IsDisabled && parameters.StencilTest.IsDisabled &&
            parameters.AlphaToCoverage != ERenderParamUsage.Enabled &&
            parameters.BlendModesPerDrawBuffer is not { Count: > 0 } &&
            parameters.WriteRed && parameters.WriteGreen && parameters.WriteBlue && parameters.WriteAlpha &&
            blend is { IsEnabled: true, RgbSrcFactor: EBlendingFactor.SrcAlpha,
                RgbDstFactor: EBlendingFactor.OneMinusSrcAlpha,
                AlphaSrcFactor: EBlendingFactor.SrcAlpha,
                AlphaDstFactor: EBlendingFactor.OneMinusSrcAlpha,
                RgbEquation: EBlendEquationMode.FuncAdd,
                AlphaEquation: EBlendEquationMode.FuncAdd };
    }

    private static string GetRenderPassDisplayName(int renderPass)
        => Enum.IsDefined(typeof(EDefaultRenderPass), renderPass)
            ? ((EDefaultRenderPass)renderPass).ToString()
            : renderPass.ToString();

    private static string GetProjectedGlyphSummary(in Matrix4x4 worldMatrix, in Vector4 glyphTransform)
    {
        var camera = RuntimeEngine.Rendering.State.RenderingCamera;
        if (camera is null)
            return "camera=<null>";

        Matrix4x4 worldViewProjection = worldMatrix * camera.ViewProjectionMatrix;
        Vector4 p0 = ProjectGlyphCorner(new Vector2(glyphTransform.X, glyphTransform.Y), in worldViewProjection);
        Vector4 p1 = ProjectGlyphCorner(new Vector2(glyphTransform.X + glyphTransform.Z, glyphTransform.Y), in worldViewProjection);
        Vector4 p2 = ProjectGlyphCorner(new Vector2(glyphTransform.X, glyphTransform.Y + glyphTransform.W), in worldViewProjection);
        Vector4 p3 = ProjectGlyphCorner(new Vector2(glyphTransform.X + glyphTransform.Z, glyphTransform.Y + glyphTransform.W), in worldViewProjection);

        float minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        return $"camera='{camera.Transform.SceneNode?.Name ?? camera.GetType().Name}' ndc=({minX:F3},{minY:F3})-({maxX:F3},{maxY:F3}) w=({p0.W:F3},{p1.W:F3},{p2.W:F3},{p3.W:F3})";
    }

    private static Vector4 ProjectGlyphCorner(Vector2 local, in Matrix4x4 worldViewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(local, 0.0f, 1.0f), worldViewProjection);
        if (MathF.Abs(clip.W) <= 1e-6f)
            return clip;

        float invW = 1.0f / clip.W;
        return new Vector4(clip.X * invW, clip.Y * invW, clip.Z * invW, clip.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TextBatchMatches(
        CollectPassState state,
        int zIndex,
        XRTexture2D fontAtlas,
        int atlasType,
        float distanceRange,
        float distanceRangeMiddle,
        float msdfFillBias,
        int debugMode,
        BoundingRectangle? cropRegion)
        => state.ActiveKind == EBatchMarkerKind.Text &&
           state.ActiveZIndex == zIndex &&
           state.ActiveCropRegion == cropRegion &&
           ReferenceEquals(state.ActiveTextAtlas, fontAtlas) &&
           state.ActiveTextAtlasType == atlasType &&
           state.ActiveTextDistanceRange.Equals(distanceRange) &&
           state.ActiveTextDistanceRangeMiddle.Equals(distanceRangeMiddle) &&
           state.ActiveTextMsdfFillBias.Equals(msdfFillBias) &&
           state.ActiveTextDebugMode == debugMode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteMatrix4x4(Span<Vector4> destination, ref int index, in Matrix4x4 m)
    {
        destination[index++] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        destination[index++] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        destination[index++] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        destination[index++] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    private static uint NextPowerOf2(uint v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BindRenderingPublication(null);
        lock (CollectorRegistryGate)
        {
            for (int index = CollectorRegistry.Count - 1; index >= 0; index--)
                if (!CollectorRegistry[index].TryGetTarget(out UIBatchCollector? collector) ||
                    ReferenceEquals(collector, this))
                    CollectorRegistry.RemoveAt(index);
        }
        Clear();
        DisposeBackdropSurfaces();
        foreach (var pair in _materialRunGPU)
            foreach (MaterialGPUResources cohort in pair.Value)
            {
                if (cohort.Receipt is { } lease && !lease.IsRetired)
                {
                    lock (CollectorRegistryGate)
                        DeferredCohortRetirement.Add((pair.Key.Renderer, pair.Key.BackendGeneration, cohort, null));
                    continue;
                }
                if (cohort.Receipt is not null)
                {
                    ForceReleaseReceipt(cohort.Receipt);
                    cohort.Receipt = null;
                }
                DestroyMaterialGPU(cohort);
            }
        _materialRunGPU.Clear();
        if (_legacyMaterialGpuCreated)
            DestroyLegacyMaterialGPU();
        foreach (var pair in _textGPU)
            foreach (TextGPUResources gpu in pair.Value)
            {
                if (gpu.Receipt is { } lease && !lease.IsRetired)
                {
                    lock (CollectorRegistryGate)
                        DeferredCohortRetirement.Add((pair.Key.Renderer, pair.Key.BackendGeneration, null, gpu));
                    continue;
                }
                if (gpu.Receipt is not null)
                {
                    ForceReleaseReceipt(gpu.Receipt);
                    gpu.Receipt = null;
                }
                gpu.Dispose();
            }
        _textGPU.Clear();
        _gpuCohortCount = 0;
    }

    private static void DestroyMaterialGPU(MaterialGPUResources gpu)
    {
        gpu.Fence?.Dispose();
        gpu.Fence = null;
        XRMeshRenderer? meshRenderer = gpu.Mesh;
        XRMesh? mesh = meshRenderer?.Mesh;
        XRMaterial? material = meshRenderer?.Material;
        gpu.Transform?.Destroy();
        gpu.Color?.Destroy();
        gpu.Bounds?.Destroy();
        gpu.Uv?.Destroy();
        gpu.BorderWidth?.Destroy();
        gpu.DrawIndex?.Destroy();
        XRMaterial? borderMaterial = gpu.BorderMesh?.Material;
        gpu.BorderMesh?.Destroy();
        borderMaterial?.Destroy();
        XRMaterial? backdropMaterial = gpu.BackdropMesh?.Material;
        gpu.BackdropMesh?.Destroy();
        backdropMaterial?.Destroy();
        foreach (XRMeshRenderer texturedMesh in gpu.TextureMeshes.Values)
        {
            XRMaterial? texturedMaterial = texturedMesh.Material;
            texturedMesh.Destroy();
            texturedMaterial?.Destroy();
        }
        gpu.TextureMeshes.Clear();
        meshRenderer?.Destroy();
        mesh?.Destroy();
        material?.Destroy();
    }

    private void DestroyLegacyMaterialGPU()
    {
        XRMeshRenderer? meshRenderer = _matQuadMesh;
        XRMesh? mesh = meshRenderer?.Mesh;
        XRMaterial? material = meshRenderer?.Material;
        _matQuadTransformBuf?.Destroy();
        _matQuadColorBuf?.Destroy();
        _matQuadBoundsBuf?.Destroy();
        _matQuadUvBuf?.Destroy();
        _matQuadBorderWidthBuf?.Destroy();
        _matQuadDrawIndexBuf?.Destroy();
        XRMaterial? borderMaterial = _matQuadBorderMesh?.Material;
        _matQuadBorderMesh?.Destroy();
        borderMaterial?.Destroy();
        XRMaterial? backdropMaterial = _matQuadBackdropMesh?.Material;
        _matQuadBackdropMesh?.Destroy();
        backdropMaterial?.Destroy();
        foreach (XRMeshRenderer texturedMesh in _materialTextureMeshes.Values)
        {
            XRMaterial? texturedMaterial = texturedMesh.Material;
            texturedMesh.Destroy();
            texturedMaterial?.Destroy();
        }
        _materialTextureMeshes.Clear();
        _staleMaterialTextures.Clear();
        meshRenderer?.Destroy();
        mesh?.Destroy();
        material?.Destroy();
        _matQuadTransformBuf = null;
        _matQuadColorBuf = null;
        _matQuadBoundsBuf = null;
        _matQuadUvBuf = null;
        _matQuadBorderWidthBuf = null;
        _matQuadDrawIndexBuf = null;
        _matQuadBorderMesh = null;
        _matQuadBackdropMesh = null;
        _matQuadBackdropTexture = null;
        _matQuadMesh = null;
    }

    #endregion
}
