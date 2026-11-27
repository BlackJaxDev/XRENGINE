using System.Net.Sockets;
using OscCore;
using XREngine.Components;

namespace XREngine.Data.Components
{
    /// <summary>
    /// Receives UDP OSC messages on <see cref="Port"/> and dispatches registered address
    /// handlers on the main tick loop.
    /// </summary>
    public class OscReceiverComponent : XRComponent
    {
        private const int MaxFallbackPortAttempts = 16;
        private readonly HashSet<string> _failedAddresses = [];

        private int _port = 9000;
        /// <summary>Gets or sets the requested UDP port bound by the underlying <see cref="OscServer"/>.</summary>
        public int Port
        {
            get => _port;
            set
            {
                if (_port == value)
                    return;
                SetField(ref _port, value);
                if (Server is not null)
                    StartServer(_port);
            }
        }

        private string _parameterPrefix = string.Empty;
        /// <summary>Gets or sets the OSC address prefix prepended to relative addresses.</summary>
        public string ParameterPrefix
        {
            get => _parameterPrefix;
            set => SetField(ref _parameterPrefix, value ?? string.Empty);
        }

        private bool _logUnhandledAddresses;
        /// <summary>
        /// When <see langword="true"/>, logs each unhandled OSC address once through a monitor callback.
        /// Keep <see langword="false"/> in steady-state operation to avoid per-packet monitor allocations.
        /// </summary>
        public bool LogUnhandledAddresses
        {
            get => _logUnhandledAddresses;
            set => SetField(ref _logUnhandledAddresses, value);
        }

        /// <summary>Gets the active <see cref="OscServer"/> while this component is active.</summary>
        public OscServer? Server { get; private set; }

        /// <summary>Gets the UDP port currently bound by <see cref="Server"/>, or <c>0</c> when stopped.</summary>
        public int ActivePort => Server?.Port ?? 0;

        /// <summary>Gets the last socket or startup error encountered while opening the OSC server.</summary>
        public string? LastServerError { get; private set; }

        /// <summary>Gets static OSC address handlers registered when the server starts.</summary>
        public Dictionary<string, Action<OscMessageValues>> ReceiverAddresses { get; } = [];

        /// <summary>
        /// Controls whether <see cref="ReceiverAddresses"/> are registered when the server starts.
        /// </summary>
        protected virtual bool ShouldRegisterStaticReceiverAddresses => true;

        /// <summary>
        /// Allows <see cref="StartServer"/> to select the next free UDP port when the requested port
        /// is already occupied by another process (for example, when OSCQuery advertises the bound port).
        /// </summary>
        protected virtual bool AllowAvailablePortFallbackWhenBusy => false;

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="candidatePort"/> must be skipped
        /// during busy-port fallback (for example, to avoid colliding with a sibling outbound sender port).
        /// </summary>
        protected virtual bool ShouldSkipFallbackPort(int candidatePort) => false;

        /// <summary>Starts or restarts the UDP OSC listener on <paramref name="port"/>.</summary>
        public void StartServer(int port)
        {
            StopServer();
            LastServerError = null;

            OscServer? server = TryCreateAndStartServer(port);
            if (server is null)
                return;

            Server = server;
            RegisterTick(ETickGroup.Normal, ETickOrder.Input, server.Update);
            if (ShouldRegisterStaticReceiverAddresses)
                RegisterStaticMethods(server);
            OnServerStarted(server);
        }

        private OscServer? TryCreateAndStartServer(int requestedPort)
        {
            int maxAttempts = AllowAvailablePortFallbackWhenBusy ? MaxFallbackPortAttempts : 1;
            SocketException? lastSocketError = null;

            for (int offset = 0; offset < maxAttempts; offset++)
            {
                int candidatePort = requestedPort + offset;
                if (candidatePort is < 1 or > 65535)
                    break;
                if (offset > 0 && ShouldSkipFallbackPort(candidatePort))
                    continue;

                try
                {
                    OscServer server = OscServer.GetOrCreate(candidatePort);
                    server.Start();
                    if (offset > 0)
                    {
                        Debug.NetworkingWarning(
                            $"[OscReceiver] Requested UDP port {requestedPort} is already in use; bound OSC receiver to UDP port {candidatePort}.");
                    }
                    return server;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastSocketError = ex;
                    if (!AllowAvailablePortFallbackWhenBusy)
                        break;
                }
                catch (SocketException ex)
                {
                    LastServerError = $"Could not bind OSC UDP port {candidatePort}: {ex.Message}";
                    Debug.NetworkingWarning($"[OscReceiver] {LastServerError}");
                    return null;
                }
            }

            LastServerError = lastSocketError is not null
                ? $"OSC UDP port {requestedPort} is already in use: {lastSocketError.Message}"
                : $"Could not bind OSC UDP port {requestedPort}.";
            Debug.NetworkingWarning($"[OscReceiver] {LastServerError}");
            return null;
        }

        /// <summary>Called immediately after <paramref name="server"/> starts and static methods register.</summary>
        protected virtual void OnServerStarted(OscServer server)
        {
        }

        /// <summary>Called immediately before <paramref name="server"/> stops and disposes.</summary>
        protected virtual void OnServerStopping(OscServer server)
        {
        }

        /// <inheritdoc />
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Server is null)
                StartServer(Port);
        }

        /// <inheritdoc />
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopServer();
        }

        private void StopServer()
        {
            if (Server is not OscServer server)
                return;

            OnServerStopping(server);
            UnregisterTick(ETickGroup.Normal, ETickOrder.Input, server.Update);
            server.Dispose();
            Server = null;
            _failedAddresses.Clear();
        }

        private void RegisterStaticMethods(OscServer server)
        {
            foreach (KeyValuePair<string, Action<OscMessageValues>> address in ReceiverAddresses)
            {
                string addr = address.Key;
                if (!addr.StartsWith('/'))
                    addr = $"{_parameterPrefix}{addr}";
                if (!server.TryAddMethod(addr, address.Value))
                    Debug.NetworkingWarning($"Failed to add OSC method for address {addr}");
            }

            if (LogUnhandledAddresses)
            {
                server.AddMonitorCallback((message, _) =>
                {
                    string address = message.ToString();
                    if (_parameterPrefix.Length > 0 && address.StartsWith(_parameterPrefix, StringComparison.Ordinal))
                        address = address[_parameterPrefix.Length..];
                    if (!ReceiverAddresses.ContainsKey(address) && _failedAddresses.Add(address))
                        Debug.Networking($"Failed to handle message (only logging once): {address}");
                });
            }
        }
    }
}
