using OpenVR.NET.Devices;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Components.VR
{
    public abstract class VRDeviceModelComponent : XRComponent
    {
        private IRuntimeVrRenderModelHandle? _renderModelHandle;
        private IRuntimeVrRenderModelProvider? _subscribedRenderModelProvider;
        private DateTime _nextModelReverifyUtc = DateTime.MinValue;

        protected IRuntimeVrRenderModelHandle RenderModelHandle
            => _renderModelHandle ??= RuntimeVrRenderingServices.CreateRenderModelHandle(SceneNode, $"{GetType().Name} Render Model");

        public bool IsLoaded => _renderModelHandle?.IsLoaded ?? false;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (IsLoaded)
                return;

            RuntimeVrStateServices.DeviceDetected += DeviceDetected;
            RuntimeVrStateServices.FrameAdvanced += ReverifyDevicesThrottled;
            _subscribedRenderModelProvider = RuntimeVrRenderingServices.RenderModelProvider;
            _subscribedRenderModelProvider.ModelsChanged += RenderModelsChanged;
            VerifyDevices();
        }

        protected override void OnComponentDeactivated()
        {
            if (_subscribedRenderModelProvider is not null)
            {
                _subscribedRenderModelProvider.ModelsChanged -= RenderModelsChanged;
                _subscribedRenderModelProvider = null;
            }

            RuntimeVrStateServices.FrameAdvanced -= ReverifyDevicesThrottled;
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
            => VerifyDevices();

        private void RenderModelsChanged()
            => VerifyDevices();

        private void ReverifyDevicesThrottled()
        {
            if (IsLoaded)
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextModelReverifyUtc)
                return;

            _nextModelReverifyUtc = now + TimeSpan.FromSeconds(1);
            VerifyDevices();
        }

        protected void VerifyDevices()
        {
            if (IsLoaded)
                return;

            if (TryGetRenderModel(out RuntimeVrRenderModelDescriptor? model))
                LoadModelAsync(model);
        }

        protected void LoadModelAsync(RuntimeVrRenderModelDescriptor? model)
        {
            if (model is null)
                return;

            RenderModelHandle.LoadModelAsync(model);
        }

        protected void ClearLoadedModel()
            => _renderModelHandle?.Clear();

        protected abstract bool TryGetRenderModel(out RuntimeVrRenderModelDescriptor? model);
    }
}
