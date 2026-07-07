using XREngine.Rendering;

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
    }
}
