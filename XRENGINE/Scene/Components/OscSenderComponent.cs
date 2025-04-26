using OscCore;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Data.Components
{
    public class OscSenderComponent : XRComponent
    {
        private int _port = 9001;
        public int Port
        {
            get => _port;
            set
            {
                if (_port == value)
                    return;
                SetField(ref _port, value);
                if (Client != null)
                    StartClient(_port);
            }
        }
        public OscClient? Client { get; private set; } = null;

        public void StateMachineVariableChanged(AnimVar variable)
        {
            if (Client == null)
                return;

            string address = variable.ParameterName;
            if (!address.StartsWith('/'))
                address = $"/avatar/parameters/{address}";

            switch (variable)
            {
                case AnimFloat f:
                    Client.Send(address, f.Value);
                    break;
                case AnimInt i:
                    Client.Send(address, i.Value);
                    break;
                case AnimBool b:
                    Client.Send(address, b.Value);
                    break;
            }
        }

        public void StartClient(int port)
        {
            Client = new OscClient("127.0.0.1", port);
        }
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Client is null)
                StartClient(Port);

            Client!.Send(FaceTrackingReceiverComponent.Param_EyeTrackingActive, true);
            Client.Send(FaceTrackingReceiverComponent.Param_LipTrackingActive, true);
            Client.Send(FaceTrackingReceiverComponent.Param_ExpressionTrackingActive, true);
        }
    }
}
