using System.Numerics;
using System.Text.Json;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.WebGPU;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private DebugDrawComponent? _debugDraw;
    private int _debugCase;
    private int _debugPrimitiveCount;
    private float _previousDebugPointSize;
    private float _previousDebugLineWidth;
    private EngineMaterialSemanticIdentity _debugGlobalOverride;
    private EngineMaterialSemanticIdentity _debugPipelineOverride;
    private bool _debugGlobalOverridePresent;
    private bool _debugPipelineOverridePresent;
    private bool _debugCallbackHasRenderer;
    private bool _debugCallbackHasCamera;
    private bool _debugCallbackIsMainPass;

    private void InitializeDebugScene()
    {
        SceneNode node = new("Registered engine debug drawing");
        node.SetTransform<Transform>();
        _scene.RootNodes.Add(node);
        _debugDraw = node.AddComponent(static () => new DebugDrawComponent())
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.DebugComponentMissing.");
        _renderWorld.AddWorldObject(_debugDraw);
        _previousDebugPointSize = Engine.EditorPreferences.Debug.DebugPointSize;
        _previousDebugLineWidth = Engine.EditorPreferences.Debug.DebugLineWidth;
        Engine.EditorPreferences.Debug.DebugPointSize = 0.025f;
        Engine.EditorPreferences.Debug.DebugLineWidth = 0.01f;
        SetDebugCase(0);
    }

    private void RestoreDebugPreferences()
    {
        if (!_debug)
            return;
        Engine.EditorPreferences.Debug.DebugPointSize = _previousDebugPointSize;
        Engine.EditorPreferences.Debug.DebugLineWidth = _previousDebugLineWidth;
    }

    /// <summary>Changes the real component's CPU-authored shapes before the shared debug callback collects them.</summary>
    public void SetDebugCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_debug || _debugDraw is null || sampleCase is < 0 or > 15)
            throw new InvalidOperationException("EngineMeshDiagnostic.DebugCaseRequired: select a case from 0 through 15.");

        _debugCase = sampleCase;
        _debugPrimitiveCount = sampleCase switch
        {
            0 or 5 or 8 or 15 => 0,
            1 or 9 => 1,
            2 => 256,
            3 or 6 => 384,
            4 or 10 or 11 or 13 => 32,
            7 => 512,
            12 => 768,
            14 => 1024,
            _ => 0,
        };
        _debugDraw.ClearShapes();
        if (_debugPrimitiveCount == 0)
            return;

        bool alternate = sampleCase is 4 or 9 or 11;
        Vector3 point = alternate ? new(0.55f, 0.38f, -1) : new(-0.55f, 0.38f, -1);
        Vector3 lineStart = alternate ? new(-0.3f, -0.05f, -1) : new(-0.3f, 0.15f, -1);
        Vector3 lineEnd = alternate ? new(0.3f, -0.05f, -1) : new(0.3f, 0.15f, -1);
        Vector3 triangleA = alternate ? new(-0.65f, -0.55f, -1) : new(0.35f, -0.55f, -1);
        Vector3 triangleB = alternate ? new(-0.35f, -0.55f, -1) : new(0.65f, -0.55f, -1);
        Vector3 triangleC = alternate ? new(-0.5f, -0.25f, -1) : new(0.5f, -0.25f, -1);
        for (int i = 0; i < _debugPrimitiveCount; ++i)
        {
            bool visible = i == 0;
            float offset = visible ? 0 : 3 + i * 0.001f;
            Vector3 shift = new(offset, 0, 0);
            _debugDraw.AddPoint(point + shift, alternate ? new ColorF4(1, 1, 0, 1) : new ColorF4(1, 0, 0, 1));
            _debugDraw.AddLine(lineStart + shift, lineEnd + shift,
                alternate ? new ColorF4(1, 0, 1, 0.6f) : new ColorF4(0, 1, 0, 0.6f));
            _debugDraw.AddShape(new DiagnosticTriangle(triangleA + shift, triangleB + shift, triangleC + shift,
                alternate ? new ColorF4(0, 1, 1, 1) : new ColorF4(0, 0, 1, 1), visible ? this : null));
        }
    }

    private void CaptureDebugRenderState()
    {
        var state = RuntimeEngine.Rendering.State.RenderingPipelineState;
        _debugGlobalOverridePresent = state?.GlobalMaterialOverride is not null;
        _debugPipelineOverridePresent = state?.OverrideMaterial is not null;
        _debugGlobalOverride = state?.GlobalMaterialOverride?.EngineSemantic ?? default;
        _debugPipelineOverride = state?.OverrideMaterial?.EngineSemantic ?? default;
        _debugCallbackHasRenderer = AbstractRenderer.Current is not null;
        _debugCallbackHasCamera = RuntimeEngine.Rendering.State.RenderingCamera is not null;
        _debugCallbackIsMainPass = RuntimeEngine.Rendering.State.IsMainPass;
    }

    public string GetDebugState()
    {
        if (!_debug || _debugDraw is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.DebugCaseRequired.");
        var visualizer = RuntimeEngine.Rendering.Debug.Debug3DVisualizerState;
        WebGpuMeshResolutionTrace[] resolutionTraces = new WebGpuMeshResolutionTrace[_renderer.LastEngineMeshResolutionTraceCount];
        for (int index = 0; index < resolutionTraces.Length; index++)
            resolutionTraces[index] = _renderer.GetEngineMeshResolutionTrace(index);
        return JsonSerializer.Serialize(new
        {
            sampleCase = _debugCase,
            expectedPoints = _debugPrimitiveCount,
            expectedLines = _debugPrimitiveCount,
            expectedTriangles = _debugPrimitiveCount,
            componentShapes = _debugDraw.Shapes.Count,
            componentCallbacks = RuntimeEngine.Rendering.Debug.LastDebugDrawComponentCallbackCount,
            visualizerPoints = visualizer.Points,
            visualizerLines = visualizer.Lines,
            visualizerTriangles = visualizer.Triangles,
            visualizerDrawCalls = visualizer.DrawCalls,
            callbackGlobalOverridePresent = _debugGlobalOverridePresent,
            callbackGlobalOverride = _debugGlobalOverride.ToString(),
            callbackPipelineOverridePresent = _debugPipelineOverridePresent,
            callbackPipelineOverride = _debugPipelineOverride.ToString(),
            callbackHasRenderer = _debugCallbackHasRenderer,
            callbackHasCamera = _debugCallbackHasCamera,
            callbackIsMainPass = _debugCallbackIsMainPass,
            resolutionTraces,
            resolutionTraceTruncated = _renderer.LastEngineMeshResolutionTraceTruncated,
            pipeline = _pipeline.GetType().Name,
            semantic = _material.EngineSemantic.ToString(),
            authoredShaderCount = _material.Shaders.Count,
            debugInstanceRenderingAvailable = RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable,
            materialConstructionTarget = RuntimeEngineMaterialConstructionServices.Target.ToString(),
            browserRuntime = OperatingSystem.IsBrowser(),
            gizmosVisible = _viewport.Camera?.CullingMask.Contains(XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex),
        });
    }

    private sealed class DiagnosticTriangle(Vector3 a, Vector3 b, Vector3 c, ColorF4 color,
        EngineMeshDiagnosticFixture? fixture)
        : DebugDrawComponent.DebugShapeBase(color, true)
    {
        public override void Render(TransformBase transform)
        {
            fixture?.CaptureDebugRenderState();
            RuntimeEngine.Rendering.Debug.RenderTriangle(
                transform.TransformPoint(a, true), transform.TransformPoint(b, true),
                transform.TransformPoint(c, true), Color, true);
        }
    }
}
