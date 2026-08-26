using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

internal sealed partial class RepositoryAgentToolProvider
{
    private AgentToolResult Read(JsonElement arguments)
    {
        string path = RequiredString(arguments, "path");
        int startLine = OptionalInt32(arguments, "start_line", 1, 1, int.MaxValue);
        int lineCount = OptionalInt32(arguments, "line_count", 200, 1, MaximumReadLines);
        string? expectedSha256 = OptionalString(arguments, "expected_sha256");
        string fullPath = _pathPolicy.ResolveTextFile(path, _allowedRoots);
        string relativePath = _pathPolicy.ToRelativePath(fullPath);
        int requestedEnd = startLine > int.MaxValue - lineCount + 1
            ? int.MaxValue
            : startLine + lineCount - 1;
        AgentContextFileSnapshot snapshot = _reader.Read(
            fullPath,
            relativePath,
            MaximumReadableFileBytes,
            startLine,
            requestedEnd,
            expectedSha256);

        bool rangeTruncated = snapshot.EndLine < snapshot.TotalLines;
        var payload = new JsonObject
        {
            ["path"] = snapshot.Path,
            ["sha256"] = snapshot.Sha256,
            ["raw_byte_length"] = snapshot.RawByteLength,
            ["start_line"] = snapshot.StartLine,
            ["end_line"] = snapshot.EndLine,
            ["total_lines"] = snapshot.TotalLines,
            ["content"] = snapshot.Content,
            ["truncated"] = rangeTruncated,
            ["next_start_line"] = snapshot.EndLine < snapshot.TotalLines
                ? snapshot.EndLine + 1
                : null,
        };
        return BoundJsonPayload(payload);
    }

    private AgentToolResult BoundJsonPayload(JsonObject payload)
    {
        string json = payload.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) <= _maxToolResultBytes)
            return new AgentToolResult { Content = json };

        if (payload["matches"] is JsonArray matches)
        {
            while (matches.Count > 0)
            {
                matches.RemoveAt(matches.Count - 1);
                payload["truncated"] = true;
                payload["truncation_reason"] = "tool_result_byte_limit";
                json = payload.ToJsonString();
                if (Encoding.UTF8.GetByteCount(json) <= _maxToolResultBytes)
                    return new AgentToolResult { Content = json, IsTruncated = true };
            }
        }

        if (payload["content"] is JsonValue contentValue
            && contentValue.TryGetValue<string>(out string? content)
            && content is not null)
        {
            int low = 0;
            int high = content.Length;
            while (low < high)
            {
                int midpoint = low + ((high - low + 1) / 2);
                payload["content"] = content[..midpoint];
                if (Encoding.UTF8.GetByteCount(payload.ToJsonString()) <= _maxToolResultBytes)
                    low = midpoint;
                else
                    high = midpoint - 1;
            }
            if (low > 0 && low < content.Length && char.IsHighSurrogate(content[low - 1]))
                low--;
            string boundedContent = content[..low];
            int lastCompleteLine = boundedContent.LastIndexOf('\n');
            if (lastCompleteLine < 0 && low < content.Length)
            {
                return new AgentToolResult
                {
                    Content = "A selected repository line exceeds the configured tool-result byte limit.",
                    IsError = true,
                    IsTruncated = true,
                };
            }
            if (lastCompleteLine >= 0 && low < content.Length)
                boundedContent = boundedContent[..lastCompleteLine];
            payload["content"] = boundedContent;
            payload["truncated"] = true;
            payload["truncation_reason"] = "tool_result_byte_limit";
            if (payload["start_line"] is JsonValue startLineValue
                && startLineValue.TryGetValue<int>(out int startLine))
            {
                int returnedLines = boundedContent.Length == 0
                    ? 0
                    : boundedContent.Count(static character => character == '\n') + 1;
                int endLine = returnedLines == 0 ? startLine - 1 : startLine + returnedLines - 1;
                payload["end_line"] = endLine;
                payload["next_start_line"] = Math.Max(startLine, endLine + 1);
            }
            json = payload.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) <= _maxToolResultBytes)
                return new AgentToolResult { Content = json, IsTruncated = true };
        }

        return new AgentToolResult
        {
            Content = "Repository tool output exceeded the configured byte limit.",
            IsError = true,
            IsTruncated = true,
        };
    }
}
