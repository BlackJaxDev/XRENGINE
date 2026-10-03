using XREngine.Audio.WebAudio;
using XREngine.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.WebGPU;

namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    private const int MaximumGraphicsRecoveryAttempts = 3;
    private IShaderProgramArtifactResolver? _rendererShaderArtifacts;
    private EngineMaterialVariantCatalog? _rendererMaterialVariants;
    private WebComputeArtifactCatalog? _rendererComputeArtifacts;
    private bool _graphicsRecoveryPending;
    private int _graphicsRecoveryAttempts;
    private string? _graphicsRecoveryFailure;

    /// <summary>Rebinds retained immutable cooked sources to a new physical renderer owner.</summary>
    private void InitializeRendererArtifacts(WebGpuRendererHost renderer)
    {
        if (_rendererComputeArtifacts?.TryResolve(WebComputeArtifactCatalog.PackedSkinningKernel,
                out ShaderProgramArtifact? deformationArtifact) == true)
            renderer.BindMeshDeformationArtifact(deformationArtifact);
        if (_rendererComputeArtifacts?.TryResolve(WebComputeArtifactCatalog.LuminanceReductionKernel,
                out ShaderProgramArtifact? luminanceArtifact) == true)
            renderer.BindLuminanceArtifact(luminanceArtifact);
        if (_rendererComputeArtifacts?.TryResolve(WebComputeArtifactCatalog.LuminanceReduction2DKernel,
                out ShaderProgramArtifact? luminance2DArtifact) == true)
            renderer.BindLuminance2DArtifact(luminance2DArtifact);
        if (_rendererComputeArtifacts?.TryResolve(WebComputeArtifactCatalog.LuminanceMipmapKernel,
                out ShaderProgramArtifact? luminanceMipmapArtifact) == true)
            renderer.BindLuminanceMipmapArtifact(luminanceMipmapArtifact);
        renderer.BindShaderArtifacts(_rendererShaderArtifacts, _rendererMaterialVariants);
        renderer.BindAdvancedPipelineArtifacts(_pipelineArtifacts);
    }

    /// <summary>Retires only GPU ownership, retaining the authored world, player, viewport and gameplay state.</summary>
    public int BeginRendererRecovery(int session, bool deviceLost)
    {
        RequireRecoveryOwner(session);
        if (!Engine.Time.Timer.IsCallerThreadLoop)
            throw new InvalidOperationException("WebGPU.Recovery.ClockStopped: the caller-thread lifecycle has stopped; device replacement cannot safely resume gameplay.");
        _graphicsRecoveryPending = true;
        _graphicsRecoveryFailure = null;
        ResetInput();
        ResetFrameTiming();
        WebAudioTransport.SetSurfaceActive(false);
        if (++_graphicsRecoveryAttempts > MaximumGraphicsRecoveryAttempts)
            throw new InvalidOperationException("WebGPU.Recovery.Exhausted: the canvas reached its three device replacement attempts; explicit restart is required.");

        WebGpuRendererHost retiring = _renderer!;
        if (retiring.State != BrowserRendererState.Disposed)
        {
            retiring.MarkFailed(deviceLost);
            retiring.BindEngineViewport(null);
            retiring.Dispose();
        }
        _renderViewport!.RenderPipelineInstance.ResetAfterRendererRetirement(retiring);
        // A new target allows device format renegotiation. Session identity, rather
        // than a reused surface counter, rejects old command and upload packets.
        _canvas = new BrowserCanvasRenderTarget(_canvas!.CanvasId);
        _renderer = OwnConstruction(() => BrowserRendererComposition.CreateRequired(_canvas)) as WebGpuRendererHost
            ?? throw new InvalidOperationException("WebGPU.Recovery.RendererRequired: the browser backend did not supply its required renderer.");
        _admittedPipeline = null;
        InitializeRendererArtifacts(_renderer);
        _renderer.BindEngineViewport(_renderViewport);
        _rendererSession = Interlocked.Increment(ref _nextRendererSession);
        if (_rendererSession <= 0)
            throw new InvalidOperationException("WebGPU.Recovery.SessionExhausted: browser renderer session identities are exhausted.");
        return _rendererSession;
    }

    /// <summary>Accepts a submitted current-output frame after the browser has validated its GPU completion.</summary>
    public void CompleteRendererRecovery(int session)
    {
        RequireRecoveryOwner(session);
        if (!_graphicsRecoveryPending || !HasPresentedCanvasFrame || _graphicsRecoveryFailure is not null)
            throw new InvalidOperationException("WebGPU.Recovery.FrameNotReady: the replacement has not produced a complete current-output frame.");
        ResetInput();
        ResetFrameTiming();
        _graphicsRecoveryPending = false;
        WebAudioTransport.SetSurfaceActive(_canvas!.Surface.CanRender);
    }

    /// <summary>Leaves the live world paused after exhausted recovery, until an explicit stop or restart.</summary>
    public void SuspendRendererRecovery(int session, string failure)
    {
        RequireRecoveryOwner(session);
        _graphicsRecoveryPending = true;
        _graphicsRecoveryFailure = failure;
        ResetInput();
        if (Engine.Time.Timer.IsCallerThreadLoop)
            ResetFrameTiming();
        WebAudioTransport.SetSurfaceActive(false);
        _renderer!.MarkFailed(deviceLost: false);
        _renderer.Dispose();
    }

    private void RequireRecoveryOwner(int session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_running || _canvas is null || _renderViewport is null || _renderer is null ||
            session <= 0 || session != _rendererSession)
            throw new InvalidOperationException("WebGPU.Recovery.ObsoleteSession: the request does not own the active canvas world.");
    }
}
