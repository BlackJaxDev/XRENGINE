using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Movement;
using XREngine.Components.Physics;
using XREngine.Components.Scene;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Surface;

public static partial class DeformedSurfaceEvaluator
{
    /// <summary>
    /// Finds every active component under <paramref name="root"/> that renders content but is not a
    /// <see cref="RenderableComponent"/> that <see cref="Evaluate(SceneNode, DeformedSurface?)"/> captures.
    /// Examples are particle emitters, UI canvases, sky boxes, and gaussian splat components.
    /// Each one has the reason <see cref="BakeSkippedComponent.NotCapturedReason"/>.
    /// </summary>
    /// <remarks>
    /// A component renders content when it is an <see cref="IRenderable"/>. These are not reported:
    /// UI elements, because their canvas is reported; lights, because the color capture records their light;
    /// and components that draw only editor or debug aids.
    /// </remarks>
    /// <param name="root">The root node of the content.</param>
    /// <returns>The skipped components, in depth-first order.</returns>
    public static IReadOnlyList<BakeSkippedComponent> FindSkipped(SceneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<BakeSkippedComponent> skipped = [];
        root.IterateComponents(component =>
        {
            if (IsNotCaptured(component) && IsRendered(component, root))
            {
                skipped.Add(new BakeSkippedComponent(
                    GetNodePath(component.SceneNode, root),
                    component.GetType().Name,
                    BakeSkippedComponent.NotCapturedReason));
            }
        }, true);
        return skipped;
    }

    private static bool IsNotCaptured(XRComponent component)
        => component switch
        {
            GaussianSplatComponent => true,
            RenderableComponent => false,
            UIComponent => false,
            IRenderable => !IsLightingOrDebugRenderer(component),
            _ => false,
        };

    /// <summary>
    /// Returns true for components whose own render output is a light, a light probe, or an editor or debug aid.
    /// </summary>
    private static bool IsLightingOrDebugRenderer(XRComponent component)
        => component is LightComponent
            or LightProbeComponent
            or HumanoidComponent
            or BaseIKSolverComponent
            or HumanoidPoseAuditOverlayComponent
            or Spline3DPreviewComponent
            or DebugDrawComponent
            or DebugVisualize3DComponent
            or CharacterMovementDebugRenderComponent
            or PhysicsJointComponent;

    /// <summary>Returns the node names from <paramref name="root"/> down to <paramref name="node"/>, joined by <c>/</c>.</summary>
    private static string GetNodePath(SceneNode node, SceneNode root)
    {
        List<string> names = [];
        for (SceneNode? current = node; current is not null; current = current.Parent)
        {
            names.Add(current.Name ?? string.Empty);
            if (ReferenceEquals(current, root))
                break;
        }

        names.Reverse();
        return string.Join('/', names);
    }
}
