using System.Numerics;
using XREngine.Components.Movement;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Movement;

/// <summary>
/// Rendering-owned diagnostic capsule for a sibling <see cref="CharacterMovement3DComponent"/>.
/// Kept separate so the movement component remains host and renderer independent.
/// </summary>
public sealed class CharacterMovementDebugRenderComponent : XRComponent, IRenderable
{
    private const int CapsuleRenderPointCountHalfCircle = 8;
    private readonly XRMeshRenderer _meshRenderer;
    private readonly RenderCommandMesh3D _command;
    private readonly RenderInfo3D _renderInfo;
    private float _lastRadius = float.NaN;
    private float _lastHeight = float.NaN;

    public CharacterMovementDebugRenderComponent()
    {
        XRMaterial material = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.DarkLavender);
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.RenderOptions.CullMode = ECullMode.None;
        material.EnableTransparency();

        _meshRenderer = new XRMeshRenderer(null, material);
        _command = new RenderCommandMesh3D((int)EDefaultRenderPass.OnTopForward, _meshRenderer, Matrix4x4.Identity, null);
        _renderInfo = RenderInfo3D.New(this, _command);
        _renderInfo.CastsShadows = false;
        _renderInfo.ReceivesShadows = false;
        RenderedObjects = [_renderInfo];
    }

    public RenderInfo[] RenderedObjects { get; }

    private CharacterMovement3DComponent? Movement
        => GetSiblingComponent<CharacterMovement3DComponent>();

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, 0, RefreshCapsule);
        RefreshCapsule();
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, 0, RefreshCapsule);
        base.OnComponentDeactivated();
    }

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        _command.WorldMatrix = renderMatrix;
        _renderInfo.CullingOffsetMatrix = renderMatrix;
    }

    private void RefreshCapsule()
    {
        CharacterMovement3DComponent? movement = Movement;
        if (movement is null)
            return;

        float radius = movement.Radius;
        float totalHeight = MathF.Max(movement.CurrentHeight, radius * 2.0f);
        if (radius == _lastRadius && totalHeight == _lastHeight)
            return;

        _lastRadius = radius;
        _lastHeight = totalHeight;
        float halfCylinderHeight = MathF.Max(0.0f, totalHeight - radius * 2.0f) * 0.5f;
        _meshRenderer.Mesh = XRMesh.Shapes.WireframeCapsule(
            Vector3.Zero,
            Globals.Up,
            radius,
            halfCylinderHeight,
            CapsuleRenderPointCountHalfCircle);
        _renderInfo.LocalCullingVolume = AABB.FromSize(new Vector3(radius * 2.0f, totalHeight, radius * 2.0f));
    }
}
