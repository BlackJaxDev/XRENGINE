using Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace XREngine
{
    public class Debug
    {
        private enum LogCategory
        {
            General,
            Rendering,
            OpenGL,
        }

        private static readonly ConcurrentDictionary<string, DateTime> RecentMessageCache = new();
        public static Queue<(string, DateTime)> Output { get; } = new Queue<(string, DateTime)>();
        public static bool AllowOutput { get; set; } = true;
        private const int MaxLogFileCount = 10;
        private static readonly object LogWriterLock = new();
        private static readonly Dictionary<LogCategory, StreamWriter?> LogWriters = new()
        {
            [LogCategory.General] = null,
            [LogCategory.Rendering] = null,
            [LogCategory.OpenGL] = null,
        };
        private static string? _logSessionId;
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

            if (verbosity > settings.OutputVerbosity)
            {
                Suppressed(message);
                return;
            }

            if (args.Length > 0)
                message = string.Format(message, args);

            if (printStackTrace)
                message += Environment.NewLine + GetStackTrace(stackTraceIgnoredLineCount, stackTraceIncludedLineCount);

            DateTime now = DateTime.Now;

            double recentness = Engine.UserSettings.DebugOutputRecencySeconds;
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

        private static void WriteLogMessage(string message, bool logToFile)
        {
            StreamWriter? writer = null;

            lock (LogWriterLock)
            {
                LogCategory category = ClassifyMessage(message);
                writer = EnsureLogWriterInternal(category, logToFile);
                if (writer is not null)
                    writer.WriteLine($"{DateTime.Now:O} {message}");
            }

            if (writer is null)
            {
                Trace.WriteLine(message);
                Console.WriteLine(message);
            }
        }

        private static StreamWriter? EnsureLogWriterInternal(LogCategory category, bool logToFile)
        {
            if (!logToFile)
            {
                ResetLogWriters();
                return null;
            }

            if (_logSessionId is null)
                _logSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            if (LogWriters[category] is null)
            {
                string baseDirectory = AppContext.BaseDirectory;
                string logsDirectory = Path.Combine(baseDirectory, "Logs");
                Directory.CreateDirectory(logsDirectory);
                EnforceLogFileLimit(logsDirectory);
                string fileSuffix = category switch
                {
                    LogCategory.OpenGL => "opengl",
                    LogCategory.Rendering => "rendering",
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

        private static LogCategory ClassifyMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return LogCategory.General;

            string normalized = message.ToLowerInvariant();

            foreach (var entry in OpenGlTokens)
            {
                if (ContainsToken(normalized, entry.Token, entry.RequireBoundary))
                    return LogCategory.OpenGL;
            }

            if (normalized.StartsWith("render"))
                return LogCategory.Rendering;

            foreach (var entry in RenderingTokens)
            {
                if (ContainsToken(normalized, entry.Token, entry.RequireBoundary))
                    return LogCategory.Rendering;
            }

            return LogCategory.General;
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
            foreach (LogCategory category in LogWriters.Keys.ToArray())
            {
                LogWriters[category]?.Dispose();
                LogWriters[category] = null;
            }

            _logSessionId = null;
        }

        private static void EnforceLogFileLimit(string logsDirectory)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(logsDirectory, "log_*.txt", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return;
            }

            if (files.Length < MaxLogFileCount)
                return;

            FileInfo? oldest = files
                .Select(path =>
                {
                    try
                    {
                        return new FileInfo(path);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(info => info is not null)
                .OrderBy(info => info!.CreationTimeUtc)
                .FirstOrDefault();

            if (oldest is null)
                return;

            try
            {
                oldest.Delete();
            }
            catch
            {
                // Ignore failures; directory permissions might block deletion.
            }
        }
    }
}
