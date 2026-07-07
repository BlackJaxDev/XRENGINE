using XREngine.Rendering;

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
    }
}
