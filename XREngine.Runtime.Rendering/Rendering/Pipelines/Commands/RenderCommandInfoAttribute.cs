namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Describes a render pipeline command for tools: its category, display text, and whether it records GPU work.
    /// Apply it to each concrete <see cref="ViewportRenderCommand"/> type.
    /// </summary>
    /// <param name="category">The palette and outline group of the command.</param>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class RenderCommandInfoAttribute(ERenderCommandCategory category) : Attribute
    {
        /// <summary>Gets the palette and outline group of the command.</summary>
        public ERenderCommandCategory Category { get; } = category;

        /// <summary>Gets or sets the name that tools show. When null, tools use the type name without the <c>VPRC_</c> prefix.</summary>
        public string? DisplayName { get; set; }

        /// <summary>Gets or sets one sentence that tells what the command does.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets whether the command records GPU work such as draws, dispatches, copies, or clears.
        /// A command that does GPU work must declare at least one render pass, or validation reports a problem.
        /// </summary>
        public bool DoesGpuWork { get; set; }

        /// <summary>
        /// Gets or sets the post-process schema stage keys that the command reads. Use
        /// <see cref="IRenderCommandStageConsumer"/> when the stage depends on the configuration of the command.
        /// </summary>
        public string[]? ConsumesStages { get; set; }
    }
}
