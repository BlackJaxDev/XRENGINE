using OscCore;
using XREngine.Components;

namespace XREngine.Data.Components
{
    public class OscReceiverComponent : XRComponent
    {
        private int _port = 9000;
        public int Port
        {
            get => _port;
            set
            {
                if (_port == value)
                    return;
                SetField(ref _port, value);
                if (Server != null)
                    StartServer(_port);
            }
        }

        public OscServer? Server { get; private set; } = null;
        public Dictionary<string, Action<OscMessageValues>> ReceiverAddresses { get; } = [];

        public void StartServer(int port)
        {
            StopServer();
            Server = OscServer.GetOrCreate(port);
            Server.Start();
            RegisterTick(ETickGroup.Normal, ETickOrder.Input, Server.Update);
        }

        private void StopServer()
        {
            if (Server != null)
            {
                UnregisterTick(ETickGroup.Normal, ETickOrder.Input, Server.Update);
                Server.Dispose();
                Server = null;
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Server is null)
            {
                StartServer(Port);
                RegisterMethods();
            }
        }

        private readonly HashSet<string> _failedAddresses = [];

        private string _parameterPrefix = string.Empty;
        public string ParameterPrefix
        {
            get => _parameterPrefix;
            set => SetField(ref _parameterPrefix, value);
        }

        private void RegisterMethods()
        {
            if (Server is not null)
            {
                Server.Dispose();
                Server = null;
                StartServer(Port);
            }
            foreach (KeyValuePair<string, Action<OscMessageValues>> address in ReceiverAddresses)
            {
                string addr = address.Key;
                if (!addr.StartsWith('/'))
                    addr = $"{_parameterPrefix}{addr}";
                if (!Server!.TryAddMethod(addr, address.Value))
                    Debug.NetworkingWarning($"Failed to add OSC method for address {addr}");
            }
            Server!.AddMonitorCallback((message, values) =>
            {
                string address = message.ToString();
                if (address.StartsWith(_parameterPrefix))
                    address = address[_parameterPrefix.Length..];
                if (!ReceiverAddresses.ContainsKey(address.ToString()) && _failedAddresses.Add(address.ToString()))
                    Debug.Networking($"Failed to handle message (only logging once): {address}");
            });
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopServer();
        }
    }
}
