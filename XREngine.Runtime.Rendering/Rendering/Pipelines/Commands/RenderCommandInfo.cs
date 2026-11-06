using System.Collections.Concurrent;
using System.Reflection;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Resolves and caches the tool description of a render command type.
    /// </summary>
    public sealed class RenderCommandInfo
    {
        private static readonly ConcurrentDictionary<Type, RenderCommandInfo> Cache = new();
        private static readonly Func<Type, RenderCommandInfo> CreateInfo = static type => new RenderCommandInfo(type);

        private RenderCommandInfo(Type type)
        {
            CommandType = type;
            Attribute = type.GetCustomAttribute<RenderCommandInfoAttribute>(inherit: false);
            Category = Attribute?.Category ?? ERenderCommandCategory.Uncategorized;
            DisplayName = Attribute?.DisplayName ?? MakeDisplayName(type);
            Description = Attribute?.Description ?? string.Empty;
            DoesGpuWork = Attribute?.DoesGpuWork ?? false;
            ConsumesStages = Attribute?.ConsumesStages ?? [];
            IsStageConsumer = typeof(IRenderCommandStageConsumer).IsAssignableFrom(type);
        }

        /// <summary>Gets the command type.</summary>
        public Type CommandType { get; }

        /// <summary>Gets the attribute on the type, or null when the type has none.</summary>
        public RenderCommandInfoAttribute? Attribute { get; }

        /// <summary>Gets whether the type has a <see cref="RenderCommandInfoAttribute"/>.</summary>
        public bool HasAttribute => Attribute is not null;

        /// <summary>Gets the category of the command.</summary>
        public ERenderCommandCategory Category { get; }

        /// <summary>Gets the name that tools show.</summary>
        public string DisplayName { get; }

        /// <summary>Gets the description of the command, or an empty string.</summary>
        public string Description { get; }

        /// <summary>Gets whether the command records GPU work.</summary>
        public bool DoesGpuWork { get; }

        /// <summary>Gets the fixed stage keys that the command reads.</summary>
        public IReadOnlyList<string> ConsumesStages { get; }

        /// <summary>Gets whether the type implements <see cref="IRenderCommandStageConsumer"/>.</summary>
        public bool IsStageConsumer { get; }

        /// <summary>Returns the cached description of <paramref name="commandType"/>.</summary>
        public static RenderCommandInfo Get(Type commandType)
        {
            ArgumentNullException.ThrowIfNull(commandType);
            if (commandType.IsGenericType && !commandType.IsGenericTypeDefinition)
                commandType = commandType.GetGenericTypeDefinition();
            return Cache.GetOrAdd(commandType, CreateInfo);
        }

        /// <summary>Returns the cached description of the type of <paramref name="command"/>.</summary>
        public static RenderCommandInfo Get(ViewportRenderCommand command)
            => Get(command.GetType());

        /// <summary>
        /// Adds every stage key that <paramref name="command"/> reads: the fixed keys from the attribute,
        /// then the keys from <see cref="IRenderCommandStageConsumer"/>.
        /// </summary>
        public static void CollectConsumedStages(ViewportRenderCommand command, List<string> stageKeys)
        {
            RenderCommandInfo info = Get(command);
            for (int i = 0; i < info.ConsumesStages.Count; i++)
                stageKeys.Add(info.ConsumesStages[i]);
            if (command is IRenderCommandStageConsumer consumer)
                consumer.CollectConsumedStages(stageKeys);
        }

        private static string MakeDisplayName(Type type)
        {
            string name = type.Name;
            int genericMark = name.IndexOf('`');
            if (genericMark >= 0)
                name = name[..genericMark];
            return name.StartsWith("VPRC_", StringComparison.Ordinal) ? name[5..] : name;
        }
    }
}
