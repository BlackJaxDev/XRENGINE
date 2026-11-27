using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>
/// Renders an unlit cyan wireframe sphere directly in the on-top forward pass for a VR tracker
/// during calibration so the marker tracks the owning <see cref="TransformBase.RenderMatrix"/>
/// in the same frame without the one-frame queue delay of <see cref="DebugDrawComponent"/>.
/// </summary>
public sealed class VrCalibrationTrackerMarkerComponent : XRComponent, IRenderable
{
    private const float DefaultRadiusMeters = 0.025f;
    private const int RingPointCount = 24;

    private readonly XRMesh _mesh;
    private readonly XRMaterial _material;
    private readonly XRMeshRenderer _meshRenderer;
    private readonly RenderCommandMesh3D _command;
    private readonly RenderInfo3D _renderInfo;

    /// <summary>
    /// Creates the wireframe sphere geometry, unlit overlay material, and CPU-direct
    /// <see cref="RenderCommandMesh3D"/> registered with the owning scene node.
    /// </summary>
    public VrCalibrationTrackerMarkerComponent()
    {
        _mesh = XRMesh.Shapes.WireframeSphere(Vector3.Zero, DefaultRadiusMeters, RingPointCount);
        _material = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Cyan);
        _material.Name = "VR.CalibrationTrackerSphere";
        _material.RenderPass = (int)EDefaultRenderPass.OnTopForward;
        _material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        _material.RenderOptions.DepthTest.UpdateDepth = false;
        _material.RenderOptions.CullMode = ECullMode.None;
        _material.RenderOptions.ExcludeFromGpuIndirect = true;

        _meshRenderer = new XRMeshRenderer(_mesh, _material);
        _command = new RenderCommandMesh3D(
            (int)EDefaultRenderPass.OnTopForward,
            _meshRenderer,
            Matrix4x4.Identity,
            null)
        {
            ForceCpuRendering = true,
            GpuProfilingLabel = "VR.CalibrationTrackerSphere",
        };

        _renderInfo = RenderInfo3D.New(this, _command);
        _renderInfo.LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(DefaultRadiusMeters * 2.0f));
        _renderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
        _renderInfo.CastsShadows = false;
        _renderInfo.ReceivesShadows = false;
        RenderedObjects = [_renderInfo];
    }

    /// <inheritdoc />
    public RenderInfo[] RenderedObjects { get; }

    /// <summary>
    /// Copies the updated render-space world matrix into the overlay draw command and culling bounds
    /// so the marker stays locked to the parent tracker transform during locomotion and late XR updates.
    /// </summary>
    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        _command.WorldMatrix = renderMatrix;
        _renderInfo.CullingOffsetMatrix = renderMatrix;
    }
}
