namespace XREngine.Rendering.Pipelines.Commands
{

    /// <summary>
    /// Runs a method to manually apply render state changes.
    /// </summary>
    /// <param name="pipeline"></param>
    public class VPRC_Manual : ViewportRenderCommand
    {
        public Action? ManualAction { get; set; }
        protected override void Execute()
        {
            ManualAction?.Invoke();
        }
    }
}
