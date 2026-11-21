using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// Maps the commands of one command generation to the render passes they describe.
/// The snapshot is immutable after it is built. Tools use it to link commands and passes,
/// to find commands that do GPU work without a pass, and to match GPU profiler paths to commands.
/// </summary>
public sealed class RenderPipelineStructureSnapshot
{
    private readonly RenderPipelineStructureCommand[] _commands;
    private readonly Dictionary<ViewportRenderCommand, int> _commandIndices;
    private readonly Dictionary<int, int> _ownerCommandByPass;
    private readonly Dictionary<string, int> _commandByProfilingPath;
    private readonly int[] _gpuCommandsWithoutPasses;
    private readonly int[] _pipelineDeclaredPasses;

    private RenderPipelineStructureSnapshot(
        ulong commandGeneration,
        IReadOnlyCollection<RenderPassMetadata> sourcePassMetadata,
        RenderPipelineStructureCommand[] commands,
        Dictionary<ViewportRenderCommand, int> commandIndices,
        Dictionary<int, int> ownerCommandByPass,
        Dictionary<string, int> commandByProfilingPath,
        int[] gpuCommandsWithoutPasses,
        int[] pipelineDeclaredPasses)
    {
        CommandGeneration = commandGeneration;
        SourcePassMetadata = sourcePassMetadata;
        _commands = commands;
        _commandIndices = commandIndices;
        _ownerCommandByPass = ownerCommandByPass;
        _commandByProfilingPath = commandByProfilingPath;
        _gpuCommandsWithoutPasses = gpuCommandsWithoutPasses;
        _pipelineDeclaredPasses = pipelineDeclaredPasses;
    }

    /// <summary>Gets an empty snapshot.</summary>
    public static RenderPipelineStructureSnapshot Empty { get; } = new(
        0UL,
        Array.Empty<RenderPassMetadata>(),
        [],
        new Dictionary<ViewportRenderCommand, int>(ReferenceEqualityComparer.Instance),
        [],
        new Dictionary<string, int>(StringComparer.Ordinal),
        [],
        []);

    /// <summary>Gets the command generation that the snapshot describes.</summary>
    public ulong CommandGeneration { get; }

    /// <summary>Gets the pass metadata collection that the snapshot was built from.</summary>
    public IReadOnlyCollection<RenderPassMetadata> SourcePassMetadata { get; }

    /// <summary>Gets every command in depth-first order.</summary>
    public IReadOnlyList<RenderPipelineStructureCommand> Commands => _commands;

    /// <summary>Gets the indices of commands that do GPU work but describe no pass.</summary>
    public IReadOnlyList<int> GpuCommandsWithoutPasses => _gpuCommandsWithoutPasses;

    /// <summary>Gets the indices of passes that no command described. The pipeline declared them directly.</summary>
    public IReadOnlyList<int> PipelineDeclaredPasses => _pipelineDeclaredPasses;

    /// <summary>Finds the snapshot entry of <paramref name="command"/>.</summary>
    public bool TryGetCommand(ViewportRenderCommand command, out RenderPipelineStructureCommand entry)
    {
        if (_commandIndices.TryGetValue(command, out int index))
        {
            entry = _commands[index];
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>Finds the index of the command that first described pass <paramref name="passIndex"/>.</summary>
    public bool TryGetOwnerCommandIndex(int passIndex, out int commandIndex)
        => _ownerCommandByPass.TryGetValue(passIndex, out commandIndex);

    /// <summary>Finds the command whose <see cref="RenderPipelineStructureCommand.ProfilingPath"/> equals <paramref name="profilingPath"/>.</summary>
    public bool TryGetCommandByProfilingPath(string profilingPath, out int commandIndex)
        => _commandByProfilingPath.TryGetValue(profilingPath, out commandIndex);

    /// <summary>
    /// Builds a snapshot for <paramref name="root"/> and the pass metadata that the same command chain produced.
    /// Call it when the command generation or the pass metadata changes, not each frame.
    /// </summary>
    public static RenderPipelineStructureSnapshot Build(
        ulong commandGeneration,
        ViewportRenderCommandContainer? root,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        passMetadata ??= Array.Empty<RenderPassMetadata>();
        if (root is null)
            return Empty;

        List<RenderPipelineStructureCommand> commands = new(Math.Max(16, root.Count * 2));
        Dictionary<ViewportRenderCommand, int> commandIndices = new(ReferenceEqualityComparer.Instance);
        HashSet<ViewportRenderCommandContainer> visited = new(ReferenceEqualityComparer.Instance);
        List<RenderCommandChildContainer> childScratch = [];
        Walk(root, "CommandChain", -1, 0, string.Empty, commands, commandIndices, visited, childScratch);

        Dictionary<int, List<int>> passesByCommand = [];
        Dictionary<int, int> ownerByPass = [];
        List<int> pipelineDeclared = [];
        foreach (RenderPassMetadata pass in passMetadata)
        {
            IReadOnlyList<ViewportRenderCommand> contributors = pass.ContributingCommands;
            if (contributors.Count == 0)
            {
                pipelineDeclared.Add(pass.PassIndex);
                continue;
            }

            for (int i = 0; i < contributors.Count; i++)
            {
                if (!commandIndices.TryGetValue(contributors[i], out int commandIndex))
                    continue;

                ownerByPass.TryAdd(pass.PassIndex, commandIndex);
                if (!passesByCommand.TryGetValue(commandIndex, out List<int>? list))
                {
                    list = [];
                    passesByCommand.Add(commandIndex, list);
                }

                list.Add(pass.PassIndex);
            }
        }

        List<int> gpuWithoutPass = [];
        Dictionary<string, int> byPath = new(StringComparer.Ordinal);
        for (int i = 0; i < commands.Count; i++)
        {
            RenderPipelineStructureCommand entry = commands[i];
            if (passesByCommand.TryGetValue(i, out List<int>? passes))
            {
                passes.Sort();
                entry.PassIndices = passes.ToArray();
            }

            if (entry.IsGpuWorkWithoutPass)
                gpuWithoutPass.Add(i);

            // The first command with a path wins. Repeated paths come from loops or reused types in one container.
            byPath.TryAdd(entry.ProfilingPath, i);
        }

        pipelineDeclared.Sort();
        return new RenderPipelineStructureSnapshot(
            commandGeneration,
            passMetadata,
            commands.ToArray(),
            commandIndices,
            ownerByPass,
            byPath,
            gpuWithoutPass.ToArray(),
            pipelineDeclared.ToArray());
    }

    private static void Walk(
        ViewportRenderCommandContainer container,
        string containerName,
        int parentIndex,
        int depth,
        string parentPath,
        List<RenderPipelineStructureCommand> commands,
        Dictionary<ViewportRenderCommand, int> commandIndices,
        HashSet<ViewportRenderCommandContainer> visited,
        List<RenderCommandChildContainer> childScratch)
    {
        if (!visited.Add(container))
            return;

        for (int i = 0; i < container.Count; i++)
        {
            ViewportRenderCommand command = container[i];
            if (commandIndices.ContainsKey(command))
                continue;

            string scopeName = command.GpuProfilingName;
            string path = parentPath.Length == 0 ? scopeName : string.Concat(parentPath, "/", scopeName);
            int index = commands.Count;
            RenderPipelineStructureCommand entry = new(index, command, parentIndex, depth, container, i, containerName, path);
            commands.Add(entry);
            commandIndices.Add(command, index);

            int childStart = childScratch.Count;
            command.CollectChildContainers(childScratch);
            int childEnd = childScratch.Count;
            for (int c = childStart; c < childEnd; c++)
            {
                RenderCommandChildContainer child = childScratch[c];
                Walk(child.Container, child.Name, index, depth + 1, path, commands, commandIndices, visited, childScratch);
            }

            childScratch.RemoveRange(childStart, childEnd - childStart);
            entry.SubtreeEnd = commands.Count;
        }
    }
}
