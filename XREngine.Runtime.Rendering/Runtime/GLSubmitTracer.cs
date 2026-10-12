using System.Diagnostics;

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
    private static IRuntimeOwnedTextLog? _log;
    private static TextWriter? _writer;
    private static volatile int _level;
    private static bool _changingLevel;
    private static int _pendingLevel = -1;
    private static bool _tracing;

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
    /// A host callback can request a new level during a transition or trace line.
    /// The last such request runs after the current operation. Repeated callback
    /// changes stop after four transitions and leave tracing off.
    /// </summary>
    public static void SetLevel(int level)
    {
        if (level < 0)
            level = 0;
        else if (level > 2)
            level = 2;

        lock (_stateLock)
        {
            // A host callback can reenter SetLevel while it opens or closes a log.
            // Apply its last request after the current transition owns its lease.
            if (_changingLevel || _tracing)
            {
                _pendingLevel = level;
                return;
            }

            _changingLevel = true;
            try
            {
                int requestedLevel = level;
                for (int transitions = 0; transitions < 4; transitions++)
                {
                    _pendingLevel = -1;
                    ApplyLevel(requestedLevel);
                    if (_pendingLevel < 0 || _pendingLevel == _level)
                        return;

                    requestedLevel = _pendingLevel;
                }

                // A provider that keeps changing the level cannot keep a lease open.
                CloseLog();
                _level = 0;
            }
            finally
            {
                _pendingLevel = -1;
                _changingLevel = false;
            }
        }
    }

    private static void ApplyLevel(int level)
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
            TextWriter? writer = _writer;
            if (writer is null || _tracing)
                return;

            _tracing = true;
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
            finally
            {
                _tracing = false;
                if (!_changingLevel && _pendingLevel >= 0)
                {
                    int pendingLevel = _pendingLevel;
                    _pendingLevel = -1;
                    SetLevel(pendingLevel);
                }
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
            IRuntimeGLSubmitTraceFileOutput output = RuntimeDiagnosticCaptureFileOutput.Current as IRuntimeGLSubmitTraceFileOutput
                ?? throw new InvalidOperationException("GL submit trace output is unavailable on this host.");
            IRuntimeOwnedTextLog log = output.OpenGLSubmitTraceLog()
                ?? throw new InvalidOperationException("GL submit trace output returned no log lease.");
            _log = log;
            // Own the lease before invoking a provider-controlled property getter.
            _writer = log.Writer ?? throw new InvalidOperationException("GL submit trace output has no writer.");
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
        IRuntimeOwnedTextLog? log = _log;
        _writer = null;
        _log = null;
        try { log?.Dispose(); } catch { }
    }
}
