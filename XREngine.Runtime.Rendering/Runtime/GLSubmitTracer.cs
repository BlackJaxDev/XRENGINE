using System.Diagnostics;
using System.Text;

namespace XREngine.Rendering;

/// <summary>
/// Surgical pre-submit tracer for OpenGL texture storage / upload calls.
///
/// Purpose: capture the exact GL submission (texture name, binding id, mip, dims, format,
/// progressive-finalize flags, storage generation, render-thread flag) immediately before
/// the driver-facing call. When the NVIDIA driver __fastfails (FAST_FAIL_FATAL_APP_EXIT)
/// during a texture submit, AppDomain unwind handlers and Serilog buffered sinks may not
/// run; the only reliable record is a per-line WriteThrough file flush.
///
/// Activation:
/// - Editor: <c>Debug → GL Submit Trace Level</c> preference (0=off, 1=basic, 2=verbose).
/// - Headless/standalone fallback: env var <c>XRE_GL_SUBMIT_TRACE=1</c> (or <c>2</c>).
///
/// When inactive, <see cref="Enabled"/> is false and all callsites must short-circuit before
/// formatting their details string to avoid hot-path allocations.
///
/// Output: <c>Build/Logs/gl-submit-trace.log</c> (truncated each time tracing is enabled).
/// One line per trace call, with <c>[HH:mm:ss.fff][tid][op]</c> prefix. On crash, the LAST
/// line names the in-flight submit.
/// </summary>
public static class GLSubmitTracer
{
    private static readonly object _stateLock = new();
    private static FileStream? _stream;
    private static StreamWriter? _writer;
    private static volatile int _level;

    /// <summary>True when basic submit tracing is enabled (storage allocations, full uploads,
    /// destroys). Cheap volatile read; callers must gate string formatting on this.</summary>
    public static bool Enabled => _level > 0;

    /// <summary>True when verbose tracing is enabled (per-row chunk submits). Implies <see cref="Enabled"/>.</summary>
    public static bool VerboseEnabled => _level >= 2;

    /// <summary>Current trace level. 0=off, 1=basic, 2=verbose.</summary>
    public static int CurrentLevel => _level;

    static GLSubmitTracer()
    {
        // Honour env-var activation for non-editor scenarios (server, headless tools).
        // The editor will overwrite this with a SetLevel(...) call once preferences load.
        try
        {
            string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GlSubmitTrace);
            if (int.TryParse(raw, out int level) && level > 0)
                SetLevel(level);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Sets the active trace level. Opens the per-line WriteThrough log file when
    /// transitioning to an active level, and closes it when transitioning back to 0.
    /// Safe to call from any thread; idempotent for unchanged levels.
    /// </summary>
    public static void SetLevel(int level)
    {
        if (level < 0)
            level = 0;
        else if (level > 2)
            level = 2;

        lock (_stateLock)
        {
            if (level == _level)
                return;

            if (level > 0 && _writer is null)
            {
                if (!TryOpenLog())
                {
                    _level = 0;
                    return;
                }
                try { _writer!.WriteLine($"# XRENGINE GL submit trace level={level} pid={Environment.ProcessId} started={DateTime.Now:O}"); } catch { }
            }
            else if (level == 0 && _writer is not null)
            {
                try { _writer.WriteLine($"# XRENGINE GL submit trace stopped={DateTime.Now:O}"); } catch { }
                CloseLog();
            }
            else if (_writer is not null)
            {
                try { _writer.WriteLine($"# XRENGINE GL submit trace level changed to {level} at {DateTime.Now:O}"); } catch { }
            }

            _level = level;
        }
    }

    /// <summary>Write one pre-submit line. Caller must check <see cref="Enabled"/> first.</summary>
    public static void Trace(string op, string details)
    {
        if (_writer is null)
            return;

        DateTime now = DateTime.Now;
        int tid = Environment.CurrentManagedThreadId;
        lock (_stateLock)
        {
            // Re-check writer under the lock: SetLevel(0) may have closed it concurrently.
            StreamWriter? writer = _writer;
            if (writer is null)
                return;

            try
            {
                writer.Write('[');
                writer.Write(now.ToString("HH:mm:ss.fff"));
                writer.Write("][t");
                writer.Write(tid);
                writer.Write("][");
                writer.Write(op);
                writer.Write("] ");
                writer.WriteLine(details);
            }
            catch
            {
                // Swallow IO errors; tracing must never throw into hot paths.
            }
        }
    }

    /// <summary>
    /// Trace a successful submit completion. Use only when verifying that a specific call
    /// returned to managed code; not required for crash forensics.
    /// </summary>
    [Conditional("DEBUG")]
    public static void TraceEnd(string op)
    {
        if (!Enabled)
            return;
        Trace(op + ".end", string.Empty);
    }

    private static bool TryOpenLog()
    {
        try
        {
            string logsRoot = Path.Combine(Directory.GetCurrentDirectory(), "Build", "Logs");
            Directory.CreateDirectory(logsRoot);
            string path = Path.Combine(logsRoot, "gl-submit-trace.log");

            // WriteThrough bypasses the OS write cache so each line survives a driver
            // fastfail without process-side flushing.
            _stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            return true;
        }
        catch
        {
            CloseLog();
            return false;
        }
    }

    private static void CloseLog()
    {
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;
    }
}
