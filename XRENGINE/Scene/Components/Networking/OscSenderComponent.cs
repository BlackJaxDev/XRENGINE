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

        private string _parameterPrefix = string.Empty;
        public string ParameterPrefix
        {
            get => _parameterPrefix;
            set => SetField(ref _parameterPrefix, value);
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
        }

        public void Send(string address, float value)
        {
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client?.Send(address, value);
        }
        public void Send(string address, int value)
        {
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client?.Send(address, value);
        }
        public void Send(string address, bool value)
        {
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client?.Send(address, value);
        }
    }
}
