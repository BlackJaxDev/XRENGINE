using XREngine.Scene.Transforms;

namespace XREngine.Scene;

public interface IRuntimeSceneNodeServices
{
    IDisposable? StartProfileScope(string scopeName);
    object CreateDefaultTransform();
    bool TryValidateTransformAssignment(object node, object transform, out string? warningMessage);
    void ApplyLayerToComponent(object node, object component, int layer);
    void LogWarning(string message);
}

public static class RuntimeSceneNodeServices
{
    private static IRuntimeSceneNodeServices _current = new DefaultRuntimeSceneNodeServices();

    public static IRuntimeSceneNodeServices Current
    {
        get => _current;
        set => _current = value ?? new DefaultRuntimeSceneNodeServices();
    }

    private sealed class DefaultRuntimeSceneNodeServices : IRuntimeSceneNodeServices
    {
        public IDisposable? StartProfileScope(string scopeName)
            => null;

        public object CreateDefaultTransform()
            => new Transform();

        public bool TryValidateTransformAssignment(object node, object transform, out string? warningMessage)
        {
            warningMessage = null;
            return true;
        }

        public void ApplyLayerToComponent(object node, object component, int layer)
        {
        }

        public void LogWarning(string message)
        {
        }
    }
}
