using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
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
    private const int TextAtlasBitmap = 0;
    private static int s_textAddDiagCount;
    private static int s_textMarkerDiagCount;
    private static int s_textRenderDiagCount;
    private static int s_textUploadDiagCount;
    private static int s_textPrepareDiagCount;

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
    }

    /// <summary>
    /// Per-instance data for a batched text component.
    /// Glyphs is a snapshot of the glyph layout data.
    /// </summary>
    public struct TextEntry
    {
        public Matrix4x4 WorldMatrix;
        public Vector4 TextColor;
        public Vector4 UIXYWH;
        public int GlyphCount;
        public (Vector4 transform, Vector4 uvs)[] Glyphs;
    }

    /// <summary>
    /// Aggregated text batch data for a single render pass and font atlas.
    /// </summary>
    private sealed class TextBatchData
    {
        public XRTexture2D? Atlas;
        public int AtlasType;
        public float DistanceRange;
        public float DistanceRangeMiddle;
        public float MsdfFillBias;
        public int DebugMode;
        public readonly List<TextEntry> Entries = [];
        public int TotalGlyphs;

        public void Clear()
        {
            Atlas = null;
            AtlasType = TextAtlasBitmap;
            DistanceRange = 0.0f;
            DistanceRangeMiddle = 0.5f;
            MsdfFillBias = 0.5f;
            DebugMode = 0;
            Entries.Clear();
            TotalGlyphs = 0;
        }
    }

    /// <summary>
    /// Aggregated material quad batch for a single render pass.
    /// </summary>
    private sealed class MaterialQuadBatchData
    {
        public readonly List<MaterialQuadEntry> Entries = [];

        public void Clear() => Entries.Clear();
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
            _kind = kind;
            _groupIndex = groupIndex;
            ZIndex = zIndex;
            Enabled = true;
        }

        public override void SwapBuffers()
        {
            base.SwapBuffers();
            _renderKind = _kind;
            _renderGroupIndex = _groupIndex;
        }

        public override void Render()
        {
            OnPreRender();
            if (_kind == EBatchMarkerKind.Text && s_textMarkerDiagCount++ < 40)
            {
                Debug.Out(
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

    #region GPU Resources — Material Quads

    private XRMeshRenderer? _matQuadMesh;
    private XRDataBuffer? _matQuadTransformBuf;  // binding 0: 4 × vec4 per instance (mat4 rows)
    private XRDataBuffer? _matQuadColorBuf;      // binding 1: vec4 per instance
    private XRDataBuffer? _matQuadBoundsBuf;     // binding 2: vec4 per instance
    private uint _matQuadCapacity;
    private bool _matQuadNeedsPush;

    #endregion

    #region GPU Resources — Text Quads (per font atlas)

    private sealed class TextGPUResources : IDisposable
    {
        public XRMeshRenderer? Mesh;
        public XRDataBuffer? GlyphTransformsBuf;  // binding 0: vec4 per glyph
        public XRDataBuffer? GlyphTexCoordsBuf;   // binding 1: vec4 per glyph
        public XRDataBuffer? TextInstanceBuf;      // binding 2: 6 × vec4 per text (mat4 rows + color + bounds)
        public XRDataBuffer? GlyphTextIndexBuf;    // binding 3: uint per glyph → text index
        public uint GlyphCapacity;
        public uint TextCapacity;
        public bool NeedsPush;

        public void Dispose()
        {
            GlyphTransformsBuf?.Destroy();
            GlyphTexCoordsBuf?.Destroy();
            TextInstanceBuf?.Destroy();
            GlyphTextIndexBuf?.Destroy();
            Mesh?.Destroy();
        }
    }

    private readonly Dictionary<XRTexture2D, TextGPUResources> _textGPU = [];

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
        in Vector4 uixywh)
    {
        var state = GetOrCreateCollectPassState(renderPass);
        var groups = GetOrCreatePool(_collectMaterialGroups, renderPass);

        MaterialQuadBatchData batch;
        if (state.ActiveKind == EBatchMarkerKind.Material && groups.TryGet(state.ActiveGroupIndex, out var existingBatch))
        {
            batch = existingBatch;
        }
        else
        {
            batch = groups.Acquire(out int groupIndex);
            batch.Clear();
            state.ActiveKind = EBatchMarkerKind.Material;
            state.ActiveTextAtlas = null;
            state.ActiveGroupIndex = groupIndex;
            passes.AddCPU(AcquireMarker(renderPass, zIndex, EBatchMarkerKind.Material, groupIndex));
        }

        batch.Entries.Add(new MaterialQuadEntry
        {
            WorldMatrix = worldMatrix,
            Color = color,
            UIXYWH = uixywh
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
        in Vector4 uixywh,
        int atlasType,
        float distanceRange,
        float distanceRangeMiddle,
        float msdfFillBias,
        int debugMode,
        (Vector4 transform, Vector4 uvs)[] glyphs)
    {
        var state = GetOrCreateCollectPassState(renderPass);
        var groups = GetOrCreatePool(_collectTextGroups, renderPass);

        TextBatchData batch;
        if (TextBatchMatches(state, fontAtlas, atlasType, distanceRange, distanceRangeMiddle, msdfFillBias, debugMode) &&
            groups.TryGet(state.ActiveGroupIndex, out var existingBatch))
        {
            batch = existingBatch;
        }
        else
        {
            batch = groups.Acquire(out int groupIndex);
            batch.Clear();
            batch.Atlas = fontAtlas;
            batch.AtlasType = atlasType;
            batch.DistanceRange = distanceRange;
            batch.DistanceRangeMiddle = distanceRangeMiddle;
            batch.MsdfFillBias = msdfFillBias;
            batch.DebugMode = debugMode;
            state.ActiveKind = EBatchMarkerKind.Text;
            state.ActiveTextAtlas = fontAtlas;
            state.ActiveTextAtlasType = atlasType;
            state.ActiveTextDistanceRange = distanceRange;
            state.ActiveTextDistanceRangeMiddle = distanceRangeMiddle;
            state.ActiveTextMsdfFillBias = msdfFillBias;
            state.ActiveTextDebugMode = debugMode;
            state.ActiveGroupIndex = groupIndex;
            passes.AddCPU(AcquireMarker(renderPass, zIndex, EBatchMarkerKind.Text, groupIndex));
        }

        batch.Entries.Add(new TextEntry
        {
            WorldMatrix = worldMatrix,
            TextColor = textColor,
            UIXYWH = uixywh,
            GlyphCount = glyphs.Length,
            Glyphs = glyphs
        });
        batch.TotalGlyphs += glyphs.Length;

        if (s_textAddDiagCount++ < 40)
        {
            var firstGlyph = glyphs.Length > 0 ? glyphs[0] : default;
            Debug.Out(
                "[FpsTextDiag] UIBatchCollector.AddTextQuad #{0}: pass={1} z={2} group={3} entries={4} totalGlyphs={5} glyphs={6} atlasType={7} range={8:F2} middle={9:F2} bias={10:F2} debugMode={11} color=({12:F2},{13:F2},{14:F2},{15:F2}) worldT=({16:F1},{17:F1},{18:F1}) bounds=({19:F1},{20:F1},{21:F1},{22:F1}) firstTfm=({23:F2},{24:F2},{25:F2},{26:F2}) firstUv=({27:F5},{28:F5},{29:F5},{30:F5}) atlas=({31}x{32})",
                s_textAddDiagCount,
                renderPass,
                zIndex,
                state.ActiveGroupIndex,
                batch.Entries.Count,
                batch.TotalGlyphs,
                glyphs.Length,
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

    private void EnsureMaterialQuadMesh()
    {
        if (_matQuadMesh is not null)
            return;

        // Create the batched material
        XRShader vertexShader = XRShader.EngineShader(
            Path.Combine("Common", "UIQuadBatched.vs"), EShaderType.Vertex);
        XRShader fragmentShader = XRShader.EngineShader(
            Path.Combine("Common", "UIQuadBatched.fs"), EShaderType.Fragment);

        var material = new XRMaterial(Array.Empty<ShaderVar>(), [vertexShader, fragmentShader])
        {
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always
                },
                StencilTest = new StencilTest
                {
                    Enabled = ERenderParamUsage.Disabled
                },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
            }
        };

        _matQuadMesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material)
        {
            GenerationPriority = EMeshGenerationPriority.RenderPipeline
        };

        _matQuadMesh.EnsureRenderPipelineVersionsCreated();

        DisableShaderPipelines(_matQuadMesh);

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

        // Transform buffer: 4 vec4 rows per matrix, so elementCount = capacity * 4
        _matQuadTransformBuf = new XRDataBuffer(
            "QuadTransformBuffer", EBufferTarget.ShaderStorageBuffer,
            _matQuadCapacity * 4, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 0,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadTransformBuffer"] = _matQuadTransformBuf;

        // Color buffer: 1 vec4 per instance
        _matQuadColorBuf = new XRDataBuffer(
            "QuadColorBuffer", EBufferTarget.ShaderStorageBuffer,
            _matQuadCapacity, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 1,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadColorBuffer"] = _matQuadColorBuf;

        // Bounds buffer: 1 vec4 per instance
        _matQuadBoundsBuf = new XRDataBuffer(
            "QuadBoundsBuffer", EBufferTarget.ShaderStorageBuffer,
            _matQuadCapacity, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 2,
            DisposeOnPush = false
        };
        mesh.Buffers["QuadBoundsBuffer"] = _matQuadBoundsBuf;
    }

    private unsafe void UploadMaterialQuadData(List<MaterialQuadEntry> entries)
    {
        uint count = (uint)entries.Count;

        // Resize if needed
        if (count > _matQuadCapacity)
        {
            _matQuadCapacity = NextPowerOf2(count);
            _matQuadTransformBuf!.Resize(_matQuadCapacity * 4);
            _matQuadColorBuf!.Resize(_matQuadCapacity);
            _matQuadBoundsBuf!.Resize(_matQuadCapacity);
            _matQuadNeedsPush = true;
        }

        // Write transform data (4 × vec4 per instance = 16 floats per matrix)
        float* tfmPtr = (float*)_matQuadTransformBuf!.ClientSideSource!.Address.Pointer;
        float* colPtr = (float*)_matQuadColorBuf!.ClientSideSource!.Address.Pointer;
        float* bndPtr = (float*)_matQuadBoundsBuf!.ClientSideSource!.Address.Pointer;

        for (int i = 0; i < entries.Count; i++)
        {
            ref readonly var e = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries)[i];

            // Write matrix rows (row-major, shader transposes)
            WriteMatrix4x4(ref tfmPtr, in e.WorldMatrix);

            // Write color
            WriteVector4(ref colPtr, in e.Color);

            // Write bounds
            WriteVector4(ref bndPtr, in e.UIXYWH);
        }

        // Push to GPU
        if (_matQuadNeedsPush)
        {
            _matQuadNeedsPush = false;
            _matQuadTransformBuf.PushData();
            _matQuadColorBuf.PushData();
            _matQuadBoundsBuf.PushData();
        }
        else
        {
            _matQuadTransformBuf.PushSubData();
            _matQuadColorBuf.PushSubData();
            _matQuadBoundsBuf.PushSubData();
        }
    }

    #endregion

    #region GPU Resource Management — Text Quads

    private TextGPUResources EnsureTextGPUResources(XRTexture2D fontAtlas)
    {
        if (_textGPU.TryGetValue(fontAtlas, out var gpu))
            return gpu;

        gpu = new TextGPUResources();

        XRShader vertexShader = XRShader.EngineShader(
            Path.Combine("Common", "UITextBatched.vs"), EShaderType.Vertex);
        XRShader stereoMv2VertexShader = XRShader.EngineShader(
            Path.Combine("Common", "UITextBatchedStereoMV2.vs"), EShaderType.Vertex);
        XRShader stereoNvVertexShader = XRShader.EngineShader(
            Path.Combine("Common", "UITextBatchedStereoNV.vs"), EShaderType.Vertex);
        XRShader fragmentShader = XRShader.EngineShader(
            Path.Combine("Common", "UITextBatched.fs"), EShaderType.Fragment);

        ShaderVar[] parameters =
        [
            new ShaderInt(0, TextAtlasTypeUniformName),
            new ShaderFloat(0.0f, MsdfDistanceRangeUniformName),
            new ShaderFloat(0.5f, MsdfDistanceRangeMiddleUniformName),
            new ShaderFloat(0.5f, MsdfFillBiasUniformName),
            new ShaderInt(0, TextDebugModeUniformName),
        ];

        var material = new XRMaterial(parameters, [fontAtlas], [vertexShader, stereoMv2VertexShader, stereoNvVertexShader, fragmentShader])
        {
            Name = "UIBatchTextMaterial",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always
                },
                StencilTest = new StencilTest
                {
                    Enabled = ERenderParamUsage.Disabled
                },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
            }
        };

        gpu.Mesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material);
        gpu.Mesh.Name = "UIBatchTextRenderer";
        if (gpu.Mesh.Mesh is not null)
            gpu.Mesh.Mesh.Name = "UIBatchTextQuadMesh";
        gpu.Mesh.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
        gpu.Mesh.EnsureRenderPipelineVersionsCreated();

        DisableShaderPipelines(gpu.Mesh);

        gpu.GlyphCapacity = 256;
        gpu.TextCapacity = 32;
        CreateTextBuffers(gpu);
        gpu.NeedsPush = true; // Ensure first upload allocates GPU buffers via PushData

        _textGPU[fontAtlas] = gpu;
        return gpu;
    }

    private static XRMeshRenderer.BaseVersion GetImmediateRenderVersion(XRMeshRenderer mesh)
    {
        if (Engine.Rendering.State.IsStereoPass)
        {
            if (Engine.Rendering.Settings.PreferNVStereo && Engine.Rendering.State.IsNVIDIA)
                return mesh.GetNVStereoVersion();

            if (Engine.Rendering.State.HasAnyMultiViewExtension)
                return mesh.GetOVRMultiViewVersion();
        }

        return mesh.GetDefaultVersion();
    }

    private static void DisableShaderPipelines(XRMeshRenderer mesh)
    {
        mesh.GetDefaultVersion().AllowShaderPipelines = false;
        mesh.GetOVRMultiViewVersion().AllowShaderPipelines = false;
        mesh.GetNVStereoVersion().AllowShaderPipelines = false;
        mesh.GetMeshDeformDefaultVersion().AllowShaderPipelines = false;
        mesh.GetMeshDeformOVRMultiViewVersion().AllowShaderPipelines = false;
        mesh.GetMeshDeformNVStereoVersion().AllowShaderPipelines = false;
    }

    private static void CreateTextBuffers(TextGPUResources gpu)
    {
        var mesh = gpu.Mesh!;

        gpu.GlyphTransformsBuf?.Destroy();
        gpu.GlyphTexCoordsBuf?.Destroy();
        gpu.TextInstanceBuf?.Destroy();
        gpu.GlyphTextIndexBuf?.Destroy();

        // Glyph transforms: vec4 per glyph
        gpu.GlyphTransformsBuf = new XRDataBuffer(
            "GlyphTransformsBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.GlyphCapacity, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 0,
            DisposeOnPush = false
        };
        mesh.Buffers["GlyphTransformsBuffer"] = gpu.GlyphTransformsBuf;

        // Glyph tex coords: vec4 per glyph
        gpu.GlyphTexCoordsBuf = new XRDataBuffer(
            "GlyphTexCoordsBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.GlyphCapacity, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 1,
            DisposeOnPush = false
        };
        mesh.Buffers["GlyphTexCoordsBuffer"] = gpu.GlyphTexCoordsBuf;

        // Text instance data: 6 vec4 per text (4 matrix rows + color + bounds)
        gpu.TextInstanceBuf = new XRDataBuffer(
            "TextInstanceBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.TextCapacity * 6, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.StreamDraw,
            BindingIndexOverride = 2,
            DisposeOnPush = false
        };
        mesh.Buffers["TextInstanceBuffer"] = gpu.TextInstanceBuf;

        // Glyph-to-text index: uint per glyph
        gpu.GlyphTextIndexBuf = new XRDataBuffer(
            "GlyphTextIndexBuffer", EBufferTarget.ShaderStorageBuffer,
            gpu.GlyphCapacity, EComponentType.UInt, 1, false, true)
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

        using var sample = Engine.Profiler.Start();

        EnsureMaterialQuadMesh();
        UploadMaterialQuadData(batch.Entries);

        var version = GetImmediateRenderVersion(_matQuadMesh!);
        version.Generate();
        if (!_matQuadMesh.TryPrepareForRendering())
            return;

        Debug.UIEvery(
            "UIBatchCollector.RenderMaterialQuadBatch",
            TimeSpan.FromSeconds(5),
            "[UIBatch] RenderMaterialQuadBatch: pass={0}, entries={1}, capacity={2}",
            renderPass, batch.Entries.Count, _matQuadCapacity);

        if (_matQuadMesh!.Material is { } material && material.RenderPass != renderPass)
            material.RenderPass = renderPass;

        DisableBatchCropping();
        try
        {
            _matQuadMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batch.Entries.Count);
        }
        finally
        {
            DisableBatchCropping();
        }
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

        using var sample = Engine.Profiler.Start();

        var gpu = EnsureTextGPUResources(batchData.Atlas);
        var material = gpu.Mesh!.Material;
        if (material is not null && material.RenderPass != renderPass)
            material.RenderPass = renderPass;

        material?.SetInt(TextAtlasTypeUniformName, batchData.AtlasType);
        material?.SetFloat(MsdfDistanceRangeUniformName, batchData.DistanceRange);
        material?.SetFloat(MsdfDistanceRangeMiddleUniformName, batchData.DistanceRangeMiddle);
        material?.SetFloat(MsdfFillBiasUniformName, batchData.MsdfFillBias);
        material?.SetInt(TextDebugModeUniformName, batchData.DebugMode);

        if (s_textRenderDiagCount++ < 40)
        {
            var firstEntry = batchData.Entries[0];
            var firstGlyph = firstEntry.GlyphCount > 0 ? firstEntry.Glyphs[0] : default;
            string projectedGlyph = GetProjectedGlyphSummary(in firstEntry.WorldMatrix, in firstGlyph.transform);
            Debug.Out(
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

        FinalizeAndUploadTextData(batchData, gpu, batchData.Atlas);

        var version = GetImmediateRenderVersion(gpu.Mesh!);
        version.Generate();
        if (!gpu.Mesh.TryPrepareForRendering())
        {
            if (s_textPrepareDiagCount++ < 40)
            {
                Debug.Out(
                    "[FpsTextDiag] UIBatchCollector.RenderTextGroup prepare pending #{0}: pass={1} group={2} totalGlyphs={3} debugMode={4}",
                    s_textPrepareDiagCount,
                    renderPass,
                    groupIndex,
                    batchData.TotalGlyphs,
                    batchData.DebugMode);
            }

            return;
        }

        DisableBatchCropping();
        try
        {
            gpu.Mesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batchData.TotalGlyphs);
        }
        finally
        {
            DisableBatchCropping();
        }
    }

    private unsafe void FinalizeAndUploadTextData(TextBatchData batchData, TextGPUResources gpu, XRTexture2D atlas)
    {
        uint totalGlyphs = (uint)batchData.TotalGlyphs;
        uint textCount = (uint)batchData.Entries.Count;

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
            gpu.TextInstanceBuf!.Resize(gpu.TextCapacity * 6);
            resized = true;
        }
        if (resized)
            gpu.NeedsPush = true;

        // Write data
        float* glyphTfmPtr = (float*)gpu.GlyphTransformsBuf!.ClientSideSource!.Address.Pointer;
        float* glyphUvPtr = (float*)gpu.GlyphTexCoordsBuf!.ClientSideSource!.Address.Pointer;
        float* textInstPtr = (float*)gpu.TextInstanceBuf!.ClientSideSource!.Address.Pointer;
        uint* glyphIdxPtr = (uint*)gpu.GlyphTextIndexBuf!.ClientSideSource!.Address.Pointer;

        uint glyphOffset = 0;
        for (int t = 0; t < batchData.Entries.Count; t++)
        {
            ref readonly var entry = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batchData.Entries)[t];

            // Write text instance data (6 vec4): matrix rows + color + bounds
            WriteMatrix4x4(ref textInstPtr, in entry.WorldMatrix);
            WriteVector4(ref textInstPtr, in entry.TextColor);
            WriteVector4(ref textInstPtr, in entry.UIXYWH);

            // Write glyph data
            for (int g = 0; g < entry.GlyphCount; g++)
            {
                var (transform, uvs) = entry.Glyphs[g];
                WriteVector4(ref glyphTfmPtr, in transform);
                WriteVector4(ref glyphUvPtr, in uvs);
                *glyphIdxPtr++ = (uint)t;
            }
            glyphOffset += (uint)entry.GlyphCount;
        }

        if (s_textUploadDiagCount++ < 40)
        {
            Debug.Out(
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

        // Push to GPU
        if (gpu.NeedsPush)
        {
            gpu.NeedsPush = false;
            gpu.GlyphTransformsBuf.PushData();
            gpu.GlyphTexCoordsBuf.PushData();
            gpu.TextInstanceBuf.PushData();
            gpu.GlyphTextIndexBuf.PushData();
        }
        else
        {
            gpu.GlyphTransformsBuf.PushSubData();
            gpu.GlyphTexCoordsBuf.PushSubData();
            gpu.TextInstanceBuf.PushSubData();
            gpu.GlyphTextIndexBuf.PushSubData();
        }
    }

    #endregion

    #region Helpers

    private static void DisableBatchCropping()
        => AbstractRenderer.Current?.SetCroppingEnabled(false);

    private static string GetProjectedGlyphSummary(in Matrix4x4 worldMatrix, in Vector4 glyphTransform)
    {
        var camera = Engine.Rendering.State.RenderingCamera;
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
        XRTexture2D fontAtlas,
        int atlasType,
        float distanceRange,
        float distanceRangeMiddle,
        float msdfFillBias,
        int debugMode)
        => state.ActiveKind == EBatchMarkerKind.Text &&
           ReferenceEquals(state.ActiveTextAtlas, fontAtlas) &&
           state.ActiveTextAtlasType == atlasType &&
           state.ActiveTextDistanceRange.Equals(distanceRange) &&
           state.ActiveTextDistanceRangeMiddle.Equals(distanceRangeMiddle) &&
           state.ActiveTextMsdfFillBias.Equals(msdfFillBias) &&
           state.ActiveTextDebugMode == debugMode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteMatrix4x4(ref float* ptr, in Matrix4x4 m)
    {
        // Write 16 floats (row-major layout matching C# Matrix4x4 memory order)
        *ptr++ = m.M11; *ptr++ = m.M12; *ptr++ = m.M13; *ptr++ = m.M14;
        *ptr++ = m.M21; *ptr++ = m.M22; *ptr++ = m.M23; *ptr++ = m.M24;
        *ptr++ = m.M31; *ptr++ = m.M32; *ptr++ = m.M33; *ptr++ = m.M34;
        *ptr++ = m.M41; *ptr++ = m.M42; *ptr++ = m.M43; *ptr++ = m.M44;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteVector4(ref float* ptr, in Vector4 v)
    {
        *ptr++ = v.X; *ptr++ = v.Y; *ptr++ = v.Z; *ptr++ = v.W;
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
        _matQuadTransformBuf?.Destroy();
        _matQuadColorBuf?.Destroy();
        _matQuadBoundsBuf?.Destroy();
        _matQuadMesh?.Destroy();

        foreach (var gpu in _textGPU.Values)
            gpu.Dispose();
        _textGPU.Clear();
    }

    #endregion
}
