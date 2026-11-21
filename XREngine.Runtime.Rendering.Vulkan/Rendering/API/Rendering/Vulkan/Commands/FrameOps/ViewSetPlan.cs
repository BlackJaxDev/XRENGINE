using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen logical view set for one frame plan. The identity uses pipeline,
/// viewport, context kind, and output target rather than acquired image slots.
/// </summary>
internal sealed class ViewSetPlan
{
    private readonly bool _fixedCapacity;
    private RenderViewKey[] _views;
    private ulong[] _historyKeys;
    private int _viewCount;
    private bool _isSealed;
    // The located view set holds eight large descriptors. Lookups run for each
    // operation, so keep only the fields they read instead of copying the set.
    private readonly ulong[] _locatedHistoryKeys = new ulong[RenderFrameViewSet.MaxViewCount];
    private readonly uint[] _locatedViewIds = new uint[RenderFrameViewSet.MaxViewCount];
    private readonly int[] _locatedOpenXrViewIndices = new int[RenderFrameViewSet.MaxViewCount];
    private readonly EVrOutputViewKind[] _locatedKinds = new EVrOutputViewKind[RenderFrameViewSet.MaxViewCount];
    private int _locatedViewCount;
    private bool _hasLocatedOpenXrViews;

    internal int Count => _viewCount;
    internal bool IsSealed => _isSealed;
    internal bool HasLocatedOpenXrViews => _hasLocatedOpenXrViews;

    internal ViewSetPlan()
        : this(4, fixedCapacity: false)
    {
    }

    internal ViewSetPlan(int capacity, bool fixedCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _fixedCapacity = fixedCapacity;
        _views = new RenderViewKey[capacity];
        _historyKeys = new ulong[capacity];
    }

    internal void Reset()
    {
        _viewCount = 0;
        _isSealed = false;
        _locatedViewCount = 0;
        _hasLocatedOpenXrViews = false;
    }

    internal void Add(in FrameOpContext context)
    {
        if (_isSealed)
            throw new InvalidOperationException("The frame-plan view set is sealed.");

        if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye)
        {
            if (!_hasLocatedOpenXrViews)
            {
                throw new InvalidOperationException(
                    "OpenXR frame planning requires the located immutable frame view set.");
            }

            AddLocatedOpenXrViews();
            return;
        }

        RenderViewKey key = new(
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.OutputTargetIdentity,
            ResolveKind(context.ContextKind),
            context.OutputFrameBufferIdentity,
            0);
        Add(key, ComputeLogicalHistoryKey(context));
    }

    /// <summary>
    /// Captures the immutable OpenXR view publication produced immediately after
    /// locate. The plan retains only its stable logical history identities; no
    /// acquired-image slot participates in view reuse or ordering.
    /// </summary>
    internal void SetLocatedOpenXrViews(in RenderFrameViewSet views)
    {
        if (_isSealed)
            throw new InvalidOperationException("The frame-plan view set is sealed.");
        if (views.ViewCount == 0)
            return;

        for (int index = 0; index < views.ViewCount; index++)
        {
            ulong historyKey = views.GetView(index).EffectiveHistoryKey;
            if (historyKey == 0UL)
                throw new InvalidOperationException("Located OpenXR views require stable history keys.");
            for (int priorIndex = 0; priorIndex < index; priorIndex++)
            {
                if (views.GetView(priorIndex).EffectiveHistoryKey == historyKey)
                {
                    throw new InvalidOperationException(
                        "Located OpenXR views must have unique stable history keys.");
                }
            }
        }

        for (int index = 0; index < views.ViewCount; index++)
        {
            RenderFrameViewDescriptor view = views.GetView(index);
            _locatedHistoryKeys[index] = view.EffectiveHistoryKey;
            _locatedViewIds[index] = view.ViewId;
            _locatedOpenXrViewIndices[index] = view.OpenXrViewIndex;
            _locatedKinds[index] = view.Kind;
        }

        _locatedViewCount = views.ViewCount;
        _hasLocatedOpenXrViews = true;
    }

    internal bool TryGetLocatedOpenXrViewKind(uint openXrViewIndex, out EVrOutputViewKind kind)
    {
        for (int index = 0; index < _locatedViewCount; index++)
        {
            if (_locatedOpenXrViewIndices[index] == openXrViewIndex)
            {
                kind = _locatedKinds[index];
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>Resolves an operation's stable logical history identity to its located eye kind.</summary>
    internal bool TryGetLocatedOpenXrViewKindByLogicalViewId(
        ulong logicalViewId,
        out EVrOutputViewKind kind)
    {
        for (int index = 0; index < _locatedViewCount; index++)
        {
            if (_locatedHistoryKeys[index] == logicalViewId)
            {
                kind = _locatedKinds[index];
                return true;
            }
        }

        kind = default;
        return false;
    }

    internal ulong GetHistoryKey(int index)
    {
        if (!_isSealed)
            throw new InvalidOperationException("The frame-plan view set must be sealed before consumption.");
        if ((uint)index >= (uint)_viewCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _historyKeys[index];
    }

    private void AddLocatedOpenXrViews()
    {
        for (int index = 0; index < _locatedViewCount; index++)
        {
            RenderViewKey key = new(
                // An OpenXR view is a logical eye, not a pipeline-specific
                // render invocation. Keeping producer identities here would
                // manufacture a pipeline x eye cross product.
                0,
                0,
                checked((int)_locatedViewIds[index]),
                RenderViewKind.VREye,
                0,
                0);
            Add(key, _locatedHistoryKeys[index]);
        }
    }

    private void Add(in RenderViewKey key, ulong historyKey)
    {
        for (int index = 0; index < _viewCount; index++)
        {
            if (_views[index].Equals(key) && _historyKeys[index] == historyKey)
                return;
        }

        EnsureCapacity(_viewCount + 1);
        _views[_viewCount] = key;
        _historyKeys[_viewCount] = historyKey;
        _viewCount++;
    }

    internal void Seal() => _isSealed = true;

    internal ref readonly RenderViewKey GetView(int index)
    {
        if (!_isSealed)
            throw new InvalidOperationException("The frame-plan view set must be sealed before consumption.");
        if ((uint)index >= (uint)_viewCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _views[index];
    }

    private void EnsureCapacity(int required)
    {
        if (_views.Length >= required)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.View,
                _views.Length,
                required);

        Array.Resize(ref _views, Math.Max(required, _views.Length * 2));
        Array.Resize(ref _historyKeys, _views.Length);
    }

    private static ulong ComputeLogicalHistoryKey(in FrameOpContext context)
    {
        ulong hash = 1469598103934665603UL;
        Add(ref hash, (ulong)(uint)context.ContextKind);
        Add(ref hash, unchecked((ulong)(uint)context.PipelineIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.ViewportIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputTargetIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputFrameBufferIdentity));
        return hash == 0UL ? 1UL : hash;
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private static RenderViewKind ResolveKind(EVulkanFrameOpContextKind contextKind)
        => contextKind switch
        {
            EVulkanFrameOpContextKind.OpenXrEye => RenderViewKind.VREye,
            EVulkanFrameOpContextKind.Shadow => RenderViewKind.Shadow,
            EVulkanFrameOpContextKind.LightProbeCapture => RenderViewKind.Probe,
            EVulkanFrameOpContextKind.OpenXrMirror or EVulkanFrameOpContextKind.UiPreview => RenderViewKind.Overlay,
            EVulkanFrameOpContextKind.SceneCapture or EVulkanFrameOpContextKind.DiagnosticCapture => RenderViewKind.Reflection,
            _ => RenderViewKind.Main,
        };
}
