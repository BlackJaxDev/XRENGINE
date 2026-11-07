using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Structural edits on the command tree of a <see cref="RenderPipelineDraftState"/>. Each edit treats a push command
/// and its paired pop command in the same container as one range, so an edit never splits a state scope.
/// </summary>
public static class RenderPipelineDraftEdits
{
    /// <summary>The label that new branch commands get.</summary>
    public const string NewBranchLabel = "New branch";

    /// <summary>Finds the container of <paramref name="command"/> inside the tree of <paramref name="root"/>.</summary>
    public static bool TryLocate(ViewportRenderCommandContainer root, ViewportRenderCommand command, out ViewportRenderCommandContainer container, out int index)
    {
        container = null!;
        index = -1;
        if (command.CommandContainer is not { } owner || !ContainsContainer(root, owner))
            return false;

        index = owner.IndexOf(command);
        if (index < 0)
            return false;

        container = owner;
        return true;
    }

    /// <summary>
    /// Returns the index of the last command in the range that starts at <paramref name="index"/>. For a push command
    /// whose pop command is in the same container, the range ends at the pop command.
    /// </summary>
    public static int GetRangeEnd(ViewportRenderCommandContainer container, int index)
    {
        if (container[index] is not ViewportStateRenderCommandBase push)
            return index;

        for (int i = index + 1; i < container.Count; i++)
        {
            if (ReferenceEquals(container[i], push.PairedPopCommand))
                return i;
        }

        return index;
    }

    /// <summary>
    /// Removes <paramref name="command"/>. For a push or pop command, the method removes both commands of the pair and
    /// keeps the commands between them, so a delete never drops a whole scope by accident.
    /// </summary>
    public static bool Delete(RenderPipelineDraftState state, ViewportRenderCommand command)
    {
        if (!TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index))
            return false;

        int pushIndex = FindPushOfPop(container, index);
        if (pushIndex >= 0)
            index = pushIndex;

        int end = GetRangeEnd(container, index);
        if (end != index)
            container.RemoveAt(end);
        container.RemoveAt(index);
        return true;
    }

    /// <summary>Inserts a copy of the range of <paramref name="command"/> after the range.</summary>
    /// <returns>The first copied command, or null.</returns>
    public static ViewportRenderCommand? Duplicate(RenderPipelineDraftState state, ViewportRenderCommand command)
    {
        if (!TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index))
            return null;

        int end = GetRangeEnd(container, index);
        List<ViewportRenderCommand> copies = RenderCommandGraphCloner.CloneCommands(Slice(container, index, end));
        for (int i = 0; i < copies.Count; i++)
            container.Insert(end + 1 + i, copies[i]);
        return copies.Count > 0 ? copies[0] : null;
    }

    /// <summary>
    /// Moves the range of <paramref name="command"/> past the neighboring range in the same container.
    /// A negative <paramref name="direction"/> moves it earlier.
    /// </summary>
    public static bool MoveBy(RenderPipelineDraftState state, ViewportRenderCommand command, int direction)
    {
        if (direction == 0 || !TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index))
            return false;

        int end = GetRangeEnd(container, index);
        if (direction < 0)
        {
            int previousStart = FindRangeStartBefore(container, index);
            if (previousStart < 0)
                return false;

            MoveRange(container, index, end, previousStart);
            return true;
        }

        if (end + 1 >= container.Count)
            return false;

        int nextEnd = GetRangeEnd(container, end + 1);
        MoveRange(container, index, end, nextEnd - (end - index));
        return true;
    }

    /// <summary>
    /// Moves the range of <paramref name="command"/> so it starts where the range of <paramref name="target"/> starts.
    /// Both commands must be in the same container.
    /// </summary>
    public static bool MoveBefore(RenderPipelineDraftState state, ViewportRenderCommand command, ViewportRenderCommand target)
    {
        if (ReferenceEquals(command, target) ||
            !TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index) ||
            !ReferenceEquals(target.CommandContainer, container))
        {
            return false;
        }

        int end = GetRangeEnd(container, index);
        int targetIndex = container.IndexOf(target);
        if (targetIndex >= index && targetIndex <= end)
            return false;

        int destination = targetIndex > end ? targetIndex - (end - index + 1) : targetIndex;
        MoveRange(container, index, end, destination);
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="commands"/> after the range of <paramref name="anchor"/>. A null anchor appends to the
    /// root container.
    /// </summary>
    public static bool InsertAfter(RenderPipelineDraftState state, ViewportRenderCommand? anchor, IReadOnlyList<ViewportRenderCommand> commands)
    {
        ViewportRenderCommandContainer container = state.Commands;
        int insertIndex = container.Count;
        if (anchor is not null)
        {
            if (!TryLocate(state.Commands, anchor, out container, out int index))
                return false;

            insertIndex = GetRangeEnd(container, index) + 1;
        }

        for (int i = 0; i < commands.Count; i++)
            container.Insert(insertIndex + i, commands[i]);
        return true;
    }

    /// <summary>Appends <paramref name="commands"/> to <paramref name="container"/>, which must be in the draft tree.</summary>
    public static bool AppendTo(RenderPipelineDraftState state, ViewportRenderCommandContainer container, IReadOnlyList<ViewportRenderCommand> commands)
    {
        if (!ContainsContainer(state.Commands, container))
            return false;

        for (int i = 0; i < commands.Count; i++)
            container.Add(commands[i]);
        return true;
    }

    /// <summary>
    /// Moves the range of <paramref name="command"/> into the true branch of a new <see cref="VPRC_IfElse"/>. The new
    /// branch has a constant true condition, so the frame does not change until the user edits the condition.
    /// </summary>
    public static VPRC_IfElse? WrapInIf(RenderPipelineDraftState state, ViewportRenderCommand command)
    {
        if (!TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index))
            return null;

        int end = GetRangeEnd(container, index);
        List<ViewportRenderCommand> moved = Take(container, index, end);
        ViewportRenderCommandContainer trueCommands = new();
        for (int i = 0; i < moved.Count; i++)
            trueCommands.Add(moved[i]);

        VPRC_IfElse branch = new()
        {
            Label = NewBranchLabel,
            Condition = RenderBranchCondition.Constant(true),
            TrueCommands = trueCommands,
        };
        container.Insert(index, branch);
        return branch;
    }

    /// <summary>
    /// Puts a new push command of <paramref name="pushType"/> before the range of <paramref name="command"/> and its
    /// paired pop command after the range.
    /// </summary>
    public static ViewportStateRenderCommandBase? WrapInScope(RenderPipelineDraftState state, ViewportRenderCommand command, Type pushType)
    {
        if (!typeof(ViewportStateRenderCommandBase).IsAssignableFrom(pushType))
            throw new ArgumentException($"'{pushType.Name}' is not a push state command.", nameof(pushType));
        if (!TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index))
            return null;

        int end = GetRangeEnd(container, index);
        ViewportStateRenderCommandBase push = (ViewportStateRenderCommandBase)RenderCommandGraphCloner.CreateCommand(pushType);
        container.Insert(end + 1, push.PairedPopCommand);
        container.Insert(index, push);
        return push;
    }

    /// <summary>
    /// Moves the range of <paramref name="command"/> into the true branch of a new <see cref="VPRC_IfElse"/>, and puts
    /// a copy of the range in the false branch. The user then edits one branch to make a variant of the original
    /// commands.
    /// </summary>
    public static VPRC_IfElse? ExtractToBranch(RenderPipelineDraftState state, ViewportRenderCommand command)
    {
        if (!TryLocate(state.Commands, command, out ViewportRenderCommandContainer container, out int index))
            return null;

        int end = GetRangeEnd(container, index);
        List<ViewportRenderCommand> copies = RenderCommandGraphCloner.CloneCommands(Slice(container, index, end));
        VPRC_IfElse? branch = WrapInIf(state, command);
        if (branch is null)
            return null;

        ViewportRenderCommandContainer falseCommands = new();
        for (int i = 0; i < copies.Count; i++)
            falseCommands.Add(copies[i]);
        branch.FalseCommands = falseCommands;
        return branch;
    }

    /// <summary>Returns true when <paramref name="target"/> is <paramref name="root"/> or a container below it.</summary>
    public static bool ContainsContainer(ViewportRenderCommandContainer root, ViewportRenderCommandContainer target)
    {
        if (ReferenceEquals(root, target))
            return true;

        List<RenderCommandChildContainer> children = [];
        for (int i = 0; i < root.Count; i++)
        {
            children.Clear();
            root[i].CollectChildContainers(children);
            for (int c = 0; c < children.Count; c++)
            {
                if (ContainsContainer(children[c].Container, target))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Calls <paramref name="visit"/> for each command in the tree, in pre-order.</summary>
    public static void ForEachCommand(ViewportRenderCommandContainer root, Action<ViewportRenderCommand> visit)
    {
        List<RenderCommandChildContainer> children = [];
        for (int i = 0; i < root.Count; i++)
        {
            ViewportRenderCommand command = root[i];
            visit(command);
            children.Clear();
            command.CollectChildContainers(children);
            for (int c = 0; c < children.Count; c++)
                ForEachCommand(children[c].Container, visit);
        }
    }

    private static int FindPushOfPop(ViewportRenderCommandContainer container, int popIndex)
    {
        ViewportRenderCommand pop = container[popIndex];
        if (pop is not ViewportPopStateRenderCommand)
            return -1;

        for (int i = popIndex - 1; i >= 0; i--)
        {
            if (container[i] is ViewportStateRenderCommandBase push && ReferenceEquals(push.PairedPopCommand, pop))
                return i;
        }

        return -1;
    }

    private static int FindRangeStartBefore(ViewportRenderCommandContainer container, int index)
    {
        if (index <= 0)
            return -1;

        // The range before index ends at index - 1. A pop command there belongs to an earlier push command.
        int pushIndex = FindPushOfPop(container, index - 1);
        return pushIndex >= 0 ? pushIndex : index - 1;
    }

    private static void MoveRange(ViewportRenderCommandContainer container, int start, int end, int destination)
    {
        List<ViewportRenderCommand> moved = Take(container, start, end);
        for (int i = 0; i < moved.Count; i++)
            container.Insert(destination + i, moved[i]);
    }

    private static List<ViewportRenderCommand> Slice(ViewportRenderCommandContainer container, int start, int end)
    {
        List<ViewportRenderCommand> commands = new(end - start + 1);
        for (int i = start; i <= end; i++)
            commands.Add(container[i]);
        return commands;
    }

    private static List<ViewportRenderCommand> Take(ViewportRenderCommandContainer container, int start, int end)
    {
        List<ViewportRenderCommand> commands = Slice(container, start, end);
        for (int i = end; i >= start; i--)
            container.RemoveAt(i);
        return commands;
    }
}
