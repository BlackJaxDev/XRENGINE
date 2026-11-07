namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>Fluent helpers that set <see cref="ViewportRenderCommand.AnchorId"/> while a chain is built.</summary>
    public static class ViewportRenderCommandAnchorExtensions
    {
        /// <summary>Sets the anchor id of the command and returns the command.</summary>
        public static T WithAnchor<T>(this T command, string anchorId) where T : ViewportRenderCommand
        {
            command.AnchorId = anchorId;
            return command;
        }
    }
}
