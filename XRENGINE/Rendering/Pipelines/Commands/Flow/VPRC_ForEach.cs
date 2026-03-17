using System.Collections.Generic;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Iterates over a dynamic collection and executes a sub-command chain once per element.
    /// The <see cref="ElementConfigurator"/> callback is invoked before each iteration so the
    /// sub-chain can be parameterized per element (e.g., bind a different shadow map face,
    /// update a uniform, or swap a render target).
    /// </summary>
    /// <typeparam name="T">The element type yielded by the collection.</typeparam>
    public class VPRC_ForEach<T> : ViewportRenderCommand
    {
        /// <summary>
        /// Supplies the collection to iterate over each frame.
        /// Called once per <see cref="Execute"/> invocation.
        /// </summary>
        public Func<IEnumerable<T>>? CollectionProvider { get; set; }

        /// <summary>
        /// Called before executing the sub-chain for each element.
        /// Use this to configure per-element state (bind FBO face, set uniforms, etc.).
        /// </summary>
        public Action<T, int, ViewportRenderCommandContainer>? ElementConfigurator { get; set; }

        private ViewportRenderCommandContainer? _body;

        /// <summary>
        /// The sub-command chain executed for every element.
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
            if (CollectionProvider is null || Body is null)
                return;

            int index = 0;
            foreach (T element in CollectionProvider())
            {
                ElementConfigurator?.Invoke(element, index, Body);
                Body.Execute();
                index++;
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
