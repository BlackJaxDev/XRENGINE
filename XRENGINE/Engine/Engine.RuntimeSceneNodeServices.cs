using XREngine.Components;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

internal sealed class EngineRuntimeSceneNodeServices : IRuntimeSceneNodeServices
{
    public IDisposable? StartProfileScope(string scopeName)
    {
        if (!Engine.Profiler.EnableFrameLogging)
            return null;
        return Engine.Profiler.Start(string.IsNullOrWhiteSpace(scopeName) ? "<unnamed>" : scopeName);
    }

    public object CreateDefaultTransform()
        => new Transform();

    public bool TryValidateTransformAssignment(object node, object transform, out string? warningMessage)
    {
        warningMessage = null;

        if (node is not SceneNode sceneNode || transform is not TransformBase transformBase)
            return true;

        if (transformBase is not UICanvasTransform || sceneNode.TryGetComponent<UICanvasComponent>(out _))
            return true;

        if (!sceneNode.TryGetComponent<PawnComponent>(out _) && !sceneNode.TryGetComponent<CameraComponent>(out _))
            return true;

        warningMessage = $"Ignoring attempt to assign UICanvasTransform to node '{sceneNode.Name}' because it has no UICanvasComponent.";
        return false;
    }

    public void ApplyLayerToComponent(object node, object component, int layer)
    {
        if (component is not IRenderable renderable)
            return;

        foreach (var renderInfo in renderable.RenderedObjects)
        {
            if (renderInfo is not RenderInfo3D renderInfo3D)
                continue;

            if (renderInfo3D.Layer == XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex)
                continue;

            renderInfo3D.Layer = layer;
        }
    }

    public void LogWarning(string message)
        => Debug.LogWarning(message);
}
