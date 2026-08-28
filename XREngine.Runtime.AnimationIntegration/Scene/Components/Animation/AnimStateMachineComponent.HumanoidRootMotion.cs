using XREngine.Animation;
using XREngine.Components.Animation;

namespace XREngine.Components;

public partial class AnimStateMachineComponent
{
    private HumanoidStateMachineRootMotionLeafState[] _rootMotionLeafStates = [];
    private bool[] _rootMotionLeafStatesUsedThisFrame = [];
    private HumanoidStateMachineRootMotionFrame? _rootMotionFrame;
    private int _rootMotionLeafStateCount;
    private int _rootMotionContributorCount;
    private long _dominantRootMotionLoopCycle;
    private ulong _observedMotionContinuityVersion;
    private bool _hasStateMachineRootMotion;

    public int RootMotionContributorCount => _rootMotionContributorCount;

    private void InitializeStateMachineRootMotionPipeline()
    {
        int capacity = StateMachine.HumanoidMotionContributionCapacity;
        _rootMotionLeafStates = capacity > 0
            ? new HumanoidStateMachineRootMotionLeafState[capacity]
            : [];
        for (int i = 0; i < _rootMotionLeafStates.Length; i++)
            _rootMotionLeafStates[i] = new HumanoidStateMachineRootMotionLeafState();
        _rootMotionLeafStatesUsedThisFrame = capacity > 0
            ? new bool[capacity]
            : [];
        _rootMotionFrame = new HumanoidStateMachineRootMotionFrame(capacity);
        _rootMotionLeafStateCount = 0;
        _rootMotionContributorCount = 0;
        _dominantRootMotionLoopCycle = 0L;
        _hasStateMachineRootMotion = false;
        _observedMotionContinuityVersion = StateMachine.HumanoidMotionContinuityVersion;
    }

    private bool PrepareStateMachineRootMotionFrame(HumanoidComponent? humanoid)
    {
        if (humanoid is null)
            return true;

        if (_rootMotionFrame is null
            || _rootMotionLeafStates.Length != StateMachine.HumanoidMotionContributionCapacity)
            InitializeStateMachineRootMotionPipeline();

        HumanoidStateMachineRootMotionFrame frame = _rootMotionFrame!;
        frame.Clear();
        Array.Clear(_rootMotionLeafStatesUsedThisFrame);
        _rootMotionContributorCount = 0;
        _dominantRootMotionLoopCycle = 0L;
        if (StateMachine.HumanoidMotionContributionsOverflowed)
        {
            PlaybackCapabilityDiagnostic =
                "The state-machine humanoid contribution frame overflowed its initialization capacity. "
                + "Reinitialize the graph after changing states, layers, or blend-tree children.";
            return false;
        }

        float dominantWeight = float.NegativeInfinity;
        ReadOnlySpan<HumanoidMotionContribution> contributions =
            StateMachine.HumanoidMotionContributions;
        for (int i = 0; i < contributions.Length; i++)
        {
            HumanoidMotionContribution contribution = contributions[i];
            if (!float.IsFinite(contribution.Weight) || contribution.Weight <= 0.0f)
                continue;

            HumanoidStateMachineRootMotionLeafState? state = FindOrAssignRootMotionLeafState(
                contribution.OccurrenceId,
                contribution.LifecycleGeneration);
            if (state is null)
            {
                PlaybackCapabilityDiagnostic =
                    "The state-machine humanoid contribution frame exceeded its initialization capacity. "
                    + "Reinitialize the graph after changing states or blend-tree children.";
                return false;
            }

            if (!state.TryPrepare(contribution, humanoid) || !frame.TryAdd(state))
            {
                PlaybackCapabilityDiagnostic =
                    $"Failed to prepare Unity humanoid Body/root leaf occurrence {contribution.OccurrenceId:X16}.";
                return false;
            }

            _rootMotionContributorCount++;
            if (contribution.Weight > dominantWeight)
            {
                dominantWeight = contribution.Weight;
                _dominantRootMotionLoopCycle = contribution.SourceLoopCycle;
            }
        }

        _hasStateMachineRootMotion = _rootMotionContributorCount > 0;
        if (_observedMotionContinuityVersion != StateMachine.HumanoidMotionContinuityVersion)
        {
            _observedMotionContinuityVersion = StateMachine.HumanoidMotionContinuityVersion;
            BeginRootMotionEpoch(rebaseFromNextPose: true);
        }

        humanoid.StageStateMachineRootMotionFrame(this, frame);
        return true;
    }

    private HumanoidStateMachineRootMotionLeafState? FindOrAssignRootMotionLeafState(
        ulong occurrenceId,
        ulong lifecycleGeneration)
    {
        for (int i = 0; i < _rootMotionLeafStateCount; i++)
            if (!_rootMotionLeafStatesUsedThisFrame[i]
                && _rootMotionLeafStates[i].Matches(occurrenceId, lifecycleGeneration))
            {
                _rootMotionLeafStatesUsedThisFrame[i] = true;
                return _rootMotionLeafStates[i];
            }

        // Lifecycle generations are frame-local identities, not permanent cache
        // reservations. Reuse the first inactive slot before growing the high-water mark.
        for (int i = 0; i < _rootMotionLeafStateCount; i++)
        {
            if (_rootMotionLeafStatesUsedThisFrame[i])
                continue;

            _rootMotionLeafStatesUsedThisFrame[i] = true;
            return _rootMotionLeafStates[i];
        }

        if (_rootMotionLeafStateCount >= _rootMotionLeafStates.Length)
            return null;

        int assignedIndex = _rootMotionLeafStateCount++;
        _rootMotionLeafStatesUsedThisFrame[assignedIndex] = true;
        return _rootMotionLeafStates[assignedIndex];
    }

    private void ClearStateMachineRootMotionPipeline(HumanoidComponent? humanoid)
    {
        humanoid?.ClearStateMachineRootMotionFrame(this);
        _rootMotionFrame?.Clear();
        _rootMotionLeafStates = [];
        _rootMotionLeafStatesUsedThisFrame = [];
        _rootMotionFrame = null;
        _rootMotionLeafStateCount = 0;
        _rootMotionContributorCount = 0;
        _dominantRootMotionLoopCycle = 0L;
        _hasStateMachineRootMotion = false;
    }
}
