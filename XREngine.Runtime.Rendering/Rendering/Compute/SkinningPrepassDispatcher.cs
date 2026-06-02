using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Numerics;
using Silk.NET.OpenGL;
using XREngine.Data.Geometry;
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
/// <remarks>
/// The top-level partial owns frame orchestration and shader lifetime. Per-renderer cache,
/// residency, binding, output-buffer, and diagnostic details live in the
/// <c>Rendering/Compute/SkinningPrepassDispatcher</c> partials.
/// </remarks>
internal sealed partial class SkinningPrepassDispatcher : IDisposable
{
    private const string ShaderPath = "Compute/Animation/SkinningPrepass.comp";
    private const string InterleavedShaderPath = "Compute/Animation/SkinningPrepassInterleaved.comp";
    private const string PrecombinedShaderPath = "Compute/Animation/SkinningPrepassPrecombined.comp";
    private const string PrecombinedInterleavedShaderPath = "Compute/Animation/SkinningPrepassInterleavedPrecombined.comp";
    private const string BlendshapePrecombineShaderPath = "Compute/Animation/BlendshapePrecombine.comp";
    private const string SkinnedBoundsReduceShaderPath = "Compute/Animation/SkinnedBoundsReduce.comp";
    private const uint ThreadGroupSize = 256u;

    internal enum BlendshapePrecombineRendererPath
    {
        ComputePrepass,
        DirectVertex,
    }

    // Keep these binding points in sync with SkinningPrepass*.comp layout(binding = N)
    // declarations. Non-interleaved and interleaved shaders share the animation inputs
    // but pack vertex I/O and spill buffers differently.
    private static class SkinningPrepassBindings
    {
        public const uint SkinPalette = 0u;
        public const uint BlendshapeActiveWeights = 1u;
        public const uint BoneCoreIndices = 2u;
        public const uint BoneCoreWeights = 3u;
        public const uint BlendshapeSparseShapeRanges = 4u;
        public const uint BlendshapeSparseRecords = 5u;
        public const uint BlendshapeQuantizedDeltas = 6u;
        public const uint BlendshapeQuantizationMetadata = 7u;

        public const uint NonInterleavedPositionInput = 8u;
        public const uint NonInterleavedNormalInput = 9u;
        public const uint NonInterleavedTangentInput = 10u;
        public const uint NonInterleavedPositionOutput = 11u;
        public const uint NonInterleavedNormalOutput = 12u;
        public const uint NonInterleavedSpillHeaders = 13u;
        public const uint NonInterleavedSpillEntries = 14u;
        public const uint NonInterleavedTangentOutput = 15u;

        public const uint InterleavedInput = 8u;
        public const uint InterleavedOutput = 9u;
        public const uint InterleavedSpillHeaders = 10u;
        public const uint InterleavedSpillEntries = 11u;

        public const uint Max = NonInterleavedTangentOutput;
    }

    private static class BlendshapePrecombineBindings
    {
        public const uint BlendshapeActiveWeights = 0u;
        public const uint BlendshapeSparseShapeRanges = 1u;
        public const uint BlendshapeSparseRecords = 2u;
        public const uint BlendshapeQuantizedDeltas = 3u;
        public const uint BlendshapeQuantizationMetadata = 4u;
        public const uint PrecombinedPositionDeltas = 5u;
        public const uint PrecombinedNormalDeltas = 6u;
        public const uint PrecombinedTangentDeltas = 7u;

        public const uint Max = PrecombinedTangentDeltas;
    }

    private static readonly Lazy<SkinningPrepassDispatcher> _instance = new(() => new SkinningPrepassDispatcher());
    public static SkinningPrepassDispatcher Instance => _instance.Value;

    private readonly object _syncRoot = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, RendererResources> _resources = new();

    private XRShader? _shader;
    private XRRenderProgram? _program;
    private XRShader? _interleavedShader;
    private XRRenderProgram? _interleavedProgram;
    private XRShader? _precombinedShader;
    private XRRenderProgram? _precombinedProgram;
    private XRShader? _precombinedInterleavedShader;
    private XRRenderProgram? _precombinedInterleavedProgram;
    private XRShader? _blendshapePrecombineShader;
    private XRRenderProgram? _blendshapePrecombineProgram;
    private XRShader? _skinnedBoundsReduceShader;
    private XRRenderProgram? _skinnedBoundsReduceProgram;
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
            _precombinedProgram?.Destroy();
            _precombinedShader?.Destroy();
            _precombinedInterleavedProgram?.Destroy();
            _precombinedInterleavedShader?.Destroy();
            _blendshapePrecombineProgram?.Destroy();
            _blendshapePrecombineShader?.Destroy();
            _skinnedBoundsReduceProgram?.Destroy();
            _skinnedBoundsReduceShader?.Destroy();
            _program = null;
            _shader = null;
            _interleavedProgram = null;
            _interleavedShader = null;
            _precombinedProgram = null;
            _precombinedShader = null;
            _precombinedInterleavedProgram = null;
            _precombinedInterleavedShader = null;
            _blendshapePrecombineProgram = null;
            _blendshapePrecombineShader = null;
            _skinnedBoundsReduceProgram = null;
            _skinnedBoundsReduceShader = null;
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

    public bool TryGetSkinnedBoundsBuffer(XRMeshRenderer renderer, out XRDataBuffer? boundsBuffer)
        => TryGetSkinnedBoundsBuffer(renderer, out boundsBuffer, out _);

    public bool TryGetSkinnedBoundsBuffer(XRMeshRenderer renderer, out XRDataBuffer? boundsBuffer, out uint boundsVec4Offset)
    {
        lock (_syncRoot)
        {
            if (_resources.TryGetValue(renderer, out var resources) && resources.HasValidSkinnedBounds)
            {
                boundsBuffer = resources.SkinnedBounds;
                boundsVec4Offset = resources.SkinnedBoundsVec4Offset;
                return boundsBuffer is not null;
            }
        }

        boundsBuffer = null;
        boundsVec4Offset = 0u;
        return false;
    }

    public bool TryReadSkinnedWorldBounds(XRMeshRenderer renderer, out AABB bounds)
    {
        lock (_syncRoot)
        {
            if (_resources.TryGetValue(renderer, out var resources) && resources.TryReadSkinnedBounds(out bounds))
                return true;
        }

        bounds = default;
        return false;
    }

    internal void InvalidateRenderer(XRMeshRenderer renderer)
    {
        lock (_syncRoot)
        {
            if (!_resources.TryGetValue(renderer, out var resources))
                return;

            resources.Dispose();
            _resources.Remove(renderer);
        }
    }

    /// <summary>
    /// Executes the compute pre-pass for the supplied renderer if the current engine settings require it.
    /// Supports both interleaved and non-interleaved meshes with separate shader programs.
    /// </summary>
    public void Run(XRMeshRenderer renderer)
        => Run(renderer, forceSkinning: false);

    internal void RunForGpuMeshBvh(XRMeshRenderer renderer)
        => Run(renderer, forceSkinning: true);

    private void Run(XRMeshRenderer renderer, bool forceSkinning)
    {
        if (AbstractRenderer.Current is null)
            return;

        var mesh = renderer.Mesh;
        if (mesh is null || mesh.VertexCount <= 0)
            return;

        bool doSkinning = (forceSkinning || RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader)
            && mesh.HasSkinning
            && RuntimeEngine.Rendering.Settings.AllowSkinning;
        bool hasBlendshapePath = mesh.BlendshapeCount > 0
            && RuntimeEngine.Rendering.Settings.AllowBlendshapes;
        bool blendshapePathRequested = mesh.BlendshapeCount > 0
            && RuntimeEngine.Rendering.Settings.AllowBlendshapes
            && (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || doSkinning);
        bool precombinePathRequested = hasBlendshapePath
            && RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass;

        if (blendshapePathRequested || precombinePathRequested)
            renderer.EnsureBlendshapeBuffers(logWarnings: false);

        bool doBlendshapes = blendshapePathRequested && renderer.HasActiveBlendshapes;
        BlendshapePrecombineRendererPath precombinePath =
            RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || doSkinning
                ? BlendshapePrecombineRendererPath.ComputePrepass
                : BlendshapePrecombineRendererPath.DirectVertex;
        bool wantsPrecombinedBlendshapes = ShouldUseBlendshapePrecombine(renderer, mesh, precombinePath);

        if (!doSkinning && (blendshapePathRequested || precombinePathRequested) && !doBlendshapes && !wantsPrecombinedBlendshapes)
        {
            RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                0L,
                0L,
                skippedBlendshapeDispatches: 1,
                blendshapeAuthoredShapeCount: (int)mesh.BlendshapeCount,
                blendshapeActiveShapeCount: renderer.ActiveBlendshapeCount,
                blendshapeAffectedVertexCount: mesh.BlendshapeAffectedVertexCount,
                compactedActiveBlendshapeCount: renderer.ActiveBlendshapeCount,
                liveBlendshapeShaderPermutations: mesh.BlendshapeShaderVariant == BlendshapeShaderVariant.None ? 0 : 1);
            return;
        }

        if (!doSkinning && !doBlendshapes && !wantsPrecombinedBlendshapes)
            return;

        // Prepare CPU-side renderer/mesh state before taking the dispatcher lock.
        if (doSkinning)
            mesh.EnsureComputeSkinningBuffers();

        if (doSkinning && !renderer.HasExternalSkinPaletteSource)
            renderer.EnsureSkinningBuffers(logWarnings: false);

        if (doSkinning && RenderDiagnosticsFlags.SkinningPrepassDiag)
            renderer.VerifyBonePaletteOrderMatchesMesh();
        bool useExternalSkinPaletteSource = doSkinning && renderer.HasExternalSkinPaletteSource;
        bool usePackedGlobalSkinPalette = doSkinning
            && !useExternalSkinPaletteSource
            && RuntimeEngine.Rendering.Settings.UseGlobalSkinPaletteBufferForComputeSkinning
            && !renderer.HasGpuDrivenBoneSource;
        bool useSharedSkinPaletteBuffer = useExternalSkinPaletteSource || usePackedGlobalSkinPalette;
        bool useGlobalBlendWeights = false;
        bool needsLiveSkinnedBounds = doSkinning &&
            (forceSkinning || RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader);

        bool isInterleaved = mesh.Interleaved;
        lock (_syncRoot)
        {
            // Per-frame cache: a renderer only needs one deformation dispatch per global render frame.
            ulong frameId = State.RenderFrameId;

            _globalInputs.BeginFrame(frameId);

            bool usePrecombinedBlendshapes = false;
            if (wantsPrecombinedBlendshapes)
                usePrecombinedBlendshapes = EnsurePrecombinedBlendshapeDeltas(renderer, mesh, precombinePath);

            if (!doSkinning && !doBlendshapes)
                return;

            XRRenderProgram? activeProgram;
            if (isInterleaved)
            {
                if (usePrecombinedBlendshapes)
                {
                    EnsurePrecombinedInterleavedProgram();
                    activeProgram = _precombinedInterleavedProgram;
                }
                else
                {
                    EnsureInterleavedProgram();
                    activeProgram = _interleavedProgram;
                }
            }
            else
            {
                if (usePrecombinedBlendshapes)
                {
                    EnsurePrecombinedProgram();
                    activeProgram = _precombinedProgram;
                }
                else
                {
                    EnsureProgram();
                    activeProgram = _program;
                }
            }

            if (activeProgram is null)
                return;

            // Per-renderer resources own transient output buffers and the reuse/residency state
            // that decides whether a dispatch is necessary this frame.
            RendererResources resources = _resources.GetValue(renderer, r => new RendererResources(r));

            bool forceLivePoseRefresh = forceSkinning && needsLiveSkinnedBounds;

            if (!forceLivePoseRefresh &&
                resources.HasFrameOutput(frameId, doSkinning, doBlendshapes, usePrecombinedBlendshapes) &&
                (!needsLiveSkinnedBounds || resources.HasValidSkinnedBounds))
            {
                return;
            }

            if (!resources.Validate(mesh, doSkinning, doBlendshapes, isInterleaved, usePrecombinedBlendshapes))
                return;

            if (!needsLiveSkinnedBounds &&
                resources.CanReuseOutput(doSkinning, doBlendshapes, usePrecombinedBlendshapes))
            {
                resources.LastComputePrepassFrameId = frameId;
                RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                    0L,
                    0L,
                    skippedSkinningDispatches: doSkinning ? 1 : 0,
                    skippedBlendshapeDispatches: doBlendshapes ? 1 : 0,
                    reusedSkinnedOutputBuffers: 1,
                    blendshapeAuthoredShapeCount: doBlendshapes ? (int)mesh.BlendshapeCount : 0,
                    blendshapeActiveShapeCount: doBlendshapes ? renderer.ActiveBlendshapeCount : 0,
                    blendshapeAffectedVertexCount: doBlendshapes ? mesh.BlendshapeAffectedVertexCount : 0,
                    compactedActiveBlendshapeCount: doBlendshapes ? renderer.ActiveBlendshapeCount : 0,
                    liveBlendshapeShaderPermutations: doBlendshapes && mesh.BlendshapeShaderVariant != BlendshapeShaderVariant.None ? 1 : 0);
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
            {
                if (forceLivePoseRefresh)
                    renderer.RefreshBoneMatricesFromRenderState();
                else
                    resources.EnsureSeededFromRenderState(mesh, isInterleaved, doSkinning, doBlendshapes, useGlobalBlendWeights);
            }

            // Ensure this renderer's animation inputs are present in the global packed buffers (if enabled).
            // This may resize and/or re-upload global buffers.
            if (usePackedGlobalSkinPalette || useGlobalBlendWeights)
            {
                _globalInputs.EnsurePackedForRenderer(renderer, usePackedGlobalSkinPalette, useGlobalBlendWeights);
                _globalInputs.PushIfDirty(pushSkinPalette: usePackedGlobalSkinPalette, pushBlendshapeWeights: useGlobalBlendWeights);
            }

            resources.SyncDynamicBuffers(
                pushSkinPalette: !useSharedSkinPaletteBuffer,
                pushBlendshapeWeights: !useGlobalBlendWeights && !usePrecombinedBlendshapes);

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
            resources.EnsureSkinningInputsResident(
                mesh,
                activeSkinPalette,
                _globalInputs.GlobalBlendshapeWeights,
                isInterleaved,
                doSkinning,
                doBlendshapes,
                useGlobalBlendWeights,
                usePrecombinedBlendshapes);

            // OUTPUT buffers (skinned positions/normals/tangents, or interleaved) are created
            // client-side in EnsureOutputBuffers but their GPU storage allocates lazily -- normally
            // not until the DRAW binds them, which happens AFTER this compute dispatch. The dispatch
            // binds the output via SetBlockIndex, which (like the inputs) does NOT force storage
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
            bool updateLiveBounds = needsLiveSkinnedBounds &&
                resources.ResetSkinnedBoundsInOutput(mesh, isInterleaved);

            try
            {
                if (doSkinning && !mesh.HasSpillInfluences)
                    EnsureEmptySpillBuffers();

                resources.BindBlocks(
                    doSkinning,
                    doBlendshapes,
                    isInterleaved,
                    useGlobalBlendWeights,
                    usePrecombinedBlendshapes,
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
                activeProgram.Uniform("usePrecombinedBlendshapeDeltas", usePrecombinedBlendshapes ? 1 : 0);
                activeProgram.Uniform("absoluteBlendshapePositions", RuntimeEngine.Rendering.Settings.UseAbsoluteBlendshapePositions ? 1 : 0);
                activeProgram.Uniform("maxBlendshapeAccumulation", mesh.MaxBlendshapeAccumulation ? 1 : 0);
                activeProgram.Uniform("activeBlendshapeCount", renderer.ActiveBlendshapeCount);
                activeProgram.Uniform("blendshapeWeightThreshold", renderer.BlendshapeActiveWeightThreshold);
                activeProgram.Uniform("useIntegerUniforms", RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders ? 1 : 0);
                activeProgram.Uniform("skinningCoreIndexFormat", (int)mesh.SkinningCoreIndexFormat);
                activeProgram.Uniform("hasSpillInfluences", mesh.HasSpillInfluences ? 1 : 0);
                activeProgram.Uniform("skinningInfluenceCap", renderer.ActiveSkinningInfluenceCap);
                activeProgram.Uniform("updateLiveBounds", updateLiveBounds ? 1 : 0);
                activeProgram.Uniform("liveBoundsVec4Offset", resources.SkinnedBoundsVec4Offset);
                activeProgram.Uniform("liveBoundsWordOffset", resources.SkinnedBoundsWordOffset);

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
                EMemoryBarrierMask barrierMask = EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray;

                activeProgram.DispatchCompute(groupsX, 1u, 1u, barrierMask);
                resources.MarkOutputValid(doSkinning, doBlendshapes, usePrecombinedBlendshapes);
                if (updateLiveBounds)
                    resources.MarkSkinnedBoundsValid();
                else if (needsLiveSkinnedBounds)
                    TryUpdateSkinnedBoundsFromOutput(resources, mesh, isInterleaved, vertexCount);
                if (doSkinning && RenderDiagnosticsFlags.SkinningPrepassDiag)
                    resources.DebugReadbackSkinnedOutput(mesh);
                RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                    0L,
                    0L,
                    doSkinning ? 1 : 0,
                    doBlendshapes ? 1 : 0,
                    blendshapeDeltaBytes: doBlendshapes
                        ? (mesh.BlendshapeQuantizedDeltas?.Length ?? mesh.BlendshapeDeltas?.Length ?? 0u)
                            + (mesh.BlendshapeQuantizationMetadata?.Length ?? 0u)
                        : 0u,
                    blendshapeAuthoredShapeCount: doBlendshapes ? (int)mesh.BlendshapeCount : 0,
                    blendshapeActiveShapeCount: doBlendshapes ? renderer.ActiveBlendshapeCount : 0,
                    blendshapeAffectedVertexCount: doBlendshapes ? mesh.BlendshapeAffectedVertexCount : 0,
                    compactedActiveBlendshapeCount: doBlendshapes ? renderer.ActiveBlendshapeCount : 0,
                    liveBlendshapeShaderPermutations: doBlendshapes && mesh.BlendshapeShaderVariant != BlendshapeShaderVariant.None ? 1 : 0);
            }
            finally
            {
                ClearOpenGlComputeBindings();
            }
        }
    }

    private bool TryUpdateSkinnedBoundsFromOutput(RendererResources resources, XRMesh mesh, bool isInterleaved, uint vertexCount)
    {
        if (vertexCount == 0u)
            return false;
        if (!EnsureSkinnedBoundsReduceProgramReady())
            return false;

        XRDataBuffer? positions = resources.SkinnedPositions;
        XRDataBuffer? interleaved = resources.SkinnedInterleaved;
        XRDataBuffer? source = isInterleaved ? interleaved : positions;
        if (source is null)
            return false;

        XRDataBuffer? boundsBuffer = resources.ResetSkinnedBoundsBuffer(mesh);
        if (boundsBuffer is null)
            return false;

        XRRenderProgram program = _skinnedBoundsReduceProgram!;
        program.BindBuffer(positions ?? source, 0);
        program.BindBuffer(boundsBuffer, 1);
        program.BindBuffer(interleaved ?? source, 2);

        program.Uniform("vertexCount", vertexCount);
        program.Uniform("slotIndex", 0u);
        program.Uniform("applyTransform", 0);
        program.Uniform("transformMatrix", Matrix4x4.Identity);
        program.Uniform("useInterleaved", isInterleaved ? 1u : 0u);
        program.Uniform("interleavedStrideBytes", mesh.InterleavedStride);
        program.Uniform("positionOffsetBytes", mesh.PositionOffset);

        uint groupsX = Math.Max(1u, (vertexCount + ThreadGroupSize - 1u) / ThreadGroupSize);
        program.DispatchCompute(
            groupsX,
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray | EMemoryBarrierMask.ClientMappedBuffer);

        resources.MarkSkinnedBoundsValid();
        return true;
    }

    internal static bool ShouldUseBlendshapePrecombine(
        XRMeshRenderer renderer,
        XRMesh mesh,
        BlendshapePrecombineRendererPath rendererPath)
    {
        var settings = RuntimeEngine.Rendering.Settings;
        if (!settings.EnableBlendshapePrecombinePass)
            return false;
        if (rendererPath == BlendshapePrecombineRendererPath.DirectVertex
            && !settings.EnableBlendshapePrecombineForDirectVertexPath)
        {
            return false;
        }
        if (!settings.AllowBlendshapes || mesh.BlendshapeCount == 0 || !renderer.HasActiveBlendshapes)
            return false;
        if (mesh.BlendshapeSparseShapeRanges is null
            || mesh.BlendshapeSparseRecords is null
            || mesh.BlendshapeQuantizedDeltas is null
            || mesh.BlendshapeQuantizationMetadata is null
            || renderer.BlendshapeActiveWeights is null)
        {
            return false;
        }

        int minActiveShapes = rendererPath == BlendshapePrecombineRendererPath.DirectVertex
            ? settings.BlendshapePrecombineDirectMinActiveShapes
            : settings.BlendshapePrecombineComputeMinActiveShapes;
        if (renderer.ActiveBlendshapeCount < Math.Max(1, minActiveShapes))
            return false;

        int minAffectedVertices = Math.Max(1, settings.BlendshapePrecombineMinAffectedVertices);
        return mesh.BlendshapeAffectedVertexCount >= minAffectedVertices;
    }

    internal static bool ShouldUseBlendshapeBasisCompression(XRMeshRenderer renderer, XRMesh mesh)
    {
        var settings = RuntimeEngine.Rendering.Settings;
        return settings.EnableBlendshapePcaBasisCompression
            && settings.AllowBlendshapes
            && mesh.HasBlendshapes
            && mesh.HasBlendshapeBasisCompressionPayload
            && renderer.HasActiveBlendshapes;
    }

    private bool EnsurePrecombinedBlendshapeDeltas(
        XRMeshRenderer renderer,
        XRMesh mesh,
        BlendshapePrecombineRendererPath rendererPath)
    {
        if (!ShouldUseBlendshapePrecombine(renderer, mesh, rendererPath))
            return false;

        if (!renderer.EnsurePrecombinedBlendshapeBuffers(mesh))
            return false;

        if (renderer.HasValidPrecombinedBlendshapeDeltas)
        {
            RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                0L,
                0L,
                skippedBlendshapeDispatches: 1,
                blendshapeAuthoredShapeCount: (int)mesh.BlendshapeCount,
                blendshapeActiveShapeCount: renderer.ActiveBlendshapeCount,
                blendshapeAffectedVertexCount: mesh.BlendshapeAffectedVertexCount,
                compactedActiveBlendshapeCount: renderer.ActiveBlendshapeCount,
                liveBlendshapeShaderPermutations: 1);
            return true;
        }

        EnsureBlendshapePrecombineProgram();
        XRRenderProgram? program = _blendshapePrecombineProgram;
        if (program is null)
            return false;

        renderer.PushBlendshapeWeightsToGPU();
        EnsurePrecombineInputsResident(renderer, mesh);

        try
        {
            BindPrecombineBlocks(renderer, mesh);

            uint vertexCount = (uint)mesh.VertexCount;
            program.Uniform("vertexCount", vertexCount);
            program.Uniform("hasNormals", mesh.HasNormals ? 1 : 0);
            program.Uniform("hasTangents", mesh.HasTangents ? 1 : 0);
            program.Uniform("maxBlendshapeAccumulation", mesh.MaxBlendshapeAccumulation ? 1 : 0);
            program.Uniform("activeBlendshapeCount", renderer.ActiveBlendshapeCount);
            program.Uniform("blendshapeWeightThreshold", renderer.BlendshapeActiveWeightThreshold);
            program.Uniform("useIntegerUniforms", RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders ? 1 : 0);

            uint groupsX = Math.Max(1u, (vertexCount + ThreadGroupSize - 1u) / ThreadGroupSize);
            program.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray);
            renderer.MarkPrecombinedBlendshapeDeltasValid(mesh);

            RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                0L,
                0L,
                blendshapeDispatches: 1,
                blendshapeDeltaBytes: (mesh.BlendshapeQuantizedDeltas?.Length ?? mesh.BlendshapeDeltas?.Length ?? 0u)
                    + (mesh.BlendshapeQuantizationMetadata?.Length ?? 0u),
                blendshapeAuthoredShapeCount: (int)mesh.BlendshapeCount,
                blendshapeActiveShapeCount: renderer.ActiveBlendshapeCount,
                blendshapeAffectedVertexCount: mesh.BlendshapeAffectedVertexCount,
                compactedActiveBlendshapeCount: renderer.ActiveBlendshapeCount,
                liveBlendshapeShaderPermutations: 1);

            return true;
        }
        finally
        {
            ClearOpenGlComputeBindings();
        }
    }

    private static void BindPrecombineBlocks(XRMeshRenderer renderer, XRMesh mesh)
    {
        renderer.BlendshapeActiveWeights?.SetBlockIndex(BlendshapePrecombineBindings.BlendshapeActiveWeights);
        mesh.BlendshapeSparseShapeRanges?.SetBlockIndex(BlendshapePrecombineBindings.BlendshapeSparseShapeRanges);
        mesh.BlendshapeSparseRecords?.SetBlockIndex(BlendshapePrecombineBindings.BlendshapeSparseRecords);
        mesh.BlendshapeQuantizedDeltas?.SetBlockIndex(BlendshapePrecombineBindings.BlendshapeQuantizedDeltas);
        mesh.BlendshapeQuantizationMetadata?.SetBlockIndex(BlendshapePrecombineBindings.BlendshapeQuantizationMetadata);
        renderer.PrecombinedBlendshapePositionsBuffer?.SetBlockIndex(BlendshapePrecombineBindings.PrecombinedPositionDeltas);
        renderer.PrecombinedBlendshapeNormalsBuffer?.SetBlockIndex(BlendshapePrecombineBindings.PrecombinedNormalDeltas);
        renderer.PrecombinedBlendshapeTangentsBuffer?.SetBlockIndex(BlendshapePrecombineBindings.PrecombinedTangentDeltas);
    }

    private static void EnsurePrecombineInputsResident(XRMeshRenderer renderer, XRMesh mesh)
    {
        EnsureBufferResident(renderer.BlendshapeActiveWeights);
        EnsureBufferResident(mesh.BlendshapeSparseShapeRanges);
        EnsureBufferResident(mesh.BlendshapeSparseRecords);
        EnsureBufferResident(mesh.BlendshapeQuantizedDeltas);
        EnsureBufferResident(mesh.BlendshapeQuantizationMetadata);
        EnsureBufferResident(renderer.PrecombinedBlendshapePositionsBuffer);
        EnsureBufferResident(renderer.PrecombinedBlendshapeNormalsBuffer);
        EnsureBufferResident(renderer.PrecombinedBlendshapeTangentsBuffer);
    }

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

    private static void ClearOpenGlComputeBindings()
    {
        if (AbstractRenderer.Current is not OpenGLRenderer glRenderer)
            return;

        for (uint binding = 0u; binding <= SkinningPrepassBindings.Max; binding++)
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

        if (!RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
            && !RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader
            && !RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass)
        {
            return;
        }

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
        bool globalBlend = false;
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
                    bool blendshapePathRequested = mesh.BlendshapeCount > 0
                        && RuntimeEngine.Rendering.Settings.AllowBlendshapes
                        && (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader
                            || skinningInCompute
                            || RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass);
                    if (blendshapePathRequested)
                        renderer.EnsureBlendshapeBuffers(logWarnings: false);
                    bool needsBlend = globalBlend
                        && blendshapePathRequested
                        && renderer.HasActiveBlendshapes;
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

    private void EnsurePrecombinedProgram()
    {
        if (_precombinedProgram is not null)
            return;

        _precombinedShader ??= ShaderHelper.LoadEngineShader(PrecombinedShaderPath, EShaderType.Compute);
        _precombinedProgram = _precombinedShader is null ? null : new XRRenderProgram(true, false, _precombinedShader);
    }

    private void EnsurePrecombinedInterleavedProgram()
    {
        if (_precombinedInterleavedProgram is not null)
            return;

        _precombinedInterleavedShader ??= ShaderHelper.LoadEngineShader(PrecombinedInterleavedShaderPath, EShaderType.Compute);
        _precombinedInterleavedProgram = _precombinedInterleavedShader is null ? null : new XRRenderProgram(true, false, _precombinedInterleavedShader);
    }

    private void EnsureBlendshapePrecombineProgram()
    {
        if (_blendshapePrecombineProgram is not null)
            return;

        _blendshapePrecombineShader ??= ShaderHelper.LoadEngineShader(BlendshapePrecombineShaderPath, EShaderType.Compute);
        _blendshapePrecombineProgram = _blendshapePrecombineShader is null ? null : new XRRenderProgram(true, false, _blendshapePrecombineShader);
    }

    private bool EnsureSkinnedBoundsReduceProgramReady()
    {
        if (_skinnedBoundsReduceProgram is null)
        {
            _skinnedBoundsReduceShader ??= ShaderHelper.LoadEngineShader(SkinnedBoundsReduceShaderPath, EShaderType.Compute);
            _skinnedBoundsReduceProgram = _skinnedBoundsReduceShader is null ? null : new XRRenderProgram(true, false, _skinnedBoundsReduceShader);
        }

        if (_skinnedBoundsReduceProgram is null)
            return false;

        if (_skinnedBoundsReduceProgram.IsLinked)
            return true;

        _skinnedBoundsReduceProgram.Link();
        return _skinnedBoundsReduceProgram.IsLinked;
    }

}
