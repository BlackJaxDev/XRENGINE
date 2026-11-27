using System.Numerics;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Scene;

namespace XREngine.Components.Lights;

/// <summary>Fixed owner/display hand-off for one mirror texture generation.</summary>
internal sealed class MirrorCaptureSlot
{
    private readonly object _sync = new();
    private readonly XRMesh _displayMesh;
    private AdvancedMutableTexturePublicationLifetime? _lifetime;
    private ulong _exactGeneration;
    private readonly XRGpuFence?[] _readerFences = new XRGpuFence?[16];
    private int _readerFenceCount;
    private int _collectedReaderCount;
    private int _gpuReaderCount;
    private bool _quarantineWarningLogged;
    private XRMaterial? _transparentMaterial;
    private XRMaterial? _transparentVelocityMaterial;
    private XRMaterial? _transparentReactiveMaterial;
    private MirrorDisplayCommand? _transparentDisplay;

    internal MirrorCaptureSlot(SceneNode node, MirrorTextureCaptureComponent capture, XRMesh mesh)
    {
        Node = node;
        Capture = capture;
        _displayMesh = mesh;
        Material = new XRMaterial(ShaderHelper.LoadEngineShader(Path.Combine("Common", "MirrorProjective.fs")))
        {
            Name = "MirrorCaptureSlot.DisplayMaterial",
        };
        Material.RenderOptions.CullMode = ECullMode.Back;
        Material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.ViewportDimensions;
        Material.Parameters =
        [
            new ShaderMat4(Matrix4x4.Identity, "ReflectedViewProjection"),
            new ShaderBool(false, "FramebufferYDown"),
        ];
        Material.Textures = [null];
        Display = new MirrorDisplayCommand(
            (int)EDefaultRenderPass.OpaqueForward,
            new XRMeshRenderer(mesh, Material),
            Matrix4x4.Identity,
            this)
        {
            ForceCpuRendering = true,
        };
    }

    internal SceneNode Node { get; }
    internal MirrorTextureCaptureComponent Capture { get; }
    internal MirrorDisplayCommand Display { get; }
    internal XRMaterial Material { get; }

    /// <summary>
    /// Returns the transparent-background mono display command for this slot, creating its
    /// late-pass material on first use and syncing it with the latest published output.
    /// </summary>
    internal MirrorDisplayCommand GetOrCreateTransparentDisplay()
    {
        lock (_sync)
        {
            if (_transparentDisplay is not null)
                return _transparentDisplay;

            ShaderMat4 matrixParameter = new(ReflectedViewProjection, "ReflectedViewProjection");
            ShaderBool yDownParameter = new(FramebufferYDown, "FramebufferYDown");
            ShaderVar[] sharedParameters = [matrixParameter, yDownParameter];
            XRMaterial velocityMaterial = new(
                sharedParameters,
                ShaderHelper.LoadEngineShader(Path.Combine("Common", "MirrorProjectiveTransparentMotionVectors.fs")))
            {
                Name = "MirrorCaptureSlot.TransparentMotionVectors",
                Textures = [Texture],
            };
            velocityMaterial.RenderOptions.CullMode = ECullMode.Back;
            velocityMaterial.RenderOptions.ExcludeFromGpuIndirect = true;
            velocityMaterial.RenderOptions.DepthTest = new DepthTest
            {
                Enabled = ERenderParamUsage.Enabled,
                UpdateDepth = false,
                Function = EComparison.Lequal,
            };
            velocityMaterial.RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
            velocityMaterial.SettingUniforms += AdvancedColoredAlphaTemporalVariantFactory.BindTemporalMatrices;

            XRMaterial reactiveMaterial = new(
                sharedParameters,
                ShaderHelper.LoadEngineShader(Path.Combine("Common", "MirrorProjectiveTransparentReactiveMask.fs")))
            {
                Name = "MirrorCaptureSlot.TransparentReactiveMask",
                Textures = [Texture],
            };
            reactiveMaterial.RenderOptions.CullMode = ECullMode.Back;
            reactiveMaterial.RenderOptions.ExcludeFromGpuIndirect = true;
            reactiveMaterial.RenderOptions.DepthTest = new DepthTest
            {
                Enabled = ERenderParamUsage.Enabled,
                UpdateDepth = false,
                Function = EComparison.Lequal,
            };
            reactiveMaterial.RenderOptions.BlendModeAllDrawBuffers = new BlendMode
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbEquation = EBlendEquationMode.Max,
                AlphaEquation = EBlendEquationMode.Max,
                RgbSrcFactor = EBlendingFactor.One,
                RgbDstFactor = EBlendingFactor.One,
                AlphaSrcFactor = EBlendingFactor.One,
                AlphaDstFactor = EBlendingFactor.One,
            };

            XRMaterial material = new(ShaderHelper.LoadEngineShader(
                Path.Combine("Common", "MirrorProjectiveTransparent.fs")))
            {
                Name = "MirrorCaptureSlot.TransparentDisplayMaterial",
                RenderPass = (int)EDefaultRenderPass.TransparentForward,
                TransparencyMode = ETransparencyMode.AlphaBlend,
                Parameters = sharedParameters,
                Textures = [Texture],
            };
            material.AdvancedLatePassMetadata = new(
                EAdvancedLatePassKind.SortedAlpha,
                participatesInMotionVectors: true,
                isOrderDependent: true)
            {
                TemporalVelocityMaterial = velocityMaterial,
                TemporalReactiveMaskMaterial = reactiveMaterial,
                RequiresRigidTemporalGeometry = true,
                TemporalSourceShaderRevision = material.ShaderStateRevision,
            };
            material.RenderOptions.CullMode = ECullMode.Back;
            material.RenderOptions.ExcludeFromGpuIndirect = true;
            material.RenderOptions.DepthTest = new DepthTest
            {
                Enabled = ERenderParamUsage.Enabled,
                UpdateDepth = false,
                Function = EComparison.Lequal,
            };
            material.RenderOptions.BlendModeAllDrawBuffers = BlendMode.EnabledPremultipliedTransparent();
            _transparentMaterial = material;
            _transparentVelocityMaterial = velocityMaterial;
            _transparentReactiveMaterial = reactiveMaterial;
            _transparentDisplay = new MirrorDisplayCommand(
                (int)EDefaultRenderPass.TransparentForward,
                new XRMeshRenderer(_displayMesh, material),
                Matrix4x4.Identity,
                this)
            {
                ForceCpuRendering = true,
                GpuProfilingLabel = "Mirror.TransparentProjective",
            };
            return _transparentDisplay;
        }
    }
    internal XRTexture2D? Texture { get; private set; }
    internal Matrix4x4 ReflectedViewProjection { get; private set; } = Matrix4x4.Identity;
    internal Matrix4x4 CapturedMirrorWorldMatrix { get; private set; } = Matrix4x4.Identity;
    internal bool FramebufferYDown { get; private set; }
    internal bool AwaitingCapture { get; set; }
    internal bool Quarantined { get; private set; }
    internal long CaptureVersion { get; private set; }
    internal bool Unavailable
    {
        get
        {
            lock (_sync)
                return Quarantined || Capture.IsCaptureQuarantined;
        }
    }

    internal bool ReadersRetired
    {
        get
        {
            lock (_sync)
                return _collectedReaderCount == 0 && _gpuReaderCount == 0 && !Unavailable;
        }
    }

    internal object GetDiagnostics()
    {
        lock (_sync)
            return new
        {
            captureOwnerId = Capture.ID,
            captureVersion = CaptureVersion,
            generation = _exactGeneration,
            awaitingCapture = AwaitingCapture,
            quarantined = Quarantined,
            collectedReaders = Volatile.Read(ref _collectedReaderCount),
            gpuReaders = Volatile.Read(ref _gpuReaderCount),
            readerFences = _readerFenceCount,
            readersRetired = ReadersRetired,
            texture = Texture?.ID,
            capture = Capture.GetCaptureDiagnostics(),
        };
    }

    internal bool TryRetainDisplayResource()
    {
        lock (_sync)
        {
            if (Texture is null || _exactGeneration == 0u || Unavailable || _lifetime is null)
                return false;
            if (!_lifetime.TryRetainPublication(_exactGeneration))
                return false;
            Interlocked.Increment(ref _collectedReaderCount);
            return true;
        }
    }

    internal void ReleaseDisplayResource()
    {
        lock (_sync)
        {
            AdvancedMutableTexturePublicationLifetime? lifetime = _lifetime;
            if (lifetime is null)
                throw new InvalidOperationException("A mirror display collection retain lost its source lifetime.");
            lifetime.ReleasePublication();
            --_collectedReaderCount;
        }
    }

    internal bool Publish(
        XRTexture2D texture,
        Matrix4x4 reflectedViewProjection,
        Matrix4x4 capturedMirrorWorldMatrix,
        bool framebufferYDown,
        bool writerPending = false)
    {
        AssertRenderThread();
        ArgumentNullException.ThrowIfNull(texture);
        ulong generation = texture.CanonicalSourceContentGeneration;
        AdvancedMutableTexturePublicationLifetime? lifetime =
            texture.CanonicalPublicationLifetime as AdvancedMutableTexturePublicationLifetime;
        if (generation == 0u || lifetime is null || !IsFinite(reflectedViewProjection))
            return false;
        lock (_sync)
        {
            if (!ReadersRetired)
                return false;
            Texture = texture;
            _exactGeneration = generation;
            _lifetime = lifetime;
            ReflectedViewProjection = reflectedViewProjection;
            CapturedMirrorWorldMatrix = IsFinite(capturedMirrorWorldMatrix)
                ? capturedMirrorWorldMatrix
                : Matrix4x4.Identity;
            FramebufferYDown = framebufferYDown;
            CaptureVersion = Capture.CaptureVersion;
            Material.Textures[0] = texture;
            Material.Parameter<ShaderMat4>("ReflectedViewProjection")!.Value = reflectedViewProjection;
            Material.Parameter<ShaderBool>("FramebufferYDown")!.Value = framebufferYDown;
            if (_transparentMaterial is not null)
            {
                _transparentMaterial.Textures[0] = texture;
                _transparentMaterial.Parameter<ShaderMat4>("ReflectedViewProjection")!.Value = reflectedViewProjection;
                _transparentMaterial.Parameter<ShaderBool>("FramebufferYDown")!.Value = framebufferYDown;
                if (_transparentVelocityMaterial is not null)
                    _transparentVelocityMaterial.Textures[0] = texture;
                if (_transparentReactiveMaterial is not null)
                    _transparentReactiveMaterial.Textures[0] = texture;
            }
            AwaitingCapture = writerPending;
            return true;
        }
    }

    internal void Withdraw()
    {
        AssertRenderThread();
        lock (_sync)
        {
            // Display command collection may already have retained this generation. Do not mutate
            // its texture/matrix binding here; owner retirement controls publication visibility.
            Capture.ClearRequestedView();
            AwaitingCapture = false;
        }
    }

    internal void SettleReaders()
    {
        AssertRenderThread();
        lock (_sync)
        {
            for (int index = _readerFenceCount - 1; index >= 0; --index)
            {
                XRGpuFence fence = _readerFences[index]!;
                EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
                if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
                    continue;
                EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
                    ? fence.Poll()
                    : EGpuFenceStatus.Failed;
                if (status == EGpuFenceStatus.Pending)
                    continue;
                if (submission != EGpuFenceSubmissionStatus.Submitted)
                {
                    fence.Dispose();
                    _readerFences[index] = _readerFences[--_readerFenceCount];
                    _readerFences[_readerFenceCount] = null;
                    ReleaseGpuReader();
                    continue;
                }
                if (status != EGpuFenceStatus.Signaled)
                {
                    Quarantine("A submitted mirror display reader failed before its completion could be trusted.");
                    continue;
                }
                fence.Dispose();
                _readerFences[index] = _readerFences[--_readerFenceCount];
                _readerFences[_readerFenceCount] = null;
                ReleaseGpuReader();
            }
        }
    }

    internal bool TryRetainGpuReader()
    {
        AssertRenderThread();
        lock (_sync)
        {
            if (Unavailable || _readerFenceCount == _readerFences.Length || _lifetime is null || _exactGeneration == 0u ||
                (!_lifetime.TryRetainPublication(_exactGeneration) &&
                 !_lifetime.TryRetainReservedGeneration(_exactGeneration)))
                return false;
            Interlocked.Increment(ref _gpuReaderCount);
            return true;
        }
    }

    internal void CompleteGpuReaderAfterRender()
    {
        AssertRenderThread();
        lock (_sync)
            if (_readerFenceCount == _readerFences.Length)
            {
                Quarantine("Mirror display reader fence capacity is exhausted.");
                return;
            }

        // This allocation is the required explicit receipt for a potentially partial GPU draw.
        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        lock (_sync)
        {
            if (fence is not null && _readerFenceCount < _readerFences.Length)
            {
                _readerFences[_readerFenceCount++] = fence;
                return;
            }
            // A retained source with no completion receipt is intentionally retained forever.
            // Reusing it would create an untracked GPU reader; quarantine makes the fault visible.
            Quarantine("Mirror display reader has no GPU completion receipt.");
        }
        fence?.Dispose();
    }

    private void ReleaseGpuReader()
    {
        lock (_sync)
        {
            AdvancedMutableTexturePublicationLifetime? lifetime = _lifetime;
            if (lifetime is null)
                throw new InvalidOperationException("A mirror GPU reader lost its source lifetime.");
            lifetime.ReleasePublication();
            --_gpuReaderCount;
        }
    }

    private void Quarantine(string reason)
    {
        Quarantined = true;
        if (_quarantineWarningLogged)
            return;
        _quarantineWarningLogged = true;
        Debug.RenderingWarning(reason);
    }

    private static void AssertRenderThread()
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Mirror capture slot GPU ownership must be advanced on the render thread.");
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
