using System.Collections.Generic;
using System.Threading;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.UI;

/// <summary>
/// Tracks the selected UI mesh passes in one closed command graph.
/// This tracks execution structure only. A renderer receipt must prove each draw.
/// </summary>
internal sealed class UIWorldPassManifest : IDisposable
{
    private const int MaximumNodes = 2048;
    private const int MaximumSwitchCases = 256;
    private const int MaximumCanvasPackets = 64;
    private const int MaximumDepth = 32;
    [ThreadStatic] private static UIWorldPassManifest? t_active;
    private static long s_nextAttemptToken;
    private readonly UIWorldPassManifestNode[] _nodes = new UIWorldPassManifestNode[MaximumNodes];
    private readonly UIWorldPassManifestSwitchCase[] _switchCases =
        new UIWorldPassManifestSwitchCase[MaximumSwitchCases];
    private readonly Dictionary<ViewportRenderCommand, int> _nodeIndices =
        new(MaximumNodes, ReferenceEqualityComparer.Instance);
    private readonly object?[] _canvasKeys = new object?[MaximumCanvasPackets];
    private readonly UIPublication?[] _packets = new UIPublication?[MaximumCanvasPackets];
    private ViewportRenderCommandContainer? _root;
    private int _rootCommandCount;
    private int _switchCaseCount;
    private int _packetCount;
    private bool _packetSetSealed;
    private int _count;
    private long _activeAttemptToken;
    private int _captureStarted;
    private int _ownerThreadId;
    private bool _failed;
    private string? _failure;

    internal static UIWorldPassManifest? Active
    {
        get => t_active;
        set => t_active = value;
    }

    internal int NodeCount => _count;
    internal string? Failure => _failure;
    internal UIWorldPassManifestScope EnterScope(ulong token)
    {
        if (token == 0 || Volatile.Read(ref _activeAttemptToken) != (long)token ||
            _ownerThreadId != Environment.CurrentManagedThreadId ||
            Volatile.Read(ref _captureStarted) == 0 || !_packetSetSealed)
            throw new InvalidOperationException("UI world attempt is not reserved and captured.");
        return new(this);
    }

    internal bool TryReserveAttempt(out ulong token)
    {
        token = 0;
        if (Interlocked.CompareExchange(ref _activeAttemptToken, -1, 0) != 0)
            return false;
        long next = Interlocked.Increment(ref s_nextAttemptToken);
        if (next <= 0)
        {
            Volatile.Write(ref _activeAttemptToken, 0);
            return false;
        }
        _ownerThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _captureStarted, 0);
        Volatile.Write(ref _activeAttemptToken, next);
        token = (ulong)next;
        return true;
    }

    internal bool ReleaseAttempt(ulong token)
    {
        if (token == 0 || Volatile.Read(ref _activeAttemptToken) != (long)token ||
            _ownerThreadId != Environment.CurrentManagedThreadId ||
            ReferenceEquals(Active, this))
            return false;
        Volatile.Write(ref _activeAttemptToken, -1);
        Volatile.Write(ref _captureStarted, 0);
        _ownerThreadId = 0;
        try
        {
            ReleasePacketClaims();
        }
        finally
        {
            _packetSetSealed = false;
            Volatile.Write(ref _activeAttemptToken, 0);
        }
        return true;
    }

    /// <summary>Holds the selected packet through all shadow and color pass decisions.</summary>
    internal bool TryBindPublication(object canvasKey, UIPublication publication)
    {
        ArgumentNullException.ThrowIfNull(canvasKey);
        if (Volatile.Read(ref _activeAttemptToken) <= 0 ||
            _ownerThreadId != Environment.CurrentManagedThreadId ||
            Volatile.Read(ref _captureStarted) == 0)
            return Fail("UI world packet has no active owner attempt.");
        for (int index = 0; index < _packetCount; index++)
            if (ReferenceEquals(_canvasKeys[index], canvasKey))
                return ReferenceEquals(_packets[index], publication) ||
                    Fail("UI world canvas selected a different packet.");
        if (_packetSetSealed)
            return Fail("UI world canvas packet set changed after authoring began.");
        if (_packetCount == MaximumCanvasPackets)
            return Fail("UI world attempt exceeds its canvas packet limit.");
        publication.Retain();
        _canvasKeys[_packetCount] = canvasKey;
        _packets[_packetCount++] = publication;
        return true;
    }

    internal bool SealPacketSet()
    {
        if (Volatile.Read(ref _activeAttemptToken) <= 0 ||
            _ownerThreadId != Environment.CurrentManagedThreadId ||
            Volatile.Read(ref _captureStarted) == 0)
            return Fail("UI world packet set has no captured owner attempt.");
        _packetSetSealed = true;
        return !_failed;
    }

    internal bool TryGetPublication(object canvasKey, out UIPublication? publication)
    {
        if (Volatile.Read(ref _activeAttemptToken) <= 0 ||
            _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            publication = null;
            return false;
        }
        for (int index = 0; index < _packetCount; index++)
            if (ReferenceEquals(_canvasKeys[index], canvasKey))
            {
                publication = _packets[index];
                return publication is not null;
            }
        publication = null;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _activeAttemptToken, -1, 0) != 0)
            throw new InvalidOperationException("A reserved UI world attempt must end through its token.");
        try
        {
            ReleasePacketClaims();
            Array.Clear(_nodes, 0, _count);
            Array.Clear(_switchCases, 0, _switchCaseCount);
            _nodeIndices.Clear();
            _root = null;
            _count = 0;
            _switchCaseCount = 0;
        }
        finally
        {
            Volatile.Write(ref _activeAttemptToken, 0);
        }
    }

    private void ReleasePacketClaims()
    {
        int count = _packetCount;
        _packetCount = 0;
        for (int index = 0; index < count; index++)
        {
            UIPublication? packet = _packets[index];
            _packets[index] = null;
            _canvasKeys[index] = null;
            packet?.Release();
        }
    }

    internal bool TryCaptureReserved(ViewportRenderCommandContainer root, ulong token,
        out string? failure)
    {
        if (token == 0 || Volatile.Read(ref _activeAttemptToken) != (long)token ||
            _ownerThreadId != Environment.CurrentManagedThreadId ||
            Interlocked.CompareExchange(ref _captureStarted, 1, 0) != 0)
        {
            failure = "UI world attempt was not reserved or was already captured.";
            return false;
        }
        ReleasePacketClaims();
        Array.Clear(_nodes, 0, _count);
        Array.Clear(_switchCases, 0, _switchCaseCount);
        _root = root;
        _rootCommandCount = root.Commands.Count;
        _count = 0;
        _switchCaseCount = 0;
        _packetSetSealed = false;
        _nodeIndices.Clear();
        _failed = false;
        _failure = null;
        if (!CaptureContainer(root, -1, 0, 0))
        {
            failure = _failure;
            return false;
        }

        failure = null;
        return true;
    }

    private bool CaptureContainer(ViewportRenderCommandContainer container,
        int parentDecisionIndex, int parentChoice, int depth)
    {
        if (depth > MaximumDepth)
            return Fail("UI world command graph exceeds its branch depth limit.");

        IReadOnlyList<ViewportRenderCommand> commands = container.Commands;
        for (int commandIndex = 0; commandIndex < commands.Count; commandIndex++)
        {
            if (_count == MaximumNodes)
                return Fail("UI world command graph exceeds its node limit.");

            ViewportRenderCommand command = commands[commandIndex];
            if (_nodeIndices.ContainsKey(command))
                return Fail("UI world command graph contains a repeated command instance.");
            if (command is VPRC_Repeat or VPRC_ConditionalRender or
                VPRC_ForEachCascade or VPRC_RenderMeshesFiltered or VPRC_RenderByRenderGroup)
                return Fail($"UI world command graph does not support {command.GetType().Name}.");
            Type commandType = command.GetType();
            if (commandType.IsGenericType &&
                commandType.GetGenericTypeDefinition() == typeof(VPRC_ForEach<>))
                return Fail("UI world command graph does not support dynamic iteration.");

            int nodeIndex = _count++;
            _nodeIndices.Add(command, nodeIndex);
            VPRC_IfElse? branch = command as VPRC_IfElse;
            VPRC_Switch? switchCommand = command as VPRC_Switch;
            _nodes[nodeIndex] = new UIWorldPassManifestNode
            {
                Command = command,
                Container = container,
                ContainerIndex = commandIndex,
                ContainerCommandCount = commands.Count,
                ParentDecisionIndex = parentDecisionIndex,
                ParentChoice = parentChoice,
                Branch = branch,
                Condition = branch?.ConditionEvaluator,
                TrueCommands = branch?.TrueCommands,
                FalseCommands = branch?.FalseCommands,
                TrueCommandCount = branch?.TrueCommands?.Commands.Count ?? -1,
                FalseCommandCount = branch?.FalseCommands?.Commands.Count ?? -1,
                Switch = switchCommand,
                SwitchEvaluator = switchCommand?.SwitchEvaluator,
                SwitchCases = switchCommand?.Cases,
                DefaultCase = switchCommand?.DefaultCase,
                DefaultCommandCount = switchCommand?.DefaultCase?.Commands.Count ?? -1,
                SwitchCaseStart = _switchCaseCount,
                SwitchCaseCount = switchCommand?.Cases?.Count ?? 0,
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

            if (branch is not null)
            {
                if (branch.TrueCommands is { } trueCommands &&
                    !CaptureContainer(trueCommands, nodeIndex, 1, depth + 1))
                    return false;
                if (branch.FalseCommands is { } falseCommands &&
                    !CaptureContainer(falseCommands, nodeIndex, 0, depth + 1))
                    return false;
            }
            if (switchCommand is null)
                continue;
            Dictionary<int, ViewportRenderCommandContainer>? cases = switchCommand.Cases;
            int firstCase = _switchCaseCount;
            if (cases is not null)
                foreach ((int key, ViewportRenderCommandContainer selected) in cases)
                {
                    if (_switchCaseCount == MaximumSwitchCases)
                        return Fail("UI world command graph exceeds its switch case limit.");
                    if (selected is null)
                        return Fail("UI world switch has a null case container.");
                    _switchCases[_switchCaseCount++] = new(key, selected,
                        selected.Commands.Count);
                }
            int caseCount = _switchCaseCount - firstCase;
            if (caseCount != _nodes[nodeIndex].SwitchCaseCount)
                return Fail("UI world switch case map changed during capture.");
            for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
                if (!CaptureContainer(_switchCases[firstCase + caseIndex].Commands,
                        nodeIndex, caseIndex, depth + 1))
                    return false;
            if (switchCommand.DefaultCase is { } defaultCase &&
                !CaptureContainer(defaultCase, nodeIndex, caseCount, depth + 1))
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
            _nodes[index].Branch is null || _nodes[index].DecisionRecorded)
        {
            Fail("UI world branch did not match the selected graph.");
            return;
        }
        _nodes[index].SelectedChoice = selectedTrue ? 1 : 0;
        _nodes[index].DecisionRecorded = true;
    }

    /// <summary>Records a frozen case before switch resource callbacks run.</summary>
    internal void RecordSwitch(VPRC_Switch switchCommand, int key,
        ViewportRenderCommandContainer? selected)
    {
        int index = Find(switchCommand);
        if (index < 0 || !ValidateNode(index) || !IsReachable(index) ||
            _nodes[index].Switch is null || _nodes[index].DecisionRecorded)
        {
            Fail("UI world switch did not match the selected graph.");
            return;
        }
        ref UIWorldPassManifestNode node = ref _nodes[index];
        int choice = node.SwitchCaseCount;
        ViewportRenderCommandContainer? expected = node.DefaultCase;
        for (int caseIndex = 0; caseIndex < node.SwitchCaseCount; caseIndex++)
        {
            UIWorldPassManifestSwitchCase entry = _switchCases[node.SwitchCaseStart + caseIndex];
            if (entry.Key != key)
                continue;
            choice = caseIndex;
            expected = entry.Commands;
            break;
        }
        if (!ReferenceEquals(expected, selected))
        {
            Fail("UI world switch selected a different command container.");
            return;
        }
        node.SelectedChoice = choice;
        node.DecisionRecorded = true;
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
        if (node.Branch is not null || node.Switch is not null)
        {
            if (!node.DecisionRecorded)
                Fail("UI world decision completed without a selected child.");
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
        else if (node.Pass is not null || node.Branch is not null || node.Switch is not null)
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
            if ((node.Branch is not null || node.Switch is not null) &&
                !node.DecisionRecorded)
                Fail("UI world branch has no execution decision.");
            else if (node.Pass is not null && (node.ExpectedUiDraws < 0 ||
                node.State is not (EUIWorldPassNodeState.Empty or EUIWorldPassNodeState.Executed)))
                Fail("UI world selected mesh pass did not complete.");
        }
        failure = _failure;
        return !_failed;
    }

    private int Find(ViewportRenderCommand command)
        => _nodeIndices.TryGetValue(command, out int index) ? index : -1;

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
        if (node.Switch is { } switchCommand)
        {
            if (!ReferenceEquals(node.SwitchEvaluator, switchCommand.SwitchEvaluator) ||
                !ReferenceEquals(node.SwitchCases, switchCommand.Cases) ||
                !ReferenceEquals(node.DefaultCase, switchCommand.DefaultCase) ||
                node.DefaultCommandCount !=
                    (switchCommand.DefaultCase?.Commands.Count ?? -1) ||
                node.SwitchCaseCount != (switchCommand.Cases?.Count ?? 0))
                return Fail("UI world switch configuration changed after capture.");
            for (int caseIndex = 0; caseIndex < node.SwitchCaseCount; caseIndex++)
            {
                UIWorldPassManifestSwitchCase entry =
                    _switchCases[node.SwitchCaseStart + caseIndex];
                if (switchCommand.Cases is null ||
                    !switchCommand.Cases.TryGetValue(entry.Key, out var current) ||
                    !ReferenceEquals(current, entry.Commands) ||
                    current.Commands.Count != entry.CommandCount)
                    return Fail("UI world switch case map changed after capture.");
            }
        }
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
        if (node.ParentDecisionIndex < 0)
            return true;
        ref UIWorldPassManifestNode parent = ref _nodes[node.ParentDecisionIndex];
        if (!IsReachable(node.ParentDecisionIndex))
            return false;
        return parent.DecisionRecorded && parent.SelectedChoice == node.ParentChoice;
    }

    private bool Fail(string message)
    {
        _failed = true;
        _failure ??= message;
        return false;
    }
}
