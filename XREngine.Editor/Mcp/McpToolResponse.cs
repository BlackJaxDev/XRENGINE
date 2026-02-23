namespace XREngine.Editor.Mcp
{
    /// <summary>
    /// Represents the response from an MCP tool invocation.
    /// </summary>
    /// <remarks>
    /// Creates a new MCP tool response.
    /// </remarks>
    /// <param name="message">A human-readable message describing the result.</param>
    /// <param name="data">Optional structured data to return (will be JSON-serialized).</param>
    /// <param name="isError">True if the operation failed.</param>
    public sealed class McpToolResponse(string message, object? data = null, bool isError = false)
    {
        /// <summary>
        /// A human-readable message describing the result of the tool invocation.
        /// </summary>
        public string Message { get; } = message;

        /// <summary>
        /// Optional structured data returned by the tool. Will be JSON-serialized in the response.
        /// </summary>
        public object? Data { get; } = data;

        /// <summary>
        /// True if the tool invocation encountered an error.
        /// </summary>
        public bool IsError { get; } = isError;
    }
}
