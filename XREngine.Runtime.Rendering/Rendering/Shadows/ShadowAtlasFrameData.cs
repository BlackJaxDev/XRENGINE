using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasFrameData
{
    private const int MaxPooledMemberArraysPerLength = 64;
    private static readonly object MemberArrayPoolSync = new();
    private static readonly Dictionary<int, Stack<ShadowAtlasGroupedAllocationMember[]>> MemberArrayPool = new();

    private ShadowAtlasAllocation[] _allocations = [];
    private ShadowAtlasGroupedDirectionalCascadeAllocation[] _directionalCascadeGroups = [];
    private ShadowAtlasGroupedPointFaceAllocation[] _pointFaceGroups = [];
    private ShadowDirectionalAtlasLightDiagnostic[] _directionalLightDiagnostics = [];
    private ShadowAtlasPageDescriptor[] _pages = [];
    private readonly Dictionary<ShadowRequestKey, int> _allocationIndexByKey = new();
    private int _allocationCount;
    private int _directionalCascadeGroupCount;
    private int _pointFaceGroupCount;
    private int _directionalLightDiagnosticCount;
    private int _pageCount;

    public ulong FrameId { get; private set; }
    public ulong Generation { get; private set; }
    public ShadowAtlasMetrics Metrics { get; private set; }
    public ShadowAtlasSolveDiagnostics SolveDiagnostics { get; private set; }
    public int AllocationCount => _allocationCount;
    public int DirectionalCascadeGroupCount => _directionalCascadeGroupCount;
    public int PointFaceGroupCount => _pointFaceGroupCount;
    public int DirectionalLightDiagnosticCount => _directionalLightDiagnosticCount;
    public int PageCount => _pageCount;
    public ReadOnlySpan<ShadowAtlasAllocation> Allocations => _allocations.AsSpan(0, _allocationCount);
    public ReadOnlySpan<ShadowAtlasGroupedDirectionalCascadeAllocation> DirectionalCascadeGroups => _directionalCascadeGroups.AsSpan(0, _directionalCascadeGroupCount);
    public ReadOnlySpan<ShadowAtlasGroupedPointFaceAllocation> PointFaceGroups => _pointFaceGroups.AsSpan(0, _pointFaceGroupCount);
    public ReadOnlySpan<ShadowDirectionalAtlasLightDiagnostic> DirectionalLightDiagnostics => _directionalLightDiagnostics.AsSpan(0, _directionalLightDiagnosticCount);
    public ReadOnlySpan<ShadowAtlasPageDescriptor> Pages => _pages.AsSpan(0, _pageCount);

    public ShadowAtlasAllocation GetAllocation(int index)
    {
        if ((uint)index >= (uint)_allocationCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _allocations[index];
    }

    public ShadowAtlasPageDescriptor GetPage(int index)
    {
        if ((uint)index >= (uint)_pageCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _pages[index];
    }

    public ShadowAtlasGroupedDirectionalCascadeAllocation GetDirectionalCascadeGroup(int index)
    {
        if ((uint)index >= (uint)_directionalCascadeGroupCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _directionalCascadeGroups[index];
    }

    public ShadowAtlasGroupedPointFaceAllocation GetPointFaceGroup(int index)
    {
        if ((uint)index >= (uint)_pointFaceGroupCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _pointFaceGroups[index];
    }

    public ShadowDirectionalAtlasLightDiagnostic GetDirectionalLightDiagnostic(int index)
    {
        if ((uint)index >= (uint)_directionalLightDiagnosticCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _directionalLightDiagnostics[index];
    }

    public bool TryGetAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
    {
        if (_allocationIndexByKey.TryGetValue(key, out int index))
        {
            allocation = _allocations[index];
            return true;
        }

        allocation = default;
        return false;
    }

    public bool TryGetAllocationIndex(ShadowRequestKey key, out int index, out ShadowAtlasAllocation allocation)
    {
        if (_allocationIndexByKey.TryGetValue(key, out index))
        {
            allocation = _allocations[index];
            return true;
        }

        index = -1;
        allocation = default;
        return false;
    }

    public bool TryGetDirectionalCascadeGroup(Guid lightId, out ShadowAtlasGroupedDirectionalCascadeAllocation group)
    {
        for (int i = 0; i < _directionalCascadeGroupCount; i++)
        {
            ShadowAtlasGroupedDirectionalCascadeAllocation candidate = _directionalCascadeGroups[i];
            if (candidate.LightId == lightId)
            {
                group = candidate;
                return true;
            }
        }

        group = default;
        return false;
    }

    public bool TryGetDirectionalLightDiagnostic(Guid lightId, out ShadowDirectionalAtlasLightDiagnostic diagnostic)
    {
        for (int i = 0; i < _directionalLightDiagnosticCount; i++)
        {
            ShadowDirectionalAtlasLightDiagnostic candidate = _directionalLightDiagnostics[i];
            if (candidate.LightId == lightId)
            {
                diagnostic = candidate;
                return true;
            }
        }

        diagnostic = default;
        return false;
    }

    public bool TryGetPointFaceGroup(Guid lightId, out ShadowAtlasGroupedPointFaceAllocation group)
    {
        for (int i = 0; i < _pointFaceGroupCount; i++)
        {
            ShadowAtlasGroupedPointFaceAllocation candidate = _pointFaceGroups[i];
            if (candidate.LightId == lightId)
            {
                group = candidate;
                return true;
            }
        }

        group = default;
        return false;
    }

    internal static ShadowAtlasGroupedAllocationMember[] RentGroupedMemberArray(int length)
    {
        if (length <= 0)
            return [];

        lock (MemberArrayPoolSync)
        {
            if (MemberArrayPool.TryGetValue(length, out Stack<ShadowAtlasGroupedAllocationMember[]>? arrays) &&
                arrays.Count > 0)
            {
                return arrays.Pop();
            }
        }

        return new ShadowAtlasGroupedAllocationMember[length];
    }

    private static void ReturnGroupedMemberArray(ShadowAtlasGroupedAllocationMember[]? array, int usedLength)
    {
        if (array is null || array.Length == 0)
            return;

        int clearLength = Math.Clamp(usedLength, 0, array.Length);
        if (clearLength > 0)
            Array.Clear(array, 0, clearLength);

        lock (MemberArrayPoolSync)
        {
            if (!MemberArrayPool.TryGetValue(array.Length, out Stack<ShadowAtlasGroupedAllocationMember[]>? arrays))
            {
                arrays = new Stack<ShadowAtlasGroupedAllocationMember[]>(4);
                MemberArrayPool.Add(array.Length, arrays);
            }

            if (arrays.Count < MaxPooledMemberArraysPerLength)
                arrays.Push(array);
        }
    }

    internal void SetData(
        ulong frameId,
        ulong generation,
        IReadOnlyList<ShadowAtlasAllocation> allocations,
        IReadOnlyList<ShadowAtlasGroupedDirectionalCascadeAllocation> directionalCascadeGroups,
        IReadOnlyList<ShadowAtlasGroupedPointFaceAllocation> pointFaceGroups,
        IReadOnlyList<ShadowDirectionalAtlasLightDiagnostic> directionalLightDiagnostics,
        IReadOnlyList<ShadowAtlasPageDescriptor> pages,
        ShadowAtlasMetrics metrics,
        ShadowAtlasSolveDiagnostics solveDiagnostics)
    {
        EnsureAllocationCapacity(allocations.Count);
        EnsureDirectionalCascadeGroupCapacity(directionalCascadeGroups.Count);
        EnsurePointFaceGroupCapacity(pointFaceGroups.Count);
        EnsureDirectionalLightDiagnosticCapacity(directionalLightDiagnostics.Count);
        EnsurePageCapacity(pages.Count);
        _allocationIndexByKey.EnsureCapacity(allocations.Count);
        _allocationIndexByKey.Clear();

        for (int i = 0; i < allocations.Count; i++)
        {
            _allocations[i] = allocations[i];
            _allocationIndexByKey[allocations[i].Key] = i;
        }
        for (int i = allocations.Count; i < _allocationCount; i++)
            _allocations[i] = default;

        for (int i = 0; i < _directionalCascadeGroupCount; i++)
            ReturnGroupedMemberArray(_directionalCascadeGroups[i].Members, _directionalCascadeGroups[i].CascadeCount);
        for (int i = 0; i < directionalCascadeGroups.Count; i++)
            _directionalCascadeGroups[i] = directionalCascadeGroups[i];
        for (int i = directionalCascadeGroups.Count; i < _directionalCascadeGroupCount; i++)
            _directionalCascadeGroups[i] = default;

        for (int i = 0; i < _pointFaceGroupCount; i++)
            ReturnGroupedMemberArray(_pointFaceGroups[i].Members, _pointFaceGroups[i].FaceCount);
        for (int i = 0; i < pointFaceGroups.Count; i++)
            _pointFaceGroups[i] = pointFaceGroups[i];
        for (int i = pointFaceGroups.Count; i < _pointFaceGroupCount; i++)
            _pointFaceGroups[i] = default;

        for (int i = 0; i < directionalLightDiagnostics.Count; i++)
            _directionalLightDiagnostics[i] = directionalLightDiagnostics[i];
        for (int i = directionalLightDiagnostics.Count; i < _directionalLightDiagnosticCount; i++)
            _directionalLightDiagnostics[i] = default;

        for (int i = 0; i < pages.Count; i++)
            _pages[i] = pages[i];
        for (int i = pages.Count; i < _pageCount; i++)
            _pages[i] = default;

        _allocationCount = allocations.Count;
        _directionalCascadeGroupCount = directionalCascadeGroups.Count;
        _pointFaceGroupCount = pointFaceGroups.Count;
        _directionalLightDiagnosticCount = directionalLightDiagnostics.Count;
        _pageCount = pages.Count;
        FrameId = frameId;
        Generation = generation;
        Metrics = metrics;
        SolveDiagnostics = solveDiagnostics;
    }

    private void EnsureAllocationCapacity(int count)
    {
        if (_allocations.Length >= count)
            return;

        int next = Math.Max(4, _allocations.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _allocations, next);
    }

    private void EnsureDirectionalCascadeGroupCapacity(int count)
    {
        if (_directionalCascadeGroups.Length >= count)
            return;

        int next = Math.Max(4, _directionalCascadeGroups.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _directionalCascadeGroups, next);
    }

    private void EnsurePointFaceGroupCapacity(int count)
    {
        if (_pointFaceGroups.Length >= count)
            return;

        int next = Math.Max(4, _pointFaceGroups.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _pointFaceGroups, next);
    }

    private void EnsureDirectionalLightDiagnosticCapacity(int count)
    {
        if (_directionalLightDiagnostics.Length >= count)
            return;

        int next = Math.Max(4, _directionalLightDiagnostics.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _directionalLightDiagnostics, next);
    }

    private void EnsurePageCapacity(int count)
    {
        if (_pages.Length >= count)
            return;

        int next = Math.Max(4, _pages.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _pages, next);
    }
}
