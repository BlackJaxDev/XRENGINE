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

        private void RegisterMethods()
        {
            foreach (var address in ReceiverAddresses)
                if (!Server!.TryAddMethod(address.Key, address.Value))
                    Debug.LogWarning($"Failed to add OSC method for address {address.Key}");
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopServer();
        }
    }
}
