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

        if (doSkinning && RenderDiagnosticsFlags.SkinningPrepassDiag)
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
                resources.EnsureSeededFromRenderState(mesh, isInterleaved, doSkinning, doBlendshapes, useGlobalBlendWeights);

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
            // read-only inputs fully resident here, synchronously, BEFORE binding/dispatching.
            resources.EnsureSkinningInputsResident(mesh, activeSkinPalette, isInterleaved, doSkinning, doBlendshapes, useGlobalBlendWeights);

            // OUTPUT buffers (skinned positions/normals/tangents, or interleaved) are created
            // client-side in EnsureOutputBuffers but their GPU storage allocates lazily -- normally
            // not until the DRAW binds them, which happens AFTER this compute dispatch. The dispatch
            // binds the output via SetBlockIndex(11), which (like the inputs) does NOT force storage
            // allocation. So the compute shader writes its skinned positions into a zero-byte GPU
            // buffer (the result is discarded), and the subsequent draw then allocates the storage
            // initialized to the buffer's zeroed client data -> every vertex reads (0,0,0) -> the
            // mesh collapses to a degenerate point and renders as MISSING (whole mesh, or missing
            // triangles when only partially allocated). Because the output cache latches after the
            // first dispatch, it stays missing until a bone move forces a re-dispatch. Force the
            // output storage allocated HERE, before the dispatch, so the compute writes into real
            // storage that the draw then reads. This only allocates (and pushes the zeroed client
            // data once); once allocated it never re-pushes, so it cannot clobber compute results.
            resources.EnsureSkinningOutputResident(isInterleaved, doSkinning);

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
                if (doSkinning && RenderDiagnosticsFlags.SkinningPrepassDiag)
                    resources.DebugReadbackSkinnedOutput(mesh);
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
        private bool _seedInputsSettled;
        private int _lastSeedPoseHash;
        private int _reseedCount;
        private int _poseChangeCount;
        private bool _settleLogged;
        private XRMesh? _lastMesh;

        /// <summary>
        /// Re-seeds the per-renderer skin palette from current bone render state, re-pushing every
        /// utilized bone matrix as if it had just moved (which also invalidates the cached skinned
        /// output). See call site in <see cref="Run"/> for rationale.
        /// </summary>
        public void EnsureSeededFromRenderState(
            XRMesh mesh,
            bool isInterleaved,
            bool doSkinning,
            bool doBlendshapes,
            bool useGlobalBlendWeights)
        {
            // The first compute dispatch can run before the mesh's asynchronously-uploaded INPUT
            // buffers have finished uploading AND before the runtime-imported skeleton has settled
            // into its final pose. A runtime avatar publishes several frames of intermediate bone
            // poses at startup; if this renderer subscribed to RenderMatrixChanged after the final
            // pose was already published (init-order race), no further event fires. For a static
            // (non-animated) mesh that means the very first dispatch -- taken at an intermediate
            // pose and/or with not-yet-resident inputs -- gets latched by the output cache
            // (MarkOutputValid) and reused indefinitely, leaving the mesh visibly wrong ("exploded")
            // until a bone is MANUALLY moved (which fires RenderMatrixChanged -> re-dispatch -> fix).
            //
            // Fix: keep re-seeding (re-push ALL bone matrices as if they moved, which also marks the
            // skinned output dirty so it recomputes) on every dispatch until BOTH (a) the inputs are
            // observed naturally GPU-resident and (b) the bone pose hash is stable across two
            // consecutive frames. Only then do we let the output cache hold. This deterministically
            // captures the final settled pose without permanently re-dispatching.
            if (_seededFromRenderState && _seedInputsSettled)
                return;

            if (!_seededFromRenderState)
                _renderer.LogBoneSeedStalenessDiagnostics("FirstDispatch");

            int poseHash = _renderer.ComputeCurrentBonePoseHash();
            bool poseStable = _seededFromRenderState && poseHash == _lastSeedPoseHash;
            if (_seededFromRenderState && poseHash != _lastSeedPoseHash)
                _poseChangeCount++;

            bool inputsResident = AreSkinningInputsNaturallyResident(
                mesh, isInterleaved, doSkinning, doBlendshapes, useGlobalBlendWeights);

            _renderer.RefreshBoneMatricesFromRenderState();
            _reseedCount++;
            _lastSeedPoseHash = poseHash;
            _seededFromRenderState = true;
            // Settle only once the inputs are resident AND the pose stopped changing; the recompute
            // we just triggered therefore uses the final input data and final pose.
            _seedInputsSettled = inputsResident && poseStable;

            if (_seedInputsSettled && !_settleLogged)
            {
                _settleLogged = true;
                if (RenderDiagnosticsFlags.SkinningPrepassDiag)
                    Debug.LogWarning(
                        $"[SkinSettle] Mesh='{mesh.Name ?? "<null>"}' verts={mesh.VertexCount} settled after " +
                        $"{_reseedCount} reseeds, {_poseChangeCount} pose change(s) after first dispatch.");
            }
        }

        /// <summary>
        /// Returns true only if every read-only compute-skinning INPUT buffer for this dispatch is
        /// already GPU-resident WITHOUT forcing the upload (i.e. its async upload genuinely finished).
        /// Used to decide when the seed/output state has settled.
        /// </summary>
        private bool AreSkinningInputsNaturallyResident(
            XRMesh mesh,
            bool isInterleaved,
            bool doSkinning,
            bool doBlendshapes,
            bool useGlobalBlendWeights)
        {
            if (isInterleaved)
            {
                if (!IsBufferNaturallyResident(mesh.InterleavedVertexBuffer))
                    return false;
            }
            else
            {
                if (!IsBufferNaturallyResident(mesh.PositionsBuffer)
                    || !IsBufferNaturallyResident(mesh.NormalsBuffer)
                    || !IsBufferNaturallyResident(mesh.TangentsBuffer))
                    return false;
            }

            if (doSkinning)
            {
                if (!IsBufferNaturallyResident(mesh.BoneInfluenceCoreIndices)
                    || !IsBufferNaturallyResident(mesh.BoneInfluenceCoreWeights))
                    return false;
                if (mesh.HasSpillInfluences
                    && (!IsBufferNaturallyResident(mesh.BoneInfluenceSpillHeaders)
                        || !IsBufferNaturallyResident(mesh.BoneInfluenceSpillEntries)))
                    return false;
            }

            if (doBlendshapes)
            {
                if (!IsBufferNaturallyResident(mesh.BlendshapeCounts)
                    || !IsBufferNaturallyResident(mesh.BlendshapeIndices)
                    || !IsBufferNaturallyResident(mesh.BlendshapeDeltas))
                    return false;
                if (!useGlobalBlendWeights && !IsBufferNaturallyResident(_renderer.BlendshapeWeights))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True if the buffer is null (nothing to upload) or its GPU wrapper reports ready WITHOUT
        /// forcing the upload to complete.
        /// </summary>
        private static bool IsBufferNaturallyResident(XRDataBuffer? buffer)
        {
            if (buffer is null)
                return true;
            foreach (var wrapper in buffer.APIWrappers)
                if (wrapper is OpenGLRenderer.GLDataBuffer gl && !gl.IsReadyForRendering)
                    return false;
            return true;
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
        /// Forces the compute-skinning OUTPUT buffer(s) to have GPU storage allocated BEFORE the
        /// dispatch writes to them. Their storage otherwise allocates lazily at draw time (after the
        /// compute pass), so the compute shader would write into a zero-byte buffer and the draw
        /// would then read zero-initialized client data -> degenerate (missing) geometry. Unlike the
        /// inputs, this is safe to call every dispatch: <see cref="GLDataBuffer.EnsureStorageAllocatedForGpuCopy"/>
        /// only pushes client data while it has not yet been fully pushed, so once the storage exists
        /// it becomes a no-op and never clobbers the compute-written results.
        /// </summary>
        public void EnsureSkinningOutputResident(bool isInterleaved, bool doSkinning)
        {
            if (!doSkinning)
                return;

            if (isInterleaved)
                EnsureBufferResident(_renderer.SkinnedInterleavedBuffer);
            else
            {
                EnsureBufferResident(_renderer.SkinnedPositionsBuffer);
                EnsureBufferResident(_renderer.SkinnedNormalsBuffer);
                EnsureBufferResident(_renderer.SkinnedTangentsBuffer);
            }
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
            if (_residencyLogged || !RenderDiagnosticsFlags.SkinningPrepassDiag)
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

        private sealed class ReadbackState
        {
            public int LoggedCount;
            public int DispatchCount;
            public float LastMinX = float.NaN, LastMaxX, LastMinY, LastMaxY, LastMinZ, LastMaxZ;
            public bool HasLast;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XRMesh, ReadbackState> _readbackByMesh = new();
        private const int ReadbackMaxLogsPerMesh = 60;
        private const float ReadbackSignificantDelta = 3f;

        /// <summary>
        /// Decisive evidence step: reads the ACTUAL compute-skinned position output back from the
        /// GPU right after a dispatch and compares it against the GPU INPUT positions. The user
        /// reports a manual bone move FIXES an exploded mesh, so routine identical frames are NOT
        /// logged (they exhausted the cap during load before): we log the first few dispatches and
        /// then only when bounds move significantly (a real break or fix), up to a high cap, so the
        /// post-move state is always captured. Keyed per mesh instance (mesh.Name is null here).
        /// </summary>
        public unsafe void DebugReadbackSkinnedOutput(XRMesh mesh)
        {
            var st = _readbackByMesh.GetValue(mesh, static _ => new ReadbackState());
            st.DispatchCount++;
            if (st.LoggedCount >= ReadbackMaxLogsPerMesh)
                return;
            var buf = _renderer.SkinnedPositionsBuffer;
            if (buf is null)
            {
                if (st.LoggedCount < 3)
                {
                    st.LoggedCount++;
                    Debug.LogWarning(
                        $"[SkinReadback] *** NO-OUTPUT-BUFFER *** verts={mesh.VertexCount} SkinnedPositionsBuffer is null " +
                        $"(skinned draw will read stale/unskinned data -> may render exploded).");
                }
                return;
            }
            if (AbstractRenderer.Current is not OpenGLRenderer glRenderer)
                return;

            uint id = 0u;
            foreach (var wrapper in buf.APIWrappers)
                if (wrapper is OpenGLRenderer.GLDataBuffer gl && gl.TryGetBindingId(out id))
                    break;
            if (id == 0u)
            {
                if (st.LoggedCount < 3)
                {
                    st.LoggedCount++;
                    Debug.LogWarning(
                        $"[SkinReadback] *** NO-OUTPUT-GLID *** verts={mesh.VertexCount} SkinnedPositionsBuffer has no GL binding id " +
                        $"(output not yet GPU-resident; skinned draw may read garbage).");
                }
                return;
            }

            var rawGl = glRenderer.RawGL;
            // Ensure the compute shader's writes are visible to this client read.
            rawGl.MemoryBarrier((uint)(GLEnum.BufferUpdateBarrierBit | GLEnum.ShaderStorageBarrierBit));

            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, id);
            // Query the ACTUAL allocated GPU byte size. If this is smaller than vertexCount*16,
            // the output buffer is under-allocated -> compute writes/draw reads run out of range.
            rawGl.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out int gpuBytes);
            long expectedBytes = (long)mesh.VertexCount * 4L * sizeof(float);

            int maxSampleVerts = gpuBytes > 0 ? (int)(gpuBytes / (4 * sizeof(float))) : 0;
            int sample = Math.Min(Math.Min(mesh.VertexCount, 512), maxSampleVerts);
            if (sample <= 0)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                st.LoggedCount++;
                Debug.LogWarning(
                    $"[SkinReadback] *** NO-GPU-DATA *** verts={mesh.VertexCount} gpuBytes={gpuBytes} expectedBytes={expectedBytes}.");
                return;
            }

            float[] data = new float[sample * 4];
            // GetBufferSubData(target) reads the buffer bound to target; bind explicitly first.
            fixed (float* p = data)
                rawGl.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, (nuint)(sample * 4 * sizeof(float)), p);
            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

            bool underAllocated = gpuBytes < expectedBytes;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int nan = 0;
            for (int i = 0; i < sample; i++)
            {
                float x = data[i * 4], y = data[i * 4 + 1], z = data[i * 4 + 2];
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                {
                    nan++;
                    continue;
                }
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            // Direct explosion signals that the span-ratio test cannot see: NaN/Inf output (NaN
            // values are excluded from min/max, and `NaN > threshold` is always false) and any
            // sampled coordinate with an absurd magnitude. Either is an unambiguous corruption.
            bool hugeAbs = (minX != float.MaxValue)
                && (MathF.Abs(minX) > 1000f || MathF.Abs(maxX) > 1000f
                    || MathF.Abs(minY) > 1000f || MathF.Abs(maxY) > 1000f
                    || MathF.Abs(minZ) > 1000f || MathF.Abs(maxZ) > 1000f);
            bool directExplosion = nan > 0 || hugeAbs;

            // Significant change vs the last LOGGED bounds = a real break or a real fix (e.g. the
            // user's bone move), as opposed to per-frame animation jitter which we suppress.
            bool significant = !st.HasLast
                || MathF.Abs(minX - st.LastMinX) > ReadbackSignificantDelta
                || MathF.Abs(maxX - st.LastMaxX) > ReadbackSignificantDelta
                || MathF.Abs(minY - st.LastMinY) > ReadbackSignificantDelta
                || MathF.Abs(maxY - st.LastMaxY) > ReadbackSignificantDelta
                || MathF.Abs(minZ - st.LastMinZ) > ReadbackSignificantDelta
                || MathF.Abs(maxZ - st.LastMaxZ) > ReadbackSignificantDelta;
            bool changed = st.HasLast && significant;
            // Log the first 3 dispatches always, then significant changes, and ALWAYS any direct
            // explosion (NaN/Inf/huge) even after the cap so a frozen-bad state keeps reporting.
            bool shouldLog = st.LoggedCount < 3 || significant || directExplosion;
            if (!shouldLog)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                return;
            }
            st.LoggedCount++;
            st.LastMinX = minX; st.LastMaxX = maxX; st.LastMinY = minY; st.LastMaxY = maxY; st.LastMinZ = minZ; st.LastMaxZ = maxZ;
            st.HasLast = true;

            string allocFlag = underAllocated ? " UNDER-ALLOCATED!" : "";
            string changeFlag = changed ? " <<< OUTPUT CHANGED" : "";
            string nanFlag = nan > 0 ? " NAN!" : "";

            // Read the GPU INPUT positions (SSBO binding 8, still bound at readback time since
            // ClearOpenGlComputeBindings runs later in the finally block). Because the GPU palette
            // is proven near-identity, a CORRECT skin gives output ~= input. A large |out-in| or a
            // ratio far from 1 therefore localizes the bug to the skinning math/weights or the
            // input position read itself (not the palette).
            float maxDisp = -1f, maxRatio = -1f;
            float in0x = 0f, in0y = 0f, in0z = 0f;
            int inPosId = 0, inBytes = 0;
            rawGl.GetInteger(GLEnum.ShaderStorageBufferBinding, 8, out inPosId);
            if (inPosId != 0)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, (uint)inPosId);
                rawGl.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out inBytes);
                int inVerts = inBytes / (3 * sizeof(float)); // tight vec3
                int inSample = Math.Min(sample, inVerts);
                if (inSample > 0)
                {
                    float[] indata = new float[inSample * 3];
                    fixed (float* p = indata)
                        rawGl.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, (nuint)(inSample * 3 * sizeof(float)), p);
                    in0x = indata[0]; in0y = indata[1]; in0z = indata[2];
                    for (int i = 0; i < inSample; i++)
                    {
                        float ox = data[i * 4], oy = data[i * 4 + 1], oz = data[i * 4 + 2];
                        if (float.IsNaN(ox) || float.IsNaN(oy) || float.IsNaN(oz))
                            continue;
                        float ix = indata[i * 3], iy = indata[i * 3 + 1], iz = indata[i * 3 + 2];
                        float disp = MathF.Sqrt((ox - ix) * (ox - ix) + (oy - iy) * (oy - iy) + (oz - iz) * (oz - iz));
                        if (disp > maxDisp) maxDisp = disp;
                        float inLen = MathF.Sqrt(ix * ix + iy * iy + iz * iz);
                        float outLen = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                        if (inLen > 0.01f)
                        {
                            float ratio = outLen / inLen;
                            if (ratio > maxRatio) maxRatio = ratio;
                        }
                    }
                }
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
            }

            // CPU client-side source positions for the SAME buffer bound at binding 8. Comparing the
            // CPU bytes against the GPU bytes (in0) classifies the corruption:
            //   cpu0 sane  + gpu in0 exploded -> GPU UPLOAD/BINDING bug (source built fine, wrong bytes on GPU).
            //   cpu0 exploded                 -> SOURCE-BUILD bug (mesh.PositionsBuffer populated/compressed wrong).
            float cpu0x = float.NaN, cpu0y = float.NaN, cpu0z = float.NaN;
            uint cpuId = 0u; long cpuBytes = -1L;
            var posBuf = mesh.PositionsBuffer;
            if (posBuf is not null)
            {
                cpuBytes = (long)posBuf.ElementCount * posBuf.ElementSize;
                foreach (var w in posBuf.APIWrappers)
                    if (w is OpenGLRenderer.GLDataBuffer glp && glp.TryGetBindingId(out cpuId))
                        break;
                if (posBuf.Address.Pointer != null && posBuf.ElementCount > 0)
                {
                    var v = posBuf.GetDataRawAtIndex<System.Numerics.Vector3>(0);
                    cpu0x = v.X; cpu0y = v.Y; cpu0z = v.Z;
                }
            }

            // Authoritative imported vertex positions (Vertices[].Position). Non-canonical meshes
            // (like the exploding one) still carry the Vertices[] array. Comparing its bounds to the
            // cooked PositionsBuffer bounds splits the corruption stage definitively:
            //   Vertices[] sane + PositionsBuffer exploded -> COOKING corrupts positions (CookedBinary/Core).
            //   Vertices[] ALSO exploded                   -> IMPORT builds wrong positions (geometryTransform).
            float vMinX = float.NaN, vMaxX = float.NaN, vMinY = float.NaN, vMaxY = float.NaN, vMinZ = float.NaN, vMaxZ = float.NaN;
            float vert0x = float.NaN, vert0y = float.NaN, vert0z = float.NaN;
            int vertCountArr = -1;
            var verts = mesh.Vertices;
            if (verts is { Length: > 0 })
            {
                vertCountArr = verts.Length;
                vMinX = vMinY = vMinZ = float.PositiveInfinity;
                vMaxX = vMaxY = vMaxZ = float.NegativeInfinity;
                var v0 = verts[0].Position;
                vert0x = v0.X; vert0y = v0.Y; vert0z = v0.Z;
                for (int vi = 0; vi < verts.Length; vi++)
                {
                    var p = verts[vi].Position;
                    if (p.X < vMinX) vMinX = p.X; if (p.X > vMaxX) vMaxX = p.X;
                    if (p.Y < vMinY) vMinY = p.Y; if (p.Y > vMaxY) vMaxY = p.Y;
                    if (p.Z < vMinZ) vMinZ = p.Z; if (p.Z > vMaxZ) vMaxZ = p.Z;
                }
            }

            // TRUE explosion test (replaces the false-positive-prone absolute-translation trigger):
            // a mesh is genuinely exploded only when its skinned OUTPUT span vastly exceeds its
            // AUTHORITATIVE source span. A large-but-correct mesh (e.g. verts=12852, ~86u Y-span) has
            // output span ~= authoritative span (ratio ~1) and is NOT exploded; a real explosion blows
            // the output span far past the source. Requires the authoritative Vertices[] array.
            string explodeFlag = string.Empty;
            if (directExplosion)
            {
                // NaN/Inf or absurd-magnitude output: the span-ratio test below cannot detect this
                // (NaN excluded from bounds, NaN comparisons are false), so flag it explicitly.
                explodeFlag = $" *** EXPLODED direct nan={nan} hugeAbs={hugeAbs} settled={_seedInputsSettled} reseed#{_reseedCount} ***";
            }
            else if (vertCountArr > 0)
            {
                float outSpanX = maxX - minX, outSpanY = maxY - minY, outSpanZ = maxZ - minZ;
                float authSpanX = vMaxX - vMinX, authSpanY = vMaxY - vMinY, authSpanZ = vMaxZ - vMinZ;
                float rX = authSpanX > 0.01f ? outSpanX / authSpanX : 0f;
                float rY = authSpanY > 0.01f ? outSpanY / authSpanY : 0f;
                float rZ = authSpanZ > 0.01f ? outSpanZ / authSpanZ : 0f;
                float worstRatio = MathF.Max(rX, MathF.Max(rY, rZ));
                // 2.5x is well above any legitimate pose (animation rotates limbs but does not multiply
                // the whole-mesh extent); a stale/garbage first dispatch blows it up by 5-50x.
                if (worstRatio > 2.5f)
                    explodeFlag = $" *** EXPLODED outVsAuth ratio={worstRatio:F1} (rX={rX:F1} rY={rY:F1} rZ={rZ:F1}) settled={_seedInputsSettled} reseed#{_reseedCount} ***";
            }

            Debug.LogWarning(
                $"[SkinReadback] verts={mesh.VertexCount} sample={sample} reseed#{_reseedCount} settled={_seedInputsSettled} " +
                $"gpuBytes={gpuBytes} expectedBytes={expectedBytes}{allocFlag}{nanFlag}{changeFlag}{explodeFlag} " +
                $"X[{minX:F2},{maxX:F2}] Y[{minY:F2},{maxY:F2}] Z[{minZ:F2},{maxZ:F2}] out0=({data[0]:F2},{data[1]:F2},{data[2]:F2}) " +
                $"in0=({in0x:F2},{in0y:F2},{in0z:F2}) maxDisp={maxDisp:F2} maxRatio={maxRatio:F2} " +
                $"bind8Id={inPosId} bind8Bytes={inBytes} posBufId={cpuId} posBufBytes={cpuBytes} cpu0=({cpu0x:F2},{cpu0y:F2},{cpu0z:F2}) " +
                $"vertsArr={vertCountArr} vert0=({vert0x:F2},{vert0y:F2},{vert0z:F2}) " +
                $"vertX[{vMinX:F2},{vMaxX:F2}] vertY[{vMinY:F2},{vMaxY:F2}] vertZ[{vMinZ:F2},{vMaxZ:F2}].");


            // If the OUTPUT is displaced, read the ACTUAL GPU palette content to determine whether
            // the corruption is in the uploaded palette (GPU palette has a bad bone entry) or
            // downstream (palette sane on GPU but geometry/index/shader wrong). Client-side palette
            // was already proven sane (DetectSkinPaletteExplosion never fired), so a bad GPU entry
            // here means the GPU palette buffer content diverges from the client buffer.
            DebugReadbackSkinPalette(mesh, rawGl, st.DispatchCount);
        }

        /// <summary>
        /// Reads the actual GPU SkinPaletteBuffer content (3 vec4 rows per bone; translation is the
        /// W component of each row) and reports the bone with the largest translation magnitude.
        /// </summary>
        private unsafe void DebugReadbackSkinPalette(XRMesh mesh, Silk.NET.OpenGL.GL rawGl, int dispatchIndex)
        {
            // Read the buffer ACTUALLY bound at SSBO binding 0 (the palette the shader used), not a
            // C# property that may be null/divergent. This is still live at readback because
            // ClearOpenGlComputeBindings runs later in the finally block.
            rawGl.GetInteger(GLEnum.ShaderStorageBufferBinding, 0, out int paletteId);
            uint pid = (uint)paletteId;
            if (pid == 0u)
            {
                Debug.LogWarning($"[SkinPaletteGpu] verts={mesh.VertexCount} dispatch#{dispatchIndex} *** NO PALETTE BOUND AT BINDING 0 ***.");
                return;
            }

            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, pid);
            rawGl.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out int pBytes);
            int boneEntries = pBytes / (12 * sizeof(float));
            if (boneEntries <= 0)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                return;
            }

            float[] pdata = new float[boneEntries * 12];
            fixed (float* p = pdata)
                rawGl.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, (nuint)(boneEntries * 12 * sizeof(float)), p);
            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

            float worstTrans = 0f;
            int worstBone = -1;
            int badBones = 0;
            int nanBones = 0;
            int identityBones = 0;      // bones whose palette == identity (passthrough => no skinning applied)
            float worstScaleDev = 0f;   // max |rowLength - 1| across the 3x3 (detects blown-up scale/rotation)
            int worstScaleBone = -1;
            for (int b = 0; b < boneEntries; b++)
            {
                // Translation = (Row0.W, Row1.W, Row2.W) = floats [3],[7],[11] of the 12-float block.
                float tx = pdata[b * 12 + 3], ty = pdata[b * 12 + 7], tz = pdata[b * 12 + 11];
                if (float.IsNaN(tx) || float.IsNaN(ty) || float.IsNaN(tz) || float.IsInfinity(tx) || float.IsInfinity(ty) || float.IsInfinity(tz))
                {
                    nanBones++;
                    continue;
                }
                float mag = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                if (mag > 50f)
                    badBones++;
                if (mag > worstTrans)
                {
                    worstTrans = mag;
                    worstBone = b;
                }

                // 3x3 row lengths: floats [0,1,2]=row0.xyz, [4,5,6]=row1.xyz, [8,9,10]=row2.xyz.
                // For a rigid/bind palette each row length is ~1. A blown-up scale here explodes
                // every vertex this bone touches WITHOUT showing up in the translation check above.
                float r0 = MathF.Sqrt(pdata[b * 12 + 0] * pdata[b * 12 + 0] + pdata[b * 12 + 1] * pdata[b * 12 + 1] + pdata[b * 12 + 2] * pdata[b * 12 + 2]);
                float r1 = MathF.Sqrt(pdata[b * 12 + 4] * pdata[b * 12 + 4] + pdata[b * 12 + 5] * pdata[b * 12 + 5] + pdata[b * 12 + 6] * pdata[b * 12 + 6]);
                float r2 = MathF.Sqrt(pdata[b * 12 + 8] * pdata[b * 12 + 8] + pdata[b * 12 + 9] * pdata[b * 12 + 9] + pdata[b * 12 + 10] * pdata[b * 12 + 10]);
                float dev = MathF.Max(MathF.Abs(r0 - 1f), MathF.Max(MathF.Abs(r1 - 1f), MathF.Abs(r2 - 1f)));
                if (dev > worstScaleDev)
                {
                    worstScaleDev = dev;
                    worstScaleBone = b;
                }

                // Identity detection: 3x3 == I (diagonal ~1, off-diagonal ~0) AND translation ~0.
                // An identity palette means the shader passes the source position through unchanged.
                // If the mesh is exploded with an identity palette, the SOURCE positions are not in a
                // directly-displayable space (they need the real bind/bone transform) -> the palette
                // was composed wrong (bone matrices not ready at load).
                const float idEps = 0.01f;
                bool isIdentity =
                    MathF.Abs(pdata[b * 12 + 0] - 1f) < idEps && MathF.Abs(pdata[b * 12 + 1]) < idEps && MathF.Abs(pdata[b * 12 + 2]) < idEps && MathF.Abs(tx) < idEps &&
                    MathF.Abs(pdata[b * 12 + 4]) < idEps && MathF.Abs(pdata[b * 12 + 5] - 1f) < idEps && MathF.Abs(pdata[b * 12 + 6]) < idEps && MathF.Abs(ty) < idEps &&
                    MathF.Abs(pdata[b * 12 + 8]) < idEps && MathF.Abs(pdata[b * 12 + 9]) < idEps && MathF.Abs(pdata[b * 12 + 10] - 1f) < idEps && MathF.Abs(tz) < idEps;
                if (isIdentity)
                    identityBones++;
            }

            string wtx = worstBone >= 0 ? $"({pdata[worstBone * 12 + 3]:F2},{pdata[worstBone * 12 + 7]:F2},{pdata[worstBone * 12 + 11]:F2})" : "n/a";
            bool mostlyIdentity = identityBones >= boneEntries - 1; // all bones (sans the unused slot 0) are identity
            string flag = (badBones > 0 || nanBones > 0 || worstScaleDev > 0.5f) ? " *** GPU PALETTE BAD ***"
                : mostlyIdentity ? " *** PALETTE ALL-IDENTITY (passthrough) ***" : "";
            Debug.LogWarning(
                $"[SkinPaletteGpu]{flag} verts={mesh.VertexCount} dispatch#{dispatchIndex} bones={boneEntries} " +
                $"identityBones={identityBones} badTrans(>50)={badBones} nanBones={nanBones} worstBone={worstBone} worstTransMag={worstTrans:F2} worstTrans={wtx} " +
                $"worstScaleBone={worstScaleBone} worstScaleDev={worstScaleDev:F2}.");
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
            _seedInputsSettled = false;
            _lastSeedPoseHash = 0;
            _reseedCount = 0;
            _poseChangeCount = 0;
            _settleLogged = false;
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
            _seedInputsSettled = false;
            _lastSeedPoseHash = 0;
            _reseedCount = 0;
            _poseChangeCount = 0;
            _settleLogged = false;
            _lastDidSkinning = false;
            _lastDidBlendshapes = false;
            _lastOutputVersion = 0;
        }
    }
}
