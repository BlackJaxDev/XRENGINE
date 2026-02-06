using Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace XREngine
{
    /// <summary>
    /// Categories for log messages, used for filtering and routing to different log files.
    /// </summary>
    public enum ELogCategory
    {
        General,
        Rendering,
        OpenGL,
        Physics,
        Audio,
        Animation,
        UI,
        Vulkan,
        Networking,
        VR,
        Scripting,
    }

    /// <summary>
    /// Represents a single log entry with its message, category, timestamp, and repeat count.
    /// </summary>
    public class LogEntry
    {
        public string Message { get; }
        public ELogCategory Category { get; }
        public DateTime Timestamp { get; }
        public int RepeatCount { get; internal set; }

        public LogEntry(string message, ELogCategory category, DateTime timestamp)
        {
            Message = message;
            Category = category;
            Timestamp = timestamp;
            RepeatCount = 1;
        }
    }

    public class Debug
    {
        private static readonly ConcurrentDictionary<string, DateTime> RecentMessageCache = new();
        private static readonly ConcurrentDictionary<string, long> RateLimitedMessageCache = new();
        public static Queue<(string, DateTime)> Output { get; } = new Queue<(string, DateTime)>();
        public static bool AllowOutput { get; set; } = true;

        /// <summary>
        /// Raised when a new log entry is added to the in-engine console buffer.
        /// This fires regardless of whether output is also written to file/console.
        /// </summary>
        public static event Action<LogEntry>? ConsoleEntryAdded;
        private const int MaxRunDirectoryCount = 3;
        private const int MaxConsoleEntries = 5000;
        private static readonly object LogWriterLock = new();
        private static readonly object ConsoleEntriesLock = new();
        private static readonly List<LogEntry> _consoleEntries = new();
        private static readonly Dictionary<ELogCategory, StreamWriter?> LogWriters = new()
        {
            [ELogCategory.General] = null,
            [ELogCategory.Rendering] = null,
            [ELogCategory.OpenGL] = null,
            [ELogCategory.Physics] = null,
            [ELogCategory.Audio] = null,
            [ELogCategory.Animation] = null,
            [ELogCategory.UI] = null,
            [ELogCategory.Vulkan] = null,
            [ELogCategory.Networking] = null,
            [ELogCategory.VR] = null,
            [ELogCategory.Scripting] = null,
        };

        /// <summary>
        /// Returns a snapshot of all console log entries.
        /// </summary>
        public static List<LogEntry> GetConsoleEntries()
        {
            lock (ConsoleEntriesLock)
            {
                return new List<LogEntry>(_consoleEntries);
            }
        }

        /// <summary>
        /// Clears all console log entries.
        /// </summary>
        public static void ClearConsoleEntries()
        {
            lock (ConsoleEntriesLock)
            {
                _consoleEntries.Clear();
            }
        }

        /// <summary>
        /// Clears console log entries for a specific category.
        /// </summary>
        public static void ClearConsoleEntries(ELogCategory category)
        {
            lock (ConsoleEntriesLock)
            {
                _consoleEntries.RemoveAll(e => e.Category == category);
            }
        }

        private static void AddConsoleEntry(string message, ELogCategory category)
        {
            LogEntry? addedEntry = null;
            lock (ConsoleEntriesLock)
            {
                // Check if this message is the same as the last one (collapse repeats)
                if (_consoleEntries.Count > 0)
                {
                    var lastEntry = _consoleEntries[^1];
                    if (lastEntry.Message == message && lastEntry.Category == category)
                    {
                        lastEntry.RepeatCount++;
                        return;
                    }
                }

                if (_consoleEntries.Count >= MaxConsoleEntries)
                    _consoleEntries.RemoveAt(0);
                addedEntry = new LogEntry(message, category, DateTime.Now);
                _consoleEntries.Add(addedEntry);
            }

            ConsoleEntryAdded?.Invoke(addedEntry);
        }

        private static string? _logSessionId;
        private static string? _logsRootDirectory;
        private static string? _logRunDirectory;

        /// <summary>
        /// Prints a message for debugging purposes.
        /// </summary>
        public static void Out(string message, params object[] args)
            => Out(EOutputVerbosity.Verbose, message, args);
        /// <summary>
        /// Prints a message for debugging purposes.
        /// </summary>
        public static void Out(EOutputVerbosity verbosity, string message, params object[] args)
            => Out(verbosity, true, message, args);
        /// <summary>
        /// Prints a message for debugging purposes.
        /// </summary>
        public static void Out(EOutputVerbosity verbosity, bool debugOnly, string message, params object[] args)
            => Out(verbosity, debugOnly, false, false, false, 0, 0, message, args);

        /// <summary>
        /// Convenience helper that routes output through the physics log.
        /// </summary>
        public static void Physics(string message, params object[] args)
            => Log(ELogCategory.Physics, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the rendering log.
        /// </summary>
        public static void Rendering(string message, params object[] args)
            => Log(ELogCategory.Rendering, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the OpenGL log.
        /// </summary>
        public static void OpenGL(string message, params object[] args)
            => Log(ELogCategory.OpenGL, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the audio log.
        /// </summary>
        public static void Audio(string message, params object[] args)
            => Log(ELogCategory.Audio, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the animation log.
        /// </summary>
        public static void Animation(string message, params object[] args)
            => Log(ELogCategory.Animation, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the UI log.
        /// </summary>
        public static void UI(string message, params object[] args)
            => Log(ELogCategory.UI, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the Vulkan log.
        /// </summary>
        public static void Vulkan(string message, params object[] args)
            => Log(ELogCategory.Vulkan, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the networking log.
        /// </summary>
        public static void Networking(string message, params object[] args)
            => Log(ELogCategory.Networking, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the VR log.
        /// </summary>
        public static void VR(string message, params object[] args)
            => Log(ELogCategory.VR, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Convenience helper that routes output through the scripting log.
        /// </summary>
        public static void Scripting(string message, params object[] args)
            => Log(ELogCategory.Scripting, EOutputVerbosity.Normal, false, message, args);

        #region Category-Specific Warnings

        /// <summary>
        /// Logs a warning message to the rendering log with stack trace.
        /// </summary>
        public static void RenderingWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Rendering, message, args);

        /// <summary>
        /// Logs a warning message to the OpenGL log with stack trace.
        /// </summary>
        public static void OpenGLWarning(string message, params object[] args)
            => LogWarning(ELogCategory.OpenGL, message, args);

        /// <summary>
        /// Logs a warning message to the physics log with stack trace.
        /// </summary>
        public static void PhysicsWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Physics, message, args);

        /// <summary>
        /// Logs a warning message to the audio log with stack trace.
        /// </summary>
        public static void AudioWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Audio, message, args);

        /// <summary>
        /// Logs a warning message to the animation log with stack trace.
        /// </summary>
        public static void AnimationWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Animation, message, args);

        /// <summary>
        /// Logs a warning message to the UI log with stack trace.
        /// </summary>
        public static void UIWarning(string message, params object[] args)
            => LogWarning(ELogCategory.UI, message, args);

        /// <summary>
        /// Logs a warning message to the Vulkan log with stack trace.
        /// </summary>
        public static void VulkanWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Vulkan, message, args);

        /// <summary>
        /// Logs a warning message to the networking log with stack trace.
        /// </summary>
        public static void NetworkingWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Networking, message, args);

        /// <summary>
        /// Logs a warning message to the VR log with stack trace.
        /// </summary>
        public static void VRWarning(string message, params object[] args)
            => LogWarning(ELogCategory.VR, message, args);

        /// <summary>
        /// Logs a warning message to the scripting log with stack trace.
        /// </summary>
        public static void ScriptingWarning(string message, params object[] args)
            => LogWarning(ELogCategory.Scripting, message, args);

        /// <summary>
        /// Logs a warning message under an explicit category with stack trace.
        /// </summary>
        public static void LogWarning(ELogCategory category, string message, params object[] args)
        {
#if DEBUG || EDITOR
            if (args.Length > 0)
                message = string.Format(message, args);
            string stackTrace = GetStackTrace(4, 5);
            WriteLogMessage($"[WARN] {message}{Environment.NewLine}{stackTrace}", Engine.GameSettings?.LogOutputToFile ?? false, category);
#endif
        }

        #endregion

        #region Category-Specific Exceptions

        /// <summary>
        /// Logs an exception to the rendering log.
        /// </summary>
        public static void RenderingException(Exception ex, string? message = null)
            => LogException(ELogCategory.Rendering, ex, message);

        /// <summary>
        /// Logs an exception to the OpenGL log.
        /// </summary>
        public static void OpenGLException(Exception ex, string? message = null)
            => LogException(ELogCategory.OpenGL, ex, message);

        /// <summary>
        /// Logs an exception to the physics log.
        /// </summary>
        public static void PhysicsException(Exception ex, string? message = null)
            => LogException(ELogCategory.Physics, ex, message);

        /// <summary>
        /// Logs an exception to the audio log.
        /// </summary>
        public static void AudioException(Exception ex, string? message = null)
            => LogException(ELogCategory.Audio, ex, message);

        /// <summary>
        /// Logs an exception to the animation log.
        /// </summary>
        public static void AnimationException(Exception ex, string? message = null)
            => LogException(ELogCategory.Animation, ex, message);

        /// <summary>
        /// Logs an exception to the UI log.
        /// </summary>
        public static void UIException(Exception ex, string? message = null)
            => LogException(ELogCategory.UI, ex, message);

        /// <summary>
        /// Logs an exception to the Vulkan log.
        /// </summary>
        public static void VulkanException(Exception ex, string? message = null)
            => LogException(ELogCategory.Vulkan, ex, message);

        /// <summary>
        /// Logs an exception to the networking log.
        /// </summary>
        public static void NetworkingException(Exception ex, string? message = null)
            => LogException(ELogCategory.Networking, ex, message);

        /// <summary>
        /// Logs an exception to the VR log.
        /// </summary>
        public static void VRException(Exception ex, string? message = null)
            => LogException(ELogCategory.VR, ex, message);

        /// <summary>
        /// Logs an exception to the scripting log.
        /// </summary>
        public static void ScriptingException(Exception ex, string? message = null)
            => LogException(ELogCategory.Scripting, ex, message);

        /// <summary>
        /// Logs an exception under an explicit category.
        /// </summary>
        public static void LogException(ELogCategory category, Exception ex, string? message = null)
        {
#if DEBUG || EDITOR
            string logMessage = message != null
                ? $"[EXCEPTION] {message}{Environment.NewLine}{ex}"
                : $"[EXCEPTION] {ex}";
            WriteLogMessage(logMessage, Engine.GameSettings?.LogOutputToFile ?? false, category);
#endif
        }

        #endregion

        #region Category-Specific Errors

        /// <summary>
        /// Logs an error message to the rendering log with stack trace.
        /// </summary>
        public static void RenderingError(string message, params object[] args)
            => LogError(ELogCategory.Rendering, message, args);

        /// <summary>
        /// Logs an error message to the OpenGL log with stack trace.
        /// </summary>
        public static void OpenGLError(string message, params object[] args)
            => LogError(ELogCategory.OpenGL, message, args);

        /// <summary>
        /// Logs an error message to the physics log with stack trace.
        /// </summary>
        public static void PhysicsError(string message, params object[] args)
            => LogError(ELogCategory.Physics, message, args);

        /// <summary>
        /// Logs an error message to the audio log with stack trace.
        /// </summary>
        public static void AudioError(string message, params object[] args)
            => LogError(ELogCategory.Audio, message, args);

        /// <summary>
        /// Logs an error message to the animation log with stack trace.
        /// </summary>
        public static void AnimationError(string message, params object[] args)
            => LogError(ELogCategory.Animation, message, args);

        /// <summary>
        /// Logs an error message to the UI log with stack trace.
        /// </summary>
        public static void UIError(string message, params object[] args)
            => LogError(ELogCategory.UI, message, args);

        /// <summary>
        /// Logs an error message to the Vulkan log with stack trace.
        /// </summary>
        public static void VulkanError(string message, params object[] args)
            => LogError(ELogCategory.Vulkan, message, args);

        /// <summary>
        /// Logs an error message to the networking log with stack trace.
        /// </summary>
        public static void NetworkingError(string message, params object[] args)
            => LogError(ELogCategory.Networking, message, args);

        /// <summary>
        /// Logs an error message to the VR log with stack trace.
        /// </summary>
        public static void VRError(string message, params object[] args)
            => LogError(ELogCategory.VR, message, args);

        /// <summary>
        /// Logs an error message to the scripting log with stack trace.
        /// </summary>
        public static void ScriptingError(string message, params object[] args)
            => LogError(ELogCategory.Scripting, message, args);

        /// <summary>
        /// Logs an error message under an explicit category with stack trace.
        /// </summary>
        public static void LogError(ELogCategory category, string message, params object[] args)
        {
#if DEBUG || EDITOR
            if (args.Length > 0)
                message = string.Format(message, args);
            string stackTrace = GetStackTrace(4, 10);
            WriteLogMessage($"[ERROR] {message}{Environment.NewLine}{stackTrace}", Engine.GameSettings?.LogOutputToFile ?? false, category);
#endif
        }

        #endregion

        /// <summary>
        /// Logs a message under an explicit category.
        /// </summary>
        public static void Log(ELogCategory category, string message, params object[] args)
            => Log(category, EOutputVerbosity.Verbose, true, message, args);

        /// <summary>
        /// Logs a message under an explicit category.
        /// </summary>
        public static void Log(ELogCategory category, EOutputVerbosity verbosity, bool debugOnly, string message, params object[] args)
        {
#if DEBUG || EDITOR
            if (!AllowOutput)
            {
                Suppressed(message);
                return;
            }

            var renderSettings = Engine.Rendering.Settings;
            if (verbosity > Engine.EffectiveSettings.OutputVerbosity)
            {
                Suppressed(message);
                return;
            }

            if (args.Length > 0)
                message = string.Format(message, args);

            DateTime now = DateTime.Now;

            double recentness = renderSettings.DebugOutputRecencySeconds;
            if (recentness > 0.0)
            {
                List<string> removeKeys = [];
                RecentMessageCache.ForEach(x =>
                {
                    TimeSpan span = now - x.Value;
                    if (span.TotalSeconds >= recentness)
                        removeKeys.Add(x.Key);
                });
                removeKeys.ForEach(x => RecentMessageCache.TryRemove(x, out _));

                if (RecentMessageCache.ContainsKey(message))
                    return;
                RecentMessageCache.TryAdd(message, now);
            }

            bool logToFile = Engine.GameSettings.LogOutputToFile;
            WriteLogMessage(message, logToFile, category);
#endif
        }
        /// <summary>
        /// Prints a message for debugging purposes.
        /// </summary>
        public static void Out(
            EOutputVerbosity verbosity,
            bool debugOnly,
            bool printDate,
            bool printAppDomain,
            bool printStackTrace,
            int stackTraceIgnoredLineCount,
            int stackTraceIncludedLineCount,
            string message,
            params object[] args)
        {
#if DEBUG || EDITOR

            if (!AllowOutput)
            {
                Suppressed(message);
                return;
            }

            GameStartupSettings? settings = Engine.GameSettings;
            var renderSettings = Engine.Rendering.Settings;

            if (verbosity > Engine.EffectiveSettings.OutputVerbosity)
            {
                Suppressed(message);
                return;
            }

            if (args.Length > 0)
                message = string.Format(message, args);

            if (printStackTrace)
                message += Environment.NewLine + GetStackTrace(stackTraceIgnoredLineCount, stackTraceIncludedLineCount);

            DateTime now = DateTime.Now;

            double recentness = renderSettings?.DebugOutputRecencySeconds ?? 0.0;
            if (recentness > 0.0)
            {
                List<string> removeKeys = [];
                RecentMessageCache.ForEach(x =>
                {
                    TimeSpan span = now - x.Value;
                    if (span.TotalSeconds >= recentness)
                        removeKeys.Add(x.Key);
                });
                removeKeys.ForEach(x => RecentMessageCache.TryRemove(x, out _));

                if (RecentMessageCache.ContainsKey(message))
                {
                    //Messages already cleaned above, just return here

                    //TimeSpan span = now - RecentMessages[message];
                    //if (span.TotalSeconds <= AllowedOutputRecentness)
                    return;
                }
                else
                    RecentMessageCache.TryAdd(message, now);
            }

            bool printDomain = printAppDomain/* || Settings.PrintAppDomainInOutput*/;

            if (printDate && printDomain)
                message = $"[{AppDomain.CurrentDomain.FriendlyName} {now}] " + message;
            else if (printDomain)
                message = $"[{AppDomain.CurrentDomain.FriendlyName}] " + message;
            else if (printDate)
                message = $"[{now}] " + message;

            bool logToFile = settings?.LogOutputToFile ?? false;
            WriteLogMessage(message, logToFile);
#endif
        }

        private static void Suppressed(string message)
            => WriteLogMessage($"[Suppressed] {message}", Engine.GameSettings?.LogOutputToFile ?? false);

        /// <summary>
        /// Logs an exception to the general log. Use category-specific methods for specialized logging.
        /// </summary>
        public static void LogException(Exception ex, string? message = null)
            => LogException(ELogCategory.General, ex, message);

        /// <summary>
        /// Logs a warning to the general log with stack trace. Use category-specific methods for specialized logging.
        /// </summary>
        public static void LogWarning(string message, int lineIgnoreCount = 0, int includedLineCount = 5)
        {
#if DEBUG || EDITOR
            string stackTrace = GetStackTrace(4 + lineIgnoreCount, includedLineCount);
            WriteLogMessage($"[WARN] {message}{Environment.NewLine}{stackTrace}", Engine.GameSettings?.LogOutputToFile ?? false, ELogCategory.General);
#endif
        }

        /// <summary>
        /// Returns true if the caller should emit a message for <paramref name="key"/>, rate-limited by <paramref name="interval"/>.
        /// Uses a monotonic clock (Stopwatch).
        /// </summary>
        public static bool ShouldLogEvery(string key, TimeSpan interval)
        {
    #if DEBUG || EDITOR
            if (interval <= TimeSpan.Zero)
            return true;

            long now = Stopwatch.GetTimestamp();
            long minTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);

            if (RateLimitedMessageCache.TryGetValue(key, out long last) && (now - last) < minTicks)
            return false;

            RateLimitedMessageCache[key] = now;
            return true;
    #else
            return false;
    #endif
        }

        /// <summary>
        /// Rate-limited rendering log. Intended for per-frame diagnostics.
        /// </summary>
        public static void RenderingEvery(string key, TimeSpan interval, string message, params object[] args)
        {
    #if DEBUG || EDITOR
            if (!ShouldLogEvery(key, interval))
            return;
            Rendering(message, args);
    #endif
        }

        /// <summary>
        /// Rate-limited warning without stack trace (keeps logs readable).
        /// </summary>
        public static void RenderingWarningEvery(string key, TimeSpan interval, string message, params object[] args)
        {
    #if DEBUG || EDITOR
            if (!ShouldLogEvery(key, interval))
            return;
            Log(ELogCategory.Rendering, EOutputVerbosity.Normal, false, "[WARN] " + message, args);
    #endif
        }

        /// <summary>
        /// Rate-limited Vulkan log. Intended for per-frame diagnostics.
        /// </summary>
        public static void VulkanEvery(string key, TimeSpan interval, string message, params object[] args)
        {
    #if DEBUG || EDITOR
            if (!ShouldLogEvery(key, interval))
            return;
            Vulkan(message, args);
    #endif
        }

        /// <summary>
        /// Rate-limited Vulkan warning without stack trace (keeps logs readable).
        /// </summary>
        public static void VulkanWarningEvery(string key, TimeSpan interval, string message, params object[] args)
        {
    #if DEBUG || EDITOR
            if (!ShouldLogEvery(key, interval))
            return;
            Log(ELogCategory.Vulkan, EOutputVerbosity.Normal, false, "[WARN] " + message, args);
    #endif
        }

        /// <summary>
        /// Logs an error to the general log with stack trace. Use category-specific methods for specialized logging.
        /// </summary>
        public static void LogError(string message, int lineIgnoreCount = 0, int includedLineCount = 10)
        {
    #if DEBUG || EDITOR
            string stackTrace = GetStackTrace(4 + lineIgnoreCount, includedLineCount);
            WriteLogMessage($"[ERROR] {message}{Environment.NewLine}{stackTrace}", Engine.GameSettings?.LogOutputToFile ?? false, ELogCategory.General);
    #endif
        }
        public static string GetStackTrace(int lineIgnoreCount = 3, int includedLineCount = -1, bool ignoreBeforeWndProc = true)
        {
            //Format and print stack trace
            string stackTrace = Environment.StackTrace;
            string atStr = "   at ";

            int at4th = stackTrace.FindOccurrence(0, lineIgnoreCount, atStr);
            if (at4th > 0)
                stackTrace = stackTrace[at4th..];

            if (ignoreBeforeWndProc)
            {
                //Everything before wndProc is almost always irrelevant
                int wndProc = stackTrace.IndexOf("WndProc(Message& m)");
                if (wndProc > 0)
                {
                    int at = stackTrace.FindFirstReverse(wndProc, atStr);
                    if (at > 0)
                        stackTrace = stackTrace[..at];
                }
            }

            if (includedLineCount >= 0)
            {
                int atXth = stackTrace.FindOccurrence(0, includedLineCount, atStr);
                if (atXth > 0)
                    stackTrace = stackTrace[..atXth];
            }

            return stackTrace;
        }

        private static void WriteLogMessage(string message, bool logToFile, ELogCategory category = ELogCategory.General)
        {
            StreamWriter? writer = null;

            lock (LogWriterLock)
            {
                writer = EnsureLogWriterInternal(category, logToFile);
                writer?.WriteLine($"{FormatTimestamp(DateTimeOffset.Now)} {message}");
            }

            // Add to console entries for in-editor viewing
            AddConsoleEntry(message, category);

            if (writer is null)
            {
                Trace.WriteLine(message);
                Console.WriteLine(message);
            }
        }

        private static StreamWriter? EnsureLogWriterInternal(ELogCategory category, bool logToFile)
        {
            if (!logToFile)
            {
                ResetLogWriters();
                return null;
            }

            _logSessionId ??= $"{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}";

            if (LogWriters[category] is null)
            {
                string logsDirectory = GetLogRunDirectory();
                string fileSuffix = category.ToString().ToLowerInvariant();
                string fileName = $"log_{fileSuffix}_{_logSessionId}.txt";
                string filePath = Path.Combine(logsDirectory, fileName);
                var writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                writer.WriteLine($"Log ({fileSuffix}) started {FormatTimestamp(DateTimeOffset.Now)}");
                LogWriters[category] = writer;
            }

            return LogWriters[category];
        }

        private static string FormatTimestamp(DateTimeOffset timestamp)
            => timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

        private static void ResetLogWriters()
        {
            foreach (ELogCategory category in LogWriters.Keys.ToArray())
            {
                LogWriters[category]?.Dispose();
                LogWriters[category] = null;
            }

            _logSessionId = null;
            _logRunDirectory = null;
        }

        /// <summary>
        /// Ensures the current run log directory exists and returns its path.
        /// Useful for capturing native stdout/stderr alongside engine logs.
        /// </summary>
        public static string EnsureLogRunDirectory()
        {
            lock (LogWriterLock)
            {
                return GetLogRunDirectory();
            }
        }

        private static string GetLogsRootDirectory()
        {
            if (_logsRootDirectory is not null)
                return _logsRootDirectory;

            string baseDirectory = FindRepositoryRoot() ?? AppContext.BaseDirectory;
            string preferred = Path.Combine(baseDirectory, "Build", "Logs");

            if (!TryCreateDirectory(preferred))
            {
                string fallback = Path.Combine(AppContext.BaseDirectory, "Logs");
                TryCreateDirectory(fallback);
                preferred = fallback;
            }

            _logsRootDirectory = preferred;
            return preferred;
        }

        private static string GetLogRunDirectory()
        {
            if (_logRunDirectory is not null)
                return _logRunDirectory;

            string rootDirectory = GetLogsRootDirectory();
            string buildFolder = SanitizePathSegment(GetBuildIdentifier());
            string platformFolder = SanitizePathSegment(GetPlatformIdentifier());

            string runsRoot = Path.Combine(rootDirectory, buildFolder, platformFolder);
            if (!TryCreateDirectory(runsRoot))
                runsRoot = rootDirectory;

            if (_logSessionId is null)
                _logSessionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}";

            string runDirectory = Path.Combine(runsRoot, _logSessionId);
            if (!TryCreateDirectory(runDirectory))
            {
                string fallback = Path.Combine(rootDirectory, $"run_{_logSessionId}");
                TryCreateDirectory(fallback);
                runDirectory = fallback;
            }

            EnforceRunDirectoryLimit(runsRoot);

            _logRunDirectory = runDirectory;
            return runDirectory;
        }

        private static string GetBuildIdentifier()
        {
            try
            {
                DirectoryInfo baseDir = new(AppContext.BaseDirectory);
                string? tfm = baseDir.Name;
                string? configuration = baseDir.Parent?.Name;

                if (!string.IsNullOrWhiteSpace(configuration) && !string.IsNullOrWhiteSpace(tfm))
                    return $"{configuration}_{tfm}";

                if (!string.IsNullOrWhiteSpace(tfm))
                    return tfm;

                return configuration ?? AppDomain.CurrentDomain.FriendlyName ?? "UnknownBuild";
            }
            catch
            {
                return AppDomain.CurrentDomain.FriendlyName ?? "UnknownBuild";
            }
        }

        private static string GetPlatformIdentifier()
        {
            try
            {
                string arch = RuntimeInformation.ProcessArchitecture.ToString();
                string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                            RuntimeInformation.OSDescription;

                return $"{os}_{arch}".ToLowerInvariant();
            }
            catch
            {
                return "unknown_platform";
            }
        }

        private static string SanitizePathSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return "unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(segment
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray());

            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
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
                // Ignore; we'll fall back to the executable directory later.
            }

            return null;
        }

        private static bool TryCreateDirectory(string path)
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

        private static void EnforceRunDirectoryLimit(string runsRoot)
        {
            try
            {
                Directory.CreateDirectory(runsRoot);
                DirectoryInfo rootInfo = new(runsRoot);
                DirectoryInfo[] runDirectories = rootInfo.GetDirectories();

                if (runDirectories.Length <= MaxRunDirectoryCount)
                    return;

                foreach (DirectoryInfo dir in runDirectories
                    .OrderByDescending(d => d.CreationTimeUtc)
                    .Skip(MaxRunDirectoryCount))
                {
                    try
                    {
                        dir.Delete(true);
                    }
                    catch
                    {
                        // Ignore cleanup failures, permissions may block deletion.
                    }
                }
            }
            catch
            {
                // If we cannot enumerate directories, skip retention to avoid crashing logging.
            }
        }
    }
}
