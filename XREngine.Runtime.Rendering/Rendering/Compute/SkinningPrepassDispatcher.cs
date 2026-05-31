using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using State = XREngine.RuntimeEngine.Rendering.State;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Dispatches the skinning + blendshape compute pre-pass ahead of any viewport rendering.
/// </summary>
internal sealed class SkinningPrepassDispatcher : IDisposable
{
    private const string ShaderPath = "Compute/Animation/SkinningPrepass.comp";
    private const string InterleavedShaderPath = "Compute/Animation/SkinningPrepassInterleaved.comp";
    private const uint ThreadGroupSize = 256u;
    private const uint MaxComputeStorageBinding = 15u;

    private static readonly Lazy<SkinningPrepassDispatcher> _instance = new(() => new SkinningPrepassDispatcher());
    public static SkinningPrepassDispatcher Instance => _instance.Value;

    private readonly object _syncRoot = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, RendererResources> _resources = new();

    private XRShader? _shader;
    private XRRenderProgram? _program;
    private XRShader? _interleavedShader;
    private XRRenderProgram? _interleavedProgram;
    private XRDataBuffer? _emptySpillHeaders;
    private XRDataBuffer? _emptySpillEntries;

    private readonly GlobalSkinPaletteBuffers _globalInputs = new();

    private SkinningPrepassDispatcher()
    {
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _program?.Destroy();
            _shader?.Destroy();
            _interleavedProgram?.Destroy();
            _interleavedShader?.Destroy();
            _program = null;
            _shader = null;
            _interleavedProgram = null;
            _interleavedShader = null;
            _emptySpillHeaders?.Destroy();
            _emptySpillEntries?.Destroy();
            _emptySpillHeaders = null;
            _emptySpillEntries = null;

            _globalInputs.Dispose();
        }
    }

    /// <summary>
    /// Gets the skinned output buffers for a renderer after compute pass has run.
    /// Returns null if compute skinning is not enabled or buffers don't exist.
    /// </summary>
    public (XRDataBuffer? positions, XRDataBuffer? normals, XRDataBuffer? tangents, XRDataBuffer? interleaved) GetSkinnedBuffers(XRMeshRenderer renderer)
    {
        lock (_syncRoot)
        {
            if (_resources.TryGetValue(renderer, out var resources))
                return (resources.SkinnedPositions, resources.SkinnedNormals, resources.SkinnedTangents, resources.SkinnedInterleaved);
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Checks if a renderer has skinned output buffers available.
    /// </summary>
    public bool HasSkinnedBuffers(XRMeshRenderer renderer)
    {
        lock (_syncRoot)
        {
            if (!_resources.TryGetValue(renderer, out var resources))
                return false;
            return resources.SkinnedPositions is not null || resources.SkinnedInterleaved is not null;
        }
    }

    /// <summary>
    /// Executes the compute pre-pass for the supplied renderer if the current engine settings require it.
    /// Supports both interleaved and non-interleaved meshes with separate shader programs.
    /// </summary>
    public void Run(XRMeshRenderer renderer)
    {
        if (AbstractRenderer.Current is null)
            return;

        var mesh = renderer.Mesh;
        if (mesh is null || mesh.VertexCount <= 0)
            return;

        bool doSkinning = RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
            && mesh.HasSkinning
            && RuntimeEngine.Rendering.Settings.AllowSkinning;
        bool doBlendshapes = mesh.BlendshapeCount > 0
            && RuntimeEngine.Rendering.Settings.AllowBlendshapes
            && (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || doSkinning);
        if (!doSkinning && !doBlendshapes)
            return;

        if (doSkinning)
            mesh.EnsureComputeSkinningBuffers();

        if (doSkinning && !renderer.HasExternalSkinPaletteSource)
            renderer.EnsureSkinningBuffers(logWarnings: false);

        if (doSkinning)
            renderer.VerifyBonePaletteOrderMatchesMesh();
        if (doBlendshapes)
            renderer.EnsureBlendshapeBuffers(logWarnings: false);

        bool useExternalSkinPaletteSource = doSkinning && renderer.HasExternalSkinPaletteSource;
        bool usePackedGlobalSkinPalette = doSkinning
            && !useExternalSkinPaletteSource
            && RuntimeEngine.Rendering.Settings.UseGlobalSkinPaletteBufferForComputeSkinning
            && !renderer.HasGpuDrivenBoneSource;
        bool useSharedSkinPaletteBuffer = useExternalSkinPaletteSource || usePackedGlobalSkinPalette;
        bool useGlobalBlendWeights = doBlendshapes && RuntimeEngine.Rendering.Settings.UseGlobalBlendshapeWeightsBufferForComputeSkinning;

        bool isInterleaved = mesh.Interleaved;
        lock (_syncRoot)
        {
            // Avoid re-running the compute deformation for the same renderer more than once per global render frame.
            ulong frameId = State.RenderFrameId;

            _globalInputs.BeginFrame(frameId);

            XRRenderProgram? activeProgram;
            if (isInterleaved)
            {
                EnsureInterleavedProgram();
                activeProgram = _interleavedProgram;
            }
            else
            {
                EnsureProgram();
                activeProgram = _program;
            }

            if (activeProgram is null)
                return;

            RendererResources resources = _resources.GetValue(renderer, r => new RendererResources(r));

            if (resources.LastComputePrepassFrameId == frameId)
                return;

            if (!resources.Validate(mesh, doSkinning, doBlendshapes, isInterleaved))
                return;

            if (resources.CanReuseOutput(doSkinning, doBlendshapes))
            {
                resources.LastComputePrepassFrameId = frameId;
                RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                    0L,
                    0L,
                    skippedSkinningDispatches: doSkinning ? 1 : 0,
                    reusedSkinnedOutputBuffers: 1);
                return;
            }

            resources.LastComputePrepassFrameId = frameId;

            // One-shot: re-seed the per-renderer skin palette from the CURRENT bone render
            // state before the first real dispatch. The palette is initially seeded in the
            // renderer constructor (PopulateBoneMatrixBuffers), which can run before the bone
            // transforms have published their render matrices. Bones seeded stale at that point
            // are only corrected by RenderMatrixChanged events; a statically-posed skeleton with
            // no animation never re-fires those events, so the stale dispatch would otherwise be
            // cached and reused indefinitely (exploding a subset of meshes until a skinning
            // toggle forces a rebuild). This runs on the render thread where render matrices are
            // guaranteed valid, mirroring the toggle path (RefreshBoneMatricesFromRenderState).
            if (doSkinning && !useExternalSkinPaletteSource)
                resources.EnsureSeededFromRenderState();

            // Ensure this renderer's animation inputs are present in the global packed buffers (if enabled).
            // This may resize and/or re-upload global buffers.
            if (usePackedGlobalSkinPalette || useGlobalBlendWeights)
            {
                _globalInputs.EnsurePackedForRenderer(renderer, usePackedGlobalSkinPalette, useGlobalBlendWeights);
                _globalInputs.PushIfDirty(pushSkinPalette: usePackedGlobalSkinPalette, pushBlendshapeWeights: useGlobalBlendWeights);
            }

            resources.SyncDynamicBuffers(
                pushSkinPalette: !useSharedSkinPaletteBuffer,
                pushBlendshapeWeights: !useGlobalBlendWeights);

            uint skinPaletteBase = renderer.ActiveSkinPaletteBase;
            uint skinPaletteCount = renderer.ActiveSkinPaletteCount;
            XRDataBuffer? activeSkinPalette = renderer.ActiveSkinPaletteBuffer;
            if (usePackedGlobalSkinPalette && _globalInputs.TryGetSkinPaletteSlice(renderer, out uint packedBase, out uint packedCount))
            {
                skinPaletteBase = packedBase;
                skinPaletteCount = packedCount;
                activeSkinPalette = _globalInputs.GlobalSkinPalette;
            }

            uint blendBase = 0u;
            if (useGlobalBlendWeights && _globalInputs.TryGetBlendshapeWeightsSlice(renderer, out uint packedBlendBase, out _))
                blendBase = packedBlendBase;

            // Compute-skinning INPUT buffers (skin palette, core indices/weights, vertex positions,
            // spill, blendshape source data) upload to the GPU asynchronously across frames via the
            // upload queue. The compute dispatch binds them with SetBlockIndex, which does NOT force
            // the pending upload to complete (unlike BindSSBO). So the very first dispatch can read
            // not-yet-uploaded GPU memory (garbage bone indices and positions) and the corrupt result
            // is then latched by the output cache (CanReuseOutput) until a bone moves. Force the
            // read-only inputs fully resident here, synchronously, BEFORE binding/dispatching. Output
            // buffers are intentionally excluded: they are GPU-written and must not be re-uploaded.
            resources.EnsureSkinningInputsResident(mesh, activeSkinPalette, isInterleaved, doSkinning, doBlendshapes, useGlobalBlendWeights);

            try
            {
                if (doSkinning && !mesh.HasSpillInfluences)
                    EnsureEmptySpillBuffers();

                resources.BindBlocks(
                    doSkinning,
                    doBlendshapes,
                    isInterleaved,
                    useGlobalBlendWeights,
                    activeSkinPalette,
                    _globalInputs.GlobalBlendshapeWeights,
                    _emptySpillHeaders,
                    _emptySpillEntries);

                uint vertexCount = (uint)mesh.VertexCount;
                activeProgram.Uniform("vertexCount", vertexCount);
                activeProgram.Uniform("hasSkinning", doSkinning ? 1 : 0);
                activeProgram.Uniform("hasNormals", mesh.HasNormals ? 1 : 0);
                activeProgram.Uniform("hasTangents", mesh.HasTangents ? 1 : 0);
                activeProgram.Uniform("hasBlendshapes", doBlendshapes ? 1 : 0);
                activeProgram.Uniform("allowBlendshapes", RuntimeEngine.Rendering.Settings.AllowBlendshapes ? 1 : 0);
                activeProgram.Uniform("absoluteBlendshapePositions", RuntimeEngine.Rendering.Settings.UseAbsoluteBlendshapePositions ? 1 : 0);
                activeProgram.Uniform("maxBlendshapeAccumulation", mesh.MaxBlendshapeAccumulation ? 1 : 0);
                activeProgram.Uniform("useIntegerUniforms", RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders ? 1 : 0);
                activeProgram.Uniform("skinningCoreIndexFormat", (int)mesh.SkinningCoreIndexFormat);
                activeProgram.Uniform("hasSpillInfluences", mesh.HasSpillInfluences ? 1 : 0);
                activeProgram.Uniform("skinningInfluenceCap", renderer.ActiveSkinningInfluenceCap);

                // Global-packed animation input base offsets (0 when using per-renderer buffers).
                activeProgram.Uniform("skinPaletteBase", skinPaletteBase);
                activeProgram.Uniform("skinPaletteCount", skinPaletteCount);
                activeProgram.Uniform("blendshapeWeightBase", blendBase);

                // Set interleaved-specific uniforms
                if (isInterleaved)
                {
                    activeProgram.Uniform("interleavedStride", mesh.InterleavedStride);
                    activeProgram.Uniform("positionOffsetBytes", mesh.PositionOffset);
                    activeProgram.Uniform("normalOffsetBytes", mesh.NormalOffset ?? 0u);
                    activeProgram.Uniform("tangentOffsetBytes", mesh.TangentOffset ?? 0u);
                }

                uint groupsX = Math.Max(1u, (vertexCount + ThreadGroupSize - 1u) / ThreadGroupSize);
                activeProgram.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray);
                resources.MarkOutputValid(doSkinning, doBlendshapes);
                RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                    0L,
                    0L,
                    doSkinning ? 1 : 0,
                    doBlendshapes ? 1 : 0);
            }
            finally
            {
                ClearOpenGlComputeBindings();
            }
        }
    }

    private static void ClearOpenGlComputeBindings()
    {
        if (AbstractRenderer.Current is not OpenGLRenderer glRenderer)
            return;

        for (uint binding = 0u; binding <= MaxComputeStorageBinding; binding++)
            glRenderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, binding, 0);
    }

    private void EnsureEmptySpillBuffers()
    {
        _emptySpillHeaders ??= CreateEmptySpillBuffer("EmptyBoneInfluenceSpillHeaders");
        _emptySpillEntries ??= CreateEmptySpillBuffer("EmptyBoneInfluenceSpillEntries");
    }

    private static XRDataBuffer CreateEmptySpillBuffer(string name)
    {
        XRDataBuffer buffer = new(name, EBufferTarget.ShaderStorageBuffer, 1u, EComponentType.UInt, 1u, false, true)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false,
        };
        buffer.SetDataRawAtIndex(0u, 0u);
        return buffer;
    }

    public void RunVisible(RenderCommandCollection commands)
    {
        if (commands is null)
            return;

        if (!RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader && !RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader)
            return;

        var dispatched = new HashSet<XRMeshRenderer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var renderers = new List<XRMeshRenderer>();
        foreach (var meshCmd in commands.EnumerateRenderingMeshCommands())
        {
            if (meshCmd.Mesh is not XRMeshRenderer renderer)
                continue;

            if (!dispatched.Add(renderer))
                continue;

            renderers.Add(renderer);
        }

        if (renderers.Count == 0)
            return;

        // Optionally pre-pack all visible renderers into global buffers for this frame.
        bool globalSkinPalette = RuntimeEngine.Rendering.Settings.UseGlobalSkinPaletteBufferForComputeSkinning;
        bool globalBlend = RuntimeEngine.Rendering.Settings.UseGlobalBlendshapeWeightsBufferForComputeSkinning;
        if (globalSkinPalette || globalBlend)
        {
            lock (_syncRoot)
            {
                _globalInputs.BeginFrame(State.RenderFrameId);
                bool anyChanged = false;
                foreach (var renderer in renderers)
                {
                    var mesh = renderer.Mesh;
                    if (mesh is null)
                        continue;

                    bool skinningInCompute = RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
                        && mesh.HasSkinning
                        && RuntimeEngine.Rendering.Settings.AllowSkinning;
                    bool needsSkinPalette = globalSkinPalette
                        && !renderer.HasExternalSkinPaletteSource
                        && !renderer.HasGpuDrivenBoneSource
                        && skinningInCompute;
                    bool needsBlend = globalBlend
                        && mesh.BlendshapeCount > 0
                        && RuntimeEngine.Rendering.Settings.AllowBlendshapes
                        && (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || skinningInCompute);
                    if (!needsSkinPalette && !needsBlend)
                        continue;

                    anyChanged |= _globalInputs.EnsurePackedForRenderer(renderer, needsSkinPalette, needsBlend);
                }

                if (anyChanged)
                    _globalInputs.PushIfDirty(pushSkinPalette: globalSkinPalette, pushBlendshapeWeights: globalBlend);
            }
        }

        foreach (var renderer in renderers)
            Run(renderer);
    }

    private void EnsureProgram()
    {
        if (_program is not null)
            return;

        _shader ??= ShaderHelper.LoadEngineShader(ShaderPath, EShaderType.Compute);
        _program = _shader is null ? null : new XRRenderProgram(true, false, _shader);
    }

    private void EnsureInterleavedProgram()
    {
        if (_interleavedProgram is not null)
            return;

        _interleavedShader ??= ShaderHelper.LoadEngineShader(InterleavedShaderPath, EShaderType.Compute);
        _interleavedProgram = _interleavedShader is null ? null : new XRRenderProgram(true, false, _interleavedShader);
    }

    private sealed class RendererResources(XRMeshRenderer renderer)
    {
        private readonly XRMeshRenderer _renderer = renderer;
        private bool _seededFromRenderState;
        private XRMesh? _lastMesh;

        /// <summary>
        /// Re-seeds the per-renderer skin palette from current bone render state exactly once,
        /// before the first compute dispatch. See call site in <see cref="Run"/> for rationale.
        /// </summary>
        public void EnsureSeededFromRenderState()
        {
            if (_seededFromRenderState)
                return;
            _seededFromRenderState = true;
            _renderer.LogBoneSeedStalenessDiagnostics("FirstDispatch");
            _renderer.RefreshBoneMatricesFromRenderState();
        }

        private bool _residencyLogged;

        /// <summary>
        /// Forces every read-only compute-skinning INPUT buffer required for this dispatch to be
        /// fully GPU-resident (generated, pending async upload flushed, storage allocated) before
        /// the dispatch binds and reads them. This mirrors what <c>BindSSBO</c> already does, but
        /// the skinning dispatcher binds via <c>SetBlockIndex</c>, which skips that guard. Output
        /// buffers are deliberately NOT touched here: they are written by the compute shader and
        /// re-uploading them would clobber the skinned results with stale client data.
        /// </summary>
        public void EnsureSkinningInputsResident(
            XRMesh mesh,
            XRDataBuffer? skinPalette,
            bool isInterleaved,
            bool doSkinning,
            bool doBlendshapes,
            bool useGlobalBlendWeights)
        {
            // Source vertex data (read-only inputs).
            if (isInterleaved)
                EnsureBufferResident(mesh.InterleavedVertexBuffer);
            else
            {
                EnsureBufferResident(mesh.PositionsBuffer);
                EnsureBufferResident(mesh.NormalsBuffer);
                EnsureBufferResident(mesh.TangentsBuffer);
            }

            if (doSkinning)
            {
                EnsureBufferResident(skinPalette);
                EnsureBufferResident(mesh.BoneInfluenceCoreIndices);
                EnsureBufferResident(mesh.BoneInfluenceCoreWeights);
                if (mesh.HasSpillInfluences)
                {
                    EnsureBufferResident(mesh.BoneInfluenceSpillHeaders);
                    EnsureBufferResident(mesh.BoneInfluenceSpillEntries);
                }
            }

            if (doBlendshapes)
            {
                EnsureBufferResident(mesh.BlendshapeCounts);
                EnsureBufferResident(mesh.BlendshapeIndices);
                EnsureBufferResident(mesh.BlendshapeDeltas);
                if (!useGlobalBlendWeights)
                    EnsureBufferResident(_renderer.BlendshapeWeights);
            }

            LogBufferResidencyOnce(mesh, skinPalette, isInterleaved);
        }

        /// <summary>
        /// If the buffer's GPU wrapper has not finished uploading, force the upload to complete now
        /// so the data is GPU-resident before the compute shader reads it. Null / already-resident
        /// buffers are a no-op.
        /// </summary>
        private static void EnsureBufferResident(XRDataBuffer? buffer)
        {
            if (buffer is null)
                return;
            foreach (var wrapper in buffer.APIWrappers)
            {
                if (wrapper is OpenGLRenderer.GLDataBuffer gl && !gl.IsReadyForRendering)
                    gl.EnsureStorageAllocatedForGpuCopy();
            }
        }

        /// <summary>
        /// One-shot diagnostic: logs whether the skinning compute INPUT buffers are GPU-resident
        /// at the first dispatch (after <see cref="EnsureSkinningInputsResident"/> has run, so they
        /// should all read 'ready' if the residency fix is working).
        /// </summary>
        public void LogBufferResidencyOnce(XRMesh mesh, XRDataBuffer? skinPalette, bool isInterleaved)
        {
            if (_residencyLogged)
                return;
            _residencyLogged = true;

            XRDataBuffer? positions = isInterleaved ? mesh.InterleavedVertexBuffer : mesh.PositionsBuffer;
            Debug.LogWarning(
                $"[SkinResidency] Mesh='{mesh.Name ?? "<null>"}' verts={mesh.VertexCount} " +
                $"palette={ResidencyState(skinPalette)} coreIdx={ResidencyState(mesh.BoneInfluenceCoreIndices)} " +
                $"coreWt={ResidencyState(mesh.BoneInfluenceCoreWeights)} pos={ResidencyState(positions)} " +
                $"spillHdr={ResidencyState(mesh.BoneInfluenceSpillHeaders)} spillEnt={ResidencyState(mesh.BoneInfluenceSpillEntries)} " +
                $"hasSpill={mesh.HasSpillInfluences}");
        }

        private static string ResidencyState(XRDataBuffer? buffer)
        {
            if (buffer is null)
                return "null";
            foreach (var wrapper in buffer.APIWrappers)
                if (wrapper is OpenGLRenderer.GLDataBuffer gl)
                    return gl.IsReadyForRendering ? "ready" : "PENDING";
            return "NOWRAP";
        }
        private int _lastVertexCount;
        private bool _lastWasInterleaved;
        private bool _hasValidOutput;
        private bool _lastDidSkinning;
        private bool _lastDidBlendshapes;
        private ulong _lastOutputVersion;

        public ulong LastComputePrepassFrameId;

        /// <summary>
        /// Gets the output buffer containing skinned positions.
        /// </summary>
        public XRDataBuffer? SkinnedPositions => _renderer.SkinnedPositionsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned normals.
        /// </summary>
        public XRDataBuffer? SkinnedNormals => _renderer.SkinnedNormalsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned tangents.
        /// </summary>
        public XRDataBuffer? SkinnedTangents => _renderer.SkinnedTangentsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned interleaved data.
        /// </summary>
        public XRDataBuffer? SkinnedInterleaved => _renderer.SkinnedInterleavedBuffer;

        public bool Validate(XRMesh mesh, bool doSkinning, bool doBlendshapes, bool isInterleaved)
        {
            if (doSkinning)
            {
                if (_renderer.ActiveSkinPaletteBuffer is null)
                    return false;
                if (!mesh.SupportsComputeSkinning)
                    return false;
            }

            if (doBlendshapes)
            {
                if (mesh.BlendshapeCounts is null || mesh.BlendshapeIndices is null || mesh.BlendshapeDeltas is null)
                    return false;
                if (_renderer.BlendshapeWeights is null)
                    return false;
            }

            // Check required buffers based on interleaved mode
            if (isInterleaved)
            {
                if (mesh.InterleavedVertexBuffer is null)
                    return false;
            }
            else
            {
                if (mesh.PositionsBuffer is null)
                    return false;
            }

            // Ensure output buffers exist and match the mesh size
            EnsureOutputBuffers(mesh, isInterleaved);

            return true;
        }

        public bool CanReuseOutput(bool doSkinning, bool doBlendshapes)
        {
            if (!_hasValidOutput)
                return false;
            if (_renderer.SkinnedOutputDirty)
                return false;
            if (_lastDidSkinning != doSkinning || _lastDidBlendshapes != doBlendshapes)
                return false;
            if (_renderer.HasPendingComputeSkinningInputChanges)
                return false;
            if (_lastOutputVersion != _renderer.SkinnedOutputVersion)
                return false;
            if (doSkinning && (_renderer.HasExternalSkinPaletteSource || _renderer.HasGpuDrivenBoneSource))
                return false;
            return doSkinning || doBlendshapes;
        }

        public void MarkOutputValid(bool doSkinning, bool doBlendshapes)
        {
            _hasValidOutput = true;
            _lastDidSkinning = doSkinning;
            _lastDidBlendshapes = doBlendshapes;
            _lastOutputVersion = _renderer.SkinnedOutputVersion;
            _renderer.MarkSkinnedOutputClean();
        }

        private void EnsureOutputBuffers(XRMesh mesh, bool isInterleaved)
        {
            int vertexCount = mesh.VertexCount;

            // Also verify the output buffers still exist — they may have been
            // destroyed externally (e.g. GLMeshRenderer.DestroySkinnedBuffers
            // when the compute-skinning toggle is flipped off and back on).
            bool buffersExist = isInterleaved
                ? _renderer.SkinnedInterleavedBuffer is not null
                : _renderer.SkinnedPositionsBuffer is not null;

            if (buffersExist && ReferenceEquals(_lastMesh, mesh) && _lastVertexCount == vertexCount && _lastWasInterleaved == isInterleaved)
                return;

            _lastMesh = mesh;
            _lastVertexCount = vertexCount;
            _lastWasInterleaved = isInterleaved;

            // Dispose old buffers on renderer
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;
            _hasValidOutput = false;
            _seededFromRenderState = false;
            _renderer.MarkSkinnedOutputDirty();

            if (isInterleaved)
            {
                // Create a single interleaved output buffer matching the input format
                // Use resizable=true so compute shader can write to it with mutable storage
                int stride = (int)mesh.InterleavedStride;
                _renderer.SkinnedInterleavedBuffer = new XRDataBuffer(
                    "InterleavedSkinned",
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)(vertexCount * stride / sizeof(float)),
                    EComponentType.Float,
                    1, // Individual floats, stride handled at binding time
                    true, // Resizable - uses mutable storage that compute shaders can write to
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };
            }
            else
            {
                // Create separate output buffers for positions, normals, tangents
                // Use ShaderStorageBuffer target and resizable=true so the compute shader can write to them
                _renderer.SkinnedPositionsBuffer = new XRDataBuffer(
                    ECommonBufferType.Position.ToString(),
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)vertexCount,
                    EComponentType.Float,
                    4, // vec4
                    true, // Resizable - uses mutable storage that compute shaders can write to
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };

                if (mesh.HasNormals)
                {
                    _renderer.SkinnedNormalsBuffer = new XRDataBuffer(
                        ECommonBufferType.Normal.ToString(),
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4, // vec4
                        true, // Resizable - uses mutable storage that compute shaders can write to
                        false)
                    {
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
                }

                if (mesh.HasTangents)
                {
                    _renderer.SkinnedTangentsBuffer = new XRDataBuffer(
                        ECommonBufferType.Tangent.ToString(),
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4, // vec4
                        true, // Resizable - uses mutable storage that compute shaders can write to
                        false)
                    {
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
                }
            }
        }

        public void SyncDynamicBuffers(bool pushSkinPalette, bool pushBlendshapeWeights)
        {
            if (pushSkinPalette)
                _renderer.PushBoneMatricesToGPU();
            if (pushBlendshapeWeights)
                _renderer.PushBlendshapeWeightsToGPU();
        }

        public void BindBlocks(
            bool doSkinning,
            bool doBlendshapes,
            bool isInterleaved,
            bool useGlobalBlendshapeWeights,
            XRDataBuffer? skinPalette,
            XRDataBuffer? globalBlendshapeWeights,
            XRDataBuffer? emptySpillHeaders,
            XRDataBuffer? emptySpillEntries)
        {
            var mesh = _renderer.Mesh;
            if (mesh is null)
                return;

            if (isInterleaved)
            {
                // Bind interleaved input/output buffers
                mesh.InterleavedVertexBuffer?.SetBlockIndex(8);
                _renderer.SkinnedInterleavedBuffer?.SetBlockIndex(9);
            }
            else
            {
                // Bind input buffers (original mesh data) - read-only
                mesh.PositionsBuffer?.SetBlockIndex(8);
                mesh.NormalsBuffer?.SetBlockIndex(9);
                mesh.TangentsBuffer?.SetBlockIndex(10);

                // Bind output buffers (skinned results) - write-only
                _renderer.SkinnedPositionsBuffer?.SetBlockIndex(11);
                _renderer.SkinnedNormalsBuffer?.SetBlockIndex(12);
                _renderer.SkinnedTangentsBuffer?.SetBlockIndex(15);
            }

            if (doSkinning)
            {
                skinPalette?.SetBlockIndex(0);
                mesh.BoneInfluenceCoreIndices?.SetBlockIndex(2);
                mesh.BoneInfluenceCoreWeights?.SetBlockIndex(3);

                XRDataBuffer? spillHeaders = mesh.HasSpillInfluences ? mesh.BoneInfluenceSpillHeaders : emptySpillHeaders;
                XRDataBuffer? spillEntries = mesh.HasSpillInfluences ? mesh.BoneInfluenceSpillEntries : emptySpillEntries;

                // Spill influence buffers have different bindings for interleaved vs non-interleaved.
                if (isInterleaved)
                {
                    spillHeaders?.SetBlockIndex(10);
                    spillEntries?.SetBlockIndex(11);
                }
                else
                {
                    spillHeaders?.SetBlockIndex(13);
                    spillEntries?.SetBlockIndex(14);
                }
            }

            if (doBlendshapes)
            {
                mesh.BlendshapeCounts?.SetBlockIndex(4);
                mesh.BlendshapeIndices?.SetBlockIndex(5);
                mesh.BlendshapeDeltas?.SetBlockIndex(6);
                if (useGlobalBlendshapeWeights)
                    globalBlendshapeWeights?.SetBlockIndex(7);
                else
                    _renderer.BlendshapeWeights?.SetBlockIndex(7);
            }
        }

        public void Dispose()
        {
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;
            _lastVertexCount = 0;
            _lastWasInterleaved = false;
            _lastMesh = null;
            _hasValidOutput = false;
            _seededFromRenderState = false;
            _lastDidSkinning = false;
            _lastDidBlendshapes = false;
            _lastOutputVersion = 0;
        }
    }
}
