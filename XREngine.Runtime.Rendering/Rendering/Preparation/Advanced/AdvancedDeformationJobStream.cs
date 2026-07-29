using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity aggregate job builder with collision-safe deduplication,
/// whole-mesh admission, and budget-only optional-work ranking.
/// </summary>
public sealed class AdvancedDeformationJobStream
{
    private readonly AdvancedDeformationCandidate[] _candidates;
    private readonly AdvancedDeformationJobRecord[] _jobs;
    private readonly bool[] _candidateAdmission;
    private readonly int[] _hashSlots;
    private readonly int[] _optionalIndices;
    private int _candidateCount;
    private int _jobCount;
    private uint _deduplicatedCount;
    private uint _candidateOverflowCount;
    private uint _visibleCandidateOverflowCount;

    public AdvancedDeformationJobStream(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _candidates = new AdvancedDeformationCandidate[capacity];
        _jobs = new AdvancedDeformationJobRecord[capacity];
        _candidateAdmission = new bool[capacity];
        _optionalIndices = new int[capacity];
        _hashSlots = new int[NextPowerOfTwo(checked(capacity * 2))];
    }

    public int Capacity => _jobs.Length;
    public int CandidateCount => _candidateCount;
    public int JobCount => _jobCount;
    public ReadOnlySpan<AdvancedDeformationJobRecord> Jobs
        => _jobs.AsSpan(0, _jobCount);

    public void BeginFrame()
    {
        Array.Clear(_hashSlots);
        _candidateCount = 0;
        _jobCount = 0;
        _deduplicatedCount = 0u;
        _candidateOverflowCount = 0u;
        _visibleCandidateOverflowCount = 0u;
    }

    /// <summary>
    /// Reports whether a canonical candidate survived whole-job admission.
    /// Duplicate draws use the canonical index returned by <see cref="TryAdd"/>
    /// and therefore receive the same verdict.
    /// </summary>
    public bool IsCandidateAdmitted(int canonicalCandidateIndex)
        => (uint)canonicalCandidateIndex < (uint)_candidateCount &&
           _candidateAdmission[canonicalCandidateIndex];

    public bool TryAdd(
        in AdvancedDeformationCandidate candidate,
        out int canonicalCandidateIndex)
    {
        ValidateCandidate(candidate);
        uint mask = checked((uint)_hashSlots.Length - 1u);
        uint start = Hash(candidate.Key) & mask;
        for (uint probe = 0u; probe < (uint)_hashSlots.Length; probe++)
        {
            int hashSlot = checked((int)((start + probe) & mask));
            int stored = _hashSlots[hashSlot];
            if (stored != 0)
            {
                int existingIndex = stored - 1;
                if (_candidates[existingIndex].Key == candidate.Key)
                {
                    canonicalCandidateIndex = existingIndex;
                    _deduplicatedCount++;
                    return true;
                }
                continue;
            }

            if (_candidateCount >= _candidates.Length)
            {
                canonicalCandidateIndex = -1;
                _candidateOverflowCount++;
                if (candidate.Visible)
                    _visibleCandidateOverflowCount++;
                return false;
            }

            int candidateIndex = _candidateCount++;
            _candidates[candidateIndex] = candidate;
            _hashSlots[hashSlot] = candidateIndex + 1;
            canonicalCandidateIndex = candidateIndex;
            return true;
        }

        canonicalCandidateIndex = -1;
        _candidateOverflowCount++;
        if (candidate.Visible)
            _visibleCandidateOverflowCount++;
        return false;
    }

    public AdvancedDeformationAdmissionResult FinalizeJobs(
        in AdvancedDeformationBudget budget)
    {
        _jobCount = 0;
        Array.Clear(_candidateAdmission, 0, _candidateCount);
        ulong admittedVertices = 0UL;
        ulong admittedBytes = 0UL;
        uint rejected = _candidateOverflowCount;
        uint visibleFallback = _visibleCandidateOverflowCount;

        bool budgetExceeded = WouldExceedBudget(budget);
        for (int i = 0; i < _candidateCount; i++)
        {
            AdvancedDeformationCandidate candidate = _candidates[i];
            if (!candidate.Mandatory)
                continue;

            if (TryAdmit(
                    i,
                    candidate,
                    budget,
                    ref admittedVertices,
                    ref admittedBytes))
                continue;

            rejected++;
            if (candidate.Visible)
                visibleFallback++;
            budgetExceeded = true;
        }

        int optionalCount = 0;
        for (int i = 0; i < _candidateCount; i++)
        {
            if (!_candidates[i].Mandatory)
                _optionalIndices[optionalCount++] = i;
        }

        if (budgetExceeded)
            SortOptionalByContribution(optionalCount);

        for (int optional = 0; optional < optionalCount; optional++)
        {
            AdvancedDeformationCandidate candidate =
                _candidates[_optionalIndices[optional]];
            if (TryAdmit(
                    _optionalIndices[optional],
                    candidate,
                    budget,
                    ref admittedVertices,
                    ref admittedBytes))
                continue;

            rejected++;
            if (candidate.Visible)
                visibleFallback++;
            budgetExceeded = true;
        }

        return new AdvancedDeformationAdmissionResult(
            CandidateCount: checked((uint)_candidateCount) + _deduplicatedCount +
                _candidateOverflowCount,
            DeduplicatedCount: _deduplicatedCount,
            AdmittedJobCount: checked((uint)_jobCount),
            RejectedJobCount: rejected,
            VisibleFallbackCount: visibleFallback,
            AdmittedVertexCount: admittedVertices,
            AdmittedOutputBytes: admittedBytes,
            BudgetExceeded: budgetExceeded,
            OverflowBehavior: budget.OverflowBehavior);
    }

    public bool TryUpload(
        AdvancedFrameSlotUploadArena arena,
        out AdvancedFrameUploadAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(arena);
        uint byteCount = checked((uint)(
            _jobCount * Unsafe.SizeOf<AdvancedDeformationJobRecord>()));
        if (!arena.TryAllocate(
                EAdvancedFrameUploadStream.DeformationJob,
                byteCount,
                alignmentBytes: 16u,
                out allocation))
        {
            return false;
        }

        MemoryMarshal.AsBytes(_jobs.AsSpan(0, _jobCount))
            .CopyTo(allocation.Span);
        return true;
    }

    private bool WouldExceedBudget(in AdvancedDeformationBudget budget)
    {
        ulong vertices = 0UL;
        ulong bytes = 0UL;
        for (int i = 0; i < _candidateCount; i++)
        {
            AdvancedDeformationJobRecord job = _candidates[i].Job;
            vertices += job.VertexCount;
            bytes += (ulong)job.VertexCount * job.OutputStride;
        }

        return IsLimitExceeded(checked((uint)_candidateCount), budget.MaximumJobs) ||
               IsLimitExceeded(vertices, budget.MaximumVertices) ||
               IsLimitExceeded(bytes, budget.MaximumOutputBytes);
    }

    private bool TryAdmit(
        int candidateIndex,
        in AdvancedDeformationCandidate candidate,
        in AdvancedDeformationBudget budget,
        ref ulong admittedVertices,
        ref ulong admittedBytes)
    {
        AdvancedDeformationJobRecord job = candidate.Job;
        ulong jobBytes = (ulong)job.VertexCount * job.OutputStride;
        uint nextJobCount = checked((uint)_jobCount + 1u);
        ulong nextVertices = admittedVertices + job.VertexCount;
        ulong nextBytes = admittedBytes + jobBytes;
        if (IsLimitExceeded(nextJobCount, budget.MaximumJobs) ||
            IsLimitExceeded(nextVertices, budget.MaximumVertices) ||
            IsLimitExceeded(nextBytes, budget.MaximumOutputBytes))
        {
            return false;
        }

        _jobs[_jobCount++] = job;
        _candidateAdmission[candidateIndex] = true;
        admittedVertices = nextVertices;
        admittedBytes = nextBytes;
        return true;
    }

    private void SortOptionalByContribution(int count)
    {
        for (int i = 1; i < count; i++)
        {
            int candidateIndex = _optionalIndices[i];
            float contribution = _candidates[candidateIndex].ProjectedContribution;
            int destination = i - 1;
            while (destination >= 0)
            {
                int priorCandidate = _optionalIndices[destination];
                float priorContribution =
                    _candidates[priorCandidate].ProjectedContribution;
                if (priorContribution > contribution ||
                    (priorContribution == contribution &&
                     priorCandidate < candidateIndex))
                {
                    break;
                }

                _optionalIndices[destination + 1] = priorCandidate;
                destination--;
            }

            _optionalIndices[destination + 1] = candidateIndex;
        }
    }

    private static void ValidateCandidate(
        in AdvancedDeformationCandidate candidate)
    {
        if (!candidate.Key.Mesh.IsValid ||
            !candidate.Key.SharedPose.IsValid ||
            candidate.Job.VertexCount == 0u ||
            candidate.Job.OutputStride == 0u ||
            candidate.Job.Order != EAdvancedDeformationOrder.BlendshapeThenSkinning)
        {
            throw new ArgumentException(
                "A deformation candidate requires valid mesh/pose handles, a complete vertex range, and the canonical operation order.",
                nameof(candidate));
        }
    }

    private static bool IsLimitExceeded(uint value, uint limit)
        => limit != 0u && value > limit;

    private static bool IsLimitExceeded(ulong value, ulong limit)
        => limit != 0UL && value > limit;

    private static uint Hash(in AdvancedDeformationJobKey key)
    {
        uint hash = 2166136261u;
        HashValue(ref hash, key.Mesh.Index);
        HashValue(ref hash, key.Mesh.Generation);
        HashValue(ref hash, key.SharedPose.Index);
        HashValue(ref hash, key.SharedPose.Generation);
        HashValue(ref hash, key.MeshGeneration);
        HashValue(ref hash, key.PoseGeneration);
        HashValue(ref hash, key.PaletteGeneration);
        HashValue(ref hash, key.TopologyGeneration);
        HashValue(ref hash, (uint)key.VertexLayoutId);
        HashValue(ref hash, (uint)(key.VertexLayoutId >> 32));
        HashValue(ref hash, (uint)key.Features);
        HashValue(ref hash, (uint)key.Precision);
        return hash;
    }

    private static void HashValue(ref uint hash, uint value)
    {
        hash ^= value;
        hash *= 16777619u;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result = checked(result << 1);
        return result;
    }
}
