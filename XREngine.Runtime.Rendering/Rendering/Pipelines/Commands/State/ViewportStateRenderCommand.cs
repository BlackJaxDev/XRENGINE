namespace XREngine.Rendering.Pipelines.Commands
{
    public abstract class ViewportStateRenderCommandBase : ViewportRenderCommand
    {
        public abstract StateObject GetUsingState();

        /// <summary>
        /// Gets the command that restores the state that this command pushes. Tools use it to show the scope of the state.
        /// The pop command is in the same container only when the pipeline used <see cref="GetUsingState"/>.
        /// </summary>
        public abstract ViewportPopStateRenderCommand PairedPopCommand { get; }
    }
    public abstract class ViewportStateRenderCommand<T> : ViewportStateRenderCommandBase where T : ViewportPopStateRenderCommand, new()
    {
        public T PopCommand { get; } = new T();

        /// <inheritdoc />
        public override ViewportPopStateRenderCommand PairedPopCommand => PopCommand;

        /// <summary>
        /// For use with the using statement. At the end of the using statement, a command will be added to the pipeline to pop the state.
        /// </summary>
        /// <returns></returns>
        public override StateObject GetUsingState() 
            => StateObject.New(() => CommandContainer?.Add(PopCommand));
    }
}
