namespace XREngine.Editor.Mcp
{
    public sealed class McpToolResponse
    {
        public McpToolResponse(string message, object? data = null, bool isError = false)
        {
            Message = message;
            Data = data;
            IsError = isError;
        }

        public string Message { get; }
        public object? Data { get; }
        public bool IsError { get; }
    }
}
