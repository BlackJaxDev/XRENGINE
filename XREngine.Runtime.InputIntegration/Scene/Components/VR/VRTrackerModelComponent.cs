using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Components.VR
{
    public class VRTrackerModelComponent : VRDeviceModelComponent
    {
        private uint? _deviceIndex;
        public uint? DeviceIndex
        {
            get => _deviceIndex;
            set => SetField(ref _deviceIndex, value);
        }

        private string? _openXrTrackerUserPath;
        public string? OpenXrTrackerUserPath
        {
            get => _openXrTrackerUserPath;
            set => SetField(ref _openXrTrackerUserPath, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(DeviceIndex) ||
                propName == nameof(OpenXrTrackerUserPath))
            {
                ClearLoadedModel();
                if (IsActive)
                    VerifyDevices();
            }
        }

        protected override bool TryGetRenderModel(out RuntimeVrRenderModelDescriptor? model)
            => RuntimeVrRenderingServices.RenderModelProvider.TryGetTrackerRenderModel(OpenXrTrackerUserPath, DeviceIndex, out model);

        /// <summary>Builds a tracker puck with a lit status cap and forward nub when the runtime provides no tracker mesh.</summary>
        protected override SceneNode? CreateFallbackModelNode()
        {
            ColorF4 puckColor = new(0.16f, 0.17f, 0.20f, 1.0f);
            ColorF4 statusCapColor = new(0.15f, 0.90f, 1.0f, 1.0f);
            ColorF4 forwardNubColor = new(0.95f, 0.95f, 1.0f, 1.0f);

            ReadOnlySpan<(Vector3 Min, Vector3 Max, ColorF4 Color, float Roughness, float Metallic, float Emission)> parts =
            [
                (new Vector3(-0.034f, -0.016f, -0.034f), new Vector3(0.034f, 0.016f, 0.034f), puckColor, 0.55f, 0.20f, 0.0f),
                (new Vector3(-0.020f, 0.016f, -0.020f), new Vector3(0.020f, 0.024f, 0.020f), statusCapColor, 0.30f, 0.05f, 0.9f),
                (new Vector3(-0.008f, -0.006f, -0.046f), new Vector3(0.008f, 0.006f, -0.034f), forwardNubColor, 0.35f, 0.05f, 0.6f)
            ];
            return CreateFallbackBoxModelNode("VR Tracker Fallback Model", parts);
        }
    }
}
