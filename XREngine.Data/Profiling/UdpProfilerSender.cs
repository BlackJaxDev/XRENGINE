using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>
/// Fire-and-forget UDP sender that ships profiler data to a remote profiler process.
/// Runs on a dedicated background thread at ~30 Hz with near-zero impact on the calling process.
/// <para>
/// Because this lives in XREngine.Data (which cannot reference the engine), all data
/// collection is done through delegates supplied at startup. The engine wires these up
/// during initialization.
/// </para>
/// </summary>
public static class UdpProfilerSender
{
    // ── state ──────────────────────────────────────────────────────────

    private static Thread? _senderThread;
    private static CancellationTokenSource? _cts;
    private static readonly object _lock = new();
    private static volatile bool _running;

    /// <summary>True when the sender thread is running.</summary>
    public static bool IsRunning => _running;

    // ── delegates the engine must provide ──────────────────────────────

    /// <summary>
    /// Returns a <see cref="ProfilerFramePacket"/> snapshot, or null if none is ready.
    /// </summary>
    public static Func<ProfilerFramePacket?>? CollectProfilerFrame { get; set; }

    /// <summary>
    /// Returns a <see cref="RenderStatsPacket"/> snapshot.
    /// </summary>
    public static Func<RenderStatsPacket?>? CollectRenderStats { get; set; }

    /// <summary>
    /// Returns a <see cref="ThreadAllocationsPacket"/> snapshot.
    /// </summary>
    public static Func<ThreadAllocationsPacket?>? CollectThreadAllocations { get; set; }

    /// <summary>
    /// Returns a <see cref="BvhMetricsPacket"/> snapshot.
    /// </summary>
    public static Func<BvhMetricsPacket?>? CollectBvhMetrics { get; set; }

    /// <summary>
    /// Returns a <see cref="JobSystemStatsPacket"/> snapshot.
    /// </summary>
    public static Func<JobSystemStatsPacket?>? CollectJobSystemStats { get; set; }

    /// <summary>
    /// Returns a <see cref="MainThreadInvokesPacket"/> snapshot (delta or full).
    /// </summary>
    public static Func<MainThreadInvokesPacket?>? CollectMainThreadInvokes { get; set; }

    // ── public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Starts the sender background thread.
    /// Safe to call multiple times; subsequent calls are no-ops if already running.
    /// </summary>
    /// <param name="port">Target UDP port on localhost. Defaults to <see cref="ProfilerProtocol.DefaultPort"/>.</param>
    public static void Start(int port = ProfilerProtocol.DefaultPort)
    {
        lock (_lock)
        {
            if (_senderThread is { IsAlive: true })
                return;

            _cts = new CancellationTokenSource();
            _senderThread = new Thread(() => SenderLoop(port, _cts.Token))
            {
                IsBackground = true,
                Name = "XREngine.ProfilerSender",
                Priority = ThreadPriority.BelowNormal,
            };
            _running = true;
            _senderThread.Start();
        }
    }

    /// <summary>
    /// Stops the sender thread and releases resources.
    /// </summary>
    public static void Stop()
    {
        CancellationTokenSource? cts;
        Thread? thread;
        lock (_lock)
        {
            cts = _cts;
            thread = _senderThread;
            _cts = null;
            _senderThread = null;
        }

        cts?.Cancel();
        if (thread is not null && thread.IsAlive)
            thread.Join(TimeSpan.FromMilliseconds(250));

        _running = false;
        cts?.Dispose();
    }

    /// <summary>
    /// Checks environment variables and starts if <c>XRE_PROFILER_ENABLED=1</c>.
    /// </summary>
    public static bool TryStartFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable(ProfilerProtocol.EnabledEnvVar) != "1")
            return false;

        int port = ProfilerProtocol.DefaultPort;
        if (int.TryParse(Environment.GetEnvironmentVariable(ProfilerProtocol.PortEnvVar), out var p) && p > 0 && p <= 65535)
            port = p;

        Start(port);
        return true;
    }

    // ── sender loop ────────────────────────────────────────────────────

    private static void SenderLoop(int port, CancellationToken token)
    {
        // Pre-allocate a reusable send buffer to avoid per-frame allocations.
        byte[] sendBuffer = new byte[ProfilerProtocol.MaxDatagramSize];
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        using var udp = new UdpClient();
        // Connect so Send() doesn't require endpoint each call. Non-blocking on localhost.
        udp.Connect(endpoint);

        var sw = Stopwatch.StartNew();
        long lastHeartbeatMs = 0;
        long lastSendMs = 0;

        string processName = Process.GetCurrentProcess().ProcessName;
        int processId = Environment.ProcessId;

        while (!token.IsCancellationRequested)
        {
            try
            {
                long nowMs = sw.ElapsedMilliseconds;

                // ── 30 Hz stats ────────────────────────────────
                if (nowMs - lastSendMs >= ProfilerProtocol.SendIntervalMs)
                {
                    lastSendMs = nowMs;

                    TrySend(udp, sendBuffer, ProfilerProtocol.MessageType.ProfilerFrame, CollectProfilerFrame);
                    TrySend(udp, sendBuffer, ProfilerProtocol.MessageType.RenderStats, CollectRenderStats);
                    TrySend(udp, sendBuffer, ProfilerProtocol.MessageType.ThreadAllocations, CollectThreadAllocations);
                    TrySend(udp, sendBuffer, ProfilerProtocol.MessageType.BvhMetrics, CollectBvhMetrics);
                    TrySend(udp, sendBuffer, ProfilerProtocol.MessageType.JobSystemStats, CollectJobSystemStats);
                    TrySend(udp, sendBuffer, ProfilerProtocol.MessageType.MainThreadInvokes, CollectMainThreadInvokes);
                }

                // ── 1 Hz heartbeat ─────────────────────────────
                if (nowMs - lastHeartbeatMs >= ProfilerProtocol.HeartbeatIntervalMs)
                {
                    lastHeartbeatMs = nowMs;
                    var heartbeat = new HeartbeatPacket
                    {
                        ProcessName = processName,
                        ProcessId = processId,
                        UptimeMs = nowMs,
                    };
                    TrySendPacket(udp, sendBuffer, ProfilerProtocol.MessageType.Heartbeat, heartbeat);
                }

                // Sleep to avoid busy-spinning. Target ~30 Hz but don't over-sleep.
                Thread.Sleep(Math.Max(1, ProfilerProtocol.SendIntervalMs / 2));
            }
            catch (SocketException)
            {
                // Receiver not listening — silently drop. Retry next cycle.
            }
            catch (ObjectDisposedException)
            {
                break; // Socket was disposed, exit gracefully.
            }
            catch (Exception)
            {
                // Guard against unexpected serialization issues — don't crash the sender thread.
                Thread.Sleep(100);
            }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void TrySend<T>(UdpClient udp, byte[] buffer, ProfilerProtocol.MessageType type, Func<T?>? collector)
        where T : class
    {
        if (collector is null)
            return;

        T? packet;
        try { packet = collector(); }
        catch { return; } // Swallow collection errors — never block the engine.

        if (packet is null)
            return;

        TrySendPacket(udp, buffer, type, packet);
    }

    private static void TrySendPacket<T>(UdpClient udp, byte[] buffer, ProfilerProtocol.MessageType type, T packet)
    {
        byte[] payload;
        try { payload = MemoryPackSerializer.Serialize(packet); }
        catch { return; }

        if (payload.Length <= ProfilerProtocol.MaxPayloadSize)
        {
            int written = ProfilerProtocol.WriteFrame(buffer, type, payload);
            if (written > 0)
                udp.Send(buffer, written);
        }
        else if (type == ProfilerProtocol.MessageType.ProfilerFrame && packet is ProfilerFramePacket frame)
        {
            // Attempt pruning for oversized profiler frames, then retry.
            PruneProfilerFrame(frame);
            try { payload = MemoryPackSerializer.Serialize(frame); }
            catch { return; }

            int written = ProfilerProtocol.WriteFrame(buffer, type, payload);
            if (written > 0)
                udp.Send(buffer, written);
            // If still too large after pruning, drop this frame silently.
        }
        // Other packet types that are too large are silently dropped.
    }

    /// <summary>
    /// Prunes leaf nodes with ElapsedMs &lt; threshold to reduce packet size.
    /// Modifies the packet in place.
    /// </summary>
    private static void PruneProfilerFrame(ProfilerFramePacket frame, float thresholdMs = 0.01f)
    {
        foreach (var thread in frame.Threads)
        {
            thread.RootNodes = PruneNodes(thread.RootNodes, thresholdMs);
        }
    }

    private static ProfilerNodeData[] PruneNodes(ProfilerNodeData[] nodes, float thresholdMs)
    {
        if (nodes.Length == 0)
            return nodes;

        var kept = new List<ProfilerNodeData>(nodes.Length);
        foreach (var node in nodes)
        {
            // Always keep the node itself if it has meaningful time or children.
            // Prune children recursively first.
            node.Children = PruneNodes(node.Children, thresholdMs);

            // Keep if: has children, or elapsed is above threshold.
            if (node.Children.Length > 0 || node.ElapsedMs >= thresholdMs)
                kept.Add(node);
        }
        return kept.ToArray();
    }
}
