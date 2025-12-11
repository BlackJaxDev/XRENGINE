using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine;

namespace XREngine.Components
{
    /// <summary>
    /// Lightweight LAN discovery helper. Hosts (servers or p2p peers) broadcast a magic tag and the
    /// connection details needed by clients; listeners pick up those broadcasts and can request the
    /// engine to connect using the advertised settings.
    /// </summary>
    public class NetworkDiscoveryComponent : XRComponent
    {
        public const string DefaultMagicTag = "XRENGINE-DISCOVERY";

        private readonly ConcurrentDictionary<string, DiscoveredEndpoint> _discovered = new(StringComparer.OrdinalIgnoreCase);
        private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
        private readonly string _localBeaconId = Guid.NewGuid().ToString("N");
        private CancellationTokenSource? _discoveryCts;
        private Task? _listenTask;
        private Task? _broadcastTask;
        private bool _tickRegistered;

        private int _discoveryPort = 47777;
        private string _multicastAddress = "239.0.0.222";
        private TimeSpan _broadcastInterval = TimeSpan.FromSeconds(2);
        private TimeSpan _discoveryTimeout = TimeSpan.FromSeconds(10);
        private bool _autoAdvertise;
        private bool _autoListen = true;
        private bool _useBroadcastFallback = true;
        private string _magicTag = DefaultMagicTag;
        private GameStartupSettings.ENetworkingType _advertisedRole = GameStartupSettings.ENetworkingType.Server;

        /// <summary>
        /// Called when a new host/peer is discovered.
        /// </summary>
        public event Action<NetworkDiscoveryComponent, DiscoveryAnnouncement>? EndpointDiscovered;
        /// <summary>
        /// Called when an already-known host refreshes its beacon.
        /// </summary>
        public event Action<NetworkDiscoveryComponent, DiscoveryAnnouncement>? EndpointUpdated;
        /// <summary>
        /// Called when a host has not been heard from for <see cref="DiscoveryTimeout"/>.
        /// </summary>
        public event Action<NetworkDiscoveryComponent, DiscoveryAnnouncement>? EndpointExpired;

        /// <summary>
        /// Port used for both listening and broadcasting discovery beacons.
        /// </summary>
        public int DiscoveryPort
        {
            get => _discoveryPort;
            set => SetField(ref _discoveryPort, Math.Max(1, value));
        }

        /// <summary>
        /// Multicast group used for discovery traffic.
        /// </summary>
        public string MulticastAddress
        {
            get => _multicastAddress;
            set => SetField(ref _multicastAddress, value);
        }

        /// <summary>
        /// When true, 255.255.255.255 broadcast is used in addition to multicast for resilience.
        /// </summary>
        public bool UseBroadcastFallback
        {
            get => _useBroadcastFallback;
            set => SetField(ref _useBroadcastFallback, value);
        }

        /// <summary>
        /// Interval between outbound beacon broadcasts when advertising.
        /// </summary>
        public TimeSpan BroadcastInterval
        {
            get => _broadcastInterval;
            set => SetField(ref _broadcastInterval, value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : value);
        }

        /// <summary>
        /// How long a host can remain silent before it is removed.
        /// </summary>
        public TimeSpan DiscoveryTimeout
        {
            get => _discoveryTimeout;
            set => SetField(ref _discoveryTimeout, value <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : value);
        }

        /// <summary>
        /// Automatically start listening when the component activates.
        /// </summary>
        public bool AutoListen
        {
            get => _autoListen;
            set => SetField(ref _autoListen, value);
        }

        /// <summary>
        /// Automatically broadcast discovery beacons when the component activates.
        /// </summary>
        public bool AutoAdvertise
        {
            get => _autoAdvertise;
            set => SetField(ref _autoAdvertise, value);
        }

        /// <summary>
        /// Magic tag used to filter discovery packets.
        /// </summary>
        public string MagicTag
        {
            get => _magicTag;
            set => SetField(ref _magicTag, string.IsNullOrWhiteSpace(value) ? DefaultMagicTag : value);
        }

        /// <summary>
        /// Role this instance advertises (Server -> clients connect; P2PClient -> peers connect as p2p clients).
        /// </summary>
        public GameStartupSettings.ENetworkingType AdvertisedRole
        {
            get => _advertisedRole;
            set => SetField(ref _advertisedRole, value);
        }

        /// <summary>
        /// Optional settings to embed in the broadcast; if null defaults are used.
        /// </summary>
        public GameStartupSettings? AdvertisedSettings { get; set; }

        /// <summary>
        /// Factory used to create a <see cref="GameStartupSettings"/> when connecting to a discovered host.
        /// </summary>
        public Func<GameStartupSettings>? ClientSettingsFactory { get; set; }

        /// <summary>
        /// Returns a snapshot of currently known hosts.
        /// </summary>
        public IReadOnlyCollection<DiscoveryAnnouncement> KnownEndpoints
            => _discovered.Values.Select(static d => d.Announcement).ToArray();

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureCts();
            if (_autoListen)
                StartListeningInternal();
            if (_autoAdvertise)
                StartAdvertisingInternal();
            RegisterTick();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopAllDiscovery();
            UnregisterTick();
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            StopAllDiscovery();
            UnregisterTick();
        }

        /// <summary>
        /// Begin listening for discovery beacons.
        /// </summary>
        public void StartListening()
        {
            EnsureCts();
            StartListeningInternal();
        }

        /// <summary>
        /// Begin broadcasting discovery beacons.
        /// </summary>
        public void StartAdvertising()
        {
            EnsureCts();
            StartAdvertisingInternal();
        }

        /// <summary>
        /// Stops listening and broadcasting.
        /// </summary>
        public void StopAllDiscovery()
        {
            try
            {
                _discoveryCts?.Cancel();
            }
            catch
            {
                // Ignore CTS races.
            }

            _listenTask = null;
            _broadcastTask = null;
            _discoveryCts?.Dispose();
            _discoveryCts = null;
        }

        /// <summary>
        /// Attempts to connect to a discovered host using the advertisement payload.
        /// </summary>
        public Task<Engine.BaseNetworkingManager?> ConnectAsync(DiscoveryAnnouncement announcement, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(announcement);

            var tcs = new TaskCompletionSource<Engine.BaseNetworkingManager?>();

            void ConnectAction()
            {
                try
                {
                    var settings = BuildSettingsForAnnouncement(announcement);
                    var manager = Engine.ConfigureNetworking(settings);
                    tcs.TrySetResult(manager);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            bool enqueued = Engine.InvokeOnMainThread(ConnectAction, true);
            if (!enqueued && cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);

            cancellationToken.Register(static state => ((TaskCompletionSource<Engine.BaseNetworkingManager?>)state!).TrySetCanceled(), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes this application as a server (or p2p host) using the advertised settings and starts networking.
        /// </summary>
        public Task<Engine.BaseNetworkingManager?> StartServerAsync(GameStartupSettings? settings = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<Engine.BaseNetworkingManager?>();

            void StartAction()
            {
                try
                {
                    GameStartupSettings startup = settings ?? AdvertisedSettings ?? new GameStartupSettings();
                    startup.NetworkingType = _advertisedRole;
                    var manager = Engine.ConfigureNetworking(startup);
                    tcs.TrySetResult(manager);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            bool enqueued = Engine.InvokeOnMainThread(StartAction, true);
            if (!enqueued && cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);

            cancellationToken.Register(static state => ((TaskCompletionSource<Engine.BaseNetworkingManager?>)state!).TrySetCanceled(), tcs);
            return tcs.Task;
        }

        private GameStartupSettings BuildSettingsForAnnouncement(DiscoveryAnnouncement announcement)
        {
            GameStartupSettings settings = ClientSettingsFactory?.Invoke() ?? new GameStartupSettings();

            settings.UdpMulticastGroupIP = announcement.MulticastGroup;
            settings.UdpMulticastPort = announcement.MulticastPort;
            settings.ServerIP = announcement.Host;
            settings.UdpServerSendPort = announcement.UdpServerSendPort;
            settings.UdpClientRecievePort = announcement.UdpClientReceivePort;
            settings.NetworkingType = announcement.AdvertisedRole switch
            {
                GameStartupSettings.ENetworkingType.Server => GameStartupSettings.ENetworkingType.Client,
                GameStartupSettings.ENetworkingType.P2PClient => GameStartupSettings.ENetworkingType.P2PClient,
                _ => GameStartupSettings.ENetworkingType.Local,
            };

            return settings;
        }

        private void EnsureCts()
            => _discoveryCts ??= new CancellationTokenSource();

        private void StartListeningInternal()
        {
            if (_listenTask is not null)
                return;

            CancellationToken token = _discoveryCts!.Token;
            _listenTask = Task.Run(() => ListenLoopAsync(token), token);
        }

        private void StartAdvertisingInternal()
        {
            if (_broadcastTask is not null)
                return;

            CancellationToken token = _discoveryCts!.Token;
            _broadcastTask = Task.Run(() => BroadcastLoopAsync(token), token);
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            using UdpClient client = new(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

            if (IPAddress.TryParse(_multicastAddress, out IPAddress? multicast) && multicast.AddressFamily == AddressFamily.InterNetwork)
                client.JoinMulticastGroup(multicast);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await client.ReceiveAsync(token).ConfigureAwait(false);
                    HandleDatagram(result.Buffer, result.RemoteEndPoint);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discovery] Listener error: {ex.Message}");
                }
            }
        }

        private async Task BroadcastLoopAsync(CancellationToken token)
        {
            using UdpClient client = new(AddressFamily.InterNetwork);
            client.EnableBroadcast = true;

            IPEndPoint broadcast = new(IPAddress.Broadcast, _discoveryPort);
            IPEndPoint? multicastEndPoint = null;
            if (IPAddress.TryParse(_multicastAddress, out IPAddress? multicast) && multicast.AddressFamily == AddressFamily.InterNetwork)
                multicastEndPoint = new IPEndPoint(multicast, _discoveryPort);

            while (!token.IsCancellationRequested)
            {
                try
                {
#pragma warning disable IL2026, IL3050
                    DiscoveryAnnouncement payload = BuildLocalAnnouncement();
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _serializerOptions);
#pragma warning restore IL2026, IL3050

                    if (multicastEndPoint is not null)
                        await client.SendAsync(bytes, bytes.Length, multicastEndPoint).ConfigureAwait(false);

                    if (_useBroadcastFallback)
                        await client.SendAsync(bytes, bytes.Length, broadcast).ConfigureAwait(false);

                    await Task.Delay(_broadcastInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discovery] Broadcast error: {ex.Message}");
                }
            }
        }

        private DiscoveryAnnouncement BuildLocalAnnouncement()
        {
            GameStartupSettings settings = AdvertisedSettings ?? new GameStartupSettings();
            string host = ResolveLocalHost(settings.ServerIP);

            return new DiscoveryAnnouncement
            {
                Magic = _magicTag,
                BeaconId = _localBeaconId,
                Host = host,
                MulticastGroup = settings.UdpMulticastGroupIP,
                MulticastPort = settings.UdpMulticastPort,
                UdpServerSendPort = settings.UdpServerSendPort,
                UdpClientReceivePort = settings.UdpClientRecievePort,
                AdvertisedRole = _advertisedRole,
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }

        private static string ResolveLocalHost(string? preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred) && !string.Equals(preferred, "0.0.0.0", StringComparison.Ordinal))
                return preferred;

            foreach (string ip in Engine.BaseNetworkingManager.GetAllLocalIPv4(NetworkInterfaceType.Ethernet))
                if (!string.IsNullOrWhiteSpace(ip))
                    return ip;

            foreach (string ip in Engine.BaseNetworkingManager.GetAllLocalIPv4(NetworkInterfaceType.Wireless80211))
                if (!string.IsNullOrWhiteSpace(ip))
                    return ip;

            return "127.0.0.1";
        }

        private void HandleDatagram(byte[] buffer, IPEndPoint remote)
        {
            if (buffer is null || buffer.Length == 0)
                return;

            DiscoveryAnnouncement? announcement = null;
            try
            {
#pragma warning disable IL2026, IL3050
                announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(buffer, _serializerOptions);
#pragma warning restore IL2026, IL3050
            }
            catch (Exception ex)
            {
                Debug.Out(EOutputVerbosity.Verbose, "[Discovery] Failed to parse beacon from {0}: {1}", remote, ex.Message);
                return;
            }

            if (announcement is null || !string.Equals(announcement.Magic, _magicTag, StringComparison.Ordinal))
                return;

            if (string.Equals(announcement.BeaconId, _localBeaconId, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(announcement.Host))
                announcement.Host = remote.Address.ToString();

            announcement.TimestampUtc = Math.Max(announcement.TimestampUtc, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            string key = announcement.BeaconId ?? announcement.Host;
            DateTimeOffset now = DateTimeOffset.UtcNow;

            bool isNew = !_discovered.ContainsKey(key);
            _discovered[key] = new DiscoveredEndpoint(announcement, now);

            SweepExpired(now);

            RunOnMainThread(() =>
            {
                if (isNew)
                    EndpointDiscovered?.Invoke(this, announcement);
                else
                    EndpointUpdated?.Invoke(this, announcement);
            });
        }

        private void SweepExpired(DateTimeOffset now)
        {
            foreach (KeyValuePair<string, DiscoveredEndpoint> kvp in _discovered)
            {
                if (now - kvp.Value.LastSeenUtc <= _discoveryTimeout)
                    continue;

                if (_discovered.TryRemove(kvp.Key, out DiscoveredEndpoint expired))
                {
                    var announcement = expired.Announcement;
                    RunOnMainThread(() => EndpointExpired?.Invoke(this, announcement));
                }
            }
        }

        private void RegisterTick()
        {
            if (_tickRegistered)
                return;

            Engine.Time.Timer.UpdateFrame += Tick;
            _tickRegistered = true;
        }

        private void UnregisterTick()
        {
            if (!_tickRegistered)
                return;

            Engine.Time.Timer.UpdateFrame -= Tick;
            _tickRegistered = false;
        }

        private void Tick()
            => SweepExpired(DateTimeOffset.UtcNow);

        private static void RunOnMainThread(Action action)
        {
            if (!Engine.InvokeOnMainThread(action))
                action();
        }

        private readonly struct DiscoveredEndpoint(DiscoveryAnnouncement announcement, DateTimeOffset lastSeenUtc)
        {
            public DiscoveryAnnouncement Announcement { get; } = announcement;
            public DateTimeOffset LastSeenUtc { get; } = lastSeenUtc;
        }
    }

    /// <summary>
    /// Payload broadcast during discovery.
    /// </summary>
    public class DiscoveryAnnouncement
    {
        public string Magic { get; set; } = NetworkDiscoveryComponent.DefaultMagicTag;
        public string BeaconId { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public string MulticastGroup { get; set; } = "239.0.0.222";
        public int MulticastPort { get; set; }
            = 5000;
        public int UdpServerSendPort { get; set; }
            = 5000;
        public int UdpClientReceivePort { get; set; }
            = 5001;
        public long TimestampUtc { get; set; }
            = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public GameStartupSettings.ENetworkingType AdvertisedRole { get; set; }
            = GameStartupSettings.ENetworkingType.Server;
    }
}
