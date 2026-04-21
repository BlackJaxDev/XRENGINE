using OpenVR.NET.Devices;
using Valve.VR;

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

        protected override DeviceModel? GetRenderModel(VrDevice? device)
        {
            if (LeftHand)
            {
                if (device is Controller controller && controller.Role == ETrackedControllerRole.LeftHand)
                    return device.Model;
            }
            else
            {
                if (device is Controller controller && controller.Role == ETrackedControllerRole.RightHand)
                    return device.Model;
            }

            return null;
        }
    }
}