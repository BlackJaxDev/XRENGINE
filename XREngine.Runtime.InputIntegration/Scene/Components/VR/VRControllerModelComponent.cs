using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Components.VR
{
    public class VRControllerModelComponent : VRDeviceModelComponent
    {
        private bool _leftHand = false;
        public bool LeftHand
        {
            get => _leftHand;
            set => SetField(ref _leftHand, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(LeftHand))
            {
                ClearLoadedModel();
                if (IsActive)
                    VerifyDevices();
            }
        }

        protected override bool TryGetRenderModel(out RuntimeVrRenderModelDescriptor? model)
            => RuntimeVrRenderingServices.RenderModelProvider.TryGetControllerRenderModel(LeftHand, out model);

        /// <summary>Builds a color-coded controller body and tracking bar when the runtime provides no controller mesh.</summary>
        protected override SceneNode? CreateFallbackModelNode()
        {
            ColorF4 gripColor = new(0.16f, 0.17f, 0.20f, 1.0f);
            ColorF4 ringColor = new(0.22f, 0.24f, 0.28f, 1.0f);
            ColorF4 accentColor = LeftHand
                ? new ColorF4(0.15f, 0.85f, 1.0f, 1.0f)
                : new ColorF4(1.0f, 0.62f, 0.15f, 1.0f);

            ReadOnlySpan<(Vector3 Min, Vector3 Max, ColorF4 Color, float Roughness, float Metallic, float Emission)> parts =
            [
                (new Vector3(-0.017f, -0.095f, -0.017f), new Vector3(0.017f, 0.025f, 0.017f), gripColor, 0.65f, 0.15f, 0.0f),
                (new Vector3(-0.030f, 0.012f, -0.050f), new Vector3(0.030f, 0.038f, 0.020f), ringColor, 0.55f, 0.20f, 0.0f),
                (new Vector3(-0.022f, 0.036f, -0.042f), new Vector3(0.022f, 0.048f, 0.012f), accentColor, 0.35f, 0.05f, 0.9f)
            ];
            return CreateFallbackBoxModelNode(
                LeftHand ? "VR Left Controller Fallback Model" : "VR Right Controller Fallback Model",
                parts);
        }
    }
}
