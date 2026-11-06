using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Components.Particles;

namespace XREngine.Cutscenes;

/// <summary>
/// Applies the policies of the <see cref="SimulationControlTrack"/> entries of a cutscene. The player calls
/// <see cref="ApplyTick"/> once at the end of each tick, so all resets and warm-ups occur in the normal tick and never in a
/// seek call. One discontinuity gives one reset of each simulation, also when two tracks bind the same node or a cut and a
/// jump occur in the same tick.
/// </summary>
/// <remarks>
/// <para>After a jump, a cut, or a hard resync, each simulation resets to the current pose. A
/// <see cref="ECutsceneSimulationPolicy.WarmUp"/> track then starts its warm-up.</para>
/// <para>After a scrub step, the simulations reset and hold. A held simulation stays at the reset pose until the player
/// plays at a rate within <see cref="ResumeRateTolerance"/> of one, <see cref="CutscenePlayer.EndScrub"/> ends the scrub,
/// or a jump occurs. A warm-up of a held track waits for the release.</para>
/// <para>A tick with no discontinuity and no hold does no work and does not allocate.</para>
/// </remarks>
public sealed class SimulationControlEvaluator
{
    /// <summary>The largest difference of the rate from one at which a held simulation starts to run again.</summary>
    public const double ResumeRateTolerance = 0.05;

    private const int ProblemNoTargets = 0;
    private const int ProblemSharedTarget = 1;
    private const int ProblemNoWarmUp = 2;

    private struct Entry
    {
        public ICutsceneSimulationTarget Target;
        public int TrackIndex;
        public bool WarmUpPending;
    }

    private readonly CompiledCutscene _compiled;
    private readonly CutsceneBindingTable _bindings;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly int[] _trackIndices;
    private readonly SceneNode?[] _nodes;
    private readonly List<ICutsceneSimulationTarget> _scratch = [];
    private readonly HashSet<(int TrackIndex, int Problem)> _reported = [];
    private Entry[] _entries = [];
    private int _entryCount;
    private bool _resolved;
    private bool _held;
    private bool _wasScrubbing;

    /// <summary>Creates an evaluator for the simulation tracks of a cutscene.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public SimulationControlEvaluator(CompiledCutscene compiled, CutsceneBindingTable bindings, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _compiled = compiled;
        _bindings = bindings;
        _diagnostics = diagnostics;

        IReadOnlyList<CutsceneTrack> tracks = compiled.Asset.Tracks;
        int count = 0;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i] is SimulationControlTrack)
                count++;

        _trackIndices = new int[count];
        _nodes = new SceneNode?[count];
        count = 0;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i] is SimulationControlTrack)
                _trackIndices[count++] = i;
    }

    /// <summary>
    /// Finds the simulation targets on a bound node. The default finds each <see cref="PhysicsChainComponent"/> and each
    /// <see cref="ParticleEmitterComponent"/> of the node. The evaluator calls it only when a bound node changes.
    /// </summary>
    internal Action<SceneNode, List<ICutsceneSimulationTarget>> TargetResolver { get; set; } = CollectTargets;

    /// <summary>True while the simulations hold the reset pose of a scrub.</summary>
    public bool IsHeld => _held;

    /// <summary>
    /// Applies the policies for one tick. The player calls it once at the end of each tick.
    /// </summary>
    /// <param name="discontinuity">The intent of the discontinuity of this tick, or null if the tick had none.</param>
    /// <param name="state">The transport state after the tick.</param>
    /// <param name="rate">The playback rate after the tick.</param>
    public void ApplyTick(ESeekIntent? discontinuity, ECutsceneTransportState state, double rate)
    {
        bool scrubbing = state == ECutsceneTransportState.Scrubbing;
        bool scrubEnded = _wasScrubbing && !scrubbing;
        _wasScrubbing = scrubbing;
        if (_trackIndices.Length == 0 || (discontinuity is null && !_held))
            return;

        RefreshTargets();

        if (discontinuity is ESeekIntent intent)
        {
            if (intent == ESeekIntent.Scrub || scrubbing)
                ResetAndHold();
            else
                ResetAndRun();
            return;
        }

        bool release = scrubEnded || (state == ECutsceneTransportState.Playing && Math.Abs(rate - 1.0) <= ResumeRateTolerance);
        if (release)
        {
            SetHeld(false);
            StartPendingWarmUps();
            return;
        }

        for (int i = 0; i < _entryCount; i++)
            _entries[i].Target.KeepHeld();
    }

    /// <summary>
    /// Releases a hold and forgets pending warm-ups. The player calls it at a stop, so a held simulation runs again
    /// and the emitters get their own simulation setting back.
    /// </summary>
    public void Release()
    {
        SetHeld(false);
        for (int i = 0; i < _entryCount; i++)
            _entries[i].WarmUpPending = false;
    }

    private void ResetAndHold()
    {
        SetHeld(true);
        for (int i = 0; i < _entryCount; i++)
        {
            ref Entry entry = ref _entries[i];
            if (!TryGetTrack(entry.TrackIndex, out SimulationControlTrack? track))
                continue;
            entry.Target.ResetToCurrentPose(track.Seed);
            entry.WarmUpPending = track.Policy == ECutsceneSimulationPolicy.WarmUp;
        }
    }

    private void ResetAndRun()
    {
        SetHeld(false);
        for (int i = 0; i < _entryCount; i++)
        {
            ref Entry entry = ref _entries[i];
            if (!TryGetTrack(entry.TrackIndex, out SimulationControlTrack? track))
                continue;
            entry.WarmUpPending = false;
            if (track.Policy == ECutsceneSimulationPolicy.WarmUp)
                StartWarmUp(ref entry, track);
            else
                entry.Target.ResetToCurrentPose(track.Seed);
        }
    }

    private void StartPendingWarmUps()
    {
        for (int i = 0; i < _entryCount; i++)
        {
            ref Entry entry = ref _entries[i];
            if (!entry.WarmUpPending)
                continue;
            entry.WarmUpPending = false;
            if (TryGetTrack(entry.TrackIndex, out SimulationControlTrack? track))
                StartWarmUp(ref entry, track);
        }
    }

    // A warm-up includes the reset. A target with no warm-up settles from a reset and gets one diagnostic for its track.
    private void StartWarmUp(ref Entry entry, SimulationControlTrack track)
    {
        if (!entry.Target.SupportsWarmUp)
        {
            entry.Target.ResetToCurrentPose(track.Seed);
            if (ShouldReport(entry.TrackIndex, ProblemNoWarmUp))
                Report(entry.TrackIndex, track, $"The {entry.Target.Name} has no warm-up. It settles from the reset.");
            return;
        }

        double seconds = double.IsFinite(track.WarmUpSeconds) ? Math.Max(0.0, track.WarmUpSeconds) : 0.0;
        entry.Target.WarmUp((float)seconds, track.Seed, track.WarmUpBudgetMilliseconds);
    }

    private void SetHeld(bool held)
    {
        if (held == _held)
            return;

        _held = held;
        for (int i = 0; i < _entryCount; i++)
            _entries[i].Target.SetHeld(held);
    }

    // The asset can change after the open. A track that is not a simulation track any more is skipped.
    private bool TryGetTrack(int trackIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SimulationControlTrack? track)
    {
        List<CutsceneTrack> tracks = _compiled.Asset.Tracks;
        track = (uint)trackIndex < (uint)tracks.Count ? tracks[trackIndex] as SimulationControlTrack : null;
        return track is not null;
    }

    // The targets are found again only when a bound node changes, so a normal tick does not allocate.
    private void RefreshTargets()
    {
        bool changed = !_resolved;
        for (int i = 0; i < _trackIndices.Length; i++)
        {
            SceneNode? node = TryGetTrack(_trackIndices[i], out SimulationControlTrack? track) ? _bindings.GetNode(track.BindingSlot) : null;
            if (!ReferenceEquals(node, _nodes[i]))
            {
                _nodes[i] = node;
                changed = true;
            }
        }
        if (!changed)
            return;

        _resolved = true;

        // A hold belongs to the old targets, so they get their own state back first.
        bool held = _held;
        SetHeld(false);
        _entryCount = 0;
        for (int i = 0; i < _trackIndices.Length; i++)
        {
            SceneNode? node = _nodes[i];
            if (node is null)
                continue;

            int trackIndex = _trackIndices[i];
            if (!TryGetTrack(trackIndex, out SimulationControlTrack? track))
                continue;

            _scratch.Clear();
            TargetResolver(node, _scratch);
            if (_scratch.Count == 0 && ShouldReport(trackIndex, ProblemNoTargets))
                Report(trackIndex, track, $"The node '{node.Name}' has no physics chain and no particle emitter. The track does nothing.");

            for (int t = 0; t < _scratch.Count; t++)
            {
                ICutsceneSimulationTarget target = _scratch[t];
                int owner = FindOwner(target.Source);
                if (owner >= 0)
                {
                    if (ShouldReport(trackIndex, ProblemSharedTarget))
                        Report(trackIndex, track, $"The {target.Name} is also on track {owner}. Only track {owner} controls it.");
                    continue;
                }
                Add(target, trackIndex);
            }
        }
        _scratch.Clear();
        SetHeld(held);
    }

    private int FindOwner(object source)
    {
        for (int i = 0; i < _entryCount; i++)
            if (ReferenceEquals(_entries[i].Target.Source, source))
                return _entries[i].TrackIndex;
        return -1;
    }

    private void Add(ICutsceneSimulationTarget target, int trackIndex)
    {
        if (_entryCount == _entries.Length)
            Array.Resize(ref _entries, Math.Max(4, _entries.Length * 2));
        _entries[_entryCount++] = new Entry { Target = target, TrackIndex = trackIndex };
    }

    // Each problem of a track is reported once. The check comes before the message text, so a repeat does not allocate.
    private bool ShouldReport(int trackIndex, int problem)
        => _reported.Add((trackIndex, problem));

    private void Report(int trackIndex, SimulationControlTrack track, string message)
    {
        string text = $"Simulation track '{track.Name}': {message}";
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, -1, text));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene evaluation: {0}", text);
    }

    private static void CollectTargets(SceneNode node, List<ICutsceneSimulationTarget> targets)
    {
        foreach (PhysicsChainComponent chain in node.GetComponents<PhysicsChainComponent>())
            targets.Add(new PhysicsChainSimulationTarget(chain));
        foreach (ParticleEmitterComponent emitter in node.GetComponents<ParticleEmitterComponent>())
            targets.Add(new ParticleEmitterSimulationTarget(emitter));
    }
}
