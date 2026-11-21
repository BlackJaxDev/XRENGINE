using System.Threading;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The active draw readiness requests of one Vulkan renderer generation. <see cref="VkMeshRenderer.QueryDrawReadiness"/>
/// registers and renews a request; the pre-recording pipeline admission and the PresentNow readiness gate prewarm the
/// pipelines of each active request for the pass contexts of its view and publish the result with the program, the shared
/// pipeline retirement generation, and the target signature that they measured. A request ends when the open ends it
/// (<see cref="RemoveRenderer"/>), when its renderer is deleted or unlinked, or when no query renewed it for
/// <see cref="RequestLifetimeMilliseconds"/>. While a request is active, the draw enqueue stores the pass state of each
/// pass context one time (<see cref="RecordPassState"/>). A frame with no active request pays one volatile read.
/// </summary>
internal sealed class VulkanDrawReadinessRegistry
{
    /// <summary>The time after the last query when a request ends.</summary>
    internal const long RequestLifetimeMilliseconds = 2000;

    /// <summary>The largest number of pass contexts that the pass-state table keeps before it starts again.</summary>
    private const int MaxPassStates = 256;

    private const string ProgramChangedReason = "the program changed after the last pipeline check";
    private const string PipelinesChangedReason = "the pipelines or the targets of the view changed after the last pipeline check";

    private readonly Lock _sync = new();
    private readonly List<VulkanDrawReadinessRequest> _requests = [];
    private readonly Lock _passStateSync = new();
    private readonly Dictionary<(object? Pipeline, int PassIndex, object? Target), VulkanFixedFunctionStateSnapshot> _passStates = [];
    private int _count;

    /// <summary>True while at least one request is active. One volatile read; the frame loop checks it first.</summary>
    public bool HasActiveRequests => Volatile.Read(ref _count) > 0;

    /// <summary>
    /// Registers or renews a request and gives the result of its last evaluation, with the program and link generation that
    /// the evaluation measured. A ready result that another shared pipeline retirement generation measured gives
    /// <see cref="ERenderDrawReadiness.Compiling"/>. A new request allocates one object; a known request allocates nothing.
    /// </summary>
    public ERenderDrawReadiness Observe(
        VkMeshRenderer renderer,
        XRMaterial material,
        RenderingParameters? renderOptionsOverride,
        XRRenderPipelineInstance? view,
        bool stereo,
        long nowTicks,
        long currentRetirementGeneration,
        out string reason,
        out object? publishedProgram,
        out ulong publishedLinkGeneration)
    {
        lock (_sync)
        {
            VulkanDrawReadinessRequest? request = null;
            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i].Matches(renderer, material, renderOptionsOverride, view, stereo))
                {
                    request = _requests[i];
                    break;
                }
            }

            if (request is null)
            {
                request = new VulkanDrawReadinessRequest(renderer, material, renderOptionsOverride, view, stereo);
                _requests.Add(request);
                Volatile.Write(ref _count, _requests.Count);
            }

            request.LastQueryTicks = nowTicks;
            publishedProgram = request.PublishedProgram;
            publishedLinkGeneration = request.PublishedLinkGeneration;
            if (request.Evaluations == 0)
            {
                reason = request.Reason;
                return ERenderDrawReadiness.NotStarted;
            }

            return ResolvePipelineGeneration(
                request.State,
                request.Reason,
                request.PublishedRetirementGeneration,
                currentRetirementGeneration,
                out reason);
        }
    }

    /// <summary>
    /// Checks a published result against the current program of the renderer. A ready result that another program or link
    /// generation measured is stale, so it gives <see cref="ERenderDrawReadiness.Compiling"/>: the new program needs its own
    /// pipelines.
    /// </summary>
    public static ERenderDrawReadiness ResolvePublishedState(
        ERenderDrawReadiness publishedState,
        string publishedReason,
        object? publishedProgram,
        ulong publishedLinkGeneration,
        object? currentProgram,
        ulong currentLinkGeneration,
        out string reason)
    {
        if (publishedState == ERenderDrawReadiness.Ready &&
            (!ReferenceEquals(publishedProgram, currentProgram) || publishedLinkGeneration != currentLinkGeneration))
        {
            reason = ProgramChangedReason;
            return ERenderDrawReadiness.Compiling;
        }

        reason = publishedReason;
        return publishedState;
    }

    /// <summary>
    /// Checks a published result against the current shared pipeline retirement generation. A retirement wave removes the
    /// pipelines that the result measured, so a ready result of another generation gives
    /// <see cref="ERenderDrawReadiness.Compiling"/>.
    /// </summary>
    public static ERenderDrawReadiness ResolvePipelineGeneration(
        ERenderDrawReadiness publishedState,
        string publishedReason,
        long publishedRetirementGeneration,
        long currentRetirementGeneration,
        out string reason)
    {
        if (publishedState == ERenderDrawReadiness.Ready && publishedRetirementGeneration != currentRetirementGeneration)
        {
            reason = PipelinesChangedReason;
            return ERenderDrawReadiness.Compiling;
        }

        reason = publishedReason;
        return publishedState;
    }

    /// <summary>
    /// Removes the requests that no query renewed in time, or whose renderer cannot draw any more, and copies the active ones
    /// to <paramref name="destination"/>. The destination list is reused, so this allocates nothing after it has grown. When
    /// no request is left, the pass-state table is cleared.
    /// </summary>
    public void CopyActive(List<VulkanDrawReadinessRequest> destination, long nowTicks)
    {
        destination.Clear();
        bool empty;
        lock (_sync)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                VulkanDrawReadinessRequest request = _requests[i];
                if (nowTicks - request.LastQueryTicks > RequestLifetimeMilliseconds ||
                    (request.Renderer is { } renderer && !renderer.CanServeDrawReadiness))
                    _requests.RemoveAt(i);
            }

            Volatile.Write(ref _count, _requests.Count);
            for (int i = 0; i < _requests.Count; i++)
                destination.Add(_requests[i]);
            empty = _requests.Count == 0;
        }

        if (empty)
            lock (_passStateSync)
                _passStates.Clear();
    }

    /// <summary>Ends every request of a renderer. The renderer calls it when it is deleted or unlinked, and the open at its end.</summary>
    public void RemoveRenderer(VkMeshRenderer renderer)
    {
        lock (_sync)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_requests[i].Renderer, renderer))
                    _requests.RemoveAt(i);
            Volatile.Write(ref _count, _requests.Count);
        }
    }

    /// <summary>
    /// Stores the fixed-function state of one pass context (the render pipeline instance, the pass, and the target). The
    /// draw enqueue calls it only while a request is active. An unchanged state writes nothing.
    /// </summary>
    public void RecordPassState(
        XRRenderPipelineInstance? pipeline,
        int passIndex,
        XRFrameBuffer? target,
        in VulkanFixedFunctionStateSnapshot passState)
    {
        (object? Pipeline, int PassIndex, object? Target) key = (pipeline, passIndex, target);
        lock (_passStateSync)
        {
            if (_passStates.TryGetValue(key, out VulkanFixedFunctionStateSnapshot known) && known == passState)
                return;
            if (_passStates.Count >= MaxPassStates && !_passStates.ContainsKey(key))
                _passStates.Clear();
            _passStates[key] = passState;
        }
    }

    /// <summary>Gets the fixed-function state of one pass context, when a draw of that context stored it.</summary>
    public bool TryGetPassState(
        XRRenderPipelineInstance? pipeline,
        int passIndex,
        XRFrameBuffer? target,
        out VulkanFixedFunctionStateSnapshot passState)
    {
        lock (_passStateSync)
            return _passStates.TryGetValue((pipeline, passIndex, target), out passState);
    }

    /// <summary>
    /// Records that a frame plan of the view had no pass context for the request. While no frame measured the request, it
    /// only changes the reason. A kept ready result whose shared pipeline retirement generation or target signature changed
    /// becomes <see cref="ERenderDrawReadiness.Compiling"/>, because the measured pipelines may not exist any more.
    /// </summary>
    public void NoteNoContext(
        VulkanDrawReadinessRequest request,
        string reason,
        long retirementGeneration,
        ulong targetSignature)
    {
        lock (_sync)
        {
            if (request.Evaluations == 0)
            {
                request.Reason = reason;
                return;
            }

            if (request.State == ERenderDrawReadiness.Ready &&
                (request.PublishedRetirementGeneration != retirementGeneration ||
                 request.PublishedTargetSignature != targetSignature))
            {
                request.State = ERenderDrawReadiness.Compiling;
                request.Reason = PipelinesChangedReason;
            }
        }
    }

    /// <summary>
    /// Publishes the result of one evaluation of a request, with the program, the shared pipeline retirement generation, and
    /// the target signature that it measured.
    /// </summary>
    public void Publish(
        VulkanDrawReadinessRequest request,
        ERenderDrawReadiness state,
        string reason,
        object? program,
        ulong linkGeneration,
        long retirementGeneration,
        ulong targetSignature)
    {
        lock (_sync)
        {
            request.State = state;
            request.Reason = reason;
            request.PublishedProgram = program;
            request.PublishedLinkGeneration = linkGeneration;
            request.PublishedRetirementGeneration = retirementGeneration;
            request.PublishedTargetSignature = targetSignature;
            request.Evaluations++;
        }
    }
}
