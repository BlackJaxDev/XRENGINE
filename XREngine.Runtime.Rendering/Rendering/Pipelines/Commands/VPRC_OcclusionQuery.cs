using XREngine.Rendering.Occlusion;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Wraps an authored command body in a hardware occlusion query and publishes the visibility result into pipeline variables.
    /// On unsupported backends, the command publishes the query as unavailable.
    /// </summary>
    [RenderPipelineScriptCommand]
    public sealed class VPRC_OcclusionQuery : ViewportRenderCommand
    {
        private readonly AsyncOcclusionQueryManager _queryManager = new();
        private XRRenderQuery? _pendingQuery;
        private bool _lastResultAvailable;
        private bool _lastAnySamplesPassed = true;
        private ViewportRenderCommandContainer? _body;

        public RenderQueryDescriptor QueryDescriptor { get; set; } = RenderQueryDescriptor.ConservativeOcclusion;
        public bool WaitForResult { get; set; }
        public string? ResultVariableName { get; set; }
        public string? ResultAvailableVariableName { get; set; }

        public ViewportRenderCommandContainer? Body
        {
            get => _body;
            set
            {
                _body = value;
                AttachPipeline(_body);
            }
        }

        public override bool NeedsCollecVisible => Body is not null;

        protected override void Execute()
        {
            ResolvePendingResult(wait: false);
            PublishResultVariables();

            if (Body is null)
                return;

            if (WaitForResult)
            {
                ExecuteSynchronousQuery();
                return;
            }

            if (_pendingQuery is not null)
                return;

            XRRenderQuery query = _queryManager.Acquire(QueryDescriptor);
            if (!TryExecuteQueryBody(query))
            {
                _queryManager.Release(query);
                PublishResultVariables();
                return;
            }

            _pendingQuery = query;
        }

        public override void CollectVisible()
            => Body?.CollectVisible();

        public override void SwapBuffers()
            => Body?.SwapBuffers();

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

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            Body?.BuildRenderPassMetadata(context);
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_pendingQuery is not null)
            {
                _queryManager.Release(_pendingQuery, pendingResult: true);
                _pendingQuery = null;
            }
        }

        private void ExecuteSynchronousQuery()
        {
            XRRenderQuery query = _queryManager.Acquire(QueryDescriptor);
            try
            {
                if (!TryExecuteQueryBody(query))
                {
                    PublishResultVariables();
                    return;
                }

                if (TryResolveQuery(query, wait: true, out bool anySamplesPassed))
                {
                    _lastResultAvailable = true;
                    _lastAnySamplesPassed = anySamplesPassed;
                }
                else
                {
                    _lastResultAvailable = false;
                }

                PublishResultVariables();
            }
            finally
            {
                _queryManager.Release(query);
            }
        }

        private void ResolvePendingResult(bool wait)
        {
            if (_pendingQuery is null)
                return;

            if (!TryResolveQuery(_pendingQuery, wait, out bool anySamplesPassed))
                return;

            _lastResultAvailable = true;
            _lastAnySamplesPassed = anySamplesPassed;
            _queryManager.Release(_pendingQuery);
            _pendingQuery = null;
        }

        private bool TryExecuteQueryBody(XRRenderQuery query)
        {
            if (AbstractRenderer.Current is not IRuntimeRendererHost renderer ||
                !renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out var capability) ||
                capability is null)
            {
                _lastResultAvailable = false;
                return false;
            }

            if (!capability.BeginOcclusionQuery(query))
                return false;
            try
            {
                Body?.Execute();
            }
            finally
            {
                capability.EndOcclusionQuery(query);
            }

            return true;
        }

        private static bool TryResolveQuery(XRRenderQuery query, bool wait, out bool anySamplesPassed)
        {
            anySamplesPassed = true;

            _ = wait; // Query resolution is deliberately nonblocking on render paths.
            if (AbstractRenderer.Current is not IRuntimeRendererHost renderer ||
                !renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out var capability) ||
                capability is null)
                return false;

            ERenderQueryReadStatus status = capability.TryGetAnySamplesPassed(query, out OcclusionQueryResult result);
            if (status != ERenderQueryReadStatus.Ready)
                return false;

            anySamplesPassed = result.AnySamplesPassed;
            return true;
        }

        private void PublishResultVariables()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (!string.IsNullOrWhiteSpace(ResultAvailableVariableName))
                instance.Variables.Set(ResultAvailableVariableName!, _lastResultAvailable);

            if (!string.IsNullOrWhiteSpace(ResultVariableName) && _lastResultAvailable)
                instance.Variables.Set(ResultVariableName!, _lastAnySamplesPassed);
        }

        private void AttachPipeline(ViewportRenderCommandContainer? container)
        {
            var pipeline = CommandContainer?.ParentPipeline;
            if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
                container.ParentPipeline = pipeline;
        }
    }
}
