using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public class PhysicsChainPlaneCollider : PhysicsChainColliderBase, IRenderable
{
    public Plane _plane;

    public RenderInfo[] RenderedObjects { get; }

    public PhysicsChainPlaneCollider()
    {
        var renderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, OnDrawGizmosSelected));
        renderInfo.Layer = DefaultLayers.GizmosIndex;
        RenderedObjects = [renderInfo];
    }

    public override void Prepare()
    {
        if (!TryResolveEffectiveTransform(null, out TransformBase effectiveTransform))
        {
            _plane = default;
            return;
        }

        Vector3 normal = Globals.Up;
        switch (_direction)
        {
            case Direction.X:
                normal = effectiveTransform.WorldRight;
                break;
            case Direction.Y:
                normal = effectiveTransform.WorldUp;
                break;
            case Direction.Z:
                normal = effectiveTransform.WorldForward;
                break;
        }

        Vector3 p = effectiveTransform.TransformPoint(_center);
        _plane = XRMath.CreatePlaneFromPointAndNormal(p, normal);
    }

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        if (!TryResolveEffectiveTransform(null, out _))
            return false;

        float d = GeoUtil.DistanceFrom.PlaneToPoint(_plane, particlePosition);

        if (_bound == EBound.Outside)
        {
            if (d < 0)
            {
                particlePosition -= _plane.Normal * d;
                return true;
            }
        }
        else
        {
            if (d > 0)
            {
                particlePosition -= _plane.Normal * d;
                return true;
            }
        }

        return false;
    }

    private void OnDrawGizmosSelected()
    {
        if (!IsActiveInHierarchy || Engine.Rendering.State.IsShadowPass)
            return;

        if (Transform is null)
            return;

        Prepare();

        ColorF4 color = _bound == EBound.Outside ? ColorF4.Yellow : ColorF4.Magenta;
        Vector3 p = Transform.TransformPoint(_center);
        Engine.Rendering.Debug.RenderLine(p, p + _plane.Normal, color);
    }
}
