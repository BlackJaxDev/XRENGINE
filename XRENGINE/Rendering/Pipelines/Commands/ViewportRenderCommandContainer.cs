using System.Collections;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class ViewportRenderCommandContainer : XRBase, IReadOnlyList<ViewportRenderCommand>
    {
        private readonly List<ViewportRenderCommand> _commands = [];
        public IReadOnlyList<ViewportRenderCommand> Commands => _commands;

        private readonly List<ViewportRenderCommand> _collecVisibleCommands = [];
        public IReadOnlyList<ViewportRenderCommand> CollecVisibleCommands => _collecVisibleCommands;

        private RenderPipeline? _parentPipeline;
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

        public ViewportRenderCommandContainer(RenderPipeline? parentPipeline = null)
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

            ViewportRenderCommand cmd = Activator.CreateInstance(t) as ViewportRenderCommand ?? throw new ArgumentException("Type must have a public parameterless constructor.", nameof(t));
            Add(cmd);
            return cmd;
        }
        public ViewportRenderCommand Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type t, params object[] arguments)
        {
            if (!typeof(ViewportRenderCommand).IsAssignableFrom(t))
                throw new ArgumentException("Type must be a subclass of ViewportRenderCommand.", nameof(t));

            ViewportRenderCommand cmd = (ViewportRenderCommand)Activator.CreateInstance(t, arguments) ?? throw new ArgumentException("Type must have a public constructor with the specified arguments.", nameof(t));
            Add(cmd);
            return cmd;
        }
        /// <summary>
        /// Adds a command to the viewport render command list.
        /// </summary>
        /// <param name="cmd"></param>
        public void Add(ViewportRenderCommand cmd)
        {
            cmd.CommandContainer = this;
            _commands.Add(cmd);
            cmd.OnAttachedToContainer();
            if (_parentPipeline is not null)
                cmd.OnParentPipelineAssigned();
            if (cmd.NeedsCollecVisible)
                _collecVisibleCommands.Add(cmd);
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
            for (int i = 0; i < _commands.Count; i++)
                _commands[i].ExecuteIfShould();
        }
        public void CollectVisible()
        {
            for (int i = 0; i < _collecVisibleCommands.Count; i++)
                _collecVisibleCommands[i].CollectVisible();
        }
        public void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("ViewportRenderCommandContainer.SwapBuffers");
            for (int i = 0; i < _collecVisibleCommands.Count; i++)
            {
                var command = _collecVisibleCommands[i];
                using var commandSample = Engine.Profiler.Start($"ViewportRenderCommandContainer.Swap.{command.GetType().Name}");
                command.SwapBuffers();
            }
        }

        public void BuildRenderPassMetadata(RenderPassMetadataCollection collection)
        {
            RenderGraphDescribeContext context = new(collection);
            for (int i = 0; i < _commands.Count; i++)
                _commands[i].DescribeRenderPass(context);
        }
        //public void GenerateFBOs()
        //{
        //    foreach (var rc in _commands)
        //        rc?.GenerateFBOs();
        //    FBOsInitialized = true;
        //}
        //public void RegenerateFBOs()
        //{
        //    foreach (var rc in _commands)
        //    {
        //        rc?.DestroyFBOs();
        //        rc?.GenerateFBOs();
        //    }
        //    FBOsInitialized = true;
        //}
    }
}
