using System;
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
/// Collects UI render data during the collect-visible thread and dispatches
/// batched instanced draws during the render thread.
/// <para>
/// Material quads (from <see cref="UIMaterialComponent"/>) are collected into a single instanced draw per render pass.
/// Text quads (from <see cref="UITextComponent"/>) are collected into a single instanced draw per render pass per font atlas.
/// </para>
/// <para>
/// This drastically reduces draw call overhead for complex UIs by replacing N individual
/// draw calls with just 1-2 instanced dispatches.
/// </para>
/// </summary>
public sealed class UIBatchCollector : IDisposable
{
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
    private const uint MaterialStyleVec4Count = 7;
    private const string UiBackdropBlurAvailableUniformName = "UiBackdropBlurAvailable";
    private const string UiViewportSizeUniformName = "UiViewportSize";
    private static int s_textAddDiagCount;
    private static int s_textMarkerDiagCount;
    private static int s_textRenderDiagCount;
    private static int s_textUploadDiagCount;
    private static int s_textPrepareDiagCount;
    private static int s_materialRenderDiagCount;
    private static long s_textPrepareTotalCalls;
    private static long s_textPrepareFailureCount;
    private static long s_textGroupCalls;
    private static long s_textEmptyGroupCalls;
    private static ulong s_textLastAttemptFrame;
    private static ulong s_textLastPreparedFrame;
    private static int s_textLastGlyphCount;
    private static string s_textLastPrepareReason = string.Empty;
    private static long s_textPrepareSummaryLastTicks;
    private static readonly Dictionary<string, int> s_textPrepareReasonCounts = new();
    private static readonly object s_textPrepareSummaryLock = new();

    /// <summary>Reads text preparation counters without a GPU readback.</summary>
    public static UITextPreparationDiagnostics GetTextPreparationDiagnostics()
        => new(
            Interlocked.Read(ref s_textGroupCalls),
            Interlocked.Read(ref s_textEmptyGroupCalls),
            Interlocked.Read(ref s_textPrepareTotalCalls),
            Interlocked.Read(ref s_textPrepareFailureCount),
            Volatile.Read(ref s_textLastAttemptFrame),
            Volatile.Read(ref s_textLastPreparedFrame),
            Volatile.Read(ref s_textLastGlyphCount),
            Volatile.Read(ref s_textLastPrepareReason));

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
        public UIQuadStyle Style;
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
        public int GlyphOffset;
        public int GlyphCount;
    }

    /// <summary>
    /// Aggregated text batch data for a single render pass and font atlas.
    /// </summary>
    private sealed class TextBatchData
    {
        public BoundingRectangle? CropRegion;
        public XRTexture2D? Atlas;
        public int AtlasType;
        public float DistanceRange;
        public float DistanceRangeMiddle;
        public float MsdfFillBias;
        public int DebugMode;
        public bool HasOutline;
        public int GpuSlotIndex;
        public readonly List<TextEntry> Entries = [];
        public readonly List<(Vector4 transform, Vector4 uvs)> Glyphs = [];
        public int TotalGlyphs;

        public void Clear()
        {
            CropRegion = null;
            Atlas = null;
            AtlasType = TextAtlasBitmap;
            DistanceRange = 0.0f;
            DistanceRangeMiddle = 0.5f;
            MsdfFillBias = 0.5f;
            DebugMode = 0;
            HasOutline = false;
            GpuSlotIndex = 0;
            Entries.Clear();
            Glyphs.Clear();
            TotalGlyphs = 0;
        }
    }

    /// <summary>
    /// Aggregated material quad batch for a single render pass.
    /// </summary>
    private sealed class MaterialQuadBatchData
    {
        public BoundingRectangle? CropRegion;
        public XRTexture2D? Texture;
        public bool ImageSrgb;
        public bool RequestsBackdropBlur;
        public int GpuSlotIndex;
        public readonly List<MaterialQuadEntry> Entries = [];

        public void Clear()
        {
            CropRegion = null;
            Texture = null;
            ImageSrgb = false;
            RequestsBackdropBlur = false;
            GpuSlotIndex = 0;
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
        public int ActiveZIndex;
        public BoundingRectangle? ActiveCropRegion;
        public XRTexture2D? ActiveMaterialTexture;
        public bool ActiveMaterialImageSrgb;
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
            ActiveZIndex = 0;
            ActiveCropRegion = null;
            ActiveMaterialTexture = null;
            ActiveMaterialImageSrgb = false;
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
            if (_kind == EBatchMarkerKind.Text && s_textMarkerDiagCount++ < 40)
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

    private readonly Dictionary<int, MarkerPool> _markerPools = [];

    #endregion

    #region GPU Resources — Material Quads (per draw slot)

    private sealed class MaterialQuadGPUResources : IDisposable
    {
        public XRMeshRenderer? Mesh;
        public XRMeshRenderer? BlurMesh;
        public XRTexture2D? BoundBlurTexture;
        public XRDataBuffer<Vector4>? TransformBuf;  // binding 0: 4 × vec4 per instance (mat4 rows)
        public XRDataBuffer<Vector4>? ColorBuf;      // binding 1: vec4 per instance
        public XRDataBuffer<Vector4>? BoundsBuf;     // binding 2: vec4 per instance
        public XRDataBuffer<Vector4>? UvBuf;         // textured binding 3: UV rectangle per instance
        public XRDataBuffer<Vector4>? StyleBuf;      // binding 4: MaterialStyleVec4Count × vec4 per instance
        public readonly Dictionary<(XRTexture2D Texture, bool Srgb), XRMeshRenderer> TextureMeshes = [];
        public uint Capacity;
        public bool NeedsPush;

        public void Dispose()
        {
            XRMeshRenderer? meshRenderer = Mesh;
            XRMesh? mesh = meshRenderer?.Mesh;
            XRMaterial? material = meshRenderer?.Material;
            if (BlurMesh is not null)
            {
                XRMaterial? blurMaterial = BlurMesh.Material;
                BlurMesh.Destroy();
                blurMaterial?.Destroy();
                BlurMesh = null;
                BoundBlurTexture = null;
            }
            TransformBuf?.Destroy();
            ColorBuf?.Destroy();
            BoundsBuf?.Destroy();
            UvBuf?.Destroy();
            StyleBuf?.Destroy();
            foreach (XRMeshRenderer texturedMesh in TextureMeshes.Values)
            {
                XRMaterial? texturedMaterial = texturedMesh.Material;
                texturedMesh.Destroy();
                texturedMaterial?.Destroy();
            }
            TextureMeshes.Clear();
            meshRenderer?.Destroy();
            mesh?.Destroy();
            material?.Destroy();
            TransformBuf = null;
            ColorBuf = null;
            BoundsBuf = null;
            UvBuf = null;
            StyleBuf = null;
            Mesh = null;
        }
    }

    private readonly List<MaterialQuadGPUResources> _materialQuadGPU = [];
    private readonly List<(XRTexture2D Texture, bool Srgb)> _staleMaterialTextures = [];
    private const int MaxMaterialTextureMeshes = 64;
    private float _collectMaxBackdropBlurRadius;
    private float _renderMaxBackdropBlurRadius;
    private bool _materialPrewarmComplete;
    private XRTexture2D? _prewarmedBlurTexture;

    #endregion

    #region GPU Resources — Text Quads (per font atlas and draw slot)

    private sealed class TextGPUResources : IDisposable
    {
        public XRMeshRenderer? Mesh;
        public XRDataBuffer<Vector4>? GlyphTransformsBuf;  // binding 0: vec4 per glyph
        public XRDataBuffer<Vector4>? GlyphTexCoordsBuf;   // binding 1: vec4 per glyph
        public XRDataBuffer<Vector4>? TextInstanceBuf;      // binding 2: TextInstanceVec4Count × vec4 per text
        public XRDataBuffer<uint>? GlyphTextIndexBuf;       // binding 3: uint per glyph → text index
        public uint GlyphCapacity;
        public uint TextCapacity;
        public bool NeedsPush;

        public void Dispose()
        {
            XRMeshRenderer? meshRenderer = Mesh;
            XRMesh? mesh = meshRenderer?.Mesh;
            XRMaterial? material = meshRenderer?.Material;
            GlyphTransformsBuf?.Destroy();
            GlyphTexCoordsBuf?.Destroy();
            TextInstanceBuf?.Destroy();
            GlyphTextIndexBuf?.Destroy();
            meshRenderer?.Destroy();
            mesh?.Destroy();
            material?.Destroy();
            GlyphTransformsBuf = null;
            GlyphTexCoordsBuf = null;
            TextInstanceBuf = null;
            GlyphTextIndexBuf = null;
            Mesh = null;
        }
    }

    private readonly Dictionary<XRTexture2D, List<TextGPUResources>> _textGPU = [];
    private readonly Dictionary<XRTexture2D, int> _swapAtlasSlotCounts = [];
    private bool _disposed;

    #endregion

    #region Public State

    private bool _enabled = true;
    /// <summary>
    /// When false, batch collection is disabled and components fall back to individual rendering.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Pipeline-agnostic blurred backdrop texture active during screen-space UI rendering.
    /// Scoped by <c>VPRC_RenderScreenSpaceUI</c> from the host pipeline's <c>RenderingState.UiBackdropBlurTexture</c>.
    /// </summary>
    internal static XRTexture2D? ActiveBackdropBlurTexture { get; set; }

    /// <summary>
    /// Host pipeline type name scoped by <c>VPRC_RenderScreenSpaceUI</c> for missing-blur diagnostics.
    /// </summary>
    internal static string? ActiveBackdropBlurHostPipelineName { get; set; }

    /// <summary>
    /// Gets the maximum backdrop blur radius in logical pixels requested by any render-side material quad this frame.
    /// Returns 0 when no visible UI quad requests backdrop blur.
    /// </summary>
    public float MaxRequestedBackdropBlurRadius => _renderMaxBackdropBlurRadius;

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

    #endregion

    #region Collect-Visible Thread API

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
        in UIQuadStyle style = default)
    {
        var state = GetOrCreateCollectPassState(renderPass);
        var groups = GetOrCreatePool(_collectMaterialGroups, renderPass);

        MaterialQuadBatchData batch;
        if (state.ActiveKind == EBatchMarkerKind.Material && state.ActiveZIndex == zIndex &&
            state.ActiveCropRegion == cropRegion &&
            ReferenceEquals(state.ActiveMaterialTexture, texture) &&
            state.ActiveMaterialImageSrgb == imageSrgb &&
            groups.TryGet(state.ActiveGroupIndex, out var existingBatch))
        {
            batch = existingBatch;
        }
        else
        {
            batch = groups.Acquire(out int groupIndex);
            batch.Clear();
            batch.CropRegion = cropRegion;
            batch.Texture = texture;
            batch.ImageSrgb = imageSrgb;
            state.ActiveKind = EBatchMarkerKind.Material;
            state.ActiveZIndex = zIndex;
            state.ActiveCropRegion = cropRegion;
            state.ActiveMaterialTexture = texture;
            state.ActiveMaterialImageSrgb = imageSrgb;
            state.ActiveTextAtlas = null;
            state.ActiveGroupIndex = groupIndex;
            passes.AddCPU(AcquireMarker(renderPass, zIndex, EBatchMarkerKind.Material, groupIndex));
        }

        float requestedBlur = style.EffectiveBackdropBlurRadius;
        if (requestedBlur > 0.001f)
        {
            batch.RequestsBackdropBlur = true;
            if (requestedBlur > _collectMaxBackdropBlurRadius)
                _collectMaxBackdropBlurRadius = requestedBlur;
        }

        batch.Entries.Add(new MaterialQuadEntry
        {
            WorldMatrix = worldMatrix,
            Color = color,
            UIXYWH = uixywh,
            UV = uv,
            Style = style,
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
        List<(Vector4 transform, Vector4 uvs)> glyphs,
        BoundingRectangle? cropRegion = null)
    {
        var state = GetOrCreateCollectPassState(renderPass);
        var groups = GetOrCreatePool(_collectTextGroups, renderPass);

        TextBatchData batch;
        if (TextBatchMatches(state, zIndex, fontAtlas, atlasType, distanceRange, distanceRangeMiddle, msdfFillBias, debugMode, cropRegion) &&
            groups.TryGet(state.ActiveGroupIndex, out var existingBatch))
        {
            batch = existingBatch;
        }
        else
        {
            batch = groups.Acquire(out int groupIndex);
            batch.Clear();
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
        }

        int glyphOffset = batch.Glyphs.Count;
        for (int i = 0; i < glyphs.Count; i++)
            batch.Glyphs.Add(glyphs[i]);

        batch.Entries.Add(new TextEntry
        {
            WorldMatrix = worldMatrix,
            TextColor = textColor,
            OutlineColor = outlineColor,
            OutlineThickness = outlineThickness,
            UIXYWH = uixywh,
            GlyphOffset = glyphOffset,
            GlyphCount = glyphs.Count,
        });
        batch.HasOutline |= outlineThickness > 0.0f && outlineColor.W > 0.0f;
        batch.TotalGlyphs += glyphs.Count;

        if (s_textAddDiagCount++ < 40)
        {
            var firstGlyph = glyphs.Count > 0 ? glyphs[0] : default;
            Debug.Log(
                ELogCategory.UI,
                "[FpsTextDiag] UIBatchCollector.AddTextQuad #{0}: pass={1} z={2} group={3} entries={4} totalGlyphs={5} glyphs={6} atlasType={7} range={8:F2} middle={9:F2} bias={10:F2} debugMode={11} color=({12:F2},{13:F2},{14:F2},{15:F2}) worldT=({16:F1},{17:F1},{18:F1}) bounds=({19:F1},{20:F1},{21:F1},{22:F1}) firstTfm=({23:F2},{24:F2},{25:F2},{26:F2}) firstUv=({27:F5},{28:F5},{29:F5},{30:F5}) atlas=({31}x{32})",
                s_textAddDiagCount,
                renderPass,
                zIndex,
                state.ActiveGroupIndex,
                batch.Entries.Count,
                batch.TotalGlyphs,
                glyphs.Count,
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
                firstGlyph.transform.X,
                firstGlyph.transform.Y,
                firstGlyph.transform.Z,
                firstGlyph.transform.W,
                firstGlyph.uvs.X,
                firstGlyph.uvs.Y,
                firstGlyph.uvs.Z,
                firstGlyph.uvs.W,
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
    /// Swaps collecting and rendering batch data.
    /// Call between collect-visible and render, alongside <see cref="Rendering.Commands.RenderCommandCollection.SwapBuffers"/>.
    /// </summary>
    public void SwapBuffers()
    {
        (_collectMaterialGroups, _renderMaterialGroups) = (_renderMaterialGroups, _collectMaterialGroups);
        (_collectTextGroups, _renderTextGroups) = (_renderTextGroups, _collectTextGroups);
        _renderMaxBackdropBlurRadius = _collectMaxBackdropBlurRadius;
        _collectMaxBackdropBlurRadius = 0.0f;

        // Assign each render-side batch group a dedicated GPU buffer slot for this frame so
        // deferred backends (such as Vulkan) do not overwrite an earlier draw's SSBO data.
        int nextMaterialSlot = 0;
        foreach (var groups in _renderMaterialGroups.Values)
        {
            for (int i = 0; i < groups.Count; i++)
                groups[i].GpuSlotIndex = nextMaterialSlot++;
        }

        _swapAtlasSlotCounts.Clear();
        foreach (var groups in _renderTextGroups.Values)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                TextBatchData batch = groups[i];
                if (batch.Atlas is not { } atlas)
                    continue;

                _swapAtlasSlotCounts.TryGetValue(atlas, out int nextTextSlot);
                batch.GpuSlotIndex = nextTextSlot;
                _swapAtlasSlotCounts[atlas] = nextTextSlot + 1;
            }
        }

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
        _collectMaxBackdropBlurRadius = 0.0f;
        _renderMaxBackdropBlurRadius = 0.0f;
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

    private MaterialQuadGPUResources EnsureMaterialQuadGPUResources(int slotIndex)
    {
        while (_materialQuadGPU.Count <= slotIndex)
            _materialQuadGPU.Add(CreateMaterialQuadGPUResources());

        return _materialQuadGPU[slotIndex];
    }

    private static MaterialQuadGPUResources CreateMaterialQuadGPUResources()
    {
        var gpu = new MaterialQuadGPUResources();

        // Create the batched material
        bool webGpu = AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
        XRShader[] shaders = webGpu ? [] :
        [
            XRShader.EngineShader(Path.Combine("Common", "UIQuadBatched.vs"), EShaderType.Vertex),
            XRShader.EngineShader(Path.Combine("Common", "UIQuadBatched.fs"), EShaderType.Fragment)
        ];

        ShaderVar[] parameters = webGpu ? Array.Empty<ShaderVar>() :
        [
            new ShaderInt(0, UiBackdropBlurAvailableUniformName),
            new ShaderVector2(new Vector2(1920.0f, 1080.0f), UiViewportSizeUniformName),
        ];

        XRTexture[] textures = webGpu ? Array.Empty<XRTexture>() :
        [
            XRTexture2D.GetRoleAwareFallbackTexture("Texture0"),
        ];

        var material = new XRMaterial(parameters, textures, shaders)
        {
            Name = "UIBatchMaterialQuadMaterial",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = !webGpu
                },
                StencilTest = new StencilTest
                {
                    Enabled = ERenderParamUsage.Disabled
                },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                MissingTextureFallback = EMissingTextureFallback.Black,
            }
        };
        if (webGpu)
            material.EngineSemantic = EngineMaterialSemanticIdentity.UIQuadBatchedV2;

        gpu.Mesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material)
        {
            Name = "UIBatchMaterialQuadRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        if (gpu.Mesh.Mesh is not null)
            gpu.Mesh.Mesh.Name = "UIBatchMaterialQuadMesh";

        DisableShaderPipelines(gpu.Mesh);
        gpu.Mesh.EnsureRenderPipelineVersionsCreated();

        // Create initial SSBOs with a reasonable starting capacity
        gpu.Capacity = 64;
        CreateMaterialQuadBuffers(gpu);
        gpu.NeedsPush = true; // Ensure first upload allocates GPU buffers via PushData
        return gpu;
    }

    private static XRMeshRenderer EnsureMaterialBlurMesh(MaterialQuadGPUResources gpu, XRTexture2D blurTexture)
    {
        if (gpu.BlurMesh is not null && ReferenceEquals(gpu.BoundBlurTexture, blurTexture))
            return gpu.BlurMesh;

        if (gpu.BlurMesh is not null)
        {
            XRMaterial? oldMat = gpu.BlurMesh.Material;
            gpu.BlurMesh.Destroy();
            oldMat?.Destroy();
            gpu.BlurMesh = null;
            gpu.BoundBlurTexture = null;
        }

        XRShader[] shaders =
        [
            XRShader.EngineShader(Path.Combine("Common", "UIQuadBatched.vs"), EShaderType.Vertex),
            XRShader.EngineShader(Path.Combine("Common", "UIQuadBatched.fs"), EShaderType.Fragment)
        ];

        ShaderVar[] parameters =
        [
            new ShaderInt(1, UiBackdropBlurAvailableUniformName),
            new ShaderVector2(new Vector2(1920.0f, 1080.0f), UiViewportSizeUniformName),
        ];

        var material = new XRMaterial(parameters, [blurTexture], shaders)
        {
            Name = "UIBatchMaterialQuadMaterial",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = true
                },
                StencilTest = new StencilTest
                {
                    Enabled = ERenderParamUsage.Disabled
                },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                MissingTextureFallback = EMissingTextureFallback.Black,
            }
        };

        XRMeshRenderer blurMesh = new(gpu.Mesh!.Mesh!, material)
        {
            Name = "UIBatchMaterialQuadRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        blurMesh.Buffers["QuadTransformBuffer"] = gpu.TransformBuf!;
        blurMesh.Buffers["QuadColorBuffer"] = gpu.ColorBuf!;
        blurMesh.Buffers["QuadBoundsBuffer"] = gpu.BoundsBuf!;
        if (gpu.StyleBuf is not null)
            blurMesh.Buffers["QuadStyleBuffer"] = gpu.StyleBuf;
        DisableShaderPipelines(blurMesh);
        blurMesh.EnsureRenderPipelineVersionsCreated();

        gpu.BlurMesh = blurMesh;
        gpu.BoundBlurTexture = blurTexture;
        return blurMesh;
    }

    /// <summary>
    /// Pre-warms initial batched quad GPU slots and backdrop-blur shader pipelines during idle frames
    /// so opening a glass overlay incurs zero first-frame compile hitch or missing-quad flash.
    /// </summary>
    internal void PrewarmMaterialQuadResources(XRTexture2D? backdropBlurTexture)
    {
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU)
            return;

        if (_materialPrewarmComplete && ReferenceEquals(_prewarmedBlurTexture, backdropBlurTexture))
            return;

        const int prewarmSlotCount = 4;
        bool allReady = true;
        for (int i = 0; i < prewarmSlotCount; i++)
        {
            MaterialQuadGPUResources gpu = EnsureMaterialQuadGPUResources(i);
            if (gpu.NeedsPush)
            {
                gpu.TransformBuf?.PushData();
                gpu.ColorBuf?.PushData();
                gpu.BoundsBuf?.PushData();
                gpu.StyleBuf?.PushData();
                gpu.NeedsPush = false;
            }
            if (gpu.Mesh is { } baseMesh)
            {
                GetImmediateRenderVersion(baseMesh).Generate();
                allReady &= baseMesh.TryPrepareForRendering();
            }

            if (backdropBlurTexture is not null && i < 2)
            {
                XRMeshRenderer blurMesh = EnsureMaterialBlurMesh(gpu, backdropBlurTexture);
                GetImmediateRenderVersion(blurMesh).Generate();
                allReady &= blurMesh.TryPrepareForRendering();
            }
        }

        _prewarmedBlurTexture = backdropBlurTexture;
        _materialPrewarmComplete = allReady;
    }

    private static void CreateMaterialQuadBuffers(MaterialQuadGPUResources gpu)
    {
        XRMeshRenderer mesh = gpu.Mesh!;
        gpu.TransformBuf?.Destroy();
        gpu.ColorBuf?.Destroy();
        gpu.BoundsBuf?.Destroy();
        gpu.UvBuf?.Destroy();
        gpu.StyleBuf?.Destroy();

        // Transform buffer: 4 vec4 rows per matrix, so elementCount = capacity * 4
        gpu.TransformBuf = new XRDataBuffer<Vector4>(
            "QuadTransformBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.Capacity * 4)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 0,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadTransformBuffer"] = gpu.TransformBuf;

        // Color buffer: 1 vec4 per instance
        gpu.ColorBuf = new XRDataBuffer<Vector4>(
            "QuadColorBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.Capacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 1,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadColorBuffer"] = gpu.ColorBuf;

        // Bounds buffer: 1 vec4 per instance
        gpu.BoundsBuf = new XRDataBuffer<Vector4>(
            "QuadBoundsBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.Capacity)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 2,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadBoundsBuffer"] = gpu.BoundsBuf;

        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU)
        {
            gpu.UvBuf = new XRDataBuffer<Vector4>(
                "QuadUvBuffer", EBufferTarget.ShaderStorageBuffer, gpu.Capacity)
            {
                Usage = EBufferUsage.StreamDraw,
                BindingIndexOverride = 3,
                DisposeOnPush = false
            };
        }
        else
        {
            // Style buffer: MaterialStyleVec4Count vec4s per instance (rounded corners, border, gradient, shadow, blur).
            gpu.StyleBuf = new XRDataBuffer<Vector4>(
                "QuadStyleBuffer", EBufferTarget.ShaderStorageBuffer,
                gpu.Capacity * MaterialStyleVec4Count)
            {
                Usage = EBufferUsage.StreamDraw,
                BindingIndexOverride = 4,
                DisposeOnPush = false
            };
            mesh.Buffers["QuadStyleBuffer"] = gpu.StyleBuf;
        }
    }

    private XRMeshRenderer EnsureMaterialTextureMesh(MaterialQuadGPUResources gpu, XRTexture2D texture, bool srgb)
    {
        var key = (texture, srgb);
        if (gpu.TextureMeshes.TryGetValue(key, out XRMeshRenderer? existing))
            return existing;

        // Old image materials are retired on the render thread. They borrow the
        // image resource, so replacement must not destroy the authored texture.
        _staleMaterialTextures.Clear();
        foreach (var cached in gpu.TextureMeshes.Keys)
            if (!IsCurrentMaterialTexture(cached))
                _staleMaterialTextures.Add(cached);
        foreach (var stale in _staleMaterialTextures)
        {
            XRMeshRenderer retired = gpu.TextureMeshes[stale];
            XRMaterial? retiredMaterial = retired.Material;
            retired.Destroy();
            retiredMaterial?.Destroy();
            gpu.TextureMeshes.Remove(stale);
        }
        if (gpu.TextureMeshes.Count >= MaxMaterialTextureMeshes)
            throw new NotSupportedException("WebGPU.UI.ImageTextureBudgetExceeded: at most 64 distinct image and color-space bindings may be visible in one frame.");

        XRMaterial material = new(Array.Empty<ShaderVar>(), [texture], Array.Empty<XRShader>())
        {
            Name = "UIBatchMaterialTexture",
            SurfaceTextureBindings = [new(EMaterialTextureSemantic.BaseColor, texture, IsSrgb: srgb)],
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false
                },
                StencilTest = new StencilTest { Enabled = ERenderParamUsage.Disabled },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                MissingTextureFallback = EMissingTextureFallback.Black,
            }
        };
        material.EngineSemantic = EngineMaterialSemanticIdentity.UIQuadBatchedTextureV2;
        XRMeshRenderer mesh = new(gpu.Mesh!.Mesh!, material)
        {
            Name = "UIBatchMaterialTextureRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        mesh.Buffers["QuadTransformBuffer"] = gpu.TransformBuf!;
        mesh.Buffers["QuadColorBuffer"] = gpu.ColorBuf!;
        mesh.Buffers["QuadBoundsBuffer"] = gpu.BoundsBuf!;
        mesh.Buffers["QuadUvBuffer"] = gpu.UvBuf!;
        DisableShaderPipelines(mesh);
        mesh.EnsureRenderPipelineVersionsCreated();
        gpu.TextureMeshes.Add(key, mesh);
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

    private static void UploadMaterialQuadData(MaterialQuadGPUResources gpu, List<MaterialQuadEntry> entries, bool textured)
    {
        uint count = (uint)entries.Count;
        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU && count > 65536)
            throw new NotSupportedException("WebGPU.UI.QuadCapacityExceeded: one screen UI quad group admits at most 65536 instances.");

        // Resize if needed
        if (count > gpu.Capacity)
        {
            gpu.Capacity = NextPowerOf2(count);
            gpu.TransformBuf!.Resize(gpu.Capacity * 4);
            gpu.ColorBuf!.Resize(gpu.Capacity);
            gpu.BoundsBuf!.Resize(gpu.Capacity);
            gpu.UvBuf?.Resize(gpu.Capacity);
            gpu.StyleBuf?.Resize(gpu.Capacity * MaterialStyleVec4Count);
            gpu.NeedsPush = true;
        }

        // Write transform data (4 × vec4 per instance = 16 floats per matrix)
        bool fullUpload = gpu.NeedsPush;
        bool hasStyleBuf = gpu.StyleBuf is not null;
        uint transformElementCount = fullUpload ? gpu.Capacity * 4u : count * 4u;
        uint instanceElementCount = fullUpload ? gpu.Capacity : count;
        uint styleElementCount = fullUpload ? gpu.Capacity * MaterialStyleVec4Count : count * MaterialStyleVec4Count;
        XRBufferWriter<Vector4> transformWriter = gpu.TransformBuf!.Alloc<Vector4>(transformElementCount);
        XRBufferWriter<Vector4> colorWriter = gpu.ColorBuf!.Alloc<Vector4>(instanceElementCount);
        XRBufferWriter<Vector4> boundsWriter = gpu.BoundsBuf!.Alloc<Vector4>(instanceElementCount);
        XRBufferWriter<Vector4> uvWriter = textured
            ? gpu.UvBuf!.Alloc<Vector4>(instanceElementCount) : default;
        XRBufferWriter<Vector4> styleWriter = hasStyleBuf
            ? gpu.StyleBuf!.Alloc<Vector4>(styleElementCount) : default;
        Span<Vector4> transforms = transformWriter.Span;
        Span<Vector4> colors = colorWriter.Span;
        Span<Vector4> bounds = boundsWriter.Span;
        Span<Vector4> uvs = textured ? uvWriter.Span : default;
        Span<Vector4> styles = hasStyleBuf ? styleWriter.Span : default;
        int transformIndex = 0;
        int styleIndex = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            ref readonly var e = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries)[i];

            // Write matrix rows (row-major, shader transposes)
            WriteMatrix4x4(transforms, ref transformIndex, in e.WorldMatrix);
            colors[i] = e.Color;
            bounds[i] = e.UIXYWH;
            if (textured)
                uvs[i] = e.UV;
            if (hasStyleBuf)
                WriteQuadStyle(styles, ref styleIndex, in e.Style);
        }

        transformWriter.Commit();
        colorWriter.Commit();
        boundsWriter.Commit();
        if (textured)
            uvWriter.Commit();
        if (hasStyleBuf)
            styleWriter.Commit();
        gpu.NeedsPush = false;
    }

    #endregion

    #region GPU Resource Management — Text Quads

    private TextGPUResources EnsureTextGPUResources(XRTexture2D fontAtlas, int slotIndex)
    {
        if (!_textGPU.TryGetValue(fontAtlas, out List<TextGPUResources>? slots))
        {
            slots = [];
            _textGPU[fontAtlas] = slots;
        }

        while (slots.Count <= slotIndex)
            slots.Add(CreateTextGPUResources(fontAtlas));

        return slots[slotIndex];
    }

    private static TextGPUResources CreateTextGPUResources(XRTexture2D fontAtlas)
    {
        var gpu = new TextGPUResources();

        bool webGpu = AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;
        XRShader[] shaders = webGpu ? [] :
        [
            XRShader.EngineShader(Path.Combine("Common", "UITextBatched.vs"), EShaderType.Vertex),
            XRShader.EngineShader(Path.Combine("Common", "UITextBatchedStereoMV2.vs"), EShaderType.Vertex),
            XRShader.EngineShader(Path.Combine("Common", "UITextBatchedStereoNV.vs"), EShaderType.Vertex),
            XRShader.EngineShader(Path.Combine("Common", "UITextBatched.fs"), EShaderType.Fragment)
        ];

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
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = !webGpu
                },
                StencilTest = new StencilTest
                {
                    Enabled = ERenderParamUsage.Disabled
                },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                MissingTextureFallback = EMissingTextureFallback.Black,
            }
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
        DisableShaderPipelines(gpu.Mesh);
        gpu.Mesh.EnsureRenderPipelineVersionsCreated();

        gpu.GlyphCapacity = 256;
        gpu.TextCapacity = 32;
        CreateTextBuffers(gpu);
        gpu.NeedsPush = true; // Ensure first upload allocates GPU buffers via PushData
        return gpu;
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
        if (!Enabled)
            return;

        if (kind == EBatchMarkerKind.Material)
            RenderMaterialGroup(renderPass, groupIndex);
        else
            RenderTextGroup(renderPass, groupIndex);
    }

    private void RenderMaterialGroup(int renderPass, int groupIndex)
    {
        if (!_renderMaterialGroups.TryGetValue(renderPass, out var groups) ||
            !groups.TryGet(groupIndex, out var batch) ||
            batch.Entries.Count == 0)
        {
            return;
        }

        using var sample = RuntimeEngine.Profiler.Start();
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        bool profileGpu = profiler.ShouldInstrumentCommandScopes;

        MaterialQuadGPUResources gpu;
        if (profileGpu)
        {
            using (profiler.StartScope($"UIBatchMaterialUpload[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batch.Entries.Count}]"))
            {
                gpu = EnsureMaterialQuadGPUResources(batch.GpuSlotIndex);
                UploadMaterialQuadData(gpu, batch.Entries, batch.Texture is not null);
            }
        }
        else
        {
            gpu = EnsureMaterialQuadGPUResources(batch.GpuSlotIndex);
            UploadMaterialQuadData(gpu, batch.Entries, batch.Texture is not null);
        }

        XRTexture2D? activeBackdropBlur = batch.RequestsBackdropBlur && batch.Texture is null
            ? ActiveBackdropBlurTexture ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.UiBackdropBlurTexture as XRTexture2D
            : null;
        if (batch.RequestsBackdropBlur && batch.Texture is null && activeBackdropBlur is null)
        {
            Debug.UIEvery(
                "UIBatchCollector.MissingBackdropBlur",
                TimeSpan.FromSeconds(10),
                "[UIBatch] Backdrop blur requested (maxRadius={0:F1}px) on host pipeline '{1}', but no UiBackdropBlurTexture is available; rendering tinted glass fallback without blur.",
                _renderMaxBackdropBlurRadius,
                ActiveBackdropBlurHostPipelineName ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.GetType().Name ?? "<unknown>");
        }
        XRMeshRenderer materialQuadMesh;
        bool usingBackdropBlur = false;
        if (batch.Texture is { } texture)
        {
            materialQuadMesh = EnsureMaterialTextureMesh(gpu, texture, batch.ImageSrgb);
        }
        else if (activeBackdropBlur is not null)
        {
            XRMeshRenderer blurMesh = EnsureMaterialBlurMesh(gpu, activeBackdropBlur);
            GetImmediateRenderVersion(blurMesh).Generate();
            if (blurMesh.TryPrepareForRendering())
            {
                materialQuadMesh = blurMesh;
                usingBackdropBlur = true;
            }
            else
            {
                // Fall back to the non-blurred styled quad mesh while the blur pipeline prepares,
                // avoiding any single-frame blank card or placeholder texture flash.
                materialQuadMesh = gpu.Mesh!;
            }
        }
        else
        {
            materialQuadMesh = gpu.Mesh!;
        }

        if (materialQuadMesh.Material is { } quadMat && batch.Texture is null &&
            AbstractRenderer.Current?.BackendId != RendererBackendId.WebGPU)
        {
            quadMat.SetInt(UiBackdropBlurAvailableUniformName, usingBackdropBlur ? 1 : 0);
            BoundingRectangle area = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.CurrentRenderRegion
                ?? RuntimeEngine.Rendering.State.RenderArea;
            if (area.Width <= 0 || area.Height <= 0)
                area = RuntimeEngine.Rendering.State.RenderArea;
            quadMat.SetVector2(
                UiViewportSizeUniformName,
                new Vector2(Math.Max(1, area.Width), Math.Max(1, area.Height)));
        }

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
            return;

        Debug.UIEvery(
            "UIBatchCollector.RenderMaterialQuadBatch",
            TimeSpan.FromSeconds(5),
            "[UIBatch] RenderMaterialQuadBatch: pass={0}, entries={1}, capacity={2}",
            renderPass, batch.Entries.Count, gpu.Capacity);

        if (s_materialRenderDiagCount++ < 40)
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

        ApplyBatchCropping(batch.CropRegion);
        try
        {
            if (profileGpu)
            {
                using (profiler.StartScope($"UIBatchMaterialDraw[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batch.Entries.Count}]"))
                    materialQuadMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batch.Entries.Count);
            }
            else
            {
                materialQuadMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batch.Entries.Count);
            }
        }
        finally
        {
            DisableBatchCropping();
        }
    }

    private void RenderTextGroup(int renderPass, int groupIndex)
    {
        Interlocked.Increment(ref s_textGroupCalls);
        if (!_renderTextGroups.TryGetValue(renderPass, out var groups) ||
            !groups.TryGet(groupIndex, out var batchData) ||
            batchData.Atlas is null ||
            batchData.Entries.Count == 0 ||
            batchData.TotalGlyphs == 0)
        {
            Interlocked.Increment(ref s_textEmptyGroupCalls);
            return;
        }

        if (AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU &&
            (batchData.AtlasType != TextAtlasBitmap || batchData.DebugMode != 0))
            throw new NotSupportedException("WebGPU.UI.TextProfileUnsupported: screen text requires a bitmap atlas and normal shading.");

        using var sample = RuntimeEngine.Profiler.Start();

        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        bool profileGpu = profiler.ShouldInstrumentCommandScopes;
        TextGPUResources gpu;
        if (profileGpu)
        {
            using (profiler.StartScope($"UIBatchTextEnsureResources[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
                gpu = EnsureTextGPUResources(batchData.Atlas, batchData.GpuSlotIndex);
        }
        else
        {
            gpu = EnsureTextGPUResources(batchData.Atlas, batchData.GpuSlotIndex);
        }

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

        if (s_textRenderDiagCount++ < 40)
        {
            var firstEntry = batchData.Entries[0];
            var firstGlyph = firstEntry.GlyphCount > 0
                ? batchData.Glyphs[firstEntry.GlyphOffset]
                : default;
            string projectedGlyph = GetProjectedGlyphSummary(in firstEntry.WorldMatrix, in firstGlyph.transform);
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
                firstGlyph.transform.X,
                firstGlyph.transform.Y,
                firstGlyph.transform.Z,
                firstGlyph.transform.W,
                firstGlyph.uvs.X,
                firstGlyph.uvs.Y,
                firstGlyph.uvs.Z,
                firstGlyph.uvs.W,
                projectedGlyph);
        }

        if (profileGpu)
        {
            using (profiler.StartScope($"UIBatchTextUpload[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
                FinalizeAndUploadTextData(batchData, gpu, batchData.Atlas);
        }
        else
        {
            FinalizeAndUploadTextData(batchData, gpu, batchData.Atlas);
        }

        var version = GetImmediateRenderVersion(gpu.Mesh!);
        Interlocked.Increment(ref s_textPrepareTotalCalls);
        s_textLastAttemptFrame = RuntimeEngine.Rendering.State.RenderFrameId;
        s_textLastGlyphCount = batchData.TotalGlyphs;
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
            Interlocked.Increment(ref s_textPrepareFailureCount);
            s_textLastPrepareReason = prepareReason;
            if (!s_textPrepareReasonCounts.TryGetValue(prepareReason, out int reasonCount))
                reasonCount = 0;
            s_textPrepareReasonCounts[prepareReason] = reasonCount + 1;

            if (s_textPrepareDiagCount++ < 40)
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

            MaybeLogTextPrepareSummary();
            return;
        }

        s_textLastPreparedFrame = RuntimeEngine.Rendering.State.RenderFrameId;
        MaybeLogTextPrepareSummary();

        ApplyBatchCropping(batchData.CropRegion);
        try
        {
            if (profileGpu)
            {
                using (profiler.StartScope($"UIBatchTextDraw[pass={GetRenderPassDisplayName(renderPass)}; group={groupIndex}; entries={batchData.Entries.Count}; glyphs={batchData.TotalGlyphs}]"))
                    textMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batchData.TotalGlyphs);
            }
            else
            {
                textMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batchData.TotalGlyphs);
            }
        }
        finally
        {
            material?.SetInt(TextRenderLayerUniformName, TextRenderLayerCombined);
            material?.SetInt(TextRenderLayerVertexUniformName, TextRenderLayerCombined);
            DisableBatchCropping();
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
                var (transform, uvs) = batchData.Glyphs[entry.GlyphOffset + g];
                glyphTransforms[glyphIndex] = transform;
                glyphUvs[glyphIndex] = uvs;
                glyphIndices[glyphIndex] = (uint)t;
                glyphIndex++;
            }
        }

        if (s_textUploadDiagCount++ < 40)
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
        gpu.NeedsPush = false;
    }

    #endregion

    #region Helpers

    private static void DisableBatchCropping()
        => AbstractRenderer.Current?.SetCroppingEnabled(false);

    private static void ApplyBatchCropping(BoundingRectangle? cropRegion)
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null) return;
        renderer.SetCroppingEnabled(cropRegion.HasValue);
        if (cropRegion is { } region)
            renderer.CropRenderArea(region);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteQuadStyle(Span<Vector4> destination, ref int index, in UIQuadStyle style)
    {
        float cornerExponent = style.CornerExponent > 0.0f ? style.CornerExponent : 2.0f;
        destination[index++] = style.CornerRadii;
        destination[index++] = new Vector4(
            cornerExponent,
            style.BorderWidth,
            style.BorderHighlightBoost,
            (float)(uint)style.Flags);
        destination[index++] = style.BorderColor;
        destination[index++] = style.GradientBottomColor;
        destination[index++] = style.ShadowColor;
        destination[index++] = style.ShadowParams;
        destination[index++] = style.BackdropBlurParams;
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
        Clear();
        for (int i = 0; i < _materialQuadGPU.Count; i++)
            _materialQuadGPU[i].Dispose();
        _materialQuadGPU.Clear();
        _staleMaterialTextures.Clear();

        foreach (List<TextGPUResources> slots in _textGPU.Values)
        {
            for (int i = 0; i < slots.Count; i++)
                slots[i].Dispose();
            slots.Clear();
        }
        _textGPU.Clear();
        _swapAtlasSlotCounts.Clear();
    }

    #endregion
}
