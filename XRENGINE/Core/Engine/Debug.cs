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
        private static readonly List<(string Token, bool RequireBoundary)> OpenGlTokens = new()
        {
            ("opengl", false),
            ("gl error", true),
            ("gl warning", true),
            ("gl_debug", true),
            ("gl debug", true),
            ("gl_invalid", true),
            ("gl_out_of", true),
            ("silk.net.opengl", false),
        };

        private static readonly List<(string Token, bool RequireBoundary)> RenderingTokens = new()
        {
            ("xreengine.rendering", false),
            ("\\rendering\\", false),
            ("/rendering/", false),
            ("gpurenderpass", false),
            ("gpu", false),
            ("glbuffer", false),
            ("gldatabuffer", false),
            ("renderpass", false),
            ("renderer", false),
            ("rendering", false),
            (" render", false),
            ("render target", false),
            ("drawcall", false),
            ("framebuffer", false),
            ("shader", false),
            ("ensurecombinedprogram", false),
            ("batch draw", false),
            ("materialid", false),
            ("creating new program", false),
            ("program created and linked", false),
            ("program cached", false),
            ("added new viewport", false),
            ("viewport", false),
            ("xrwindow", false),
            ("xrmesh", false),
            ("mesh ", false),
            ("mesh.", false),
            ("mesh:", false),
            ("mesh=", false),
        };

        private static readonly List<(string Token, bool RequireBoundary)> PhysicsTokens = new()
        {
            ("[physics]", false),
            ("physics", true),
            ("physx", false),
            ("rigidbody", false),
            ("collision", false),
        };

        private const string PhysicsPrefix = "[Physics]";

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
            => Log(ELogCategory.Physics, EOutputVerbosity.Normal, false, $"{PhysicsPrefix} {message}", args);

        public static void Rendering(string message, params object[] args)
            => Log(ELogCategory.Rendering, EOutputVerbosity.Normal, false, message, args);
        public static void OpenGL(string message, params object[] args)
            => Log(ELogCategory.OpenGL, EOutputVerbosity.Normal, false, message, args);
        public static void Audio(string message, params object[] args)
            => Log(ELogCategory.Audio, EOutputVerbosity.Normal, false, message, args);

        /// <summary>
        /// Logs a message under an explicit category (no keyword-based classification).
        /// </summary>
        public static void Log(ELogCategory category, string message, params object[] args)
            => Log(category, EOutputVerbosity.Verbose, true, message, args);

        /// <summary>
        /// Logs a message under an explicit category (no keyword-based classification).
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

            GameStartupSettings settings = Engine.GameSettings;
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

            bool logToFile = settings.LogOutputToFile;
            WriteLogMessage(message, logToFile);
#endif
        }

        private static void Suppressed(string message)
            => WriteLogMessage($"[Suppressed] {message}", Engine.GameSettings?.LogOutputToFile ?? false);

        public static void LogException(Exception ex, string? message = null)
        {
#if DEBUG || EDITOR
            if (message != null)
                Out(EOutputVerbosity.Minimal, false, $"{message}{Environment.NewLine}{ex}");
            else
                Out(EOutputVerbosity.Minimal, false, ex.ToString());
#endif
        }
        public static void LogWarning(string message, int lineIgnoreCount = 0, int includedLineCount = 5)
        {
#if DEBUG || EDITOR
            Out(EOutputVerbosity.Normal, true, false, false, true, 4 + lineIgnoreCount, includedLineCount, message);
#endif
        }

        public static void LogError(string message, int lineIgnoreCount = 0, int includedLineCount = 10)
        {
    #if DEBUG || EDITOR
            Out(EOutputVerbosity.Minimal, false, false, false, true, 4 + lineIgnoreCount, includedLineCount, message);
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

        private static void WriteLogMessage(string message, bool logToFile, ELogCategory? categoryOverride = null)
        {
            StreamWriter? writer = null;
            ELogCategory category;

            lock (LogWriterLock)
            {
            category = categoryOverride ?? ClassifyMessage(message);
                writer = EnsureLogWriterInternal(category, logToFile);
                if (writer is not null)
                    writer.WriteLine($"{DateTime.Now:O} {message}");
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
                string fileSuffix = category switch
                {
                    ELogCategory.OpenGL => "opengl",
                    ELogCategory.Rendering => "rendering",
                    ELogCategory.Physics => "physics",
                    ELogCategory.Audio => "audio",
                    _ => "general",
                };
                string fileName = $"log_{fileSuffix}_{_logSessionId}.txt";
                string filePath = Path.Combine(logsDirectory, fileName);
                var writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                writer.WriteLine($"Log ({fileSuffix}) started {DateTime.Now:O}");
                LogWriters[category] = writer;
            }

            return LogWriters[category];
        }

        private static ELogCategory ClassifyMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return ELogCategory.General;

            string normalized = message.ToLowerInvariant();

            foreach (var entry in OpenGlTokens)
            {
                if (ContainsToken(normalized, entry.Token, entry.RequireBoundary))
                    return ELogCategory.OpenGL;
            }

            if (normalized.StartsWith("render"))
                return ELogCategory.Rendering;

            foreach (var entry in RenderingTokens)
            {
                if (ContainsToken(normalized, entry.Token, entry.RequireBoundary))
                    return ELogCategory.Rendering;
            }

            foreach (var entry in PhysicsTokens)
            {
                if (ContainsToken(normalized, entry.Token, entry.RequireBoundary))
                    return ELogCategory.Physics;
            }

            return ELogCategory.General;
        }

        private static bool ContainsToken(string source, string token, bool requireWordBoundary)
        {
            int index = source.IndexOf(token, StringComparison.Ordinal);
            while (index >= 0)
            {
                if (!requireWordBoundary || index == 0 || !char.IsLetterOrDigit(source[index - 1]))
                    return true;

                index = source.IndexOf(token, index + token.Length, StringComparison.Ordinal);
            }

            return false;
        }

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
