using System.Numerics;
using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

internal sealed class EngineRuntimeVrRenderingServices : IRuntimeVrRenderingServices
{
    public IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
        => new EngineRuntimeVrEyeCamera(transform, leftEye, nearPlane, farPlane);

    public void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera, IRuntimeWorldContext? world, SceneNode? hmdNode)
    {
        XRCamera? leftCamera = (leftEyeCamera as EngineRuntimeVrEyeCamera)?.Camera;
        XRCamera? rightCamera = (rightEyeCamera as EngineRuntimeVrEyeCamera)?.Camera;
        XRWorldInstance? xrWorld = world as XRWorldInstance;
        Engine.VRState.ViewInformation = (leftCamera, rightCamera, xrWorld, hmdNode);
    }

    public IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null)
        => new EngineRuntimeVrRenderModelHandle(node, childName);

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

    private sealed class EngineRuntimeVrRenderModelHandle : IRuntimeVrRenderModelHandle
    {
        private readonly SceneNode _renderNode;
        private readonly ModelComponent _modelComponent;
        private bool _disposed;

        public EngineRuntimeVrRenderModelHandle(SceneNode node, string? childName)
        {
            _renderNode = node.NewChild(childName ?? "VR Render Model");
            _modelComponent = _renderNode.AddComponent<ModelComponent>()!;
        }

        public bool IsLoaded => !_disposed && _modelComponent.Model is not null;

        public void Clear()
        {
            if (_disposed)
                return;

            if (_modelComponent.Model is Model model)
            {
                _modelComponent.Model = null;
                model.Destroy();
            }
        }

        public void LoadModelAsync(object? renderModel)
        {
            if (_disposed || renderModel is not DeviceModel deviceModel)
                return;

            Clear();

            Model model = new();
            _modelComponent.Model = model;
            Task.Run(() => LoadDeviceAsync(deviceModel, model));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Clear();

            try
            {
                _renderNode.Destroy();
            }
            catch
            {
            }
        }

        private static async Task LoadDeviceAsync(DeviceModel deviceModel, Model model)
        {
            foreach (ComponentModel comp in deviceModel.Components)
            {
                SubMesh? subMesh = await LoadComponentAsync(comp);
                if (subMesh is not null)
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