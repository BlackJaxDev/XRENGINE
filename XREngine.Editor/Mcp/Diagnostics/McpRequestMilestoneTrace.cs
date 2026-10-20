using System.Diagnostics;
using System.Text.Json;

namespace XREngine.Editor.Mcp;

/// <summary>Keeps a fixed number of opt-in MCP request milestones.</summary>
internal sealed class McpRequestMilestoneTrace
{
    private const int Capacity = 512;
    private const int MaxIdentifierLength = 128;
    private static readonly object s_gate = new();
    private static readonly McpRequestMilestoneRecord[] s_records = new McpRequestMilestoneRecord[Capacity];
    private static int s_nextIndex;
    private static int s_count;

    internal static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("XRE_MCP_REQUEST_TRACE"), "1", StringComparison.Ordinal);

    private readonly long _requestId;
    private readonly long _startedAt;
    private readonly string _jsonRpcId;
    private readonly string _toolName;
    private readonly string? _typeName;
    private readonly string? _methodName;

    private McpRequestMilestoneTrace(long requestId, long startedAt,
        string jsonRpcId, string toolName, string? typeName, string? methodName)
    {
        _requestId = requestId;
        _startedAt = startedAt;
        _jsonRpcId = jsonRpcId;
        _toolName = toolName;
        _typeName = typeName;
        _methodName = methodName;
    }

    internal static McpRequestMilestoneTrace? TryCreate(long requestId, long startedAt, JsonElement root)
    {
        if (!Enabled || root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("method", out JsonElement rpcMethod) ||
            rpcMethod.ValueKind != JsonValueKind.String ||
            !string.Equals(rpcMethod.GetString(), "tools/call", StringComparison.Ordinal) ||
            !root.TryGetProperty("params", out JsonElement parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement tool) ||
            tool.ValueKind != JsonValueKind.String ||
            !string.Equals(tool.GetString(), "invoke_method", StringComparison.OrdinalIgnoreCase))
            return null;

        string jsonRpcId = root.TryGetProperty("id", out JsonElement id)
            ? Limit(id.ToString()) ?? "<null>" : "<notification>";
        string? typeName = null;
        string? methodName = null;
        if (parameters.TryGetProperty("arguments", out JsonElement arguments) &&
            arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("type_name", out JsonElement type) && type.ValueKind == JsonValueKind.String)
                typeName = Limit(type.GetString());
            if (arguments.TryGetProperty("method_name", out JsonElement method) && method.ValueKind == JsonValueKind.String)
                methodName = Limit(method.GetString());
        }

        return new McpRequestMilestoneTrace(requestId, startedAt, jsonRpcId,
            "invoke_method", typeName, methodName);
    }

    internal void Record(string milestone, string? detail = null)
    {
        var record = new McpRequestMilestoneRecord(
            _requestId, _jsonRpcId, _toolName, _typeName, _methodName,
            milestone, Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds,
            Environment.CurrentManagedThreadId, Limit(detail));
        lock (s_gate)
        {
            s_records[s_nextIndex] = record;
            s_nextIndex = (s_nextIndex + 1) % Capacity;
            if (s_count < Capacity)
                ++s_count;
        }
        try
        {
            XREngine.Debug.Out($"[MCP.Trace] req={record.RequestId} rpc={record.JsonRpcId} tool={record.ToolName} type={record.TypeName ?? "-"} method={record.MethodName ?? "-"} stage={record.Milestone} elapsed_ms={record.ElapsedMilliseconds:F3} thread={record.ManagedThreadId} detail={record.Detail ?? "-"}");
        }
        catch (Exception)
        {
            // Diagnostic logging must not change request completion.
        }
    }

    internal static McpRequestMilestoneRecord[] Snapshot()
    {
        lock (s_gate)
        {
            McpRequestMilestoneRecord[] result = new McpRequestMilestoneRecord[s_count];
            int start = (s_nextIndex - s_count + Capacity) % Capacity;
            for (int index = 0; index < s_count; ++index)
                result[index] = s_records[(start + index) % Capacity];
            return result;
        }
    }

    private static string? Limit(string? value)
    {
        if (value is null)
            return null;
        int length = Math.Min(value.Length, MaxIdentifierLength);
        bool replaceControl = false;
        for (int index = 0; index < length; ++index)
            if (char.IsControl(value[index]))
            {
                replaceControl = true;
                break;
            }
        if (!replaceControl)
            return value.Length == length ? value : value[..length];

        Span<char> clean = stackalloc char[MaxIdentifierLength];
        for (int index = 0; index < length; ++index)
            clean[index] = char.IsControl(value[index]) ? '_' : value[index];
        return new string(clean[..length]);
    }
}
