namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private sealed class ToolCallEntry
    {
        private string? _displayName;

        /// <summary>Shows the action and its target while Details retains the tool identifier.</summary>
        public string DisplayName => _displayName ??= string.IsNullOrEmpty(TargetLabel)
            ? ToolActionSummary.GetActionName(ToolName)
            : $"{ToolActionSummary.GetActionName(ToolName)}: {TargetLabel}";

        public required string ToolName { get; init; }
        public string ArgsSummary { get; init; } = string.Empty;
        public string ResultSummary { get; set; } = string.Empty;
        public string TargetLabel { get; private set; } = string.Empty;
        public string Category { get; private set; } = "Editor action";
        public string OutcomeSummary { get; private set; } = string.Empty;
        public string VerificationSummary { get; private set; } = string.Empty;
        public string? SourcePath { get; private set; }
        public int? SourceStartLine { get; private set; }
        public string? SourceHash { get; private set; }
        public string? SourceRevision { get; private set; }
        public string? SourceBinaryMatch { get; private set; }
        /// <summary>
        /// A compact summary of the tool result that preserves IDs (node IDs, asset IDs, etc.).
        /// Used by <see cref="BuildConversationContextBlock"/> to keep ID references available
        /// across continuation turns so the model doesn't need to re-query.
        /// </summary>
        public string ContextResultSummary { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public bool IsComplete { get; set; }
        /// <summary>Local file path returned by the tool (e.g. screenshot path), for UI rendering.</summary>
        public string? ResultFilePath { get; set; }

        /// <summary>Reads safe target fields once, before the tool call enters the chat.</summary>
        public void SetInvocation(string argumentsJson)
        {
            TargetLabel = ToolActionSummary.GetTargetLabel(argumentsJson);
            Category = ToolActionSummary.GetCategory(ToolName);
            _displayName = null;
        }

        /// <summary>Records what the tool reported without claiming a separate read-back.</summary>
        public void UpdateOutcome(string result)
        {
            OutcomeSummary = ToolActionSummary.GetOutcome(ToolName, result, IsError, ResultFilePath);
            VerificationSummary = ToolActionSummary.GetVerification(ToolName, IsError);
            string? sourcePath = null;
            int? sourceStartLine = null;
            string? sourceHash = null;
            string? sourceRevision = null;
            string? sourceBinaryMatch = null;
            if (!IsError)
                ToolActionSummary.ReadSourceReference(
                    ToolName, result, out sourcePath, out sourceStartLine,
                    out sourceHash, out sourceRevision, out sourceBinaryMatch);
            SourcePath = sourcePath;
            SourceStartLine = sourceStartLine;
            SourceHash = sourceHash;
            SourceRevision = sourceRevision;
            SourceBinaryMatch = sourceBinaryMatch;
        }
    }
}
