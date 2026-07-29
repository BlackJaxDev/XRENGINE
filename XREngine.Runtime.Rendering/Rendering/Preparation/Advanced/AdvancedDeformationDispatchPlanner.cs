namespace XREngine.Rendering;

/// <summary>
/// Groups jobs into a configured maximum number of layout/precision shader
/// families without allocating. Compatible N-mesh workloads produce one
/// dispatch regardless of N.
/// </summary>
public sealed class AdvancedDeformationDispatchPlanner
{
    public const uint ThreadGroupSize = 256u;

    private readonly AdvancedDeformationDispatchBatch[] _batches;
    private readonly int[] _jobIndices;
    private readonly uint[] _jobVertexOffsets;
    private readonly uint[] _writeCursors;
    private int _batchCount;
    private int _jobIndexCount;
    private uint _familyOverflowCount;

    public AdvancedDeformationDispatchPlanner(
        int maximumJobs,
        int maximumFamilies)
    {
        if (maximumJobs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        if (maximumFamilies <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFamilies));

        _batches = new AdvancedDeformationDispatchBatch[maximumFamilies];
        _jobIndices = new int[maximumJobs];
        _jobVertexOffsets = new uint[maximumJobs];
        _writeCursors = new uint[maximumFamilies];
    }

    public ReadOnlySpan<AdvancedDeformationDispatchBatch> Batches
        => _batches.AsSpan(0, _batchCount);
    public ReadOnlySpan<int> JobIndices
        => _jobIndices.AsSpan(0, _jobIndexCount);
    public ReadOnlySpan<uint> JobVertexOffsets
        => _jobVertexOffsets.AsSpan(0, _jobIndexCount);
    public uint FamilyOverflowCount => _familyOverflowCount;

    public bool Build(ReadOnlySpan<AdvancedDeformationJobRecord> jobs)
    {
        if (jobs.Length > _jobIndices.Length)
            throw new ArgumentOutOfRangeException(nameof(jobs));

        _batchCount = 0;
        _jobIndexCount = 0;
        _familyOverflowCount = 0u;

        for (int jobIndex = 0; jobIndex < jobs.Length; jobIndex++)
        {
            AdvancedDeformationJobRecord job = jobs[jobIndex];
            AdvancedDeformationDispatchKey key = GetKey(job);
            int batchIndex = FindBatch(key);
            if (batchIndex < 0)
            {
                if (_batchCount >= _batches.Length)
                {
                    _familyOverflowCount++;
                    continue;
                }

                batchIndex = _batchCount++;
                _batches[batchIndex] = new AdvancedDeformationDispatchBatch(
                    key,
                    FirstJobIndex: 0u,
                    JobCount: 0u,
                    VertexCount: 0UL,
                    WorkGroupCount: 0u);
            }

            AdvancedDeformationDispatchBatch batch = _batches[batchIndex];
            _batches[batchIndex] = batch with
            {
                JobCount = checked(batch.JobCount + 1u),
                VertexCount = batch.VertexCount + job.VertexCount,
            };
        }

        uint prefix = 0u;
        for (int batchIndex = 0; batchIndex < _batchCount; batchIndex++)
        {
            AdvancedDeformationDispatchBatch batch = _batches[batchIndex];
            _batches[batchIndex] = batch with
            {
                FirstJobIndex = prefix,
                WorkGroupCount = checked((uint)Math.Max(
                    1UL,
                    (batch.VertexCount + ThreadGroupSize - 1UL) /
                    ThreadGroupSize)),
            };
            _writeCursors[batchIndex] = prefix;
            prefix = checked(prefix + batch.JobCount);
        }

        for (int jobIndex = 0; jobIndex < jobs.Length; jobIndex++)
        {
            int batchIndex = FindBatch(GetKey(jobs[jobIndex]));
            if (batchIndex < 0)
                continue;
            uint destination = _writeCursors[batchIndex]++;
            _jobIndices[destination] = jobIndex;
        }

        _jobIndexCount = checked((int)prefix);
        for (int batchIndex = 0; batchIndex < _batchCount; batchIndex++)
        {
            AdvancedDeformationDispatchBatch batch = _batches[batchIndex];
            uint vertexOffset = 0u;
            for (uint localJob = 0u; localJob < batch.JobCount; localJob++)
            {
                uint groupedIndex = batch.FirstJobIndex + localJob;
                _jobVertexOffsets[groupedIndex] = vertexOffset;
                int sourceJob = _jobIndices[groupedIndex];
                vertexOffset = checked(
                    vertexOffset + jobs[sourceJob].VertexCount);
            }
        }
        return _familyOverflowCount == 0u;
    }

    private int FindBatch(in AdvancedDeformationDispatchKey key)
    {
        for (int i = 0; i < _batchCount; i++)
            if (_batches[i].Key == key)
                return i;
        return -1;
    }

    private static AdvancedDeformationDispatchKey GetKey(
        in AdvancedDeformationJobRecord job)
        => new(
            job.VertexLayoutId,
            job.Precision,
            EAdvancedDeformationFeatureFlags.None);
}
