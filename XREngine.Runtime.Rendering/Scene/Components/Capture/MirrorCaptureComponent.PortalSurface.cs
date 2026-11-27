using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Lights;

public partial class MirrorCaptureComponent : IAdvancedPortalSurfaceProvider
{
    private EMirrorCaptureMode _captureMode = EMirrorCaptureMode.OffscreenCapture;
    private EMirrorMsaaShadingMode _msaaShadingMode = EMirrorMsaaShadingMode.SingleSample;
    private bool _allowRecursion;
    private bool _renderTransparents;
    private Vector4 _glassTint = new(1.0f, 1.0f, 1.0f, 0.0f);
    private float _fresnelStrength;
    private float _specularStrength;
    private Matrix4x4 _previousPortalWorldMatrix = Matrix4x4.Identity;
    private Matrix4x4 _currentPortalWorldMatrix = Matrix4x4.Identity;
    private ulong _portalTransformFrameId;
    private bool _hasPortalTransformHistory;
    private AdvancedGpuHandle _publishedPortalDrawHandle;

    /// <summary>
    /// Selects offscreen render-to-texture capture or in-place stencil visibility rendering
    /// on <see cref="AdvancedRenderPipeline"/>.
    /// </summary>
    public EMirrorCaptureMode CaptureMode
    {
        get => _captureMode;
        set
        {
            lock (_mirrorLifetimeSync)
                if (SetField(ref _captureMode, value))
                    _configurationDirty = true;
        }
    }

    /// <summary>
    /// Controls whether multisample visibility frames shade only the representative sample
    /// (<see cref="EMirrorMsaaShadingMode.SingleSample"/>) or every MSAA sample inside this mirror.
    /// </summary>
    public EMirrorMsaaShadingMode MsaaShadingMode
    {
        get => _msaaShadingMode;
        set => SetField(ref _msaaShadingMode, value);
    }

    /// <summary>Allows this mirror to participate in recursive mirror reflections when <see cref="CaptureMode"/> is <see cref="EMirrorCaptureMode.InPlaceStencil"/>.</summary>
    public bool AllowRecursion
    {
        get => _allowRecursion;
        set => SetField(ref _allowRecursion, value);
    }

    /// <summary>Enables reflected transparent draws during deepest-out portal unwind.</summary>
    public bool RenderTransparents
    {
        get => _renderTransparents;
        set => SetField(ref _renderTransparents, value);
    }

    /// <summary>Mirror glass tint RGB and blend alpha applied during deepest-out portal unwind.</summary>
    public Vector4 GlassTint
    {
        get => _glassTint;
        set => SetField(ref _glassTint, value);
    }

    /// <summary>Fresnel reflectance strength applied on the mirror glass composite pass.</summary>
    public float FresnelStrength
    {
        get => _fresnelStrength;
        set => SetField(ref _fresnelStrength, Math.Clamp(value, 0.0f, 1.0f));
    }

    /// <summary>Specular glass highlight strength applied on the mirror glass composite pass.</summary>
    public float SpecularStrength
    {
        get => _specularStrength;
        set => SetField(ref _specularStrength, Math.Clamp(value, 0.0f, 1.0f));
    }

    /// <inheritdoc />
    public ulong PortalProviderId => _renderInfo.StableInstanceId;

    /// <inheritdoc />
    public bool IsPortalSurfaceActive
    {
        get
        {
            lock (_mirrorLifetimeSync)
                return UsesInPlaceStencilOnAdvanced && !_retirementRequested && !IsDestroyed && IsActive;
        }
    }

    internal bool UsesInPlaceStencilOnAdvanced
        => _captureMode == EMirrorCaptureMode.InPlaceStencil;

    private void RegisterTransparentPortalSurfaceIfNeeded()
    {
        bool shouldRegister;
        lock (_mirrorLifetimeSync)
            shouldRegister = UsesInPlaceStencilOnAdvanced && _transparentBackground && !_retirementRequested && !IsDestroyed;
        if (!shouldRegister)
            return;
        World?.GetRenderWorld()?.VisualScene?.GPUCommands?.AdvancedScenePublisher?.RegisterPortalSurface(this);
    }

    private void UnregisterPortalSurfaceFromWorld()
        => World?.GetRenderWorld()?.VisualScene?.GPUCommands?.AdvancedScenePublisher?.UnregisterPortalSurface(this);

    /// <summary>Updates the canonical draw handle assigned to this mirror surface by the GPU scene publisher.</summary>
    internal void SetPublishedPortalDrawHandle(AdvancedGpuHandle drawHandle)
    {
        lock (_mirrorLifetimeSync)
            _publishedPortalDrawHandle = drawHandle;
    }

    /// <summary>
    /// Updates current and previous world transforms used to compute reflected current and
    /// previous view-projection matrices for moving-mirror TSR velocity. Advancing to a new
    /// frame with an unchanged transform collapses previous to current so stationary mirrors
    /// emit zero velocity.
    /// </summary>
    internal void RecordPortalWorldTransform(in Matrix4x4 worldMatrix, ulong renderFrameId)
    {
        lock (_mirrorLifetimeSync)
        {
            if (!_hasPortalTransformHistory)
            {
                _previousPortalWorldMatrix = worldMatrix;
                _currentPortalWorldMatrix = worldMatrix;
                _portalTransformFrameId = renderFrameId;
                _hasPortalTransformHistory = true;
                return;
            }

            if (renderFrameId != _portalTransformFrameId)
            {
                _previousPortalWorldMatrix = _currentPortalWorldMatrix;
                _portalTransformFrameId = renderFrameId;
            }

            _currentPortalWorldMatrix = worldMatrix;
        }
    }

    /// <inheritdoc />
    public bool TryCapturePortalSurface(out AdvancedPortalSurfaceDescriptor descriptor)
        => TryCapturePortalSurfaceForFrame(
            RuntimeEngine.Rendering.State.RenderFrameId,
            overrideWorldMatrix: null,
            allowWhenRetired: false,
            out descriptor);

    /// <summary>
    /// Captures the portal surface descriptor for <paramref name="renderFrameId"/>, advancing
    /// previous-frame transform history so moving mirrors populate accurate reflected motion
    /// vectors and stationary frames settle to zero velocity.
    /// </summary>
    internal bool TryCapturePortalSurfaceForFrame(
        ulong renderFrameId,
        Matrix4x4? overrideWorldMatrix,
        bool allowWhenRetired,
        out AdvancedPortalSurfaceDescriptor descriptor)
    {
        descriptor = default;
        Matrix4x4 worldMatrix;
        Matrix4x4 previousWorldMatrix;
        AdvancedGpuHandle drawHandle;
        Vector4 glassTint;
        float fresnel;
        float specular;
        uint cullingLayerMask;
        EMirrorMsaaShadingMode msaaMode;
        bool allowRecursion;
        bool renderTransparents;
        bool transparentBackground;

        lock (_mirrorLifetimeSync)
        {
            if (!UsesInPlaceStencilOnAdvanced || IsDestroyed || (!allowWhenRetired && _retirementRequested))
                return false;

            Matrix4x4 liveWorld = overrideWorldMatrix
                ?? Transform?.RenderMatrix
                ?? (_hasPortalTransformHistory ? _currentPortalWorldMatrix : Matrix4x4.Identity);
            if (!_hasPortalTransformHistory)
            {
                _previousPortalWorldMatrix = liveWorld;
                _currentPortalWorldMatrix = liveWorld;
                _portalTransformFrameId = renderFrameId;
                _hasPortalTransformHistory = true;
            }
            else if (renderFrameId > _portalTransformFrameId)
            {
                _previousPortalWorldMatrix = _currentPortalWorldMatrix;
                _currentPortalWorldMatrix = liveWorld;
                _portalTransformFrameId = renderFrameId;
            }
            else
            {
                // Refresh same-frame late XR pose updates (for example head-following calibration
                // mirrors) without advancing the previous-frame motion history.
                _currentPortalWorldMatrix = liveWorld;
            }

            worldMatrix = _currentPortalWorldMatrix;
            previousWorldMatrix = _previousPortalWorldMatrix;
            drawHandle = _transparentBackground ? default : _publishedPortalDrawHandle;
            glassTint = _glassTint;
            fresnel = _fresnelStrength;
            specular = _specularStrength;
            cullingLayerMask = unchecked((uint)_captureCullingMask.Value);
            msaaMode = _msaaShadingMode;
            allowRecursion = _allowRecursion;
            renderTransparents = _renderTransparents;
            transparentBackground = _transparentBackground;
        }

        if (!TryComputeMirrorPlane(in worldMatrix, out Vector3 planePoint, out Vector3 planeNormal, out Matrix4x4 reflectionMatrix))
            return false;
        if (!TryComputeMirrorPlane(in previousWorldMatrix, out Vector3 previousPlanePoint, out Vector3 previousPlaneNormal, out Matrix4x4 previousReflectionMatrix))
        {
            previousPlanePoint = planePoint;
            previousPlaneNormal = planeNormal;
            previousReflectionMatrix = reflectionMatrix;
        }

        Vector3 c0 = Vector3.Transform(new Vector3(-0.5f, -0.5f, 0.0f), worldMatrix);
        Vector3 c1 = Vector3.Transform(new Vector3(0.5f, -0.5f, 0.0f), worldMatrix);
        Vector3 c2 = Vector3.Transform(new Vector3(0.5f, 0.5f, 0.0f), worldMatrix);
        Vector3 c3 = Vector3.Transform(new Vector3(-0.5f, 0.5f, 0.0f), worldMatrix);

        float radius = MathF.Max(
            MathF.Max(Vector3.Distance(planePoint, c0), Vector3.Distance(planePoint, c1)),
            MathF.Max(Vector3.Distance(planePoint, c2), Vector3.Distance(planePoint, c3)));
        if (!float.IsFinite(radius) || radius <= 0.0f)
            return false;

        descriptor = new AdvancedPortalSurfaceDescriptor(
            ProviderId: PortalProviderId,
            DrawHandle: drawHandle,
            WorldMatrix: worldMatrix,
            PreviousWorldMatrix: previousWorldMatrix,
            PlanePoint: planePoint,
            PlaneNormal: planeNormal,
            PreviousPlanePoint: previousPlanePoint,
            PreviousPlaneNormal: previousPlaneNormal,
            QuadCorner0: c0,
            QuadCorner1: c1,
            QuadCorner2: c2,
            QuadCorner3: c3,
            BoundsSphere: new Vector4(planePoint, radius),
            GlassTint: glassTint,
            FresnelStrength: fresnel,
            SpecularStrength: specular,
            CullingLayerMask: cullingLayerMask,
            MsaaShadingMode: msaaMode,
            AllowRecursion: allowRecursion,
            RenderTransparents: renderTransparents,
            IsMirror: true,
            PortalTransform: reflectionMatrix,
            PreviousPortalTransform: previousReflectionMatrix,
            TransparentBackground: transparentBackground);
        return true;
    }

    private static bool TryComputeMirrorPlane(
        in Matrix4x4 worldMatrix,
        out Vector3 planePoint,
        out Vector3 planeNormal,
        out Matrix4x4 reflectionMatrix)
    {
        planePoint = worldMatrix.Translation;
        // Local +Z faces the reflecting side of the VertexQuad.PosZ display quad.
        Vector3 rawNormal = new(worldMatrix.M31, worldMatrix.M32, worldMatrix.M33);
        float scale = MathF.Max(MathF.Abs(rawNormal.X), MathF.Max(MathF.Abs(rawNormal.Y), MathF.Abs(rawNormal.Z)));
        if (!float.IsFinite(scale) || scale <= 0.0f ||
            !float.IsFinite(planePoint.X) || !float.IsFinite(planePoint.Y) || !float.IsFinite(planePoint.Z))
        {
            planeNormal = Vector3.UnitZ;
            reflectionMatrix = Matrix4x4.Identity;
            return false;
        }

        planeNormal = Vector3.Normalize(rawNormal / scale);
        float planeD = -Vector3.Dot(planeNormal, planePoint);
        reflectionMatrix = Matrix4x4.CreateReflection(new Plane(planeNormal, planeD));
        return true;
    }

    private static XRMaterial CreatePortalSurfaceMaterial()
    {
        XRMaterial material = XRMaterial.CreateColorMaterialDeferred();
        material.Name = "Mirror.InPlacePortalSurface";
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        material.RenderOptions.CullMode = ECullMode.Back;
        return material;
    }
}
