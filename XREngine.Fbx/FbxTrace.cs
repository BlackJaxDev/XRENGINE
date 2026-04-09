using System.Diagnostics;

namespace XREngine.Fbx;

public enum FbxLogVerbosity
{
    Off = 0,
    Errors = 1,
    Warnings = 2,
    Info = 3,
    Verbose = 4,
}

public static class FbxTrace
{
    public static Action<string>? LogSink { get; set; }

    public static FbxLogVerbosity Verbosity { get; set; } = ReadVerbosityFromEnvironment();

    public static void RefreshFromEnvironment()
        => Verbosity = ReadVerbosityFromEnvironment();

    public static bool IsEnabled(FbxLogVerbosity verbosity)
        => verbosity != FbxLogVerbosity.Off && Verbosity >= verbosity;

    public static void Error(string component, string message)
        => Write(FbxLogVerbosity.Errors, component, message);

    public static void Warning(string component, string message)
        => Write(FbxLogVerbosity.Warnings, component, message);

    public static void Info(string component, string message)
        => Write(FbxLogVerbosity.Info, component, message);

    public static void Verbose(string component, string message)
        => Write(FbxLogVerbosity.Verbose, component, message);

    public static T TraceOperation<T>(
        string component,
        string startMessage,
        Func<T, string> completionMessageFactory,
        Func<T> action,
        FbxLogVerbosity verbosity = FbxLogVerbosity.Info)
    {
        ArgumentNullException.ThrowIfNull(completionMessageFactory);
        ArgumentNullException.ThrowIfNull(action);

        bool logProgress = IsEnabled(verbosity);
        bool logErrors = IsEnabled(FbxLogVerbosity.Errors);
        Stopwatch? stopwatch = logProgress || logErrors ? Stopwatch.StartNew() : null;

        if (logProgress)
            Write(verbosity, component, startMessage);

        try
        {
            T result = action();
            if (logProgress)
                Write(verbosity, component, $"{completionMessageFactory(result)} (elapsed={FormatElapsed(stopwatch)})");
            return result;
        }
        catch (Exception ex)
        {
            if (logErrors)
                Write(FbxLogVerbosity.Errors, component, $"{startMessage} failed after {FormatElapsed(stopwatch)}: {ex}");
            throw;
        }
    }

    public static void TraceOperation(
        string component,
        string startMessage,
        Action action,
        Func<string>? completionMessageFactory = null,
        FbxLogVerbosity verbosity = FbxLogVerbosity.Info)
    {
        ArgumentNullException.ThrowIfNull(action);

        bool logProgress = IsEnabled(verbosity);
        bool logErrors = IsEnabled(FbxLogVerbosity.Errors);
        Stopwatch? stopwatch = logProgress || logErrors ? Stopwatch.StartNew() : null;

        if (logProgress)
            Write(verbosity, component, startMessage);

        try
        {
            action();
            if (logProgress)
            {
                string completionMessage = completionMessageFactory?.Invoke() ?? "Completed.";
                Write(verbosity, component, $"{completionMessage} (elapsed={FormatElapsed(stopwatch)})");
            }
        }
        catch (Exception ex)
        {
            if (logErrors)
                Write(FbxLogVerbosity.Errors, component, $"{startMessage} failed after {FormatElapsed(stopwatch)}: {ex}");
            throw;
        }
    }

    private static void Write(FbxLogVerbosity verbosity, string component, string message)
    {
        if (!IsEnabled(verbosity))
            return;

        string line = $"[FBX][{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}][{verbosity}][{component}] {message}";
        try
        {
            if (LogSink is not null)
            {
                LogSink(line);
                return;
            }
        }
        catch
        {
            // Fall back to Trace/Console if the host sink fails.
        }

        Trace.WriteLine(line);
        Console.WriteLine(line);
    }

    private static string FormatElapsed(Stopwatch? stopwatch)
        => stopwatch is null ? "n/a" : $"{stopwatch.Elapsed.TotalMilliseconds:0.###} ms";

    private static FbxLogVerbosity ReadVerbosityFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("XRE_FBX_LOG");
        if (string.IsNullOrWhiteSpace(value))
            return FbxLogVerbosity.Off;

        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "none" => FbxLogVerbosity.Off,
            "error" or "errors" => FbxLogVerbosity.Errors,
            "warn" or "warning" or "warnings" => FbxLogVerbosity.Warnings,
            "info" => FbxLogVerbosity.Info,
            "1" or "on" or "true" or "trace" or "verbose" or "debug" or "all" => FbxLogVerbosity.Verbose,
            _ => FbxLogVerbosity.Info,
        };
    }
}