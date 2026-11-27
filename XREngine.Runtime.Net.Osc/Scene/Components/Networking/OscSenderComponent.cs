using OscCore;
using XREngine.Animation;
using XREngine.Components;

namespace XREngine.Data.Components
{
    /// <summary>
    /// Sends outbound UDP OSC messages and guards state-machine parameter broadcasts against
    /// Animated Animator Parameter and inbound OSC echo loops.
    /// </summary>
    public class OscSenderComponent : XRComponent
    {
        private const string InternalOscmParameterPrefix = "OSCm/";

        private readonly Dictionary<AnimVar, string> _cachedParameterAddresses = new(ReferenceEqualityComparer.Instance);
        private string _cachedAddressPrefix = string.Empty;

        private int _port = 9001;
        /// <summary>Gets or sets the destination UDP port on loopback.</summary>
        public int Port
        {
            get => _port;
            set
            {
                if (_port == value)
                    return;
                SetField(ref _port, value);
                if (Client is not null)
                    StartClient(_port);
            }
        }

        /// <summary>Gets the active <see cref="OscClient"/> while this component is active.</summary>
        public OscClient? Client { get; private set; }

        private string _parameterPrefix = string.Empty;
        /// <summary>Gets or sets the OSC address prefix prepended to relative addresses.</summary>
        public string ParameterPrefix
        {
            get => _parameterPrefix;
            set => SetField(ref _parameterPrefix, value ?? string.Empty);
        }

        /// <summary>Starts or restarts the outbound UDP OSC client on <paramref name="port"/>.</summary>
        public void StartClient(int port)
        {
            StopClient();
            Client = new OscClient("127.0.0.1", port);
        }

        /// <inheritdoc />
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Client is null)
                StartClient(Port);
        }

        /// <inheritdoc />
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopClient();
            _cachedParameterAddresses.Clear();
        }

        /// <summary>
        /// Returns <see langword="false"/> when broadcasting <paramref name="variable"/> would
        /// echo an Animated Animator Parameter write, an inbound OSC update, or an internal
        /// <c>OSCm/*</c> smoothing parameter.
        /// </summary>
        public static bool ShouldBroadcastParameter(
            AnimStateMachine stateMachine,
            AnimVar variable,
            AnimStateMachineOscReceiverComponent? inboundReceiver = null)
        {
            ArgumentNullException.ThrowIfNull(stateMachine);
            ArgumentNullException.ThrowIfNull(variable);

            if (stateMachine.IsApplyingAnimatedParameters)
                return false;
            if (inboundReceiver is not null && inboundReceiver.IsApplyingInboundOsc)
                return false;
            if (variable.ParameterName.StartsWith(InternalOscmParameterPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// Broadcasts a state-machine parameter over UDP only when it is not an AAP-mutated,
        /// internal <c>OSCm/*</c>, or inbound-OSC echo update. Uses a cached address table to
        /// avoid steady-state string allocations.
        /// </summary>
        public bool SendStateMachineParameterIfAllowed(
            AnimStateMachine stateMachine,
            AnimVar variable,
            AnimStateMachineOscReceiverComponent? inboundReceiver = null)
        {
            if (Client is null || !ShouldBroadcastParameter(stateMachine, variable, inboundReceiver))
                return false;

            string address = GetCachedParameterAddress(variable);
            switch (variable)
            {
                case AnimFloat f:
                    Client.Send(address, f.Value);
                    return true;
                case AnimInt i:
                    Client.Send(address, i.Value);
                    return true;
                case AnimBool b:
                    Client.Send(address, b.Value);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Sends a single 32-bit float OSC message.</summary>
        public void Send(string address, float value)
        {
            if (Client is null)
                return;
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client.Send(address, value);
        }

        /// <summary>Sends a single 32-bit integer OSC message.</summary>
        public void Send(string address, int value)
        {
            if (Client is null)
                return;
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client.Send(address, value);
        }

        /// <summary>Sends a single boolean OSC message.</summary>
        public void Send(string address, bool value)
        {
            if (Client is null)
                return;
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client.Send(address, value);
        }

        /// <summary>Sends a single string OSC message.</summary>
        public void Send(string address, string value)
        {
            if (Client is null)
                return;
            if (!address.StartsWith('/'))
                address = $"{ParameterPrefix}{address}";
            Client.Send(address, value);
        }

        private string GetCachedParameterAddress(AnimVar variable)
        {
            if (!string.Equals(_cachedAddressPrefix, _parameterPrefix, StringComparison.Ordinal))
            {
                _cachedParameterAddresses.Clear();
                _cachedAddressPrefix = _parameterPrefix;
            }

            if (_cachedParameterAddresses.TryGetValue(variable, out string? cached))
                return cached;

            string parameterName = variable.ParameterName;
            string fullAddress = parameterName.StartsWith('/')
                ? parameterName
                : $"{_parameterPrefix}{parameterName}";
            _cachedParameterAddresses.Add(variable, fullAddress);
            return fullAddress;
        }

        private void StopClient()
        {
            if (Client is null)
                return;

            Client.Dispose();
            Client = null;
        }
    }
}
