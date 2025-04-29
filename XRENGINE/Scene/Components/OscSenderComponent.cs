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

        /// <summary>
        /// Called when a variable changes in a state machine.
        /// </summary>
        /// <param name="variable"></param>
        public void StateMachineVariableChanged(AnimVar variable)
        {
            if (Client == null)
                return;

            string address = variable.ParameterName;
            if (!address.StartsWith('/'))
                address = $"{_parameterPrefix}{address}";

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
        }
    }
}
