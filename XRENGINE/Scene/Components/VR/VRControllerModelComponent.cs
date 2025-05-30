using OpenVR.NET.Devices;

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
            switch (propName)
            {
                case nameof(LeftHand):
                    Model?.Destroy();
                    Model = null;
                    break;
                case nameof(Model):
                    VerifyDevices();
                    break;
            }
        }

        protected override DeviceModel? GetRenderModel(VrDevice? device)
        {
            //if (device is null || Engine.VRState.Api.CVR.GetTrackedDeviceClass(device.DeviceIndex) != Valve.VR.ETrackedDeviceClass.Controller)
            //    return null;

            if (LeftHand)
            {
                if (device is Controller c && c.Role == Valve.VR.ETrackedControllerRole.LeftHand)
                    return device.Model;
            }
            else
            {
                if (device is Controller c && c.Role == Valve.VR.ETrackedControllerRole.RightHand)
                    return device.Model;
            }

            return null;
        }
    }
}
