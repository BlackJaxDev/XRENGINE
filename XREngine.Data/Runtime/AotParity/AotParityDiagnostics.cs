using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Data.Runtime.AotParity;

/// <summary>
/// Development-mode parity diagnostics. Reflective fallback sites call <see cref="Report"/> when a
/// player-path resolution succeeded only through reflection. The owner type decides whether that
/// is ignored, logged once per type and category, or raised as an exception.
/// </summary>
/// <remarks>
/// Published builds ignore the mode because their reflective fallbacks are already unavailable:
/// every call to <see cref="Report"/> returns immediately there. Development builds read
/// <c>XRE_AOT_PARITY</c> once; hosts and tests may override it with <see cref="ConfigureMode"/>.
/// </remarks>
public static class AotParityDiagnostics
{
    /// <summary>Environment variable that selects the mode: <c>off</c>, <c>warn</c>, or <c>error</c>.</summary>
    public const string EnvironmentVariable = XREngineEnvironmentVariables.AotParity;

    private static readonly object Sync = new();
    private static readonly AsyncLocal<int> PlayerPathDepth = new();
    private static readonly ConcurrentDictionary<(string TypeFullName, EAotParityCategory Category), byte> Reported = new();
    private static readonly List<AotParityDiagnostic> ReportedList = [];
    [ThreadStatic] private static int _synchronousPlayerPathDepth;
    private static EAotParityMode? _mode;
    private static Action<string>? _logSink;

    /// <summary>The effective mode. Read lazily from the environment unless configured explicitly.</summary>
    public static EAotParityMode Mode
    {
        get
        {
            EAotParityMode? mode = _mode;
            if (mode.HasValue)
                return mode.Value;

            lock (Sync)
            {
                _mode ??= ResolveModeFromEnvironment();
                return _mode.Value;
            }
        }
    }

    /// <summary>True while the current call chain or synchronous runtime callback is player-path work.</summary>
    public static bool IsPlayerPath
        => _synchronousPlayerPathDepth > 0 || PlayerPathDepth.Value > 0;

    /// <summary>True when reporting would have an effect: development build, mode is not off, and the player path is active.</summary>
    public static bool IsActive
        => !XRRuntimeEnvironment.IsPublishedBuild && Mode != EAotParityMode.Off && IsPlayerPath;

    /// <summary>Number of distinct type and category pairs reported since the last reset.</summary>
    public static int ReportedCount => Reported.Count;

    /// <summary>Overrides the environment-selected mode. Hosts call this for the unit-test lane and the parity smoke.</summary>
    public static void ConfigureMode(EAotParityMode mode)
    {
        lock (Sync)
            _mode = mode;
    }

    /// <summary>Installs the sink that receives <see cref="EAotParityMode.Warn"/> lines. The default writes to <see cref="Trace"/>.</summary>
    public static void ConfigureLogSink(Action<string>? sink)
        => _logSink = sink;

    /// <summary>Clears reported diagnostics. The mode is left unchanged.</summary>
    public static void ResetReported()
    {
        Reported.Clear();
        lock (ReportedList)
            ReportedList.Clear();
    }

    /// <summary>Clears the mode override and reported diagnostics so the next read consults the environment again.</summary>
    public static void ResetForTestsOrReconfiguration()
    {
        lock (Sync)
            _mode = null;
        ResetReported();
    }

    /// <summary>Copies the reported diagnostics in report order.</summary>
    public static AotParityDiagnostic[] SnapshotReported()
    {
        lock (ReportedList)
            return [.. ReportedList];
    }

    /// <summary>Enters an explicit player-path scope for the current logical call chain.</summary>
    public static AotParityPlayerPathScope EnterPlayerPath(EAotParityPlayerPathKind kind)
    {
        _ = kind;
        if (XRRuntimeEnvironment.IsPublishedBuild || Mode == EAotParityMode.Off)
            return new AotParityPlayerPathScope(entered: false);

        PlayerPathDepth.Value = PlayerPathDepth.Value + 1;
        return new AotParityPlayerPathScope(entered: true);
    }

    /// <summary>Marks a synchronous runtime callback without allocating an asynchronous execution context.</summary>
    public static AotParityPlayerPathScope EnterSynchronousPlayerPath(EAotParityPlayerPathKind kind)
    {
        _ = kind;
        if (XRRuntimeEnvironment.IsPublishedBuild || Mode == EAotParityMode.Off)
            return new AotParityPlayerPathScope(entered: false);
        _synchronousPlayerPathDepth++;
        return new AotParityPlayerPathScope(entered: true, synchronous: true);
    }

    internal static void ExitPlayerPath(bool synchronous)
    {
        if (synchronous)
        {
            if (_synchronousPlayerPathDepth > 0) _synchronousPlayerPathDepth--;
            return;
        }
        int depth = PlayerPathDepth.Value;
        if (depth > 0) PlayerPathDepth.Value = depth - 1;
    }

    /// <summary>
    /// Reports a reflective fallback. Returns without effect in published builds, when the mode is
    /// off, or outside the player path. In <see cref="EAotParityMode.Error"/> mode every attempted
    /// fallback throws; only warning emission and inventory entries are deduplicated.
    /// </summary>
    public static void Report(Type type, EAotParityCategory category, string callSiteOwner, string remediation)
    {
        ArgumentNullException.ThrowIfNull(type);
        Report(type.FullName ?? type.Name, category, callSiteOwner, remediation);
    }

    /// <inheritdoc cref="Report(Type, EAotParityCategory, string, string)"/>
    public static void Report(string typeFullName, EAotParityCategory category, string callSiteOwner, string remediation)
    {
        if (!IsActive)
            return;

        bool first = Reported.TryAdd((typeFullName, category), 0);
        if (!first && Mode != EAotParityMode.Error) return;
        AotParityDiagnostic diagnostic = new(typeFullName, category, callSiteOwner, remediation);
        if (first)
            lock (ReportedList) ReportedList.Add(diagnostic);
        if (Mode == EAotParityMode.Error)
            throw new AotParityViolationException(diagnostic);

        Action<string>? sink = _logSink;
        if (sink is not null)
            sink(diagnostic.Format());
        else
            Trace.TraceWarning(diagnostic.Format());
    }

    private static EAotParityMode ResolveModeFromEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string value = raw.Trim();
            if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value == "0")
                return EAotParityMode.Off;
            if (value.Equals("warn", StringComparison.OrdinalIgnoreCase) || value.Equals("warning", StringComparison.OrdinalIgnoreCase) || value == "1")
                return EAotParityMode.Warn;
            if (value.Equals("error", StringComparison.OrdinalIgnoreCase) || value.Equals("throw", StringComparison.OrdinalIgnoreCase) || value == "2")
                return EAotParityMode.Error;

            Trace.TraceWarning($"[AotParity] Unrecognized {EnvironmentVariable} value '{raw}'. Expected off, warn, or error. Using off.");
            return EAotParityMode.Off;
        }

        // The unit-test lane and headless validation default to error so a missing registration
        // fails the lane instead of waiting for a NativeAOT publish and smoke.
        string? worldMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.WorldMode);
        if (string.Equals(worldMode, "UnitTesting", StringComparison.OrdinalIgnoreCase))
            return EAotParityMode.Error;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.HeadlessTest)))
            return EAotParityMode.Error;

        return EAotParityMode.Off;
    }
}
