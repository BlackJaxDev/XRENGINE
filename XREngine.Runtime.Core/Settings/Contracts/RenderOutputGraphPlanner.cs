namespace XREngine;

/// <summary>
/// Builds the persistent frame-output DAG from ordinary runtime output requests.
/// Execution/deadline scheduling is intentionally owned by the later output scheduler.
/// </summary>
public sealed class RenderOutputGraphPlanner
{
    private const ulong PublicationNodeKey = 0x0100_0000_0000_0001UL;
    private const ulong XrSceneNodeDomain = 0x0200_0000_0000_0000UL;
    private const ulong OutputNodeDomain = 0x0300_0000_0000_0000UL;
    private const ulong TerminalNodeDomain = 0x0400_0000_0000_0000UL;
    private const ulong ProbeNodeDomain = 0x0500_0000_0000_0000UL;

    private readonly RenderOutputDag _graph;
    private ulong _frameId = ulong.MaxValue;

    public RenderOutputGraphPlanner(int nodeCapacity = 256, int edgeCapacity = 512)
        => _graph = new RenderOutputDag(nodeCapacity, edgeCapacity);

    public RenderOutputDag Graph => _graph;

    public int Plan(
        in RenderOutputRequest request,
        bool isDue,
        bool independentDesktopScene,
        EFrameOutputKind xrSourceKind)
    {
        if (!EnsureFrame(request.FrameId))
            return -1;
        ulong terminalKey = GetTerminalNodeKey(request);
        if (_graph.TryGetNodeIndex(terminalKey, out int plannedNode))
        {
            if (!isDue && !_graph.TryReuse(plannedNode))
                _graph.SetSkipped(plannedNode);
            return plannedNode;
        }

        int publicationNode = AddPublicationNode();
        int terminalNode = request.OutputKind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit =>
                AddXrSceneNode(request.OutputKind, request.ViewFamilyId, publicationNode),
            EFrameOutputKind.DesktopMirror => AddDesktopOutput(
                request, xrSourceKind, independentDesktopScene, publicationNode),
            EFrameOutputKind.DesktopScene or EFrameOutputKind.EditorScenePanel =>
                AddSceneOutput(request, publicationNode),
            EFrameOutputKind.VrPickupMirror or EFrameOutputKind.InWorldMirror =>
                AddViewDependentOutput(request, publicationNode),
            EFrameOutputKind.LightProbeCapture or EFrameOutputKind.ReflectionProbeCapture or
                EFrameOutputKind.ImageBasedLighting => AddProbeOutput(request, publicationNode),
            EFrameOutputKind.SceneCapture or EFrameOutputKind.Thumbnail or EFrameOutputKind.Diagnostic =>
                AddCaptureOutput(request, publicationNode),
            _ => AddNonSceneOutput(request, publicationNode),
        };

        if (terminalNode >= 0 && !isDue && !_graph.TryReuse(terminalNode))
            _graph.SetSkipped(terminalNode);
        return terminalNode;
    }

    public void Complete(in RenderOutputRequest request)
    {
        if (!EnsureFrame(request.FrameId))
            return;
        if (_graph.TryGetNodeIndex(GetTerminalNodeKey(request), out int nodeIndex))
            _graph.SetProgress(nodeIndex, 1.0f);
    }

    public bool TryGetStatus(in RenderOutputRequest request, out RenderOutputDagNodeStatus status)
    {
        if (!EnsureFrame(request.FrameId))
        {
            status = default;
            return false;
        }
        if (_graph.TryGetNodeIndex(GetTerminalNodeKey(request), out int nodeIndex))
        {
            status = _graph.GetStatus(nodeIndex);
            return true;
        }

        status = default;
        return false;
    }

    private bool EnsureFrame(ulong frameId)
    {
        if (_frameId == frameId)
            return true;
        if (_frameId != ulong.MaxValue && frameId < _frameId)
            return false;
        _frameId = frameId;
        _graph.BeginFrame(unchecked((uint)frameId));
        return true;
    }

    private int AddPublicationNode()
        => _graph.AddNode(new(
            PublicationNodeKey,
            PublicationNodeKey,
            ERenderOutputDagNodeKind.Publish,
            ERenderOutputDataClass.ViewIndependent,
            0UL,
            PublicationNodeKey,
            0u,
            Cacheable: false,
            Resumable: false,
            "Shared scene/material/light publication"));

    private int AddDesktopOutput(
        in RenderOutputRequest request,
        EFrameOutputKind xrSourceKind,
        bool independentDesktopScene,
        int publicationNode)
    {
        int eyeNode = independentDesktopScene
            ? -1
            : AddXrSceneNode(xrSourceKind, GetXrFamilyKey(xrSourceKind), publicationNode);
        return AuxiliaryOutputGraphBuilder.AddDesktopMirror(
            _graph,
            GetTerminalNodeKey(request),
            request.OutputId,
            request.OutputId,
            eyeNode,
            independentDesktopScene,
            publicationNode);
    }

    private int AddSceneOutput(in RenderOutputRequest request, int publicationNode)
        => AuxiliaryOutputGraphBuilder.AddDesktopMirror(
            _graph,
            GetTerminalNodeKey(request),
            request.OutputId,
            request.OutputId,
            renderedEyeNode: -1,
            independentCamera: true,
            publicationNode);

    private int AddViewDependentOutput(in RenderOutputRequest request, int publicationNode)
    {
        AuxiliaryOutputPolicy policy = CreateAuxiliaryPolicy(request, cacheLastResult: true);
        int node = AuxiliaryOutputGraphBuilder.AddViewDependentOutput(
            _graph, policy, request.OutputId, GetOutputNodeKey(request), publicationNode);
        return AuxiliaryOutputGraphBuilder.AddPostProcess(
            _graph, GetTerminalNodeKey(request), request.OutputId, node, policy.EnablePostProcess);
    }

    private int AddCaptureOutput(in RenderOutputRequest request, int publicationNode)
    {
        bool cache = (request.FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0;
        int node = AuxiliaryOutputGraphBuilder.AddCapture(
            _graph,
            GetOutputNodeKey(request),
            request.OutputId,
            request.OutputId,
            request.Schedule.MaxContentAgeFrames,
            cache,
            publicationNode);
        bool postProcess = HasPostProcess(request.OutputKind);
        if (!postProcess)
            return node;
        return AuxiliaryOutputGraphBuilder.AddPostProcess(
            _graph, GetTerminalNodeKey(request), request.OutputId, node, enabled: true);
    }

    private int AddProbeOutput(in RenderOutputRequest request, int publicationNode)
        => AuxiliaryOutputGraphBuilder.AddProbePipeline(
            _graph,
            request.OutputId,
            GetProbeNodeBase(request),
            faceCount: 6,
            prefilterMipCount: 6,
            publicationNode);

    private int AddNonSceneOutput(in RenderOutputRequest request, int publicationNode)
    {
        int node = _graph.AddNode(new(
            GetTerminalNodeKey(request),
            request.OutputId,
            ERenderOutputDagNodeKind.Publish,
            ERenderOutputDataClass.ViewIndependent,
            0UL,
            request.OutputId,
            request.Schedule.MaxContentAgeFrames,
            Cacheable: false,
            Resumable: false,
            GetDebugName(request.OutputKind)));
        if (node >= 0)
            _graph.AddDependency(publicationNode, node);
        return node;
    }

    private int AddXrSceneNode(EFrameOutputKind kind, ulong familyKey, int publicationNode)
    {
        ulong key = GetXrSceneNodeKey(kind);
        int node = _graph.AddNode(new(
            key,
            familyKey,
            ERenderOutputDagNodeKind.SceneView,
            ERenderOutputDataClass.ViewDependent,
            familyKey,
            familyKey,
            0u,
            Cacheable: false,
            Resumable: false,
            kind == EFrameOutputKind.OpenXREyeSubmit ? "OpenXR eye family" : "OpenVR eye family"));
        if (node >= 0)
            _graph.AddDependency(publicationNode, node);
        return node;
    }

    private static AuxiliaryOutputPolicy CreateAuxiliaryPolicy(
        in RenderOutputRequest request,
        bool cacheLastResult)
        => new(
            request.OutputId,
            ScreenCoverage: 1.0f,
            request.Schedule.DesiredRateHz,
            request.Schedule.MaxContentAgeFrames,
            ResolutionScale: 1.0f,
            RecursionLimit: 1,
            RequiresIndependentCamera: true,
            EnablePostProcess: true,
            cacheLastResult);

    private static string GetDebugName(EFrameOutputKind kind)
        => kind switch
        {
            EFrameOutputKind.ImGuiOverlay => "ImGui overlay",
            EFrameOutputKind.DynamicTextOverlay => "Dynamic text overlay",
            EFrameOutputKind.Present => "Present",
            EFrameOutputKind.UiPreview => "UI preview",
            _ => "Frame output",
        };

    private static bool HasPostProcess(EFrameOutputKind kind)
        => kind is EFrameOutputKind.SceneCapture or EFrameOutputKind.Thumbnail;

    private static ulong GetTerminalNodeKey(in RenderOutputRequest request)
        => request.OutputKind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit =>
                GetXrSceneNodeKey(request.OutputKind),
            EFrameOutputKind.LightProbeCapture or EFrameOutputKind.ReflectionProbeCapture or
                EFrameOutputKind.ImageBasedLighting => GetProbeNodeBase(request) + 0x800UL,
            EFrameOutputKind.Diagnostic => GetOutputNodeKey(request),
            _ => TerminalNodeDomain ^ request.OutputId,
        };

    private static ulong GetProbeNodeBase(in RenderOutputRequest request)
        => ProbeNodeDomain ^ request.OutputId;

    private static ulong GetOutputNodeKey(in RenderOutputRequest request)
        => OutputNodeDomain ^ request.OutputId;

    private static ulong GetXrSceneNodeKey(EFrameOutputKind kind)
        => XrSceneNodeDomain | ((ulong)(uint)kind + 1UL);

    private static ulong GetXrFamilyKey(EFrameOutputKind kind)
        => XrSceneNodeDomain ^ (0x100UL + (ulong)(uint)kind);
}