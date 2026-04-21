using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public interface IRuntimeVrEyeCamera
{
    float Near { get; set; }
    float Far { get; set; }
}

public interface IRuntimeVrRenderModelHandle : IDisposable
{
    bool IsLoaded { get; }
    void Clear();
    void LoadModelAsync(object? renderModel);
}

public interface IRuntimeVrRenderingServices
{
    IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane);
    void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode);
    IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null);
}

public static class RuntimeVrRenderingServices
{
    private static readonly IRuntimeVrRenderingServices Default = new DefaultRuntimeVrRenderingServices();
    private static IRuntimeVrRenderingServices _current = Default;

    public static IRuntimeVrRenderingServices Current
    {
        get => _current;
        set => _current = value ?? Default;
    }

    public static IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
        => Current.CreateEyeCamera(transform, leftEye, nearPlane, farPlane);

    public static void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode)
        => Current.SetHeadsetViewInformation(leftEyeCamera, rightEyeCamera, world, hmdNode);

    public static IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null)
        => Current.CreateRenderModelHandle(node, childName);

    private sealed class DefaultRuntimeVrRenderingServices : IRuntimeVrRenderingServices
    {
        public IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
            => new DefaultRuntimeVrEyeCamera(nearPlane, farPlane);

        public void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode)
        {
        }

        public IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null)
            => new DefaultRuntimeVrRenderModelHandle();

        private sealed class DefaultRuntimeVrEyeCamera(float nearPlane, float farPlane) : IRuntimeVrEyeCamera
        {
            public float Near { get; set; } = nearPlane;
            public float Far { get; set; } = farPlane;
        }

        private sealed class DefaultRuntimeVrRenderModelHandle : IRuntimeVrRenderModelHandle
        {
            public bool IsLoaded => false;

            public void Clear()
            {
            }

            public void LoadModelAsync(object? renderModel)
            {
            }

            public void Dispose()
            {
            }
        }
    }
}