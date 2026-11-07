namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Reports the post-process schema stages that a command reads when the stages depend on its configuration.
    /// Commands with a fixed stage list use <see cref="RenderCommandInfoAttribute.ConsumesStages"/> instead.
    /// </summary>
    public interface IRenderCommandStageConsumer
    {
        /// <summary>
        /// Adds the stage keys that the command reads with its current configuration.
        /// </summary>
        /// <param name="stageKeys">The list that receives the stage keys. The method does not clear it.</param>
        void CollectConsumedStages(List<string> stageKeys);
    }
}
