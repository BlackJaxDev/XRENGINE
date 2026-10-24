using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.UI;

/// <summary>
/// Tracks the selected UI mesh passes in one closed command graph.
/// This tracks execution structure only. A renderer receipt must prove each draw.
/// </summary>
internal sealed class UIWorldPassManifest : IDisposable
{
    private const int MaximumNodes = 2048;
    private const int MaximumDepth = 32;
    [ThreadStatic] private static UIWorldPassManifest? t_active;
    private readonly UIWorldPassManifestNode[] _nodes = new UIWorldPassManifestNode[MaximumNodes];
    private ViewportRenderCommandContainer? _root;
    private int _rootCommandCount;
    private UIPublication? _publication;
    private int _count;
    private bool _failed;
    private string? _failure;

    internal static UIWorldPassManifest? Active
    {
        get => t_active;
        set => t_active = value;
    }

    internal int NodeCount => _count;
    internal string? Failure => _failure;
    internal UIPublication? Publication => _publication;
    internal UIWorldPassManifestScope EnterScope() => new(this);

    /// <summary>Holds the selected packet through all shadow and color pass decisions.</summary>
    internal bool TryBindPublication(UIPublication publication)
    {
        if (_publication is not null)
            return ReferenceEquals(_publication, publication) ||
                Fail("UI world attempt selected a different canvas packet.");
        publication.Retain();
        _publication = publication;
        return true;
    }

    public void Dispose()
    {
        UIPublication? publication = _publication;
        _publication = null;
        publication?.Release();
    }

    internal bool TryCapture(ViewportRenderCommandContainer root, out string? failure)
    {
        Dispose();
        _root = root;
        _rootCommandCount = root.Commands.Count;
        _count = 0;
        _failed = false;
        _failure = null;
        if (!CaptureContainer(root, -1, false, 0))
        {
            failure = _failure;
            return false;
        }

        failure = null;
        return true;
    }

    private bool CaptureContainer(ViewportRenderCommandContainer container,
        int parentBranchIndex, bool parentBranchValue, int depth)
    {
        if (depth > MaximumDepth)
            return Fail("UI world command graph exceeds its branch depth limit.");

        IReadOnlyList<ViewportRenderCommand> commands = container.Commands;
        for (int commandIndex = 0; commandIndex < commands.Count; commandIndex++)
        {
            if (_count == MaximumNodes)
                return Fail("UI world command graph exceeds its node limit.");

            ViewportRenderCommand command = commands[commandIndex];
            if (Find(command) >= 0)
                return Fail("UI world command graph contains a repeated command instance.");
            if (command is VPRC_Switch or VPRC_Repeat or VPRC_ConditionalRender or
                VPRC_ForEachCascade or VPRC_RenderMeshesFiltered or VPRC_RenderByRenderGroup)
                return Fail($"UI world command graph does not support {command.GetType().Name}.");
            Type commandType = command.GetType();
            if (commandType.IsGenericType &&
                commandType.GetGenericTypeDefinition() == typeof(VPRC_ForEach<>))
                return Fail("UI world command graph does not support dynamic iteration.");

            int nodeIndex = _count++;
            _nodes[nodeIndex] = new UIWorldPassManifestNode
            {
                Command = command,
                Container = container,
                ContainerIndex = commandIndex,
                ContainerCommandCount = commands.Count,
                ParentBranchIndex = parentBranchIndex,
                ParentBranchValue = parentBranchValue,
                Branch = command as VPRC_IfElse,
                Condition = (command as VPRC_IfElse)?.ConditionEvaluator,
                TrueCommands = (command as VPRC_IfElse)?.TrueCommands,
                FalseCommands = (command as VPRC_IfElse)?.FalseCommands,
                TrueCommandCount = (command as VPRC_IfElse)?.TrueCommands?.Commands.Count ?? -1,
                FalseCommandCount = (command as VPRC_IfElse)?.FalseCommands?.Commands.Count ?? -1,
                Pass = command as VPRC_RenderMeshesPassShared,
                RenderPass = (command as VPRC_RenderMeshesPassShared)?.RenderPass ?? -1,
                PathIntent = (command as VPRC_RenderMeshesPassShared)?.PathIntent ?? default,
                MeshSubmissionStrategy = (command as VPRC_RenderMeshesPassShared)?.MeshSubmissionStrategy ?? default,
                PreserveMeshSubmissionStrategy = (command as VPRC_RenderMeshesPassShared)?.PreserveMeshSubmissionStrategy ?? false,
                EnforceAdvancedLatePassEligibility = (command as VPRC_RenderMeshesPassShared)?.EnforceAdvancedLatePassEligibility ?? false,
                RenderGraphPassName = (command as VPRC_RenderMeshesPassShared)?.RenderGraphPassName,
                ShouldExecute = command.ShouldExecute,
                ExecuteInShadowPass = command.ExecuteInShadowPass,
                ExpectedUiDraws = -1,
                State = EUIWorldPassNodeState.Pending,
            };

            if (command is not VPRC_IfElse branch)
                continue;
            if (branch.TrueCommands is { } trueCommands &&
                !CaptureContainer(trueCommands, nodeIndex, true, depth + 1))
                return false;
            if (branch.FalseCommands is { } falseCommands &&
                !CaptureContainer(falseCommands, nodeIndex, false, depth + 1))
                return false;
        }
        return true;
    }

    /// <summary>Records the UI draw count from the published pass image before authoring.</summary>
    internal bool SetExpectedUiDraws(VPRC_RenderMeshesPassShared pass, int count)
    {
        if (count < 0)
            return Fail("UI world pass count is negative.");
        int index = Find(pass);
        if (index < 0 || _nodes[index].Pass is null || _nodes[index].ExpectedUiDraws >= 0)
            return Fail("UI world pass has no unique published selection.");
        _nodes[index].ExpectedUiDraws = count;
        return true;
    }

    /// <summary>Records one branch result before branch resource callbacks run.</summary>
    internal void RecordBranch(VPRC_IfElse branch, bool selectedTrue)
    {
        int index = Find(branch);
        if (index < 0 || !ValidateNode(index) || !IsReachable(index) ||
            _nodes[index].Branch is null || _nodes[index].State != EUIWorldPassNodeState.Pending)
        {
            Fail("UI world branch did not match the selected graph.");
            return;
        }
        _nodes[index].State = selectedTrue
            ? EUIWorldPassNodeState.BranchTrue : EUIWorldPassNodeState.BranchFalse;
    }

    internal void RecordExecuted(ViewportRenderCommand command)
    {
        int index = Find(command);
        if (index < 0 || !ValidateNode(index) || !IsReachable(index))
        {
            Fail("UI world command execution did not match the selected graph.");
            return;
        }
        ref UIWorldPassManifestNode node = ref _nodes[index];
        if (node.Branch is not null)
        {
            if (node.State is not (EUIWorldPassNodeState.BranchTrue or EUIWorldPassNodeState.BranchFalse))
                Fail("UI world branch completed without a selected child.");
            return;
        }
        if (node.Pass is null)
            return;
        if (node.ExpectedUiDraws < 0 || node.State != EUIWorldPassNodeState.Pending)
            Fail("UI world mesh pass has no unique expected selection.");
        else
            node.State = node.ExpectedUiDraws == 0
                ? EUIWorldPassNodeState.Empty : EUIWorldPassNodeState.Executed;
    }

    internal void RecordSkipped(ViewportRenderCommand command)
    {
        int index = Find(command);
        if (index < 0 || !ValidateNode(index) || !IsReachable(index))
        {
            Fail("UI world command skip did not match the selected graph.");
            return;
        }
        ref UIWorldPassManifestNode node = ref _nodes[index];
        if (node.Pass is not null && node.ExpectedUiDraws == 0)
            node.State = EUIWorldPassNodeState.Empty;
        else if (node.Pass is not null || node.Branch is not null)
            Fail("UI world required command was skipped.");
    }

    internal void RecordFailure(ViewportRenderCommand command)
        => Fail($"UI world command {command.GetType().Name} failed.");

    /// <summary>Checks branch and pass execution. This does not prove native draws.</summary>
    internal bool TryValidateExecutionStructure(out string? failure)
    {
        if (_root is null)
            Fail("UI world command graph has no root.");
        else if (_root.Commands.Count != _rootCommandCount)
            Fail("UI world root command count changed after capture.");
        for (int index = 0; index < _count && !_failed; index++)
        {
            if (!ValidateNode(index, includeTransientState: false))
                break;
            if (!IsReachable(index))
                continue;
            ref UIWorldPassManifestNode node = ref _nodes[index];
            if (node.Branch is not null && node.State is not
                (EUIWorldPassNodeState.BranchTrue or EUIWorldPassNodeState.BranchFalse))
                Fail("UI world branch has no execution decision.");
            else if (node.Pass is not null && (node.ExpectedUiDraws < 0 ||
                node.State is not (EUIWorldPassNodeState.Empty or EUIWorldPassNodeState.Executed)))
                Fail("UI world selected mesh pass did not complete.");
        }
        failure = _failure;
        return !_failed;
    }

    private int Find(ViewportRenderCommand command)
    {
        for (int index = 0; index < _count; index++)
            if (ReferenceEquals(_nodes[index].Command, command))
                return index;
        return -1;
    }

    private bool ValidateNode(int index, bool includeTransientState = true)
    {
        ref UIWorldPassManifestNode node = ref _nodes[index];
        IReadOnlyList<ViewportRenderCommand> commands = node.Container.Commands;
        if (commands.Count != node.ContainerCommandCount ||
            node.ContainerIndex >= commands.Count ||
            !ReferenceEquals(commands[node.ContainerIndex], node.Command) ||
            (includeTransientState && node.State == EUIWorldPassNodeState.Pending &&
                node.ShouldExecute != node.Command.ShouldExecute) ||
            node.ExecuteInShadowPass != node.Command.ExecuteInShadowPass)
            return Fail("UI world command graph changed after capture.");
        if (node.Branch is { } branch &&
            (!ReferenceEquals(node.Condition, branch.ConditionEvaluator) ||
             !ReferenceEquals(node.TrueCommands, branch.TrueCommands) ||
             !ReferenceEquals(node.FalseCommands, branch.FalseCommands) ||
             node.TrueCommandCount != (branch.TrueCommands?.Commands.Count ?? -1) ||
             node.FalseCommandCount != (branch.FalseCommands?.Commands.Count ?? -1)))
            return Fail("UI world branch configuration changed after capture.");
        if (node.Pass is { } pass &&
            (node.RenderPass != pass.RenderPass || node.PathIntent != pass.PathIntent ||
             node.MeshSubmissionStrategy != pass.MeshSubmissionStrategy ||
             node.PreserveMeshSubmissionStrategy != pass.PreserveMeshSubmissionStrategy ||
             node.EnforceAdvancedLatePassEligibility != pass.EnforceAdvancedLatePassEligibility ||
             node.RenderGraphPassName != pass.RenderGraphPassName))
            return Fail("UI world mesh pass configuration changed after capture.");
        return true;
    }

    private bool IsReachable(int index)
    {
        ref UIWorldPassManifestNode node = ref _nodes[index];
        if (node.ParentBranchIndex < 0)
            return true;
        ref UIWorldPassManifestNode parent = ref _nodes[node.ParentBranchIndex];
        if (!IsReachable(node.ParentBranchIndex))
            return false;
        return parent.State == (node.ParentBranchValue
            ? EUIWorldPassNodeState.BranchTrue : EUIWorldPassNodeState.BranchFalse);
    }

    private bool Fail(string message)
    {
        _failed = true;
        _failure ??= message;
        return false;
    }
}
