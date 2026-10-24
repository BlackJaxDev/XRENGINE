using Silk.NET.OpenGL;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private XRWindow? _worldUiWindow;
    private IRuntimeWindowGlContext? _worldUiContext;
    private XRRenderPipelineInstance? _worldUiPipeline;
    private XRCamera? _worldUiCamera;
    private RenderResourceGeneration? _worldUiGeneration;
    private UIPublication? _worldUiPublication;
    private XRFrameBuffer? _worldUiForwardFbo, _worldUiDeferredFbo;
    private XRTexture? _worldUiHdr, _worldUiDepth, _worldUiPost, _worldUiFinal,
        _worldUiAlbedo;
    private XRFrameBuffer? _worldUiPostFbo, _worldUiFinalFbo;
    private XRQuadFrameBuffer? _worldUiLightQuad, _worldUiPostQuad,
        _worldUiFinalQuad;
    private XRMaterial? _worldUiLightMaterial, _worldUiPostMaterial,
        _worldUiFinalMaterial;
    private readonly XRShader?[] _worldUiQuadVertex = new XRShader?[3];
    private readonly XRShader?[] _worldUiQuadFragment = new XRShader?[3];
    private readonly long[] _worldUiQuadVertexRevision = new long[3];
    private readonly long[] _worldUiQuadFragmentRevision = new long[3];
    private XRTexture? _worldUiStepSource;
    private XRFrameBuffer? _worldUiStepDestination;
    private XRMeshRenderer? _worldUiStepMesh;
    private XRMaterial? _worldUiStepMaterial;
    private UIPublishedWorldMeshCommand? _worldUiDrawCommand;
    private XRShader? _worldUiStepVertexShader, _worldUiStepFragmentShader;
    private long _worldUiStepVertexRevision, _worldUiStepFragmentRevision;
    private BoundingRectangle _worldUiRegion, _worldUiStepRegion;
    private RenderFrameViewDescriptor _worldUiFrozenView;
    private long _worldUiContextGeneration;
    private int _worldUiResourceGeneration, _worldUiDrawCount, _worldUiStepDrawCount;
    private ulong _worldUiCommandGeneration;
    private ulong _worldUiDispatch, _worldUiReceipt, _worldUiAttempt, _worldUiStep,
        _nextWorldUiAttempt, _nextWorldUiStep;
    private long _worldUiBackendGeneration;
    private EUIWorldOutputStage _worldUiStepStage;
    private int _worldUiPhase;
    private bool _worldUiFailed, _worldUiDepthCleared, _worldUiForwardCleared;
    private string? _worldUiFailure;
    private uint _worldUiForwardId, _worldUiDeferredId, _worldUiPostFboId,
        _worldUiFinalFboId, _worldUiHdrId, _worldUiDepthId,
        _worldUiPostId, _worldUiFinalId, _worldUiAlbedoId;
    private GLTexture2D? _worldUiHdrGl, _worldUiDepthGl, _worldUiPostGl,
        _worldUiFinalGl, _worldUiAlbedoGl;
    private readonly GLFrameBuffer?[] _worldUiFboWrappers = new GLFrameBuffer?[4];
    private readonly long[] _worldUiFboIncarnations = new long[4];
    private long _worldUiHdrObjectIncarnation, _worldUiDepthObjectIncarnation,
        _worldUiPostObjectIncarnation, _worldUiFinalObjectIncarnation,
        _worldUiAlbedoObjectIncarnation;
    private int _worldUiHdrStorage, _worldUiDepthStorage, _worldUiPostStorage,
        _worldUiFinalStorage, _worldUiAlbedoStorage;
    private long _worldUiHdrNativeWrite, _worldUiDepthNativeWrite,
        _worldUiPostNativeWrite, _worldUiFinalNativeWrite,
        _worldUiAlbedoNativeWrite;

    internal override bool SupportsCheckedWorldUiOutput => OperatingSystem.IsWindows();

    private OpenGLWorldUiReadiness? _worldUiReady;

    internal override void ObserveUiWorldOutputImageReadiness(
        XRWindow window, XRRenderPipelineInstance pipeline)
    {
        if (!SupportsCheckedWorldUiOutput || !AcceptsBackendWork ||
            RuntimeEngine.Rendering.Settings.AllowShaderPipelines ||
            !ReferenceEquals(window.Renderer, this) ||
            !HasCurrentUiBackdropWindowContext(window) ||
            pipeline.ActiveGeneration is not { } generation ||
            pipeline.PendingGeneration is not null ||
            !HasExistingWorldOutputResources(pipeline))
        {
            System.Threading.Volatile.Write(ref _worldUiReady, null);
            return;
        }

        IRuntimeWindowGlContext? context = window.DesktopGlContext;
        if (context is null || !HasCurrentUiBackdropWindowContext(window) ||
            !ReferenceEquals(generation, pipeline.ActiveGeneration))
        {
            System.Threading.Volatile.Write(ref _worldUiReady, null);
            return;
        }

        int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
        OpenGLWorldUiReadiness? prior =
            System.Threading.Volatile.Read(ref _worldUiReady);
        if (prior?.ShaderConfigVersion == shaderConfigVersion &&
            prior.Snapshot.Matches(window, pipeline, context, generation,
                context.OwnerGeneration, BackendGeneration,
                pipeline.ResourceGeneration,
                pipeline.Pipeline?.CommandGeneration ?? 0UL,
                hasPendingGeneration: false))
            return;

        System.Threading.Volatile.Write(ref _worldUiReady,
            new OpenGLWorldUiReadiness(
                new UIWorldOutputReadiness(window, pipeline, context, generation,
                    context.OwnerGeneration, BackendGeneration,
                    pipeline.ResourceGeneration,
                    pipeline.Pipeline?.CommandGeneration ?? 0UL),
                shaderConfigVersion));
    }

    internal override bool CanAdmitUiWorldOutputImage(
        XRWindow window, XRRenderPipelineInstance pipeline)
    {
        OpenGLWorldUiReadiness? ready =
            System.Threading.Volatile.Read(ref _worldUiReady);
        IRuntimeWindowGlContext? context = window.DesktopGlContext;
        return SupportsCheckedWorldUiOutput && AcceptsBackendWork &&
            !RuntimeEngine.Rendering.Settings.AllowShaderPipelines &&
            ReferenceEquals(window.Renderer, this) && ready is not null &&
            ready.ShaderConfigVersion == RuntimeEngine.Rendering.Settings.ShaderConfigVersion &&
            ready.Snapshot.Matches(window, pipeline, context,
                pipeline.ActiveGeneration, context?.OwnerGeneration ?? 0L,
                BackendGeneration, pipeline.ResourceGeneration,
                pipeline.Pipeline?.CommandGeneration ?? 0UL,
                pipeline.PendingGeneration is not null);
    }

    private bool HasExistingWorldOutputResources(XRRenderPipelineInstance pipeline)
        => pipeline.Pipeline is DefaultRenderPipeline profile &&
            pipeline.GetFBO<XRQuadFrameBuffer>(DefaultRenderPipeline.LightCombineFBOName) is { } light &&
            profile.IsCurrentWorldUiFactoryQuad(light) &&
            pipeline.GetFBO<XRQuadFrameBuffer>(DefaultRenderPipeline.PostProcessFBOName) is { } postQuad &&
            profile.IsCurrentWorldUiFactoryQuad(postQuad) &&
            pipeline.GetFBO<XRQuadFrameBuffer>(DefaultRenderPipeline.FinalPostProcessFBOName) is { } finalQuad &&
            profile.IsCurrentWorldUiFactoryQuad(finalQuad) &&
            ExistingWorldFbo(pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.ForwardPassFBOName)) &&
            ExistingWorldFbo(pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.DeferredGBufferFBOName)) &&
            ExistingWorldFbo(pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.PostProcessOutputFBOName)) &&
            ExistingWorldFbo(pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.FinalPostProcessOutputFBOName)) &&
            ExistingWorldTexture(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.HDRSceneTextureName)) &&
            ExistingWorldTexture(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DepthStencilTextureName)) &&
            ExistingWorldTexture(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.AlbedoOpacityTextureName)) &&
            ExistingWorldTexture(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.PostProcessOutputTextureName)) &&
            ExistingWorldTexture(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.FinalPostProcessOutputTextureName));

    private bool ExistingWorldFbo(XRFrameBuffer? fbo)
        => fbo is not null && TryGetAPIRenderObject(fbo, out var existing) &&
            existing is GLFrameBuffer wrapper &&
            wrapper.TryGetBindingId(out uint id) && id != 0u &&
            RawGL.CheckNamedFramebufferStatus(id, GLEnum.DrawFramebuffer) == GLEnum.FramebufferComplete;

    private bool ExistingWorldTexture(XRTexture? texture)
        => texture is XRTexture2D mono &&
            TryGetAPIRenderObject(mono, out var existing) &&
            existing is GLTexture2D wrapper &&
            wrapper.TryGetBindingId(out uint id) && id != 0u &&
            wrapper.CurrentStorageGeneration > 0;

    internal override bool TryBeginUiWorldOutputAttempt(XRWindow window,
        XRRenderPipelineInstance pipeline, UIPublication publication,
        BoundingRectangle region, ulong dispatchToken, ulong receiptId,
        in RenderFrameViewDescriptor frozenView,
        out ulong token)
    {
        token = 0UL;
        if (_worldUiAttempt != 0UL || !AcceptsBackendWork ||
            RuntimeEngine.Rendering.Settings.AllowShaderPipelines ||
            !ReferenceEquals(XRWindow, window) ||
            !ReferenceEquals(window.Renderer, this) ||
            !HasCurrentUiBackdropWindowContext(window) ||
            dispatchToken == 0UL || dispatchToken != window.ActiveOpenGlDispatchToken ||
            receiptId == 0UL || region.Width <= 0 || region.Height <= 0 ||
            pipeline.Pipeline is not DefaultRenderPipeline profile ||
            window.Viewports.Count != 1 ||
            window.Viewports[0].ActiveCamera is not { } camera ||
            !profile.SupportsRetainedWorldUiColorProfile(camera) ||
            pipeline.ActiveGeneration is not { } generation ||
            pipeline.PendingGeneration is not null ||
            publication.WorldCommands.Length == 0)
            return RejectWorldUiBegin();

        XRFrameBuffer? forward = pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.ForwardPassFBOName);
        XRFrameBuffer? deferred = pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.DeferredGBufferFBOName);
        XRFrameBuffer? postFbo = pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.PostProcessOutputFBOName);
        XRFrameBuffer? finalFbo = pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.FinalPostProcessOutputFBOName);
        XRQuadFrameBuffer? lightQuad = pipeline.GetFBO<XRQuadFrameBuffer>(DefaultRenderPipeline.LightCombineFBOName);
        XRQuadFrameBuffer? postQuad = pipeline.GetFBO<XRQuadFrameBuffer>(DefaultRenderPipeline.PostProcessFBOName);
        XRQuadFrameBuffer? finalQuad = pipeline.GetFBO<XRQuadFrameBuffer>(DefaultRenderPipeline.FinalPostProcessFBOName);
        XRTexture? hdr = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.HDRSceneTextureName);
        XRTexture? albedo = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.AlbedoOpacityTextureName);
        XRTexture? depth = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DepthStencilTextureName);
        XRTexture? post = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.PostProcessOutputTextureName);
        XRTexture? final = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.FinalPostProcessOutputTextureName);
        if (forward is null || deferred is null || postFbo is null || finalFbo is null ||
            lightQuad is null || lightQuad.Material is null ||
            postQuad is null || postQuad.Material is null ||
            finalQuad is null || finalQuad.Material is null ||
            !SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(lightQuad.FullScreenMesh.Mesh) ||
            !SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(postQuad.FullScreenMesh.Mesh) ||
            !SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(finalQuad.FullScreenMesh.Mesh) ||
            !profile.IsCurrentWorldUiFactoryQuad(lightQuad) ||
            !profile.IsCurrentWorldUiFactoryQuad(postQuad) ||
            !profile.IsCurrentWorldUiFactoryQuad(finalQuad) ||
            hdr is null || albedo is null || depth is null || post is null || final is null ||
            !HasWorldUiDepthTarget(deferred, depth) ||
            !HasWorldUiTargets(forward, hdr, depth) ||
            !HasWorldUiTargets(postFbo, post, null) ||
            !HasWorldUiTargets(finalFbo, final, null) ||
            !TryNativeWorldFbo(forward, out uint forwardId) ||
            !TryNativeWorldFbo(deferred, out uint deferredId) ||
            !TryNativeWorldFbo(postFbo, out uint postFboId) ||
            !TryNativeWorldFbo(finalFbo, out uint finalFboId) ||
            !TryNativeWorldTexture(hdr, out uint hdrId) ||
            !TryNativeWorldTexture(albedo, out uint albedoId) ||
            !TryNativeWorldTexture(depth, out uint depthId) ||
            !TryNativeWorldTexture(post, out uint postId) ||
            !TryNativeWorldTexture(final, out uint finalId) ||
            !WorldUiNativeTextureSizeMatches(hdrId, region) ||
            !WorldUiNativeTextureSizeMatches(depthId, region) ||
            !WorldUiNativeTextureSizeMatches(postId, region) ||
            !WorldUiNativeTextureSizeMatches(finalId, region))
            return RejectWorldUiBegin();
        if (!HasCurrentUiBackdropWindowContext(window) ||
            dispatchToken != window.ActiveOpenGlDispatchToken ||
            !ReferenceEquals(generation, pipeline.ActiveGeneration))
            return RejectWorldUiBegin();

        token = unchecked(++_nextWorldUiAttempt);
        if (token == 0UL)
            token = unchecked(++_nextWorldUiAttempt);
        _worldUiAttempt = token;
        _worldUiWindow = window;
        _worldUiContext = window.DesktopGlContext;
        _worldUiContextGeneration = _worldUiContext?.OwnerGeneration ?? 0L;
        _worldUiPipeline = pipeline;
        _worldUiCamera = camera;
        _worldUiGeneration = generation;
        _worldUiResourceGeneration = pipeline.ResourceGeneration;
        _worldUiCommandGeneration = pipeline.Pipeline.CommandGeneration;
        _worldUiBackendGeneration = BackendGeneration;
        _worldUiPublication = publication;
        _worldUiForwardFbo = forward;
        _worldUiDeferredFbo = deferred;
        _worldUiPostFbo = postFbo;
        _worldUiFinalFbo = finalFbo;
        _worldUiLightQuad = lightQuad;
        _worldUiPostQuad = postQuad;
        _worldUiFinalQuad = finalQuad;
        _worldUiLightMaterial = lightQuad.Material;
        _worldUiPostMaterial = postQuad.Material;
        _worldUiFinalMaterial = finalQuad.Material;
        _worldUiHdr = hdr;
        _worldUiAlbedo = albedo;
        _worldUiDepth = depth;
        _worldUiPost = post;
        _worldUiFinal = final;
        _worldUiForwardId = forwardId;
        _worldUiDeferredId = deferredId;
        _worldUiPostFboId = postFboId;
        _worldUiFinalFboId = finalFboId;
        _worldUiHdrId = hdrId;
        _worldUiDepthId = depthId;
        _worldUiPostId = postId;
        _worldUiFinalId = finalId;
        _worldUiAlbedoId = albedoId;
        _worldUiFboWrappers[0] = GenericToAPI<GLFrameBuffer>(forward);
        _worldUiFboWrappers[1] = GenericToAPI<GLFrameBuffer>(deferred);
        _worldUiFboWrappers[2] = GenericToAPI<GLFrameBuffer>(postFbo);
        _worldUiFboWrappers[3] = GenericToAPI<GLFrameBuffer>(finalFbo);
        for (int index = 0; index < _worldUiFboWrappers.Length; index++)
            _worldUiFboIncarnations[index] =
                _worldUiFboWrappers[index]?.NativeObjectIncarnation ?? 0L;
        _worldUiHdrGl = GenericToAPI<GLTexture2D>((XRTexture2D)hdr);
        _worldUiDepthGl = GenericToAPI<GLTexture2D>((XRTexture2D)depth);
        _worldUiPostGl = GenericToAPI<GLTexture2D>((XRTexture2D)post);
        _worldUiFinalGl = GenericToAPI<GLTexture2D>((XRTexture2D)final);
        _worldUiAlbedoGl = GenericToAPI<GLTexture2D>((XRTexture2D)albedo);
        _worldUiHdrObjectIncarnation = _worldUiHdrGl?.NativeObjectIncarnation ?? 0L;
        _worldUiDepthObjectIncarnation = _worldUiDepthGl?.NativeObjectIncarnation ?? 0L;
        _worldUiPostObjectIncarnation = _worldUiPostGl?.NativeObjectIncarnation ?? 0L;
        _worldUiFinalObjectIncarnation = _worldUiFinalGl?.NativeObjectIncarnation ?? 0L;
        _worldUiAlbedoObjectIncarnation = _worldUiAlbedoGl?.NativeObjectIncarnation ?? 0L;
        _worldUiHdrStorage = _worldUiHdrGl?.CurrentStorageGeneration ?? -1;
        _worldUiDepthStorage = _worldUiDepthGl?.CurrentStorageGeneration ?? -1;
        _worldUiPostStorage = _worldUiPostGl?.CurrentStorageGeneration ?? -1;
        _worldUiFinalStorage = _worldUiFinalGl?.CurrentStorageGeneration ?? -1;
        _worldUiAlbedoStorage = _worldUiAlbedoGl?.CurrentStorageGeneration ?? -1;
        _worldUiHdrNativeWrite = _worldUiHdrGl?.NativeWriteGeneration ?? -1L;
        _worldUiDepthNativeWrite = _worldUiDepthGl?.NativeWriteGeneration ?? -1L;
        _worldUiPostNativeWrite = _worldUiPostGl?.NativeWriteGeneration ?? -1L;
        _worldUiFinalNativeWrite = _worldUiFinalGl?.NativeWriteGeneration ?? -1L;
        _worldUiAlbedoNativeWrite = _worldUiAlbedoGl?.NativeWriteGeneration ?? -1L;
        _worldUiDispatch = dispatchToken;
        _worldUiReceipt = receiptId;
        _worldUiRegion = region;
        _worldUiFrozenView = frozenView;
        _worldUiPhase = 0;
        _worldUiDrawCount = 0;
        _worldUiFailed = false;
        _worldUiDepthCleared = _worldUiForwardCleared = false;
        _worldUiFailure = null;
        if (_worldUiHdrObjectIncarnation <= 0 ||
            _worldUiDepthObjectIncarnation <= 0 ||
            _worldUiPostObjectIncarnation <= 0 ||
            _worldUiFinalObjectIncarnation <= 0 ||
            _worldUiAlbedoObjectIncarnation <= 0 ||
            _worldUiFboIncarnations[0] <= 0 ||
            _worldUiFboIncarnations[1] <= 0 ||
            _worldUiFboIncarnations[2] <= 0 ||
            _worldUiFboIncarnations[3] <= 0 ||
            _worldUiHdrStorage <= 0 || _worldUiDepthStorage <= 0 ||
            _worldUiPostStorage <= 0 || _worldUiFinalStorage <= 0 ||
            _worldUiAlbedoStorage <= 0 ||
            !CaptureWorldUiQuadShader(0, _worldUiLightMaterial) ||
            !CaptureWorldUiQuadShader(1, _worldUiPostMaterial) ||
            !CaptureWorldUiQuadShader(2, _worldUiFinalMaterial) ||
            !WorldUiAttachmentMatches(deferredId, GLEnum.DepthStencilAttachment, depth) ||
            !WorldUiAttachmentMatches(forwardId, GLEnum.ColorAttachment0, hdr) ||
            !WorldUiAttachmentMatches(forwardId, GLEnum.DepthStencilAttachment, depth) ||
            !WorldUiAttachmentMatches(postFboId, GLEnum.ColorAttachment0, post) ||
            !WorldUiAttachmentMatches(finalFboId, GLEnum.ColorAttachment0, final) ||
            Api.GetError() != GLEnum.NoError)
        {
            AbortUiWorldOutputAttempt(token);
            token = 0UL;
            return RejectWorldUiBegin();
        }
        return true;
    }

    private bool RejectWorldUiBegin()
    {
        System.Threading.Volatile.Write(ref _worldUiReady, null);
        return false;
    }

    private static bool HasWorldUiTargets(XRFrameBuffer fbo, XRTexture color, XRTexture? depth)
    {
        var targets = fbo.Targets;
        if (targets is null || targets.Length != (depth is null ? 1 : 2) ||
            !ReferenceEquals(targets[0].Target, color) ||
            targets[0].Attachment != EFrameBufferAttachment.ColorAttachment0)
            return false;
        return depth is null ||
            ReferenceEquals(targets[1].Target, depth) &&
            targets[1].Attachment == EFrameBufferAttachment.DepthStencilAttachment;
    }

    private static bool HasWorldUiDepthTarget(XRFrameBuffer? fbo, XRTexture depth)
    {
        if (fbo?.Targets is not { } targets)
            return false;
        for (int index = 0; index < targets.Length; index++)
            if (targets[index].Attachment == EFrameBufferAttachment.DepthStencilAttachment)
                return ReferenceEquals(targets[index].Target, depth);
        return false;
    }

    private bool CaptureWorldUiQuadShader(int index, XRMaterial? material)
    {
        if (material is null || material.Shaders.Count != 2 ||
            material.VertexShaders.Count != 1 ||
            material.FragmentShaders.Count != 1 ||
            material.GeometryShaders.Count != 0 ||
            material.TessCtrlShaders.Count != 0 ||
            material.TessEvalShaders.Count != 0)
            return false;
        _worldUiQuadVertex[index] = material.VertexShaders[0];
        _worldUiQuadFragment[index] = material.FragmentShaders[0];
        _worldUiQuadVertexRevision[index] = material.VertexShaders[0].SourceRevision;
        _worldUiQuadFragmentRevision[index] = material.FragmentShaders[0].SourceRevision;
        return true;
    }

    private bool WorldUiQuadShaderCurrent(EUIWorldOutputStage stage,
        XRMaterial material)
    {
        int index = stage switch
        {
            EUIWorldOutputStage.LightCombine => 0,
            EUIWorldOutputStage.PostProcess => 1,
            EUIWorldOutputStage.FinalPostProcess => 2,
            _ => -1,
        };
        XRQuadFrameBuffer? quad = stage switch
        {
            EUIWorldOutputStage.LightCombine => _worldUiLightQuad,
            EUIWorldOutputStage.PostProcess => _worldUiPostQuad,
            EUIWorldOutputStage.FinalPostProcess => _worldUiFinalQuad,
            _ => null,
        };
        return index >= 0 && quad is not null &&
            _worldUiPipeline?.Pipeline is DefaultRenderPipeline profile &&
            profile.IsCurrentWorldUiFactoryQuad(quad) &&
            material.Shaders.Count == 2 &&
            material.VertexShaders.Count == 1 && material.FragmentShaders.Count == 1 &&
            material.GeometryShaders.Count == 0 &&
            material.TessCtrlShaders.Count == 0 &&
            material.TessEvalShaders.Count == 0 &&
            ReferenceEquals(material.VertexShaders[0], _worldUiQuadVertex[index]) &&
            ReferenceEquals(material.FragmentShaders[0], _worldUiQuadFragment[index]) &&
            material.VertexShaders[0].SourceRevision == _worldUiQuadVertexRevision[index] &&
            material.FragmentShaders[0].SourceRevision == _worldUiQuadFragmentRevision[index];
    }

    private bool WorldUiOwnerCurrent()
        => !_worldUiFailed && _worldUiAttempt != 0UL && AcceptsBackendWork &&
            ReferenceEquals(Current, this) &&
            _worldUiWindow is { } window &&
            ReferenceEquals(XRWindow, window) &&
            ReferenceEquals(window.Renderer, this) &&
            ReferenceEquals(window.DesktopGlContext, _worldUiContext) &&
            _worldUiContextGeneration == _worldUiContext?.OwnerGeneration &&
            _worldUiDispatch != 0UL && _worldUiDispatch == window.ActiveOpenGlDispatchToken &&
            HasCurrentUiBackdropWindowContext(window) &&
            _worldUiBackendGeneration == BackendGeneration &&
            _worldUiPipeline is { } pipeline &&
            _worldUiCamera is { } camera &&
            window.Viewports.Count == 1 &&
            ReferenceEquals(window.Viewports[0].ActiveCamera, camera) &&
            pipeline.Pipeline is DefaultRenderPipeline profile &&
            profile.SupportsRetainedWorldUiColorProfile(camera) &&
            (pipeline.RenderState.SceneCamera is null ||
                ReferenceEquals(pipeline.RenderState.SceneCamera, camera)) &&
            ReferenceEquals(pipeline.ActiveGeneration, _worldUiGeneration) &&
            pipeline.PendingGeneration is null &&
            pipeline.ResourceGeneration == _worldUiResourceGeneration &&
            pipeline.Pipeline?.CommandGeneration == _worldUiCommandGeneration &&
            WorldUiNativeResourcesCurrent();

    private bool WorldUiNativeResourcesCurrent()
        => _worldUiForwardFbo is not null &&
            _worldUiDeferredFbo is not null &&
            _worldUiPostFbo is not null &&
            _worldUiFinalFbo is not null &&
            _worldUiHdr is not null && _worldUiDepth is not null &&
            _worldUiPost is not null && _worldUiFinal is not null &&
            _worldUiAlbedo is not null &&
            WorldUiNativeFboOwnerCurrent(0, _worldUiForwardFbo) &&
            WorldUiNativeFboOwnerCurrent(1, _worldUiDeferredFbo) &&
            WorldUiNativeFboOwnerCurrent(2, _worldUiPostFbo) &&
            WorldUiNativeFboOwnerCurrent(3, _worldUiFinalFbo) &&
            TryNativeWorldFbo(_worldUiForwardFbo, out uint forward) &&
            forward == _worldUiForwardId &&
            TryNativeWorldFbo(_worldUiDeferredFbo, out uint deferred) &&
            deferred == _worldUiDeferredId &&
            TryNativeWorldFbo(_worldUiPostFbo, out uint postFbo) &&
            postFbo == _worldUiPostFboId &&
            TryNativeWorldFbo(_worldUiFinalFbo, out uint finalFbo) &&
            finalFbo == _worldUiFinalFboId &&
            TryNativeWorldTexture(_worldUiHdr, out uint hdr) &&
            hdr == _worldUiHdrId &&
            TryNativeWorldTexture(_worldUiDepth, out uint depth) &&
            depth == _worldUiDepthId &&
            TryNativeWorldTexture(_worldUiPost, out uint post) &&
            post == _worldUiPostId &&
            TryNativeWorldTexture(_worldUiFinal, out uint final) &&
            final == _worldUiFinalId &&
            TryNativeWorldTexture(_worldUiAlbedo, out uint albedo) &&
            albedo == _worldUiAlbedoId &&
            ReferenceEquals(_worldUiHdrGl, GenericToAPI<GLTexture2D>((XRTexture2D)_worldUiHdr)) &&
            _worldUiHdrGl?.NativeObjectIncarnation == _worldUiHdrObjectIncarnation &&
            _worldUiHdrGl?.CurrentStorageGeneration == _worldUiHdrStorage &&
            _worldUiHdrGl?.NativeWriteGeneration == _worldUiHdrNativeWrite &&
            ReferenceEquals(_worldUiDepthGl, GenericToAPI<GLTexture2D>((XRTexture2D)_worldUiDepth)) &&
            _worldUiDepthGl?.NativeObjectIncarnation == _worldUiDepthObjectIncarnation &&
            _worldUiDepthGl?.CurrentStorageGeneration == _worldUiDepthStorage &&
            _worldUiDepthGl?.NativeWriteGeneration == _worldUiDepthNativeWrite &&
            ReferenceEquals(_worldUiPostGl, GenericToAPI<GLTexture2D>((XRTexture2D)_worldUiPost)) &&
            _worldUiPostGl?.NativeObjectIncarnation == _worldUiPostObjectIncarnation &&
            _worldUiPostGl?.CurrentStorageGeneration == _worldUiPostStorage &&
            _worldUiPostGl?.NativeWriteGeneration == _worldUiPostNativeWrite &&
            ReferenceEquals(_worldUiFinalGl, GenericToAPI<GLTexture2D>((XRTexture2D)_worldUiFinal)) &&
            _worldUiFinalGl?.NativeObjectIncarnation == _worldUiFinalObjectIncarnation &&
            _worldUiFinalGl?.CurrentStorageGeneration == _worldUiFinalStorage &&
            _worldUiFinalGl?.NativeWriteGeneration == _worldUiFinalNativeWrite &&
            ReferenceEquals(_worldUiAlbedoGl, GenericToAPI<GLTexture2D>((XRTexture2D)_worldUiAlbedo)) &&
            _worldUiAlbedoGl?.NativeObjectIncarnation == _worldUiAlbedoObjectIncarnation &&
            _worldUiAlbedoGl?.CurrentStorageGeneration == _worldUiAlbedoStorage &&
            _worldUiAlbedoGl?.NativeWriteGeneration == _worldUiAlbedoNativeWrite;

    private bool WorldUiNativeFboOwnerCurrent(int index, XRFrameBuffer fbo)
        => _worldUiFboWrappers[index] is { } wrapper &&
            ReferenceEquals(wrapper, GenericToAPI<GLFrameBuffer>(fbo)) &&
            wrapper.NativeObjectIncarnation == _worldUiFboIncarnations[index];

    private bool TryNativeWorldFbo(XRFrameBuffer fbo, out uint id)
    {
        id = 0u;
        return GenericToAPI<GLFrameBuffer>(fbo)?.TryGetBindingId(out id) == true && id != 0u;
    }

    private bool TryNativeWorldTexture(XRTexture texture, out uint id)
    {
        id = 0u;
        return texture is XRTexture2D mono &&
            !mono.SparseTextureStreamingEnabled &&
            !mono.UsesOpenGlExternalMemoryImport &&
            !mono.RuntimeManagedProgressiveUploadActive &&
            !mono.RuntimeManagedProgressiveFinalizePending &&
            GenericToAPI<GLTexture2D>(mono)?.TryGetBindingId(out id) == true && id != 0u;
    }

    private bool WorldUiNativeTextureSizeMatches(uint texture, BoundingRectangle region)
    {
        RawGL.GetTextureLevelParameter(texture, 0, GLEnum.TextureWidth, out int width);
        RawGL.GetTextureLevelParameter(texture, 0, GLEnum.TextureHeight, out int height);
        return width == region.Width && height == region.Height;
    }

    private uint WorldUiPinnedFboId(XRFrameBuffer fbo)
        => ReferenceEquals(fbo, _worldUiForwardFbo) ? _worldUiForwardId :
            ReferenceEquals(fbo, _worldUiDeferredFbo) ? _worldUiDeferredId :
            ReferenceEquals(fbo, _worldUiPostFbo) ? _worldUiPostFboId :
            ReferenceEquals(fbo, _worldUiFinalFbo) ? _worldUiFinalFboId : 0u;

    private uint WorldUiPinnedTextureId(XRTexture texture)
        => ReferenceEquals(texture, _worldUiHdr) ? _worldUiHdrId :
            ReferenceEquals(texture, _worldUiDepth) ? _worldUiDepthId :
            ReferenceEquals(texture, _worldUiPost) ? _worldUiPostId :
            ReferenceEquals(texture, _worldUiFinal) ? _worldUiFinalId :
            ReferenceEquals(texture, _worldUiAlbedo) ? _worldUiAlbedoId : 0u;

    private bool IsWorldUiTrackedOutputFbo(int binding)
        => binding != 0 && (binding == (int)_worldUiForwardId ||
            binding == (int)_worldUiPostFboId ||
            binding == (int)_worldUiFinalFboId);

    private bool WorldUiFboAliasesTrackedTexture(uint framebuffer)
    {
        if (framebuffer == 0u)
            return false;
        int attachmentCount = Math.Min(
            RawGL.GetInteger(GLEnum.MaxColorAttachments), 32);
        for (int attachment = 0; attachment < attachmentCount; attachment++)
        {
            RawGL.GetNamedFramebufferAttachmentParameter(framebuffer,
                (GLEnum)((int)GLEnum.ColorAttachment0 + attachment),
                GLEnum.FramebufferAttachmentObjectName, out int objectId);
            if (objectId == (int)_worldUiHdrId ||
                objectId == (int)_worldUiPostId ||
                objectId == (int)_worldUiFinalId)
                return true;
        }
        RawGL.GetNamedFramebufferAttachmentParameter(framebuffer,
            GLEnum.DepthStencilAttachment,
            GLEnum.FramebufferAttachmentObjectName, out int depthId);
        if (depthId == (int)_worldUiDepthId)
            return true;
        RawGL.GetNamedFramebufferAttachmentParameter(framebuffer,
            GLEnum.DepthAttachment,
            GLEnum.FramebufferAttachmentObjectName, out depthId);
        if (depthId == (int)_worldUiDepthId)
            return true;
        RawGL.GetNamedFramebufferAttachmentParameter(framebuffer,
            GLEnum.StencilAttachment,
            GLEnum.FramebufferAttachmentObjectName, out depthId);
        return depthId == (int)_worldUiDepthId;
    }

    private void InvalidateWorldUiOnBlitDestination(uint destinationId)
    {
        if (_worldUiAttempt == 0UL ||
            (_worldUiPhase < 1 && !_worldUiDepthCleared))
            return;
        if ((_worldUiPhase >= 1 && destinationId == 0u) ||
            destinationId == _worldUiDeferredId ||
            IsWorldUiTrackedOutputFbo((int)destinationId) ||
            WorldUiFboAliasesTrackedTexture(destinationId))
            FailWorldUi("A native blit wrote a pinned world UI output outside its checked stages.");
    }

    private bool WorldUiFboMatches(XRFrameBuffer fbo, XRTexture color, XRTexture? depth)
    {
        if (!TryNativeWorldFbo(fbo, out uint id) ||
            id != WorldUiPinnedFboId(fbo) ||
            RawGL.GetInteger(GLEnum.DrawFramebufferBinding) != (int)id ||
            RawGL.CheckNamedFramebufferStatus(id, GLEnum.DrawFramebuffer) != GLEnum.FramebufferComplete ||
            !WorldUiAttachmentMatches(id, GLEnum.ColorAttachment0, color))
            return false;
        return depth is null || WorldUiAttachmentMatches(id, GLEnum.DepthStencilAttachment, depth);
    }

    private bool WorldUiAttachmentMatches(uint fbo, GLEnum attachment, XRTexture texture)
    {
        if (!TryNativeWorldTexture(texture, out uint expected))
            return false;
        if (_worldUiAttempt != 0UL && expected != WorldUiPinnedTextureId(texture))
            return false;
        RawGL.GetNamedFramebufferAttachmentParameter(fbo, attachment,
            GLEnum.FramebufferAttachmentObjectType, out int type);
        RawGL.GetNamedFramebufferAttachmentParameter(fbo, attachment,
            GLEnum.FramebufferAttachmentObjectName, out int name);
        RawGL.GetNamedFramebufferAttachmentParameter(fbo, attachment,
            GLEnum.FramebufferAttachmentTextureLevel, out int level);
        return type == (int)GLEnum.Texture && name == (int)expected &&
            level == 0 && WorldUiNativeTextureSizeMatches(expected, _worldUiRegion);
    }

    private unsafe bool WorldUiRegionMatches(BoundingRectangle region)
    {
        int* viewport = stackalloc int[4];
        RawGL.GetInteger(GLEnum.Viewport, viewport);
        if (viewport[0] != region.X || viewport[1] != region.Y ||
            viewport[2] != region.Width || viewport[3] != region.Height)
            return false;
        if (!RawGL.IsEnabled(GLEnum.ScissorTest))
            return true;
        int* scissor = stackalloc int[4];
        RawGL.GetInteger(GLEnum.ScissorBox, scissor);
        return scissor[0] == region.X && scissor[1] == region.Y &&
            scissor[2] == region.Width && scissor[3] == region.Height;
    }

    private unsafe bool WorldUiClearStateMatches(bool depthStencil)
    {
        if (!WorldUiRegionMatches(_worldUiRegion))
            return false;
        int count = depthStencil ? 5 : 1;
        int* mask = stackalloc int[4];
        for (int attachment = 0; attachment < count; attachment++)
        {
            RawGL.GetInteger(GLEnum.ColorWritemask, (uint)attachment, mask);
            if (mask[0] == 0 || mask[1] == 0 || mask[2] == 0 || mask[3] == 0)
                return false;
        }
        if (!depthStencil)
            return true;
        RawGL.GetNamedFramebufferAttachmentParameter(_worldUiDeferredId,
            GLEnum.DepthStencilAttachment,
            GLEnum.FramebufferAttachmentStencilSize, out int stencilBits);
        return RawGL.GetBoolean(GLEnum.DepthWritemask) &&
            UIWorldStencilMask.CoversAttachment(
                RawGL.GetInteger(GLEnum.StencilWritemask), stencilBits) &&
            RawGL.GetFloat(GLEnum.DepthClearValue) == 1.0f &&
            RawGL.GetInteger(GLEnum.StencilClearValue) == 0;
    }

    private unsafe bool WorldUiColorWritable()
    {
        int* mask = stackalloc int[4];
        RawGL.GetInteger(GLEnum.ColorWritemask, 0, mask);
        return mask[0] != 0 && mask[1] != 0 &&
            mask[2] != 0 && mask[3] != 0;
    }

    private void FailWorldUi(string reason)
    {
        _worldUiFailed = true;
        _worldUiFailure ??= reason;
        System.Threading.Volatile.Write(ref _worldUiReady, null);
    }

    private bool WorldUiStepNativeMatches(GLMeshRenderer mesh, EPrimitiveType topology,
        uint indexCount, uint instances, XRMaterial? material,
        XRRenderProgram? program, uint nativeProgramId)
    {
        if (!WorldUiOwnerCurrent() || _worldUiStep == 0UL ||
            _worldUiStepDrawCount != 0 ||
            !ReferenceEquals(mesh.MeshRenderer, _worldUiStepMesh) ||
            !ReferenceEquals(material, _worldUiStepMaterial) ||
            !SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(mesh.Mesh) ||
            _worldUiStepVertexShader is null ||
            _worldUiStepFragmentShader is null ||
            _worldUiStepVertexShader.SourceRevision != _worldUiStepVertexRevision ||
            _worldUiStepFragmentShader.SourceRevision != _worldUiStepFragmentRevision ||
            (_worldUiStepStage != EUIWorldOutputStage.WindowPresent &&
                (_worldUiStepMaterial is null ||
                 !WorldUiQuadShaderCurrent(_worldUiStepStage, _worldUiStepMaterial))) ||
            program is null || program.Shaders.Count != 2 ||
            (!ReferenceEquals(program.Shaders[0], _worldUiStepVertexShader) &&
                !ReferenceEquals(program.Shaders[1], _worldUiStepVertexShader)) ||
            (!ReferenceEquals(program.Shaders[0], _worldUiStepFragmentShader) &&
                !ReferenceEquals(program.Shaders[1], _worldUiStepFragmentShader)) ||
            mesh.ConditionalRenderQuery is not null ||
            topology != EPrimitiveType.Triangles || indexCount != 3u ||
            instances != 1u || nativeProgramId == 0u ||
            RawGL.GetInteger(GLEnum.CurrentProgram) != (int)nativeProgramId ||
            RawGL.IsEnabled(GLEnum.DepthTest) ||
            RawGL.IsEnabled(GLEnum.StencilTest) ||
            RawGL.IsEnabled(GLEnum.Blend) ||
            RawGL.IsEnabled(GLEnum.RasterizerDiscard) ||
            RawGL.IsEnabled(GLEnum.CullFace) ||
            RawGL.IsEnabled(GLEnum.ColorLogicOp) ||
            RawGL.IsEnabled(GLEnum.SampleAlphaToCoverage) ||
            RawGL.IsEnabled(GLEnum.SampleCoverage) ||
            RawGL.IsEnabled(GLEnum.SampleMask) ||
            !WorldUiRegionMatches(_worldUiStepRegion))
            return false;
        unsafe
        {
            int* colorMask = stackalloc int[4];
            RawGL.GetInteger(GLEnum.ColorWritemask, 0, colorMask);
            if (colorMask[0] == 0 || colorMask[1] == 0 ||
                colorMask[2] == 0 || colorMask[3] == 0)
                return false;
            int* polygonMode = stackalloc int[2];
            RawGL.GetInteger(GLEnum.PolygonMode, polygonMode);
            if (polygonMode[0] != (int)GLEnum.Fill ||
                polygonMode[1] != (int)GLEnum.Fill)
                return false;
            for (int index = 0; index < 8; index++)
                if (RawGL.IsEnabled((GLEnum)((int)GLEnum.ClipDistance0 + index)))
                    return false;
        }
        bool target = _worldUiStepStage == EUIWorldOutputStage.WindowPresent
            ? RawGL.GetInteger(GLEnum.DrawFramebufferBinding) == 0 &&
              RawGL.GetInteger(GLEnum.DrawBuffer) == (int)GLEnum.Back
            : _worldUiStepDestination is not null &&
              WorldUiFboMatches(_worldUiStepDestination,
                  _worldUiStepStage switch
                  {
                      EUIWorldOutputStage.LightCombine => _worldUiHdr!,
                      EUIWorldOutputStage.PostProcess => _worldUiPost!,
                      _ => _worldUiFinal!,
                  }, _worldUiStepStage == EUIWorldOutputStage.LightCombine
                      ? _worldUiDepth : null) &&
              RawGL.GetInteger(GLEnum.DrawBuffer) == (int)GLEnum.ColorAttachment0;
        if (!target || _worldUiStepSource is null ||
            !TryNativeWorldTexture(_worldUiStepSource, out uint expectedTexture) ||
            expectedTexture != WorldUiPinnedTextureId(_worldUiStepSource) ||
            !WorldUiNativeTextureSizeMatches(expectedTexture, _worldUiRegion))
            return false;
        if (_worldUiStepStage == EUIWorldOutputStage.WindowPresent &&
            (!TryGetUiBackdropOutputCandidate(mesh, out var candidate) ||
             !UIBackdropOutputContract.IsCanonicalWindowPresentCandidate(
                 in candidate, mesh.MeshRenderer, mesh.Mesh, material) ||
             !IsCanonicalUiBackdropNativeProgram(
                 in candidate, program, nativeProgramId) ||
             !CanOverwriteUiBackdropOutput(in candidate, mesh, indexCount, instances)))
            return false;
        if (_worldUiStepStage == EUIWorldOutputStage.PostProcess &&
            !WorldUiUniformIsZero(nativeProgramId, "UseGpuAutoExposure"))
            return false;
        if (_worldUiStepStage == EUIWorldOutputStage.FinalPostProcess &&
            !WorldUiUniformIsZero(nativeProgramId, "LensDistortionMode"))
            return false;
        string sampler = _worldUiStepStage switch
        {
            EUIWorldOutputStage.LightCombine => "AlbedoOpacity",
            EUIWorldOutputStage.PostProcess => "HDRSceneTex",
            EUIWorldOutputStage.FinalPostProcess => "PostProcessOutputTexture",
            _ => "SourceTexture",
        };
        int location = RawGL.GetUniformLocation(nativeProgramId, sampler);
        if (location < 0)
            return false;
        RawGL.GetUniform(nativeProgramId, location, out int textureUnit);
        if (textureUnit < 0 || textureUnit >= MaxFragmentTextureImageUnits)
            return false;
        int previousUnit = RawGL.GetInteger(GLEnum.ActiveTexture);
        try
        {
            RawGL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + textureUnit));
            if (RawGL.GetInteger(GLEnum.TextureBinding2D) != (int)expectedTexture ||
                RawGL.GetInteger(GLEnum.SamplerBinding) != 0)
                return false;
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureBaseLevel, out int baseLevel);
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureMaxLevel, out int maxLevel);
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureMinFilter, out int minFilter);
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureMagFilter, out int magFilter);
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureCompareMode, out int compareMode);
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureWrapS, out int wrapS);
            RawGL.GetTextureParameter(expectedTexture, GLEnum.TextureWrapT, out int wrapT);
            return baseLevel == 0 && maxLevel >= 0 &&
                (minFilter == (int)GLEnum.Nearest || minFilter == (int)GLEnum.Linear) &&
                (magFilter == (int)GLEnum.Nearest || magFilter == (int)GLEnum.Linear) &&
                compareMode == (int)GLEnum.None &&
                wrapS == (int)GLEnum.ClampToEdge &&
                wrapT == (int)GLEnum.ClampToEdge;
        }
        finally
        {
            RawGL.ActiveTexture((TextureUnit)previousUnit);
        }
    }

    private bool WorldUiUniformIsZero(uint programId, string name)
    {
        int location = RawGL.GetUniformLocation(programId, name);
        if (location < 0)
            return false;
        RawGL.GetUniform(programId, location, out int value);
        return value == 0;
    }

    private bool WorldUiMeshDrawNativeMatches(GLMeshRenderer mesh,
        EPrimitiveType topology, uint indexCount, uint instances,
        XRMaterial? material, XRRenderProgram? program, uint nativeProgramId)
        => WorldUiOwnerCurrent() && _worldUiPhase == 2 &&
            CurrentUiPublicationReceiptId == _worldUiReceipt &&
            _worldUiDrawCommand is { } command &&
            ReferenceEquals(mesh.MeshRenderer, command.Mesh) &&
            ReferenceEquals(material, command.MaterialOverride ?? command.Mesh?.Material) &&
            program is not null && nativeProgramId != 0u &&
            RawGL.GetInteger(GLEnum.CurrentProgram) == (int)nativeProgramId &&
            _worldUiPipeline!.Pipeline?.CommandGeneration == _worldUiCommandGeneration &&
            RawGL.GetInteger(GLEnum.DrawBuffer) == (int)GLEnum.ColorAttachment0 &&
            WorldUiColorWritable() &&
            _worldUiForwardFbo is not null && _worldUiHdr is not null &&
            _worldUiDepth is not null &&
            WorldUiFboMatches(_worldUiForwardFbo, _worldUiHdr, _worldUiDepth) &&
            WorldUiRegionMatches(_worldUiRegion) &&
            material is not null &&
            WorldUiCropNativeMatches(command, mesh, material, program, nativeProgramId) &&
            topology == EPrimitiveType.Triangles && indexCount > 0u && instances > 0u &&
            mesh.ConditionalRenderQuery is null;

    private unsafe bool WorldUiCropNativeMatches(UIPublishedWorldMeshCommand command,
        GLMeshRenderer mesh, XRMaterial material, XRRenderProgram program,
        uint nativeProgramId)
    {
        if (command.WorldCrop is null)
            return true;
        if (command.WorldCropMinMax is not { } minMax ||
            command.WorldCropModelToCanvas is not { } modelToCanvas ||
            !command.WorldMatrixIsModelMatrix ||
            command.WorldCropFragmentShader is not { } fragment ||
            fragment.SourceRevision != command.WorldCropFragmentRevision ||
            !UIWorldCropShaderContract.IsCanonicalClippedFragment(fragment) ||
            (int)material.BillboardMode != 0 ||
            material.FragmentShaders.Count != 1 ||
            !ReferenceEquals(material.FragmentShaders[0], fragment) ||
            program.Shaders.Count != 2 ||
            !TryGetAPIRenderObject(program, out var existing) ||
            existing is not GLRenderProgram native ||
            !native.TryGetBindingId(out uint actualProgramId) ||
            actualProgramId != nativeProgramId ||
            !native.HasActiveUniformType(UIWorldCropShaderContract.MinMaxUniform,
                GLEnum.FloatVec4) ||
            !native.HasActiveUniformType(UIWorldCropShaderContract.MatrixUniform,
                GLEnum.FloatMat4) ||
            !native.HasActiveUniformType("ModelMatrix", GLEnum.FloatMat4))
            return false;

        XRShader? programVertex = null;
        XRShader? programFragment = null;
        for (int index = 0; index < program.Shaders.Count; index++)
        {
            XRShader shader = program.Shaders[index];
            if (shader.Type == EShaderType.Vertex)
                programVertex = shader;
            else if (shader.Type == EShaderType.Fragment)
                programFragment = shader;
            else
                return false;
        }
        if (programVertex is null || !ReferenceEquals(programFragment, fragment))
            return false;
        if (!native.HasUnchangedInitialWorldUiSources(programVertex, fragment))
            return false;

        if (material.VertexShaders.Count == 0)
        {
            if (command.WorldCropVertexShader is not null ||
                mesh.Data is not XRMeshRenderer.Version<DefaultVertexShaderGenerator> version ||
                !ReferenceEquals(version.Parent, command.Mesh) ||
                !string.Equals(programVertex.Source?.Text,
                    version.VertexShaderSource, StringComparison.Ordinal) ||
                !native.HasActiveUniformType("ViewMatrix_VTX", GLEnum.FloatMat4) ||
                !native.HasActiveUniformType("ProjMatrix_VTX", GLEnum.FloatMat4) ||
                !native.HasActiveUniformType("VRMode", GLEnum.Bool) ||
                !WorldUiMatrixUniformMatches(nativeProgramId, "ViewMatrix_VTX",
                    _worldUiFrozenView.ViewMatrix) ||
                !WorldUiMatrixUniformMatches(nativeProgramId, "ProjMatrix_VTX",
                    _worldUiFrozenView.ProjectionMatrix) ||
                !WorldUiIntUniformMatches(nativeProgramId, "VRMode", 0) ||
                native.TryGetActiveUniformType("BillboardMode", out GLEnum billboardType) &&
                    (billboardType != GLEnum.Int ||
                     !WorldUiIntUniformMatches(nativeProgramId, "BillboardMode", 0)))
                return false;
        }
        else
        {
            bool exactOwnedVertex = false;
            for (int index = 0; index < material.VertexShaders.Count; index++)
                if (ReferenceEquals(material.VertexShaders[index], programVertex))
                {
                    exactOwnedVertex = true;
                    break;
                }
            if (!exactOwnedVertex ||
                !ReferenceEquals(command.WorldCropVertexShader, programVertex) ||
                programVertex.SourceRevision != command.WorldCropVertexRevision ||
                !UIWorldCropShaderContract.IsCanonicalVertex(programVertex) ||
                !native.HasActiveUniformType("ViewProjectionMatrix_VTX", GLEnum.FloatMat4) ||
                !WorldUiMatrixUniformMatches(nativeProgramId,
                    "ViewProjectionMatrix_VTX", _worldUiFrozenView.ViewProjectionMatrix))
                return false;
        }

        int cropLocation = RawGL.GetUniformLocation(nativeProgramId,
            UIWorldCropShaderContract.MinMaxUniform);
        if (cropLocation < 0)
            return false;
        float* cropValues = stackalloc float[4];
        RawGL.GetUniform(nativeProgramId, cropLocation, cropValues);
        if (cropValues[0] != minMax.X || cropValues[1] != minMax.Y ||
            cropValues[2] != minMax.Z || cropValues[3] != minMax.W)
            return false;
        return WorldUiMatrixUniformMatches(nativeProgramId,
                UIWorldCropShaderContract.MatrixUniform, modelToCanvas) &&
            WorldUiMatrixUniformMatches(nativeProgramId,
                "ModelMatrix", command.WorldMatrix) &&
            RawGL.GetError() == GLEnum.NoError;
    }

    private unsafe bool WorldUiMatrixUniformMatches(uint programId, string name,
        Matrix4x4 expected)
    {
        int location = RawGL.GetUniformLocation(programId, name);
        if (location < 0)
            return false;
        float* actual = stackalloc float[16];
        RawGL.GetUniform(programId, location, actual);
        float* expectedValues = &expected.M11;
        for (int index = 0; index < 16; index++)
            if (actual[index] != expectedValues[index])
                return false;
        return true;
    }

    private unsafe bool WorldUiIntUniformMatches(uint programId, string name,
        int expected)
    {
        int location = RawGL.GetUniformLocation(programId, name);
        if (location < 0)
            return false;
        int actual = 0;
        RawGL.GetUniform(programId, location, &actual);
        return actual == expected;
    }

    private static bool WorldUiSameRegion(in BoundingRectangle a,
        in BoundingRectangle b)
        => a.X == b.X && a.Y == b.Y &&
            a.Width == b.Width && a.Height == b.Height;

    internal override bool TryBeginUiWorldOutputStep(ulong attemptToken,
        EUIWorldOutputStage stage, XRTexture source, XRFrameBuffer? destination,
        XRMeshRenderer mesh, XRMaterial material, BoundingRectangle region,
        out ulong stepToken)
    {
        stepToken = 0UL;
        int expectedPhase = stage switch
        {
            EUIWorldOutputStage.LightCombine => 1,
            EUIWorldOutputStage.PostProcess => 2,
            EUIWorldOutputStage.FinalPostProcess => 3,
            EUIWorldOutputStage.WindowPresent => 4,
            _ => -1,
        };
        XRTexture? expectedSource = stage switch
        {
            EUIWorldOutputStage.LightCombine => _worldUiAlbedo,
            EUIWorldOutputStage.PostProcess => _worldUiHdr,
            EUIWorldOutputStage.FinalPostProcess => _worldUiPost,
            EUIWorldOutputStage.WindowPresent => _worldUiFinal,
            _ => null,
        };
        XRFrameBuffer? expectedDestination = stage switch
        {
            EUIWorldOutputStage.LightCombine => _worldUiForwardFbo,
            EUIWorldOutputStage.PostProcess => _worldUiPostFbo,
            EUIWorldOutputStage.FinalPostProcess => _worldUiFinalFbo,
            _ => null,
        };
        XRQuadFrameBuffer? expectedQuad = stage switch
        {
            EUIWorldOutputStage.LightCombine => _worldUiLightQuad,
            EUIWorldOutputStage.PostProcess => _worldUiPostQuad,
            EUIWorldOutputStage.FinalPostProcess => _worldUiFinalQuad,
            _ => null,
        };
        XRMaterial? expectedMaterial = stage switch
        {
            EUIWorldOutputStage.LightCombine => _worldUiLightMaterial,
            EUIWorldOutputStage.PostProcess => _worldUiPostMaterial,
            EUIWorldOutputStage.FinalPostProcess => _worldUiFinalMaterial,
            _ => null,
        };
        if (attemptToken == 0UL || attemptToken != _worldUiAttempt ||
            !WorldUiOwnerCurrent() || _worldUiStep != 0UL ||
            _worldUiPhase != expectedPhase ||
            (stage == EUIWorldOutputStage.PostProcess &&
                _worldUiDrawCount != _worldUiPublication?.WorldCommands.Length) ||
            !ReferenceEquals(source, expectedSource) ||
            !ReferenceEquals(destination, expectedDestination) ||
            (stage != EUIWorldOutputStage.WindowPresent &&
                (!ReferenceEquals(expectedQuad?.FullScreenMesh, mesh) ||
                 !ReferenceEquals(expectedQuad.Material, material) ||
                 !ReferenceEquals(expectedMaterial, material) ||
                 !WorldUiQuadShaderCurrent(stage, material))) ||
            !ReferenceEquals(mesh.Material, material) || mesh.Mesh is null ||
            !ReferenceEquals(_worldUiPipeline?.RenderState.WindowViewport?.Window, _worldUiWindow) ||
            !WorldUiSameRegion(region, _worldUiRegion))
        {
            FailWorldUi("World UI native output stage changed its owner or source.");
            return false;
        }
        if (stage != EUIWorldOutputStage.WindowPresent &&
            !HasWorldUiTargets(destination!, stage switch
            {
                EUIWorldOutputStage.LightCombine => _worldUiHdr!,
                EUIWorldOutputStage.PostProcess => _worldUiPost!,
                _ => _worldUiFinal!,
            }, stage == EUIWorldOutputStage.LightCombine ? _worldUiDepth : null))
        {
            FailWorldUi("World UI postprocess destination changed.");
            return false;
        }
        stepToken = unchecked(++_nextWorldUiStep);
        if (stepToken == 0UL)
            stepToken = unchecked(++_nextWorldUiStep);
        _worldUiStep = stepToken;
        _worldUiStepStage = stage;
        _worldUiStepSource = source;
        _worldUiStepDestination = destination;
        _worldUiStepMesh = mesh;
        _worldUiStepMaterial = material;
        if (material.Shaders.Count != 2)
        {
            FailWorldUi("World UI output quad has an unsupported shader program.");
            ClearWorldUiStep();
            stepToken = 0UL;
            return false;
        }
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            if (shader.Type == EShaderType.Vertex)
            {
                _worldUiStepVertexShader = shader;
                _worldUiStepVertexRevision = shader.SourceRevision;
            }
            else if (shader.Type == EShaderType.Fragment)
            {
                _worldUiStepFragmentShader = shader;
                _worldUiStepFragmentRevision = shader.SourceRevision;
            }
        }
        if (_worldUiStepVertexShader is null || _worldUiStepFragmentShader is null)
        {
            FailWorldUi("World UI output quad has no exact vertex/fragment shader pair.");
            ClearWorldUiStep();
            stepToken = 0UL;
            return false;
        }
        _worldUiStepRegion = region;
        _worldUiStepDrawCount = 0;
        return true;
    }

    internal override bool CompleteUiWorldOutputStep(ulong stepToken)
    {
        bool completed = stepToken != 0UL && stepToken == _worldUiStep &&
            !_worldUiFailed && _worldUiStepDrawCount == 1 && WorldUiOwnerCurrent();
        if (completed)
            _worldUiPhase++;
        else
            FailWorldUi("World UI native output stage did not issue exactly one checked draw.");
        ClearWorldUiStep();
        return completed;
    }

    internal override void AbortUiWorldOutputStep(ulong stepToken)
    {
        if (stepToken == 0UL || stepToken != _worldUiStep)
            return;
        FailWorldUi("World UI native output stage was aborted.");
        ClearWorldUiStep();
    }

    private void ClearWorldUiStep()
    {
        _worldUiStep = 0UL;
        _worldUiStepSource = null;
        _worldUiStepDestination = null;
        _worldUiStepMesh = null;
        _worldUiStepMaterial = null;
        _worldUiStepVertexShader = _worldUiStepFragmentShader = null;
        _worldUiStepDrawCount = 0;
    }

    internal override bool CompleteUiWorldOutputAttempt(ulong token, out string? failure)
    {
        bool complete = token != 0UL && token == _worldUiAttempt &&
            WorldUiOwnerCurrent() && _worldUiPhase == 5 && _worldUiStep == 0UL &&
            _worldUiDrawCount == _worldUiPublication?.WorldCommands.Length;
        if (!complete)
            FailWorldUi("World UI native clear, scene draw, postprocess, or window output was incomplete.");
        failure = _worldUiFailure;
        return complete;
    }

    internal override bool IsUiWorldOutputAttemptStillValid(ulong token)
    {
        bool valid = token != 0UL && token == _worldUiAttempt && !_worldUiFailed &&
            _worldUiPhase == 5 && _worldUiStep == 0UL && WorldUiOwnerCurrent();
        if (!valid && token != 0UL && token == _worldUiAttempt)
            System.Threading.Volatile.Write(ref _worldUiReady, null);
        return valid;
    }

    internal override void AbortUiWorldOutputAttempt(ulong token)
    {
        if (token == 0UL || token != _worldUiAttempt)
            return;
        if (_worldUiFailed || _worldUiPhase != 5)
            System.Threading.Volatile.Write(ref _worldUiReady, null);
        _worldUiAttempt = _worldUiStep = 0UL;
        _worldUiWindow = null;
        _worldUiContext = null;
        _worldUiPipeline = null;
        _worldUiCamera = null;
        _worldUiGeneration = null;
        _worldUiPublication = null;
        _worldUiDrawCommand = null;
        _worldUiForwardFbo = _worldUiDeferredFbo = _worldUiPostFbo = _worldUiFinalFbo = null;
        _worldUiLightQuad = _worldUiPostQuad = _worldUiFinalQuad = null;
        _worldUiLightMaterial = _worldUiPostMaterial = _worldUiFinalMaterial = null;
        Array.Clear(_worldUiQuadVertex, 0, _worldUiQuadVertex.Length);
        Array.Clear(_worldUiQuadFragment, 0, _worldUiQuadFragment.Length);
        _worldUiHdr = _worldUiDepth = _worldUiPost = _worldUiFinal = _worldUiAlbedo = null;
        _worldUiHdrGl = _worldUiDepthGl = _worldUiPostGl = _worldUiFinalGl = _worldUiAlbedoGl = null;
        Array.Clear(_worldUiFboWrappers, 0, _worldUiFboWrappers.Length);
        ClearWorldUiStep();
        _worldUiFailure = null;
        _worldUiFailed = false;
        _worldUiForwardId = _worldUiDeferredId = _worldUiPostFboId =
            _worldUiFinalFboId = _worldUiHdrId = _worldUiDepthId =
            _worldUiPostId = _worldUiFinalId = _worldUiAlbedoId = 0u;
        _worldUiPhase = _worldUiDrawCount = 0;
    }
}
