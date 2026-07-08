namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Base class for building a <see cref="RenderPipelineResourceSpec"/> with common properties and methods for configuring resource specifications.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the builder.</typeparam>
    /// <param name="owner">The owner of the resource layout builder.</param>
    /// <param name="name">The name of the resource specification.</param>
    public abstract class SpecBuilder<TBuilder>(RenderPipelineResourceLayoutBuilder owner, string name)
        where TBuilder : SpecBuilder<TBuilder>
    {
        private readonly List<string> _dependencies = [];
        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;
        private RenderResourceSizePolicy _sizePolicy = RenderResourceSizePolicy.Internal();
        private RenderPipelineResourceUsage _usage = RenderPipelineResourceUsage.None;
        private RenderPipelineResourcePredicate? _predicate;
        private RenderResourceHistoryPolicy _historyPolicy = RenderResourceHistoryPolicy.None;
        private string? _debugLabel;
        private bool _required = true;

        protected RenderPipelineResourceLayoutBuilder Owner { get; } = owner;
        protected string Name { get; } = name;
        protected RenderResourceLifetime LifetimeValue => _lifetime;
        protected RenderResourceSizePolicy SizePolicyValue => _sizePolicy;
        protected RenderPipelineResourceUsage UsageValue => _usage;
        protected IReadOnlyList<string> DependenciesValue => _dependencies.ToArray();
        protected RenderPipelineResourcePredicate? PredicateValue => _predicate;
        protected RenderResourceHistoryPolicy HistoryPolicyValue => _historyPolicy;
        protected string? DebugLabelValue => _debugLabel;
        protected bool RequiredValue => _required;

        /// <summary>
        /// Sets the lifetime of the resource specification.
        /// </summary>
        /// <param name="lifetime">The lifetime of the resource.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder Lifetime(RenderResourceLifetime lifetime)
        {
            _lifetime = lifetime;
            return (TBuilder)this;
        }

        /// <summary>
        /// Sets the size policy of the resource specification.
        /// </summary>
        /// <param name="sizePolicy">The size policy of the resource.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder Size(RenderResourceSizePolicy sizePolicy)
        {
            _sizePolicy = sizePolicy;
            return (TBuilder)this;
        }

        /// <summary>
        /// Sets the usage of the resource specification.
        /// </summary>
        /// <param name="usage">The usage of the resource.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder Usage(RenderPipelineResourceUsage usage)
        {
            _usage = usage;
            return (TBuilder)this;
        }

        /// <summary>
        /// Specifies the dependencies of the resource specification.
        /// </summary>
        /// <param name="dependencies">The names of the resources this resource depends on.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder DependsOn(params string[] dependencies)
        {
            if (dependencies is null)
                return (TBuilder)this;

            for (int i = 0; i < dependencies.Length; i++)
                if (!string.IsNullOrWhiteSpace(dependencies[i]))
                    _dependencies.Add(dependencies[i]);

            return (TBuilder)this;
        }

        /// <summary>
        /// Sets a predicate that determines whether the resource specification is enabled based on the resource profile.
        /// </summary>
        /// <param name="predicate">The predicate that determines whether the resource is enabled.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder When(RenderPipelineResourcePredicate predicate)
        {
            _predicate = predicate;
            return (TBuilder)this;
        }

        /// <summary>
        /// Sets the history policy of the resource specification.
        /// </summary>
        /// <param name="historyPolicy">The history policy of the resource.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder History(RenderResourceHistoryPolicy historyPolicy)
        {
            _historyPolicy = historyPolicy;
            return (TBuilder)this;
        }

        /// <summary>
        /// Sets the debug label of the resource specification.
        /// </summary>
        /// <param name="debugLabel">The debug label of the resource.</param>
        /// <returns>The builder instance.</returns>
        public TBuilder DebugLabel(string debugLabel)
        {
            _debugLabel = debugLabel;
            return (TBuilder)this;
        }

        /// <summary>
        /// Marks the resource specification as optional.
        /// </summary>
        /// <returns>The builder instance.</returns>
        public TBuilder Optional()
        {
            _required = false;
            return (TBuilder)this;
        }
    }
}
