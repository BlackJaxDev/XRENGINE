using XREngine.Core.Files;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Generator;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>OpenGL program lifecycle for the native Advanced stage family.</summary>
public partial class OpenGLRenderer
{
    private XRRenderProgram? _advancedEarlyVisibilityProgram;
    private XRRenderProgram? _advancedBuildIndirectProgram;
    private XRRenderProgram? _advancedDepthPyramidProgram;
    private XRRenderProgram? _advancedLateVisibilityProgram;
    private XRRenderProgram? _advancedVisibilityRasterProgram;
    private XRRenderProgram? _advancedVisibilityMaskedRasterProgram;
    private XRRenderProgram? _advancedGtaoProgram;
    private XRRenderProgram? _advancedClassifyTilesProgram;
    private XRRenderProgram? _advancedBuildClassificationIndirectProgram;
    private XRRenderProgram? _advancedBuildFroxelsProgram;
    private XRRenderProgram? _advancedShadeNativeOpaqueProgram;
    private XRRenderProgram? _advancedShadeBackgroundProgram;
    private string? _advancedStageProgramFailure;
    private OpenGLAdvancedVisibilityOutputRegistry? _advancedOutputRegistry;
    private OpenGLAdvancedSceneTableUploader? _advancedSceneUploader;
    private OpenGLAdvancedVisibilityInputStorage? _advancedInputStorage;
    private OpenGLAdvancedVisibilityAtlasVao? _advancedAtlasVao;
    private uint _advancedNativeSampler;

    /// <summary>Releases the complete native Advanced runtime while the GL context is
    /// still valid. The orphan path deliberately drops managed references only because
    /// the shutdown policy has already abandoned the context's asynchronous work.</summary>
    private void DisposeAdvancedRuntimeForShutdown(bool orphanGLHandles)
    {
        if (!orphanGLHandles)
        {
            // Slot-owned scene publications may still hold resident bindless
            // pairs. Complete the queue before releasing those leases.
            RawGL.Finish();
            _advancedOutputRegistry?.Dispose();
            DisposeAdvancedBindlessResidency();
            _advancedAtlasVao?.Dispose();
            if (_advancedRasterFramebuffer != 0u)
                RawGL.DeleteFramebuffer(_advancedRasterFramebuffer);
            if (_advancedNativeSampler != 0u)
                RawGL.DeleteSampler(_advancedNativeSampler);
            DestroyAdvancedProgram(_advancedEarlyVisibilityProgram);
            DestroyAdvancedProgram(_advancedBuildIndirectProgram);
            DestroyAdvancedProgram(_advancedDepthPyramidProgram);
            DestroyAdvancedProgram(_advancedLateVisibilityProgram);
            DestroyAdvancedProgram(_advancedVisibilityRasterProgram);
            DestroyAdvancedProgram(_advancedVisibilityMaskedRasterProgram);
            DestroyAdvancedProgram(_advancedGtaoProgram);
            DestroyAdvancedProgram(_advancedClassifyTilesProgram);
            DestroyAdvancedProgram(_advancedBuildClassificationIndirectProgram);
            DestroyAdvancedProgram(_advancedBuildFroxelsProgram);
            DestroyAdvancedProgram(_advancedShadeNativeOpaqueProgram);
            DestroyAdvancedProgram(_advancedShadeBackgroundProgram);
            DestroyAdvancedProgram(_advancedStereoIndirectProgram);
            DestroyAdvancedProgram(_advancedStereoRasterProgram);
            DestroyAdvancedProgram(_advancedStereoMaskedRasterProgram);
        }

        _advancedOutputRegistry = null;
        _advancedSceneUploader = null;
        _advancedInputStorage = null;
        _advancedAtlasVao = null;
        _advancedRasterFramebuffer = 0u;
        _advancedNativeSampler = 0u;
        _advancedEarlyVisibilityProgram = null;
        _advancedBuildIndirectProgram = null;
        _advancedDepthPyramidProgram = null;
        _advancedLateVisibilityProgram = null;
        _advancedVisibilityRasterProgram = null;
        _advancedVisibilityMaskedRasterProgram = null;
        _advancedGtaoProgram = null;
        _advancedClassifyTilesProgram = null;
        _advancedBuildClassificationIndirectProgram = null;
        _advancedBuildFroxelsProgram = null;
        _advancedShadeNativeOpaqueProgram = null;
        _advancedShadeBackgroundProgram = null;
        _advancedStageProgramFailure = null;
        _advancedStereoIndirectProgram = null;
        _advancedStereoRasterProgram = null;
        _advancedStereoMaskedRasterProgram = null;
        _advancedStereoProgramFailure = null;
        _advancedStereoReady = false;
    }

    private void DestroyAdvancedProgram(XRRenderProgram? program)
        => GenericToAPI<GLRenderProgram>(program)?.Destroy();

    private bool TryEnsureAdvancedRuntime(out string reason)
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            reason = "OpenGL Advanced runtime must initialize on the render thread.";
            return false;
        }
        _advancedOutputRegistry ??= new OpenGLAdvancedVisibilityOutputRegistry(this);
        _advancedInputStorage ??= new OpenGLAdvancedVisibilityInputStorage(65_536, 4_096);
        _advancedAtlasVao ??= new OpenGLAdvancedVisibilityAtlasVao(this);
        reason = "Ready";
        return true;
    }

    internal bool TryPrepareAdvancedVisibility(
        in AdvancedVisibilityStageBackendRequest request,
        out string reason)
    {
        if (!TryEnsureAdvancedRuntime(out reason) || !TryEnsureAdvancedStagePrograms(out reason))
            return false;
        if (request.SceneDatabase is null || request.BackendReadyPackage is null)
        {
            reason = "OpenGL Advanced preparation requires the sealed scene database and backend frame package.";
            return false;
        }
        OpenGLAdvancedVisibilityInputStorage inputStorage = _advancedInputStorage!;
        Commands.AdvancedSharedGpuSceneDatabase sceneDatabase = request.SceneDatabase;
        Commands.BackendReadyFramePackage backendPackage = request.BackendReadyPackage;
        AdvancedPreparationPublication publication = request.Publication;
        Commands.AdvancedGpuScenePublication scenePublication = publication.ScenePublication;
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null)
        {
            reason = "OpenGL Advanced preparation requires its current output pipeline.";
            return false;
        }
        if (!inputStorage.TryCapture(in request, out reason))
            return false;
        if (!_advancedOutputRegistry!.TryAcquireSlot(in reservation, out OpenGLAdvancedVisibilitySlot? slot, out reason) || slot is null)
            return false;
        slot.MonoTextureArrayAliases ??= new OpenGLAdvancedMonoTextureArrayAliasOwner(this);
        slot.MonoTextureArrayAliases.BeginReacquiredFamily(in reservation, pipeline.ResourceGeneration);
        OpenGLAdvancedSceneTableUploader sceneUploader = slot.SceneUploader ??= new(this);
        _advancedSceneUploader = sceneUploader;
        if (!sceneUploader.TryUpload(sceneDatabase, in scenePublication, out reason) ||
            !sceneUploader.TryUploadViews(backendPackage, request.Views, out reason))
            return false;
        if (!_advancedOutputRegistry.TryBindPersistentState(
                in reservation,
                inputStorage.PersistentStateByteLength,
                out reason))
        {
            return false;
        }

        // Inputs remain immutable for the sealed family. GPU-produced images
        // are distinct per fenced slot and explicitly cleared before either
        // producer can append work for a view.
        slot.Upload(this, 48u, inputStorage.Candidates);
        slot.Upload(this, 52u, inputStorage.Payloads);
        slot.Upload(this, 53u, inputStorage.Producers);
        slot.Upload(this, 54u, inputStorage.RangeIndices);
        slot.Upload(this, 55u, inputStorage.RangeOffsets);
        if (!inputStorage.TryInitializeCounters(sceneUploader, out reason))
        {
            return false;
        }
        slot.Upload(this, 57u, inputStorage.Counters);

        uint payloadCount = checked((uint)inputStorage.Payloads.Length);
        uint rangeCount = checked((uint)inputStorage.RangeOffsets.Length);
        uint viewCount = inputStorage.ViewCount;
        uint totalPayloadCount = checked(payloadCount * viewCount);
        uint totalRangeCount = checked(rangeCount * viewCount);
        slot.EnsureStorage(this, 50u, checked(totalPayloadCount * sizeof(uint)), clear: true);
        slot.EnsureStorage(this, 51u, checked(totalPayloadCount * sizeof(uint)), clear: true);
        slot.EnsureStorage(this, 56u, checked(totalRangeCount * sizeof(uint)), clear: true);
        slot.EnsureStorage(this, 58u, checked(totalPayloadCount * 20u), clear: true);
        slot.EnsureStorage(this, 59u, checked(totalPayloadCount * 12u), clear: true);
        slot.EnsureStorage(this, 60u, checked(totalPayloadCount * sizeof(uint)), clear: true);
        // Late visibility owns separate streams. Allocate their first-use
        // storage here so the later producer cannot observe the slot's
        // one-word initialization image.
        slot.EnsureStorage(this, 68u, checked(totalPayloadCount * sizeof(uint)), clear: true);
        slot.EnsureStorage(this, 69u, checked(totalRangeCount * sizeof(uint)), clear: true);
        slot.EnsureStorage(this, 70u, checked(totalPayloadCount * 20u), clear: true);
        slot.EnsureStorage(this, 73u, checked(totalPayloadCount * 12u), clear: true);
        slot.EnsureStorage(this, 74u, checked(totalPayloadCount * sizeof(uint)), clear: true);
        slot.Bind(this);
        if (!_advancedOutputRegistry.TryBindPersistentState(
                in reservation,
                inputStorage.PersistentStateByteLength,
                out reason))
            return false;
        if (!sceneUploader.TryBindVisibilityGeometry(out _, out _))
        {
            reason = "OpenGL Advanced preparation has no initialized canonical geometry atlas buffers.";
            return false;
        }
        if (!TryPrepareAdvancedVisibilityDeformation(
                inputStorage,
                sceneDatabase,
                in scenePublication,
                backendPackage,
                slot,
                out reason))
            return false;
        GLRenderProgram? early = GenericToAPI<GLRenderProgram>(_advancedEarlyVisibilityProgram);
        GLRenderProgram? indirect = GenericToAPI<GLRenderProgram>(_advancedBuildIndirectProgram);
        if (early is null || indirect is null)
        {
            reason = "OpenGL Advanced preparation programs are not linked.";
            return false;
        }

        uint earlyGroups = Math.Max(1u, (payloadCount + 255u) / 256u);
        Span<uint> push = stackalloc uint[4];
        for (uint view = 0u; view < viewCount; ++view)
        {
            push[0] = view;
            push[1] = checked(view * payloadCount);
            push[2] = payloadCount;
            push[3] = checked(view * rangeCount);
            slot.UploadUniform(this, 0u, push);
            if (!early.Use())
            {
                reason = "The OpenGL Advanced early-visibility program is not linked.";
                return false;
            }
            RawGL.DispatchCompute(earlyGroups, 1u, 1u);
        }
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        for (uint view = 0u; view < viewCount; ++view)
        {
            push[0] = view;
            push[1] = checked(view * payloadCount);
            push[2] = payloadCount;
            push[3] = checked(view * rangeCount);
            slot.UploadUniform(this, 0u, push);
            if (!indirect.Use())
            {
                reason = "The OpenGL Advanced indirect-expansion program is not linked.";
                return false;
            }
            RawGL.DispatchCompute(earlyGroups, 1u, 1u);
        }
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.CommandBarrierBit);
        reason = "Ready";
        return true;
    }

    internal bool TryCompleteAdvancedVisibilityFamily(
        in AdvancedVisibilityStageBackendRequest request,
        out string reason)
    {
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        if (_advancedOutputRegistry is null || _advancedSceneUploader is null ||
            !_advancedOutputRegistry.TryGetRetainedSlot(in reservation, out OpenGLAdvancedVisibilitySlot? slot) ||
            slot is null)
        {
            reason = "The OpenGL Advanced family has no retained preparation slot.";
            return false;
        }
        // This is the only ownership transfer point: all raster/compute work
        // for the sealed family precedes this fence on the same GL queue.
        slot.MarkSubmitted(this);
        _advancedOutputRegistry.Complete(in reservation);
        return _advancedSceneUploader.TryMarkCurrentSubmission(out reason);
    }

    /// <summary>
    /// Resolves every compute variant required by the native stage family.
    /// A pending asynchronous link is not treated as a usable program.
    /// </summary>
    internal bool TryEnsureAdvancedStagePrograms(out string reason)
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            reason = "OpenGL Advanced stage programs must initialize on the render thread.";
            return false;
        }
        if (_advancedStageProgramFailure is not null)
        {
            reason = _advancedStageProgramFailure;
            return false;
        }

        EAdvancedTextureIndirectionMode textureMode = SupportsBindlessTextureHandles
            ? EAdvancedTextureIndirectionMode.OpenGlBindlessHandles
            : EAdvancedTextureIndirectionMode.TextureArray;
        try
        {
            _advancedEarlyVisibilityProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Preparation.EarlyVisibility", "Advanced/Preparation/EarlyVisibility.comp", textureMode);
            _advancedBuildIndirectProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Preparation.BuildVisibilityIndirect", "Advanced/Preparation/BuildVisibilityIndirect.comp", textureMode);
            _advancedDepthPyramidProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Preparation.BuildDepthPyramid", "Advanced/Preparation/BuildDepthPyramid.comp", textureMode);
            _advancedLateVisibilityProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Preparation.LateVisibility", "Advanced/Preparation/LateVisibility.comp", textureMode);
            _advancedVisibilityRasterProgram ??= CreateAdvancedRasterProgram(textureMode);
            _advancedVisibilityMaskedRasterProgram ??= CreateAdvancedRasterProgram(textureMode, masked: true);
            _advancedGtaoProgram ??= CreateAdvancedComputeProgram(
                "Advanced.AO.Gtao", "Advanced/AO/Gtao.comp", textureMode);
            _advancedClassifyTilesProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Classification.ClassifyTiles", "Advanced/Classification/ClassifyTiles.comp", textureMode);
            _advancedBuildClassificationIndirectProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Classification.BuildIndirect", "Advanced/Classification/BuildClassificationIndirect.comp", textureMode);
            _advancedBuildFroxelsProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Lighting.BuildFroxels", "Advanced/Lighting/BuildFroxels.comp", textureMode);
            _advancedShadeNativeOpaqueProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Shading.NativeOpaque", "Advanced/Shading/ShadeNativeOpaque.comp", textureMode);
            _advancedShadeBackgroundProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Shading.Background", "Advanced/Shading/ShadeBackground.comp", textureMode);
        }
        catch (Exception exception)
        {
            _advancedStageProgramFailure =
                $"OpenGL Advanced stage program creation failed: {exception.Message}";
            reason = _advancedStageProgramFailure;
            return false;
        }

        if (!AreAdvancedStageProgramsLinked())
        {
            reason = "OpenGL Advanced stage programs are still compiling or linking.";
            return false;
        }

        reason = "Ready";
        return true;
    }

    internal GLRenderProgram? GetAdvancedStageProgram(
        EAdvancedRenderStage stage,
        EAdvancedVisibilityStageBackendPhase phase)
        => (stage, phase) switch
        {
            (EAdvancedRenderStage.VisibilityPreparation, EAdvancedVisibilityStageBackendPhase.Complete)
                => GenericToAPI<GLRenderProgram>(_advancedEarlyVisibilityProgram),
            (EAdvancedRenderStage.VisibilityRaster, EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility, EAdvancedVisibilityStageBackendPhase.LateRaster)
                => GenericToAPI<GLRenderProgram>(_advancedVisibilityRasterProgram),
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility, EAdvancedVisibilityStageBackendPhase.LateCompute)
                => GenericToAPI<GLRenderProgram>(_advancedDepthPyramidProgram),
            (EAdvancedRenderStage.WorkClassification, EAdvancedVisibilityStageBackendPhase.Complete)
                => GenericToAPI<GLRenderProgram>(_advancedClassifyTilesProgram),
            (EAdvancedRenderStage.AmbientOcclusion, EAdvancedVisibilityStageBackendPhase.Complete)
                => GenericToAPI<GLRenderProgram>(_advancedGtaoProgram),
            (EAdvancedRenderStage.NativeOpaqueShading, EAdvancedVisibilityStageBackendPhase.Complete)
                => GenericToAPI<GLRenderProgram>(_advancedShadeNativeOpaqueProgram),
            _ => null,
        };

    private XRRenderProgram CreateAdvancedComputeProgram(
        string name,
        string shaderPath,
        EAdvancedTextureIndirectionMode textureMode)
    {
        XRShader template = ShaderHelper.LoadEngineShader(shaderPath, EShaderType.Compute);
        uint pushBinding = shaderPath.StartsWith("Advanced/Preparation/", StringComparison.Ordinal)
            ? 0u
            : 1u;
        string source = ConvertOpenGlPushConstants(
            InjectAdvancedPreamble(
            ResolveAdvancedShaderSource(template),
            AdvancedShaderAccessLibrary.BuildPreamble(
                RuntimeGraphicsApiKind.OpenGL,
                textureMode) + "\n#define XR_ADV_VISIBILITY_ARRAY 1\n"),
            pushBinding);
        string sourcePath = string.IsNullOrWhiteSpace(template.Source.FilePath)
            ? shaderPath
            : template.Source.FilePath;
        XRShader shader = new(EShaderType.Compute, new TextFile(sourcePath) { Text = source });
        return new XRRenderProgram(true, false, shader) { Name = name };
    }

    private bool AreAdvancedStageProgramsLinked()
        => IsLinked(_advancedEarlyVisibilityProgram) &&
           IsLinked(_advancedBuildIndirectProgram) &&
           IsLinked(_advancedDepthPyramidProgram) &&
           IsLinked(_advancedLateVisibilityProgram) &&
           IsLinked(_advancedVisibilityRasterProgram) &&
           IsLinked(_advancedVisibilityMaskedRasterProgram) &&
           IsLinked(_advancedGtaoProgram) &&
           IsLinked(_advancedClassifyTilesProgram) &&
           IsLinked(_advancedBuildClassificationIndirectProgram) &&
           IsLinked(_advancedBuildFroxelsProgram) &&
           IsLinked(_advancedShadeNativeOpaqueProgram) &&
           IsLinked(_advancedShadeBackgroundProgram);

    private bool IsLinked(XRRenderProgram? program)
    {
        GLRenderProgram? glProgram = GenericToAPI<GLRenderProgram>(program);
        return glProgram is not null && glProgram.Use() && glProgram.IsLinked;
    }

    private XRRenderProgram CreateAdvancedRasterProgram(
        EAdvancedTextureIndirectionMode textureMode,
        bool masked = false,
        bool stereo = false)
    {
        string preamble = AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.OpenGL, textureMode);
        XRShader vertexTemplate = ShaderHelper.LoadEngineShader(
            AdvancedVisibilityShaderLibrary.Vertex, EShaderType.Vertex);
        XRShader fragmentTemplate = ShaderHelper.LoadEngineShader(
            masked ? AdvancedVisibilityShaderLibrary.MaskedFragment : AdvancedVisibilityShaderLibrary.OpaqueFragment, EShaderType.Fragment);
        XRShader vertex = new(EShaderType.Vertex, new TextFile(
            string.IsNullOrWhiteSpace(vertexTemplate.Source.FilePath)
                ? AdvancedVisibilityShaderLibrary.Vertex
                : vertexTemplate.Source.FilePath)
        {
            // The shared preamble declares record types. Required extensions
            // must precede those declarations on desktop GL drivers.
            Text = ConvertOpenGlPushConstants(InjectAdvancedPreamble(
                ResolveAdvancedShaderSource(vertexTemplate).Replace(
                    "#extension GL_ARB_shader_draw_parameters : require", string.Empty, StringComparison.Ordinal),
                "#extension GL_ARB_shader_draw_parameters : require\n" +
                (stereo ? "#extension GL_OVR_multiview2 : require\n#define XR_ADV_OPENGL_MULTIVIEW_RASTER 1\n" : string.Empty) + preamble), 2u)
        });
        XRShader fragment = new(EShaderType.Fragment, new TextFile(
            string.IsNullOrWhiteSpace(fragmentTemplate.Source.FilePath)
                ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                : fragmentTemplate.Source.FilePath)
        {
            Text = ConvertOpenGlPushConstants(InjectAdvancedPreamble(ResolveAdvancedShaderSource(fragmentTemplate), preamble), 2u)
        });
        return new XRRenderProgram(true, false, vertex, fragment)
        {
            Name = stereo ? (masked ? "Advanced.Visibility.StereoRasterMasked" : "Advanced.Visibility.StereoRaster") :
                masked ? "Advanced.Visibility.RasterMasked" : "Advanced.Visibility.Raster"
        };
    }

    private static string InjectAdvancedPreamble(string? source, string preamble)
    {
        // These stage templates already include the access library and are
        // resolved before specialization. Leaving the preamble's convenience
        // include here would reintroduce unlowered tables on the next resolve.
        preamble = preamble.Replace($"#include \"{AdvancedShaderAccessLibrary.IncludePath}\"", string.Empty, StringComparison.Ordinal);
        const string version = "#version";
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("The Advanced shader asset has no GLSL source.");
        int start = 0;
        while (start < source.Length && char.IsWhiteSpace(source[start]))
            ++start;
        if (!source.AsSpan(start).StartsWith(version, StringComparison.Ordinal))
            throw new InvalidOperationException("The Advanced shader asset does not begin with a GLSL version directive.");
        int lineEnd = source.IndexOfAny(['\r', '\n'], start);
        if (lineEnd < 0)
            return string.Concat(source, Environment.NewLine, preamble);
        int insertion = lineEnd;
        while (insertion < source.Length && (source[insertion] == '\r' || source[insertion] == '\n'))
            ++insertion;
        return string.Concat(source.AsSpan(0, insertion), preamble, source.AsSpan(insertion));
    }

    private static string ConvertOpenGlPushConstants(string source, uint binding)
        => OpenGLAdvancedSceneShaderLowering.Lower(GlslSnippetDeadCodeEliminator.TrimWholeSource(
            SpecializeOpenGlAdvancedIndexedSource(source.Replace(
                "layout(push_constant, std430) uniform",
                $"layout(std140, binding = {binding}) uniform",
                StringComparison.Ordinal)),
            ["main"]));

    private static string ResolveAdvancedShaderSource(XRShader shader)
    {
        if (!shader.TryGetResolvedSource(out string source, annotateIncludes: false, logFailures: true))
            throw new InvalidOperationException($"The Advanced shader '{shader.Source.FilePath}' has unresolved includes.");
        return source;
    }
}
