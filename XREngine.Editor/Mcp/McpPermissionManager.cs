using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{

    /// <summary>
    /// Represents a pending permission request shown to the user.
    /// </summary>
    public sealed class McpPermissionRequest
    {
        public required string ToolName { get; init; }
        public required string ToolDescription { get; init; }
        public required McpPermissionLevel PermissionLevel { get; init; }
        public string? PermissionReason { get; init; }

        /// <summary>
        /// A human-readable summary of the arguments being passed to the tool.
        /// </summary>
        public string ArgumentsSummary { get; init; } = string.Empty;

        /// <summary>
        /// The source of the request (e.g., "MCP Server", "MCP Assistant").
        /// </summary>
        public string Source { get; init; } = "MCP";

        /// <summary>
        /// Timestamp when the request was created.
        /// </summary>
        public DateTime Timestamp { get; } = DateTime.Now;

        /// <summary>
        /// Completion source that the calling async code awaits.
        /// True = approved, False = denied.
        /// </summary>
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Whether the user checked "Remember for this tool" in the prompt.
        /// Set by the UI before completing the request.
        /// </summary>
        public bool RememberChoice { get; set; }
    }

    /// <summary>
    /// Centralized permission gate for MCP tool invocations.
    /// <para>
    /// When a tool's <see cref="McpPermissionLevel"/> exceeds the user's configured
    /// auto-approve threshold (<see cref="McpPermissionPolicy"/>), the manager enqueues
    /// a <see cref="McpPermissionRequest"/> and blocks the calling async context until
    /// the user approves or denies via the ImGui permission prompt.
    /// </para>
    /// </summary>
    public sealed class McpPermissionManager
    {
        private static McpPermissionManager? _instance;
        private static readonly object _lock = new();

        /// <summary>Singleton instance.</summary>
        public static McpPermissionManager Instance
        {
            get
            {
                if (_instance is null)
                {
                    lock (_lock)
                        _instance ??= new McpPermissionManager();
                }
                return _instance;
            }
        }

        private McpPermissionManager() { }

        /// <summary>
        /// Queue of requests waiting for user approval.
        /// The ImGui render loop dequeues and displays these.
        /// </summary>
        private readonly ConcurrentQueue<McpPermissionRequest> _pendingRequests = new();

        /// <summary>
        /// Per-tool remembered decisions (tool name → approved).
        /// Cleared on editor restart or when the user resets.
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _rememberedDecisions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The currently displayed request (set by the UI during rendering).
        /// </summary>
        public McpPermissionRequest? ActiveRequest { get; private set; }

        /// <summary>
        /// Whether there are pending requests waiting for user attention.
        /// </summary>
        public bool HasPendingRequests => !_pendingRequests.IsEmpty || ActiveRequest is not null;

        /// <summary>
        /// Determines whether a tool requires permission and, if so, waits for user approval.
        /// Returns true if the tool is allowed to execute, false if denied.
        /// </summary>
        /// <param name="tool">The tool definition to check.</param>
        /// <param name="policy">The current permission policy threshold.</param>
        /// <param name="argumentsJson">The JSON arguments being passed, for display in the prompt.</param>
        /// <param name="source">Source label (e.g., "MCP Server", "MCP Assistant").</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if approved, false if denied or canceled.</returns>
        public async Task<bool> RequestPermissionAsync(
            McpToolDefinition tool,
            McpPermissionPolicy policy,
            JsonElement argumentsJson,
            string source,
            CancellationToken token)
        {
            // Auto-approve if the tool's level is at or below the policy threshold.
            if (IsAutoApproved(tool.PermissionLevel, policy))
                return true;

            // Check remembered per-tool decisions.
            if (_rememberedDecisions.TryGetValue(tool.Name, out bool remembered))
                return remembered;

            // Build a human-readable argument summary.
            string argsSummary = SummarizeArguments(argumentsJson);

            var request = new McpPermissionRequest
            {
                ToolName = tool.Name,
                ToolDescription = tool.Description,
                PermissionLevel = tool.PermissionLevel,
                PermissionReason = tool.PermissionReason,
                ArgumentsSummary = argsSummary,
                Source = source,
            };

            _pendingRequests.Enqueue(request);

            // Wait for the ImGui render loop to process this request.
            try
            {
                using var registration = token.Register(() => request.Completion.TrySetResult(false));
                bool approved = await request.Completion.Task;

                // If the user checked "Remember", store the decision.
                if (request.RememberChoice)
                    _rememberedDecisions[tool.Name] = approved;

                return approved;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Called by the ImGui render loop to get the next request to display.
        /// Returns null if there are no pending requests.
        /// </summary>
        public McpPermissionRequest? DequeueNextRequest()
        {
            if (ActiveRequest is not null)
                return ActiveRequest; // Still displaying the current one.

            if (_pendingRequests.TryDequeue(out var request))
            {
                ActiveRequest = request;
                return request;
            }

            return null;
        }

        /// <summary>
        /// Called by the ImGui render loop when the user approves the active request.
        /// </summary>
        public void ApproveActiveRequest()
        {
            if (ActiveRequest is null)
                return;

            ActiveRequest.Completion.TrySetResult(true);
            ActiveRequest = null;
        }

        /// <summary>
        /// Called by the ImGui render loop when the user denies the active request.
        /// </summary>
        public void DenyActiveRequest()
        {
            if (ActiveRequest is null)
                return;

            ActiveRequest.Completion.TrySetResult(false);
            ActiveRequest = null;
        }

        /// <summary>
        /// Clears all remembered per-tool decisions.
        /// </summary>
        public void ClearRememberedDecisions()
            => _rememberedDecisions.Clear();

        /// <summary>
        /// Denies all pending requests and clears the queue.
        /// Useful when stopping the MCP server or shutting down.
        /// </summary>
        public void DenyAllPending()
        {
            if (ActiveRequest is not null)
            {
                ActiveRequest.Completion.TrySetResult(false);
                ActiveRequest = null;
            }

            while (_pendingRequests.TryDequeue(out var request))
                request.Completion.TrySetResult(false);
        }

        /// <summary>
        /// Returns true if the given permission level is auto-approved by the policy.
        /// </summary>
        public static bool IsAutoApproved(McpPermissionLevel level, McpPermissionPolicy policy)
        {
            return policy switch
            {
                McpPermissionPolicy.AlwaysAsk => false,
                McpPermissionPolicy.AllowReadOnly => level <= McpPermissionLevel.ReadOnly,
                McpPermissionPolicy.AllowMutate => level <= McpPermissionLevel.Mutate,
                McpPermissionPolicy.AllowDestructive => level <= McpPermissionLevel.Destructive,
                McpPermissionPolicy.AllowAll => true,
                _ => false,
            };
        }

        private static string SummarizeArguments(JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var parts = new System.Collections.Generic.List<string>();
            foreach (var prop in args.EnumerateObject())
            {
                string value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => $"\"{Truncate(prop.Value.GetString() ?? "", 80)}\"",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "null",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => Truncate(prop.Value.GetRawText(), 80),
                };
                parts.Add($"{prop.Name}: {value}");
            }

            return string.Join(", ", parts);
        }

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}
