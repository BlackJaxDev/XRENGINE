using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
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
        if (leftCamera is not null)
            leftCamera.CullingMask = DefaultLayers.FirstPersonVr;
        if (rightCamera is not null)
            rightCamera.CullingMask = DefaultLayers.FirstPersonVr;
        IRuntimeRenderWorld? renderWorld = RuntimeRenderWorldRegistry.Get(world);
        EngineVrLifecycle.ViewInformation = (leftCamera, rightCamera, renderWorld, hmdNode);
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
        public int CullingMask { get => Camera.CullingMask.Value; set => Camera.CullingMask = value; }

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
        private readonly OpenVrModelCatalog _openVrModels = new();
        private bool _openVrUnavailableLogged;
        private bool _openVrFallbackDisabledLogged;
        private Action? _modelsChanged;

        public EngineRuntimeVrRenderModelProvider()
            => _openVrModels.ModelsChanged += () => _modelsChanged?.Invoke();

        public event Action? ModelsChanged
        {
            add => _modelsChanged += value;
            remove => _modelsChanged -= value;
        }

        public bool TryGetControllerRenderModel(bool leftHand, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        {
            renderModel = null;

            if (RuntimeEngine.VRState.IsOpenXRActive &&
                RuntimeEngine.VRState.OpenXRApi is { } openXrApi &&
                openXrApi.TryGetControllerRenderModel(leftHand, out renderModel))
            {
                return true;
            }

            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                LogOpenVrFallbackDisabledDuringOpenXr();
                return false;
            }

            if (!_openVrModels.TryGetControllerModelName(
                leftHand, RuntimeEngine.VRState.IsOpenVRActive, allowUtilityRuntime: true, out string? modelName))
            {
                LogOpenVrUnavailable(_openVrModels.LastFailure ?? "render-model service unavailable");
                return false;
            }

            renderModel = RuntimeVrRenderModelDescriptor.FromOpenVrModelName(
                modelName!,
                $"openvr-controller:{(leftHand ? "left" : "right")}:{modelName}",
                $"{(leftHand ? "Left" : "Right")} SteamVR controller model",
                alignOpenVrModelToGripPose: RuntimeEngine.VRState.IsOpenXRActive);
            return true;
        }

        public bool TryGetTrackerRenderModel(string? openXrTrackerUserPath, uint? openVrDeviceIndex, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        {
            renderModel = null;

            bool openVrActive = RuntimeEngine.VRState.IsOpenVRActive;
            if (openVrDeviceIndex is uint directIndex &&
                _openVrModels.TryGetTrackerModelName(
                    directIndex, null, openVrActive, allowUtilityRuntime: false,
                    out _, out string? directModelName))
            {
                renderModel = RuntimeVrRenderModelDescriptor.FromOpenVrModelName(
                    directModelName!,
                    $"openvr-tracker:{directIndex}:{directModelName}",
                    $"SteamVR tracker {directIndex} model");
                return true;
            }

            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                LogOpenVrFallbackDisabledDuringOpenXr();
                return false;
            }

            if (!_openVrModels.TryGetTrackerModelName(
                openVrDeviceIndex, openXrTrackerUserPath, openVrActive, allowUtilityRuntime: true,
                out uint selectedIndex, out string? modelName))
            {
                LogOpenVrUnavailable(_openVrModels.LastFailure ?? "render-model service unavailable");
                return false;
            }

            string keyPrefix = openVrDeviceIndex.HasValue
                ? $"openvr-tracker:{selectedIndex}"
                : $"openvr-tracker:{openXrTrackerUserPath}";
            string displayName = openVrDeviceIndex.HasValue
                ? $"SteamVR tracker {selectedIndex} model"
                : $"SteamVR tracker model for {openXrTrackerUserPath}";
            renderModel = RuntimeVrRenderModelDescriptor.FromOpenVrModelName(
                modelName!, $"{keyPrefix}:{modelName}", displayName);
            return true;
        }

        public string DescribeAvailability()
        {
            string openXr = RuntimeEngine.VRState.OpenXRApi?.DescribeControllerRenderModelAvailability() ?? "OpenXR not initialized";
            string openVr = DescribeOpenVrRenderModelAvailability();
            return $"{openXr}; {openVr}";
        }


        private string DescribeOpenVrRenderModelAvailability()
        {
            if (ShouldBlockOpenVrRenderModelFallbackDuringOpenXr())
            {
                return
                    $"SteamVR/OpenVR render-model fallback disabled while OpenXR is requested or active; set {XREngineEnvironmentVariables.OpenXrAllowOpenVrRenderModelFallback}=1 to opt in";
            }

            return _openVrModels.HasSystem(RuntimeEngine.VRState.IsOpenVRActive, allowUtilityRuntime: true)
                ? "SteamVR/OpenVR render-model service available"
                : "SteamVR/OpenVR render-model service unavailable";
        }

        private static bool ShouldBlockOpenVrRenderModelFallbackDuringOpenXr()
        {
            if (RuntimeEngine.VRState.IsOpenVRActive)
                return false;

            string? explicitOverride = XREnvironment.GetValue(XREngineEnvironmentVariables.OpenXrAllowOpenVrRenderModelFallback);
            if (!string.IsNullOrWhiteSpace(explicitOverride))
                return !IsTruthyEnvironmentValue(explicitOverride);

            // Allow OpenVR utility render models automatically when SteamVR is the active OpenXR
            // runtime so Valve Index controllers and Vive/Tundra trackers load their real meshes.
            if (RuntimeEngine.VRState.OpenXRApi?.IsSteamVrRuntime == true)
                return false;

            return IsOpenXrRuntimeRequestedOrInitialized();
        }

        private static bool IsOpenXrRuntimeRequestedOrInitialized()
        {
            if (RuntimeEngine.VRState.IsOpenXRActive ||
                RuntimeEngine.VRState.OpenXRApi is not null ||
                Engine.StartupOpenXrRuntimeRequested ||
                Engine.GameSettings is IVRGameStartupSettings { VRRuntime: EVRRuntime.OpenXR })
            {
                return true;
            }

            string? unitTestVrMode = XREnvironment.GetValue(XREngineEnvironmentVariables.UnitTestVrMode);
            return string.Equals(unitTestVrMode, "MonadoOpenXR", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(unitTestVrMode, "OpenXR", StringComparison.OrdinalIgnoreCase) ||
                   IsTruthyEnvironmentValue(XREnvironment.GetValue(XREngineEnvironmentVariables.UnitTestUseOpenXr));
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
            _renderNode = new SceneNode
            {
                Name = childName ?? "VR Render Model",
                Layer = DefaultLayers.CharactersIndex,
            };
            _modelComponent = _renderNode.AddComponent<ModelComponent>()!;
            _renderNode.Transform.SetParent(node.Transform, false, EParentAssignmentMode.Immediate);
        }

        public bool IsLoaded => !_disposed && (_isLoading || _modelComponent.Model is not null || _importedModelRoot is not null);

        public void SetVisible(bool visible)
        {
            if (_disposed || _renderNode.IsDestroyed || _renderNode.IsDestroyQueued)
                return;
            if (_renderNode.IsActiveSelf != visible)
                _renderNode.IsActiveSelf = visible;
        }

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

            if (renderModel.OpenVrModelName is { Length: > 0 } modelName)
            {
                _isLoading = true;
                bool alignToGripPose = renderModel.AlignOpenVrModelToGripPose;
                _ = Task.Run(() => LoadOpenVrModelAsync(modelName, alignToGripPose, generation));
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
                SceneNode? importedRoot = await RuntimeModelSceneLoadingServices.Current.LoadAsync(
                    path,
                    _renderNode);

                if (_disposed || generation != _loadGeneration)
                {
                    importedRoot?.Destroy();
                    return;
                }

                _importedModelRoot = importedRoot;
                if (_importedModelRoot is not null)
                {
                    _importedModelRoot.Name = renderModel.DisplayName;
                    ApplyLayerRecursively(_importedModelRoot, DefaultLayers.CharactersIndex);
                }
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

        private async Task LoadOpenVrModelAsync(string modelName, bool alignToGripPose, int generation)
        {
            try
            {
                RuntimeVrModelComponentData[] components = await OpenVrModelLoader.LoadAsync(modelName, alignToGripPose);
                if (_disposed || generation != _loadGeneration || components.Length == 0)
                {
                    if (!_disposed && generation == _loadGeneration)
                        _isLoading = false;
                    return;
                }

                Engine.EnqueueUpdateThreadTask(() =>
                {
                    if (_disposed || generation != _loadGeneration)
                        return;

                    try
                    {
                        SubMesh[] subMeshes = new SubMesh[components.Length];
                        for (int i = 0; i < components.Length; i++)
                        {
                            RuntimeVrModelComponentData component = components[i];
                            XRMesh mesh = new(component.Vertices, component.TriangleIndices);
                            XRMaterial material = component.Texture is { } texture
                                ? XRMaterial.CreateAuthoredLitPbrTextureMaterial(
                                    new XRTexture2D(texture.Width, texture.Height, texture.RgbaPixels),
                                    roughnessFactor: 0.72f,
                                    metallicFactor: 0.08f)
                                : XRMaterial.CreateAuthoredLitPbrColorMaterial(
                                    new ColorF4(0.24f, 0.25f, 0.28f, 1.0f),
                                    roughness: 0.65f,
                                    metallic: 0.15f);
                            subMeshes[i] = new SubMesh(new SubMeshLOD(material, mesh, 0.0f))
                            {
                                CullingBounds = ComputeVertexBounds(component.Vertices)
                            };
                        }

                        Model model = new(subMeshes);
                        _modelComponent.Model = model;
                        ApplyLayerRecursively(_renderNode, DefaultLayers.CharactersIndex);
                    }
                    catch (Exception ex)
                    {
                        Debug.VRWarning($"Failed to publish OpenVR render model '{modelName}': {ex.Message}");
                    }
                    finally
                    {
                        if (!_disposed && generation == _loadGeneration)
                            _isLoading = false;
                    }
                });
                return;
            }
            catch (Exception ex)
            {
                if (!_disposed && generation == _loadGeneration)
                    Debug.VRWarning($"Failed to load OpenVR render model '{modelName}': {ex.Message}");
            }

            if (!_disposed && generation == _loadGeneration)
                _isLoading = false;
        }

        private static XREngine.Data.Geometry.AABB ComputeVertexBounds(Vertex[] vertices)
        {
            if (vertices.Length == 0)
                return XREngine.Data.Geometry.AABB.FromCenterSize(System.Numerics.Vector3.Zero, new System.Numerics.Vector3(0.25f));

            System.Numerics.Vector3 min = vertices[0].Position;
            System.Numerics.Vector3 max = min;
            for (int i = 1; i < vertices.Length; i++)
            {
                System.Numerics.Vector3 p = vertices[i].Position;
                min = System.Numerics.Vector3.Min(min, p);
                max = System.Numerics.Vector3.Max(max, p);
            }
            return new XREngine.Data.Geometry.AABB(min, max);
        }

        private static void ApplyLayerRecursively(SceneNode root, int layer)
        {
            if (root.IsDestroyed || root.IsDestroyQueued)
                return;

            if (root.Layer != layer)
                root.Layer = layer;

            if (root.GetComponent<ModelComponent>() is { IsDestroyed: false } modelComponent)
            {
                var meshes = modelComponent.Meshes;
                for (int i = 0; i < meshes.Count; ++i)
                    if (meshes[i].RenderInfo.Layer != layer)
                        meshes[i].RenderInfo.Layer = layer;
            }

            if (root.Transform is { } transform)
            {
                foreach (TransformBase? child in transform.Children)
                    if (child?.SceneNode is SceneNode childNode)
                        ApplyLayerRecursively(childNode, layer);
            }
        }
    }
}
