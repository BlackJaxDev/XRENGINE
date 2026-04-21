using OpenVR.NET.Devices;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Components.VR
{
    public abstract class VRDeviceModelComponent : XRComponent
    {
        private IRuntimeVrRenderModelHandle? _renderModelHandle;

        protected IRuntimeVrRenderModelHandle RenderModelHandle
            => _renderModelHandle ??= RuntimeVrRenderingServices.CreateRenderModelHandle(SceneNode, $"{GetType().Name} Render Model");

        public bool IsLoaded => _renderModelHandle?.IsLoaded ?? false;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (IsLoaded)
                return;

            RuntimeVrStateServices.DeviceDetected += DeviceDetected;
            VerifyDevices();
        }

        protected override void OnComponentDeactivated()
        {
            RuntimeVrStateServices.DeviceDetected -= DeviceDetected;
            _renderModelHandle?.Clear();
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            _renderModelHandle?.Dispose();
            _renderModelHandle = null;
            base.OnDestroying();
        }

        private void DeviceDetected(VrDevice device)
        {
            if (!IsLoaded && GetRenderModel(device) is DeviceModel model)
                LoadModelAsync(model);
        }

        protected void VerifyDevices()
        {
            if (IsLoaded)
                return;

            foreach (VrDevice device in RuntimeVrStateServices.TrackedDevices)
            {
                if (GetRenderModel(device) is not DeviceModel model)
                    continue;

                LoadModelAsync(model);
                break;
            }
        }

        protected void LoadModelAsync(DeviceModel? model)
        {
            if (model is null)
                return;

            RenderModelHandle.LoadModelAsync(model);
        }

        protected void ClearLoadedModel()
            => _renderModelHandle?.Clear();

        protected abstract DeviceModel? GetRenderModel(VrDevice? device);
    }
}