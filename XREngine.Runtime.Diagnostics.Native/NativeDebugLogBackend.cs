using System.Threading;

namespace XREngine.Runtime.Diagnostics.Native;

/// <summary>Creates native log runs and dispatches render-thread log work.</summary>
public sealed class NativeDebugLogBackend : IRuntimeDebugLogBackend
{
    private readonly object _rootLock = new();
    private string? _logsRootDirectory;

    public IRuntimeDebugLogSession CreateSession(RuntimeDebugLogSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new NativeDebugLogSession(this, options);
    }

    public void QueueOut(WaitCallback callback, object state)
        => ThreadPool.QueueUserWorkItem(callback, state);

    public void QueueCategory(WaitCallback callback, object state)
        => ThreadPool.QueueUserWorkItem(callback, state);

    public void QueueAuxiliary(WaitCallback callback, object state)
        => ThreadPool.QueueUserWorkItem(callback, state);

    internal string GetLogsRootDirectory()
    {
        lock (_rootLock)
        {
            if (_logsRootDirectory is not null)
                return _logsRootDirectory;

            string? editorSessionRoot = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.EditorSessionRoot);
            string preferred = string.IsNullOrWhiteSpace(editorSessionRoot)
                ? Path.Combine(FindRepositoryRoot() ?? AppContext.BaseDirectory, "Build", "Logs")
                : Path.Combine(Path.GetFullPath(editorSessionRoot), "logs");

            if (!TryCreateDirectory(preferred))
            {
                string fallback = Path.Combine(AppContext.BaseDirectory, "Logs");
                TryCreateDirectory(fallback);
                preferred = fallback;
            }

            _logsRootDirectory = preferred;
            return preferred;
        }
    }

    internal static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindRepositoryRoot()
    {
        try
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                string currentPath = current.FullName;
                if (File.Exists(Path.Combine(currentPath, "XRENGINE.sln")) || Directory.Exists(Path.Combine(currentPath, ".git")))
                    return currentPath;

                current = current.Parent;
            }
        }
        catch
        {
            // Use the executable directory when repository discovery fails.
        }

        return null;
    }
}
