using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
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
    #region Batch Entry Types

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
        public readonly List<TextEntry> Entries = [];
        public int TotalGlyphs;

        public void Clear()
        {
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

    #endregion

    #region Double-buffered Batch Lists

    // Collecting side (written on collect-visible thread)
    private Dictionary<int, MaterialQuadBatchData> _collectMaterialQuads = [];
    private Dictionary<int, Dictionary<XRTexture2D, TextBatchData>> _collectTextData = [];

    // Rendering side (read on render thread)
    private Dictionary<int, MaterialQuadBatchData> _renderMaterialQuads = [];
    private Dictionary<int, Dictionary<XRTexture2D, TextBatchData>> _renderTextData = [];

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
            foreach (var kv in _renderMaterialQuads)
                if (kv.Value.Entries.Count > 0) return true;
            foreach (var kv in _renderTextData)
                foreach (var kv2 in kv.Value)
                    if (kv2.Value.Entries.Count > 0) return true;
            return false;
        }
    }

    #endregion

    #region Collect-Visible Thread API

    /// <summary>
    /// Register a material quad for batched rendering.
    /// Called from the collect-visible thread.
    /// </summary>
    public void AddMaterialQuad(int renderPass, in Matrix4x4 worldMatrix, in Vector4 color, in Vector4 uixywh)
    {
        if (!_collectMaterialQuads.TryGetValue(renderPass, out var batch))
        {
            batch = new MaterialQuadBatchData();
            _collectMaterialQuads[renderPass] = batch;
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
    /// <param name="fontAtlas">The font atlas texture used by this text.</param>
    /// <param name="worldMatrix">The text component's world matrix.</param>
    /// <param name="textColor">The text color.</param>
    /// <param name="uixywh">The UI bounds (x, y, w, h).</param>
    /// <param name="glyphs">Snapshot of glyph layout data.</param>
    public void AddTextQuad(
        int renderPass,
        XRTexture2D fontAtlas,
        in Matrix4x4 worldMatrix,
        in Vector4 textColor,
        in Vector4 uixywh,
        (Vector4 transform, Vector4 uvs)[] glyphs)
    {
        if (!_collectTextData.TryGetValue(renderPass, out var atlasMap))
        {
            atlasMap = [];
            _collectTextData[renderPass] = atlasMap;
        }
        if (!atlasMap.TryGetValue(fontAtlas, out var batch))
        {
            batch = new TextBatchData();
            atlasMap[fontAtlas] = batch;
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
    }

    #endregion

    #region Swap Buffers

    /// <summary>
    /// Swaps collecting and rendering batch data.
    /// Call between collect-visible and render, alongside <see cref="Rendering.Commands.RenderCommandCollection.SwapBuffers"/>.
    /// </summary>
    public void SwapBuffers()
    {
        // Swap material quads
        (_collectMaterialQuads, _renderMaterialQuads) = (_renderMaterialQuads, _collectMaterialQuads);

        // Swap text data
        (_collectTextData, _renderTextData) = (_renderTextData, _collectTextData);

        // Clear collecting side for next frame
        foreach (var batch in _collectMaterialQuads.Values)
            batch.Clear();
        foreach (var atlasMap in _collectTextData.Values)
            foreach (var batch in atlasMap.Values)
                batch.Clear();
    }

    /// <summary>
    /// Clears both collecting and rendering-side batch lists immediately.
    /// Useful when toggling between batched and strict per-item rendering modes.
    /// </summary>
    public void Clear()
    {
        foreach (var batch in _collectMaterialQuads.Values)
            batch.Clear();
        foreach (var batch in _renderMaterialQuads.Values)
            batch.Clear();

        foreach (var atlasMap in _collectTextData.Values)
            foreach (var batch in atlasMap.Values)
                batch.Clear();

        foreach (var atlasMap in _renderTextData.Values)
            foreach (var batch in atlasMap.Values)
                batch.Clear();
    }

    #endregion

    #region Render Thread API

    public void Render(int renderPass)
    {
        if (!Enabled)
            return;

        RenderMaterialQuadBatch(renderPass);
        RenderTextBatch(renderPass);
    }
    
    /// <summary>
    /// Renders all batched material quads for the given render pass.
    /// Call from the render thread.
    /// </summary>
    public void RenderMaterialQuadBatch(int renderPass)
    {
        if (!_renderMaterialQuads.TryGetValue(renderPass, out var batch) || batch.Entries.Count == 0)
            return;

        using var sample = Engine.Profiler.Start();

        EnsureMaterialQuadMesh();
        UploadMaterialQuadData(batch.Entries);

        // Force-generate the mesh renderer to bypass any deferred generation.
        // This ensures the first frame renders immediately (matches the text batch path).
        var version = _matQuadMesh!.GetDefaultVersion();
        version.Generate();

        _matQuadMesh!.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batch.Entries.Count);
    }

    /// <summary>
    /// Renders all batched text quads for the given render pass.
    /// Issues one instanced draw per font atlas.
    /// Call from the render thread.
    /// </summary>
    public void RenderTextBatch(int renderPass)
    {
        if (!_renderTextData.TryGetValue(renderPass, out var atlasMap))
            return;

        using var sample = Engine.Profiler.Start();

        foreach (var (atlas, batchData) in atlasMap)
        {
            if (batchData.Entries.Count == 0 || batchData.TotalGlyphs == 0)
                continue;

            var gpu = EnsureTextGPUResources(atlas);
            FinalizeAndUploadTextData(batchData, gpu, atlas);

            // Force-generate the mesh renderer to bypass any deferred generation.
            // This ensures the first frame renders immediately.
            var version = gpu.Mesh!.GetDefaultVersion();
            version.Generate();

            gpu.Mesh!.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)batchData.TotalGlyphs);
        }
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
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
            }
        };

        _matQuadMesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material);

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
        XRShader fragmentShader = XRShader.EngineShader(
            Path.Combine("Common", "UITextBatched.fs"), EShaderType.Fragment);

        var material = new XRMaterial(Array.Empty<ShaderVar>(), [fontAtlas], [vertexShader, fragmentShader])
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
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
            }
        };

        gpu.Mesh = new XRMeshRenderer(
            XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
            material);

        DisableShaderPipelines(gpu.Mesh);

        gpu.GlyphCapacity = 256;
        gpu.TextCapacity = 32;
        CreateTextBuffers(gpu);
        gpu.NeedsPush = true; // Ensure first upload allocates GPU buffers via PushData

        _textGPU[fontAtlas] = gpu;
        return gpu;
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
