using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Components
{
    /// <summary>
    /// Lightweight TCP server component that accepts multiple clients and raises main-thread events for incoming data.
    /// </summary>
    public class TcpServerComponent : XRComponent
    {
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
        private CancellationTokenSource? _listenerCts;
        private Task? _acceptTask;
        private TcpListener? _listener;
        private bool _isRunning;

        private string _listenAddress = "0.0.0.0";
        private int _listenPort = 7777;
        private bool _autoStartOnActivate = true;
        private bool _dispatchTextMessages = true;
        private Encoding _textEncoding = Encoding.UTF8;
        private int _receiveBufferSize = 8 * 1024;
        private bool _enableNagle;

        public event Action<TcpServerComponent>? Started;
        public event Action<TcpServerComponent>? Stopped;
        public event Action<TcpServerComponent, TcpServerClient>? ClientConnected;
        public event Action<TcpServerComponent, TcpServerClient, Exception?>? ClientDisconnected;
        public event Action<TcpServerComponent, TcpServerClient, ReadOnlyMemory<byte>>? ClientDataReceived;
        public event Action<TcpServerComponent, TcpServerClient, string>? ClientTextReceived;
        public event Action<TcpServerComponent, Exception>? ServerError;

        /// <summary>
        /// Address the server listens on (defaults to any IPv4 address).
        /// </summary>
        public string ListenAddress
        {
            get => _listenAddress;
            set => SetField(ref _listenAddress, value);
        }

        /// <summary>
        /// TCP port the server binds to.
        /// </summary>
        public int ListenPort
        {
            get => _listenPort;
            set => SetField(ref _listenPort, Math.Max(0, value));
        }

        /// <summary>
        /// Automatically starts listening once the component activates.
        /// </summary>
        public bool AutoStartOnActivate
        {
            get => _autoStartOnActivate;
            set => SetField(ref _autoStartOnActivate, value);
        }

        /// <summary>
        /// Converts inbound payloads to text and raises <see cref="ClientTextReceived"/> when true.
        /// </summary>
        public bool DispatchTextMessages
        {
            get => _dispatchTextMessages;
            set => SetField(ref _dispatchTextMessages, value);
        }

        /// <summary>
        /// Encoding used for text conversions.
        /// </summary>
        public Encoding TextEncoding
        {
            get => _textEncoding;
            set => SetField(ref _textEncoding, value ?? Encoding.UTF8);
        }

        /// <summary>
        /// Buffer size used while streaming incoming data per client.
        /// </summary>
        public int ReceiveBufferSize
        {
            get => _receiveBufferSize;
            set => SetField(ref _receiveBufferSize, Math.Max(1024, value));
        }

        /// <summary>
        /// Enables Nagle's algorithm on accepted sockets when true.
        /// </summary>
        public bool EnableNagle
        {
            get => _enableNagle;
            set => SetField(ref _enableNagle, value);
        }

        /// <summary>
        /// True when the listener is currently running.
        /// </summary>
        public bool IsListening => _listener is not null;

        public Task StartAsync(CancellationToken cancellationToken = default)
            => RunStartAsync(cancellationToken);

        public Task StopAsync()
            => RunStopAsync();

        /// <summary>
        /// Sends bytes to the specified client.
        /// </summary>
        public async Task SendAsync(Guid clientId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (!_clients.TryGetValue(clientId, out ClientState? state))
                throw new InvalidOperationException("Client is no longer connected.");

            await state.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await state.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await state.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.SendLock.Release();
            }
        }

        /// <summary>
        /// Broadcasts a payload to all active clients.
        /// </summary>
        public async Task BroadcastAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Guid[] snapshot = _clients.Keys.ToArray();
            foreach (Guid clientId in snapshot)
            {
                if (!_clients.ContainsKey(clientId))
                    continue;

                try
                {
                    await SendAsync(clientId, payload, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore per-client send failures triggered by disconnects.
                }
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (_autoStartOnActivate)
                _ = RunStartAsync(CancellationToken.None);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            _ = RunStopAsync();
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _ = RunStopAsync();
        }

        private async Task RunStartAsync(CancellationToken cancellationToken)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_listener is not null)
                    return;

                if (_listenPort <= 0)
                    throw new InvalidOperationException("ListenPort must be greater than zero.");

                IPAddress address = ResolveListenAddress();
                var listener = new TcpListener(address, _listenPort);
                listener.Start();

                _listener = listener;
                _listenerCts = new CancellationTokenSource();
                _acceptTask = AcceptLoopAsync(listener, _listenerCts.Token);
                _isRunning = true;
                DispatchStarted();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task RunStopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_listener is null)
                    return;

                CancelAcceptLoop();
                _listener.Stop();
                _listener = null;
            }
            finally
            {
                _lifecycleLock.Release();
            }

            await WaitForAcceptLoopAsync().ConfigureAwait(false);
            await StopAllClientsAsync().ConfigureAwait(false);

            if (_isRunning)
            {
                _isRunning = false;
                DispatchStopped();
            }
        }

        private void CancelAcceptLoop()
        {
            try
            {
                _listenerCts?.Cancel();
            }
            catch
            {
                // Ignore CTS disposal races.
            }

            _listenerCts?.Dispose();
            _listenerCts = null;
        }

        private async Task WaitForAcceptLoopAsync()
        {
            if (_acceptTask is null)
                return;

            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down.
            }
            finally
            {
                _acceptTask = null;
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    client.NoDelay = !_enableNagle;
                    var descriptor = RegisterClient(client, token);
                    DispatchClientConnected(descriptor);
                }
            }
            catch (OperationCanceledException)
            {
                // Listener shut down.
            }
            catch (Exception ex)
            {
                DispatchServerError(ex);
            }
        }

        private TcpServerClient RegisterClient(TcpClient client, CancellationToken serverToken)
        {
            var descriptor = new TcpServerClient(Guid.NewGuid(), (IPEndPoint?)client.Client.RemoteEndPoint);
            NetworkStream stream = client.GetStream();
            var state = new ClientState(descriptor.Id, client, stream);
            _clients[descriptor.Id] = state;
            state.ReceiveTask = ReceiveClientLoopAsync(state, descriptor, serverToken);
            return descriptor;
        }

        private async Task ReceiveClientLoopAsync(ClientState state, TcpServerClient descriptor, CancellationToken serverToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);
            Exception? disconnectReason = null;
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken, state.ReceiveCts.Token);
            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    int read = await state.Stream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    byte[] payload = new byte[read];
                    Buffer.BlockCopy(buffer, 0, payload, 0, read);
                    DispatchClientData(descriptor, payload);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                disconnectReason = ex;
                DispatchServerError(ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                HandleClientDisconnected(state, descriptor, disconnectReason);
            }
        }

        private async Task StopAllClientsAsync()
        {
            ClientState[] snapshot = _clients.Values.ToArray();
            foreach (ClientState state in snapshot)
            {
                try
                {
                    state.ReceiveCts.Cancel();
                }
                catch
                {
                    // Ignore cancellation races.
                }
            }

            foreach (ClientState state in snapshot)
            {
                if (state.ReceiveTask is null)
                    continue;

                try
                {
                    await state.ReceiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down.
                }
                catch (Exception ex)
                {
                    DispatchServerError(ex);
                }
            }

        }

        private void HandleClientDisconnected(ClientState state, TcpServerClient descriptor, Exception? reason)
        {
            if (_clients.TryRemove(state.Id, out _))
            {
                DisposeClientState(state);
                DispatchClientDisconnected(descriptor, reason);
            }
            else
            {
                DisposeClientState(state);
            }
        }

        private static void DisposeClientState(ClientState state)
        {
            try
            {
                state.Client.Dispose();
            }
            catch
            {
                // Ignore shutdown races.
            }

            state.ReceiveCts.Dispose();
            state.SendLock.Dispose();
        }

        private IPAddress ResolveListenAddress()
        {
            if (IPAddress.TryParse(_listenAddress, out IPAddress? parsed))
                return parsed;

            return _listenAddress.Contains(':') ? IPAddress.IPv6Any : IPAddress.Any;
        }

        private void DispatchStarted()
            => RunOnMainThread(() => Started?.Invoke(this), "TcpServerComponent.Started");

        private void DispatchStopped()
            => RunOnMainThread(() => Stopped?.Invoke(this), "TcpServerComponent.Stopped");

        private void DispatchClientConnected(TcpServerClient client)
            => RunOnMainThread(() => ClientConnected?.Invoke(this, client), "TcpServerComponent.ClientConnected");

        private void DispatchClientDisconnected(TcpServerClient client, Exception? reason)
            => RunOnMainThread(() => ClientDisconnected?.Invoke(this, client, reason), "TcpServerComponent.ClientDisconnected");

        private void DispatchClientData(TcpServerClient client, ReadOnlyMemory<byte> payload)
        {
            RunOnMainThread(() => ClientDataReceived?.Invoke(this, client, payload), "TcpServerComponent.ClientDataReceived");
            if (_dispatchTextMessages)
            {
                string text = _textEncoding.GetString(payload.Span);
                RunOnMainThread(() => ClientTextReceived?.Invoke(this, client, text), "TcpServerComponent.ClientTextReceived");
            }
        }

        private void DispatchServerError(Exception ex)
            => RunOnMainThread(() => ServerError?.Invoke(this, ex), "TcpServerComponent.ServerError");

        private static void RunOnMainThread(Action action, string reason)
        {
            if (!Engine.InvokeOnMainThread(action, reason))
                action();
        }

        private sealed class ClientState
        {
            public ClientState(Guid id, TcpClient client, NetworkStream stream)
            {
                Id = id;
                Client = client;
                Stream = stream;
                SendLock = new SemaphoreSlim(1, 1);
                ReceiveCts = new CancellationTokenSource();
            }

            public Guid Id { get; }
            public TcpClient Client { get; }
            public NetworkStream Stream { get; }
            public SemaphoreSlim SendLock { get; }
            public CancellationTokenSource ReceiveCts { get; }
            public Task? ReceiveTask { get; set; }
        }
    }

    /// <summary>
    /// Client descriptor exposed by <see cref="TcpServerComponent"/> events.
    /// </summary>
    public readonly struct TcpServerClient
    {
        internal TcpServerClient(Guid id, IPEndPoint? remoteEndPoint)
        {
            Id = id;
            RemoteEndPoint = remoteEndPoint;
        }

        public Guid Id { get; }
        public IPEndPoint? RemoteEndPoint { get; }
    }
}
