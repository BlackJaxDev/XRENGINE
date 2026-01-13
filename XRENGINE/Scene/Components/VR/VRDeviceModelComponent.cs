using OpenVR.NET.Devices;
using System.Numerics;
using Valve.VR;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Components.VR
{
    public abstract class VRDeviceModelComponent : ModelComponent
    {
        public bool IsLoaded => Model is not null;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (IsLoaded)
                return;

            Engine.VRState.OpenVRApi.DeviceDetected += DeviceDetected;
            VerifyDevices();
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Engine.VRState.OpenVRApi.DeviceDetected -= DeviceDetected;
        }

        private void DeviceDetected(VrDevice device)
        {
            if (!IsLoaded && GetRenderModel(device) is DeviceModel d)
                LoadModelAsync(d);
        }

        protected void VerifyDevices()
        {
            if (IsLoaded)
                return;
            
            foreach (VrDevice device in Engine.VRState.OpenVRApi.TrackedDevices)
            {
                if (GetRenderModel(device) is not DeviceModel d)
                    continue;

                LoadModelAsync(d);
                break;
            }
        }

        public void LoadModelAsync(DeviceModel? d)
        {
            if (d is null)
                return;

            Model m = new();
            Model = m;
            Task.Run(() => LoadDeviceAsync(d, m));
        }

        protected abstract DeviceModel? GetRenderModel(VrDevice? device);

        public static async Task LoadDeviceAsync(DeviceModel? deviceModel, Model model)
        {
            if (deviceModel is null)
                return;

            var comps = deviceModel.Components;
            foreach (var comp in comps)
            {
                var subMesh = await LoadComponentAsync(comp);
                if (subMesh is not null)
                    model.Meshes.Add(subMesh);
            }
        }

        protected static async Task<SubMesh?> LoadComponentAsync(ComponentModel comp)
        {
            if (!comp.ModelName.EndsWith(".obj"))
                return null;

            XRMesh mesh = new();
            XRMaterial mat = new();

            List<Vertex> vertices = [];
            List<ushort> triangleIndices = [];
            List<XRTexture2D> textures = [];

            SubMesh? m = null;
            void OnError(EVRRenderModelError error, ComponentModel.Context context)
            {

            }
            void AddTexture(ComponentModel.Texture texture)
            {
                textures.Add(new XRTexture2D(texture.LoadImage(true)));
            }
            void AddTriangle(short index0, short index1, short index2)
            {
                triangleIndices.Add((ushort)index0);
                triangleIndices.Add((ushort)index2);
                triangleIndices.Add((ushort)index1);
            }
            void AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
            {
                vertices.Add(new Vertex(position, normal, uv));
            }
            void End(ComponentModel.ComponentType type)
            {
                if (vertices.Count == 0 || triangleIndices.Count == 0)
                    return;

                if (triangleIndices.Max() >= vertices.Count)
                {
                    Debug.Out("Invalid triangle index detected in model component.");
                    return;
                }

                mesh = new XRMesh(vertices, triangleIndices);
                if (textures.Count > 0)
                    mat = XRMaterial.CreateLitTextureMaterial(textures[0]);
                else
                    mat = XRMaterial.CreateLitColorMaterial(ColorF4.Magenta);
                m = new(new SubMeshLOD(mat, mesh, 0.0f));
            }
            bool Begin(ComponentModel.ComponentType type)
            {
                return true;
            }
            await comp.LoadAsync(Begin, End, AddVertex, AddTriangle, AddTexture, OnError);
            return m;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Model):
                    if (IsActive)
                    {
                        if (Model is not null)
                            Engine.VRState.OpenVRApi.DeviceDetected -= DeviceDetected;
                        else
                            Engine.VRState.OpenVRApi.DeviceDetected += DeviceDetected;
                    }
                    break;
            }
        }
    }
}
