using OpenVR.NET.Devices;
using XREngine.Input;

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

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(DeviceIndex))
            {
                ClearLoadedModel();
                if (IsActive)
                    VerifyDevices();
            }
        }

        protected override DeviceModel? GetRenderModel(VrDevice? device)
        {
            if (DeviceIndex is null || device is null || device.DeviceIndex != DeviceIndex.Value)
                return null;

            return RuntimeVrStateServices.IsGenericTracker(device.DeviceIndex)
                ? device.Model
                : null;
        }
    }
}