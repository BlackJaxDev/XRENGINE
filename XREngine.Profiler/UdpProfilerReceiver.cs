using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MemoryPack;
using XREngine.Data.Profiling;

namespace XREngine.Profiler;

/// <summary>
/// Receives profiler telemetry packets on a background thread via UDP.
/// Thread-safe: latest snapshots are stored in volatile references read from the render thread.
/// </summary>
internal sealed class UdpProfilerReceiver : IDisposable, IProfilerDataSource
{
    private readonly int _port;
    private readonly Thread _thread;
    private volatile bool _running;

    // Latest snapshots — written by receiver thread, read by ImGui thread.
    private volatile ProfilerFramePacket? _latestFrame;
    private volatile RenderStatsPacket? _latestRenderStats;
    private volatile ThreadAllocationsPacket? _latestAllocations;
    private volatile BvhMetricsPacket? _latestBvhMetrics;
    private volatile JobSystemStatsPacket? _latestJobStats;
    private volatile MainThreadInvokesPacket? _latestMainThreadInvokes;
    private volatile HeartbeatPacket? _latestHeartbeat;

    public ProfilerFramePacket? LatestFrame => _latestFrame;
    public RenderStatsPacket? LatestRenderStats => _latestRenderStats;
    public ThreadAllocationsPacket? LatestAllocations => _latestAllocations;
    public BvhMetricsPacket? LatestBvhMetrics => _latestBvhMetrics;
    public JobSystemStatsPacket? LatestJobStats => _latestJobStats;
    public MainThreadInvokesPacket? LatestMainThreadInvokes => _latestMainThreadInvokes;
    public HeartbeatPacket? LatestHeartbeat => _latestHeartbeat;

    /// <summary>
    /// True when a heartbeat has been received within the last 3 seconds.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            var hb = _latestHeartbeat;
            return hb is not null && (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds < 3.0;
        }
    }

    /// <summary>Seconds since the last heartbeat, or <see cref="double.MaxValue"/> if none received.</summary>
    public double SecondsSinceLastHeartbeat
    {
        get
        {
            if (_latestHeartbeat is null) return double.MaxValue;
            return (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds;
        }
    }

    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    // Cumulative counters for status display
    private long _packetsReceived;
    private long _bytesReceived;
    private long _errorsCount;

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long ErrorsCount => Interlocked.Read(ref _errorsCount);

    // ──── Multi-instance tracking ────

    private static readonly TimeSpan SourceStaleTimeout = TimeSpan.FromSeconds(10.0);
    private readonly ConcurrentDictionary<int, ProfilerSourceInfo> _knownSources = new();

    /// <summary>
    /// Returns all engine instances that have sent a heartbeat within the last 10 seconds.
    /// </summary>
    public IReadOnlyList<ProfilerSourceInfo> GetKnownSources()
    {
        var now = DateTime.UtcNow;
        var list = new List<ProfilerSourceInfo>();
        foreach (var kvp in _knownSources)
        {
            if (now - kvp.Value.LastSeenUtc < SourceStaleTimeout)
                list.Add(kvp.Value);
            else
                _knownSources.TryRemove(kvp.Key, out _);
        }
        return list;
    }

    /// <summary>True when more than one engine PID has been seen recently.</summary>
    public bool HasMultipleSources => _knownSources.Count > 1;

    public UdpProfilerReceiver(int port)
    {
        _port = port;
        _thread = new Thread(ReceiverLoop)
        {
            Name = "ProfilerUdpReceiver",
            IsBackground = true
        };
    }

    public void Start()
    {
        _running = true;
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        // Thread will exit on next SocketException after we close the client.
    }

    public void Dispose()
    {
        Stop();
        // Give the thread a moment to finish.
        _thread.Join(500);
    }

    private void ReceiverLoop()
    {
        UdpClient? client = null;
        try
        {
            client = new UdpClient(_port);
            client.Client.ReceiveTimeout = 500; // ms — allows periodic _running checks
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfilerReceiver] Failed to bind UDP port {_port}: {ex.Message}");
            return;
        }

        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            byte[] datagram;
            try
            {
                datagram = client.Receive(ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                continue; // Timeout — loop and check _running
            }
            catch (SocketException)
            {
                if (!_running) break;
                Interlocked.Increment(ref _errorsCount);
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            Interlocked.Increment(ref _packetsReceived);
            Interlocked.Add(ref _bytesReceived, datagram.Length);

            if (!ProfilerProtocol.TryReadFrame(datagram, out var msgType, out var payload))
            {
                Interlocked.Increment(ref _errorsCount);
                continue;
            }

            try
            {
                switch (msgType)
                {
                    case ProfilerProtocol.MessageType.ProfilerFrame:
                        _latestFrame = MemoryPackSerializer.Deserialize<ProfilerFramePacket>(payload);
                        break;
                    case ProfilerProtocol.MessageType.RenderStats:
                        _latestRenderStats = MemoryPackSerializer.Deserialize<RenderStatsPacket>(payload);
                        break;
                    case ProfilerProtocol.MessageType.ThreadAllocations:
                        _latestAllocations = MemoryPackSerializer.Deserialize<ThreadAllocationsPacket>(payload);
                        break;
                    case ProfilerProtocol.MessageType.BvhMetrics:
                        _latestBvhMetrics = MemoryPackSerializer.Deserialize<BvhMetricsPacket>(payload);
                        break;
                    case ProfilerProtocol.MessageType.JobSystemStats:
                        _latestJobStats = MemoryPackSerializer.Deserialize<JobSystemStatsPacket>(payload);
                        break;
                    case ProfilerProtocol.MessageType.MainThreadInvokes:
                        _latestMainThreadInvokes = MemoryPackSerializer.Deserialize<MainThreadInvokesPacket>(payload);
                        break;
                    case ProfilerProtocol.MessageType.Heartbeat:
                        var heartbeat = MemoryPackSerializer.Deserialize<HeartbeatPacket>(payload);
                        _latestHeartbeat = heartbeat;
                        _lastHeartbeatUtc = DateTime.UtcNow;
                        if (heartbeat is not null)
                        {
                            _knownSources.AddOrUpdate(
                                heartbeat.ProcessId,
                                _ => new ProfilerSourceInfo
                                {
                                    ProcessName = heartbeat.ProcessName,
                                    ProcessId = heartbeat.ProcessId,
                                    UptimeMs = heartbeat.UptimeMs,
                                    LastSeenUtc = DateTime.UtcNow
                                },
                                (_, existing) =>
                                {
                                    existing.UptimeMs = heartbeat.UptimeMs;
                                    existing.LastSeenUtc = DateTime.UtcNow;
                                    return existing;
                                });
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ProfilerReceiver] Deserialization error for {msgType}: {ex.Message}");
                Interlocked.Increment(ref _errorsCount);
            }
        }

        client.Dispose();
    }
}
