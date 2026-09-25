using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Builds a bounded, path-sanitized exception description for MCP error responses.
/// </summary>
/// <remarks>
/// Release builds compile out the textual <c>Debug</c> error sinks, so a tool that only
/// logged its exception would leave callers with a generic failure. The description keeps
/// exception types, trimmed messages and the innermost method frames (no file names or line
/// numbers) so the responsible member can be identified without exposing local paths.
/// </remarks>
internal static partial class McpExceptionDiagnostics
{
    private const int MaxChainLength = 6;
    private const int MaxMessageLength = 512;
    private const int MaxFrames = 24;

    /// <summary>
    /// Describes <paramref name="exception"/> and its inner exceptions for an MCP response.
    /// </summary>
    public static object Describe(Exception exception)
    {
        List<object> chain = [];
        Exception? innermost = exception;
        for (Exception? current = exception; current is not null && chain.Count < MaxChainLength; current = Next(current))
        {
            chain.Add(new
            {
                type = current.GetType().FullName,
                message = Sanitize(current.Message),
            });
            innermost = current;
        }

        return new
        {
            chain,
            deepestReportedFrames = DescribeFrames(innermost!),
            truncatedChain = innermost is not null && Next(innermost) is not null,
        };
    }

    private static Exception? Next(Exception exception)
        => exception is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception.InnerException;

    private static List<string> DescribeFrames(Exception exception)
    {
        List<string> frames = [];
        StackFrame[] stackFrames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        for (int i = 0; i < stackFrames.Length && frames.Count < MaxFrames; i++)
        {
            MethodBase? method = stackFrames[i].GetMethod();
            if (method is null)
                continue;

            string declaringType = method.DeclaringType?.FullName ?? "<unknown>";
            frames.Add($"{declaringType}.{method.Name}");
        }

        return frames;
    }

    private static string Sanitize(string message)
    {
        string sanitized = RootedPathPattern().Replace(message, "<path>");
        return sanitized.Length <= MaxMessageLength
            ? sanitized
            : string.Concat(sanitized.AsSpan(0, MaxMessageLength), "...");
    }

    [GeneratedRegex(@"([A-Za-z]:[\\/]|\\\\|/(?:Users|home)/)[^\s'""<>|]*")]
    private static partial Regex RootedPathPattern();
}
