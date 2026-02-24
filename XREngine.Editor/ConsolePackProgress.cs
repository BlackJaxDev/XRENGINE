using System.Diagnostics;
using XREngine.Core.Files;

namespace XREngine.Editor;

/// <summary>
/// Renders <see cref="AssetPacker"/> progress with in-place console updates.
/// Permanently-printed "Staged" lines scroll upward while a live dashboard below
/// shows actively-compressing large files with unicode progress bars that update
/// in-place instead of spamming repeated lines.
/// <para/>
/// Falls back to simple throttled <see cref="Console.WriteLine"/> output when the
/// console doesn't support cursor manipulation (e.g. redirected / piped output).
/// </summary>
internal sealed class ConsolePackProgress : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FileProgress> _activeFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fallbackThrottle = new(StringComparer.Ordinal);
    private readonly Stopwatch _stopwatch = new();
    private int _liveLineCount;
    private int _overallPercent;
    private long _lastRedrawMs;
    private bool _interactive;
    private bool _disposed;
    private long _totalSourceBytes;
    private long _grandTotalBytes;
    private long _lastRateSampleBytes;
    private double _lastRateSampleSeconds;
    private double _smoothedBytesPerSecond;
    private double? _lastEtaSeconds;

    /// <summary>Minimum interval between live-area redraws (~15 fps).</summary>
    private const int MinRedrawIntervalMs = 66;

    /// <summary>Maximum number of active-file lines in the live area.</summary>
    private const int MaxLiveLines = 10;

    /// <summary>Width of the unicode progress bar in characters.</summary>
    private const int BarWidth = 20;

    /// <summary>EWMA smoothing factor for instantaneous throughput updates.</summary>
    private const double ThroughputAlpha = 0.2;

    private struct FileProgress
    {
        public long Processed;
        public long Total;
        public int Order;
    }

    public ConsolePackProgress()
    {
        try
        {
            _interactive = !Console.IsOutputRedirected;
            if (_interactive)
                Console.CursorVisible = false;
        }
        catch
        {
            _interactive = false;
        }
    }

    /// <summary>Progress callback suitable for passing to <see cref="AssetPacker.Pack"/>.</summary>
    public void HandleProgress(AssetPacker.PackProgress progress)
    {
        if (progress.TotalFiles <= 0)
            return;

        lock (_lock)
        {
            if (!_stopwatch.IsRunning)
                _stopwatch.Start();

            _totalSourceBytes = progress.TotalSourceBytes;
            _grandTotalBytes = progress.GrandTotalBytes;

            long effectiveBytes = ComputeEffectiveBytes();
            UpdateThroughputEstimate(effectiveBytes);

            _overallPercent = _grandTotalBytes > 0
                ? (int)(effectiveBytes * 100L / _grandTotalBytes)
                : (int)(progress.ProcessedFiles * 100L / progress.TotalFiles);

            switch (progress.Phase)
            {
                case AssetPacker.PackPhase.Compressing:
                    OnCompressing(progress);
                    break;
                case AssetPacker.PackPhase.CompressingLargeFile:
                    OnLargeFile(progress);
                    break;
                case AssetPacker.PackPhase.Staged:
                    OnStaged(progress);
                    break;
            }
        }
    }

    /// <summary>Restore cursor visibility and clear the live area.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (_interactive)
            {
                ClearLiveArea();
                try { Console.CursorVisible = true; } catch { /* best-effort */ }
            }

            _activeFiles.Clear();
            _fallbackThrottle.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Finish();
    }

    // ──────────────────────── Phase handlers ────────────────────────

    private void OnCompressing(AssetPacker.PackProgress p)
    {
        // Only track large files in the live dashboard.
        if (p.SourceBytes < AssetPacker.LargeFileThreshold)
            return;

        if (!_activeFiles.ContainsKey(p.RelativePath))
        {
            _activeFiles[p.RelativePath] = new FileProgress
            {
                Processed = 0,
                Total = p.SourceBytes,
                Order = _activeFiles.Count,
            };
        }

        if (_interactive)
            ThrottledRedraw();
    }

    private void OnLargeFile(AssetPacker.PackProgress p)
    {
        int order = _activeFiles.TryGetValue(p.RelativePath, out var existing)
            ? existing.Order
            : _activeFiles.Count;

        _activeFiles[p.RelativePath] = new FileProgress
        {
            Processed = p.CompressedBytes,
            Total = p.SourceBytes,
            Order = order,
        };

        if (_interactive)
            ThrottledRedraw();
        else
            FallbackLargeFile(p);
    }

    private void OnStaged(AssetPacker.PackProgress p)
    {
        _activeFiles.Remove(p.RelativePath);
        _fallbackThrottle.Remove(p.RelativePath);

        long totalInMb = p.TotalSourceBytes / (1024 * 1024);
        long totalOutMb = p.TotalCompressedBytes / (1024 * 1024);
        string timing = FormatTiming();
        string line = $"[pack {_overallPercent,3}%] {timing} {p.ProcessedFiles}/{p.TotalFiles} ({totalInMb} MB -> {totalOutMb} MB): {p.RelativePath}";

        if (_interactive)
        {
            ClearLiveArea();
            Console.WriteLine(line);
            _liveLineCount = 0;

            if (_activeFiles.Count > 0)
                RedrawLiveArea();
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    // ──────────────────────── Console rendering ────────────────────────

    private void ThrottledRedraw()
    {
        long now = Environment.TickCount64;
        if (now - _lastRedrawMs < MinRedrawIntervalMs)
            return;
        _lastRedrawMs = now;
        RedrawLiveArea();
    }

    private void ClearLiveArea()
    {
        if (_liveLineCount <= 0)
            return;

        try
        {
            int width = GetWidth();
            int top = Math.Max(0, Console.CursorTop - _liveLineCount);
            Console.SetCursorPosition(0, top);

            string blank = new(' ', width);
            for (int i = 0; i < _liveLineCount; i++)
            {
                Console.Write(blank);
                Console.WriteLine();
            }

            Console.SetCursorPosition(0, top);
        }
        catch
        {
            _interactive = false;
        }

        _liveLineCount = 0;
    }

    private void RedrawLiveArea()
    {
        try
        {
            int width = GetWidth();

            // Position cursor at start of the live area.
            if (_liveLineCount > 0)
            {
                int top = Math.Max(0, Console.CursorTop - _liveLineCount);
                Console.SetCursorPosition(0, top);
            }

            var entries = _activeFiles
                .OrderBy(static kv => kv.Value.Order)
                .Take(MaxLiveLines)
                .ToList();

            bool hasMore = _activeFiles.Count > MaxLiveLines;
            int newLineCount = entries.Count + (hasMore ? 1 : 0);

            foreach (var (path, fp) in entries)
                WriteConsoleLine(FormatProgressLine(path, fp), width);

            if (hasMore)
                WriteConsoleLine($"          ... and {_activeFiles.Count - MaxLiveLines} more", width);

            // Clear leftover lines from a previous (larger) render.
            for (int i = newLineCount; i < _liveLineCount; i++)
                WriteConsoleLine("", width);

            // Reposition cursor at end of actual live content.
            if (newLineCount < _liveLineCount)
            {
                int excess = _liveLineCount - newLineCount;
                Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - excess));
            }

            _liveLineCount = newLineCount;
        }
        catch
        {
            _interactive = false;
        }
    }

    private string FormatProgressLine(string path, FileProgress fp)
    {
        int pct = fp.Total > 0 ? (int)(fp.Processed * 100L / fp.Total) : 0;
        long procMb = fp.Processed / (1024 * 1024);
        long totMb = fp.Total / (1024 * 1024);
        int filled = BarWidth * pct / 100;
        string bar = new string('\u2588', filled) + new string('\u2591', BarWidth - filled);
        string timing = FormatTiming();
        return $"[pack {_overallPercent,3}%] {timing} {bar} {path}: {procMb}/{totMb} MB ({pct}%)";
    }

    private static void WriteConsoleLine(string text, int width)
    {
        Console.Write(text.Length >= width ? text[..width] : text.PadRight(width));
        Console.WriteLine();
    }

    // ──────────────────────── Fallback (non-interactive) ────────────────────────

    private void FallbackLargeFile(AssetPacker.PackProgress p)
    {
        int pct = p.SourceBytes > 0 ? (int)(p.CompressedBytes * 100L / p.SourceBytes) : 0;
        int bucket = pct / 10 * 10;

        if (_fallbackThrottle.TryGetValue(p.RelativePath, out int last) && bucket == last)
            return;

        _fallbackThrottle[p.RelativePath] = bucket;

        long processed = p.CompressedBytes / (1024 * 1024);
        long total = p.SourceBytes / (1024 * 1024);
        string timing = FormatTiming();
        Console.WriteLine($"[pack {_overallPercent,3}%] {timing}   {p.RelativePath}: {processed}/{total} MB ({pct}%)");
    }

    /// <summary>
    /// Returns staged bytes plus fractional progress of all in-flight files,
    /// so bytes advance smoothly during compression rather than jumping on staging.
    /// </summary>
    private long ComputeEffectiveBytes()
    {
        long inFlight = 0;
        foreach (var kv in _activeFiles)
        {
            var fp = kv.Value;
            if (fp.Total > 0 && fp.Processed > 0)
                inFlight += (long)((double)fp.Processed / fp.Total * fp.Total);
        }
        return _totalSourceBytes + inFlight;
    }

    private string FormatTiming()
    {
        TimeSpan elapsed = _stopwatch.Elapsed;
        string elapsedStr = FormatTimeSpan(elapsed);

        long effectiveBytes = ComputeEffectiveBytes();
        if (_grandTotalBytes > 0 && effectiveBytes > 0 && elapsed.TotalSeconds >= 1.0)
        {
            long remainingBytes = Math.Max(0, _grandTotalBytes - effectiveBytes);
            double globalRate = effectiveBytes / elapsed.TotalSeconds;
            double etaGlobal = globalRate > 0.0 ? remainingBytes / globalRate : double.PositiveInfinity;

            double etaSmoothed = _smoothedBytesPerSecond > 0.0
                ? remainingBytes / _smoothedBytesPerSecond
                : etaGlobal;

            double fraction = (double)effectiveBytes / _grandTotalBytes;
            double smoothedWeight = Math.Clamp((fraction - 0.08) / 0.42, 0.0, 1.0);
            double etaSeconds = etaGlobal * (1.0 - smoothedWeight) + etaSmoothed * smoothedWeight;

            if (_lastEtaSeconds.HasValue)
            {
                double maxStep = Math.Max(8.0, _lastEtaSeconds.Value * 0.2);
                double minEta = Math.Max(0.0, _lastEtaSeconds.Value - maxStep);
                double maxEta = _lastEtaSeconds.Value + maxStep;
                etaSeconds = Math.Clamp(etaSeconds, minEta, maxEta);
            }

            _lastEtaSeconds = etaSeconds;
            TimeSpan remaining = TimeSpan.FromSeconds(Math.Max(0.0, etaSeconds));
            return $"[{elapsedStr} / ETA {FormatTimeSpan(remaining)}]";
        }

        return $"[{elapsedStr}]";
    }

    private void UpdateThroughputEstimate(long effectiveBytes)
    {
        if (!_stopwatch.IsRunning)
            return;

        double nowSeconds = _stopwatch.Elapsed.TotalSeconds;
        if (nowSeconds <= _lastRateSampleSeconds)
            return;

        long deltaBytes = effectiveBytes - _lastRateSampleBytes;
        double deltaSeconds = nowSeconds - _lastRateSampleSeconds;
        if (deltaBytes > 0 && deltaSeconds >= 0.2)
        {
            double instantBps = deltaBytes / deltaSeconds;
            _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0.0
                ? instantBps
                : (_smoothedBytesPerSecond * (1.0 - ThroughputAlpha)) + (instantBps * ThroughputAlpha);
        }

        _lastRateSampleBytes = effectiveBytes;
        _lastRateSampleSeconds = nowSeconds;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1.0)
            return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m{ts.Seconds:D2}s";
        if (ts.TotalMinutes >= 1.0)
            return $"{ts.Minutes}m{ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    private static int GetWidth()
    {
        try { return Math.Max(Console.WindowWidth - 1, 60); }
        catch { return 120; }
    }
}
