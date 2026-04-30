namespace XREngine.Rendering.Pipelines.Commands
{
    public abstract class ViewportStateRenderCommandBase : ViewportRenderCommand
    {
        public abstract StateObject GetUsingState();
    }
    public abstract class ViewportStateRenderCommand<T> : ViewportStateRenderCommandBase where T : ViewportPopStateRenderCommand, new()
    {
        public T PopCommand { get; } = new T();

        /// <summary>
        /// For use with the using statement. At the end of the using statement, a command will be added to the pipeline to pop the state.
        /// </summary>
        /// <returns></returns>
        public override StateObject GetUsingState() 
            => StateObject.New(() => CommandContainer?.Add(PopCommand));
    }
}
