using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights;

/// <summary>
/// Renders a transparent-background projective mirror for single-pass stereo eyes.
/// Retains both eye capture slots at collection time and across GPU execution so
/// neither reflected texture can be overwritten while the stereo draw is in flight.
/// </summary>
internal sealed class MirrorStereoTransparentDisplayCommand : RenderCommandMesh3D, IRenderCommandCollectedResource
{
    private readonly MirrorCaptureSlot _leftSlot;
    private readonly MirrorCaptureSlot _rightSlot;
    private readonly MirrorCameraBank? _leftBank;
    private readonly MirrorCameraBank? _rightBank;
    private readonly TransformBase? _ownerTransform;
    private readonly XRMaterial _material;
    private readonly XRMaterial _velocityMaterial;
    private readonly XRMaterial _reactiveMaterial;
    private readonly ShaderMat4 _leftMatrixParameter;
    private readonly ShaderMat4 _rightMatrixParameter;
    private readonly ShaderBool _framebufferYDownParameter;
    private MirrorCaptureSlot? _syncedLeftSlot;
    private MirrorCaptureSlot? _syncedRightSlot;
    private long _syncedLeftVersion = -1;
    private long _syncedRightVersion = -1;
    private Matrix4x4 _currentRenderWorldMatrix = Matrix4x4.Identity;
    private Matrix4x4 _syncedRenderWorldMatrix = Matrix4x4.Identity;
    private bool _hasCurrentRenderWorldMatrix;

    private MirrorStereoTransparentDisplayCommand(
        XRMesh mesh,
        MirrorCaptureSlot leftSlot,
        MirrorCaptureSlot rightSlot,
        MirrorCameraBank? leftBank,
        MirrorCameraBank? rightBank,
        TransformBase? ownerTransform,
        SharedModelHistory? sharedModelHistory,
        XRMaterial material,
        XRMaterial velocityMaterial,
        XRMaterial reactiveMaterial,
        ShaderMat4 leftMatrixParameter,
        ShaderMat4 rightMatrixParameter,
        ShaderBool framebufferYDownParameter)
        : base((int)EDefaultRenderPass.TransparentForward, new XRMeshRenderer(mesh, material), Matrix4x4.Identity)
    {
        _leftSlot = leftSlot;
        _rightSlot = rightSlot;
        _leftBank = leftBank;
        _rightBank = rightBank;
        _ownerTransform = ownerTransform;
        _material = material;
        _velocityMaterial = velocityMaterial;
        _reactiveMaterial = reactiveMaterial;
        _leftMatrixParameter = leftMatrixParameter;
        _rightMatrixParameter = rightMatrixParameter;
        _framebufferYDownParameter = framebufferYDownParameter;
        if (sharedModelHistory is not null)
            AttachSharedModelHistory(sharedModelHistory);
        ForceCpuRendering = true;
        GpuProfilingLabel = "Mirror.TransparentProjectiveStereo";
    }

    internal static MirrorStereoTransparentDisplayCommand Create(
        XRMesh mesh,
        MirrorCaptureSlot leftSlot,
        MirrorCaptureSlot rightSlot,
        MirrorCameraBank? leftBank = null,
        MirrorCameraBank? rightBank = null,
        TransformBase? ownerTransform = null,
        SharedModelHistory? sharedModelHistory = null)
    {
        ShaderMat4 leftMatrix = new(Matrix4x4.Identity, "ReflectedViewProjection0");
        ShaderMat4 rightMatrix = new(Matrix4x4.Identity, "ReflectedViewProjection1");
        ShaderBool yDown = new(false, "FramebufferYDown");
        ShaderVar[] sharedParameters = [leftMatrix, rightMatrix, yDown];

        XRMaterial velocityMaterial = new(
            sharedParameters,
            ShaderHelper.LoadEngineShader(Path.Combine("Common", "MirrorProjectiveTransparentStereoMotionVectors.fs")))
        {
            Name = "MirrorCapture.TransparentProjectiveStereoMotionVectors",
            Textures = [null, null],
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
            ShaderHelper.LoadEngineShader(Path.Combine("Common", "MirrorProjectiveTransparentStereoReactiveMask.fs")))
        {
            Name = "MirrorCapture.TransparentProjectiveStereoReactiveMask",
            Textures = [null, null],
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
            Path.Combine("Common", "MirrorProjectiveTransparentStereo.fs")))
        {
            Name = "MirrorCapture.TransparentProjectiveStereo",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            TransparencyMode = ETransparencyMode.AlphaBlend,
            Parameters = sharedParameters,
            Textures = [null, null],
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
        return new MirrorStereoTransparentDisplayCommand(
            mesh,
            leftSlot,
            rightSlot,
            leftBank,
            rightBank,
            ownerTransform,
            sharedModelHistory,
            material,
            velocityMaterial,
            reactiveMaterial,
            leftMatrix,
            rightMatrix,
            yDown);
    }

    internal bool MatchesSlots(MirrorCaptureSlot leftSlot, MirrorCaptureSlot rightSlot)
        => ReferenceEquals(_leftSlot, leftSlot) && ReferenceEquals(_rightSlot, rightSlot);

    internal void UpdateLateRenderWorldMatrix(in Matrix4x4 renderWorldMatrix)
    {
        _currentRenderWorldMatrix = renderWorldMatrix;
        _hasCurrentRenderWorldMatrix = true;
        ApplyLateRenderWorldMatrix(renderWorldMatrix);
    }

    internal void SyncFromSlots(in Matrix4x4 mirrorWorldMatrix)
    {
        _currentRenderWorldMatrix = mirrorWorldMatrix;
        _hasCurrentRenderWorldMatrix = true;
        SyncFromSlots(_leftSlot, _rightSlot);
    }

    private void SyncFromSlots(MirrorCaptureSlot leftSlot, MirrorCaptureSlot rightSlot)
    {
        long leftVersion = leftSlot.CaptureVersion;
        long rightVersion = rightSlot.CaptureVersion;
        Matrix4x4 currentWorld = _hasCurrentRenderWorldMatrix ? _currentRenderWorldMatrix : WorldMatrix;
        if (ReferenceEquals(_syncedLeftSlot, leftSlot) &&
            ReferenceEquals(_syncedRightSlot, rightSlot) &&
            _syncedLeftVersion == leftVersion &&
            _syncedRightVersion == rightVersion &&
            _syncedRenderWorldMatrix.Equals(currentWorld))
            return;

        XRTexture2D? leftTexture = leftSlot.Texture;
        XRTexture2D? rightTexture = rightSlot.Texture;
        _material.Textures[0] = leftTexture;
        _material.Textures[1] = rightTexture;
        _velocityMaterial.Textures[0] = leftTexture;
        _velocityMaterial.Textures[1] = rightTexture;
        _reactiveMaterial.Textures[0] = leftTexture;
        _reactiveMaterial.Textures[1] = rightTexture;
        _leftMatrixParameter.Value = ComputeEffectiveReflectedViewProjection(leftSlot, in currentWorld);
        _rightMatrixParameter.Value = ComputeEffectiveReflectedViewProjection(rightSlot, in currentWorld);
        _framebufferYDownParameter.Value = leftSlot.FramebufferYDown || rightSlot.FramebufferYDown;
        _syncedLeftSlot = leftSlot;
        _syncedRightSlot = rightSlot;
        _syncedLeftVersion = leftVersion;
        _syncedRightVersion = rightVersion;
        _syncedRenderWorldMatrix = currentWorld;
    }

    private static Matrix4x4 ComputeEffectiveReflectedViewProjection(
        MirrorCaptureSlot slot,
        in Matrix4x4 currentMirrorWorld)
    {
        Matrix4x4 capturedMirrorWorld = slot.CapturedMirrorWorldMatrix;
        if (currentMirrorWorld.Equals(capturedMirrorWorld) ||
            capturedMirrorWorld.Equals(Matrix4x4.Identity) ||
            !Matrix4x4.Invert(currentMirrorWorld, out Matrix4x4 inverseCurrentMirrorWorld))
            return slot.ReflectedViewProjection;

        // Remap fragments on a moved mirror quad back onto the captured quad plane so a deferred
        // offscreen capture never causes inverse perspective zoom during locomotion or rotation.
        return inverseCurrentMirrorWorld * capturedMirrorWorld * slot.ReflectedViewProjection;
    }

    public bool TryRetainCollectedResource()
    {
        if (!_leftSlot.TryRetainDisplayResource())
            return false;
        if (ReferenceEquals(_leftSlot, _rightSlot))
            return true;
        if (_rightSlot.TryRetainDisplayResource())
            return true;

        _leftSlot.ReleaseDisplayResource();
        return false;
    }

    public void ReleaseCollectedResource()
    {
        _leftSlot.ReleaseDisplayResource();
        if (!ReferenceEquals(_leftSlot, _rightSlot))
            _rightSlot.ReleaseDisplayResource();
    }

    public override void Render()
    {
        // Use the newest completed slot from each eye bank when AdvanceMirrorCaptures published a
        // newer frame between visibility collection and eye rendering.
        MirrorCaptureSlot activeLeft = _leftBank?.Published ?? _leftSlot;
        MirrorCaptureSlot activeRight = _rightBank?.Published ?? _rightSlot;
        if (!activeLeft.TryRetainGpuReader())
        {
            if (ReferenceEquals(activeLeft, _leftSlot) || !_leftSlot.TryRetainGpuReader())
                return;
            activeLeft = _leftSlot;
        }
        bool retainedRight = false;
        if (!ReferenceEquals(activeLeft, activeRight))
        {
            if (!activeRight.TryRetainGpuReader())
            {
                if (!ReferenceEquals(activeRight, _rightSlot) && !ReferenceEquals(activeLeft, _rightSlot) &&
                    _rightSlot.TryRetainGpuReader())
                {
                    activeRight = _rightSlot;
                    retainedRight = true;
                }
                else
                {
                    activeLeft.CompleteGpuReaderAfterRender();
                    return;
                }
            }
            else
            {
                retainedRight = true;
            }
        }
        try
        {
            // Pull the mirror's live render matrix on the render thread so late XR pose updates
            // always drive the quad transform and projected UVs without collect-thread clobbering.
            if (_ownerTransform is { } ownerTransform)
            {
                Matrix4x4 liveMirrorWorld = ownerTransform.RenderMatrix;
                UpdateLateRenderWorldMatrix(in liveMirrorWorld);
            }
            SyncFromSlots(activeLeft, activeRight);
            base.Render();
        }
        finally
        {
            activeLeft.CompleteGpuReaderAfterRender();
            if (retainedRight)
                activeRight.CompleteGpuReaderAfterRender();
        }
    }
}
