using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// A container for a list of viewport render commands that can be executed in sequence.
    /// This class manages the lifecycle of its commands, 
    /// including resource allocation and disposal, 
    /// and provides methods for adding, removing, and executing commands.
    /// </summary>
    public class ViewportRenderCommandContainer : XRBase, IReadOnlyList<ViewportRenderCommand>
    {
        private static readonly object FactorySync = new();
        private static readonly Dictionary<Type, Func<ViewportRenderCommand>> CommandFactories = [];
        private static int _structureNotificationSuppressionDepth;

        /// <summary>
        /// Gets the list of registered command types that can be created by this container.
        /// This list is read-only and reflects the types that have been registered with the container's factory system.
        /// New command types can be registered using the RegisterCommandFactory method.
        /// </summary>
        public static Type[] RegisteredCommandTypes
        {
            get
            {
                lock (FactorySync)
                    return [.. CommandFactories.Keys];
            }
        }

        /// <summary>
        /// Registers a factory method for creating instances of a specific viewport render command type.
        /// This allows the container to create instances of the command type when adding commands by type.
        /// </summary>
        /// <typeparam name="TCommand">The type of the viewport render command.</typeparam>
        /// <param name="factory">The factory method for creating instances of the command type.</param>
        public static void RegisterCommandFactory<TCommand>(Func<TCommand>? factory = null)
            where TCommand : ViewportRenderCommand, new() 
            => RegisterCommandFactory(
                typeof(TCommand),
                factory is null
                    ? static () => new TCommand()
                    : () => factory());

        /// <summary>
        /// Registers a factory method for creating instances of a specific viewport render command type.
        /// This allows the container to create instances of the command type when adding commands by type.
        /// </summary>
        /// <param name="commandType">The type of the viewport render command.</param>
        /// <param name="factory">The factory method for creating instances of the command type.</param>
        /// <exception cref="ArgumentException">Thrown if the commandType does not derive from ViewportRenderCommand.</exception>
        public static void RegisterCommandFactory(Type commandType, Func<ViewportRenderCommand> factory)
        {
            ArgumentNullException.ThrowIfNull(commandType);
            ArgumentNullException.ThrowIfNull(factory);

            if (!typeof(ViewportRenderCommand).IsAssignableFrom(commandType))
                throw new ArgumentException($"Type must derive from {nameof(ViewportRenderCommand)}.", nameof(commandType));

            lock (FactorySync)
                CommandFactories[commandType] = factory;
        }

        /// <summary>
        /// Attempts to create an instance of a registered viewport render command type using the registered factory method.
        /// </summary>
        /// <param name="commandType">The type of the viewport render command.</param>
        /// <param name="command">The created instance of the viewport render command, or null if creation failed.</param>
        /// <returns>True if the command was successfully created; otherwise, false.</returns>
        public static bool TryCreateRegisteredCommand(Type commandType, out ViewportRenderCommand? command)
        {
            ArgumentNullException.ThrowIfNull(commandType);

            Func<ViewportRenderCommand>? factory;
            lock (FactorySync)
                CommandFactories.TryGetValue(commandType, out factory);

            command = factory?.Invoke();
            return command is not null;
        }

        internal static IDisposable SuppressStructureChangeNotifications()
        {
            Interlocked.Increment(ref _structureNotificationSuppressionDepth);
            return StructureNotificationSuppressionScope.Instance;
        }

        private static bool StructureChangeNotificationsSuppressed
            => Volatile.Read(ref _structureNotificationSuppressionDepth) > 0;

        private sealed class StructureNotificationSuppressionScope : IDisposable
        {
            public static readonly StructureNotificationSuppressionScope Instance = new();

            public void Dispose()
                => Interlocked.Decrement(ref _structureNotificationSuppressionDepth);
        }

        /// <summary>
        /// Defines the behavior of resources allocated for this command container when the pipeline branch is deselected.
        /// </summary>
        public enum BranchResourceBehavior
        {
            /// <summary>
            /// Preserve resources allocated for this command container when the pipeline branch is deselected. 
            /// Resources will be reused if the branch is reselected.
            /// </summary>
            PreserveResources,
            /// <summary>
            /// Dispose resources allocated for this command container when the pipeline branch is deselected.
            /// </summary>
            DisposeResourcesOnBranchExit
        }

        private readonly List<ViewportRenderCommand> _commands = [];
        /// <summary>
        /// Gets the list of commands in this container. 
        /// This list is read-only and reflects the current state of the container.
        /// Commands can be added or removed using the Add, Remove, and Insert methods.
        /// </summary>
        public IReadOnlyList<ViewportRenderCommand> Commands => _commands;

        private readonly List<ViewportRenderCommand> _collectVisibleCommands = [];
        /// <summary>
        /// Gets the list of commands in this container that require visibility collection.
        /// This list is read-only and reflects the current state of the container.
        /// </summary>
        public IReadOnlyList<ViewportRenderCommand> CollectVisibleCommands => _collectVisibleCommands;

        /// <summary>
        /// Represents the state of resources allocated for a specific pipeline instance.
        /// </summary>
        private sealed class InstanceResourceState
        {
            /// <summary>
            /// Indicates whether resources for this command container have been allocated for the associated pipeline instance.
            /// If true, resources have been allocated and are valid for use. If false, resources need to be allocated.
            /// </summary>
            public bool ResourcesAllocated;
            /// <summary>
            /// Indicates the resource generation of the associated pipeline instance at the time resources were allocated for this command container.
            /// This is used to determine if resources need to be reallocated due to changes in the pipeline instance.
            /// </summary>
            public int AllocatedAtGeneration;
        }

        private readonly Dictionary<XRRenderPipelineInstance, InstanceResourceState> _instanceStates = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Gets or sets the behavior for branching resources in this command container.
        /// </summary>
        public BranchResourceBehavior BranchResources { get; set; } = BranchResourceBehavior.PreserveResources;

        private RenderPipeline? _parentPipeline;
        /// <summary>
        /// Gets or sets the parent render pipeline that owns this command container.
        /// When set, all commands in this container will be notified of the parent pipeline assignment.
        /// </summary>
        [YamlIgnore]
        public RenderPipeline? ParentPipeline
        {
            get => _parentPipeline;
            internal set
            {
                if (ReferenceEquals(_parentPipeline, value))
                    return;

                _parentPipeline = value;

                if (_parentPipeline is not null)
                {
                    for (int i = 0; i < _commands.Count; i++)
                        _commands[i].OnParentPipelineAssigned();
                }
            }
        }

        public ViewportRenderCommandContainer()
            : this(null)
        {
        }

        public ViewportRenderCommandContainer(RenderPipeline? parentPipeline)
        {
            ParentPipeline = parentPipeline;
        }

        //public bool FBOsInitialized { get; private set; } = false;
        //public bool ModifyingFBOs { get; protected set; } = false;

        public int Count => Commands.Count;

        public ViewportRenderCommand this[int index] => Commands[index];

        /// <summary>
        /// Adds a command that pushes a new state and pops it later with another command when the using block ends.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="setOptionsFunc"></param>
        /// <returns></returns>
        public StateObject AddUsing<T>(Action<T>? setOptionsFunc = null) where T : ViewportStateRenderCommandBase, new()
        {
            T cmd = Add<T>();
            setOptionsFunc?.Invoke(cmd);
            return cmd.GetUsingState();
        }
        /// <summary>
        /// Adds a command that pushes a new state and pops it later with another command when the using block ends.
        /// </summary>
        /// <param name="t"></param>
        /// <param name="setOptionsFunc"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public StateObject AddUsing([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type t, Action<ViewportStateRenderCommandBase>? setOptionsFunc = null)
        {
            if (!typeof(ViewportStateRenderCommandBase).IsAssignableFrom(t))
                throw new ArgumentException("Type must be a subclass of ViewportStateRenderCommand.", nameof(t));

            var cmd = (ViewportStateRenderCommandBase)Add(t);
            setOptionsFunc?.Invoke(cmd);
            return cmd.GetUsingState();
        }
        /// <summary>
        /// Adds a command that pushes a new state and pops it later with another command when the using block ends.
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public StateObject AddUsing(ViewportStateRenderCommandBase cmd)
        {
            Add(cmd);
            return cmd.GetUsingState();
        }
        /// <summary>
        /// Adds a command to the viewport render command list.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T Add<T>() where T : ViewportRenderCommand, new()
        {
            //Create instance with this as the only parameter
            T cmd = new();
            Add(cmd);
            return cmd;
        }
        /// <summary>
        /// Adds a command to the viewport render command list.
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public ViewportRenderCommand Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type t)
        {
            if (!typeof(ViewportRenderCommand).IsAssignableFrom(t))
                throw new ArgumentException("Type must be a subclass of ViewportRenderCommand.", nameof(t));

            if (!TryCreateRegisteredCommand(t, out ViewportRenderCommand? cmd) || cmd is null)
            {
                if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                    throw new InvalidOperationException($"No registered viewport render command factory for type {t.FullName}.");

                cmd = Activator.CreateInstance(t) as ViewportRenderCommand ?? throw new ArgumentException("Type must have a public parameterless constructor.", nameof(t));
            }

            Add(cmd);
            return cmd;
        }
        public ViewportRenderCommand Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type t, params object[] arguments)
        {
            if (!typeof(ViewportRenderCommand).IsAssignableFrom(t))
                throw new ArgumentException("Type must be a subclass of ViewportRenderCommand.", nameof(t));

            ViewportRenderCommand? cmd = null;
            if (arguments.Length == 0)
                TryCreateRegisteredCommand(t, out cmd);

            if (cmd is null)
            {
                if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                    throw new InvalidOperationException($"No registered argument-aware viewport render command factory for type {t.FullName}.");

                cmd = Activator.CreateInstance(t, arguments) as ViewportRenderCommand
                    ?? throw new ArgumentException("Type must have a public constructor with the specified arguments.", nameof(t));
            }

            Add(cmd);
            return cmd;
        }
        /// <summary>
        /// Adds a command to the viewport render command list.
        /// </summary>
        /// <param name="cmd"></param>
        public void Add(ViewportRenderCommand cmd)
        {
            AttachCommand(cmd, _commands.Count, notifyStructureChanged: true);
        }

        public void Insert(int index, ViewportRenderCommand cmd)
        {
            int clampedIndex = Math.Clamp(index, 0, _commands.Count);
            AttachCommand(cmd, clampedIndex, notifyStructureChanged: true);
        }

        public int IndexOf(ViewportRenderCommand cmd)
            => _commands.IndexOf(cmd);

        public bool Remove(ViewportRenderCommand cmd)
        {
            int index = _commands.IndexOf(cmd);
            if (index < 0)
                return false;

            RemoveAt(index);
            return true;
        }

        public ViewportRenderCommand RemoveAt(int index)
        {
            ViewportRenderCommand cmd = _commands[index];
            _commands.RemoveAt(index);
            cmd.CommandContainer = null;
            RebuildCollectVisibleCommands();
            NotifyStructureChanged();
            return cmd;
        }

        public ViewportRenderCommand ReplaceAt(int index, ViewportRenderCommand cmd)
        {
            ArgumentNullException.ThrowIfNull(cmd);

            ViewportRenderCommand previous = _commands[index];
            if (ReferenceEquals(previous, cmd))
                return previous;

            if (cmd.CommandContainer is not null && !ReferenceEquals(cmd.CommandContainer, this))
                throw new InvalidOperationException("Command is already attached to a different command container.");

            previous.CommandContainer = null;
            _commands[index] = cmd;
            cmd.CommandContainer = this;
            cmd.OnAttachedToContainer();
            if (_parentPipeline is not null)
                cmd.OnParentPipelineAssigned();

            RebuildCollectVisibleCommands();
            NotifyStructureChanged();
            return previous;
        }

        public void Move(int fromIndex, int toIndex)
        {
            if (_commands.Count <= 1)
                return;

            int sourceIndex = Math.Clamp(fromIndex, 0, _commands.Count - 1);
            int targetIndex = Math.Clamp(toIndex, 0, _commands.Count - 1);
            if (sourceIndex == targetIndex)
                return;

            ViewportRenderCommand cmd = _commands[sourceIndex];
            _commands.RemoveAt(sourceIndex);
            _commands.Insert(targetIndex, cmd);

            RebuildCollectVisibleCommands();
            NotifyStructureChanged();
        }

        public void Move(ViewportRenderCommand cmd, int toIndex)
        {
            int fromIndex = _commands.IndexOf(cmd);
            if (fromIndex < 0)
                return;

            Move(fromIndex, toIndex);
        }

        public IEnumerator<ViewportRenderCommand> GetEnumerator()
            => Commands.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)Commands).GetEnumerator();

        /// <summary>
        /// Executes all commands in the container.
        /// </summary>
        public void Execute()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("ViewportRenderCommandContainer.Execute");
            var instance = ViewportRenderCommand.ActivePipelineInstance;
            if (instance is null)
                return;

            using (RuntimeRenderingHostServices.Current.StartProfileScope("ViewportRenderCommandContainer.EnsureResourcesAllocated"))
                EnsureResourcesAllocated(instance);

            for (int i = 0; i < _commands.Count; i++)
            {
                try
                {
                    _commands[i].ExecuteIfShould();
                }
                catch (Exception ex)
                {
                    // Isolate individual command failures so one bad factory (e.g., a
                    // missing texture during FBO recreation after resize) does not abort
                    // the entire command chain. Subsequent commands that succeed will
                    // populate the resource registry, allowing the failing command to
                    // recover on the next frame once its dependencies are available.
                    Debug.RenderingWarningEvery(
                        $"VPRC.Execute.{_commands[i].GetType().Name}.{i}",
                        TimeSpan.FromSeconds(1),
                        "[RenderDiag] Command [{0}] {1} threw {2}: {3}\n{4}",
                        i,
                        _commands[i].GetType().Name,
                        ex.GetType().FullName ?? ex.GetType().Name,
                        ex.Message,
                        ex.ToString());
                }
            }
        }
        public void CollectVisible()
        {
            for (int i = 0; i < _collectVisibleCommands.Count; i++)
                _collectVisibleCommands[i].CollectVisible();
        }
        public void SwapBuffers()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("ViewportRenderCommandContainer.SwapBuffers");
            for (int i = 0; i < _collectVisibleCommands.Count; i++)
            {
                var command = _collectVisibleCommands[i];
                using var commandSample = RuntimeRenderingHostServices.Current.StartProfileScope($"ViewportRenderCommandContainer.Swap.{command.GetType().Name}");
                command.SwapBuffers();
            }
        }

        public void BuildRenderPassMetadata(RenderPassMetadataCollection collection)
        {
            RenderGraphDescribeContext context = new(collection);
            BuildRenderPassMetadata(context);
        }

        internal void BuildRenderPassMetadata(RenderGraphDescribeContext context)
        {
            for (int i = 0; i < _commands.Count; i++)
                _commands[i].DescribeRenderPass(context);
        }
        internal void OnBranchSelected(XRRenderPipelineInstance instance)
            => EnsureResourcesAllocated(instance);

        internal void OnBranchDeselected(XRRenderPipelineInstance instance)
        {
            if (BranchResources == BranchResourceBehavior.DisposeResourcesOnBranchExit)
                ReleaseResources(instance);
        }

        private InstanceResourceState GetInstanceState(XRRenderPipelineInstance instance)
        {
            if (!_instanceStates.TryGetValue(instance, out var state))
            {
                state = new InstanceResourceState();
                _instanceStates.Add(instance, state);
            }

            return state;
        }

        private void EnsureResourcesAllocated(XRRenderPipelineInstance instance)
        {
            var state = GetInstanceState(instance);
            int generation = instance.ResourceGeneration;

            if (state.ResourcesAllocated && state.AllocatedAtGeneration == generation)
                return;

            // If resources were previously allocated but the pipeline's physical
            // resources have been invalidated (e.g., after a viewport resize),
            // release the stale per-command resources first so they are rebuilt
            // with the correct dimensions and texture/FBO references.
            if (state.ResourcesAllocated)
            {
                for (int i = _commands.Count - 1; i >= 0; i--)
                    _commands[i].ReleaseContainerResources(instance);
            }

            for (int i = 0; i < _commands.Count; i++)
                _commands[i].AllocateContainerResources(instance);

            state.ResourcesAllocated = true;
            state.AllocatedAtGeneration = generation;
        }

        private void ReleaseResources(XRRenderPipelineInstance instance)
        {
            if (!_instanceStates.TryGetValue(instance, out var state) || !state.ResourcesAllocated)
                return;

            for (int i = _commands.Count - 1; i >= 0; i--)
                _commands[i].ReleaseContainerResources(instance);

            state.ResourcesAllocated = false;
        }

        private void AttachCommand(ViewportRenderCommand cmd, int index, bool notifyStructureChanged)
        {
            ArgumentNullException.ThrowIfNull(cmd);

            if (cmd.CommandContainer is not null && !ReferenceEquals(cmd.CommandContainer, this))
                throw new InvalidOperationException("Command is already attached to a different command container.");

            cmd.CommandContainer = this;
            _commands.Insert(index, cmd);
            cmd.OnAttachedToContainer();
            if (_parentPipeline is not null)
                cmd.OnParentPipelineAssigned();

            RebuildCollectVisibleCommands();
            if (notifyStructureChanged)
                NotifyStructureChanged();
        }

        private void RebuildCollectVisibleCommands()
        {
            _collectVisibleCommands.Clear();
            for (int i = 0; i < _commands.Count; i++)
            {
                if (_commands[i].NeedsCollecVisible)
                    _collectVisibleCommands.Add(_commands[i]);
            }
        }

        private void NotifyStructureChanged()
        {
            _instanceStates.Clear();
            if (StructureChangeNotificationsSuppressed)
                return;

            _parentPipeline?.NotifyCommandChainStructureChanged();
        }
    }
}
