using OscCore;
using XREngine.Components;

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
        public void StartClient(int port)
        {
            Client = new OscClient("127.0.0.1", port);
        }
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Client is null)
                StartClient(Port);

            Client!.Send("EyeTrackingActive", true);
            Client.Send("ExpressionTrackingActive", true);
        }
    }
}
