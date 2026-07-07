using System.Diagnostics.CodeAnalysis;
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
    void LoadModelAsync(RuntimeVrRenderModelDescriptor? renderModel);
}

public enum RuntimeVrRenderModelSourceKind
{
    Unknown,
    OpenVrDeviceModel,
    OpenXrControllerModel,
    OpenXrInteractionRenderModel,
    Fallback,
}

public sealed class RuntimeVrRenderModelDescriptor
{
    private RuntimeVrRenderModelDescriptor(
        string key,
        string displayName,
        RuntimeVrRenderModelSourceKind sourceKind,
        object? nativeModel,
        byte[]? binaryModelData,
        string? binaryModelFileExtension)
    {
        Key = key;
        DisplayName = displayName;
        SourceKind = sourceKind;
        NativeModel = nativeModel;
        BinaryModelData = binaryModelData;
        BinaryModelFileExtension = binaryModelFileExtension;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public RuntimeVrRenderModelSourceKind SourceKind { get; }
    public object? NativeModel { get; }
    public byte[]? BinaryModelData { get; }
    public string? BinaryModelFileExtension { get; }

    public static RuntimeVrRenderModelDescriptor FromOpenVrDeviceModel(object deviceModel, string key, string displayName)
        => new(key, displayName, RuntimeVrRenderModelSourceKind.OpenVrDeviceModel, deviceModel, null, null);

    public static RuntimeVrRenderModelDescriptor FromOpenXrControllerModel(byte[] glbData, string key, string displayName)
        => new(key, displayName, RuntimeVrRenderModelSourceKind.OpenXrControllerModel, null, glbData, ".glb");
}

public interface IRuntimeVrRenderModelProvider
{
    event Action? ModelsChanged;

    bool TryGetControllerRenderModel(bool leftHand, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel);
    bool TryGetTrackerRenderModel(string? openXrTrackerUserPath, uint? openVrDeviceIndex, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel);
    string DescribeAvailability();
}

public interface IRuntimeVrRenderingServices
{
    IRuntimeVrRenderModelProvider RenderModelProvider { get; }
    IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane);
    void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode);
    bool TryEnsureHeadsetViewInformation(IRuntimeWorldContext? world, SceneNode? hmdNode, float nearPlane, float farPlane);
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

    public static IRuntimeVrRenderModelProvider RenderModelProvider
        => Current.RenderModelProvider;

    public static void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode)
        => Current.SetHeadsetViewInformation(leftEyeCamera, rightEyeCamera, world, hmdNode);

    public static bool TryEnsureHeadsetViewInformation(IRuntimeWorldContext? world, SceneNode? hmdNode, float nearPlane, float farPlane)
        => Current.TryEnsureHeadsetViewInformation(world, hmdNode, nearPlane, farPlane);

    public static IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null)
        => Current.CreateRenderModelHandle(node, childName);

    private sealed class DefaultRuntimeVrRenderingServices : IRuntimeVrRenderingServices
    {
        public IRuntimeVrRenderModelProvider RenderModelProvider { get; } = new DefaultRuntimeVrRenderModelProvider();

        public IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
            => new DefaultRuntimeVrEyeCamera(nearPlane, farPlane);

        public void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode)
        {
        }

        public bool TryEnsureHeadsetViewInformation(IRuntimeWorldContext? world, SceneNode? hmdNode, float nearPlane, float farPlane)
            => false;

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

            public void LoadModelAsync(RuntimeVrRenderModelDescriptor? renderModel)
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class DefaultRuntimeVrRenderModelProvider : IRuntimeVrRenderModelProvider
        {
            public event Action? ModelsChanged
            {
                add { }
                remove { }
            }

            public bool TryGetControllerRenderModel(bool leftHand, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
            {
                renderModel = null;
                return false;
            }

            public bool TryGetTrackerRenderModel(string? openXrTrackerUserPath, uint? openVrDeviceIndex, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
            {
                renderModel = null;
                return false;
            }

            public string DescribeAvailability()
                => "No runtime VR render-model provider is installed.";
        }
    }
}
