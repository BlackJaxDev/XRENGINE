namespace XREngine.Editor.Mcp
{
    /// <summary>
    /// Represents the response from an MCP tool invocation.
    /// </summary>
    public sealed class McpToolResponse
    {
        /// <summary>
        /// Creates a new MCP tool response.
        /// </summary>
        /// <param name="message">A human-readable message describing the result.</param>
        /// <param name="data">Optional structured data to return (will be JSON-serialized).</param>
        /// <param name="isError">True if the operation failed.</param>
        public McpToolResponse(string message, object? data = null, bool isError = false)
        {
            Message = message;
            Data = data;
            IsError = isError;
        }

        /// <summary>
        /// A human-readable message describing the result of the tool invocation.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Optional structured data returned by the tool. Will be JSON-serialized in the response.
        /// </summary>
        public object? Data { get; }

        /// <summary>
        /// True if the tool invocation encountered an error.
        /// </summary>
        public bool IsError { get; }
    }
}
