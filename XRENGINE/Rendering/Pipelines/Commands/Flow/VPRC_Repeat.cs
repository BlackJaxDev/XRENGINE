using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Executes a sub-command chain a fixed or dynamic number of times.
    /// Useful for iterative effects: ping-pong blur, multi-bounce GI approximation,
    /// or any multi-pass refinement technique.
    /// </summary>
    public class VPRC_Repeat : ViewportRenderCommand
    {
        /// <summary>
        /// Fixed iteration count when <see cref="CountProvider"/> is <c>null</c>.
        /// </summary>
        public int Count { get; set; } = 1;

        /// <summary>
        /// Optional dynamic iteration count evaluated each frame.
        /// When set, overrides <see cref="Count"/>.
        /// </summary>
        public Func<int>? CountProvider { get; set; }

        /// <summary>
        /// Called before each iteration with the zero-based iteration index.
        /// Use this to swap ping-pong targets, update uniforms, etc.
        /// </summary>
        public Action<int, ViewportRenderCommandContainer>? IterationConfigurator { get; set; }

        private ViewportRenderCommandContainer? _body;

        /// <summary>
        /// The sub-command chain executed each iteration.
        /// </summary>
        public ViewportRenderCommandContainer? Body
        {
            get => _body;
            set
            {
                _body = value;
                AttachPipeline(_body);
            }
        }

        protected override void Execute()
        {
            if (Body is null)
                return;

            int iterations = CountProvider?.Invoke() ?? Count;
            for (int i = 0; i < iterations; i++)
            {
                IterationConfigurator?.Invoke(i, Body);
                Body.Execute();
            }
        }

        internal override void OnAttachedToContainer()
        {
            base.OnAttachedToContainer();
            AttachPipeline(_body);
        }

        internal override void OnParentPipelineAssigned()
        {
            base.OnParentPipelineAssigned();
            AttachPipeline(_body);
        }

        private void AttachPipeline(ViewportRenderCommandContainer? container)
        {
            var pipeline = CommandContainer?.ParentPipeline;
            if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
                container.ParentPipeline = pipeline;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            Body?.BuildRenderPassMetadata(context);
        }
    }
}
