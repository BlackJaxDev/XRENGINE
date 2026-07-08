using Assimp;
using OpenVR.NET;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

internal sealed class EngineRuntimeVrRenderingServices : IRuntimeVrRenderingServices
{
    private readonly EngineRuntimeVrRenderModelProvider _renderModelProvider = new();

    public IRuntimeVrRenderModelProvider RenderModelProvider
        => _renderModelProvider;

    public IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
        => new EngineRuntimeVrEyeCamera(transform, leftEye, nearPlane, farPlane);

    public void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode)
    {
        XRCamera? leftCamera = (leftEyeCamera as EngineRuntimeVrEyeCamera)?.Camera;
        XRCamera? rightCamera = (rightEyeCamera as EngineRuntimeVrEyeCamera)?.Camera;
        XRWorldInstance? xrWorld = world as XRWorldInstance;
        Engine.VRState.ViewInformation = (leftCamera, rightCamera, xrWorld, hmdNode);
    }

    public bool TryEnsureHeadsetViewInformation(IRuntimeWorldContext? world, SceneNode? hmdNode, float nearPlane, float farPlane)
        => TryPublishActiveHeadsetComponent(world, hmdNode, nearPlane, farPlane);

    public IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null)
        => new EngineRuntimeVrRenderModelHandle(node, childName);

    private bool TryPublishActiveHeadsetComponent(IRuntimeWorldContext? world, SceneNode? hmdNode, float nearPlane, float farPlane)
    {
        var headset = VRHeadsetComponent.Instance;
        if (headset is null || !headset.IsActiveInHierarchy)
            return false;

        if (hmdNode is not null && !ReferenceEquals(hmdNode, headset.SceneNode))
            return false;

        if (world is not null && headset.World is not null && !ReferenceEquals(world, headset.World))
            return false;

        headset.LeftEyeCamera.Near = headset.RightEyeCamera.Near = nearPlane;
        headset.LeftEyeCamera.Far = headset.RightEyeCamera.Far = farPlane;
        SetHeadsetViewInformation(headset.LeftEyeCamera, headset.RightEyeCamera, headset.World ?? world, headset.SceneNode);
        return true;
    }

    private sealed class EngineRuntimeVrEyeCamera : IRuntimeVrEyeCamera
    {
        private readonly XROVRCameraParameters _parameters;

        public EngineRuntimeVrEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
        {
            _parameters = new XROVRCameraParameters(leftEye, nearPlane, farPlane);
            Camera = new XRCamera(transform, _parameters);
        }

        internal XRCamera Camera { get; }

        public float Near
        {
            get => _parameters.NearZ;
            set => _parameters.NearZ = value;
        }

        public float Far
        {
            get => _parameters.FarZ;
            set => _parameters.FarZ = value;
        }
    }

    private sealed class EngineRuntimeVrRenderModelProvider : IRuntimeVrRenderModelProvider
    {
        private static readonly string[] OpenXrTrackerRoleOrder =
        [
            "/user/vive_tracker_htcx/role/waist",
            "/user/vive_tracker_htcx/role/chest",
            "/user/vive_tracker_htcx/role/left_foot",
            "/user/vive_tracker_htcx/role/right_foot",
            "/user/vive_tracker_htcx/role/left_shoulder",
            "/user/vive_tracker_htcx/role/right_shoulder",
            "/user/vive_tracker_htcx/role/left_elbow",
            "/user/vive_tracker_htcx/role/right_elbow",
            "/user/vive_tracker_htcx/role/left_knee",
            "/user/vive_tracker_htcx/role/right_knee",
            "/user/vive_tracker_htcx/role/camera",
            "/user/vive_tracker_htcx/role/keyboard",
        ];

        private VR? _modelOnlyOpenVr;
        private bool _triedModelOnlyOpenVr;
        private bool _openVrUnavailableLogged;
        private bool _openVrFallbackDisabledLogged;
        private Action? _modelsChanged;

        public event Action? ModelsChanged
        {
            add => _modelsChanged += value;
            remove => _modelsChanged -= value;
        }

        public bool TryGetControllerRenderModel(bool leftHand, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        {
            renderModel = null;

            if (Engine.VRState.IsOpenXRActive &&
                Engine.VRState.OpenXRApi is { } openXrApi &&
                openXrApi.TryGetControllerRenderModel(leftHand, out renderModel))
            {
                return true;
            }

            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                LogOpenVrFallbackDisabledDuringOpenXr();
                return false;
            }

            if (TryGetOpenVrControllerDevice(leftHand) is DeviceModel deviceModel)
            {
                renderModel = RuntimeVrRenderModelDescriptor.FromOpenVrDeviceModel(
                    deviceModel,
                    $"openvr-controller:{(leftHand ? "left" : "right")}:{deviceModel.Name}",
                    $"{(leftHand ? "Left" : "Right")} SteamVR controller model");
                return true;
            }

            if (!TryGetOpenVrSystem(out CVRSystem? cvr))
                return false;

            ETrackedControllerRole role = leftHand ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
            uint deviceIndex = cvr.GetTrackedDeviceIndexForControllerRole(role);
            if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                return false;

            return TryCreateOpenVrDescriptorForDevice(
                cvr,
                deviceIndex,
                ETrackedDeviceClass.Controller,
                $"openvr-controller:{(leftHand ? "left" : "right")}",
                $"{(leftHand ? "Left" : "Right")} SteamVR controller model",
                out renderModel);
        }

        public bool TryGetTrackerRenderModel(string? openXrTrackerUserPath, uint? openVrDeviceIndex, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        {
            renderModel = null;

            if (openVrDeviceIndex is uint deviceIndex && TryGetOpenVrTrackedDeviceModel(deviceIndex) is DeviceModel trackedDeviceModel)
            {
                renderModel = RuntimeVrRenderModelDescriptor.FromOpenVrDeviceModel(
                    trackedDeviceModel,
                    $"openvr-tracker:{deviceIndex}:{trackedDeviceModel.Name}",
                    $"SteamVR tracker {deviceIndex} model");
                return true;
            }

            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                LogOpenVrFallbackDisabledDuringOpenXr();
                return false;
            }

            if (!TryGetOpenVrSystem(out CVRSystem? cvr))
                return false;

            if (openVrDeviceIndex is uint explicitDeviceIndex &&
                TryCreateOpenVrDescriptorForDevice(
                    cvr,
                    explicitDeviceIndex,
                    ETrackedDeviceClass.GenericTracker,
                    $"openvr-tracker:{explicitDeviceIndex}",
                    $"SteamVR tracker {explicitDeviceIndex} model",
                    out renderModel))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(openXrTrackerUserPath))
                return false;

            uint[] trackerIndices = EnumerateTrackedDevices(cvr, ETrackedDeviceClass.GenericTracker);
            if (trackerIndices.Length == 0)
                return false;

            uint selectedIndex = SelectTrackerIndexForOpenXrPath(openXrTrackerUserPath, trackerIndices);
            return TryCreateOpenVrDescriptorForDevice(
                cvr,
                selectedIndex,
                ETrackedDeviceClass.GenericTracker,
                $"openvr-tracker:{openXrTrackerUserPath}",
                $"SteamVR tracker model for {openXrTrackerUserPath}",
                out renderModel);
        }

        public string DescribeAvailability()
        {
            string openXr = Engine.VRState.OpenXRApi?.DescribeControllerRenderModelAvailability() ?? "OpenXR not initialized";
            string openVr = DescribeOpenVrRenderModelAvailability();
            return $"{openXr}; {openVr}";
        }

        private static DeviceModel? TryGetOpenVrControllerDevice(bool leftHand)
        {
            if (!Engine.VRState.IsOpenVRActive)
                return null;

            VrDevice? device = leftHand ? Engine.VRState.OpenVRApi.LeftController : Engine.VRState.OpenVRApi.RightController;
            return device?.Model;
        }

        private static DeviceModel? TryGetOpenVrTrackedDeviceModel(uint deviceIndex)
        {
            if (!Engine.VRState.IsOpenVRActive)
                return null;

            foreach (VrDevice device in Engine.VRState.OpenVRApi.TrackedDevices)
            {
                if (device.DeviceIndex == deviceIndex &&
                    Engine.VRState.OpenVRApi.CVR.GetTrackedDeviceClass(deviceIndex) == ETrackedDeviceClass.GenericTracker)
                {
                    return device.Model;
                }
            }

            return null;
        }

        private bool TryGetOpenVrSystem([NotNullWhen(true)] out CVRSystem? cvr)
        {
            cvr = null;

            if (Engine.VRState.IsOpenVRActive &&
                Engine.VRState.OpenVRApi.State.HasFlag(VrState.OK) &&
                Engine.VRState.OpenVRApi.CVR is { } activeCvr)
            {
                cvr = activeCvr;
                return true;
            }

            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                LogOpenVrFallbackDisabledDuringOpenXr();
                return false;
            }

            if (_modelOnlyOpenVr is { State: var state } && state.HasFlag(VrState.OK) && _modelOnlyOpenVr.CVR is { } modelOnlyCvr)
            {
                cvr = modelOnlyCvr;
                return true;
            }

            if (_triedModelOnlyOpenVr)
                return false;

            _triedModelOnlyOpenVr = true;

            try
            {
                _modelOnlyOpenVr = new VR();
                if (!_modelOnlyOpenVr.TryStart(EVRApplicationType.VRApplication_Utility))
                {
                    LogOpenVrUnavailable("OpenVR utility initialization failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogOpenVrUnavailable($"OpenVR utility initialization failed: {ex.Message}");
                return false;
            }

            cvr = _modelOnlyOpenVr.CVR;
            _modelsChanged?.Invoke();
            return cvr is not null;
        }

        private string DescribeOpenVrRenderModelAvailability()
        {
            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                return
                    $"SteamVR/OpenVR render-model fallback disabled while OpenXR is requested or active; set {XREngineEnvironmentVariables.OpenXrAllowOpenVrRenderModelFallback}=1 to opt in";
            }

            return TryGetOpenVrSystem(out _)
                ? "SteamVR/OpenVR render-model service available"
                : "SteamVR/OpenVR render-model service unavailable";
        }

        private static bool ShouldBlockOpenVrRenderModelFallbackDuringOpenXr()
        {
            if (Engine.VRState.IsOpenVRActive)
                return false;

            return IsOpenXrRuntimeRequestedOrInitialized() && !IsTruthyEnvironmentValue(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrAllowOpenVrRenderModelFallback));
        }

        private static bool IsOpenXrRuntimeRequestedOrInitialized()
        {
            if (Engine.VRState.IsOpenXRActive ||
                Engine.VRState.OpenXRApi is not null ||
                Engine.StartupOpenXrRuntimeRequested ||
                Engine.GameSettings is IVRGameStartupSettings { VRRuntime: EVRRuntime.OpenXR })
            {
                return true;
            }

            string? unitTestVrMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode);
            return string.Equals(unitTestVrMode, "MonadoOpenXR", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(unitTestVrMode, "OpenXR", StringComparison.OrdinalIgnoreCase) ||
                   IsTruthyEnvironmentValue(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr));
        }

        private void LogOpenVrFallbackDisabledDuringOpenXr()
        {
            if (_openVrFallbackDisabledLogged)
                return;

            _openVrFallbackDisabledLogged = true;
            Debug.LogWarning(
                $"SteamVR/OpenVR render-model fallback skipped while OpenXR is requested or active. Set {XREngineEnvironmentVariables.OpenXrAllowOpenVrRenderModelFallback}=1 to opt in.");
        }

        private void LogOpenVrUnavailable(string reason)
        {
            if (_openVrUnavailableLogged)
                return;

            _openVrUnavailableLogged = true;
            Debug.LogWarning($"SteamVR render models unavailable while resolving runtime VR device models. Reason={reason}");
        }

        private static bool IsTruthyEnvironmentValue(string? value)
            => value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));

        private static bool TryCreateOpenVrDescriptorForDevice(
            CVRSystem cvr,
            uint deviceIndex,
            ETrackedDeviceClass expectedClass,
            string keyPrefix,
            string displayName,
            [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        {
            renderModel = null;

            if (cvr.GetTrackedDeviceClass(deviceIndex) != expectedClass)
                return false;

            if (!TryGetOpenVrRenderModelName(cvr, deviceIndex, out string? modelName))
                return false;

            DeviceModel deviceModel = new(modelName);
            renderModel = RuntimeVrRenderModelDescriptor.FromOpenVrDeviceModel(
                deviceModel,
                $"{keyPrefix}:{modelName}",
                displayName);
            return true;
        }

        private static bool TryGetOpenVrRenderModelName(CVRSystem cvr, uint deviceIndex, [NotNullWhen(true)] out string? modelName)
        {
            modelName = null;

            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            StringBuilder builder = new(256);
            uint length = cvr.GetStringTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_RenderModelName_String,
                builder,
                (uint)builder.Capacity,
                ref error);

            if (error == ETrackedPropertyError.TrackedProp_BufferTooSmall)
            {
                builder.EnsureCapacity((int)length);
                error = ETrackedPropertyError.TrackedProp_Success;
                cvr.GetStringTrackedDeviceProperty(
                    deviceIndex,
                    ETrackedDeviceProperty.Prop_RenderModelName_String,
                    builder,
                    length,
                    ref error);
            }

            if (error != ETrackedPropertyError.TrackedProp_Success)
                return false;

            modelName = builder.ToString();
            return !string.IsNullOrWhiteSpace(modelName);
        }

        private static uint[] EnumerateTrackedDevices(CVRSystem cvr, ETrackedDeviceClass trackedDeviceClass)
        {
            uint[] scratch = new uint[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
            int count = 0;

            for (uint i = 0; i < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                if (cvr.GetTrackedDeviceClass(i) != trackedDeviceClass)
                    continue;

                scratch[count++] = i;
            }

            if (count == 0)
                return [];

            uint[] result = new uint[count];
            Array.Copy(scratch, result, count);
            return result;
        }

        private static uint SelectTrackerIndexForOpenXrPath(string openXrTrackerUserPath, uint[] trackerIndices)
        {
            if (trackerIndices.Length == 1)
                return trackerIndices[0];

            for (int i = 0; i < OpenXrTrackerRoleOrder.Length; i++)
            {
                if (string.Equals(OpenXrTrackerRoleOrder[i], openXrTrackerUserPath, StringComparison.Ordinal))
                    return trackerIndices[Math.Min(i, trackerIndices.Length - 1)];
            }

            int hash = StringComparer.Ordinal.GetHashCode(openXrTrackerUserPath) & int.MaxValue;
            return trackerIndices[hash % trackerIndices.Length];
        }
    }

    private sealed class EngineRuntimeVrRenderModelHandle : IRuntimeVrRenderModelHandle
    {
        private readonly SceneNode _renderNode;
        private readonly ModelComponent _modelComponent;
        private SceneNode? _importedModelRoot;
        private int _loadGeneration;
        private bool _isLoading;
        private bool _disposed;

        public EngineRuntimeVrRenderModelHandle(SceneNode node, string? childName)
        {
            _renderNode = node.NewChild(childName ?? "VR Render Model");
            _modelComponent = _renderNode.AddComponent<ModelComponent>()!;
        }

        public bool IsLoaded => !_disposed && (_isLoading || _modelComponent.Model is not null || _importedModelRoot is not null);

        public void Clear()
        {
            if (_disposed)
                return;

            unchecked
            {
                _loadGeneration++;
            }
            _isLoading = false;

            if (_importedModelRoot is not null)
            {
                try
                {
                    _importedModelRoot.Destroy();
                }
                catch
                {
                }

                _importedModelRoot = null;
            }

            if (_modelComponent.Model is Model model)
            {
                _modelComponent.Model = null;
                model.Destroy();
            }
        }

        public void LoadModelAsync(RuntimeVrRenderModelDescriptor? renderModel)
        {
            if (_disposed || renderModel is null)
                return;

            Clear();
            int generation = _loadGeneration;

            if (renderModel.NativeModel is DeviceModel deviceModel)
            {
                Model model = new();
                _modelComponent.Model = model;
                Task.Run(() => LoadDeviceAsync(deviceModel, model, generation));
                return;
            }

            if (renderModel.BinaryModelData is { Length: > 0 })
            {
                _isLoading = true;
                _ = LoadBinaryModelAsync(renderModel, generation);
            }
        }

        private async Task LoadBinaryModelAsync(RuntimeVrRenderModelDescriptor renderModel, int generation)
        {
            try
            {
                string path = WriteBinaryModelCache(renderModel);
                var result = await ModelImporter.ImportAsync(
                    path,
                    PostProcessSteps.Triangulate |
                    PostProcessSteps.GenerateSmoothNormals |
                    PostProcessSteps.CalculateTangentSpace |
                    PostProcessSteps.JoinIdenticalVertices |
                    PostProcessSteps.ImproveCacheLocality,
                    onCompleted: null,
                    materialFactory: null,
                    parent: _renderNode,
                    scaleConversion: 1.0f,
                    zUp: false,
                    batchSubmeshAddsDuringAsyncImport: false);

                if (_disposed || generation != _loadGeneration)
                {
                    result.rootNode?.Destroy();
                    return;
                }

                _importedModelRoot = result.rootNode;
                if (_importedModelRoot is not null)
                    _importedModelRoot.Name = renderModel.DisplayName;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to load VR render model '{renderModel.DisplayName}': {ex.Message}");
            }
            finally
            {
                if (!_disposed && generation == _loadGeneration)
                    _isLoading = false;
            }
        }

        private static string WriteBinaryModelCache(RuntimeVrRenderModelDescriptor renderModel)
        {
            byte[] data = renderModel.BinaryModelData!;
            string extension = string.IsNullOrWhiteSpace(renderModel.BinaryModelFileExtension)
                ? ".bin"
                : renderModel.BinaryModelFileExtension;
            if (!extension.StartsWith('.'))
                extension = "." + extension;

            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XREngine",
                "RuntimeVrRenderModels");
            Directory.CreateDirectory(directory);

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(renderModel.Key))).ToLowerInvariant();
            string fileName = $"{SanitizeFileStem(renderModel.DisplayName)}-{hash}{extension}";
            string path = Path.Combine(directory, fileName);

            if (!File.Exists(path) || new FileInfo(path).Length != data.Length)
                File.WriteAllBytes(path, data);

            return path;
        }

        private static string SanitizeFileStem(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "vr-render-model";

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new(value.Length);
            for (int i = 0; i < value.Length && builder.Length < 80; i++)
            {
                char c = value[i];
                builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '-' : c);
            }

            return builder.Length == 0 ? "vr-render-model" : builder.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Clear();
            _disposed = true;

            try
            {
                _renderNode.Destroy();
            }
            catch
            {
            }
        }

        private async Task LoadDeviceAsync(DeviceModel deviceModel, Model model, int generation)
        {
            foreach (ComponentModel comp in deviceModel.Components)
            {
                if (_disposed || generation != _loadGeneration)
                    return;

                SubMesh? subMesh = await LoadComponentAsync(comp);
                if (subMesh is not null && !_disposed && generation == _loadGeneration)
                    model.Meshes.Add(subMesh);
            }
        }

        private static async Task<SubMesh?> LoadComponentAsync(ComponentModel comp)
        {
            if (!comp.ModelName.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                return null;

            XRMesh mesh = new();
            XRMaterial mat = new();

            List<Vertex> vertices = [];
            List<ushort> triangleIndices = [];
            List<XRTexture2D> textures = [];

            SubMesh? subMesh = null;

            void OnError(EVRRenderModelError error, ComponentModel.Context context)
            {
            }

            void AddTexture(ComponentModel.Texture texture)
                => textures.Add(new XRTexture2D(texture.LoadImage(true)));

            void AddTriangle(short index0, short index1, short index2)
            {
                triangleIndices.Add((ushort)index0);
                triangleIndices.Add((ushort)index2);
                triangleIndices.Add((ushort)index1);
            }

            void AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
                => vertices.Add(new Vertex(position, normal, uv));

            void End(ComponentModel.ComponentType type)
            {
                if (vertices.Count == 0 || triangleIndices.Count == 0)
                    return;

                if (triangleIndices.Max() >= vertices.Count)
                {
                    Debug.VRWarning("Invalid triangle index detected in model component.");
                    return;
                }

                mesh = new XRMesh(vertices, triangleIndices);
                mat = textures.Count > 0
                    ? XRMaterial.CreateLitTextureMaterial(textures[0])
                    : XRMaterial.CreateLitColorMaterial(ColorF4.Magenta);
                subMesh = new SubMesh(new SubMeshLOD(mat, mesh, 0.0f));
            }

            bool Begin(ComponentModel.ComponentType type)
                => true;

            await comp.LoadAsync(Begin, End, AddVertex, AddTriangle, AddTexture, OnError);
            return subMesh;
        }
    }
}
